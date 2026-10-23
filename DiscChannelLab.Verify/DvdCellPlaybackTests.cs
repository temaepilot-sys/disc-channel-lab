using Disc2Flac;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class DvdCellPlaybackTests
{
    public static async Task RunAsync(string source, string output)
    {
        var root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>(); var decodes = new List<object>();
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidDataException(text);
            checks.Add(text); Console.WriteLine("PASS " + text);
        }
        var fixture = Path.Combine(root, "fixture"); var video = Path.Combine(fixture, "VIDEO_TS"); Directory.CreateDirectory(video);
        var vmg = new byte[4096]; U32(vmg, 196, 1); U16(vmg, 2048, 1);
        vmg[2057] = 1; U16(vmg, 2058, 2); vmg[2062] = 1; vmg[2063] = 1;
        File.WriteAllBytes(Path.Combine(video, "VIDEO_TS.IFO"), vmg);
        var ifo = new byte[8192]; Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);
        U32(ifo, 200, 1); U32(ifo, 204, 2); U16(ifo, 2048, 1); U32(ifo, 2052, 19); U32(ifo, 2056, 12);
        U16(ifo, 2060, 1); U16(ifo, 2062, 1); U16(ifo, 2064, 1); U16(ifo, 2066, 2);
        U16(ifo, 4096, 1); U32(ifo, 4108, 16); const int pgc = 4112;
        ifo[pgc + 2] = 2; ifo[pgc + 3] = 3; U16(ifo, pgc + 230, 236); U16(ifo, pgc + 232, 240);
        ifo[pgc + 236] = 1; ifo[pgc + 237] = 3;
        for (int c = 0; c < 3; c++) { U32(ifo, pgc + 240 + c * 24 + 8, (uint)(c * 2)); U32(ifo, pgc + 240 + c * 24 + 20, (uint)(c * 2)); }
        var ifoPath = Path.Combine(video, "VTS_01_0.IFO"); File.WriteAllBytes(ifoPath, ifo);
        File.WriteAllBytes(Path.Combine(video, "VTS_01_1.VOB"), Enumerable.Range(0, 3).SelectMany(i => Enumerable.Repeat((byte)i, 2048)).ToArray());
        File.WriteAllBytes(Path.Combine(video, "VTS_01_2.VOB"), Enumerable.Range(3, 2).SelectMany(i => Enumerable.Repeat((byte)i, 2048)).ToArray());
        var plan = DvdVideoSectors.TryRead(fixture, 1, 2);
        Check(plan is { Chapters.Count: 2 } && plan.Chapters[0].Count == 2, "IFO program maps retain every cell in a chapter");
        using (var copy = new MemoryStream())
        {
            await DvdVideoSectors.CopyAsync(fixture, plan!, 1, 2, copy, CancellationToken.None);
            Check(copy.ToArray().SequenceEqual(new[] { 0, 2, 4 }.SelectMany(i => Enumerable.Repeat((byte)i, 2048))),
                "Sector copy spans VOB boundaries and excludes unreferenced cells");
        }
        using (var copy = new MemoryStream())
        {
            await DvdVideoSectors.CopyAsync(fixture, plan!, 1, 2, copy, CancellationToken.None, 2048);
            Check(copy.Length == 2048, "Probe reads obey their byte limit");
        }
        ifo[pgc + 240] = 0x10; File.WriteAllBytes(ifoPath, ifo);
        Check(DvdVideoSectors.TryRead(fixture, 1, 2) is null, "Angle/block titles retain navigation instead of concatenating alternate content");
        ifo[pgc + 240] = 0; U32(ifo, pgc + 240 + 20, 500); File.WriteAllBytes(ifoPath, ifo);
        Check(DvdVideoSectors.TryRead(fixture, 1, 2) is null, "Out-of-range IFO cells are rejected");
        ifo[pgc + 237] = 0; File.WriteAllBytes(ifoPath, ifo);
        Check(DvdVideoSectors.TryRead(fixture, 1, 2) is null, "Malformed program maps fail safely");
        var sector = new byte[2048]; sector[2] = 1; sector[3] = 0xba; sector[16] = 1; sector[17] = 0xbd;
        sector[19] = 30; sector[20] = 0x80;
        sector[30] = 0; sector[31] = 0; sector[32] = 1; sector[33] = 0xbd; sector[36] = 0xa0;
        DvdProgramStreamProtection.RequireClear(sector);
        Check(true, "PES-like bytes inside clear payloads are not mistaken for encryption flags");
        sector[20] = 0xa0;
        try { DvdProgramStreamProtection.RequireClear(sector); throw new InvalidDataException("Encrypted PES accepted"); }
        catch (ProtectedDiscException) { Check(true, "Scrambled audio packets are refused without decrypting them"); }
        sector[17] = 0xe0;
        try { DvdProgramStreamProtection.RequireClear(sector); throw new InvalidDataException("Encrypted video accepted"); }
        catch (ProtectedDiscException) { Check(true, "Scrambled video packets are refused"); }

        var log = new AppLog(Path.Combine(root, "logs")); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var disc = service.Analyze(source);
        foreach (var audio in disc.Playlists.Where(p => p.Format == DiscFormat.DvdAudio))
        {
            var inspection = await service.InspectPlaylistAsync(disc, audio, CancellationToken.None);
            Check(inspection.Availability == AudioAvailability.Protected, "Actual DVD-Audio title is shown as protected rather than playback-ready: " + audio.Id);
        }
        var title = disc.Playlists.Single(p => p.Format == DiscFormat.DvdVideo && p.DvdVideoTitle == 1);
        Check(title.DvdVideoCells is not null, "Actual DVD-Video title uses the validated cell reader");
        var originalStarts = title.ChapterStarts.ToArray(); var originalDuration = title.DurationTicks;
        var inspected = await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None);
        Check(title.Availability == AudioAvailability.Ready && inspected.Streams.Count == 2, "Both DVD-Video audio streams remain available");
        foreach (var stream in inspected.Streams)
        {
            for (int chapter = 0; chapter < title.ChapterStarts.Count - 1; chapter++)
            {
                var result = await runner.RunWithInputAsync(tools.Ffmpeg,
                    ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0",
                     "-map", DvdVideoSectors.StreamMap(stream), "-vn", "-sn", "-dn", "-af", "asetpts=N/SR/TB", "-progress", "pipe:1", "-f", "null", "NUL"],
                    (input, ct) => DvdVideoSectors.CopyAsync(source, title.DvdVideoCells!, chapter + 1, chapter + 1, input, ct), CancellationToken.None);
                var decoded = result.Output.Split('\n').Where(line => line.StartsWith("out_time_us="))
                    .Select(line => long.Parse(line.AsSpan(12), CultureInfo.InvariantCulture)).Last() / 1e6;
                var expected = (title.ChapterStarts[chapter + 1] - title.ChapterStarts[chapter]) / 45000d;
                decodes.Add(new { chapter = chapter + 1, stream.Codec, stream.Channels, expected, decoded });
                // Chapter 1 includes about three seconds of video-only lead-in. IFO estimates
                // also differ slightly from the actual NTSC audio packet clock.
                Check(decoded >= expected - (chapter == 0 ? 4 : .1) && decoded <= expected + 1.3,
                    $"Title 1 chapter {chapter + 1:00} / {stream.Codec}: complete decode {decoded:0.000}s vs IFO estimate {expected:0.000}s");
            }
        }
        var pcm = inspected.Streams.Single(s => s.Codec == "pcm_dvd");
        var navigation = new AudioNavigationService(tools, runner, probe, log); var factory = AudioNavigationService.HeadphoneOutputFactory;
        var options = HeadphoneSession.Current.Options; CountingOutput? device = null;
        try
        {
            HeadphoneSession.Current.SetOptions(options with { Enabled = false });
            AudioNavigationService.HeadphoneOutputFactory = () => device = new CountingOutput();
            foreach (var seconds in new[] { 85, 110, 250, 490 })
            {
                long start = title.ChapterStarts[1] + seconds * 45000L;
                await navigation.PlayAsync(disc, title, pcm, start, start + 45000, CancellationToken.None);
                Check(device is { Peak: > .0001, SubmittedFrames: > 48000 }, $"Actual playback after seek to chapter 2 + {seconds}s produces non-silent audio");
            }
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = factory; HeadphoneSession.Current.SetOptions(options); }
        var excerpt = new TrackRow { Number = 2, IsChapter = false, Title = "After 90 seconds", StartTicks = title.ChapterStarts[1] + 120 * 45000L,
            EndTicks = title.ChapterStarts[1] + 121 * 45000L };
        var folder = await new ConversionService(tools, runner, probe, log).ConvertAsync(disc, title, pcm, [excerpt], OutputQuality.Cd,
            Path.Combine(root, "export"), new Progress<ConversionProgress>(), CancellationToken.None);
        var exported = await probe.ProbeFileAsync(Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Single(), CancellationToken.None);
        Check(exported.SampleCount == 44100 && exported.Channels == 2, "FLAC export after the old cutoff preserves the complete requested interval");
        Check(title.DurationTicks == originalDuration && title.ChapterStarts.SequenceEqual(originalStarts), "Existing chapter timestamps and saved-edit coordinates are preserved");
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks, decodes }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void U16(byte[] data, int pos, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(pos), value);
    static void U32(byte[] data, int pos, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(pos), value);
    sealed class CountingOutput : IHeadphoneOutput
    {
        public double Peak; public long SubmittedFrames { get; private set; }
        public long PlayedFrames => SubmittedFrames; public double BufferMilliseconds => 10;
        public bool HasStarted => SubmittedFrames > 0; public long EmptyBufferCount => 0;
        public void WaitForSpace(int frames, CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Write(float[] data, int frames)
        { for (int i = 0; i < frames * 2; i++) Peak = Math.Max(Peak, Math.Abs(data[i])); SubmittedFrames += frames; }
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() { }
    }
}
