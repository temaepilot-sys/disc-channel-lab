using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Disc2Flac;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _switchingMixPreview;
    private readonly DispatcherTimer _driveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _mixTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public MainWindow()
    {
        InitializeComponent();
        ThemeToggle.IsChecked = ThemeService.IsDark;
        _model = new MainViewModel();
        DataContext = _model;
        LanguageCombo.SelectedIndex = LanguageService.Instance.IsJapanese ? 1 : 0;
        _driveTimer.Tick += async (_, _) => await _model.DetectInsertedDiscAsync();
        _playTimer.Tick += (_, _) => _model.AdvancePlaybackClock();
        _mixTimer.Tick += async (_, _) => { _mixTimer.Stop(); await ApplyMixControlAsync(); };
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Tracks_PreviewMouseLeftButtonDown), true);
        PlayerSeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekSlider_MouseDown), true);
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SeekSlider_MouseUp), true);
        PlayerSeekSlider.PreviewKeyDown += SeekSlider_KeyDown;
        PlayerSeekSlider.KeyUp += SeekSlider_KeyUp;
        PlaylistCombo.DropDownOpened += (_, _) => UpdatePlaylistDropDownWidth();
        SizeChanged += (_, _) => { if (PlaylistCombo.IsDropDownOpen) UpdatePlaylistDropDownWidth(); };
    }

    private void UpdatePlaylistDropDownWidth()
    {
        var width = Math.Min(Math.Max(PlaylistCombo.ActualWidth, ActualWidth - 72),
            SystemParameters.WorkArea.Width - 64);
        var style = new Style(typeof(ComboBoxItem));
        style.BasedOn = (Style)Application.Current.FindResource(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
        PlaylistCombo.ItemContainerStyle = style;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _driveTimer.Start();
        _playTimer.Start();
        await _model.DetectInsertedDiscAsync();
    }

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox) ThemeService.SetDark(checkBox.IsChecked == true);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || LanguageCombo.SelectedIndex < 0) return;
        LanguageService.Instance.SetJapanese(LanguageCombo.SelectedIndex == 1);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        CommitTrackEdits();
        _driveTimer.Stop();
        _playTimer.Stop();
        _mixTimer.Stop();
        _model.Cancel();
        _model.DetachLanguage();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        CommitTrackEdits();
        _model.RefreshDrives();
        await _model.OpenSourceAsync(_model.Source);
    }

    private async void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _model is null || _model.IsBusy) return;
        CommitTrackEdits();
        if (!DiscService.HasSupportedSource(_model.Source)) { _model.ClearCurrentDisc(); return; }
        await _model.OpenSourceAsync(_model.Source);
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = LanguageService.T("BDMV / AUDIO_TS / VIDEO_TS またはディスクのルートを選択") };
        if (dialog.ShowDialog(this) != true) return;
        if (!_model.Sources.Contains(dialog.FolderName)) _model.Sources.Add(dialog.FolderName);
        await _model.OpenSourceAsync(dialog.FolderName);
    }

    private async void Iso_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageService.T("Blu-ray / DVD ISOファイルを選択"),
            Filter = LanguageService.T("ISOファイル (*.iso)|*.iso"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        CommitTrackEdits();
        await _model.OpenIsoAsync(dialog.FileName);
    }

    private async void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _model.IsBusy) return;
        CommitTrackEdits();
        await _model.ChoosePlaylistAsync(_model.SelectedPlaylist);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _model.SelectAll(true);
    private async void RetryTitle_Click(object sender, RoutedEventArgs e)
    { CommitTrackEdits(); await _model.RetrySelectedTitleAsync(); }
    private void InspectAll_Click(object sender, RoutedEventArgs e) => _model.InspectAllTitles();
    private void StopInspection_Click(object sender, RoutedEventArgs e) => _model.StopInspection();
    private void MergeShortTail_Click(object sender, RoutedEventArgs e)
    {
        CommitTrackEdits();
        _model.ToggleShortTail(((CheckBox)sender).IsChecked == true);
    }
    private async void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = LanguageService.T("診断レポートを保存"),
            Filter = "JSON (*.json)|*.json", FileName = $"DiscChannelLab-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) == true) await _model.SaveDiagnosticsAsync(dialog.FileName);
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => _model.SelectAll(false);
    private void BulkEditAll_Click(object sender, RoutedEventArgs e) => OpenBulkEditor(selectedOnly: false);
    private void BulkEditSelected_Click(object sender, RoutedEventArgs e) => OpenBulkEditor(selectedOnly: true);
    private void ChapterNames_Click(object sender, RoutedEventArgs e) => OpenBulkEditor(selectedOnly: false, showChapterTab: true);

    private void OpenBulkEditor(bool selectedOnly, bool showChapterTab = false)
    {
        if (!_model.CanEditTracks) return;
        CommitTrackEdits();
        var tracks = _model.Tracks.ToArray();
        var selected = TracksGrid.SelectedItems.OfType<TrackRow>()
            .Select(track => Array.IndexOf(tracks, track)).Where(index => index >= 0).Distinct().Order().ToArray();
        if (selected.Length == 0 && _model.SelectedTrack is { } current)
        {
            var index = Array.IndexOf(tracks, current);
            if (index >= 0) selected = [index];
        }
        var targets = selectedOnly ? selected : Enumerable.Range(0, tracks.Length).ToArray();
        if (targets.Length == 0) return;
        var dialog = new BulkEditWindow(_model.AlbumTitle, _model.TitleName, tracks, targets, selected,
            _model.ChapterTitles, showChapterTab) { Owner = this };
        _driveTimer.Stop();
        try
        {
            if (dialog.ShowDialog() == true && dialog.Plan is { } plan)
                _model.ApplyBulkEdit(plan);
        }
        finally { _driveTimer.Start(); }
    }
    private async void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && double.TryParse(button.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            await _model.NudgePreviewAsync(seconds);
    }
    private void Split_Click(object sender, RoutedEventArgs e) => _model.SplitAtPreview();
    private void Merge_Click(object sender, RoutedEventArgs e) => _model.MergeWithNext();
    private async void Play_Click(object sender, RoutedEventArgs e) => await _model.TogglePlaybackAsync();
    private async void PreviewChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || _model.IsRefreshingLanguage || _switchingMixPreview ||
            sender is not ComboBox box) return;
        _model.SelectedPreviewChannel = box.SelectedItem as ChannelChoice;
        await _model.ChangePreviewChannelAsync();
    }
    private async void PreviousTrack_Click(object sender, RoutedEventArgs e) => await _model.PreviousTrackAsync();
    private async void NextTrack_Click(object sender, RoutedEventArgs e) => await _model.NextTrackAsync();
    private async void Tracks_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not DependencyObject source) return;
        var row = FindPlayableTrackRow(source);
        if (row?.Item is not TrackRow track) return;
        e.Handled = true;
        CommitTrackEdits();
        TracksGrid.SelectedItem = track;
        _model.SelectedTrack = track;
        await _model.PlaySelectedTrackAsync();
    }
    private static DataGridRow? FindPlayableTrackRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is CheckBox or TextBoxBase or DataGridColumnHeader) return null;
            if (source is DataGridRow row) return row;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void MixSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_model is null || !IsLoaded || _model.IsRefreshingLanguage) return;
        _mixTimer.Stop();
        _mixTimer.Start();
    }

    private async void MixSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _mixTimer.Stop();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (sender is Slider slider) slider.GetBindingExpression(Slider.ValueProperty)?.UpdateSource();
        await ApplyMixControlAsync();
    }

    private async void MixSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (sender is Slider slider) slider.GetBindingExpression(Slider.ValueProperty)?.UpdateSource();
        await ApplyMixControlAsync();
    }

    private async void MixInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!Validation.GetHasError(input)) await ApplyMixControlAsync();
    }

    private async void MixInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!Validation.GetHasError(input)) await ApplyMixControlAsync();
        e.Handled = true;
    }

    private async void MixMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox mute) return;
        var checkedState = mute.IsChecked == true;
        switch (mute.Tag as string)
        {
            case "Front": _model.FrontMixMuted = checkedState; break;
            case "Center": _model.CenterMixMuted = checkedState; break;
            case "Surround": _model.SurroundMixMuted = checkedState; break;
            case "LFE": _model.LfeMixMuted = checkedState; break;
        }
        mute.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        await ApplyMixControlAsync();
    }

    private async void ResetStereoMix_Click(object sender, RoutedEventArgs e)
    {
        SelectStereoMixPreview();
        await _model.ResetStereoMixAsync();
    }

    private async Task ApplyMixControlAsync()
    {
        _mixTimer.Stop();
        SelectStereoMixPreview();
        await _model.ApplyStereoMixAsync();
    }

    private void SelectStereoMixPreview()
    {
        if (_model.SelectedPreviewChannel?.Code is null || _model.PreviewChannels.Count == 0) return;
        _switchingMixPreview = true;
        try
        {
            var stereo = _model.PreviewChannels[0];
            PreviewChannelCombo.SelectedItem = stereo;
            _model.SelectedPreviewChannel = stereo;
        }
        finally { _switchingMixPreview = false; }
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e) => _model.StopPlayback();
    private async void FindSilence_Click(object sender, RoutedEventArgs e) => await _model.FindSilenceAsync();

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_model.CanSeek) return;
        _model.IsScrubbing = true;
        if (PlayerSeekSlider.Template.FindName("PART_Track", PlayerSeekSlider) is not Track track ||
            IsInsideThumb(e.OriginalSource as DependencyObject)) return;
        PlayerSeekSlider.Value = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)),
            PlayerSeekSlider.Minimum, PlayerSeekSlider.Maximum);
        e.Handled = true;
    }

    private async void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_model.IsScrubbing) return;
        _model.IsScrubbing = false;
        await _model.SeekAsync(PlayerSeekSlider.Value);
    }

    private void SeekSlider_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsSeekKey(e.Key)) _model.IsScrubbing = true;
    }

    private async void SeekSlider_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        _model.IsScrubbing = false;
        await _model.SeekAsync(PlayerSeekSlider.Value);
    }

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or
        Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private static bool IsInsideThumb(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Thumb) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private async void SplitTimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        await _model.SeekAsync(_model.PreviewSeconds);
    }

    private void SplitTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private async void Candidate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_model is not null && _model.SelectedCandidate is not null)
            await _model.SeekAsync(_model.PreviewSeconds);
    }

    private void OutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = LanguageService.T("FLAC の保存先を選択"), InitialDirectory = _model.OutputFolder };
        if (dialog.ShowDialog(this) == true) _model.OutputFolder = dialog.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitTrackEdits();
        await _model.ConvertAsync();
    }

    private void CommitTrackEdits()
    {
        TracksGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TracksGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _model.Cancel();
    private void OpenSaved_Click(object sender, RoutedEventArgs e) => _model.OpenSavedFolder();
}
