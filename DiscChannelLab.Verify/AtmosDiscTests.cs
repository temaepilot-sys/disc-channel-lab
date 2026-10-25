using Disc2Flac;
using System.IO;

internal static class AtmosDiscTests
{
    public static async Task Inspect(string drive, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", output);
        var log = new AppLog(); var tools = new ToolPaths();
        var reader = new DiscService(new FfprobeService(tools, new ProcessRunner(log)), log);
        var disc = reader.Analyze(drive);
        var titles = disc.Playlists.Select(p => new { p.DisplayOrder, p.Id, p.DurationTicks, p.TitleName,
            Chapters = p.ChapterStarts, Clips = p.Clips.Select(c => new { c.Id, c.InTicks, c.OutTicks, c.PlaylistStartTicks }) }).ToArray();
        SpatialJson.Save(Path.Combine(output, "titles.json"), titles);
        foreach (var p in disc.Playlists)
            Console.WriteLine($"{p.TitleLabel}: MPLS {p.Id:00000}, {p.DurationLabel}, {p.Clips.Count} clips, {p.ChapterStarts.Count} chapters; {string.Join(',', p.Clips.Select(c => c.Id))}");
        foreach (var p in disc.Playlists.Where(p => p.DisplayOrder is 7 or 8 or 9))
        {
            var analysis = await reader.AnalyzePlaylistAsync(disc, p, CancellationToken.None);
            SpatialJson.Save(Path.Combine(output, $"title-{p.DisplayOrder:00}-streams.json"), analysis.Streams);
            Console.WriteLine($"{p.TitleLabel} audio: {string.Join("; ", analysis.Streams.Select(s => $"{s.Index} {s.Codec} {s.Profile} {s.Channels}ch"))}");
        }
    }

    public static async Task Import(string drive, int number, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", output);
        var log = new AppLog(); var tools = new ToolPaths(); var probe = new FfprobeService(tools, new ProcessRunner(log));
        var reader = new DiscService(probe, log);
        var disc = File.Exists(drive) ? await new AudioFileReader(probe, log).AnalyzeAsync([Path.GetFullPath(drive)], CancellationToken.None) : reader.Analyze(drive);
        var title = disc.Playlists.Single(p => p.DisplayOrder == number);
        var analysis = await reader.AnalyzePlaylistAsync(disc, title, CancellationToken.None);
        var stream = analysis.Streams.First(AtmosImportService.IsAtmos);
        var watch = System.Diagnostics.Stopwatch.StartNew(); long last = -1;
        var progress = new Progress<ConversionProgress>(p =>
        {
            long seconds = watch.ElapsedMilliseconds / 5000;
            if (Interlocked.Exchange(ref last, seconds) != seconds) Console.WriteLine($"{watch.Elapsed:hh\\:mm\\:ss} {p.Fraction:P1} {p.Message}");
        });
        string path = await new AtmosImportService(tools, probe, log).PrepareAsync(disc, title, stream, analysis.Tracks, progress, CancellationToken.None);
        var adm = AdmWave.Open(path);
        if (Math.Abs(adm.Scene.DurationSamples - ConversionService.ToSample(title.DurationTicks, 48000)) > 480)
            throw new InvalidDataException("Exported ADM does not preserve the complete title timeline.");
        if (adm.Album?.Chapters.Length != analysis.Tracks.Count) throw new InvalidDataException("ADM chapter count differs from the source.");
        SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = 2, path, adm.Channels,
            adm.Scene.DurationSamples, Chapters = adm.Album.Chapters.Length, ElapsedSeconds = watch.Elapsed.TotalSeconds });
        Console.WriteLine($"PASS {title.TitleLabel}: complete ADM WAV, {adm.Channels} tracks, {adm.Scene.DurationSamples} samples, {adm.Album.Chapters.Length} chapters");
    }
}
