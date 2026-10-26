using Disc2Flac;
using System.IO;
using System.Text.Json;

internal static class SingleClipAtmosTests
{
    public static async Task Run(string raw, string output)
    {
        raw = Path.GetFullPath(raw); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", output);
        var checks = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        var tools = new ToolPaths(); var log = new AppLog(); var probe = new FfprobeService(tools, new ProcessRunner(log));
        var service = new AtmosImportService(tools, probe, log);
        var files = await new AudioFileReader(probe, log).AnalyzeAsync([raw], CancellationToken.None);
        var fileTitle = files.Playlists.Single();
        var fileAnalysis = await new DiscService(probe, log).AnalyzePlaylistAsync(files, fileTitle, CancellationToken.None);
        string reference = await service.PrepareAsync(files, fileTitle, fileAnalysis.Streams.First(AtmosImportService.IsAtmos),
            fileAnalysis.Tracks, null, CancellationToken.None);
        var original = AdmWave.Open(reference);
        Check(original.Scene.DurationSamples == 96000, "Standalone two-second Atmos input keeps its original exact duration");
        byte[] pcm = ReadPcm(original, reference);
        string discRoot = Path.Combine(output, "test-disc"), streamPath = Path.Combine(discRoot, "BDMV", "STREAM", "00000.m2ts");
        Directory.CreateDirectory(Path.GetDirectoryName(streamPath)!);
        await SpatialRendering.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", raw,
            "-map", "0:a:0", "-c:a", "copy", "-strict", "-2", "-mpegts_m2ts_mode", "1", "-muxdelay", "0",
            "-output_ts_offset", "36597", "-f", "mpegts", streamPath], CancellationToken.None);
        var available = await probe.ProbeClipAsync(streamPath, CancellationToken.None);
        var stream = available.First(AtmosImportService.IsAtmos);
        long origin = stream.StartTimeTicks ?? throw new InvalidDataException("Fixture source time is missing.");
        Check(origin >= 36597L * 45000, "The BD fixture uses a real large nonzero source timestamp");
        var coverage = await AudioTimestampCoverage.ReadAsync(tools.Ffprobe, streamPath, stream.Index, log, CancellationToken.None);
        Check(coverage.End - coverage.Start == 96000, "BD packet timestamps independently verify the complete original audio duration");

        var tail = await Export(0, origin, origin + 91875, 98000);
        var padded = ReadPcm(tail.Wave, tail.Path);
        Check(padded.AsSpan(0, pcm.Length).SequenceEqual(pcm), "A single-clip BD preserves every native PCM byte before the verified empty tail");
        Check(padded.AsSpan(pcm.Length).ToArray().All(b => b == 0), "The 41.667-ms empty BD tail is represented by exactly 2000 silent samples");
        Check(tail.Wave.Album?.Chapters.Length == 2 && tail.Wave.Album.Chapters[1].StartSample == 48000,
            "Authored chapter sample positions are unchanged by edge padding");
        using (var json = JsonDocument.Parse(File.ReadAllText(tail.Path + ".import.json")))
            Check(json.RootElement.GetProperty("Timeline")[0].GetProperty("TrailingSilenceFrames").GetInt64() == 2000,
                "The saved import record identifies the verified tail adjustment");
        var edges = await Export(1, origin - 75, origin + 90000 + 150, 96240);
        byte[] both = ReadPcm(edges.Wave, edges.Path); int bytesPerFrame = original.Channels * 3;
        Check(both.AsSpan(0, 80 * bytesPerFrame).ToArray().All(b => b == 0) &&
            both.AsSpan(80 * bytesPerFrame, pcm.Length).SequenceEqual(pcm) &&
            both.AsSpan(80 * bytesPerFrame + pcm.Length).ToArray().All(b => b == 0),
            "A single BD PlayItem can start 80 samples before its audio and end 160 samples after it without shifting or losing PCM");
        SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = checks.Count, checks, Channels = original.Channels });

        async Task<(string Path, AdmWave Wave)> Export(int id, long start, long end, long frames)
        {
            var title = new PlaylistInfo { Id = id, Format = DiscFormat.BluRay, DisplayOrder = id + 1,
                DurationTicks = end - start, Clips = [new("00000", start, end, 0)], ChapterStarts = [0, 45000] };
            var disc = new DiscAnalysis { Root = discRoot, DiscKey = new string('A', 64), AlbumTitle = "Single BD clip regression", Playlists = [title] };
            string path = await service.PrepareAsync(disc, title, stream, DiscService.BuildTracks(disc, title), null, CancellationToken.None);
            var wave = AdmWave.Open(path);
            Check(wave.Scene.DurationSamples == frames && wave.Channels == original.Channels, "Single BD title " + id + " retains the exact authored timeline and source layout");
            return (path, wave);
        }
    }

    static byte[] ReadPcm(AdmWave wave, string path)
    { using var file = File.OpenRead(path); file.Position = wave.DataOffset; byte[] pcm = new byte[wave.DataBytes]; file.ReadExactly(pcm); return pcm; }
}
