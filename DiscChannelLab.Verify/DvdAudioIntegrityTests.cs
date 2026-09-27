using Disc2Flac;
using System.Globalization;
using System.IO;
using System.Text.Json;

internal static class DvdAudioIntegrityTests
{
    public static async Task RunAsync(string root, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var disc = service.Analyze(root);
        var reports = new List<object>();
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        foreach (var title in disc.Playlists.Where(t => t.Format == DiscFormat.DvdAudio))
        {
            var inspected = await service.InspectPlaylistAsync(disc, title, CancellationToken.None);
            var stream = inspected.Streams.FirstOrDefault(x => x.CanMakeCd)
                ?? throw new InvalidDataException("DVD-Audio has no decoded stream: " + title.DisplayName);
            var audio = title.DvdAudio!;
            Console.WriteLine($"{title.Id}: {stream.DisplayName}; {audio.Programs.Count} tracks");
            for (var i = 0; i < audio.Programs.Count; i++)
            {
                var program = audio.Programs[i];
                // Decode every byte in the track's sector range; errors are fatal.
                var result = await runner.RunWithInputAsync(tools.Ffmpeg,
                    ["-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-f", "mpeg",
                     "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0", "-map", $"0:{stream.Index}",
                     "-c:a", "pcm_s24le", "-progress", "pipe:1", "-f", "hash", "-hash", "sha256", "pipe:1"],
                    (input, token) => DvdAudioSectors.CopyAsync(root, audio.TitleSet, program.StartSector, program.EndSector, input, token),
                    CancellationToken.None);
                if (!result.Output.Contains("SHA256=") || !result.Output.Contains("progress=end"))
                    throw new InvalidDataException("Full decode did not finish.");
                var decoded = result.Output.Split('\n').Where(x => x.StartsWith("out_time_us="))
                    .Select(x => long.Parse(x[12..].Trim(), CultureInfo.InvariantCulture)).Last() / 1_000_000d;
                var duration = (program.EndTicks - program.StartTicks) / 45000d;
                if (decoded + .03 < duration) throw new InvalidDataException($"Track {i + 1} decoded short: {decoded}/{duration}");
                var started = false; var meterCount = 0;
                await navigation.PlayAsync(disc, title, stream, program.StartTicks,
                    Math.Min(program.EndTicks, program.StartTicks + 45000), CancellationToken.None,
                    _ => started = true, channelLevels: (_, _, _, _) => meterCount++);
                if (!started || meterCount == 0) throw new InvalidDataException("Playback/meter did not start.");
                reports.Add(new { Title = title.Id, Track = i + 1, program.StartSector, program.EndSector,
                    ExpectedSeconds = duration, DecodedSeconds = decoded, Decode = "passed", Playback = "passed",
                    PcmHash = result.Output.Split('\n').Single(x => x.StartsWith("SHA256=")).Trim(),
                    DecoderMessages = result.Error });
                Console.WriteLine($"Track {i + 1}: full decode {decoded:0.000}s / expected {duration:0.000}s; playback and meters OK");
            }
            var first = audio.Programs[0];
            var excerpt = new TrackRow { Number = 1, Title = "DVD-Audio LPCM check", StartTicks = first.StartTicks,
                EndTicks = Math.Min(first.EndTicks, first.StartTicks + 90000), IsChapter = false };
            var saved = await new ConversionService(tools, runner, probe, log).ConvertAsync(disc, title, stream, [excerpt],
                OutputQuality.HighResolution, Path.Combine(output, "flac-check"), new Progress<ConversionProgress>(), CancellationToken.None);
            var flac = Directory.GetFiles(saved, "*.flac", SearchOption.AllDirectories).Last();
            var format = await probe.ProbeFileAsync(flac, CancellationToken.None);
            if (format.SampleRate != stream.SampleRate || format.BitDepth != 24 || format.Channels != stream.Channels)
                throw new InvalidDataException("DVD-Audio FLAC format mismatch.");
            Console.WriteLine($"FLAC check: {format.SampleRate}Hz / {format.BitDepth}bit / {format.Channels}ch, {format.SampleCount} samples");
        }
        if (reports.Count == 0) throw new InvalidDataException("No DVD-Audio tracks inspected.");
        var version = (await runner.RunAsync(tools.Ffmpeg, ["-version"], CancellationToken.None)).Output.Split('\n')[0].Trim();
        await File.WriteAllTextAsync(Path.Combine(output, "integrity-report.json"),
            JsonSerializer.Serialize(new { disc.AlbumTitle, disc.DiscKey, Ffmpeg = version, Tracks = reports }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
