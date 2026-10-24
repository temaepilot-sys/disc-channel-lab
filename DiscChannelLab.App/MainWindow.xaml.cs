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
    private MixerWindow? _mixerWindow;
    private HeadphonesWindow? _headphonesWindow;
    private CacheWindow? _cacheWindow;
    private SpatialLabWindow? _spatialLab;
    private void OpenSpatialLab_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanOpenSpatialLab) return;
        if (_spatialLab is { IsWindowClosed: false } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (!existing.IsVisible) existing.Show(); existing.Activate(); return;
        }
        _spatialLab = null;
        var window = new SpatialLabWindow(_model) { Owner = this };
        window.Closed += (_, _) => { if (ReferenceEquals(_spatialLab, window)) _spatialLab = null; };
        _spatialLab = window;
        try { window.Show(); }
        catch { if (ReferenceEquals(_spatialLab, window)) _spatialLab = null; throw; }
    }
    private void OpenAdmScene_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanOpenAdmScene) return;
        OpenSpatialLab_Click(sender, e);
        _spatialLab?.ShowAdmScene();
    }
    private bool _mixerOpened;
    private readonly DispatcherTimer _driveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public MainWindow() : this(null) { }

    public MainWindow(MainViewModel? model)
    {
        InitializeComponent();
        ThemeToggle.IsChecked = ThemeService.IsDark;
        _model = model ?? new MainViewModel();
        DataContext = _model;
        UpdateSacdToolPath();
        LanguageCombo.SelectedIndex = LanguageService.Instance.IsJapanese ? 1 : 0;
        _driveTimer.Tick += async (_, _) => { await _model.DetectInsertedDiscAsync(); _model.RefreshCacheControls(); };
        _playTimer.Tick += (_, _) => _model.AdvancePlaybackClock();
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Tracks_PreviewMouseLeftButtonDown), true);
        PlayerSeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekSlider_MouseDown), true);
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SeekSlider_MouseUp), true);
        PlayerSeekSlider.PreviewKeyDown += SeekSlider_KeyDown;
        PlayerSeekSlider.KeyUp += SeekSlider_KeyUp;
        PlaylistCombo.DropDownOpened += (_, _) => UpdatePlaylistDropDownWidth();
        SizeChanged += (_, _) => { if (PlaylistCombo.IsDropDownOpen) UpdatePlaylistDropDownWidth(); };
        if (Environment.GetCommandLineArgs().Contains("--spatial-lab")) Loaded += (_, _) => OpenSpatialLab_Click(this, new RoutedEventArgs());
    }

    private void OpenMixer_Click(object sender, RoutedEventArgs e)
    {
        if (_mixerWindow is not null)
        {
            if (_mixerWindow.WindowState == WindowState.Minimized) _mixerWindow.WindowState = WindowState.Normal;
            _mixerWindow.Activate();
            return;
        }
        if (!_mixerOpened && _model.CanUseExperimentalMixer)
        {
            SelectStereoMixPreview();
            _model.ExperimentalMixerEnabled = true;
            _mixerOpened = true;
        }
        _mixerWindow = new MixerWindow(_model) { Owner = this };
        _mixerWindow.Closed += (_, _) => _mixerWindow = null;
        _mixerWindow.Show();
    }

    private void OpenHeadphones_Click(object sender, RoutedEventArgs e)
    {
        if (_headphonesWindow is not null)
        {
            if (_headphonesWindow.WindowState == WindowState.Minimized) _headphonesWindow.WindowState = WindowState.Normal;
            _headphonesWindow.Activate(); return;
        }
        _headphonesWindow = new HeadphonesWindow(_model) { Owner = this };
        _headphonesWindow.Closed += (_, _) => _headphonesWindow = null;
        _headphonesWindow.Show();
    }

    private void OpenVisualizer_Click(object sender, RoutedEventArgs e)
    {
        if (_model.IsClearingCache) return;
        try
        {
            string? executable = null;
            for (var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                foreach (var relative in new[] { "SpaceSketch/DiscChannelLab.Visualizer.exe", "DiscChannelLab.Visualizer.exe",
                    "artifacts/personal-win-x64/SpaceSketch/DiscChannelLab.Visualizer.exe",
                    "artifacts/public-win-x64/SpaceSketch/DiscChannelLab.Visualizer.exe" })
                {
                    var candidate = System.IO.Path.Combine(directory.FullName, relative);
                    if (System.IO.File.Exists(candidate)) { executable = candidate; break; }
                }
                if (executable is not null) break;
            }
            if (executable is null)
            {
                var dialog = new OpenFileDialog { Title = LanguageService.T("3Dビューアーを選択"), Filter = "DiscChannelLab.Visualizer.exe|DiscChannelLab.Visualizer.exe" };
                if (dialog.ShowDialog(this) != true) return;
                executable = dialog.FileName;
            }
            _model.Visualization.Open(executable);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "3D Viewer", MessageBoxButton.OK, MessageBoxImage.Error); }
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
        bool autoDisc = !Environment.GetCommandLineArgs().Contains("--no-auto-disc");
        if (autoDisc) _driveTimer.Start();
        _playTimer.Start();
        HeadphoneSession.Current.RestartTracker();
        await Task.WhenAll(_model.RefreshCacheSizeAsync(), autoDisc ? _model.DetectInsertedDiscAsync() : Task.CompletedTask);
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
        _spatialLab?.Close();
        _cacheWindow?.Close();
        _headphonesWindow?.Close();
        HeadphoneSession.Current.Dispose();
        _mixerWindow?.Close();
        CommitTrackEdits();
        _driveTimer.Stop();
        _playTimer.Stop();
        _model.Cancel();
        _model.ReleaseAtmosStreaming();
        _model.Visualization.Dispose();
        _model.ChooseSpatialInput = null;
        _model.DetachLanguage();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        CommitTrackEdits();
        _model.RefreshDrives();
        await _model.OpenSourceAsync(_model.Source);
    }

    private void OpenCacheControl_Click(object sender, RoutedEventArgs e)
    {
        if (_cacheWindow is not null)
        {
            if (_cacheWindow.WindowState == WindowState.Minimized) _cacheWindow.WindowState = WindowState.Normal;
            _cacheWindow.Activate();
            return;
        }
        _cacheWindow = new CacheWindow(_model) { Owner = this };
        _cacheWindow.Closed += (_, _) => _cacheWindow = null;
        _cacheWindow.Show();
    }

    private async void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _model is null || _model.IsBusy) return;
        CommitTrackEdits();
        // Use this selection, rather than a binding value that may still be updating.
        var source = SourceCombo.SelectedItem as string ?? "";
        if (!DiscService.HasSupportedSource(source) && !Directory.Exists(source)) { _model.ClearCurrentDisc(); return; }
        await _model.OpenSourceAsync(source);
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = LanguageService.T("ディスクまたは音楽フォルダーを選択") };
        if (dialog.ShowDialog(this) != true) return;
        CommitTrackEdits();
        if (!_model.Sources.Contains(dialog.FolderName)) _model.Sources.Add(dialog.FolderName);
        await _model.OpenSourceAsync(dialog.FolderName);
    }

    private async void AudioFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_model.IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = LanguageService.T("音声ファイルを開く"), Multiselect = true, CheckFileExists = true,
            Filter = $"{LanguageService.T("音声ファイル")}|{AudioFileReader.Extensions}"
        };
        if (dialog.ShowDialog(this) != true) return;
        CommitTrackEdits();
        await _model.OpenAudioFilesAsync(dialog.FileNames);
    }

    private async void SpatialSource_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanChooseSpatialSource) return;
        if (_model.CurrentAdm is null && AtmosImportService.FindTool() is null)
        {
            var tool = new OpenFileDialog { Title = "Select truehdd 0.6.2", Filter = "truehdd.exe|truehdd.exe", CheckFileExists = true };
            if (tool.ShowDialog(this) != true) return;
            AtmosImportService.SaveTool(tool.FileName);
        }
        await _model.ChooseSpatialSourceAsync();
    }
    private async void AtmosPresentation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_model is null || sender is not System.Windows.Controls.ComboBox { SelectedItem: AtmosPreviewChoice choice }) return;
        await _model.ChangeAtmosPresentationAsync(choice);
    }
    private void ObjectAudioGuide_Click(object sender, RoutedEventArgs e) => new ObjectAudioGuideWindow { Owner = this }.Show();
    private async void ExportAdm_Click(object sender, RoutedEventArgs e)
    {
        CommitTrackEdits();
        var dialog = new SaveFileDialog { Title = "Save position-aware ADM — all chapters", Filter = "ADM BWF / BW64 (*.wav)|*.wav", FileName = "Position-aware-ADM.wav", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true && await _model.ExportAdmAsync(dialog.FileName))
            MessageBox.Show(this, LanguageService.Instance.IsJapanese ? "全曲の位置情報付きADMを保存しました。" : "Position-aware ADM saved, including all chapters.", "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Iso_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageService.T("Blu-ray / DVD / SACD ISOファイルを選択"),
            Filter = LanguageService.T("ISOファイル (*.iso)|*.iso"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        CommitTrackEdits();
        try
        {
            if (SacdService.IsSacdIso(dialog.FileName) && SacdService.FindTool() is null && !ChooseSacdTool()) return;
            await _model.OpenIsoAsync(dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, LanguageService.T(ex.Message), "SACD ISO", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SacdTool_Click(object sender, RoutedEventArgs e) => ChooseSacdTool();

    private void UpdateSacdToolPath()
    {
        var path = SacdService.FindTool();
        SacdToolPathText.Text = path ?? "";
        SacdToolPathText.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool ChooseSacdTool()
    {
        var currentTool = SacdService.FindTool();
        var dialog = new OpenFileDialog
        {
            Title = LanguageService.T("sacd_extract.exe（EuFlo 0.3.9.x互換版）を選択"),
            Filter = "sacd_extract.exe|sacd_extract.exe|Executable (*.exe)|*.exe", CheckFileExists = true,
            InitialDirectory = currentTool is null ? "" : System.IO.Path.GetDirectoryName(currentTool) ?? "",
            FileName = currentTool is null ? "sacd_extract.exe" : System.IO.Path.GetFileName(currentTool)
        };
        if (dialog.ShowDialog(this) != true) return false;
        try { SacdService.SaveTool(dialog.FileName); UpdateSacdToolPath(); return true; }
        catch (Exception ex) { MessageBox.Show(this, LanguageService.T(ex.Message), "SACD tool", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
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
        if (_model.SaveMixerDownmix && _mixerWindow?.CommitInputs() == false) return;
        CommitTrackEdits();
        if (await _model.ConvertAsync())
            MessageBox.Show(this, LanguageService.T("FLACの書き出しが完了しました。"), "DiscChannelLab", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CommitTrackEdits()
    {
        TracksGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TracksGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _model.Cancel();
    private void OpenSaved_Click(object sender, RoutedEventArgs e) => _model.OpenSavedFolder();
}
