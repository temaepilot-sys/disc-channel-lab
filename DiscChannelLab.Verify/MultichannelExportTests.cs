using Disc2Flac;
using System.IO;

internal static class MultichannelExportTests
{
    public static async Task RunAsync(string output)
    {
        var root = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
        var log = new AppLog(Path.Combine(root, "logs")); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var converter = new ConversionService(tools, runner, probe, log);
        foreach (var depth in new[] { 16, 24 })
        {
            var id = depth.ToString("00000"); var source = Path.Combine(root, "BDMV", "STREAM", id + ".m2ts");
            var expression = string.Join('|', Enumerable.Range(1, 6).Select(c => $"0.1*sin(2*PI*{200 * c}*t)"));
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"aevalsrc={expression}:s=48000:c=5.1",
                "-t", "0.4", "-c:a", "pcm_bluray", "-sample_fmt", depth == 16 ? "s16" : "s32",
                "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", source], CancellationToken.None);
            var audio = (await probe.ProbeClipAsync(source, CancellationToken.None)).Single();
            if (audio.SampleRate != 48000 || audio.BitDepth != depth || audio.CanMakeHighResolution)
                throw new InvalidDataException("Synthetic source format mismatch");
            var start = audio.StartTimeTicks!.Value;
            var title = new PlaylistInfo { Id = depth, TitleName = "Original PCM test", ChapterStarts = [0], DurationTicks = 18000,
                Clips = [new(id, start, start + 18000, 0)] };
            var disc = new DiscAnalysis { Root = root, AlbumTitle = "Multichannel test", DiscKey = new string('A', 64), Playlists = [title] };
            var track = new TrackRow { Number = depth, Title = "Original " + depth, StartTicks = 0, EndTicks = 18000, IsChapter = false };
            var folder = await converter.ConvertAsync(disc, title, audio, [track], OutputQuality.OriginalPcm,
                Path.Combine(root, "exports"), new Progress<ConversionProgress>(), CancellationToken.None, saveMultichannelFlac: true);
            var file = Directory.GetFiles(folder, $"{depth:00} *.flac").Single();
            var actual = await probe.ProbeFileAsync(file, CancellationToken.None);
            if (actual.SampleRate != 48000 || actual.BitDepth != depth || actual.Channels != 6 ||
                actual.ChannelLayout != audio.ChannelLayout || actual.SampleCount != 19200)
                throw new InvalidDataException("Original PCM output format mismatch");
            async Task<byte[]> Decode(string input, string stem)
            {
                var raw = Path.Combine(root, stem + ".pcm");
                await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", input, "-t", "0.4", "-map", "0:a:0",
                    "-c:a", "pcm_s32le", "-f", "s32le", "-y", raw], CancellationToken.None);
                return File.ReadAllBytes(raw);
            }
            var before = await Decode(source, "source-" + depth); var after = await Decode(file, "export-" + depth);
            if (!before.SequenceEqual(after)) throw new InvalidDataException("Original multichannel PCM samples changed");
            Console.WriteLine($"PASS Original {depth}-bit/48kHz six-channel export preserves every PCM sample and channel layout");
        }
    }
}
