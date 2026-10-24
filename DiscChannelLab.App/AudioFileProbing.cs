using System.Text.Json;

namespace Disc2Flac;

public sealed record AudioFileProbe(long DurationTicks, PlaylistProbeResult Analysis);

public sealed partial class FfprobeService
{
    public async Task VerifyAudioFileAsync(string path, AudioStreamInfo stream, CancellationToken token)
    {
        var result = await WithTimeout(ct => runner.RunAsync(paths.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-i", path,
             "-map", $"0:{stream.Index}", "-vn", "-sn", "-dn", "-t", "0.15", "-f", "null", "NUL"], ct), token);
        if (!result.Output.Split(['\r', '\n']).Any(line => line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan("out_time_us=".Length), out var time) && time > 0))
            throw new InvalidDataException("音声データをデコードできませんでした。");
    }

    public async Task<AudioFileProbe> ProbeAudioFileAsync(string path, CancellationToken token)
    {
        var result = await WithTimeout(ct => runner.RunAsync(paths.Ffprobe,
            ["-v", "error", "-of", "json", "-show_streams", "-show_chapters", "-show_format", path], ct), token);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var streams = ReadSupportedAudioStreams(root, DiscFormat.AudioFile);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duration = 0L;
        if (root.TryGetProperty("format", out var format))
        {
            duration = ParseTicks(String(format, "duration")) ?? 0;
            if (format.TryGetProperty("tags", out var ft)) foreach (var tag in ReadTags(ft)) tags[tag.Key] = tag.Value;
        }
        if (root.TryGetProperty("streams", out var elements))
        {
            foreach (var element in elements.EnumerateArray().Where(e => String(e, "codec_type") == "audio"))
            {
                if (duration <= 0) duration = ParseTicks(String(element, "duration")) ?? 0;
                if (element.TryGetProperty("tags", out var st)) foreach (var tag in ReadTags(st)) tags.TryAdd(tag.Key, tag.Value);
            }
        }
        if (streams.FirstOrDefault() is { SampleCount: > 0 } first)
            duration = checked((long)Math.Round(first.SampleCount.Value * 45000m / first.SampleRate, MidpointRounding.AwayFromZero));
        if (duration <= 0 && streams.FirstOrDefault() is { Codec: "truehd" or "mlp" } rawStream)
        {
            // Elementary TrueHD/MLP has no container duration. Count its decoded timeline
            // without retaining PCM; do not invent a duration from compressed file size.
            var scan = await runner.RunAsync(paths.Ffmpeg, ["-nostdin", "-v", "error", "-i", path, "-map", $"0:{rawStream.Index}", "-vn", "-progress", "pipe:1", "-f", "null", "NUL"], token);
            var times = scan.Output.Split(['\r', '\n']).Where(l => l.StartsWith("out_time_us=", StringComparison.Ordinal)).Select(l => long.TryParse(l.AsSpan(12), out var n) ? n : 0);
            duration = (long)Math.Round(times.DefaultIfEmpty().Max() * 45000m / 1000000m);
        }
        if (duration <= 0) throw new InvalidDataException("音声ファイルの再生時間を確認できません。");
        var chapters = new Dictionary<int, ChapterMetadata>();
        if (root.TryGetProperty("chapters", out var chapterElements))
            foreach (var chapter in chapterElements.EnumerateArray())
                if (ParseTicks(String(chapter, "start_time")) is { } start && start >= 0 && start < duration)
                {
                    var ct = chapter.TryGetProperty("tags", out var chapterTags) ? ReadTags(chapterTags) : new();
                    chapters[chapters.Count + 1] = new(Tag(ct, "title"), Tag(ct, "artist")) { StartTicks = start };
                }
        var descriptions = DescribeAudio(root);
        return new(duration, new(streams, chapters, tags) { DetectedAudioCount = descriptions.Count, Diagnostics = descriptions });
    }

    // duration_ts is a sample count only when the stream time base is 1/sample_rate.
    private static long? AudioFileSamples(JsonElement root, JsonElement stream, int rate)
    {
        if (rate <= 0 || String(stream, "time_base") != $"1/{rate}" ||
            !long.TryParse(String(stream, "duration_ts"), out var samples) || samples <= 0) return null;
        // Ogg Opus granule positions include pre-skip, unlike MP4's edited presentation duration.
        if (String(stream, "codec_name") == "opus" && root.TryGetProperty("format", out var format) && String(format, "format_name") == "ogg")
            samples -= Number(stream, "initial_padding") ?? 0;
        return samples > 0 ? samples : null;
    }
}
