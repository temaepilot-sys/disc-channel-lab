using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Uses an external checkout of sfiera/flac-test-files. No sample audio is bundled.</summary>
internal static class FlacSampleTests
{
    public static async Task RunAsync(string fixtures, string output)
    {
        var root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        var layouts = new Dictionary<string, string[]>
        {
            ["stereo"] = ["FL", "FR"], ["center"] = ["FL", "FR", "FC"],
            ["quad"] = ["FL", "FR", "BL", "BR"], ["surround50"] = ["FL", "FR", "FC", "BL", "BR"],
            ["surround51"] = ["FL", "FR", "FC", "LFE", "BL", "BR"],
            ["surround61"] = ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
            ["surround71"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"]
        };
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var converter = new ConversionService(tools, runner, probe, log);
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        var reports = new List<object>();
        async Task<byte[]> Decode(string input, string name, bool cd)
        {
            var raw = Path.Combine(root, name + ".pcm");
            var arguments = new List<string> { "-v", "error", "-i", input, "-map", "0:a:0" };
            if (cd) arguments.AddRange(["-af", "aresample=44100:osf=s16:dither_method=triangular"]);
            arguments.AddRange(["-c:a", "pcm_s16le", "-f", "s16le", "-y", raw]);
            await runner.RunAsync(tools.Ffmpeg, arguments, CancellationToken.None);
            return File.ReadAllBytes(raw);
        }
        foreach (var (name, declaredNames) in layouts)
        {
            var file = Path.GetFullPath(Path.Combine(fixtures, name + ".flac"));
            var before = Hash(file);
            var disc = await service.AnalyzeAsync(file, CancellationToken.None); var title = disc.Playlists.Single();
            var (streams, tracks) = await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None);
            var stream = streams.Single();
            // RFC 9639 calls the 5/6-channel pair "back/surround". FFmpeg names
            // this pair SL/SR when no explicit FLAC speaker-mask tag is present.
            // This is a label convention; the channel slots must never be reordered.
            var names = stream.ChannelLayout is "5.0(side)" or "5.1(side)"
                ? declaredNames.Select(c => c == "BL" ? "SL" : c == "BR" ? "SR" : c).ToArray() : declaredNames;
            Check(title.Availability == AudioAvailability.Ready && stream.Channels == names.Length && stream.BitDepth == 16 && stream.SampleRate == 48000,
                name + " input format");
            if (stream.Channels > 2)
            {
                Check(StereoMixSettings.ChannelNames(stream).SequenceEqual(names), name + " explicit speaker order");
                var originalFolder = await converter.ConvertAsync(disc, title, stream, tracks, OutputQuality.OriginalPcm,
                    Path.Combine(root, name, "original"), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
                var originalPcm = await Decode(file, name + "-source", false);
                var exportedPcm = await Decode(Directory.GetFiles(originalFolder, "*.flac").Single(), name + "-original", false);
                Check(originalPcm.SequenceEqual(exportedPcm), name + " original multichannel PCM");
                var soloFolder = await converter.ConvertAsync(disc, title, stream, tracks, OutputQuality.Cd,
                    Path.Combine(root, name, "channels"), new Progress<ConversionProgress>(), CancellationToken.None, saveIndividualChannels: true);
                var reference = await Decode(file, name + "-reference", true);
                for (var channel = 0; channel < names.Length; channel++)
                {
                    var monoFile = Directory.GetFiles(soloFolder, "* - " + names[channel] + ".flac", SearchOption.AllDirectories).Single();
                    var mono = await Decode(monoFile, name + "-" + names[channel], false);
                    Check(mono.Length * names.Length == reference.Length, name + " " + names[channel] + " full sample count");
                    for (var sample = 0; sample < mono.Length / 2; sample++)
                    {
                        var actual = BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(sample * 2, 2));
                        var expected = BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan((sample * names.Length + channel) * 2, 2));
                        if (Math.Abs(actual - expected) > 2) throw new InvalidDataException(name + " " + names[channel] + " channel mapping mismatch");
                    }
                }
            }
            var stereoFolder = await converter.ConvertAsync(disc, title, stream, tracks, OutputQuality.Cd,
                Path.Combine(root, name, "stereo"), new Progress<ConversionProgress>(), CancellationToken.None, saveStereoDownmix: stream.Channels > 2);
            var stereo = await probe.ProbeFileAsync(Directory.GetFiles(stereoFolder, "*.flac").Single(), CancellationToken.None);
            Check(stereo.Channels == 2 && stereo.SampleRate == 44100 && stereo.BitDepth == 16, name + " CD stereo export");
            var chunks = new List<PreviewPcmChunk>(); var meters = 0;
            var driver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
            Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
            try
            {
                await navigation.PlayAsync(disc, title, stream, 0, Math.Min(title.DurationTicks, 22500), CancellationToken.None,
                    liveMix: () => new(StereoMixSettings.Default, null), mixedLevels: (_, _) => meters++, visualization: new(() => true, chunks.Add));
            }
            finally { Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", driver); }
            Check(chunks.Count > 0 && chunks.All(c => c.Channels.SequenceEqual(names)) && (stream.Channels == 2 || meters > 0), name + " playback, meters and visualizer channels");
            Check(Hash(file) == before, name + " source unchanged");
            reports.Add(new { File = name + ".flac", stream.Channels, stream.ChannelLayout, SpeakerOrder = names,
                Input = "Pass", Playback = "Pass (dummy audio)", Visualization = "Pass (PCM feed)", StereoExport = "Pass",
                OriginalMultichannel = stream.Channels > 2 ? "PCM identical" : "Not applicable (stereo)",
                IndividualChannels = stream.Channels > 2 ? "All channels match reference within 2 LSB dither" : "Not applicable (stereo)" });
            Console.WriteLine("PASS " + name + ".flac: " + string.Join(' ', names) + "; input, playback, visualizer and exports");
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("All seven sfiera FLAC samples passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
