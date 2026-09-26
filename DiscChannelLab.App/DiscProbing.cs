using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Disc2Flac;

public sealed partial class FfprobeService
{
    private readonly ConcurrentDictionary<string, PlaylistProbeResult> _clipProbes = new(StringComparer.OrdinalIgnoreCase);
    public void ClearCache() => _clipProbes.Clear();
    public static bool IsRecoverable(Exception ex) => ex is FfToolException or IOException or
        UnauthorizedAccessException or JsonException or TimeoutException or InvalidDataException;
    private static bool IsProtectionError(Exception ex) => ex.Message.Contains("AACS", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("BD+", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

    public static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> action, CancellationToken token, int seconds = 12)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"Analysis exceeded {seconds} seconds."); }
    }

    private static List<string> DescribeAudio(JsonElement root) => root.TryGetProperty("streams", out var streams)
        ? streams.EnumerateArray().Where(x => String(x, "codec_type") == "audio")
            .Select(x => $"index={Number(x, "index")} id={String(x, "id")} codec={String(x, "codec_name")} " +
                         $"profile={String(x, "profile")} channels={Number(x, "channels")} rate={String(x, "sample_rate")}").ToList() : [];

    private async Task<PlaylistProbeResult> ProbeClipDetailedAsync(string path, CancellationToken token, bool deep = false)
    {
        var file = new FileInfo(path);
        var key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{deep}";
        if (_clipProbes.TryGetValue(key, out var cached)) return cached;
        var result = await WithTimeout(ct => runner.RunAsync(paths.Ffprobe,
            ["-v", "error", "-probesize", deep ? "32000000" : "4000000", "-analyzeduration", deep ? "20000000" : "5000000",
             "-of", "json", "-show_streams", "-show_format", path], ct), token, deep ? 20 : 8);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var start = root.TryGetProperty("format", out var format) ? ParseTicks(String(format, "start_time")) : null;
        var audio = DescribeAudio(root);
        var parsed = new PlaylistProbeResult(ReadSupportedAudioStreams(root, formatStartTicks: start),
            new Dictionary<int, ChapterMetadata>(), new Dictionary<string, string>())
            { DetectedAudioCount = audio.Count, Diagnostics = audio };
        token.ThrowIfCancellationRequested();
        _clipProbes[key] = parsed;
        return parsed;
    }

    public async Task<PlaylistProbeResult> ProbePlaylistAsync(string root, PlaylistInfo playlist, CancellationToken token,
        bool deep = false)
    {
        var notes = new List<string>();
        if (playlist.DurationTicks == 0 && playlist.AnalysisDetails.Length > 0)
            notes.Add("structure: " + playlist.AnalysisDetails);
        PlaylistProbeResult? primary = null;
        var failed = false;
        try
        {
            primary = await WithTimeout(ct => ProbePlaylistCoreAsync(root, playlist, ct), token, deep ? 25 : 10);
            notes.AddRange(primary.Diagnostics.Select(x => "playlist: " + x));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            if (IsProtectionError(ex)) throw new ProtectedDiscException("このディスクはコピー保護されているため処理できません。");
            notes.Add("playlist: " + ex.Message);
            failed = true;
        }
        var streams = primary?.Streams.ToList() ?? [];
        var audioCount = primary?.DetectedAudioCount ?? 0;
        var clips = new Dictionary<string, PlaylistProbeResult>();
        if (playlist.Format == DiscFormat.BluRay)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(deep ? 90 : 30));
            foreach (var clip in playlist.Clips.DistinctBy(x => x.Id))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var path = Path.Combine(root, "BDMV", "STREAM", clip.Id + ".m2ts");
                    var inspected = await ProbeClipDetailedAsync(path, budget.Token, deep);
                    if (inspected.DetectedAudioCount == 0 && !deep)
                        inspected = await ProbeClipDetailedAsync(path, budget.Token, true);
                    clips.Add(clip.Id, inspected);
                    audioCount = Math.Max(audioCount, inspected.DetectedAudioCount);
                    notes.AddRange(inspected.Diagnostics.Select(x => $"clip {clip.Id}: {x}"));
                    foreach (var stream in inspected.Streams)
                        if (!streams.Any(x => AudioStreamMatcher.Identity(x) == AudioStreamMatcher.Identity(stream))) streams.Add(stream);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { failed = true; notes.Add("Clip inspection time limit reached; use Retry for an extended scan."); break; }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    if (IsProtectionError(ex)) throw new ProtectedDiscException("このディスクはコピー保護されているため処理できません。");
                    failed = true; notes.Add($"clip {clip.Id}: {ex.Message}");
                }
            }
            if (clips.Count > 0 && clips.Count == playlist.Clips.DistinctBy(x => x.Id).Count()) failed = false;
        }
        else if (primary is null)
        {
            // A bounded second attempt can recover delayed headers on DVD sources.
            try
            {
                primary = await WithTimeout(ct => ProbePlaylistCoreAsync(root, playlist, ct), token, 25);
                streams = primary.Streams.ToList(); audioCount = primary.DetectedAudioCount;
                notes.AddRange(primary.Diagnostics.Select(x => "retry: " + x));
                failed = false;
            }
            catch (Exception ex) when (IsRecoverable(ex)) { notes.Add("retry: " + ex.Message); }
        }

        int Coverage(AudioStreamInfo stream) => clips.Values.Count(clip =>
        { try { AudioStreamMatcher.Resolve(stream, clip.Streams); return true; } catch (InvalidDataException) { return false; } });
        streams = streams.OrderByDescending(Coverage).ThenByDescending(x => x.IsStereo).ToList();
        if (playlist.Format == DiscFormat.BluRay)
            streams = streams.Select(stream => stream.WithCoverage(clips.Where(pair =>
                { try { AudioStreamMatcher.Resolve(stream, pair.Value.Streams); return true; } catch (InvalidDataException) { return false; } })
                .Select(x => x.Key).ToArray(), clips.Keys.ToArray(), playlist.Clips.DistinctBy(x => x.Id).Count())).ToList();
        AudioStreamInfo? verified = null;
        foreach (var stream in streams)
        {
            for (var sample = 0; sample < (playlist.Format == DiscFormat.BluRay ? 1 : 3); sample++)
            {
            try
            {
                if (await WithTimeout(ct => VerifyAudioAsync(root, playlist, stream, clips, ct, sample), token, 8))
                { verified = stream; notes.Add($"Short decode passed: {AudioStreamMatcher.Identity(stream)}"); break; }
                notes.Add($"No PCM decoded at sample {sample + 1}: {AudioStreamMatcher.Identity(stream)}");
            }
            catch (Exception ex) when (IsRecoverable(ex)) { notes.Add("decode: " + ex.Message); }
            }
            if (verified is not null) break;
        }
        if (verified is not null) { streams.Remove(verified); streams.Insert(0, verified); }
        var state = streams.Count == 0
            ? failed ? AudioAvailability.Failed : audioCount > 0 ? AudioAvailability.Unsupported : AudioAvailability.NoAudio
            : verified is null ? AudioAvailability.Failed
            : playlist.Format == DiscFormat.BluRay && (failed || streams.Any(x => Coverage(x) < playlist.Clips.DistinctBy(c => c.Id).Count()))
                ? AudioAvailability.Partial : AudioAvailability.Ready;
        return new PlaylistProbeResult(streams,
            primary?.Chapters ?? new Dictionary<int, ChapterMetadata>(), primary?.Tags ?? new Dictionary<string, string>())
            { Availability = state, DetectedAudioCount = audioCount, Diagnostics = notes };
    }

    private async Task<bool> VerifyAudioAsync(string root, PlaylistInfo playlist, AudioStreamInfo stream,
        IReadOnlyDictionary<string, PlaylistProbeResult> clips, CancellationToken token, int sample)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1" };
        var index = stream.Index;
        if (playlist.Format == DiscFormat.BluRay)
        {
            ClipInfo? chosen = null;
            AudioStreamInfo? source = null;
            foreach (var clip in playlist.Clips)
            {
                if (!clips.TryGetValue(clip.Id, out var info)) continue;
                try { source = AudioStreamMatcher.Resolve(stream, info.Streams); chosen = clip; break; }
                catch (InvalidDataException) { }
            }
            if (chosen is null || source?.StartTimeTicks is null) return false;
            var seek = Math.Max(0, chosen.InTicks - source.StartTimeTicks.Value);
            args.AddRange(["-ss", (seek / 45000d).ToString("0.########", CultureInfo.InvariantCulture),
                "-i", Path.Combine(root, "BDMV", "STREAM", chosen.Id + ".m2ts")]);
            index = source.Index;
        }
        else if (playlist.Format == DiscFormat.DvdVideo)
        {
            var count = playlist.EffectiveChapterStarts.Count;
            var chapter = sample == 0 ? 1 : sample == 1 ? Math.Max(1, count / 2) : Math.Max(1, count);
            args.AddRange(["-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(),
                "-chapter_start", chapter.ToString(), "-chapter_end", chapter.ToString(), "-i", root]);
        }
        else args.AddRange(["-f", "mpeg", "-i", "pipe:0"]);
        args.AddRange(["-map", $"0:{index}", "-t", "0.2", "-c:a", "pcm_s16le", "-f", "null", "NUL"]);
        ProcessResult result;
        if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
        {
            var program = audio.Programs[sample == 0 ? 0 : sample == 1 ? audio.Programs.Count / 2 : audio.Programs.Count - 1];
            result = await runner.RunWithInputAsync(paths.Ffmpeg, args,
                (input, ct) => DvdAudioSectors.CopyAsync(root, audio.TitleSet, program.StartSector, program.EndSector,
                    input, ct, 8 * 1024 * 1024), token);
        }
        else result = await runner.RunAsync(paths.Ffmpeg, args, token);
        return result.Output.Split(['\r', '\n']).Any(line => line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(12), out var time) && time > 0);
    }
}
