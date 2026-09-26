using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

public sealed class DiscService(FfprobeService probe, AppLog log)
{
    public DiscAnalysis Analyze(string path)
    {
        var root = NormalizeDiscRoot(path);
        if (Directory.Exists(Path.Combine(root, "BDMV", "PLAYLIST")))
            return AnalyzeBluRay(root);
        var audio = DvdAudioReader.ReadAll(root, log.Write);
        IReadOnlyList<PlaylistInfo> video = [];
        if (File.Exists(Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO")))
        {
            try { video = DvdVideoReader.ReadAll(root, probe, log.Write); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { log.Write($"DVD-Video: {ex.Message}"); }
        }
        var playlists = audio.Concat(video).ToArray();
        if (playlists.Length == 0)
            throw new InvalidDataException("再生可能な DVD-Audio / DVD-Video のタイトルが見つかりません。");
        for (var i = 0; i < playlists.Length; i++) playlists[i].DisplayOrder = i + 1;
        var label = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)) label = drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new DiscAnalysis
        {
            Root = root, Format = audio.Count > 0 ? DiscFormat.DvdAudio : DiscFormat.DvdVideo,
            DiscKey = audio.Count > 0 ? DvdAudioReader.DiscKey(root) : DvdVideoReader.DiscKey(root),
            AlbumTitle = label, Playlists = playlists
        };
    }

    private DiscAnalysis AnalyzeBluRay(string root)
    {
        if (Directory.Exists(Path.Combine(root, "AACS")) || Directory.Exists(Path.Combine(root, "BDMV", "AACS")))
            throw new ProtectedDiscException("このディスクはコピー保護されているため処理できません。");
        var playlists = MplsReader.ReadAll(root, log.Write);
        if (playlists.Count == 0) throw new InvalidDataException("再生可能なプレイリストを確認できませんでした。");
        var metadata = ReadDiscMetadata(root);
        return new DiscAnalysis
        {
            Root = root, DiscKey = ReadDiscKey(root), AlbumTitle = metadata.AlbumTitle ?? "Unknown Album", Playlists = playlists,
            Artist = metadata.Artist, Date = metadata.Date, Genre = metadata.Genre,
            Publisher = metadata.Publisher, Description = metadata.Description
        };
    }

    public static string NormalizeDiscRoot(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) is { } name &&
            (name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AUDIO_TS", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)))
            full = Directory.GetParent(full)?.FullName ?? full;
        if (!Directory.Exists(Path.Combine(full, "BDMV", "PLAYLIST")) &&
            !File.Exists(Path.Combine(full, "AUDIO_TS", "AUDIO_TS.IFO")) &&
            !File.Exists(Path.Combine(full, "VIDEO_TS", "VIDEO_TS.IFO")))
            throw new InvalidDataException("BDMV、AUDIO_TS、VIDEO_TS が見つかりません。");
        return full;
    }

    public static bool HasSupportedSource(string root) =>
        Directory.Exists(Path.Combine(root, "BDMV", "PLAYLIST")) ||
        File.Exists(Path.Combine(root, "AUDIO_TS", "AUDIO_TS.IFO")) ||
        File.Exists(Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO"));

    public async Task<(IReadOnlyList<AudioStreamInfo> Streams, IReadOnlyList<TrackRow> Tracks)> AnalyzePlaylistAsync(DiscAnalysis disc, PlaylistInfo playlist, CancellationToken token)
    {
        PlaylistProbeResult result;
        try { result = await probe.ProbePlaylistAsync(disc.Root, playlist, token); }
        catch (FfToolException ex) when (ex.Message.Contains("AACS", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("BD+", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProtectedDiscException("このディスクはコピー保護されているため処理できません。");
        }
        if (result.Streams.Count == 0) throw new InvalidDataException("対応する音声ストリームがありません。");
        if (disc.AlbumTitle == "Unknown Album") disc.AlbumTitle = Tag(result.Tags, "album") ?? Tag(result.Tags, "title") ?? disc.AlbumTitle;
        disc.Artist ??= Tag(result.Tags, "album_artist") ?? Tag(result.Tags, "artist");
        disc.Date ??= Tag(result.Tags, "date");
        disc.Genre ??= Tag(result.Tags, "genre");
        disc.Publisher ??= Tag(result.Tags, "publisher");
        disc.Description ??= Tag(result.Tags, "description") ?? Tag(result.Tags, "comment");
        var tracks = new List<TrackRow>();
        var wholePlaylistIsOneTrack = playlist.ChapterStarts.Count == 1 && playlist.ChapterStarts[0] == 0;
        for (var i = 0; i < playlist.ChapterStarts.Count; i++)
        {
            var start = playlist.ChapterStarts[i];
            var end = i + 1 < playlist.ChapterStarts.Count ? playlist.ChapterStarts[i + 1] : playlist.DurationTicks;
            if (end <= start) continue;
            result.Chapters.TryGetValue(i + 1, out var chapter);
            var title = chapter?.Title;
            var hasTitle = !string.IsNullOrWhiteSpace(title) && !title.StartsWith("Chapter", StringComparison.OrdinalIgnoreCase);
            tracks.Add(new TrackRow
            {
                Number = i + 1,
                StartTicks = start,
                EndTicks = end,
                IsChapter = playlist.Format != DiscFormat.DvdAudio && !hasTitle && !wholePlaylistIsOneTrack,
                IsSelected = playlist.Format != DiscFormat.DvdAudio || end - start >= 2 * 45000,
                Title = hasTitle ? title!.Trim() : playlist.Format == DiscFormat.DvdAudio ? $"Track {i + 1:00}" : wholePlaylistIsOneTrack ? LanguageService.T("Track 01 (全編)") : $"Chapter {i + 1:00}",
                Artist = chapter?.Artist ?? disc.Artist
            });
        }
        return (result.Streams, tracks);
    }

    private static string? Tag(IReadOnlyDictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string ReadDiscKey(string root)
    {
        var builder = new StringBuilder();
        var bdmv = Path.Combine(root, "BDMV");
        var index = Path.Combine(bdmv, "index.bdmv");
        if (File.Exists(index)) builder.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(index))));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(bdmv, "PLAYLIST"), "*.mpls").OrderBy(Path.GetFileName))
            builder.Append(Path.GetFileName(file)).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append(';');
        var streams = Path.Combine(bdmv, "STREAM");
        if (Directory.Exists(streams))
            foreach (var file in Directory.EnumerateFiles(streams, "*.m2ts").OrderBy(Path.GetFileName))
            {
                builder.Append(Path.GetFileName(file)).Append(':').Append(new FileInfo(file).Length).Append(':');
                using var input = File.OpenRead(file);
                var sample = new byte[4096];
                var count = input.Read(sample);
                builder.Append(Convert.ToHexString(SHA256.HashData(sample.AsSpan(0, count)))).Append(';');
            }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static DiscMetadata ReadDiscMetadata(string root)
    {
        var folder = Path.Combine(root, "BDMV", "META", "DL");
        if (!Directory.Exists(folder)) return new DiscMetadata();
        var metadata = new DiscMetadata();
        foreach (var file in Directory.EnumerateFiles(folder, "*.xml").OrderBy(x =>
                     Path.GetFileName(x).Contains("eng", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            try
            {
                using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(reader);
                metadata.AlbumTitle ??= Value(document, "name", "title");
                metadata.Artist ??= Value(document, "artist", "creator", "albumartist");
                metadata.Date ??= Value(document, "date", "releasedate", "year");
                metadata.Genre ??= Value(document, "genre");
                metadata.Publisher ??= Value(document, "publisher");
                metadata.Description ??= Value(document, "description", "synopsis");
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { }
        }
        return metadata;
    }

    private static string? Value(XDocument document, params string[] names) => document.Descendants()
        .Where(x => names.Contains(x.Name.LocalName, StringComparer.OrdinalIgnoreCase))
        .Select(x => x.Value.Trim()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}

public sealed class DiscMetadata
{
    public string? AlbumTitle { get; set; }
    public string? Artist { get; set; }
    public string? Date { get; set; }
    public string? Genre { get; set; }
    public string? Publisher { get; set; }
    public string? Description { get; set; }
}
