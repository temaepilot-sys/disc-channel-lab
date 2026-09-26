using System.Text.Json;

namespace Disc2Flac;

public static class DiscDiagnostics
{
    public static async Task SaveAsync(DiscAnalysis disc, string path, CancellationToken token)
    {
        var tools = new ToolPaths();
        var runner = new ProcessRunner(new AppLog());
        async Task<string> Version(string tool)
        {
            try { return (await FfprobeService.WithTimeout(ct => runner.RunAsync(tool, ["-version"], ct), token, 3))
                    .Output.Split('\n')[0].Trim(); }
            catch (Exception ex) when (FfprobeService.IsRecoverable(ex)) { return ex.Message; }
        }
        var ffmpeg = await Version(tools.Ffmpeg);
        var ffprobe = await Version(tools.Ffprobe);
        var report = new
        {
            schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow, application = "DiscChannelLab",
            applicationVersion = typeof(DiscDiagnostics).Assembly.GetName().Version?.ToString(),
            ffmpeg, ffprobe, disc.DiscKey, disc.AlbumTitle, disc.Diagnostics, format = disc.Format.ToString(),
            titles = disc.Playlists.Select(title => new
            {
                title.Id, title.TitleName, format = title.Format.ToString(), title.DurationTicks,
                state = title.Availability.ToString(), rankingScore = PlaylistRanking.Score(title),
                repeatedShortClips = title.IsRepeatedShortClipLoop, originalChapters = title.ChapterStarts,
                effectiveChapters = title.EffectiveChapterStarts, title.MergeShortTail,
                title.Clips, details = title.AnalysisDetails
            }).ToArray()
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
