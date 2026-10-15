using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

public sealed record AudioFileSource(string Path, long Length, long WriteTicks, long? SampleCount, int SampleRate);

/// <summary>Local audio files use the same bounded clip, PCM playback and export paths as disc tracks.</summary>
public sealed class AudioFileReader(FfprobeService probe, AppLog log)
{
    public const string Extensions = "*.flac;*.wav;*.wave;*.w64;*.aif;*.aiff;*.aifc;*.wv;*.ape;*.m4a;*.m4b;*.mp4;*.mka;*.caf;*.ogg;*.oga;*.opus;*.aac;*.mp3;*.wma";
    public static bool CanOpen(string path) => File.Exists(path) && Extensions.Split(';')
        .Any(extension => Path.GetExtension(path).Equals(extension[1..], StringComparison.OrdinalIgnoreCase));
    public static bool IsUsable(AudioStreamInfo stream) => stream.Channels is 1 or 2 || StereoMixSettings.Supports(stream);

    public async Task<DiscAnalysis> AnalyzeAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (paths.Count == 0) throw new InvalidDataException("音声ファイルを選択してください。");
        var files = new List<Input>();
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!CanOpen(path)) throw new InvalidDataException(LanguageService.T("対応する音声ファイルが見つかりません。") + " " + path);
            var info = new FileInfo(path);
            var data = await probe.ProbeAudioFileAsync(path, token);
            if (data.Analysis.DetectedAudioCount == 0) throw new InvalidDataException(LanguageService.T("音声ストリームがありません。") + " " + path);
            files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks, data));
        }
        var playlists = new List<PlaylistInfo>();
        foreach (var group in files.GroupBy(GroupKey))
        {
            var items = group.OrderBy(f => TrackNumber(Tag(f.Data.Analysis.Tags, "disc")))
                .ThenBy(f => TrackNumber(Tag(f.Data.Analysis.Tags, "track"))).ToArray();
            var first = items[0];
            var tags = first.Data.Analysis.Tags;
            var album = Tag(tags, "album") ?? (items.Length == 1 ? Path.GetFileNameWithoutExtension(first.Path)
                : new DirectoryInfo(Path.GetDirectoryName(first.Path)!).Name);
            long position = 0;
            var starts = new List<long>(); var chapters = new Dictionary<int, ChapterMetadata>(); var clips = new List<ClipInfo>();
            foreach (var item in items)
            {
                var local = item.Data.Analysis.Chapters.Values.OrderBy(c => c.StartTicks).DistinctBy(c => c.StartTicks).ToList();
                if (local.Count == 0 || local[0].StartTicks != 0)
                    local.Insert(0, new(Tag(item.Data.Analysis.Tags, "title") ?? Path.GetFileNameWithoutExtension(item.Path), Tag(item.Data.Analysis.Tags, "artist")) { StartTicks = 0 });
                foreach (var chapter in local)
                {
                    var start = checked(position + chapter.StartTicks!.Value);
                    starts.Add(start);
                    chapters[starts.Count] = new(chapter.Title ?? Tag(item.Data.Analysis.Tags, "title") ?? Path.GetFileNameWithoutExtension(item.Path),
                        chapter.Artist ?? Tag(item.Data.Analysis.Tags, "artist")) { StartTicks = start };
                }
                var stream = item.Data.Analysis.Streams.FirstOrDefault();
                clips.Add(new(clips.Count.ToString("00000"), 0, item.Data.DurationTicks, position, File:
                    new(item.Path, item.Length, item.WriteTicks, stream?.SampleCount, stream?.SampleRate ?? 0)));
                position = checked(position + item.Data.DurationTicks);
            }
            playlists.Add(new()
            {
                Id = playlists.Count + 1, DisplayOrder = playlists.Count + 1, Format = DiscFormat.AudioFile,
                TitleName = album, DurationTicks = position, Clips = clips, ChapterStarts = starts,
                ChapterTitles = chapters.Values.Select(c => c.Title).ToArray(), MergeShortTail = false,
                FileAnalysis = new(first.Data.Analysis.Streams, chapters, tags)
                { DetectedAudioCount = first.Data.Analysis.DetectedAudioCount, Diagnostics = items.Select(i => i.Path).ToArray() }
            });
        }
        var albums = playlists.Select(p => Tag(p.FileAnalysis!.Tags, "album") ?? p.TitleName).Distinct().ToArray();
        var initial = files[0].Data.Analysis.Tags;
        var identity = string.Join('\n', files.Select(f => $"{f.Path.ToUpperInvariant()}|{f.Length}|{f.WriteTicks}"));
        return new()
        {
            Root = files[0].Path, Format = DiscFormat.AudioFile,
            DiscKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("AudioFiles/v1\n" + identity))),
            AlbumTitle = albums.Length == 1 ? albums[0] : LanguageService.T("音声ファイル"),
            Artist = Tag(initial, "album_artist") ?? Tag(initial, "artist"), Date = Tag(initial, "date"),
            Genre = Tag(initial, "genre"), Publisher = Tag(initial, "publisher"),
            Description = Tag(initial, "description") ?? Tag(initial, "comment"),
            Playlists = playlists, Diagnostics = files.Select(f => f.Path).ToArray()
        };
    }

    public async Task<PlaylistProbeResult> InspectAsync(PlaylistInfo playlist, CancellationToken token)
    {
        var analysis = playlist.FileAnalysis ?? throw new InvalidDataException("音声ファイルの情報がありません。");
        foreach (var clip in playlist.Clips) RequireUnchanged(clip.File!);
        var verified = new List<AudioStreamInfo>(); var notes = new List<string>(analysis.Diagnostics);
        foreach (var stream in analysis.Streams.Where(IsUsable))
        {
            try
            {
                await probe.VerifyAudioFileAsync(playlist.Clips[0].File!.Path, stream, token);
                verified.Add(stream);
            }
            catch (Exception ex) when (FfprobeService.IsRecoverable(ex)) { notes.Add(ex.Message); log.Write("AUDIO FILE: " + ex.Message); }
        }
        if (analysis.Streams.Any(s => !IsUsable(s))) notes.Add(LanguageService.T("チャンネル配置が不明または未対応の音声は自動ミックスしません。"));
        return analysis with
        {
            Streams = verified.Count > 0 ? verified : analysis.Streams,
            Availability = verified.Count > 0 ? AudioAvailability.Ready : analysis.Streams.Any(IsUsable) ? AudioAvailability.Failed : AudioAvailability.Unsupported,
            Diagnostics = notes
        };
    }

    public static void RequireUnchanged(AudioFileSource source)
    {
        var info = new FileInfo(source.Path);
        if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc.Ticks != source.WriteTicks)
            throw new IOException(LanguageService.T("音声ファイルが変更または移動されています。開き直してください。") + " " + source.Path);
    }
    private static string GroupKey(Input file)
    {
        var data = file.Data.Analysis;
        if (data.Streams.Count != 1 || data.Chapters.Count > 0) return "file:" + file.Path.ToUpperInvariant();
        var stream = data.Streams[0];
        return string.Join('\u001f', Path.GetDirectoryName(file.Path)!.ToUpperInvariant(), Tag(data.Tags, "album") ?? "",
            Tag(data.Tags, "album_artist") ?? "", stream.Codec, stream.Profile, stream.SampleRate.ToString(),
            stream.BitDepth?.ToString() ?? "", stream.UsesDecodedPcmDepth.ToString(), stream.Channels.ToString(), stream.ChannelLayout);
    }
    private static int TrackNumber(string? text) => int.TryParse(text?.Split('/')[0], out var value) ? value : int.MaxValue;
    private static string? Tag(IReadOnlyDictionary<string, string> tags, string name) => tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private sealed record Input(string Path, long Length, long WriteTicks, AudioFileProbe Data);
}
