using Disc2Flac;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal static class SacdTests
{
    private const string Toc = """
        Album Information:
            Title: SACD テスト Album
            Artist: Test artist
            Publisher: Test publisher
        Disc Information:
            Title: Disc title
            Creation date: 2026-10-13
        Area count: 2
            Area Information [0]:
            Track Count: 2
            Frame format encoding: [00] [DSD]
            Speaker config: 2 Channel
            Track list of area[0]:
                Title[1]: 曲名 One
                Performer[1]: First artist
                Duration: 00:00:45 [mins:secs:frames]
                Title[2]: Track Two
                Duration: 00:00:60 [mins:secs:frames]
            Area Information [1]:
            Track Count: 2
            Frame format encoding: [02] [DST]
            Speaker config: 6 Channel
            Track list of area[1]:
                Title[1]: 曲名 One
                Performer[1]: First artist
                Duration: 00:00:45 [mins:secs:frames]
                Duration: 00:00:60 [mins:secs:frames]
        """;

    public static async Task FixtureAsync(string root, string[] args)
    {
        Directory.CreateDirectory(root);
        File.AppendAllText(Path.Combine(root, "calls.txt"), JsonSerializer.Serialize(args) + "\n");
        if (args.Contains("-P")) { Console.WriteLine(File.Exists(Path.Combine(root, "five.flag")) ? Toc.Replace("6 Channel", "5 Channel") : Toc); return; }
        if (File.Exists(Path.Combine(root, "slow.flag"))) await Task.Delay(15000);
        var number = Array.IndexOf(args, "-t");
        Check(number >= 0 && args[number + 1] is "1" or "2", "Fixture did not receive a selected track");
        Check(args.Contains("-c") && args.Contains("-s") && !args.Contains("-b"), "Unexpected extraction mode");
        var output = args[Array.IndexOf(args, "-y") + 1];
        var channels = args.Contains("-m") ? 6 : 2;
        if (File.Exists(Path.Combine(root, "five.flag"))) channels = 5;
        if (File.Exists(Path.Combine(root, "unknown.flag"))) channels = 4;
        WriteDsf(Path.Combine(output, "fixture.dsf"), channels, unknownLayout: channels == 4);
    }

    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixture");
        Directory.CreateDirectory(fixture);
        var log = new AppLog(Path.Combine(output, "logs"));
        var paths = new ToolPaths(); var runner = new ProcessRunner(log); var probe = new FfprobeService(paths, runner);
        var host = Environment.ProcessPath!;
        var prefix = new List<string>();
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) prefix.Add(Assembly.GetExecutingAssembly().Location);
        prefix.AddRange(["sacd-tool-fixture", fixture]);
        var cachePaths = new AppDataPaths(Path.Combine(output, "cache-appdata"));
        var cache = Path.Combine(cachePaths.Root, "sacd-cache");
        var reader = new SacdService(paths, runner, log, cache, host, prefix);
        var service = new DiscService(probe, log, reader);
        var checks = new List<string>();
        void Passed(string name) { checks.Add(name); Console.WriteLine("PASS " + name); }
        var parsed = SacdService.ParseToc(Toc);
        Check(parsed.Areas.Count == 2 && parsed.Album == "SACD テスト Album" && parsed.Areas[0].Tracks[0].Artist == "First artist", "Metadata parse mismatch");
        Check(parsed.Areas[1].Tracks[1].Title is null && parsed.Areas[1].Tracks[1].DurationTicks == 36000, "Missing title or 75fps duration mismatch");
        Passed("TOC Unicode metadata, missing titles, stereo/multichannel areas and 75fps durations");
        foreach (var invalid in new[] { Toc.Replace("6 Channel", "Unknown"), Toc.Replace("00:00:45", "00:00:75"), "nothing here" })
        {
            try { SacdService.ParseToc(invalid); throw new Exception("Invalid TOC accepted"); } catch (InvalidDataException) { }
        }
        Passed("Unknown layout, invalid duration and unsupported metadata rejected");
        Check(SacdService.DecodeToolText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Toc)).ToArray()) == Toc, "UTF16 BOM not decoded");
        Check(SacdService.DecodeToolText(Encoding.UTF8.GetBytes(Toc)) == Toc, "UTF8 not decoded");
        Passed("Native tool UTF8 and UTF16 output decoding");
        var iso = Path.Combine(output, "SACD test 音源.iso");
        using (var image = File.Create(iso)) { image.SetLength(1_200_000); image.Position = 510L * 2048; image.Write("SACDMTOC"u8); }
        var before = SHA(iso);
        Check(SacdService.IsSacdIso(iso), "SACD signature not recognized");
        var copied = Path.Combine(output, "renamed.iso"); File.Copy(iso, copied, true);
        Check(SacdService.Fingerprint(iso) == SacdService.Fingerprint(copied), "Disc identity depends on filename");
        Passed("SACD ISO detection and location-independent disc identity");
        var disc = await service.AnalyzeAsync(iso, CancellationToken.None);
        Check(disc.Format == DiscFormat.Sacd && disc.Playlists.Count == 2 && !Directory.GetFiles(cache, "*.flac", SearchOption.AllDirectories).Any(), "Opening should only list tracks");
        Check(disc.Playlists.All(p => !p.HasShortTail), "Real SACD tracks were merged as short tails");
        Passed("Track list without full disc extraction; short tracks preserved");
        var stereo = disc.Playlists[0]; var multi = disc.Playlists[1];
        Check(DiscService.BuildTracks(disc, multi)[0].Title == "曲名 One" &&
            DiscService.BuildTracks(disc, multi)[0].Artist == "First artist", "TOC metadata should display before decoding");
        var result = await service.AnalyzePlaylistAsync(disc, multi, CancellationToken.None);
        var audio = result.Streams.Single();
        Check(audio.Channels == 6 && audio.ChannelLayout == "5.1" && audio.BitDepth == 24 && audio.SampleRate == 88200 && audio.StartTimeTicks == 0, "DSD to PCM format mismatch");
        Check(result.Tracks[0].Title == "曲名 One" && result.Tracks[0].Artist == "First artist" && result.Tracks[1].Title == "Track 02", "Track rows lost source metadata");
        Check(!File.Exists(multi.Clips[1].Sacd!.PcmPath), "Unneeded track was extracted");
        Passed("Real multichannel DSF decoded by FFmpeg to explicit 5.1 PCM 24bit/88.2kHz");
        var calls = File.ReadAllLines(Path.Combine(fixture, "calls.txt")).Length;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => reader.PrepareAsync(multi.Clips[0].Sacd!, CancellationToken.None)));
        Check(File.ReadAllLines(Path.Combine(fixture, "calls.txt")).Length == calls, "Cached track extracted again");
        Passed("Concurrent cache reuse without repeated extraction");
        var second = multi.Clips[1].Sacd!;
        await Task.WhenAll(reader.PrepareAsync(second, CancellationToken.None), reader.PrepareAsync(second, CancellationToken.None));
        Check(File.ReadAllLines(Path.Combine(fixture, "calls.txt")).Length == calls + 1, "Duplicate concurrent extraction");
        Passed("Only the newly selected track is extracted, once");
        var stereoResult = await service.AnalyzePlaylistAsync(disc, stereo, CancellationToken.None);
        Check(stereoResult.Streams.Single().Channels == 2, "Stereo area not decoded independently");
        Passed("Separate stereo area selection");
        var editStore = new TrackEditsStore(log, Path.Combine(output, "edits"));
        editStore.Save(disc, multi, result.Tracks);
        Check(editStore.Load(disc, multi)![0].Title == "曲名 One" && editStore.Load(disc, stereo) is null, "Area-specific edits not isolated");
        Passed("Disc and area-specific metadata edits preserved");
        var converter = new ConversionService(paths, runner, probe, log, reader);
        var multiOutput = Path.Combine(output, "multichannel-exports");
        var originalFolder = await converter.ConvertAsync(disc, multi, audio, [result.Tracks[0]], OutputQuality.OriginalPcm,
            multiOutput, new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
        var originalFile = Directory.GetFiles(originalFolder, "*.flac").Single();
        var originalFormat = await probe.ProbeFileAsync(originalFile, CancellationToken.None);
        Check(originalFormat.Channels == 6 && originalFormat.ChannelLayout == audio.ChannelLayout && originalFormat.BitDepth == 24 &&
              originalFormat.SampleRate == 88200 && originalFormat.SampleCount == audio.SampleCount, "Original multichannel format changed");
        async Task<byte[]> DecodeMulti(string file, string stem, bool cdReference = false)
        {
            var path = Path.Combine(output, stem + ".pcm");
            var args = new List<string> { "-v", "error", "-i", file };
            if (cdReference) args.AddRange(["-af", "aresample=44100:osf=s16:dither_method=triangular"]);
            args.AddRange(["-c:a", cdReference ? "pcm_s16le" : "pcm_s32le", "-f", cdReference ? "s16le" : "s32le", "-y", path]);
            await runner.RunAsync(paths.Ffmpeg, args, CancellationToken.None);
            return File.ReadAllBytes(path);
        }
        var cachedPcm = await DecodeMulti(multi.Clips[0].Sacd!.PcmPath, "cached-multichannel");
        var exportedPcm = await DecodeMulti(originalFile, "original-multichannel");
        Check(cachedPcm.SequenceEqual(exportedPcm), "Original export is not sample-identical to cached PCM");
        Passed("Original 6-channel FLAC matches every cached SACD PCM sample at 24-bit/88.2kHz");
        await converter.ConvertAsync(disc, multi, audio, [result.Tracks[0]], OutputQuality.Cd,
            multiOutput, new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
        var multichannelCd = Directory.GetFiles(originalFolder, "*.flac").Single(f => f != originalFile);
        var multichannelCdFormat = await probe.ProbeFileAsync(multichannelCd, CancellationToken.None);
        Check(multichannelCdFormat.Channels == 6 && multichannelCdFormat.ChannelLayout == audio.ChannelLayout &&
              multichannelCdFormat.SampleRate == 44100 && multichannelCdFormat.BitDepth == 16 &&
              multichannelCdFormat.SampleCount == ConversionService.ToSample(result.Tracks[0].EndTicks, 44100), "Multichannel CD format mismatch");
        var cdActualPath = Path.Combine(output, "multichannel-cd.pcm");
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-i", multichannelCd, "-c:a", "pcm_s16le", "-f", "s16le", "-y", cdActualPath], CancellationToken.None);
        var cdActual = File.ReadAllBytes(cdActualPath); var cdReference = await DecodeMulti(multi.Clips[0].Sacd!.PcmPath, "cd-reference", cdReference: true);
        Check(cdActual.Length == cdReference.Length, "Multichannel CD duration mismatch");
        for (int i = 0; i < cdActual.Length; i += 2)
            Check(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(cdActual.AsSpan(i, 2)) - BinaryPrimitives.ReadInt16LittleEndian(cdReference.AsSpan(i, 2))) <= 2,
                "CD multichannel samples differ from independently resampled input");
        Passed("CD 6-channel FLAC retains channel order with correct 16-bit/44.1kHz PCM and sample count");
        var selected = new[] { new TrackRow { Number = 1, Title = "Across boundary", StartTicks = 9000, EndTicks = 45000, IsChapter = false, Artist = "Track artist" } };
        var outputRoot = Path.Combine(output, "exports");
        var channels = StereoMixSettings.ChannelNames(audio).Select(c => new ExperimentalChannel(c, c == "FC" ? 1 : 0, -1, false)).ToArray();
        var mixer = new ChannelMixExport(audio, channels, "Center left");
        var folder = await converter.ConvertAsync(disc, multi, audio, selected, OutputQuality.HighResolution,
            outputRoot, new Progress<ConversionProgress>(), CancellationToken.None, channelMix: mixer);
        var flac = Directory.GetFiles(folder, "*.flac").Single();
        var actual = await probe.ProbeFileAsync(flac, CancellationToken.None);
        Check(actual.Channels == 2 && actual.SampleRate == 88200 && actual.BitDepth == 24 && actual.SampleCount == 70560, "Mixer export format or joined samples mismatch");
        var pcm = Path.Combine(output, "mixer.pcm");
        await runner.RunAsync(paths.Ffmpeg, ["-v", "error", "-i", flac, "-c:a", "pcm_s16le", "-f", "s16le", "-y", pcm], CancellationToken.None);
        var bytes = File.ReadAllBytes(pcm); bool audible = false;
        for (int i = 0; i < bytes.Length; i += 4) { Check(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i + 2, 2)) == 0, "Pan leaked into right output"); audible |= BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2)) != 0; }
        Check(audible, "DSD tone converted to silence");
        Passed("Mixer applies to FLAC, center-to-left pan, edited boundary spanning two tracks");
        var tags = await runner.RunAsync(paths.Ffprobe, ["-v", "error", "-show_entries", "format_tags", "-of", "json", flac], CancellationToken.None);
        Check(tags.Output.Contains("SACD ISO") && tags.Output.Contains("DSD_TO_PCM") && tags.Output.Contains("Multichannel"), "SACD conversion provenance missing");
        Passed("SACD area, encoding, PCM conversion and mixer export metadata");
        await converter.ConvertAsync(disc, multi, audio, [result.Tracks[0]], OutputQuality.Cd, outputRoot,
            new Progress<ConversionProgress>(), CancellationToken.None, saveStereoDownmix: true);
        var cd = Directory.GetFiles(folder, "*.flac").Single(f => f != flac);
        var cdFormat = await probe.ProbeFileAsync(cd, CancellationToken.None);
        Check(cdFormat.SampleRate == 44100 && cdFormat.BitDepth == 16 && cdFormat.Channels == 2, "CD stereo format mismatch");
        Passed("CD quality 16bit/44.1kHz stereo downmix");
        await converter.ConvertAsync(disc, multi, audio, [result.Tracks[0]], OutputQuality.HighResolution,
            outputRoot, new Progress<ConversionProgress>(), CancellationToken.None, saveIndividualChannels: true);
        var mono = Directory.GetFiles(folder, "*.flac", SearchOption.AllDirectories).Where(f => !Directory.GetFiles(folder, "*.flac").Contains(f)).ToArray();
        Check(mono.Length == 6, "Individual channel file count mismatch");
        foreach (var file in mono) Check((await probe.ProbeFileAsync(file, CancellationToken.None)).Channels == 1, "Individual channel was not mono");
        Passed("Six individual FLAC channels in a per-track multichannel folder");
        var cacheStorage = new CacheStorage(cachePaths, Path.Combine(output, "viewer-home"), () => false);
        var clearedCache = await cacheStorage.ClearAsync();
        Check(clearedCache.Errors.Count == 0 && clearedCache.Usage.AudioBytes == 0 && !File.Exists(multi.Clips[0].Sacd!.PcmPath) &&
              editStore.Load(disc, multi)![0].Title == "曲名 One", "Cache clearing damaged metadata or left audio behind");
        await reader.PrepareAsync(multi.Clips[0].Sacd!, CancellationToken.None);
        var regenerated = await DecodeMulti(multi.Clips[0].Sacd!.PcmPath, "regenerated-cache");
        Check(cachedPcm.SequenceEqual(regenerated), "Recreated cache PCM differs from the original");
        Passed("Clearing SACD cache preserves edits and regenerates identical multichannel PCM from the ISO");
        File.WriteAllText(Path.Combine(fixture, "five.flag"), "");
        var fiveReader = new SacdService(paths, runner, log, Path.Combine(output, "five-cache"), host, prefix);
        var fiveDisc = await fiveReader.AnalyzeAsync(iso, CancellationToken.None);
        var fiveResult = await fiveReader.InspectAsync(fiveDisc.Playlists[1], CancellationToken.None);
        Check(fiveResult.Streams.Single().Channels == 5 && fiveResult.Streams.Single().ChannelLayout == "5.0" &&
            !StereoMixSettings.ChannelNames(fiveResult.Streams.Single()).Contains("LFE"), "5.0 input fabricated an LFE channel");
        File.Delete(Path.Combine(fixture, "five.flag"));
        Passed("5.0 SACD layout remains five channels without invented LFE");
        var coldReader = new SacdService(paths, runner, log, Path.Combine(output, "headphone-cold-cache"), host, prefix);
        var coldDisc = await coldReader.AnalyzeAsync(iso, CancellationToken.None);
        var coldTitle = coldDisc.Playlists[1];
        var coldAudio = (await coldReader.InspectAsync(coldTitle, CancellationToken.None)).Streams.Single();
        var coldTrack = coldTitle.Clips[1].Sacd!;
        Check(!File.Exists(coldTrack.PcmPath), "Headphone cold-track fixture unexpectedly cached");
        var headphoneFactory = AudioNavigationService.HeadphoneOutputFactory;
        var headphoneOptions = HeadphoneSession.Current.Options;
        bool openedAfterCache = false; RecordingHeadphoneOutput? headphoneOutput = null;
        try
        {
            HeadphoneSession.Current.SetOptions(new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio });
            AudioNavigationService.HeadphoneOutputFactory = () =>
            {
                openedAfterCache = File.Exists(coldTrack.PcmPath);
                return headphoneOutput = new RecordingHeadphoneOutput();
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await new AudioNavigationService(paths, runner, probe, log, coldReader).PlayAsync(coldDisc, coldTitle, coldAudio,
                coldTitle.ChapterStarts[1], coldTitle.DurationTicks, timeout.Token);
            Check(openedAfterCache && headphoneOutput is { Disposed: true, SubmittedFrames: > 38000 },
                "Headphone output opened before cold SACD extraction/PCM preparation finished");
            Passed("Cold SACD next track finishes PCM preparation and prebuffering before headphone output opens");
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = headphoneFactory; HeadphoneSession.Current.SetOptions(headphoneOptions); }

        var navigation = new AudioNavigationService(paths, runner, probe, log, reader);
        var levels = 0; var chunks = new List<PreviewPcmChunk>();
        var priorDriver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        try
        {
            await navigation.PlayAsync(disc, multi, audio, 9000, 18000, CancellationToken.None,
                liveMix: () => new(StereoMixSettings.Default, null, channels), mixedLevels: (_, _) => levels++,
                visualization: new(() => true, chunks.Add));
        }
        finally { Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", priorDriver); }
        Check(levels > 0 && chunks.Count > 0 && chunks.All(c => c.Channels.SequenceEqual(StereoMixSettings.ChannelNames(audio))), "Playback did not feed mixer or visualizer");
        Check(chunks.SelectMany(c => c.Output.Where((_, i) => i % 4 >= 2)).All(b => b == 0), "Live center-to-left pan mismatch");
        Passed("Playback seek, live mixer meters and visualizer PCM (dummy audio device)");
        File.WriteAllText(Path.Combine(fixture, "slow.flag"), "");
        var uncached = stereo.Clips[1].Sacd!;
        using (var cancel = new CancellationTokenSource(350))
        {
            try { await reader.PrepareAsync(uncached, cancel.Token); throw new Exception("Extraction ignored cancellation"); } catch (OperationCanceledException) { }
        }
        File.Delete(Path.Combine(fixture, "slow.flag"));
        Check(!File.Exists(uncached.PcmPath) && !Directory.GetDirectories(cache, ".extract-*", SearchOption.AllDirectories).Any(), "Canceled track left ready output or temp files");
        Passed("Cancel kills external tool and removes incomplete jobs");
        File.WriteAllText(Path.Combine(fixture, "unknown.flag"), "");
        try { await reader.PrepareAsync(uncached, CancellationToken.None); throw new Exception("Unknown layout accepted"); } catch (InvalidDataException) { }
        File.Delete(Path.Combine(fixture, "unknown.flag"));
        Passed("Unknown or mismatched DSF layout cannot reach the mixer");
        Check(SHA(iso) == before, "Input ISO modified");
        using (var image = File.Open(iso, FileMode.Open, FileAccess.Write)) { image.Position = 0; image.WriteByte(1); }
        try { await reader.PrepareAsync(second, CancellationToken.None); throw new Exception("Changed ISO reused stale data"); } catch (IOException) { }
        Passed("Input remains unchanged; changed ISO requires reopening");
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        { mode = "Synthetic ISO/TOC external-tool fixture; real DSF, FFmpeg, mixer and FLAC; dummy audio output", passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SACD tests passed: {checks.Count}");
    }

    private static string SHA(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    private sealed class RecordingHeadphoneOutput : IHeadphoneOutput
    {
        public long SubmittedFrames { get; private set; }
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 0;
        public bool Disposed { get; private set; }
        public void WaitForSpace(int frames, CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Write(float[] samples, int frames) => SubmittedFrames += frames;
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() => Disposed = true;
    }

    private static void WriteDsf(string path, int channels, bool unknownLayout = false)
    {
        const int dsdRate = 2822400, block = 4096;
        const int samples = dsdRate; // one second, each channel a different audible tone
        var byteCount = samples / 8;
        var blocks = (byteCount + block - 1) / block;
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write("DSD "u8); writer.Write(28L); writer.Write(92L + blocks * block * channels); writer.Write(0L);
        writer.Write("fmt "u8); writer.Write(52L); writer.Write(1); writer.Write(0);
        writer.Write(unknownLayout ? 0 : channels == 2 ? 2 : channels == 5 ? 6 : 7);
        writer.Write(channels); writer.Write(dsdRate); writer.Write(1); writer.Write((long)samples); writer.Write(block); writer.Write(0);
        writer.Write("data"u8); writer.Write(12L + blocks * block * channels);
        var accumulators = new double[channels]; var buffer = new byte[block];
        for (int b = 0; b < blocks; b++)
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(buffer);
            for (int j = 0; j < block; j++)
            for (int bit = 0; bit < 8; bit++)
            {
                var sample = ((b * block + j) * 8 + bit);
                var target = .35 * Math.Sin(2 * Math.PI * (220 + c * 110) * sample / dsdRate);
                accumulators[c] += target;
                var high = accumulators[c] >= 0;
                accumulators[c] -= high ? 1 : -1;
                if (high) buffer[j] |= (byte)(1 << bit);
            }
            writer.Write(buffer);
        }
    }
}
