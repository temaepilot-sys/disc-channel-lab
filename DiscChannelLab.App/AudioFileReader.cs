using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

public sealed record AudioFileSource(string Path, long Length, long WriteTicks, long? SampleCount, int SampleRate)
{
    public IReadOnlyList<AudioStreamInfo> Streams { get; init; } = [];
    public AdmWave? Adm { get; init; }
}

/// <summary>Local audio files use the same bounded clip, PCM playback and export paths as disc tracks.</summary>
public sealed class AudioFileReader(FfprobeService probe, AppLog log)
{
    public const string Extensions = "*.flac;*.wav;*.wave;*.bwf;*.bw64;*.rf64;*.w64;*.aif;*.aiff;*.aifc;*.wv;*.ape;*.m4a;*.m4b;*.mp4;*.mka;*.mkv;*.m2ts;*.thd;*.truehd;*.mlp;*.ec3;*.eac3;*.caf;*.ogg;*.oga;*.opus;*.aac;*.mp3;*.wma";
    public static bool CanOpen(string path) => File.Exists(path) && Extensions.Split(';')
        .Any(extension => Path.GetExtension(path).Equals(extension[1..], StringComparison.OrdinalIgnoreCase));
    public static bool IsUsable(AudioStreamInfo stream) => stream.Channels is 1 or 2 || StereoMixSettings.Supports(stream);

    public async Task<DiscAnalysis> AnalyzeFolderAsync(string folder, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        var paths = await Task.Run(() => Directory.EnumerateFiles(folder).Where(path =>
        {
            token.ThrowIfCancellationRequested();
            return CanOpen(path);
        }).ToArray(), token);
        if (paths.Length == 0) throw new InvalidDataException(LanguageService.T("このフォルダーには対応する音声ファイルがありません。"));
        return await AnalyzeAsync(paths, token, folder);
    }

    public async Task<DiscAnalysis> AnalyzeAsync(IReadOnlyList<string> paths, CancellationToken token, string? folder = null)
    {
        if (paths.Count == 0) throw new InvalidDataException("音声ファイルを選択してください。");
        var files = new List<Input>();
        var skipped = new List<string>();
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!CanOpen(path)) throw new InvalidDataException(LanguageService.T("対応する音声ファイルが見つかりません。") + " " + path);
                var info = new FileInfo(path);
                AdmWave? adm = await Task.Run(() => AdmWave.HasMetadata(path) ? AdmWave.Open(path) : null, token);
                var data = adm is null ? await probe.ProbeAudioFileAsync(path, token) : AdmFileFormat.Probe(adm);
                if (data.Analysis.DetectedAudioCount == 0) throw new InvalidDataException(LanguageService.T("音声ストリームがありません。") + " " + path);
                if (folder is not null)
                {
                    var playable = data.Analysis.Streams.FirstOrDefault(IsUsable)
                        ?? throw new InvalidDataException(LanguageService.T("チャンネル配置が不明または未対応の音声は自動ミックスしません。"));
                    if (adm is null) await probe.VerifyAudioFileAsync(path, playable, token);
                }
                files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks, data, adm));
            }
            catch (Exception ex) when (folder is not null && FfprobeService.IsRecoverable(ex))
            {
                var note = $"{path}: {ex.Message}";
                skipped.Add(note); log.Write("AUDIO FOLDER SKIPPED " + note);
            }
        }
        if (files.Count == 0) throw new InvalidDataException(LanguageService.T("このフォルダーには再生できる音声ファイルがありません。"));
        var playlists = new List<PlaylistInfo>();
        foreach (var group in files.GroupBy(file => folder is null ? GroupKey(file) : folder))
        {
            var items = group.OrderBy(f => TrackNumber(Tag(f.Data.Analysis.Tags, "disc") ?? Tag(f.Data.Analysis.Tags, "discnumber"), 1))
                .ThenBy(f => TrackNumber(Tag(f.Data.Analysis.Tags, "track") ?? Tag(f.Data.Analysis.Tags, "tracknumber")))
                .ThenBy(f => Path.GetFileName(f.Path), NaturalFileNameComparer.Instance)
                .ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
            var first = items[0];
            var tags = first.Data.Analysis.Tags;
            var album = Tag(tags, "album") ?? (items.Length == 1 ? Path.GetFileNameWithoutExtension(first.Path)
                : new DirectoryInfo(folder ?? Path.GetDirectoryName(first.Path)!).Name);
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
                    new(item.Path, item.Length, item.WriteTicks, stream?.SampleCount, stream?.SampleRate ?? 0)
                    { Streams = item.Data.Analysis.Streams, Adm = item.Adm }));
                position = checked(position + item.Data.DurationTicks);
            }
            playlists.Add(new()
            {
                Id = playlists.Count + 1, DisplayOrder = playlists.Count + 1, Format = DiscFormat.AudioFile,
                IsAudioFolder = folder is not null, SkippedAudioFiles = skipped.Count,
                TitleName = album, DurationTicks = position, Clips = clips, ChapterStarts = starts,
                ChapterTitles = chapters.Values.Select(c => c.Title).ToArray(), MergeShortTail = false,
                FileAnalysis = new(first.Data.Analysis.Streams, chapters, tags)
                { DetectedAudioCount = first.Data.Analysis.DetectedAudioCount, Diagnostics = items.Select(i => i.Path).Concat(skipped).ToArray() }
            });
        }
        var albums = playlists.Select(p => Tag(p.FileAnalysis!.Tags, "album") ?? p.TitleName).Distinct().ToArray();
        var initial = files[0].Data.Analysis.Tags;
        var identity = string.Join('\n', files.Select(f => $"{f.Path.ToUpperInvariant()}|{f.Length}|{f.WriteTicks}"));
        return new()
        {
            Root = folder ?? files[0].Path, Format = DiscFormat.AudioFile,
            DiscKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((folder is null ? "AudioFiles/v1\n" : "AudioFolder/v1\n") + identity))),
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
                if (stream.Codec != "adm") await probe.VerifyAudioFileAsync(playlist.Clips[0].File!.Path, stream, token);
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
    public static IReadOnlyList<AudioStreamInfo> StreamsForTrack(PlaylistInfo playlist, TrackRow track) =>
        playlist.Clips.FirstOrDefault(clip => clip.PlaylistStartTicks <= track.StartTicks &&
            clip.PlaylistStartTicks + clip.OutTicks - clip.InTicks > track.StartTicks)?.File?.Streams ?? [];

    public static AudioStreamInfo StreamForTrack(PlaylistInfo playlist, TrackRow track, AudioStreamInfo preferred)
    {
        if (!playlist.IsAudioFolder) return preferred;
        var streams = StreamsForTrack(playlist, track);
        return streams.FirstOrDefault(s => s.Index == preferred.Index && IsUsable(s))
            ?? streams.FirstOrDefault(IsUsable) ?? throw new InvalidDataException(LanguageService.T("音声ストリームがありません。"));
    }

    private static int TrackNumber(string? text, int fallback = int.MaxValue) =>
        int.TryParse(text?.Split('/')[0].Trim(), out var value) && value > 0 ? value : fallback;
    private static string? Tag(IReadOnlyDictionary<string, string> tags, string name) => tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private sealed record Input(string Path, long Length, long WriteTicks, AudioFileProbe Data, AdmWave? Adm);
}

/// <summary>Numeric runs compare by value without integer overflow: Track 2 precedes Track 10.</summary>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        int a = 0, b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                int ax = a, by = b;
                while (a < x.Length && char.IsAsciiDigit(x[a])) a++;
                while (b < y.Length && char.IsAsciiDigit(y[b])) b++;
                while (ax < a - 1 && x[ax] == '0') ax++;
                while (by < b - 1 && y[by] == '0') by++;
                var result = (a - ax).CompareTo(b - by);
                if (result == 0) result = x.AsSpan(ax, a - ax).CompareTo(y.AsSpan(by, b - by), StringComparison.Ordinal);
                if (result != 0) return result;
            }
            else
            {
                var result = char.ToUpperInvariant(x[a++]).CompareTo(char.ToUpperInvariant(y[b++]));
                if (result != 0) return result;
            }
        }
        return (x.Length - a).CompareTo(y.Length - b);
    }
}
