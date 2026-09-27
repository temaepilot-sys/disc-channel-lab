using Disc2Flac;
using System.Buffers.Binary;
using System.IO;

internal static class SilentTailTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    public static async Task SyntheticAsync(string output)
    {
        var root = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var reader = new DiscService(probe, log);
        string Clip(string id) => Path.Combine(root, "BDMV", "STREAM", id + ".m2ts");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
            "aevalsrc=0.1|0.2|0.05|0.03|0.04|0.06:s=96000:c=5.1", "-t", "2", "-c:a", "pcm_bluray",
            "-sample_fmt", "s32", "-streamid", "0:4352", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", Clip("00001")], CancellationToken.None);
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=32x32:r=24",
            "-t", "1", "-c:v", "mpeg2video", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", Clip("00002")], CancellationToken.None);
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "1", "-c:a", "ac3", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", Clip("00003")], CancellationToken.None);
        File.WriteAllText(Clip("00004"), "damaged media");
        var source = (await probe.ProbeClipAsync(Clip("00001"), CancellationToken.None)).Single();
        var start = source.StartTimeTicks!.Value;
        var title = new PlaylistInfo { Id = 1, Clips = [new("00001", start, start + 90000, 0),
            new("00002", 0, 45045, 90000)], ChapterStarts = [0, 95000], DurationTicks = 135045 };
        var disc = new DiscAnalysis { Root = root, DiscKey = new string('B', 64), AlbumTitle = "Silent tail test", Playlists = [title] };
        var inspected = await reader.InspectPlaylistAsync(disc, title, CancellationToken.None);
        var stream = inspected.Streams.Single();
        Check(inspected.Availability == AudioAvailability.Ready && stream.SilentTailClipId == "00002" && !stream.HasPartialCoverage,
            "Verified video-only tail still blocks playback");
        var tracks = DiscService.BuildTracks(disc, title);
        Check(tracks.Count == 1 && tracks[0].EndTicks == title.DurationTicks && stream.Covers(title, tracks[0]),
            "Merged tail lost duration or remained unavailable");
        title.MergeShortTail = false;
        Check(DiscService.BuildTracks(disc, title).All(t => stream.Covers(title, t)), "Original chapters are not playable");
        foreach (var tailId in new[] { "00003", "00004", "99999" })
        {
            var bad = new PlaylistInfo { Id = 20, Clips = [title.Clips[0], title.Clips[1] with { Id = tailId }],
                ChapterStarts = [0], DurationTicks = title.DurationTicks };
            var badInfo = await reader.InspectPlaylistAsync(disc, bad, CancellationToken.None, force: true);
            Check(badInfo.Streams.Count == 1 && badInfo.Streams.All(s => s.SilentTailClipId is null && !s.Covers(bad, tracks[0])),
                "Unsupported, corrupt or missing audio was masked as silence");
        }
        foreach (var clips in new IReadOnlyList<ClipInfo>[]
        {
            [title.Clips[0], title.Clips[1] with { OutTicks = 135000 }],
            [title.Clips[0], title.Clips[1], title.Clips[0] with { PlaylistStartTicks = 135045 }]
        })
        {
            var rejected = new PlaylistInfo { Id = 21, Clips = clips, ChapterStarts = [0],
                DurationTicks = clips.Sum(c => c.OutTicks - c.InTicks) };
            var result = await reader.InspectPlaylistAsync(disc, rejected, CancellationToken.None, force: true);
            Check(result.Streams.All(s => s.SilentTailClipId is null), "Long or interior video-only gap was accepted");
        }
        await CheckBoundaryAsync(disc, title, stream, root, tools, runner, probe, log, highRes: true);
        Console.WriteLine("PASS: full packet scan, merged/original chapters, unsupported/corrupt/missing/long/interior rejection, live mixer tail and CD/24-bit exports.");
    }

    public static async Task DiscAsync(string root, string output)
    {
        Directory.CreateDirectory(output);
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var reader = new DiscService(probe, log);
        var disc = reader.Analyze(root);
        var title = disc.Playlists[0];
        var inspected = await reader.InspectPlaylistAsync(disc, title, CancellationToken.None);
        var stream = inspected.Streams.First(s => s.SilentTailClipId is not null);
        Check(inspected.Availability == AudioAvailability.Ready && DiscService.BuildTracks(disc, title).All(t => stream.Covers(title, t)),
            "Disc tracks still blocked by the silent tail");
        await CheckBoundaryAsync(disc, title, stream, output, tools, runner, probe, log, highRes: false);
        await DiscDiagnostics.SaveAsync(disc, Path.Combine(output, "diagnostic.json"), CancellationToken.None);
        Console.WriteLine($"PASS: title {title.Id:00000}, {stream.DisplayName}; audio-to-video tail playback and exact FLAC length.");
    }

    private static async Task CheckBoundaryAsync(DiscAnalysis disc, PlaylistInfo title, AudioStreamInfo stream,
        string output, ToolPaths tools, ProcessRunner runner, FfprobeService probe, AppLog log, bool highRes)
    {
        var tail = title.Clips[^1];
        var start = Math.Max(0, tail.PlaylistStartTicks - 45000);
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        var starts = new List<long>(); var silentFrames = 0; var nonSilentFrames = 0;
        var live = new PreviewMixState(StereoMixSettings.Default, null, StereoPreviewMixer.StandardChannels(stream));
        await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc, title, stream,
            start, title.DurationTicks, CancellationToken.None, segmentStarted: starts.Add,
            channelLevels: (position, _, peaks, _) =>
            { if (position == tail.PlaylistStartTicks) { Check(peaks.All(double.IsNegativeInfinity), "Tail contains audio"); silentFrames++; } },
            liveMix: StereoMixSettings.Supports(stream) ? () => live : null,
            mixedLevels: (position, frame) =>
            {
                if (position == tail.PlaylistStartTicks)
                { Check(frame.Input.All(double.IsNegativeInfinity) && frame.Output.All(double.IsNegativeInfinity), "Mixer tail is not silent"); silentFrames++; }
                else if (frame.Input.Any(double.IsFinite)) nonSilentFrames++;
            });
        Check(starts.Count == 2 && silentFrames > 20, "Playback did not traverse the tail");
        if (highRes) Check(nonSilentFrames > 0, "Synthetic musical section is silent");
        // Seeking straight into the tail uses the same zero PCM path.
        await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc, title, stream,
            tail.PlaylistStartTicks + 22050, title.DurationTicks, CancellationToken.None);
        var excerpt = new TrackRow { Number = 1, Title = "Tail boundary", StartTicks = start, EndTicks = title.DurationTicks, IsChapter = false };
        foreach (var quality in highRes ? new[] { OutputQuality.Cd, OutputQuality.HighResolution } : [OutputQuality.Cd])
        {
            var mix = highRes ? new ChannelMixExport(stream, StereoPreviewMixer.StandardChannels(stream), "Standard") : null;
            var folder = await new ConversionService(tools, runner, probe, log).ConvertAsync(disc, title, stream,
                [excerpt], quality, Path.Combine(output, quality.ToString()), new Progress<ConversionProgress>(), CancellationToken.None, channelMix: mix);
            var file = Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Single();
            var saved = await probe.ProbeFileAsync(file, CancellationToken.None);
            var rate = quality == OutputQuality.Cd ? 44100 : stream.SampleRate;
            Check(saved.SampleCount == ConversionService.ToSample(title.DurationTicks, rate) - ConversionService.ToSample(start, rate),
                "Silence changed exported sample count");
            var raw = Path.Combine(output, quality + "-tail.pcm");
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", file, "-af", "atrim=start=1.05",
                "-c:a", "pcm_s16le", "-f", "s16le", "-y", raw], CancellationToken.None);
            var bytes = File.ReadAllBytes(raw);
            Check(bytes.Length > 0 && Enumerable.Range(0, bytes.Length / 2).All(i =>
                Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2))) <= 1), "Exported tail is not silent");
            Console.WriteLine($"{quality}: {saved.SampleCount} samples at {saved.SampleRate} Hz, {saved.BitDepth} bit; tail silence checked.");
        }
    }
}
