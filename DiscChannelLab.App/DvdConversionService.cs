using System.Globalization;

namespace Disc2Flac;

public sealed class DvdConversionService(ToolPaths paths, ProcessRunner runner, FfprobeService probe, AppLog log)
{
    public async Task<string> ConvertAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream,
        IReadOnlyList<TrackRow> selected, OutputQuality quality, string outputRoot,
        IProgress<ConversionProgress> progress, CancellationToken token, bool groupByChapter,
        bool saveStereoDownmix, StereoMixSettings mix, bool saveIndividualChannels, ChannelMixExport? channelMix = null)
    {
        if (channelMix is not null)
        {
            channelMix.Validate(stream);
            if (saveIndividualChannels) throw new InvalidOperationException("2ch保存とチャンネル別保存は同時に選べません。");
            saveStereoDownmix = true;
        }
        if (selected.Count == 0) throw new InvalidOperationException("保存する曲を選択してください。");
        if (quality == OutputQuality.Cd && !stream.CanMakeCd ||
            quality == OutputQuality.HighResolution && !stream.CanMakeHighResolution)
            throw new InvalidOperationException("選択した音質で保存できない音声です。");
        var albumFolder = Path.Combine(outputRoot, ConversionService.SafeName(disc.AlbumTitle));
        var titleFolder = Path.Combine(albumFolder, ConversionService.SafeName(playlist.TitleName));
        Directory.CreateDirectory(albumFolder);
        var stage = Path.Combine(albumFolder, $".BDDVD2Flac-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var produced = new List<(string Staged, string Final)>();
        var sampleRate = quality == OutputQuality.Cd ? 44100 : stream.SampleRate;
        var bitDepth = quality == OutputQuality.Cd ? 16 : stream.BitDepth;
        var channels = saveIndividualChannels ? StereoMixSettings.ChannelNames(stream).Cast<string?>().ToArray() : [null];
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var track = selected[index];
                if (track.StartTicks < 0 || track.EndTicks <= track.StartTicks || track.EndTicks > playlist.DurationTicks)
                    throw new InvalidDataException($"{track.Title} の曲境界が不正です。");
                var chapterRange = ConversionService.GetChapterRange(playlist, track);
                var chapterTitle = chapterRange.Start == chapterRange.End ? playlist.ChapterTitle(chapterRange.Start) : null;
                var title = string.IsNullOrWhiteSpace(track.Title) ? $"Track {track.Number:00}" : track.Title.Trim();
                var sampleCount = ConversionService.ToSample(track.EndTicks, sampleRate) -
                                  ConversionService.ToSample(track.StartTicks, sampleRate);
                if (sampleCount <= 0) throw new InvalidDataException($"{title} の音声区間が短すぎます。");
                for (var channelIndex = 0; channelIndex < channels.Length; channelIndex++)
                {
                var channel = channels[channelIndex];
                var staged = Path.Combine(stage, $"{index + 1:000}-{channelIndex + 1:00}.flac");
                var arguments = new List<string> { "-hide_banner", "-v", "error" };
                long firstChapterTicks;
                if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
                {
                    var range = DvdAudioSectors.Range(audio, track.StartTicks, track.EndTicks);
                    firstChapterTicks = track.StartTicks - range.SkipTicks;
                    arguments.AddRange(["-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000",
                                        "-i", "pipe:0"]);
                }
                else if (playlist.Format == DiscFormat.DvdVideo)
                {
                    firstChapterTicks = playlist.ChapterStarts[chapterRange.Start - 1];
                    arguments.AddRange(["-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(),
                                        "-chapter_start", chapterRange.Start.ToString(),
                                        "-chapter_end", chapterRange.End.ToString(), "-i", disc.Root]);
                }
                else throw new InvalidDataException("DVD のタイトル情報がありません。");

                var skipSamples = ConversionService.ToSample(track.StartTicks, sampleRate) -
                                  ConversionService.ToSample(firstChapterTicks, sampleRate);
                var filter = quality == OutputQuality.Cd
                    ? $"aresample=44100:osf=s16:dither_method=triangular,atrim=start_sample={skipSamples}:end_sample={skipSamples + sampleCount},asetpts=PTS-STARTPTS"
                    : $"atrim=start_sample={skipSamples}:end_sample={skipSamples + sampleCount},asetpts=PTS-STARTPTS";
                if (channel is not null) filter = StereoMixSettings.SoloFilter(stream, channel, stereo: false) + "," + filter;
                else if (saveStereoDownmix) filter = (channelMix?.Filter(stream) ?? mix.PanFilter(stream)) + "," + filter;
                arguments.AddRange(["-map", $"0:{stream.Index}", "-vn", "-sn", "-dn", "-af", filter,
                    "-c:a", "flac", "-sample_fmt", quality == OutputQuality.Cd ? "s16" : "s32",
                    "-metadata", $"title={title}", "-metadata", $"track={track.Number}",
                    "-metadata", $"album={playlist.TitleName}"]);
                arguments.AddRange(["-metadata", $"SOURCE_FORMAT={(playlist.Format == DiscFormat.DvdAudio ? "DVD-Audio" : "DVD-Video")}",
                    "-metadata", $"DVD_TITLE_NUMBER={(playlist.Format == DiscFormat.DvdAudio ? playlist.DvdAudio!.TitleNumber : playlist.DvdVideoTitle)}"]);
                if (playlist.Format == DiscFormat.DvdAudio)
                    arguments.AddRange(["-metadata", $"DVD_TITLE_SET={playlist.DvdAudio!.TitleSet}"]);
                ConversionService.AddDiscTags(arguments, disc, playlist, track, chapterRange, chapterTitle ?? title);
                if (channelMix is not null) channelMix.AddTags(arguments);
                else if (saveStereoDownmix) ConversionService.AddDownmixTags(arguments, mix);
                if (channel is not null) ConversionService.AddChannelTags(arguments, stream, channel);
                arguments.AddRange(["-y", staged]);
                progress.Report(new ConversionProgress((index * channels.Length + channelIndex) / (double)(selected.Count * channels.Length),
                    $"{index + 1} / {selected.Count} 曲 · {channel ?? "全チャンネル"} を保存しています"));

                if (playlist.Format == DiscFormat.DvdAudio)
                {
                    var audioTitle = playlist.DvdAudio!;
                    var range = DvdAudioSectors.Range(audioTitle, track.StartTicks, track.EndTicks);
                    await runner.RunWithInputAsync(paths.Ffmpeg, arguments,
                        (input, ct) => DvdAudioSectors.CopyAsync(disc.Root, audioTitle.TitleSet,
                            range.FirstSector, range.LastSector, input, ct), token);
                }
                else await runner.RunAsync(paths.Ffmpeg, arguments.Prepend("-nostdin"), token);

                var actual = await probe.ProbeFileAsync(staged, token);
                ConversionService.ValidateFormat(actual, sampleRate, bitDepth, stream, saveStereoDownmix, channel is not null);
                if (actual.SampleCount is null or <= 0 ||
                    playlist.Format == DiscFormat.DvdAudio && actual.SampleCount != sampleCount)
                    throw new InvalidDataException($"{title} の音声を最後まで正確に読み取れませんでした。");

                var chapterFolder = chapterRange.Start == chapterRange.End
                    ? $"Chapter {chapterRange.Start:00}" : $"Chapter {chapterRange.Start:00}-{chapterRange.End:00}";
                if (chapterTitle is not null) chapterFolder += $" - {ConversionService.SafeName(chapterTitle, 80)}";
                var folder = groupByChapter ? Path.Combine(titleFolder, chapterFolder) : titleFolder;
                if (channel is not null)
                    folder = Path.Combine(folder, $"{track.Number:00} {ConversionService.SafeName(title, 80)} [Multichannel]");
                var channelSuffix = channelMix is not null ? channelMix.FileSuffix : saveStereoDownmix ? " [Stereo mix]" : stream.Channels == 2 ? "" : stream.Channels == 1 ? " [Mono]"
                    : $" [{ConversionService.SafeName(stream.ChannelLayout)}]";
                var name = channel is null ? $"{track.Number:00} {ConversionService.SafeName(title)}{channelSuffix}.flac"
                    : $"{ConversionService.SafeName(title)} - {channel}.flac";
                produced.Add((staged, ConversionService.UniquePath(Path.Combine(folder, name),
                    produced.Select(item => item.Final))));
                progress.Report(new ConversionProgress((index * channels.Length + channelIndex + 1d) / (selected.Count * channels.Length),
                    $"{index + 1} / {selected.Count} 曲 · {channel ?? "全チャンネル"} を保存しました"));
                }
            }
            foreach (var item in produced)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Final)!);
                File.Move(item.Staged, item.Final);
            }
            log.Write($"DVD COMPLETE {produced.Count} files -> {titleFolder}");
            return titleFolder;
        }
        finally
        {
            for (var attempt = 0; attempt < 10 && Directory.Exists(stage); attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(stage)) File.Delete(file);
                    Directory.Delete(stage);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 9) log.Write($"DVD CLEANUP WARNING {stage}: {ex.Message}");
                    else await Task.Delay(100);
                }
            }
        }
    }
}
