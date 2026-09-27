using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Disc2Flac;

public sealed class AudioNavigationService(ToolPaths paths, ProcessRunner runner, FfprobeService probe, AppLog log)
{
    private static readonly Regex SilenceEvent = new(@"silence_(start|end):\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);
    internal const double MeterWindowSeconds = 0.02;
    private const string MeterFilter = "asetnsamples=n=960:p=0,astats=metadata=1:reset=1:measure_perchannel=Peak_level+RMS_level:measure_overall=none,ametadata=mode=print";
    private static readonly Regex PlayerClock = new(@"^\s*(-?\d+(?:\.\d+)?)\s+M-A:", RegexOptions.Compiled);
    private readonly Dictionary<string, AudioStreamInfo> _clipCache = new(StringComparer.OrdinalIgnoreCase);
    private bool? _meterFiltersAvailable;

    public bool CanPlay => paths.Ffplay is not null;
    public void ClearCache() => _clipCache.Clear();

    public async Task PlayAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream,
        long startTicks, long endTicks, CancellationToken token, Action<long>? segmentStarted = null,
        Func<double>? volume = null, StereoMixSettings? mix = null, string? soloChannel = null,
        Action<long, double, double[], double[]>? channelLevels = null,
        Action<long, double>? playbackPosition = null,
        Func<PreviewMixState>? liveMix = null, Action<long, MixerMeterFrame>? mixedLevels = null)
    {
        var ffplay = paths.Ffplay ?? throw new FileNotFoundException("再生用の ffplay.exe が見つかりません。");
        mix ??= StereoMixSettings.Default;
        // The live PCM mixer supplies input and output levels in one timestamped frame.
        // Do not also feed input meters from the independently buffered FFmpeg log.
        if (liveMix is not null && mixedLevels is not null && StereoMixSettings.Supports(stream))
            channelLevels = null;
        if (channelLevels is not null && !await MeterFiltersAvailableAsync(token)) channelLevels = null;
        if (playlist.Format != DiscFormat.BluRay)
        {
            await PlayDvdAsync(ffplay, disc, playlist, stream, startTicks, endTicks,
                token, segmentStarted, volume ?? (() => 1), mix, soloChannel, channelLevels, playbackPosition, liveMix, mixedLevels);
            return;
        }
        foreach (var segment in PlaylistSegments.ForRange(playlist, startTicks, endTicks))
        {
            token.ThrowIfCancellationRequested();
            var silent = stream.IsSilentTail(playlist, segment.Clip);
            var (sourcePath, seekTicks, source) = silent
                ? (stream.SilenceInput, 0L, stream) : await GetSourceAsync(disc, stream, segment, token);
            await PlaySegmentAsync(ffplay, sourcePath, source, seekTicks, segment.EndTicks - segment.StartTicks,
                token, () => segmentStarted?.Invoke(segment.StartTicks), volume ?? (() => 1), mix, soloChannel,
                segment.StartTicks, channelLevels, playbackPosition, liveMix, mixedLevels, silent);
        }
    }

    private async Task<bool> MeterFiltersAvailableAsync(CancellationToken token)
    {
        if (_meterFiltersAvailable is { } cached) return cached;
        try
        {
            var filters = (await runner.RunAsync(paths.Ffmpeg, ["-hide_banner", "-filters"], token)).Output;
            _meterFiltersAvailable = Regex.IsMatch(filters, @"(?m)^\s*[TSC\.]{2,3}\s+astats\s") &&
                Regex.IsMatch(filters, @"(?m)^\s*[TSC\.]{2,3}\s+ametadata\s");
        }
        catch (Exception ex) when (ex is FfToolException or IOException)
        {
            _meterFiltersAvailable = false;
            log.Write($"Channel meter unavailable: {ex.Message}");
        }
        if (_meterFiltersAvailable == false)
            log.Write("Channel meter unavailable: FFmpeg astats/ametadata filters are missing; preview continues without meters.");
        return _meterFiltersAvailable.Value;
    }

    private async Task PlayDvdAsync(string ffplay, DiscAnalysis disc, PlaylistInfo playlist,
        AudioStreamInfo stream, long startTicks, long endTicks, CancellationToken token,
        Action<long>? segmentStarted, Func<double> volume, StereoMixSettings mix, string? soloChannel,
        Action<long, double, double[], double[]>? channelLevels, Action<long, double>? playbackPosition,
        Func<PreviewMixState>? liveMix, Action<long, MixerMeterFrame>? mixedLevels)
    {
        var inputArgs = new List<string>();
        Func<Stream, CancellationToken, Task>? writeInput = null;
        long sourceStart;
        if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
        {
            var range = DvdAudioSectors.Range(audio, startTicks, endTicks);
            sourceStart = startTicks - range.SkipTicks;
            inputArgs.AddRange(["-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0"]);
            writeInput = (input, ct) => DvdAudioSectors.CopyAsync(disc.Root, audio.TitleSet,
                range.FirstSector, range.LastSector, input, ct);
        }
        else if (playlist.Format == DiscFormat.DvdVideo)
        {
            var first = 1;
            var last = 1;
            for (var i = 0; i < playlist.ChapterStarts.Count; i++)
            {
                if (playlist.ChapterStarts[i] <= startTicks) first = i + 1;
                if (playlist.ChapterStarts[i] < endTicks) last = i + 1;
            }
            sourceStart = playlist.ChapterStarts[first - 1];
            inputArgs.AddRange(["-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(),
                "-chapter_start", first.ToString(), "-chapter_end", last.ToString(), "-i", disc.Root]);
        }
        else throw new InvalidDataException("DVD の再生元がありません。");
        var skipSamples = ConversionService.ToSample(startTicks - sourceStart, 48000);
        var sampleCount = ConversionService.ToSample(endTicks - startTicks, 48000);
        var liveChannels = liveMix is not null && StereoMixSettings.Supports(stream);
        var filter = PreviewFilter(stream, skipSamples, sampleCount, liveChannels, mix, soloChannel, channelLevels is not null);
        var outputArgs = new List<string> { "-map", $"0:{stream.Index}", "-af", filter };
        outputArgs.AddRange(["-vn", "-sn", "-dn", "-ac", liveChannels ? stream.Channels.ToString() : "2",
            "-c:a", "pcm_s16le",
            "-t", Seconds(endTicks - startTicks), "-f", "s16le", "pipe:1"]);
        if (liveChannels) outputArgs.InsertRange(outputArgs.Count - 3, ["-ch_layout", stream.ChannelLayout]);
        var decodeInfo = new ProcessStartInfo(paths.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = writeInput is not null,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-hide_banner", "-v", channelLevels is null ? "error" : "info" }
                     .Concat(inputArgs).Concat(outputArgs)) decodeInfo.ArgumentList.Add(argument);
        var playInfo = new ProcessStartInfo(ffplay)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-nodisp", "-autoexit", playbackPosition is null ? "-nostats" : "-stats",
                     "-loglevel", playbackPosition is null ? "error" : "info",
                     "-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32", "-max_delay", "0",
                     "-f", "s16le", "-sample_rate", "48000", "-ch_layout", "stereo", "-i", "pipe:0" })
            playInfo.ArgumentList.Add(argument);
        log.Write($"DVD PREVIEW {playlist.DisplayName} {startTicks / 45000d:0.###}-{endTicks / 45000d:0.###} " +
                  $"channel={soloChannel ?? "stereo"} mix={mix.Center:0.###}/{mix.Surround:0.###}/{mix.Lfe:0.###} front={mix.Front:0.###}");
        using var player = new Process { StartInfo = playInfo };
        using var decoder = new Process { StartInfo = decodeInfo };
        if (!player.Start()) throw new IOException("音声プレーヤーを起動できません。");
        var decoderStarted = false;
        try
        {
            decoderStarted = decoder.Start();
            if (!decoderStarted) throw new IOException("音声デコーダーを起動できません。");
            using var registration = token.Register(() =>
            {
                try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { if (!player.HasExited) player.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            var decodeError = channelLevels is null
                ? decoder.StandardError.ReadToEndAsync(token)
                : ReadLevelLogAsync(decoder.StandardError, stream.Channels, startTicks, channelLevels, token);
            var playError = playbackPosition is null ? player.StandardError.ReadToEndAsync(token)
                : ReadPlayerClockAsync(player.StandardError, startTicks, playbackPosition, token);
            var inputTask = writeInput is null ? Task.CompletedTask : FeedAsync();
            long submittedBytes;
            try
            {
                submittedBytes = liveChannels
                    ? await StereoPreviewMixer.CopyAsync(decoder.StandardOutput.BaseStream, player.StandardInput.BaseStream,
                        stream, liveMix!, volume, token, () => segmentStarted?.Invoke(startTicks), LogLiveMix, frame => mixedLevels?.Invoke(startTicks, frame))
                    : await CopyPcmWithVolumeAsync(decoder.StandardOutput.BaseStream, player.StandardInput.BaseStream,
                        volume, token, () => segmentStarted?.Invoke(startTicks));
            }
            finally { player.StandardInput.Close(); }
            await inputTask;
            await decoder.WaitForExitAsync(token);
            await player.WaitForExitAsync(token);
            var errors = (await decodeError) + (await playError);
            var expectedBytes = sampleCount * (liveChannels ? stream.Channels : 2) * sizeof(short);
            var halfSecondBytes = 48000 * (liveChannels ? stream.Channels : 2) * sizeof(short) / 2;
            if (decoder.ExitCode != 0 && player.ExitCode == 0 &&
                submittedBytes >= expectedBytes / 2 &&
                expectedBytes - submittedBytes <= halfSecondBytes)
            {
                // Some DVD streams end with a damaged packet just before a chapter boundary.
                // The preview has already played almost all requested audio, so allow the next track.
                log.Write($"DVD PREVIEW trailing decoder error: {submittedBytes}/{expectedBytes} PCM bytes, " +
                          $"decoder exit {decoder.ExitCode}. Playback continues.");
                return;
            }
            if (decoder.ExitCode != 0 || player.ExitCode != 0)
            {
                log.Write($"DVD PREVIEW failed: {submittedBytes}/{expectedBytes} PCM bytes, " +
                          $"decoder exit {decoder.ExitCode}, player exit {player.ExitCode}.");
                throw new FfToolException(string.IsNullOrWhiteSpace(errors) ? "DVD の試聴に失敗しました。" : errors);
            }
            async Task FeedAsync()
            {
                try { await writeInput!(decoder.StandardInput.BaseStream, token); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232)
                {
                    // FFmpeg can stop reading once the requested sample range has been decoded.
                }
                finally { decoder.StandardInput.Close(); }
            }
        }
        finally
        {
            if (decoderStarted && !decoder.HasExited) decoder.Kill(entireProcessTree: true);
            if (!player.HasExited) player.Kill(entireProcessTree: true);
        }
    }

    private async Task PlaySegmentAsync(string ffplay, string sourcePath, AudioStreamInfo stream, long seekTicks,
        long durationTicks, CancellationToken token, Action onStarted, Func<double> volume,
        StereoMixSettings mix, string? soloChannel, long segmentStartTicks,
        Action<long, double, double[], double[]>? channelLevels, Action<long, double>? playbackPosition,
        Func<PreviewMixState>? liveMix, Action<long, MixerMeterFrame>? mixedLevels, bool silent = false)
    {
        var samples = (long)Math.Round(durationTicks * 48000d / 45000d, MidpointRounding.AwayFromZero);
        var inputSeekTicks = Math.Max(0, seekTicks - 45000);
        var skipTicks = seekTicks - inputSeekTicks;
        var skipSamples = (long)Math.Round(skipTicks * 48000d / 45000d, MidpointRounding.AwayFromZero);
        var liveChannels = liveMix is not null && StereoMixSettings.Supports(stream);
        var filter = PreviewFilter(stream, skipSamples, samples, liveChannels, mix, soloChannel, channelLevels is not null);
        var outputArgs = new List<string> { "-map", $"0:{(silent ? 0 : stream.Index)}", "-af", filter };
        outputArgs.AddRange(["-vn", "-sn", "-dn", "-ac", liveChannels ? stream.Channels.ToString() : "2",
            "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
        if (liveChannels) outputArgs.InsertRange(outputArgs.Count - 3, ["-ch_layout", stream.ChannelLayout]);
        var decodeInfo = new ProcessStartInfo(paths.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var inputArgs = new List<string> { "-hide_banner", "-nostdin", "-v", channelLevels is null ? "error" : "info" };
        if (silent) inputArgs.AddRange(["-f", "lavfi"]);
        else inputArgs.AddRange(["-ss", Seconds(inputSeekTicks)]);
        inputArgs.AddRange(["-t", Seconds(durationTicks + skipTicks + 4500), "-i", sourcePath]);
        foreach (var argument in inputArgs.Concat(outputArgs)) decodeInfo.ArgumentList.Add(argument);
        if (silent) log.Write($"SILENT TAIL preview at={segmentStartTicks} duration={durationTicks}");
        var playInfo = new ProcessStartInfo(ffplay)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-nodisp", "-autoexit", playbackPosition is null ? "-nostats" : "-stats",
            "-loglevel", playbackPosition is null ? "error" : "info",
            "-fflags", "nobuffer", "-flags", "low_delay", "-probesize", "32", "-max_delay", "0", "-f", "s16le",
            "-sample_rate", "48000", "-ch_layout", "stereo", "-i", "pipe:0"
        }) playInfo.ArgumentList.Add(argument);

        log.Write($"PREVIEW {Path.GetFileName(sourcePath)} seek={Seconds(seekTicks)} duration={Seconds(durationTicks)} " +
                  $"channel={soloChannel ?? "stereo"} mix={mix.Center:0.###}/{mix.Surround:0.###}/{mix.Lfe:0.###} front={mix.Front:0.###}");
        using var player = new Process { StartInfo = playInfo };
        using var decoder = new Process { StartInfo = decodeInfo };
        if (!player.Start()) throw new IOException("音声プレーヤーを起動できません。");
        var decoderStarted = false;
        try
        {
            decoderStarted = decoder.Start();
            if (!decoderStarted) throw new IOException("音声デコーダーを起動できません。");
            using var registration = token.Register(() =>
            {
                try { if (!decoder.HasExited) decoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { if (!player.HasExited) player.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            token.ThrowIfCancellationRequested();
            var decodeError = channelLevels is null
                ? decoder.StandardError.ReadToEndAsync(token)
                : ReadLevelLogAsync(decoder.StandardError, stream.Channels, segmentStartTicks, channelLevels, token);
            var playError = playbackPosition is null ? player.StandardError.ReadToEndAsync(token)
                : ReadPlayerClockAsync(player.StandardError, segmentStartTicks, playbackPosition, token);
            try
            {
                if (liveChannels)
                    await StereoPreviewMixer.CopyAsync(decoder.StandardOutput.BaseStream, player.StandardInput.BaseStream,
                        stream, liveMix!, volume, token, onStarted, LogLiveMix, frame => mixedLevels?.Invoke(segmentStartTicks, frame));
                else
                    await CopyPcmWithVolumeAsync(decoder.StandardOutput.BaseStream, player.StandardInput.BaseStream,
                        volume, token, onStarted);
            }
            finally { player.StandardInput.Close(); }
            await decoder.WaitForExitAsync(token);
            await player.WaitForExitAsync(token);
            var errors = (await decodeError) + (await playError);
            if (decoder.ExitCode != 0 || player.ExitCode != 0)
                throw new FfToolException(string.IsNullOrWhiteSpace(errors) ? "音声プレビューに失敗しました。" : errors);
        }
        finally
        {
            if (decoderStarted && !decoder.HasExited) decoder.Kill(entireProcessTree: true);
            if (!player.HasExited) player.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> ReadLevelLogAsync(StreamReader reader, int channelCount,
        long segmentStartTicks, Action<long, double, double[], double[]> onLevels, CancellationToken token)
    {
        var parser = new ChannelLevelLogParser(channelCount, segmentStartTicks, onLevels);
        var diagnostics = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
        {
            if (parser.Consume(line)) continue;
            if (diagnostics.Length < 16000) diagnostics.AppendLine(line);
        }
        parser.Flush();
        return diagnostics.ToString();
    }

    private static async Task<string> ReadPlayerClockAsync(StreamReader reader, long segmentStartTicks,
        Action<long, double> onPosition, CancellationToken token)
    {
        var diagnostics = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
        {
            var match = PlayerClock.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
            {
                onPosition(segmentStartTicks, Math.Max(0, seconds));
                continue;
            }
            if (diagnostics.Length < 16000) diagnostics.AppendLine(line);
        }
        return diagnostics.ToString();
    }

    private void LogLiveMix(PreviewMixState state) =>
        log.Write($"LIVE PCM MIX channel={state.SoloChannel ?? "stereo"} " +
                  $"mode={(state.SoloChannel is not null ? "solo" : state.Channels is null ? "standard" : "channel-mixer")} " +
                  $"mix={state.Mix.Center:0.###}/{state.Mix.Surround:0.###}/{state.Mix.Lfe:0.###} front={state.Mix.Front:0.###} " +
                  $"channels={string.Join(';', (state.Channels ?? []).Select(x => $"{x.Code}:gain={x.Gain:0.####},pan={x.Pan:0.####},mute={x.Muted},solo={x.Solo}"))}");

    public static async Task<long> CopyPcmWithVolumeAsync(Stream source, Stream destination, Func<double> volume,
        CancellationToken token, Action? firstWrite = null)
    {
        const int bytesPerSecond = 48000 * 2 * sizeof(short);
        const double maxQueuedSeconds = 0.06;
        var clock = new Stopwatch();
        var submittedBytes = 0L;
        var buffer = new byte[4096];
        var carried = 0;
        var currentGain = SafeGain(volume());
        var targetGain = currentGain;
        var rampFrames = 0;
        var rampStep = 0d;
        var started = false;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(carried), token).ConfigureAwait(false)) > 0)
        {
            var available = count + carried;
            var complete = available & ~3;
            if (complete == 0) { carried = available; continue; }
            // Seeking may spend seconds decoding before the first PCM arrives.
            // Start pacing at that first frame, so decoder startup cannot build playback credit.
            if (!clock.IsRunning) clock.Start();
            var due = (submittedBytes + complete) / (double)bytesPerSecond - maxQueuedSeconds;
            var wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);
            var requestedGain = SafeGain(volume());
            if (requestedGain != targetGain)
            {
                targetGain = requestedGain;
                rampFrames = 480; // Smooth changes over 10 ms at 48 kHz.
                rampStep = (targetGain - currentGain) / rampFrames;
            }
            if (currentGain != 1 || rampFrames > 0)
            {
                for (var i = 0; i < complete; i += 4)
                {
                    if (rampFrames > 0)
                    {
                        currentGain += rampStep;
                        if (--rampFrames == 0) currentGain = targetGain;
                    }
                    for (var channel = 0; channel < 4; channel += 2)
                    {
                        var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i + channel, 2));
                        var scaled = (short)Math.Clamp(Math.Round(sample * currentGain), short.MinValue, short.MaxValue);
                        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i + channel, 2), scaled);
                    }
                }
            }
            await destination.WriteAsync(buffer.AsMemory(0, complete), token).ConfigureAwait(false);
            if (!started) { started = true; firstWrite?.Invoke(); }
            submittedBytes += complete;
            carried = available - complete;
            if (carried != 0) buffer.AsSpan(complete, carried).CopyTo(buffer);
        }
        if (carried != 0) throw new InvalidDataException("再生音声のサンプルが途中で切れています。");
        return submittedBytes;
    }

    private static double SafeGain(double requested) =>
        double.IsFinite(requested) ? Math.Clamp(requested, 0, 2) : 1;

    public async Task<IReadOnlyList<SplitCandidate>> FindSilenceAsync(DiscAnalysis disc, PlaylistInfo playlist,
        AudioStreamInfo stream, TrackRow track, CancellationToken token)
    {
        if (playlist.Format != DiscFormat.BluRay)
            return await FindDvdSilenceAsync(disc, playlist, stream, track, token);
        var positions = new List<long>();
        foreach (var segment in PlaylistSegments.ForRange(playlist, track.StartTicks, track.EndTicks))
        {
            token.ThrowIfCancellationRequested();
            var (sourcePath, seekTicks, source) = await GetSourceAsync(disc, stream, segment, token);
            var result = await runner.RunAsync(paths.Ffmpeg,
                ["-hide_banner", "-nostdin", "-nostats", "-v", "info",
                 "-ss", Seconds(seekTicks), "-t", Seconds(segment.EndTicks - segment.StartTicks), "-i", sourcePath,
                 "-map", $"0:{source.Index}", "-vn", "-sn", "-dn",
                 "-af", "silencedetect=noise=-45dB:duration=0.8", "-f", "null", "-"], token);
            foreach (var offset in ParseSilenceMidpoints(result.Error))
            {
                var position = segment.StartTicks + (long)Math.Round(offset * 45000, MidpointRounding.AwayFromZero);
                if (position > track.StartTicks + 45000 && position < track.EndTicks - 45000)
                    positions.Add(position);
            }
        }
        var unique = new List<long>();
        foreach (var position in positions.Order())
            if (unique.Count == 0 || position - unique[^1] >= 90000) unique.Add(position);
        return unique.Select(x => new SplitCandidate(x,
            $"無音付近 · {TimeSpan.FromSeconds(x / 45000d).ToString(@"hh\:mm\:ss\.fff")}")).ToArray();
    }

    private async Task<IReadOnlyList<SplitCandidate>> FindDvdSilenceAsync(DiscAnalysis disc,
        PlaylistInfo playlist, AudioStreamInfo stream, TrackRow track, CancellationToken token)
    {
        var arguments = new List<string> { "-hide_banner", "-nostats", "-v", "info" };
        ProcessResult result;
        if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
        {
            var range = DvdAudioSectors.Range(audio, track.StartTicks, track.EndTicks);
            arguments.AddRange(["-f", "mpeg", "-i", "pipe:0", "-ss", (range.SkipTicks / 45000m).ToString(CultureInfo.InvariantCulture),
                "-t", track.DurationSeconds.ToString(CultureInfo.InvariantCulture), "-map", $"0:{stream.Index}",
                "-vn", "-sn", "-dn", "-af", "silencedetect=noise=-45dB:duration=0.8", "-f", "null", "-"]);
            result = await runner.RunWithInputAsync(paths.Ffmpeg, arguments,
                (input, ct) => DvdAudioSectors.CopyAsync(disc.Root, audio.TitleSet,
                    range.FirstSector, range.LastSector, input, ct), token);
        }
        else
        {
            var first = 1;
            var last = 1;
            for (var i = 0; i < playlist.ChapterStarts.Count; i++)
            {
                if (playlist.ChapterStarts[i] <= track.StartTicks) first = i + 1;
                if (playlist.ChapterStarts[i] < track.EndTicks) last = i + 1;
            }
            arguments.AddRange(["-nostdin", "-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(),
                "-chapter_start", first.ToString(), "-chapter_end", last.ToString(), "-i", disc.Root,
                "-ss", ((track.StartTicks - playlist.ChapterStarts[first - 1]) / 45000m).ToString(CultureInfo.InvariantCulture),
                "-t", track.DurationSeconds.ToString(CultureInfo.InvariantCulture), "-map", $"0:{stream.Index}",
                "-vn", "-sn", "-dn", "-af", "silencedetect=noise=-45dB:duration=0.8", "-f", "null", "-"]);
            result = await runner.RunAsync(paths.Ffmpeg, arguments, token);
        }
        return ParseSilenceMidpoints(result.Error)
            .Select(seconds => track.StartTicks + (long)Math.Round(seconds * 45000, MidpointRounding.AwayFromZero))
            .Where(position => position > track.StartTicks + 45000 && position < track.EndTicks - 45000)
            .Select(position => new SplitCandidate(position,
                $"無音付近 · {TimeSpan.FromSeconds(position / 45000d).ToString(@"hh\:mm\:ss\.fff")}"))
            .ToArray();
    }

    public static IReadOnlyList<double> ParseSilenceMidpoints(string output)
    {
        var result = new List<double>();
        double? start = null;
        foreach (Match match in SilenceEvent.Matches(output))
        {
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) continue;
            if (match.Groups[1].Value == "start") start = seconds;
            else if (start is { } beginning && seconds >= beginning)
            {
                result.Add((beginning + seconds) / 2);
                start = null;
            }
        }
        return result;
    }

    private async Task<(string Path, long SeekTicks, AudioStreamInfo Source)> GetSourceAsync(DiscAnalysis disc, AudioStreamInfo stream,
        PlaylistSegment segment, CancellationToken token)
    {
        var path = Path.Combine(disc.Root, "BDMV", "STREAM", $"{segment.Clip.Id}.m2ts");
        if (!File.Exists(path)) throw new FileNotFoundException($"音声クリップが見つかりません: {path}");
        var cacheKey = $"{path}|{AudioStreamMatcher.Identity(stream)}";
        if (!_clipCache.TryGetValue(cacheKey, out var source))
        {
            source = AudioStreamMatcher.Resolve(stream, await probe.ProbeClipAsync(path, token));
            _clipCache[cacheKey] = source;
        }
        if (source.StartTimeTicks is null) throw new InvalidDataException($"クリップ {segment.Clip.Id} の音声開始時刻が不明です。");
        var seekTicks = PlaylistSegments.SourceTicks(segment) - source.StartTimeTicks.Value;
        if (seekTicks < 0) throw new InvalidDataException($"クリップ {segment.Clip.Id} の再生位置が不正です。");
        return (path, seekTicks, source);
    }

    private static string PreviewFilter(AudioStreamInfo stream, long skip, long count, bool liveChannels,
        StereoMixSettings mix, string? soloChannel, bool meters)
    {
        var filter = $"aresample=48000,atrim=start_sample={skip}:end_sample={skip + count},asetpts=PTS-STARTPTS";
        // Keep metering in the same chain: a separate sink can crash older FFmpeg builds at EOF.
        if (meters) filter += "," + MeterFilter;
        if (!liveChannels)
        {
            if (soloChannel is not null) filter += "," + StereoMixSettings.SoloFilter(stream, soloChannel, stereo: true);
            else if (StereoMixSettings.Supports(stream)) filter += "," + mix.PanFilter(stream);
        }
        return filter + ",aformat=sample_fmts=s16";
    }

    private static string Seconds(long ticks) => (ticks / 45000m).ToString("0.########", CultureInfo.InvariantCulture);
}
