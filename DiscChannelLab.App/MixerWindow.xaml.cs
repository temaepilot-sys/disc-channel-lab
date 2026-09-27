using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Disc2Flac;

public partial class MixerWindow : Window
{
    private readonly MainViewModel _model;
    public MixerWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        LanguageService.Instance.LanguageChanged += LanguageChanged;
        Closed += (_, _) => LanguageService.Instance.LanguageChanged -= LanguageChanged;
    }
    private void LanguageChanged(object? sender, EventArgs e)
    { foreach (var strip in _model.MixerStrips) strip.RefreshLanguage(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => _model.ResetExperimentalMixer();
    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitInputs()) return;
        var dialog = new SaveFileDialog
        {
            Title = LanguageService.T("ミキサー設定を保存"), Filter = "Mixer preset (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true, FileName = "mixer-preset.json", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try { _model.CaptureMixerPreset().Save(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowPresetError(ex); }
    }
    private void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LanguageService.T("ミキサー設定を読み込む"), Filter = "Mixer preset (*.json)|*.json",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try { _model.ApplyMixerPreset(MixerPreset.Load(dialog.FileName)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowPresetError(ex); }
    }
    private void ShowPresetError(Exception ex) => MessageBox.Show(this, ex.Message,
        LanguageService.T("ミキサー設定"), MessageBoxButton.OK, MessageBoxImage.Error);
    private void PanReset_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: MixerStrip strip }) strip.Pan = strip.DefaultPan; }
    private void Level_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox text) return;
        text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanExportMixer || !CommitInputs()) return;
        _model.SaveMixerDownmix = true;
        await _model.ConvertAsync();
    }
    public bool CommitInputs()
    {
        foreach (var input in Inputs((DependencyObject)Content))
        {
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (!Validation.GetHasError(input)) continue;
            input.Focus();
            return false;
        }
        return true;
        static IEnumerable<TextBox> Inputs(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox text) yield return text;
                foreach (var input in Inputs(child)) yield return input;
            }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
