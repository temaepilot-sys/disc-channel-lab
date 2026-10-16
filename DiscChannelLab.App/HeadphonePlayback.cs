using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace Disc2Flac;

public interface IHeadphoneOutput : IDisposable
{
    long SubmittedFrames { get; }
    long PlayedFrames { get; }
    double BufferMilliseconds { get; }
    void WaitForSpace(int frames, CancellationToken token);
    void Write(float[] interleaved, int frames);
    void Drain(CancellationToken token);
}

public sealed partial class AudioNavigationService
{
    public static Func<IHeadphoneOutput> HeadphoneOutputFactory { get; set; } = () => new WasapiHeadphoneOutput();
    private async Task PlayHeadphonesAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream,
        long startTicks, long endTicks, CancellationToken token, Action<long>? started, Func<double> volume,
        Func<PreviewMixState> liveMix, Action<long, double>? playbackPosition,
        Action<long, MixerMeterFrame>? mixedLevels, PreviewPcmTap? visualization)
    {
        var channels = HeadphoneGeometry.Channels(stream);
        if (channels.Length != stream.Channels) throw new InvalidOperationException("Unknown headphone speaker layout.");
        var session = HeadphoneSession.Current;
        using var renderer = new HeadphoneRenderer(channels, session.Options);
        using var output = HeadphoneOutputFactory();
        session.ReportOutput(output.BufferMilliseconds, 0);
        log.Write($"HEADPHONES {session.Options.Method}, 48000 Hz float, {channels.Length}ch, endpoint buffer {output.BufferMilliseconds:0.0} ms");
        var input = channels.Select(_ => new float[HeadphoneRenderer.BlockSize]).ToArray();
        var stereo = new float[HeadphoneRenderer.BlockSize * 2];
        var bytes = new byte[HeadphoneRenderer.BlockSize * channels.Length * 4];
        int carried = 0; bool announced = false;

        if (playlist.Format is DiscFormat.DvdAudio or DiscFormat.DvdVideo)
        {
            var arguments = new List<string>(); Func<Stream, CancellationToken, Task>? feed = null; long skip;
            if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
            {
                var range = DvdAudioSectors.Range(audio, startTicks, endTicks); skip = range.SkipTicks;
                arguments.AddRange(["-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0"]);
                feed = (destination, ct) => DvdAudioSectors.CopyAsync(disc.Root, audio.TitleSet, range.FirstSector, range.LastSector, destination, ct);
            }
            else
            {
                var first = 1; var last = 1;
                for (var i = 0; i < playlist.ChapterStarts.Count; i++)
                {
                    if (playlist.ChapterStarts[i] <= startTicks) first = i + 1;
                    if (playlist.ChapterStarts[i] < endTicks) last = i + 1;
                }
                skip = startTicks - playlist.ChapterStarts[first - 1];
                arguments.AddRange(["-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(), "-chapter_start", first.ToString(), "-chapter_end", last.ToString(), "-i", disc.Root]);
            }
            await Decode(arguments, stream, skip, endTicks - startTicks, feed);
        }
        else foreach (var segment in PlaylistSegments.ForRange(playlist, startTicks, endTicks))
        {
            token.ThrowIfCancellationRequested();
            var silent = stream.IsSilentTail(playlist, segment.Clip);
            var (path, seek, source) = silent ? (stream.SilenceInput, 0L, stream) : await GetSourceAsync(disc, stream, segment, token);
            var seekBefore = Math.Max(0, seek - 45000);
            var arguments = new List<string>();
            if (silent) arguments.AddRange(["-f", "lavfi"]);
            else arguments.AddRange(["-ss", (seekBefore / 45000m).ToString(CultureInfo.InvariantCulture)]);
            arguments.AddRange(["-i", path]);
            await Decode(arguments, source, seek - seekBefore, segment.EndTicks - segment.StartTicks, null, silent);
        }
        if (carried % (channels.Length * 4) != 0) throw new InvalidDataException("Incomplete floating-point audio frame.");
        if (carried > 0)
        {
            // Preserve the HRIR samples after a partial final source block. Do not discard
            // the remainder of a processed block and jump to the next convolution block.
            bytes.AsSpan(carried).Clear();
            Render(HeadphoneRenderer.BlockSize);
        }
        // Let the final HRIR / delay-line decay rather than cutting it at the last source sample.
        Array.Clear(bytes);
        for (int i = 0; i < 4; i++) Render(HeadphoneRenderer.BlockSize);
        output.Drain(token);
        playbackPosition?.Invoke(startTicks, output.PlayedFrames / 48000d);

        async Task Decode(List<string> arguments, AudioStreamInfo source, long skipTicks, long durationTicks,
            Func<Stream, CancellationToken, Task>? feed, bool silent = false)
        {
            var skip = ConversionService.ToSample(skipTicks, 48000); var count = ConversionService.ToSample(durationTicks, 48000);
            arguments.InsertRange(0, ["-hide_banner", "-nostdin", "-v", "error"]);
            arguments.AddRange(["-map", $"0:{(silent ? 0 : source.Index)}", "-vn", "-sn", "-dn", "-af", $"aresample=48000,atrim=start_sample={skip}:end_sample={skip + count},asetpts=PTS-STARTPTS",
                "-ac", channels.Length.ToString(), "-ar", "48000", "-c:a", "pcm_f32le"]);
            if (channels.Length > 2) arguments.AddRange(["-ch_layout", source.ChannelLayout]);
            arguments.AddRange(["-f", "f32le", "pipe:1"]);
            var info = new ProcessStartInfo(paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = feed is not null, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var decoder = new Process { StartInfo = info };
            if (!decoder.Start()) throw new IOException("Cannot start audio decoder.");
            using var cancellation = token.Register(() => { try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var errors = decoder.StandardError.ReadToEndAsync(token);
            var feedTask = feed is null ? Task.CompletedTask : Feed();
            try
            {
                int read;
                while ((read = await decoder.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(carried), token)) > 0)
                {
                    carried += read;
                    if (carried == bytes.Length) { Render(HeadphoneRenderer.BlockSize); carried = 0; }
                }
                await feedTask; await decoder.WaitForExitAsync(token);
                if (decoder.ExitCode != 0) throw new FfToolException(await errors);
            }
            finally { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); }
            async Task Feed()
            {
                try { await feed!(decoder.StandardInput.BaseStream, token); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232) { }
                finally { decoder.StandardInput.Close(); }
            }
        }

        void Render(int frames)
        {
            token.ThrowIfCancellationRequested();
            for (int channel = 0; channel < channels.Length; channel++)
                for (int frame = 0; frame < HeadphoneRenderer.BlockSize; frame++)
                {
                    var value = frame < frames ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((frame * channels.Length + channel) * 4, 4))) : 0;
                    input[channel][frame] = float.IsFinite(value) ? value : 0;
                }
            output.WaitForSpace(frames, token);
            var before = output.SubmittedFrames;
            // The latest pose is sampled AFTER waiting for the output device, never in the decoder/lookahead.
            renderer.Render(input, stereo, session.Pose, session.Options, liveMix(), volume());
            session.ReportOutput(output.BufferMilliseconds, renderer.ClippedSamples);
            output.Write(stereo, frames);
            if (!announced) { announced = true; started?.Invoke(startTicks); }
            playbackPosition?.Invoke(startTicks, output.PlayedFrames / 48000d);
            if (mixedLevels is not null)
            {
                double Db(double value) => value > 0 ? 20 * Math.Log10(value) : -120;
                var peaks = input.Select(c => Db(c.Take(frames).Max(v => Math.Abs(v)))).ToArray();
                var rms = input.Select(c => Db(Math.Sqrt(c.Take(frames).Average(v => (double)v * v)))).ToArray();
                var left = renderer.ChannelLeft.Select(c => Db(c.Take(frames).Max(v => Math.Abs(v)))).ToArray();
                var right = renderer.ChannelRight.Select(c => Db(c.Take(frames).Max(v => Math.Abs(v)))).ToArray();
                var final = new[] { Db(Enumerable.Range(0, frames).Max(i => Math.Abs(stereo[2 * i]))), Db(Enumerable.Range(0, frames).Max(i => Math.Abs(stereo[2 * i + 1]))) };
                mixedLevels(startTicks, new((before + frames / 2d) / 48000, peaks, rms, left, right, final));
            }
            if (visualization?.Enabled() == true)
            {
                var source = new byte[frames * channels.Length * 2]; var post = new float[frames * channels.Length]; var final = new byte[frames * 4];
                short Pcm(float sample) => (short)Math.Clamp((int)Math.Round(sample * 32768), short.MinValue, short.MaxValue);
                for (int frame = 0; frame < frames; frame++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(final.AsSpan(frame * 4, 2), Pcm(stereo[frame * 2]));
                    BinaryPrimitives.WriteInt16LittleEndian(final.AsSpan(frame * 4 + 2, 2), Pcm(stereo[frame * 2 + 1]));
                    for (int channel = 0; channel < channels.Length; channel++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * channels.Length + channel) * 2, 2), Pcm(input[channel][frame]));
                        post[frame * channels.Length + channel] = (renderer.ChannelLeft[channel][frame] + renderer.ChannelRight[channel][frame]) * .70710678f;
                    }
                }
                visualization.TryPublish(new(startTicks / 45000d + before / 48000d, channels, source, post, final));
            }
        }
    }
}
