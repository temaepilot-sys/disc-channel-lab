namespace Disc2Flac;

public sealed record BulkTrackChange(int Index, string Title, bool HasArtist, string? Artist);

public sealed record BulkEditPlan(string AlbumTitle, IReadOnlyList<BulkTrackChange> Tracks,
    IReadOnlyList<int> ArtistTargets, bool ApplyArtist, string? Artist)
{
    public IReadOnlyList<string?>? ChapterTitles { get; init; }
    public string? TitleName { get; init; }
}

public static class BulkEditService
{
    public const string VisibleTab = "⇥";
    public static string FormatChapterTitles(IReadOnlyList<string?> titles) =>
        string.Join(Environment.NewLine, titles.Select(title => title ?? ""));

    public static IReadOnlyList<string?> ParseChapterTitles(string text, int chapterCount)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count == chapterCount + 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count != chapterCount)
            throw new InvalidDataException($"チャプター名は {chapterCount} 行必要です。現在は {lines.Count} 行です。空欄も1行として残してください。");
        return lines.Select(line => string.IsNullOrWhiteSpace(line) ? null : line.Trim()).ToArray();
    }

    public static string FormatRows(IEnumerable<TrackRow> tracks) =>
        string.Join(Environment.NewLine, tracks.Select(track => track.Title +
            (string.IsNullOrEmpty(track.Artist) ? "" : VisibleTab + track.Artist)));

    public static BulkEditPlan Parse(IReadOnlyList<TrackRow> tracks, IReadOnlyList<int> titleTargets,
        IReadOnlyList<int> artistTargets, string text, string albumTitle, bool applyArtist, string? artist)
    {
        var album = albumTitle.Trim();
        if (album.Length == 0) throw new InvalidDataException("アルバム名を入力してください。");
        if (titleTargets.Count == 0) throw new InvalidDataException("編集する行を選択してください。");
        if (titleTargets.Distinct().Count() != titleTargets.Count ||
            titleTargets.Any(index => index < 0 || index >= tracks.Count) ||
            artistTargets.Any(index => index < 0 || index >= tracks.Count))
            throw new InvalidDataException("編集対象の行が変わりました。画面から開き直してください。");

        var lines = Lines(text);
        if (lines.Count != titleTargets.Count)
            throw new InvalidDataException($"曲名は {titleTargets.Count} 行必要です。現在は {lines.Count} 行です。");
        if (applyArtist && artistTargets.Count == 0)
            throw new InvalidDataException("アーティスト名を設定する行を表で選択してください。");
        if (applyArtist && string.IsNullOrWhiteSpace(artist))
            throw new InvalidDataException("選択行に設定するアーティスト名を入力してください。");

        var changes = new List<BulkTrackChange>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var tab = lines[i].IndexOf('\t');
            var visible = lines[i].IndexOf(VisibleTab, StringComparison.Ordinal);
            var useVisible = visible >= 0 && (tab < 0 || visible < tab);
            var separator = useVisible ? visible : tab;
            var width = useVisible ? VisibleTab.Length : 1;
            var title = (separator < 0 ? lines[i] : lines[i][..separator]).Trim();
            var rowArtist = separator < 0 ? null : lines[i][(separator + width)..].Trim();
            changes.Add(new BulkTrackChange(titleTargets[i], title, separator >= 0, rowArtist));
        }
        return new BulkEditPlan(album, changes, artistTargets.Distinct().ToArray(),
            applyArtist, artist?.Trim());
    }

    private static List<string> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
