using System.Globalization;
using System.Text;

namespace Disc2Flac;

public sealed record ConversionProgress(double Fraction, string Message);

public sealed class ConversionService(ToolPaths paths, ProcessRunner runner, FfprobeService probe, AppLog log)
{
    public async Task<string> ConvertAsync(
        DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream, IReadOnlyList<TrackRow> selected,
        OutputQuality quality, string outputRoot, IProgress<ConversionProgress> progress, CancellationToken token,
        bool groupByChapter = false, bool saveStereoDownmix = false, StereoMixSettings? mix = null,
        bool saveIndividualChannels = false)
    {
        if (saveStereoDownmix && saveIndividualChannels)
            throw new InvalidOperationException("2ch保存とチャンネル別保存は同時に選べません。");
        if (saveIndividualChannels && !StereoMixSettings.Supports(stream))
            throw new InvalidOperationException("この音声のチャンネル配置は個別保存に対応していません。");
        if (saveStereoDownmix && !StereoMixSettings.Supports(stream))
            throw new InvalidOperationException("この音声のチャンネル配置は2ch変換に対応していません。");
        mix ??= StereoMixSettings.Default;
        if (playlist.Format != DiscFormat.BluRay)
            return await new DvdConversionService(paths, runner, probe, log).ConvertAsync(
                disc, playlist, stream, selected, quality, outputRoot, progress, token, groupByChapter,
                saveStereoDownmix, mix, saveIndividualChannels);
        if (selected.Count == 0) throw new InvalidOperationException("保存する曲またはチャプターを選択してください。");
        if (quality == OutputQuality.Cd && !stream.CanMakeCd) throw new InvalidOperationException("この音声はCD音質への変換条件を満たしていません。");
        if (quality == OutputQuality.HighResolution && !stream.CanMakeHighResolution) throw new InvalidOperationException("この音声はハイレゾで保存できません。");
        if (!Directory.Exists(outputRoot)) Directory.CreateDirectory(outputRoot);
        var albumFolder = Path.Combine(outputRoot, SafeName(disc.AlbumTitle));
        var titleFolder = Path.Combine(albumFolder, SafeName(playlist.TitleName));
        Directory.CreateDirectory(albumFolder);
        var stage = Path.Combine(albumFolder, $".Disc2Flac-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var produced = new List<(string staged, string final)>();
        var expectedRate = quality == OutputQuality.Cd ? 44100 : stream.SampleRate;
        var expectedDepth = quality == OutputQuality.Cd ? 16 : stream.BitDepth;
        var channels = saveIndividualChannels ? StereoMixSettings.ChannelNames(stream).Cast<string?>().ToArray() : [null];
        try
        {
            var checkedClips = new Dictionary<string, AudioStreamInfo>(StringComparer.Ordinal);
            for (var i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var track = selected[i];
                if (track.StartTicks < 0 || track.EndTicks <= track.StartTicks || track.EndTicks > playlist.DurationTicks)
                    throw new InvalidDataException($"{track.Title} の曲境界を確定できません。");
                var chapterRange = GetChapterRange(playlist, track);
                var chapterTitle = chapterRange.Start == chapterRange.End
                    ? playlist.ChapterTitle(chapterRange.Start) : null;
                var flacAlbum = playlist.TitleName;
                var segments = PlaylistSegments.ForRange(playlist, track.StartTicks, track.EndTicks);
                if (segments.Count == 0) throw new InvalidDataException($"{track.Title} に対応する音声クリップがありません。");
                var title = string.IsNullOrWhiteSpace(track.Title) ? $"{(track.IsChapter ? "Chapter" : "Track")} {track.Number:00}" : track.Title.Trim();
                for (var channelIndex = 0; channelIndex < channels.Length; channelIndex++)
                {
                var channel = channels[channelIndex];
                var parts = new List<string>();
                for (var partIndex = 0; partIndex < segments.Count; partIndex++)
                {
                    var segment = segments[partIndex];
                    var sourcePath = Path.Combine(disc.Root, "BDMV", "STREAM", $"{segment.Clip.Id}.m2ts");
                    if (!File.Exists(sourcePath)) throw new FileNotFoundException($"音声クリップが見つかりません: {sourcePath}");
                    if (!checkedClips.TryGetValue(segment.Clip.Id, out var source))
                    {
                        source = (await probe.ProbeClipAsync(sourcePath, token)).FirstOrDefault(x => x.Index == stream.Index);
                        if (source is null || source.Codec != stream.Codec || source.Profile != stream.Profile ||
                            (!string.IsNullOrEmpty(stream.TransportId) && source.TransportId != stream.TransportId) ||
                            source.SampleRate != stream.SampleRate ||
                            source.BitDepth != stream.BitDepth || source.Channels != stream.Channels ||
                            (stream.Channels > 2 && !string.Equals(source.ChannelLayout, stream.ChannelLayout, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException($"クリップ {segment.Clip.Id} の音声形式が選択した音声と一致しません。");
                        if (source.StartTimeTicks is null)
                            throw new InvalidDataException($"クリップ {segment.Clip.Id} の音声開始時刻が不明です。");
                        checkedClips.Add(segment.Clip.Id, source);
                    }

                    var sourceStartTicks = PlaylistSegments.SourceTicks(segment);
                    var seekTicks = sourceStartTicks - source.StartTimeTicks!.Value;
                    if (seekTicks < 0) throw new InvalidDataException($"クリップ {segment.Clip.Id} の音声開始位置が不正です。");
                    var durationTicks = segment.EndTicks - segment.StartTicks;
                    var sampleCount = ToSample(segment.EndTicks, expectedRate) - ToSample(segment.StartTicks, expectedRate);
                    if (sampleCount <= 0) throw new InvalidDataException($"{title} の再生区間が短すぎます。");
                    var part = Path.Combine(stage, $"{i + 1:000}-{channelIndex + 1:00}-part-{partIndex + 1:00}.flac");
                    var filter = quality == OutputQuality.Cd
                        ? $"aresample=44100:osf=s16:dither_method=triangular,atrim=end_sample={sampleCount},asetpts=PTS-STARTPTS"
                        : $"atrim=end_sample={sampleCount},asetpts=PTS-STARTPTS";
                    if (channel is not null) filter = StereoMixSettings.SoloFilter(stream, channel, stereo: false) + "," + filter;
                    else if (saveStereoDownmix) filter = mix.PanFilter(stream) + "," + filter;
                    var arguments = new List<string>
                    {
                        "-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1",
                        "-ss", Seconds(seekTicks), "-t", Seconds(durationTicks + 4500), "-i", sourcePath,
                        "-map", $"0:{stream.Index}", "-vn", "-sn", "-dn", "-af", filter,
                        "-c:a", "flac", "-sample_fmt", quality == OutputQuality.Cd ? "s16" : "s32",
                        "-metadata", $"title={title}", "-metadata", $"track={track.Number}",
                        "-metadata", $"album={flacAlbum}"
                    };
                    AddDiscTags(arguments, disc, playlist, track, chapterRange, chapterTitle ?? title);
                    if (saveStereoDownmix) AddDownmixTags(arguments, mix);
                    if (channel is not null) AddChannelTags(arguments, stream, channel);
                    arguments.AddRange(["-y", part]);
                    var completedParts = partIndex;
                    await runner.RunAsync(paths.Ffmpeg, arguments, token, line =>
                    {
                        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                        if (!long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds)) return;
                        var partFraction = Math.Clamp(microseconds / (durationTicks / 45000d * 1_000_000), 0, 1);
                        progress.Report(new ConversionProgress((i * channels.Length + channelIndex + (completedParts + partFraction) / segments.Count) / (selected.Count * channels.Length),
                            $"{i + 1} / {selected.Count} 曲 · {channel ?? "全チャンネル"} を保存しています"));
                    });
                    var partInfo = await probe.ProbeFileAsync(part, token);
                    ValidateFormat(partInfo, expectedRate, expectedDepth, stream, saveStereoDownmix, channel is not null);
                    if (partInfo.SampleCount != sampleCount)
                        throw new InvalidDataException($"{title} の音声区間を正確に読み取れませんでした。");
                    parts.Add(part);
                }

                var staged = Path.Combine(stage, $"{i + 1:000}-{channelIndex + 1:00}.flac");
                if (parts.Count == 1) File.Move(parts[0], staged);
                else
                {
                    var listPath = Path.Combine(stage, $"{i + 1:000}-{channelIndex + 1:00}.ffconcat");
                    File.WriteAllText(listPath, "ffconcat version 1.0\n" +
                        string.Join("", parts.Select(x => $"file '{Path.GetFileName(x)}'\n")), Encoding.ASCII);
                    var joinArguments = new List<string>
                    { "-hide_banner", "-nostdin", "-v", "error", "-f", "concat", "-safe", "0", "-i", listPath,
                         "-map", "0:a:0", "-c:a", "flac", "-sample_fmt", quality == OutputQuality.Cd ? "s16" : "s32",
                         "-metadata", $"title={title}", "-metadata", $"track={track.Number}",
                         "-metadata", $"album={flacAlbum}" };
                    AddDiscTags(joinArguments, disc, playlist, track, chapterRange, chapterTitle ?? title);
                    if (saveStereoDownmix) AddDownmixTags(joinArguments, mix);
                    if (channel is not null) AddChannelTags(joinArguments, stream, channel);
                    joinArguments.AddRange(["-y", staged]);
                    await runner.RunAsync(paths.Ffmpeg, joinArguments, token);
                    var joined = await probe.ProbeFileAsync(staged, token);
                    ValidateFormat(joined, expectedRate, expectedDepth, stream, saveStereoDownmix, channel is not null);
                    if (joined.SampleCount != ToSample(track.EndTicks, expectedRate) - ToSample(track.StartTicks, expectedRate))
                        throw new InvalidDataException($"{title} の結合後のサンプル数が一致しません。");
                }
                var channelSuffix = saveStereoDownmix ? " [Stereo mix]" : stream.Channels == 2 ? "" : stream.Channels == 1 ? " [Mono]" : $" [{SafeName(stream.ChannelLayout)}]";
                var fileName = channel is null ? $"{track.Number:00} {SafeName(title)}{channelSuffix}.flac"
                    : $"{SafeName(title)} - {channel}.flac";
                var chapterFolder = chapterRange.Start == chapterRange.End
                    ? $"Chapter {chapterRange.Start:00}" : $"Chapter {chapterRange.Start:00}-{chapterRange.End:00}";
                if (chapterTitle is not null) chapterFolder += $" - {SafeName(chapterTitle, 80)}";
                var targetFolder = groupByChapter ? Path.Combine(titleFolder, chapterFolder) : titleFolder;
                if (channel is not null)
                    targetFolder = Path.Combine(targetFolder, $"{track.Number:00} {SafeName(title, 80)} [Multichannel]");
                produced.Add((staged, UniquePath(Path.Combine(targetFolder, fileName), produced.Select(x => x.final))));
                progress.Report(new ConversionProgress((i * channels.Length + channelIndex + 1d) / (selected.Count * channels.Length),
                    $"{i + 1} / {selected.Count} 曲 · {channel ?? "全チャンネル"} を保存しました"));
                }
            }

            foreach (var (staged, final) in produced)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(staged, final);
            }
            log.Write($"COMPLETE {produced.Count} files -> {titleFolder}");
            progress.Report(new ConversionProgress(1, $"{produced.Count} ファイルを保存しました"));
            return titleFolder;
        }
        finally
        {
            // A scanner can briefly hold a generated file after FFmpeg exits.
            for (var attempt = 0; attempt < 10 && Directory.Exists(stage); attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(stage)) File.Delete(file);
                    Directory.Delete(stage);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 9) log.Write($"CLEANUP WARNING {stage}: {ex.Message}");
                    else await Task.Delay(100);
                }
            }
        }
    }

    private static string Seconds(long ticks) => (ticks / 45000m).ToString("0.########", CultureInfo.InvariantCulture);

    internal static (int Start, int End) GetChapterRange(PlaylistInfo playlist, TrackRow track)
    {
        if (playlist.ChapterStarts.Count == 0) throw new InvalidDataException("チャプター情報がありません。");
        var start = 1;
        var end = 1;
        for (var i = 0; i < playlist.ChapterStarts.Count; i++)
        {
            if (playlist.ChapterStarts[i] <= track.StartTicks) start = i + 1;
            if (playlist.ChapterStarts[i] < track.EndTicks) end = i + 1;
        }
        return (start, end);
    }

    internal static void AddDiscTags(List<string> arguments, DiscAnalysis disc, PlaylistInfo playlist, TrackRow track,
        (int Start, int End) chapterRange, string? chapterTitle)
    {
        Add("CHAPTERNUMBER", chapterRange.Start.ToString(CultureInfo.InvariantCulture));
        if (chapterRange.End != chapterRange.Start)
            Add("CHAPTEREND", chapterRange.End.ToString(CultureInfo.InvariantCulture));
        Add("CHAPTERTITLE", chapterTitle);
        Add("DISC_TITLE", disc.AlbumTitle);
        if (playlist.Format == DiscFormat.BluRay)
        {
            Add("BD_PLAYLIST", playlist.Id.ToString("00000", CultureInfo.InvariantCulture));
            Add("BD_TITLE_NUMBER", playlist.DisplayOrder.ToString(CultureInfo.InvariantCulture));
        }
        Add("artist", track.Artist ?? disc.Artist);
        Add("album_artist", disc.Artist);
        Add("date", disc.Date);
        Add("genre", disc.Genre);
        Add("publisher", disc.Publisher);
        Add("description", disc.Description);
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) arguments.AddRange(["-metadata", $"{name}={value.Trim()}"]);
        }
    }

    internal static void AddDownmixTags(List<string> arguments, StereoMixSettings mix)
    {
        arguments.AddRange(["-metadata", "DOWNMIX=multichannel to stereo",
            "-metadata", $"DOWNMIX_CENTER={mix.Center.ToString("0.######", CultureInfo.InvariantCulture)}",
            "-metadata", $"DOWNMIX_SURROUND={mix.Surround.ToString("0.######", CultureInfo.InvariantCulture)}",
            "-metadata", $"DOWNMIX_LFE={mix.Lfe.ToString("0.######", CultureInfo.InvariantCulture)}"]);
    }

    internal static void AddChannelTags(List<string> arguments, AudioStreamInfo source, string channel)
    {
        arguments.AddRange(["-metadata", $"SOURCE_CHANNEL={channel}",
            "-metadata", $"SOURCE_CHANNEL_LAYOUT={source.ChannelLayout}"]);
    }

    internal static void ValidateFormat(AudioStreamInfo actual, int expectedRate, int? expectedDepth,
        AudioStreamInfo source, bool stereoDownmix = false, bool individualChannel = false)
    {
        if (stereoDownmix && individualChannel)
            throw new ArgumentException("出力チャンネルの指定が重複しています。");
        if (actual.SampleRate != expectedRate || actual.BitDepth != expectedDepth ||
            actual.Channels != (individualChannel ? 1 : stereoDownmix ? 2 : source.Channels))
            throw new InvalidDataException("FLAC の周波数・ビット深度・チャンネル数が指定と一致しません。");
        if (individualChannel && !string.Equals(actual.ChannelLayout, "mono", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FLAC のチャンネル配置がモノラルではありません。");
        if (stereoDownmix && !string.Equals(actual.ChannelLayout, "stereo", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FLAC のチャンネル配置がステレオではありません。");
        if (!stereoDownmix && !individualChannel && source.Channels > 2 &&
            !string.Equals(actual.ChannelLayout, source.ChannelLayout, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FLAC のチャンネル配置が入力と一致しません。");
    }

    internal static long ToSample(long ticks, int sampleRate) => (long)Math.Round(ticks * (double)sampleRate / 45000d, MidpointRounding.AwayFromZero);

    internal static string UniquePath(string path, IEnumerable<string> reserved)
    {
        var used = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path) && !used.Contains(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var number = 2; number < 10000; number++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({number}).flac");
            if (!File.Exists(candidate) && !used.Contains(candidate)) return candidate;
        }
        throw new IOException("保存先のファイル名を決められません。");
    }

    internal static string SafeName(string raw, int maxLength = 120)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(raw.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "Unknown Album";
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd(' ', '.');
        var reserved = new HashSet<string>(["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"], StringComparer.OrdinalIgnoreCase);
        if (reserved.Contains(name)) name += "_";
        return name;
    }
}
