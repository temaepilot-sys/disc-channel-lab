using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Disc2Flac;

public sealed record SpatialInput(string PcmPath, int StoredChannels, SpatialScene Scene, long FirstSample,
    long Frames, string SourceName, string PcmHash, long PcmOffsetFrames = 0)
{
    public AdmWave? Wave { get; init; }
    public ProgressiveAtmosSource? Progressive => Wave?.Progressive;
    public AtmosPresentation Presentation { get; init; } = AtmosPresentation.Objects;
    public static SpatialInput FromAdm(AdmWave wave, long first, long frames)
    {
        if (wave.Progressive is { } live) return live.Input(first, frames, AtmosPresentation.Objects);
        if (first < 0 || frames <= 0 || first > wave.Scene.DurationSamples - frames) throw new InvalidDataException("ADM interval outside PCM.");
        return new(wave.Path, wave.Channels, wave.Scene, first, frames, wave.Path + " · " + wave.ProgrammeId, "preview; hash is collected during offline rendering", first) { Wave = wave };
    }
}

public sealed partial class MainViewModel
{
    public SpatialRenderSnapshot SpatialSnapshot(HrtfEntry hrtf, HeadPose pose, SpatialRoomOptions room)
    {
        var options = SofaLibrary.Apply(hrtf, HeadphoneSession.Current.Options) with { Room = room };
        if (IsAtmosStreamingEnabled) options = ProgressiveAtmosSource.Options(options, SelectedAtmosPresentation);
        return CurrentAdm is null ? new(options, pose, PreviewState(CurrentMix, _activePreviewChannel), VolumeCurve.Gain(Volume, PerceivedVolume), hrtf.Hash)
            : new(options, pose, new(StereoMixSettings.Default, null), VolumeCurve.Gain(Volume, PerceivedVolume), hrtf.Hash) { OutputMix = PreviewState(CurrentMix, _activePreviewChannel) };
    }
    public async Task<SpatialInput> PrepareSpatialInputAsync(double offset, double seconds, CancellationToken token)
    {
        if (_disc is null || _loadedPlaylist is null || SelectedStream is null || SelectedTrack is null || !CanPlay && !IsPlaying)
            throw new InvalidOperationException("Select a verified audio track in the main window first.");
        return await PrepareSpatialTrackAsync(SelectedTrack, offset, seconds, token);
    }
    async Task<SpatialInput> PrepareSpatialTrackAsync(TrackRow track, double offset, double seconds, CancellationToken token)
    {
        var disc = _disc!; var playlist = _loadedPlaylist!; var stream = IsAtmosStreamingEnabled ? _navigation.ProgressiveAtmos!.Stream : AudioFileReader.StreamForTrack(playlist, track, SelectedStream!);
        long first = track.StartTicks + (long)Math.Round(Math.Max(0, offset) * 45000), last = Math.Min(track.EndTicks, first + (long)Math.Round(seconds * 45000));
        if (last <= first) throw new InvalidDataException("Empty audio interval.");
        var segment = PlaylistSegments.ForRange(playlist, first, last).First();
        if (IsAtmosStreamingEnabled && _navigation.ProgressiveAtmos is { } live)
        {
            long from = ConversionService.ToSample(first, 48000), count = ConversionService.ToSample(last - first, 48000);
            await live.EnsureAsync(from, Math.Min(live.TotalFrames, from + Math.Min(count, ProgressiveAtmosSource.StartupFrames)), SelectedAtmosPresentation, token);
            return live.Input(from, count, SelectedAtmosPresentation);
        }
        if (segment.Clip.File is { } sourceFile) AudioFileReader.RequireUnchanged(sourceFile);
        if (segment.Clip.File?.Adm is { } adm && last <= segment.Clip.PlaylistStartTicks + segment.Clip.OutTicks - segment.Clip.InTicks)
            return SpatialInput.FromAdm(adm, ConversionService.ToSample(PlaylistSegments.SourceTicks(segment), 48000), ConversionService.ToSample(last - first, 48000));
        string root = Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "spatial"), path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".f32");
        Directory.CreateDirectory(root);
        try
        {
            await using (var file = File.Create(path)) await _navigation.WriteSpatialPcmAsync(disc, playlist, stream, first, last, file, token);
            int channels = stream.Channels; long frames = new FileInfo(path).Length / (channels * 4);
            if (frames <= 0 || new FileInfo(path).Length % (channels * 4) != 0) throw new InvalidDataException("Decoded PCM is empty or incomplete.");
            string hash; await using (var file = File.OpenRead(path)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
            return new(path, channels, SpatialScene.ChannelBed(HeadphoneGeometry.Channels(stream), frames, HeadphoneSession.Current.Options.FrontAngle), 0, frames,
                $"{disc.Root} · {track.Title} · {offset:0.000}s", hash);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
}

public sealed partial class AudioNavigationService
{
    // Decode only: all spatial DSP is shared with normal headphone playback.
    public async Task WriteSpatialPcmAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream,
        long start, long end, Stream destination, CancellationToken token)
    {
        int channels = HeadphoneGeometry.Channels(stream).Length;
        if (channels != stream.Channels) throw new NotSupportedException("Unknown source speaker layout.");
        if (playlist.Format is DiscFormat.DvdAudio or DiscFormat.DvdVideo)
        {
            List<string> args = []; Func<Stream, CancellationToken, Task>? feed = null; long skip; string? map = null;
            if (playlist.DvdAudio is { } audio)
            {
                var range = DvdAudioSectors.Range(audio, start, end); skip = range.SkipTicks;
                args.AddRange(["-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0"]);
                feed = (s, ct) => DvdAudioSectors.CopyAsync(disc.Root, audio.TitleSet, range.FirstSector, range.LastSector, s, ct);
            }
            else if (playlist.DvdVideoCells is { } cells)
            {
                var range = DvdVideoSectors.Range(playlist, start, end); skip = start - range.StartTicks; map = DvdVideoSectors.StreamMap(stream);
                args.AddRange(["-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000", "-i", "pipe:0"]);
                feed = (s, ct) => DvdVideoSectors.CopyAsync(disc.Root, cells, range.First, range.Last, s, ct);
            }
            else
            {
                int first = 1, last = 1;
                for (int i = 0; i < playlist.ChapterStarts.Count; i++) { if (playlist.ChapterStarts[i] <= start) first = i + 1; if (playlist.ChapterStarts[i] < end) last = i + 1; }
                skip = start - playlist.ChapterStarts[first - 1];
                args.AddRange(["-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(), "-chapter_start", first.ToString(), "-chapter_end", last.ToString(), "-i", DvdVideoReader.InputPath(disc.Root)]);
            }
            await Decode(args, stream, skip, end - start, feed, map); return;
        }
        foreach (var segment in PlaylistSegments.ForRange(playlist, start, end))
        {
            bool silent = stream.IsSilentTail(playlist, segment.Clip);
            var (path, seek, source) = silent ? (stream.SilenceInput, 0L, stream) : await GetSourceAsync(disc, stream, segment, token);
            long before = Math.Max(0, seek - 45000); var args = new List<string>();
            if (silent) args.AddRange(["-f", "lavfi"]);
            else if (before > 0 || segment.Clip.File is null) args.AddRange(["-ss", (before / 45000m).ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-i", path]);
            await Decode(args, source, seek - before, segment.EndTicks - segment.StartTicks, null, silent ? "0:0" : null);
        }
        async Task Decode(List<string> args, AudioStreamInfo source, long skip, long duration, Func<Stream, CancellationToken, Task>? feed, string? map)
        {
            long first = ConversionService.ToSample(skip, 48000), count = ConversionService.ToSample(duration, 48000);
            args.InsertRange(0, ["-hide_banner", "-nostdin", "-v", "error"]);
            args.AddRange(["-map", map ?? $"0:{source.Index}", "-vn", "-sn", "-dn", "-af", $"aresample=48000,atrim=start_sample={first}:end_sample={first + count},asetpts=PTS-STARTPTS", "-ac", channels.ToString(), "-ar", "48000", "-c:a", "pcm_f32le"]);
            if (channels > 2) args.AddRange(["-ch_layout", source.ChannelLayout]); args.AddRange(["-f", "f32le", "pipe:1"]);
            var info = new ProcessStartInfo(paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = feed is not null };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Cannot start decoder.");
            using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync(token);
            var feeding = feed is null ? Task.CompletedTask : Feed();
            try { await process.StandardOutput.BaseStream.CopyToAsync(destination, token); await feeding; await process.WaitForExitAsync(token); if (process.ExitCode != 0) throw new FfToolException(await errors); }
            finally { if (!process.HasExited) process.Kill(true); }
            async Task Feed() { try { await feed!(process.StandardInput.BaseStream, token); } finally { process.StandardInput.Close(); } }
        }
    }
}
