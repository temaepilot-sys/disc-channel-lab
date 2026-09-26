using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Disc2Flac;

public partial class BulkEditWindow : Window
{
    private readonly IReadOnlyList<TrackRow> _tracks;
    private readonly IReadOnlyList<int> _titleTargets;
    private readonly IReadOnlyList<int> _artistTargets;
    private readonly int _chapterCount;
    private bool _normalizingTabs;
    private TabMarkerAdorner? _tabMarker;
    private AdornerLayer? _adornerLayer;
    public BulkEditPlan? Plan { get; private set; }

    public BulkEditWindow(string albumTitle, string titleName, IReadOnlyList<TrackRow> tracks,
        IReadOnlyList<int> titleTargets, IReadOnlyList<int> artistTargets,
        IReadOnlyList<string?> chapterTitles, bool showChapterTab = false)
    {
        InitializeComponent();
        _tracks = tracks;
        _titleTargets = titleTargets;
        _artistTargets = artistTargets;
        _chapterCount = chapterTitles.Count;
        AlbumBox.Text = albumTitle;
        TitleBox.Text = titleName;
        TitlesBox.Text = BulkEditService.FormatRows(titleTargets.Select(index => tracks[index]));
        ChapterNamesBox.Text = BulkEditService.FormatChapterTitles(chapterTitles);
        ChapterCountText.Text = LanguageService.T($"全 {_chapterCount} チャプター。上から1行ずつ入力します。空欄の行は従来の名前で保存します。");
        TargetText.Text = LanguageService.T(titleTargets.Count == tracks.Count
            ? $"全 {tracks.Count} 行を編集"
            : $"選択した {titleTargets.Count} 行を編集");
        ArtistTargetsText.Text = LanguageService.T(artistTargets.Count == 0
            ? "表で行を選択すると、一括でアーティスト名を設定できます。"
            : artistTargets.Count <= 12
                ? $"表で選択中: {string.Join(", ", artistTargets.Select(index => tracks[index].Number))} 行目"
                : $"表で選択中: {artistTargets.Count} 行");
        ArtistCheck.IsEnabled = artistTargets.Count > 0;
        if (showChapterTab) EditorTabs.SelectedIndex = 1;
        TitlesBox.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => _tabMarker?.InvalidateVisual()), true);
        TitlesBox.SizeChanged += (_, _) => _tabMarker?.InvalidateVisual();
        Loaded += (_, _) =>
        {
            _adornerLayer = AdornerLayer.GetAdornerLayer(TitlesBox);
            if (_adornerLayer is not null && _tabMarker is null)
            {
                _tabMarker = new TabMarkerAdorner(TitlesBox);
                _adornerLayer.Add(_tabMarker);
            }
            (showChapterTab ? ChapterNamesBox : TitlesBox).Focus();
        };
        Closed += (_, _) => { if (_adornerLayer is not null && _tabMarker is not null) _adornerLayer.Remove(_tabMarker); };
    }

    private void TitlesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_normalizingTabs) return;
        if (TitlesBox.Text.Contains('\t'))
        {
            _normalizingTabs = true;
            try
            {
                var caret = TitlesBox.CaretIndex;
                var priorTabs = TitlesBox.Text.AsSpan(0, caret).Count('\t');
                TitlesBox.Text = TitlesBox.Text.Replace("\t", BulkEditService.VisibleTab);
                TitlesBox.CaretIndex = caret + priorTabs * (BulkEditService.VisibleTab.Length - 1);
            }
            finally { _normalizingTabs = false; }
        }
        _tabMarker?.InvalidateVisual();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) throw new InvalidDataException("タイトル名を入力してください。");
            Plan = BulkEditService.Parse(_tracks, _titleTargets, _artistTargets,
                TitlesBox.Text, AlbumBox.Text, ArtistCheck.IsChecked == true, ArtistBox.Text) with
            {
                TitleName = TitleBox.Text.Trim(),
                ChapterTitles = BulkEditService.ParseChapterTitles(ChapterNamesBox.Text, _chapterCount)
            };
            DialogResult = true;
        }
        catch (InvalidDataException ex) { ErrorText.Text = LanguageService.T(ex.Message); }
    }
}
