using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class AudioFileTests
{
    public static async Task RunAsync(string output)
    {
        var root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var converter = new ConversionService(tools, runner, probe, log);
        const int samples = 19201;
        async Task<string> Generate(string name, string codec, string layout, int channels, int track = 1)
        {
            var file = Path.Combine(root, name);
            var expression = string.Join('|', Enumerable.Range(1, channels).Select(c => $"0.08*sin(2*PI*{200 * c}*t)"));
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"aevalsrc={expression}:s=48000:c={layout}",
                "-af", $"atrim=end_sample={samples}", "-c:a", codec, "-metadata", "album=File album", "-metadata", $"title=Song {track}",
                "-metadata", $"track={track}", "-metadata", $"artist=Artist {track}", "-metadata", "album_artist=Album artist", "-y", file], CancellationToken.None);
            return file;
        }
        var first = await Generate("01 フロント.flac", "flac", "5.1", 6);
        var second = await Generate("02 次の曲.flac", "flac", "5.1", 6, 2);
        var before = new[] { first, second }.Select(Hash).ToArray();
        var disc = await service.AnalyzeAudioFilesAsync([second, first], CancellationToken.None);
        var playlist = disc.Playlists.Single();
        var (streams, tracks) = await service.AnalyzePlaylistAsync(disc, playlist, CancellationToken.None);
        var stream = streams.Single();
        Check(disc.Format == DiscFormat.AudioFile && playlist.Availability == AudioAvailability.Ready &&
            stream.Channels == 6 && stream.ChannelLayout == "5.1" && stream.BitDepth == 24 && stream.SampleCount == samples,
            "FLAC input exposes actual six-channel layout, depth and exact sample count");
        Check(tracks.Count == 2 && tracks.Select(t => t.Title).SequenceEqual(new[] { "Song 1", "Song 2" }) &&
            tracks.Select(t => t.Artist).SequenceEqual(new[] { "Artist 1", "Artist 2" }) && playlist.TitleName == "File album",
            "Multiple compatible files form an ordered album with embedded track titles and artists");
        async Task<byte[]> Decode(string file, string name)
        {
            var raw = Path.Combine(root, name + ".pcm");
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", file, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "s32le", "-y", raw], CancellationToken.None);
            return File.ReadAllBytes(raw);
        }
        var folder = await converter.ConvertAsync(disc, playlist, stream, tracks, OutputQuality.OriginalPcm,
            Path.Combine(root, "original"), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
        var exports = Directory.GetFiles(folder, "*.flac").Order().ToArray();
        for (var i = 0; i < exports.Length; i++)
        {
            var sourcePcm = await Decode(i == 0 ? first : second, "source-" + i);
            var exportedPcm = await Decode(exports[i], "export-" + i);
            Check(sourcePcm.SequenceEqual(exportedPcm),
                "Original multichannel export preserves every PCM sample, including a non-tick-aligned file " + (i + 1));
        }
        var merged = new TrackRow { Number = 1, StartTicks = 0, EndTicks = playlist.DurationTicks, IsChapter = false, Title = "Joined files" };
        var mergedFolder = await converter.ConvertAsync(disc, playlist, stream, [merged], OutputQuality.OriginalPcm,
            Path.Combine(root, "joined"), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
        var mergedPcm = await Decode(Directory.GetFiles(mergedFolder, "*.flac").Single(), "joined");
        var originals = File.ReadAllBytes(Path.Combine(root, "source-0.pcm")).Concat(File.ReadAllBytes(Path.Combine(root, "source-1.pcm")));
        Check(mergedPcm.SequenceEqual(originals), "Merging complete audio files preserves every PCM sample across the join");
        var channels = StereoMixSettings.ChannelNames(stream).Select(c => new ExperimentalChannel(c, c == "FC" ? 1 : 0, -1, false)).ToArray();
        var mix = new ChannelMixExport(stream, channels, "Center left");
        var mixedFolder = await converter.ConvertAsync(disc, playlist, stream, [tracks[0]], OutputQuality.Cd,
            Path.Combine(root, "mixed"), new Progress<ConversionProgress>(), CancellationToken.None, channelMix: mix);
        var mixed = await Decode(Directory.GetFiles(mixedFolder, "*.flac").Single(), "mixed");
        var quietRight = true; var audibleLeft = false;
        for (var i = 0; i < mixed.Length; i += 8)
        {
            quietRight &= Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(mixed.AsSpan(i + 4, 4))) <= 65536;
            audibleLeft |= Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(mixed.AsSpan(i, 4))) > 655360;
        }
        Check(quietRight && audibleLeft,
            "Existing mixer settings apply to FLAC-file input and exported center-to-left stereo");
        var soloFolder = await converter.ConvertAsync(disc, playlist, stream, [tracks[0]], OutputQuality.Cd,
            Path.Combine(root, "solo"), new Progress<ConversionProgress>(), CancellationToken.None, saveIndividualChannels: true);
        Check(Directory.GetFiles(soloFolder, "*.flac", SearchOption.AllDirectories).Length == 6,
            "File input supports all six individual-channel FLAC exports");
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        var chunks = new List<PreviewPcmChunk>(); var levels = 0; var segments = 0;
        var previousDriver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        try
        {
            await navigation.PlayAsync(disc, playlist, stream, 4500, playlist.DurationTicks, CancellationToken.None,
                segmentStarted: _ => segments++, liveMix: () => new(StereoMixSettings.Default, null, channels),
                mixedLevels: (_, _) => levels++, visualization: new(() => true, chunks.Add));
        }
        finally { Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", previousDriver); }
        // Native playback retains one output session across clip boundaries; its start callback
        // describes that session, not each decoder. Verify PCM actually reaches the second file.
        Check(segments >= 1 && levels > 0 && chunks.Any(c => c.Seconds >= playlist.Clips[1].PlaylistStartTicks / 45000d) &&
            chunks.All(c => c.Channels.SequenceEqual(StereoMixSettings.ChannelNames(stream))),
            "Seeking and playback across two files feed synchronized mixer meters and visualizer PCM");
        Check(chunks.SelectMany(c => c.Output.Where((_, i) => i % 4 >= 2)).All(b => b == 0), "Live PCM mixer pan also applies to file playback");
        foreach (var fixture in new[] {
            ("surround.wav", "pcm_s24le", "5.1(side)", 6), ("quad.aiff", "pcm_s24be", "quad", 4),
            ("eight.flac", "flac", "7.1", 8), ("six.m4a", "alac", "5.1", 6), ("six.wv", "wavpack", "5.1", 6),
            ("six-aac.m4a", "aac", "5.1", 6), ("six.opus", "libopus", "5.1", 6), ("stereo.wav", "pcm_s16le", "stereo", 2) })
        {
            var file = await Generate(fixture.Item1, fixture.Item2, fixture.Item3, fixture.Item4);
            var input = await service.AnalyzeAsync(file, CancellationToken.None); var title = input.Playlists.Single();
            var inspected = await service.AnalyzePlaylistAsync(input, title, CancellationToken.None);
            Check(title.Availability == AudioAvailability.Ready && inspected.Streams[0].Channels == fixture.Item4 &&
                (fixture.Item4 <= 2 || AudioFileReader.IsUsable(inspected.Streams[0])),
                $"{fixture.Item2} / {fixture.Item3} file opens and decodes without channel-order guessing");
            if (fixture.Item2 is "aac" or "libopus")
            {
                var saved = await converter.ConvertAsync(input, title, inspected.Streams[0], inspected.Tracks, OutputQuality.Cd,
                    Path.Combine(root, "compressed-" + fixture.Item2), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
                var savedStream = await probe.ProbeFileAsync(Directory.GetFiles(saved, "*.flac").Single(), CancellationToken.None);
                Check(savedStream.Channels == 6 && savedStream.SampleRate == 44100 && savedStream.BitDepth == 16,
                    fixture.Item2 + " exports CD-quality multichannel FLAC with codec delay excluded");
            }
        }
        var chapterMetadata = Path.Combine(root, "chapters.ffmeta");
        File.WriteAllText(chapterMetadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=200\ntitle=First part\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=200\nEND=400\ntitle=Second part\n");
        var chapterFile = Path.Combine(root, "chapters.mka");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", first, "-i", chapterMetadata, "-map", "0:a:0", "-map_chapters", "1", "-c:a", "copy", "-y", chapterFile], CancellationToken.None);
        var chapterDisc = await service.AnalyzeAsync(chapterFile, CancellationToken.None);
        var chapterResult = await service.AnalyzePlaylistAsync(chapterDisc, chapterDisc.Playlists.Single(), CancellationToken.None);
        Check(chapterResult.Tracks.Select(t => t.Title).SequenceEqual(new[] { "First part", "Second part" }) && chapterResult.Tracks[1].StartTicks == 9000,
            "Embedded audio-file chapter boundaries and names remain individually selectable");
        var multiFile = Path.Combine(root, "two-streams.mka");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", first, "-i", first, "-map", "0:a:0", "-map", "1:a:0", "-c:a", "copy", "-y", multiFile], CancellationToken.None);
        var multiDisc = await service.AnalyzeAsync(multiFile, CancellationToken.None);
        var multi = await service.AnalyzePlaylistAsync(multiDisc, multiDisc.Playlists.Single(), CancellationToken.None);
        Check(multi.Streams.Count == 2 && AudioStreamMatcher.Resolve(multi.Streams[1], (await probe.ProbeAudioFileAsync(multiFile, CancellationToken.None)).Analysis.Streams).Index == 1,
            "Same-format audio streams in one container remain explicitly distinguishable");
        var multiFolder = await converter.ConvertAsync(multiDisc, multiDisc.Playlists.Single(), multi.Streams[1], multi.Tracks,
            OutputQuality.OriginalPcm, Path.Combine(root, "container"), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
        var containerPcm = await Decode(Directory.GetFiles(multiFolder, "*.flac").Single(), "container");
        Check(containerPcm.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "source-0.pcm"))),
            "Container stream selection and whole-file export preserve samples beyond millisecond duration precision");
        var unknown = await Generate("unknown.wav", "pcm_s24le", "5.1", 6);
        var wav = File.ReadAllBytes(unknown); var fmt = Find(wav, "fmt "u8.ToArray());
        Check(fmt >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(fmt + 8, 2)) == 0xfffe, "Unknown-layout fixture has WAVEFORMATEXTENSIBLE header");
        wav.AsSpan(fmt + 28, 4).Clear(); File.WriteAllBytes(unknown, wav);
        var unknownDisc = await service.AnalyzeAsync(unknown, CancellationToken.None);
        var unknownTitle = unknownDisc.Playlists.Single();
        var unknownResult = await service.AnalyzePlaylistAsync(unknownDisc, unknownTitle, CancellationToken.None);
        Check(unknownTitle.Availability == AudioAvailability.Unsupported && unknownResult.Streams.All(s => !AudioFileReader.IsUsable(s)),
            "A multichannel WAV without a speaker mask is shown as unsupported; channels are never guessed");

        Exception? uiError = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var edits = new TrackEditsStore(log, Path.Combine(root, "edits")); var model = new MainViewModel(edits);
                window = new MainWindow(model);
                Check(window.FindName("OpenAudioFilesButton") is Button, "Main GUI exposes audio-file input");
                Pump(model.OpenAudioFilesAsync([second, first]));
                Check(model.Tracks.Count == 2 && model.CanPlay && model.CanSaveMultichannel, "Real view model connects files to playback and multichannel export");
                model.Tracks[0].Title = "User title";
                Pump(model.DetectInsertedDiscAsync());
                Check(model.Source == first && model.Tracks[0].Title == "User title", "Optical-drive polling preserves the selected audio collection");
                Pump(model.OpenSourceAsync(first));
                Check(model.Tracks.Count == 2 && model.Tracks[0].Title == "User title", "Reloading a file collection restores its saved track edits");
                model.StopInspection(); model.Visualization.Dispose(); model.DetachLanguage();
            }
            catch (Exception ex) { uiError = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (uiError is not null) throw uiError;
        Check(before.SequenceEqual(new[] { first, second }.Select(Hash)), "Opening, playing, mixing and saving leave source audio files unchanged");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
        try { AudioFileReader.RequireUnchanged(playlist.Clips[0].File!); throw new Exception("Changed file reused"); } catch (IOException) { }
        Check(true, "Changed source files require reopening instead of reusing stale ranges");
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static int Find(byte[] data, byte[] value) { for (int i = 0; i <= data.Length - value.Length; i++) if (data.AsSpan(i, value.Length).SequenceEqual(value)) return i; return -1; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Pump(Task task)
    {
        do
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        } while (!task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
}
