using System.Text.Json;
using System.Text.RegularExpressions;

namespace Disc2Flac;

public sealed class TrackEditsStore
{
    private readonly AppLog _log;
    private readonly string _directory;
    private readonly string? _legacyDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TrackEditsStore(AppLog log, string? directory = null)
    {
        _log = log;
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = directory ?? Path.Combine(localData, "BDDVD2Flac", "track-edits");
        _legacyDirectory = directory is null ? Path.Combine(localData, "Disc2Flac", "track-edits") : null;
    }

    public string? LoadAlbumTitle(DiscAnalysis disc)
    {
        foreach (var path in ReadPaths(disc, "album.json"))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var edit = JsonSerializer.Deserialize<AlbumEdit>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(edit?.Title)) return edit.Title.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Write($"ALBUM EDIT LOAD FAILED {path}: {ex.Message}");
            }
        }
        return null;
    }

    public void SaveAlbumTitle(DiscAnalysis disc, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("アルバム名を入力してください。");
        var path = Path.Combine(DiscDirectory(disc), "album.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AlbumEdit(title.Trim()), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string? LoadTitleName(DiscAnalysis disc, PlaylistInfo playlist)
    {
        foreach (var path in ReadPaths(disc, $"{playlist.Id:00000}.title.json"))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var edit = JsonSerializer.Deserialize<AlbumEdit>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(edit?.Title)) return edit.Title.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Write($"TITLE NAME LOAD FAILED {path}: {ex.Message}");
            }
        }
        return null;
    }

    public void SaveTitleName(DiscAnalysis disc, PlaylistInfo playlist, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("タイトル名を入力してください。");
        var path = Path.Combine(DiscDirectory(disc), $"{playlist.Id:00000}.title.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AlbumEdit(title.Trim()), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<string?>? LoadChapterTitles(DiscAnalysis disc, PlaylistInfo playlist)
    {
        foreach (var path in ReadPaths(disc, $"{playlist.Id:00000}.chapters.json"))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var titles = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(path));
                if (titles is not null && titles.Count == playlist.ChapterStarts.Count)
                    return titles.Select(title => string.IsNullOrWhiteSpace(title) ? null : title.Trim()).ToArray();
                _log.Write($"CHAPTER TITLES INVALID {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Write($"CHAPTER TITLES LOAD FAILED {path}: {ex.Message}");
            }
        }
        return null;
    }

    public void SaveChapterTitles(DiscAnalysis disc, PlaylistInfo playlist, IReadOnlyList<string?> titles)
    {
        if (titles.Count != playlist.ChapterStarts.Count)
            throw new InvalidDataException("チャプター名の行数がチャプター数と一致しません。");
        var path = Path.Combine(DiscDirectory(disc), $"{playlist.Id:00000}.chapters.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                titles.Select(title => string.IsNullOrWhiteSpace(title) ? null : title.Trim()).ToArray(), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<TrackRow>? Load(DiscAnalysis disc, PlaylistInfo playlist)
    {
        foreach (var path in ReadPaths(disc, $"{playlist.Id:00000}.json"))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var rows = JsonSerializer.Deserialize<List<TrackEdit>>(File.ReadAllText(path));
                if (rows is null || !IsValid(rows, playlist.DurationTicks))
                {
                    _log.Write($"TRACK EDITS INVALID {path}");
                    continue;
                }
                return rows.Select((row, i) => new TrackRow
                {
                    Number = i + 1,
                    StartTicks = row.StartTicks,
                    EndTicks = row.EndTicks,
                    IsChapter = row.IsChapter,
                    IsSelected = row.IsSelected,
                    Title = row.Title!,
                    Artist = row.Artist
                }).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Write($"TRACK EDITS LOAD FAILED {path}: {ex.Message}");
            }
        }
        return null;
    }

    public void Save(DiscAnalysis disc, PlaylistInfo playlist, IReadOnlyList<TrackRow> tracks)
    {
        var rows = tracks.Select(track => new TrackEdit(track.StartTicks, track.EndTicks,
            track.Title, track.Artist, track.IsSelected, track.IsChapter)).ToArray();
        if (!IsValid(rows, playlist.DurationTicks)) throw new InvalidDataException("曲名情報の保存範囲が不正です。");
        var path = FilePath(disc, playlist);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(rows, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string FilePath(DiscAnalysis disc, PlaylistInfo playlist)
    {
        return Path.Combine(DiscDirectory(disc), $"{playlist.Id:00000}.json");
    }

    private IEnumerable<string> ReadPaths(DiscAnalysis disc, string fileName)
    {
        yield return Path.Combine(DiscDirectory(disc), fileName);
        if (disc.Format == DiscFormat.BluRay && _legacyDirectory is not null)
            yield return Path.Combine(_legacyDirectory, disc.DiscKey, fileName);
    }

    private string DiscDirectory(DiscAnalysis disc)
    {
        if (!Regex.IsMatch(disc.DiscKey, "^[0-9A-F]{64}$"))
            throw new InvalidDataException("ディスク識別情報がありません。");
        return Path.Combine(_directory, disc.DiscKey);
    }

    private static bool IsValid(IReadOnlyList<TrackEdit> rows, long durationTicks)
    {
        if (rows.Count is 0 or > 10000) return false;
        long expectedStart = 0;
        foreach (var row in rows)
        {
            if (row.StartTicks != expectedStart || row.EndTicks <= row.StartTicks ||
                row.EndTicks > durationTicks || row.Title is null) return false;
            expectedStart = row.EndTicks;
        }
        return expectedStart == durationTicks;
    }

    private sealed record TrackEdit(long StartTicks, long EndTicks, string? Title, string? Artist,
        bool IsSelected, bool IsChapter);
    private sealed record AlbumEdit(string Title);
}
