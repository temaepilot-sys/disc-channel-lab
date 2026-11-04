using Disc2Flac;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AtmosSilentTailTests
{
    public static async Task Run(string raw, string output)
    {
        raw = Path.GetFullPath(raw); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var token = timeout.Token;
        List<string> checks = [];
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        var tools = new ToolPaths(); var log = new AppLog(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new AtmosImportService(tools, probe, log);
        var files = await new AudioFileReader(probe, log).AnalyzeAsync([raw], token);
        var fileTitle = files.Playlists.Single(); var originalStream = fileTitle.FileAnalysis!.Streams.First(AtmosImportService.IsAtmos);
        string reference = await service.PrepareAsync(files, fileTitle, originalStream, DiscService.BuildTracks(files, fileTitle), null, token);
        var original = AdmWave.Open(reference); byte[] referencePcm = ReadPcm(original, reference);
        Check(original.Scene.DurationSamples == 96000, "Reference Atmos input has an exact two-second native PCM timeline");
        string root = Path.Combine(output, "disc"), folder = Path.Combine(root, "BDMV", "STREAM"); Directory.CreateDirectory(folder);
        string Clip(string id) => Path.Combine(folder, id + ".m2ts");
        await runner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", raw,
            "-map", "0:a:0", "-c:a", "copy", "-strict", "-2", "-mpegts_m2ts_mode", "1", "-muxdelay", "0",
            "-f", "mpegts", Clip("00077")], token);
        var input = (await probe.ProbeClipAsync(Clip("00077"), token)).First(AtmosImportService.IsAtmos);
        long origin = input.StartTimeTicks!.Value;
        for (int i = 78; i <= 90; i++) File.Copy(Clip("00077"), Clip(i.ToString("00000")));
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=32x32:r=24",
            "-t", "1.5", "-c:v", "mpeg2video", "-f", "mpegts", "-mpegts_m2ts_mode", "1", Clip("00091")], token);
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "1", "-c:a", "ac3", "-f", "mpegts", "-mpegts_m2ts_mode", "1", Clip("00092")], token);
        File.WriteAllText(Clip("00093"), "damaged stream, not verified silence");
        var hashes = Directory.GetFiles(folder).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var clips = Enumerable.Range(77, 14).Select((id, i) => new ClipInfo(id.ToString("00000"), origin, origin + 90000, i * 90000L))
            .Append(new("00091", 0, 67500, 14 * 90000L)).ToArray();
        var title = new PlaylistInfo { Id = 14, DisplayOrder = 3, Format = DiscFormat.BluRay, Clips = clips,
            DurationTicks = 14 * 90000L + 67500, ChapterStarts = Enumerable.Range(0, 14).Select(i => i * 90000L).ToArray() };
        var disc = new DiscAnalysis { Root = root, DiscKey = new string('C', 64), AlbumTitle = "Verified Atmos silent-tail regression", Playlists = [title] };
        var inspected = await new DiscService(probe, log).InspectPlaylistAsync(disc, title, token);
        var selected = inspected.Streams.First(AtmosImportService.IsAtmos);
        Check(inspected.Availability == AudioAvailability.Ready && selected.SilentTailClipId == "00091" && !selected.HasPartialCoverage,
            "Full packet inspection identifies the fifteenth video-only clip as a verified short tail");
        Check(await probe.VerifySilentTailAsync(Clip("00091"), title, clips[^1], selected, token),
            "ADM preflight independently confirms the current tail file contains no audio");
        async Task Reject(PlaylistInfo bad, AudioStreamInfo badStream, string reason)
        {
            var badDisc = new DiscAnalysis { Root = root, DiscKey = Guid.NewGuid().ToString("N").PadRight(64, 'A'),
                AlbumTitle = reason, Playlists = [bad] };
            bool rejected = false, decoded = false;
            try { await service.PrepareAsync(badDisc, bad, badStream, DiscService.BuildTracks(badDisc, bad),
                new Immediate(p => decoded |= p.Message.StartsWith("Decoding clip", StringComparison.Ordinal)), token); }
            catch (Exception ex) when (FfprobeService.IsRecoverable(ex))
            { rejected = ex.Message.Contains(bad.Clips.First(c => c.Id is "00091" or "00092" or "00093" or "99999").Id); }
            Check(rejected && !decoded, reason + ": rejected with the clip ID before decoding any music");
        }
        PlaylistInfo Variant(string tail, long ticks = 67500) => new() { Id = 15, DisplayOrder = 4, Format = DiscFormat.BluRay,
            Clips = clips[..^1].Append(new ClipInfo(tail, 0, ticks, 14 * 90000L)).ToArray(),
            DurationTicks = 14 * 90000L + ticks, ChapterStarts = title.ChapterStarts };
        AudioStreamInfo Coverage(string? tail) => selected.WithCoverage(clips[..^1].Select(c => c.Id).ToArray(),
            clips.Select(c => c.Id).ToArray(), clips.Length, tail);
        await Reject(Variant("00091"), Coverage(null), "Unverified missing audio is not concealed");
        await Reject(Variant("00092"), Coverage("00092"), "A tail with a different real audio stream is not discarded");
        await Reject(Variant("00093"), Coverage("00093"), "A corrupt tail is not padded as silence");
        await Reject(Variant("99999"), Coverage("99999"), "A missing file is not padded as silence");
        await Reject(Variant("00091", 135000), Coverage("00091"), "A long video-only section is not treated as a short tail");
        var inside = new PlaylistInfo { Id = 16, Format = DiscFormat.BluRay, ChapterStarts = [0], DurationTicks = 247500,
            Clips = [clips[0], new("00091", 0, 67500, 90000), clips[1] with { PlaylistStartTicks = 157500 }] };
        await Reject(inside, Coverage("00091"), "An interior gap is not concealed");
        var tracks = DiscService.BuildTracks(disc, title);
        for (int i = 0; i < tracks.Count; i++) { tracks[i].Title = $"Song {i + 1}"; tracks[i].Artist = "Synthetic test"; }
        string saved = await service.PrepareAsync(disc, title, selected, tracks, null, token);
        var wave = AdmWave.Open(saved); byte[] pcm = ReadPcm(wave, saved);
        const long musicFrames = 14 * 96000L, tailFrames = 72000;
        Check(wave.Scene.DurationSamples == musicFrames + tailFrames && wave.Channels == original.Channels,
            "ADM saves the complete fourteen-clip timeline plus the exact 1.5-second silent tail");
        int nativeBytes = referencePcm.Length;
        Check(Enumerable.Range(0, 14).All(i => pcm.AsSpan(i * nativeBytes, nativeBytes).SequenceEqual(referencePcm)),
            "Every 24-bit PCM sample of all fourteen music clips is byte-identical to the native reference");
        Check(pcm.AsSpan(14 * nativeBytes).ToArray().All(b => b == 0), "Only the verified final video clip becomes zero PCM");
        Check(wave.Scene.Sources.All(s => s.At(musicFrames).Gain == 0), "All source metadata becomes inactive at the silent-tail boundary");
        Check(wave.Album?.Chapters.Length == 14 && wave.Album.Chapters.Select(c => c.Title).SequenceEqual(tracks.Select(t => t.Title))
            && wave.Album.Chapters.Select(c => c.StartSample).SequenceEqual(Enumerable.Range(0, 14).Select(i => i * 96000L)),
            "All edited track names and original chapter sample boundaries survive the ADM export");
        using (var json = JsonDocument.Parse(File.ReadAllText(saved + ".import.json")))
            Check(json.RootElement.GetProperty("Timeline")[13].GetProperty("TrailingSilenceFrames").GetInt64() == tailFrames,
                "The import record explicitly identifies the verified silent sample count");
        Check(hashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)), "All source files remain unchanged");
        Check(!Directory.GetDirectories(Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "atmos"), ".prepare-playlist-*").Any(),
            "Completed and rejected imports leave no private per-clip work directories");
        SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = checks.Count, checks, saved, wave.Channels,
            wave.Scene.DurationSamples, Chapters = wave.Album!.Chapters.Length, MusicSamples = musicFrames, SilentSamples = tailFrames });
    }
    static byte[] ReadPcm(AdmWave wave, string path)
    { using var input = File.OpenRead(path); input.Position = wave.DataOffset; byte[] bytes = new byte[wave.DataBytes]; input.ReadExactly(bytes); return bytes; }
    sealed class Immediate(Action<ConversionProgress> action) : IProgress<ConversionProgress>
    { public void Report(ConversionProgress value) => action(value); }
}
