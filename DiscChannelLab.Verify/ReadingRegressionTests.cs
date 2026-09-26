using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class ReadingRegressionTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }

    public static async Task RunAsync()
    {
        var normal = new PlaylistInfo { Id = 1, Clips = [new("00001", 0, 450000, 0)],
            ChapterStarts = [0, 225000, 427500], DurationTicks = 450000 };
        var disc = new DiscAnalysis { Root = "unused", DiscKey = new string('A', 64), AlbumTitle = "Test", Playlists = [normal] };
        var corrected = DiscService.BuildTracks(disc, normal);
        Check(normal.ChapterStarts.Count == 3 && corrected.Count == 2 && corrected[^1].EndTicks == normal.DurationTicks,
            "Original markers or the final audio tail were lost.");
        corrected[1].Title = "Edited title";
        normal.MergeShortTail = false;
        var original = ChapterCorrection.Apply(corrected, normal);
        Check(original.Count == 3 && original[1].Title == "Edited title" && original[^1].StartTicks == 427500,
            "Restoring original markers lost edits or used the wrong boundary.");
        normal.MergeShortTail = true;
        var merged = ChapterCorrection.Apply(original, normal);
        Check(merged.Count == 2 && merged.Sum(x => x.EndTicks - x.StartTicks) == normal.DurationTicks,
            "Chapter correction changed total duration.");
        var noAudio = new PlaylistProbeResult([], new Dictionary<int, ChapterMetadata>(), new Dictionary<string, string>())
            { Availability = AudioAvailability.Unsupported };
        Check(DiscService.BuildTracks(disc, normal, noAudio).Count == 2, "Unsupported audio hid chapter rows.");
        var loop = new PlaylistInfo { Id = 2, Clips = Enumerable.Range(0, 600)
            .Select(i => new ClipInfo((i % 6).ToString("00000"), 0, 120 * 45000L, i * 120 * 45000L)).ToArray(),
            ChapterStarts = [0, 450000], DurationTicks = 600 * 120 * 45000L };
        Check(PlaylistRanking.Score(normal) > PlaylistRanking.Score(loop), "A long menu loop outranked a normal title.");
        AudioStreamInfo Stream(int index, string id) => new() { Index = index, TransportId = id, Codec = "pcm_bluray",
            SampleRate = 48000, BitDepth = 24, Channels = 2, ChannelLayout = "stereo" };
        var selected = Stream(1, "0x1100");
        var remapped = AudioStreamMatcher.Resolve(selected, [Stream(1, "0x1101"), Stream(3, "4352")]);
        Check(remapped.Index == 3, "Audio followed the file index rather than the transport ID.");
        try { AudioStreamMatcher.Resolve(Stream(1, ""), [Stream(1, ""), Stream(2, "")]); throw new Exception("Ambiguous audio was accepted."); }
        catch (InvalidDataException) { }
        try
        {
            await FfprobeService.WithTimeout(async ct => { await Task.Delay(10000, ct); return true; }, CancellationToken.None, 1);
            throw new Exception("Timeout was ignored.");
        }
        catch (TimeoutException) { }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await FfprobeService.WithTimeout(ct => Task.FromCanceled<bool>(ct), cancelled.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }

        var root = Path.Combine(Path.GetTempPath(), "DiscChannelLab-reading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "PLAYLIST"));
        Directory.CreateDirectory(Path.Combine(root, "BDMV", "STREAM"));
        var log = new AppLog(); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner);
        var first = Path.Combine(root, "BDMV", "STREAM", "00001.m2ts");
        var second = Path.Combine(root, "BDMV", "STREAM", "00002.m2ts");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", "2", "-c:a", "pcm_bluray", "-streamid", "0:4352", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", first], CancellationToken.None);
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=32x32:r=24",
            "-f", "lavfi", "-i", "anullsrc=r=96000:cl=stereo", "-t", "2", "-map", "0:v", "-map", "1:a",
            "-c:v", "mpeg2video", "-c:a", "pcm_bluray", "-streamid", "0:4113", "-streamid", "1:4352",
            "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", second], CancellationToken.None);
        var a = (await probe.ProbeClipAsync(first, CancellationToken.None)).Single();
        var b = (await probe.ProbeClipAsync(second, CancellationToken.None)).Single();
        Check(a.Index != b.Index, "Fixture did not change audio indexes.");
        WritePlaylist(Path.Combine(root, "BDMV", "PLAYLIST", "00001.mpls"),
            [new("00001", a.StartTimeTicks!.Value, a.StartTimeTicks.Value + 90000, 0),
             new("00002", b.StartTimeTicks!.Value, b.StartTimeTicks.Value + 90000, 90000)]);
        File.WriteAllText(Path.Combine(root, "BDMV", "PLAYLIST", "00002.mpls"), "broken playlist");
        var service = new DiscService(probe, log);
        var synthetic = service.Analyze(root);
        Check(synthetic.Playlists.Count == 2, "Malformed title was silently discarded.");
        var title = synthetic.Playlists.Single(x => x.Id == 1);
        var inspection = await service.InspectPlaylistAsync(synthetic, title, CancellationToken.None);
        Check(inspection.Streams.Count > 0 && inspection.Availability == AudioAvailability.Ready,
            $"Direct clip fallback failed: {title.AnalysisDetails}");
        Check(inspection.Diagnostics.Any(x => x.StartsWith("playlist:")), "Fallback diagnostics were lost.");
        var broken = synthetic.Playlists.Single(x => x.Id == 2);
        await service.InspectPlaylistAsync(synthetic, broken, CancellationToken.None);
        Check(broken.Availability == AudioAvailability.Failed, "A failed inspection was reported as no audio.");
        var videoOnly = Path.Combine(root, "BDMV", "STREAM", "00003.m2ts");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=s=32x32:r=24", "-t", "2",
            "-c:v", "mpeg2video", "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-y", videoOnly], CancellationToken.None);
        var silentTitle = new PlaylistInfo { Id = 99, Clips = [new("00003", 0, 90000, 0)], ChapterStarts = [0], DurationTicks = 90000 };
        var silentInfo = await service.InspectPlaylistAsync(synthetic, silentTitle, CancellationToken.None);
        Check(silentInfo.Availability == AudioAvailability.NoAudio && DiscService.BuildTracks(synthetic, silentTitle).Count == 1,
            "No-audio detection hid chapters or confused missing audio with a failed probe.");
        File.Copy(Path.Combine(root, "BDMV", "PLAYLIST", "00001.mpls"), Path.Combine(root, "BDMV", "PLAYLIST", "00002.mpls"), true);
        await service.InspectPlaylistAsync(synthetic, broken, CancellationToken.None, force: true);
        Check(broken.Availability == AudioAvailability.Ready && broken.ChapterStarts.Count > 0,
            "Retry did not recover corrected title structure.");
        var clipStream = inspection.Streams[0];
        var excerpt = new TrackRow { Number = 1, Title = "Cross clip", StartTicks = 45000, EndTicks = 135000, IsChapter = false };
        var folder = await new ConversionService(tools, runner, probe, log).ConvertAsync(synthetic, title, clipStream,
            [excerpt], OutputQuality.Cd, Path.Combine(root, "output"), new Progress<ConversionProgress>(), CancellationToken.None);
        var flac = Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Single();
        var savedAudio = await probe.ProbeFileAsync(flac, CancellationToken.None);
        Check(savedAudio.SampleCount == 88200 && savedAudio.Channels == 2, "Remapped clip export changed duration or channels.");
        var store = new TrackEditsStore(log, Path.Combine(root, "edits"));
        store.Save(disc, normal, merged);
        normal.MergeShortTail = false;
        store.Save(disc, normal, original); store.SaveMergeShortTail(disc, normal);
        Check(!store.LoadMergeShortTail(disc, normal) && store.Load(disc, normal)!.Count == 3,
            "Original chapter mode did not persist.");
        normal.MergeShortTail = true;
        Check(store.Load(disc, normal)![1].Title == "Edited title", "Corrected chapter edits were overwritten.");
        var report = Path.Combine(root, "diagnostics.json");
        await DiscDiagnostics.SaveAsync(synthetic, report, CancellationToken.None);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Check(json.RootElement.GetProperty("titles").GetArrayLength() == 2, "Diagnostic report lost the failed title.");
        Console.WriteLine("PASS: chapter preservation, reversible correction, edit persistence, menu ranking, ID remapping, ambiguity rejection, clip fallback, malformed title isolation, exact cross-clip FLAC samples, diagnostics.");
        Console.WriteLine($"Synthetic fixtures and report: {root}");
    }

    private static void WritePlaylist(string path, IReadOnlyList<ClipInfo> clips)
    {
        var bytes = new byte[50 + clips.Count * 24];
        Encoding.ASCII.GetBytes("MPLS0200").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 40);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(46), (ushort)clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            var offset = 50 + i * 24;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), 22);
            Encoding.ASCII.GetBytes(clips[i].Id).CopyTo(bytes, offset + 2);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 14), (uint)clips[i].InTicks);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 18), (uint)clips[i].OutTicks);
        }
        File.WriteAllBytes(path, bytes);
    }

    public static async Task RunDiscAsync(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "DiscChannelLab-disc-test-" + Guid.NewGuid().ToString("N"));
        var log = new AppLog();
        var model = new MainViewModel(new TrackEditsStore(log, Path.Combine(root, "edits")));
        try
        {
            await model.OpenSourceAsync(source);
            model.StopInspection();
            await model.InspectionTask;
            var title = model.SelectedPlaylist ?? throw new InvalidDataException("No title selected.");
            Check(model.Tracks.Count > 0 && model.CanPlay, $"Initial selection is not playable: {model.Status}");
            var correctedCount = model.Tracks.Count;
            if (title.HasShortTail)
            {
                model.Tracks[0].Title = "Retained edit";
                model.ToggleShortTail(false);
                Check(model.Tracks.Count == correctedCount + 1 && !model.MergeShortTail, "Original chapter mode did not restore the tail.");
                model.ToggleShortTail(true);
                Check(model.Tracks.Count == correctedCount && model.Tracks[0].Title == "Retained edit", "Correction lost edited track data.");
            }
            if (model.Streams.FirstOrDefault(x => x.HasPartialCoverage) is { } partial)
            {
                model.SelectedStream = partial;
                Check(!model.CanConvert, "A stream missing from selected tracks was allowed for export.");
                var available = model.Tracks.FirstOrDefault(x => partial.Covers(title, x));
                if (available is not null)
                {
                    model.SelectAll(false); available.IsSelected = true; model.SelectedTrack = available;
                    Check(model.CanConvert && model.CanPlay, "Audio in a later clip was not selectable.");
                }
            }
            var probe = new FfprobeService(new ToolPaths(), new ProcessRunner(log));
            var reader = new DiscService(probe, log);
            var disc = reader.Analyze(source);
            var main = disc.Playlists.Single(x => x.Id == title.Id);
            var info = await reader.InspectPlaylistAsync(disc, main, CancellationToken.None);
            var start = main.ChapterStarts[^1];
            var excerpt = new TrackRow { Number = 1, Title = "Tail excerpt", StartTicks = start,
                EndTicks = Math.Min(start + 90000, main.DurationTicks), IsChapter = false };
            var folder = await new ConversionService(new ToolPaths(), new ProcessRunner(log), probe, log)
                .ConvertAsync(disc, main, info.Streams[0], [excerpt], OutputQuality.Cd, Path.Combine(root, "output"),
                    new Progress<ConversionProgress>(), CancellationToken.None);
            var audio = await probe.ProbeFileAsync(Directory.EnumerateFiles(folder, "*.flac", SearchOption.AllDirectories).Single(), CancellationToken.None);
            Check(audio.SampleCount == ConversionService.ToSample(excerpt.EndTicks, 44100) - ConversionService.ToSample(start, 44100),
                "Final chapter export had incorrect sample length.");
            if (model.Playlists.FirstOrDefault(x => x.IsRepeatedShortClipLoop) is { } menu)
            {
                model.SelectedPlaylist = menu;
                await model.ChoosePlaylistAsync(menu);
                Check(model.Tracks.Count > 0 && model.Streams.Count == 0 && !model.CanPlay &&
                    menu.Availability is AudioAvailability.Unsupported or AudioAvailability.NoAudio,
                    "An unsupported menu hid the chapter list or was reported as playable.");
            }
            Console.WriteLine($"PASS: disc UI selection, chapter toggle/edit preservation, partial stream coverage, exact tail FLAC export, unsupported title chapter display. Output: {root}");
        }
        finally { model.Cancel(); model.DetachLanguage(); }
    }
}
