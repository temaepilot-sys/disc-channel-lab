using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace Disc2Flac;

public interface IHeadphoneOutput : IDisposable
{
    long SubmittedFrames { get; }
    long PlayedFrames { get; }
    double BufferMilliseconds { get; }
    double StreamLatencyMilliseconds => 0;
    bool HasStarted => true;
    long EmptyBufferCount => 0;
    bool SupportsPause => false;
    void Pause() { }
    void Resume() { }
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
        Action<long, MixerMeterFrame>? mixedLevels, PreviewPcmTap? visualization, bool spatial = true,
        PreviewPlaybackControl? transport = null)
    {
        var channels = HeadphoneGeometry.Channels(stream);
        if (channels.Length != stream.Channels) throw new InvalidOperationException("Unknown headphone speaker layout.");
        var session = HeadphoneSession.Current;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = stop.Token;
        using var pcm = new HeadphonePcmBuffer(HeadphoneRenderer.BlockSize * channels.Length * 4);
        var producer = Task.Run(Produce);
        var consumer = HeadphoneAudioThread.Run(Consume);
        try { await Task.WhenAll(consumer, producer).ConfigureAwait(false); }
        finally { stop.Cancel(); }

        async Task Produce()
        {
            var bytes = pcm.Rent();
            int carried = 0;
            long decodedFrames = 0;
            try
            {
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
                    await pcm.AddAsync(bytes, token).ConfigureAwait(false);
                }
                pcm.Complete();
            }
            catch (Exception ex) { pcm.Complete(ex); }

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
                    await using var lookahead = !spatial && visualization is { Lookahead: true }
                        ? new VisualizerLookaheadStream(decoder.StandardOutput.BaseStream, channels, liveMix, volume,
                            visualization.AtOffset(startTicks / 45000d + decodedFrames / 48000d), token, sampleBytes: 4) : null;
                    Stream decoded = lookahead is null ? decoder.StandardOutput.BaseStream : lookahead;
                    long decodedBytes = 0;
                    int read;
                    while ((read = await decoded.ReadAsync(bytes.AsMemory(carried), token)) > 0)
                    {
                        decodedBytes += read;
                        carried += read;
                        if (carried == bytes.Length) { await pcm.AddAsync(bytes, token).ConfigureAwait(false); bytes = pcm.Rent(); carried = 0; }
                    }
                    decodedFrames += decodedBytes / (channels.Length * 4);
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
        }

        void Consume()
        {
            try
            {
                // Cache extraction and decoder startup finish before an audio device is opened.
                pcm.WaitUntilReady(token);
                token.ThrowIfCancellationRequested();
                using var scheduling = new HeadphoneAudioThread.Scheduling();
                using IPreviewPcmRenderer renderer = spatial ? new HeadphoneRenderer(channels, session.Options) : new StereoPcmRenderer(channels);
                using var output = new HeadphonePlaybackOutput(HeadphoneOutputFactory());
                session.ReportOutput(output.BufferMilliseconds, 0);
                log.Write($"{(spatial ? "HEADPHONES " + session.Options.Method : "STEREO PCM")}, 48000 Hz float, {channels.Length}ch, endpoint buffer {output.BufferMilliseconds:0.0} ms, " +
                    $"raw prebuffer {pcm.Count * HeadphoneRenderer.BlockSize * 1000d / 48000:0.0} ms, " +
                    $"device latency {output.StreamLatencyMilliseconds:0.0} ms, silent guard {output.GuardFrames * 1000d / 48000:0.0} ms, MMCSS={scheduling.IsRegistered}");
                if (spatial && session.Options.Method == HeadphoneMethod.SteamAudio)
                    log.Write($"HEADPHONES HRTF {session.Options.Hrtf}" + (session.Options.Hrtf == HeadphoneHrtf.Sofa ? $" {session.Options.SofaPath}" : ""));
                var input = channels.Select(_ => new float[HeadphoneRenderer.BlockSize]).ToArray();
                var stereo = new float[HeadphoneRenderer.BlockSize * 2];
                bool announced = false;
                using var observer = new PreviewAudioObserver(channels, startTicks, visualization, mixedLevels, playbackPosition, transport, output.BufferMilliseconds);
                output.Prepare(token);
                transport?.Attach(output);
                try
                {
                    foreach (var block in pcm.ReadAll(token, () => transport?.Apply(output, token)))
                    {
                        try { Render(block, HeadphoneRenderer.BlockSize); }
                        finally { pcm.Recycle(block); }
                    }
                    // Preserve the final HRIR / delay-line decay without discarding partial source blocks.
                    var silence = pcm.Rent();
                    Array.Clear(silence);
                    for (int i = 0, blocks = renderer.TailBlocks; i < blocks; i++) Render(silence, HeadphoneRenderer.BlockSize);
                    pcm.Recycle(silence);
                    transport?.Apply(output, token);
                    output.Drain(token);
                    if (!announced && output.HasStarted) started?.Invoke(startTicks);
                    playbackPosition?.Invoke(startTicks, output.PlayedFrames / 48000d);
                }
                finally
                {
                    transport?.Detach();
                    observer.Dispose();
                    session.ReportOutput(output.BufferMilliseconds, renderer.ClippedSamples, output.EmptyBufferCount);
                    log.Write($"{(spatial ? "HEADPHONES" : "STEREO PCM")} OUTPUT frames={output.SubmittedFrames} empty-buffer-events={output.EmptyBufferCount} clipped-samples={renderer.ClippedSamples} observer-drops={observer.DroppedPackets} observer-errors={observer.Errors}");
                }

                void Render(byte[] bytes, int frames)
                {
                    token.ThrowIfCancellationRequested();
                    transport?.Apply(output, token);
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
                    output.Write(stereo, frames);
                    transport?.Sample(output);
                    if (!announced && output.HasStarted) { announced = true; started?.Invoke(startTicks); }
                    observer.Capture(input, renderer, stereo, frames, before, output.PlayedFrames, output.EmptyBufferCount);
                }
            }
            catch { stop.Cancel(); throw; }
        }
    }
}
