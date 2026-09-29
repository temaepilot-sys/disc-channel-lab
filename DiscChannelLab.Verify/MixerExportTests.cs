using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

internal static class MixerExportTests
{
    public static async Task RunDvdAsync(string source, string output)
    {
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var disc = service.Analyze(source);
        var title = disc.Playlists.First(x => x.Format == DiscFormat.DvdAudio);
        var inspected = await service.InspectPlaylistAsync(disc, title, CancellationToken.None);
        var audio = inspected.Streams.First(StereoMixSettings.Supports);
        var settings = new ChannelMixExport(audio, StereoMixSettings.ChannelNames(audio)
            .Select(code => new ExperimentalChannel(code, code == "FC" ? 1 : 0, -1, false)), "Center to left");
        var track = new TrackRow { Number = 1, Title = "Mixer DVD check", StartTicks = 45000,
            EndTicks = 135000, IsChapter = false };
        var folder = await new ConversionService(tools, runner, probe, log).ConvertAsync(disc, title, audio,
            [track], OutputQuality.HighResolution, output, new Progress<ConversionProgress>(), CancellationToken.None, channelMix: settings);
        var file = Directory.GetFiles(folder, "*.flac").Single();
        var format = await probe.ProbeFileAsync(file, CancellationToken.None);
        Check(format.Channels == 2 && format.SampleRate == audio.SampleRate && format.SampleCount == 2 * audio.SampleRate,
            "DVD mixer output format mismatch");
        var pcm = Path.Combine(output, "dvd-mixer-check.pcm");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-xerror", "-i", file, "-c:a", "pcm_s32le", "-f", "s32le", "-y", pcm], CancellationToken.None);
        var bytes = File.ReadAllBytes(pcm); var audible = false;
        for (var i = 0; i < bytes.Length; i += 8)
        {
            Check(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 4, 4)) == 0, "DVD center-to-left pan leaked into right channel");
            audible |= BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i, 4)) != 0;
        }
        Check(audible, "DVD mixer exported silence");
        Console.WriteLine($"DVD mixer export passed: {format.SampleRate}Hz / {format.BitDepth}bit / stereo, {format.SampleCount} samples, FC routed to L only.");
    }

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner);
        var source = Path.Combine(root, "BDMV", "STREAM", "00001.m2ts");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
            "aevalsrc=0.75|0|0|0|0|0:s=96000:c=5.1", "-t", "0.5", "-c:a", "pcm_bluray", "-sample_fmt", "s32",
            "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", source], CancellationToken.None);
        var audio = (await probe.ProbeClipAsync(source, CancellationToken.None)).Single();
        var start = audio.StartTimeTicks!.Value;
        var playlist = new PlaylistInfo { Id = 1, TitleName = "Mixer export tests", ChapterStarts = [0], DurationTicks = 45000,
            Clips = [new("00001", start, start + 22500, 0), new("00001", start, start + 22500, 22500)] };
        var disc = new DiscAnalysis { Root = root, AlbumTitle = "Synthetic mixer", DiscKey = new string('A', 64), Playlists = [playlist] };
        var track = new TrackRow { Number = 1, Title = "Pan right", StartTicks = 0, EndTicks = 45000, IsChapter = false };
        var channels = StereoMixSettings.ChannelNames(audio).Select(c => new ExperimentalChannel(c, c == "FL" ? 1 : 0, 1, false)).ToArray();
        var settings = new ChannelMixExport(audio, channels, "Right test");
        // Modifying the caller's array after capture must not change the job.
        channels[0] = channels[0] with { Muted = true };
        var converter = new ConversionService(tools, runner, probe, log);
        var outputRoot = Path.Combine(root, "output");
        async Task<string> Export(ChannelMixExport mix, OutputQuality quality, TrackRow[]? tracks = null) =>
            await converter.ConvertAsync(disc, playlist, audio, tracks ?? [track], quality, outputRoot,
                new Progress<ConversionProgress>(), CancellationToken.None, channelMix: mix);
        var folder = await Export(settings, OutputQuality.HighResolution);
        var file = Directory.GetFiles(folder, "*.flac").Single();
        var info = await probe.ProbeFileAsync(file, CancellationToken.None);
        Check(info.Channels == 2 && info.SampleRate == 96000 && info.BitDepth == 24 && info.SampleCount == 96000,
            "Wrong high-resolution format or joined sample count");
        var bytes = await Decode(file, "high");
        var right = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12000 * 8 + 4, 4)) / 2147483648d;
        var weightsL = new double[6]; var weightsR = new double[6];
        var original = StereoMixSettings.ChannelNames(audio).Select(c => new ExperimentalChannel(c, c == "FL" ? 1 : 0, 1, false)).ToArray();
        StereoPreviewMixer.BuildWeights(StereoMixSettings.ChannelNames(audio), new(StereoMixSettings.Default, null, original), weightsL, weightsR);
        Check(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12000 * 8, 4)) == 0 && Math.Abs(right - .75) < 1e-6,
            "Export differs from playback pan/gain");
        var metadata = await runner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format_tags", "-of", "json", file], CancellationToken.None);
        using var tags = JsonDocument.Parse(metadata.Output);
        var properties = tags.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject().ToArray();
        Check(properties.Any(p => p.Name.Equals("MIXER_SETTINGS", StringComparison.OrdinalIgnoreCase) && p.Value.GetString()!.Contains("Right test")),
            "Joined export lost mixer metadata");
        var originalBytes = File.ReadAllBytes(file);
        await Export(settings, OutputQuality.Cd);
        Check(Directory.GetFiles(folder, "*.flac").Length == 2 && File.ReadAllBytes(file).SequenceEqual(originalBytes), "Export overwrote existing variant");
        var cd = Directory.GetFiles(folder, "*.flac").Single(x => x != file);
        var cdInfo = await probe.ProbeFileAsync(cd, CancellationToken.None);
        Check(cdInfo.SampleRate == 44100 && cdInfo.BitDepth == 16 && cdInfo.SampleCount == 44100, "CD export format mismatch");

        var soloRight = new ChannelMixExport(audio, original.Select(c => c with { Solo = c.Code == "FR" }), "Solo silent FR");
        await Export(soloRight, OutputQuality.HighResolution);
        var silentSolo = Directory.GetFiles(folder, "*Solo silent FR*.flac").Single();
        Check((await Decode(silentSolo, "solo-silent")).All(b => b == 0), "Unselected FL leaked into Solo FLAC");
        var soloBothChannels = original.Select(c => c with { Solo = c.Code is "FL" or "FR" }).ToArray();
        var soloBoth = new ChannelMixExport(audio, soloBothChannels, "Solo FL and FR");
        soloBothChannels[0] = soloBothChannels[0] with { Solo = false };
        await Export(soloBoth, OutputQuality.HighResolution);
        var bothSolo = Directory.GetFiles(folder, "*Solo FL and FR*.flac").Single();
        var bothBytes = await Decode(bothSolo, "solo-both");
        Check(bothBytes.SequenceEqual(bytes), "Multiple solos changed gain or export snapshot was not frozen");
        var soloMetadata = await runner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format_tags", "-of", "json", bothSolo], CancellationToken.None);
        using var soloTags = JsonDocument.Parse(soloMetadata.Output);
        var mixerTag = soloTags.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
            .Single(p => p.Name.Equals("MIXER_SETTINGS", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        using var mixerJson = JsonDocument.Parse(mixerTag);
        Check(mixerJson.RootElement.GetProperty("Version").GetInt32() == 4 &&
              mixerJson.RootElement.GetProperty("Channels").EnumerateArray().Count(c => c.GetProperty("Solo").GetBoolean()) == 2,
            "FLAC metadata lost Solo settings");
        try { _ = new ChannelMixExport(audio, original.Select(c => c with { Muted = true, Solo = true }), "Invalid switches"); throw new Exception("Dual-on switches accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("Solo FLAC: excluded channels are silent, multiple solos preserve gain, frozen settings and Solo metadata verified.");

        var invertedChannels = original.Select(c => c with { Gain = 1, InvertPolarity = true }).ToArray();
        var inverted = new ChannelMixExport(audio, invertedChannels, "Polarity reversal");
        invertedChannels[0] = invertedChannels[0] with { InvertPolarity = false };
        await Export(inverted, OutputQuality.HighResolution);
        var invertedFile = Directory.GetFiles(folder, "*Polarity reversal*.flac").Single();
        var invertedPcm = await Decode(invertedFile, "inverted");
        Check(invertedPcm.Length == bytes.Length, "Polarity changed sample count");
        for (var i = 0; i < bytes.Length; i += 4)
            Check(BinaryPrimitives.ReadInt32LittleEndian(invertedPcm.AsSpan(i, 4)) ==
                  -BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i, 4)), "FLAC polarity did not invert exact PCM samples");
        var invertedMetadata = await runner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format_tags", "-of", "json", invertedFile], CancellationToken.None);
        using var invertedTags = JsonDocument.Parse(invertedMetadata.Output);
        using var invertedJson = JsonDocument.Parse(invertedTags.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
            .Single(p => p.Name.Equals("MIXER_SETTINGS", StringComparison.OrdinalIgnoreCase)).Value.GetString()!);
        Check(invertedJson.RootElement.GetProperty("Channels").EnumerateArray().All(c => c.GetProperty("InvertPolarity").GetBoolean()),
            "FLAC metadata lost polarity settings");
        Console.WriteLine("Polarity FLAC: all decoded samples inverted exactly, negative pan coefficients accepted, frozen settings and metadata preserved.");

        try { _ = new ChannelMixExport(audio, original.Select(x => x with { Gain = 1.001 }), "Over 100"); throw new Exception("Gain above 100% accepted"); }
        catch (InvalidDataException) { }
        var loud = original.Select(x => x with { Gain = x.Code == "FL" ? 1 : 0 }).ToArray();
        var boosted = new ChannelMixExport(audio, loud, "Saturation test", 2);
        await Export(boosted, OutputQuality.HighResolution);
        var boostedFile = Directory.GetFiles(folder, "*Saturation*.flac").Single();
        var boostedBytes = await Decode(boostedFile, "saturation");
        Check(BinaryPrimitives.ReadInt32LittleEndian(boostedBytes.AsSpan(12000 * 8 + 4, 4)) > 2147480000,
            "Overload wrapped around or was auto-normalized instead of saturating");
        var beforeFailure = Directory.GetFiles(folder, "*.flac").Length;
        try
        {
            await Export(settings, OutputQuality.Cd, [track, new TrackRow { Number = 2, Title = "Bad range", StartTicks = 0, EndTicks = 90000, IsChapter = false }]);
            throw new Exception("Invalid second track accepted");
        }
        catch (InvalidDataException) { }
        Check(Directory.GetFiles(folder, "*.flac").Length == beforeFailure &&
              !Directory.GetDirectories(Path.Combine(outputRoot, "Synthetic mixer"), ".Disc2Flac-*").Any(),
            "Failed job left completed or temporary files");
        try { _ = new ChannelMixExport(audio, loud.Select(x => x with { Pan = double.NaN }), "Invalid"); throw new Exception("NaN accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("Mixer FLAC: CD/24-bit, cross-clip join, preview equivalence, settings tags, frozen snapshot, no overwrite, saturation and failed-job cleanup passed.");
        async Task<byte[]> Decode(string input, string stem)
        {
            var output = Path.Combine(root, stem + ".pcm");
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-xerror", "-i", input, "-c:a", "pcm_s32le", "-f", "s32le", "-y", output], CancellationToken.None);
            return File.ReadAllBytes(output);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
