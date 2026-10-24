using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Disc2Flac;

public partial class HeadphonesWindow : Window
{
    private readonly MainViewModel _model;
    private readonly HeadphoneSession _session = HeadphoneSession.Current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _updating = true;
    private bool _sofaLoading, _closed;
    private static readonly HeadphoneOptions Defaults = new();
    private static readonly IReadOnlyDictionary<string, double> SliderDefaults = new Dictionary<string, double>
    {
        ["FrontAngle"] = Defaults.FrontAngle, ["Shadow"] = Defaults.ShadowStrength * 100,
        ["Rear"] = Defaults.RearCutDb, ["Corner"] = Defaults.RearFrequency, ["Radius"] = Defaults.HeadRadius * 100,
        ["Headroom"] = Defaults.HeadroomDb, ["Lfe"] = Defaults.LfeGain * 100,
        ["DirectTrim"] = Defaults.DirectTrimDb, ["SteamTrim"] = Defaults.SteamTrimDb, ["Smooth"] = Defaults.SmoothingMs,
        ["Yaw"] = 0, ["Pitch"] = 0, ["Roll"] = 0
    };
    private double _mark;
    private TrackRow? _markTrack;
    public HeadphonesWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        ReadOptions();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Room.Focus(); Refresh(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); _session.Save(); };
        _updating = false;
    }
    private void ReadOptions()
    {
        var o = _session.Options;
        EnableHeadphones.IsChecked = o.Enabled;
        MethodCombo.SelectedIndex = (int)o.Method;
        SettingsTabs.SelectedIndex = (int)o.Method;
        ReadSoundOptions();
        ReadHrtfOptions();
        UseTracker.IsChecked = o.UseTracker; Port.Text = o.UdpPort.ToString();
        InvertYaw.IsChecked = o.InvertYaw; InvertPitch.IsChecked = o.InvertPitch; InvertRoll.IsChecked = o.InvertRoll;
        UpdateLabels();
    }
    private void ReadSoundOptions()
    {
        var o = _session.Options;
        Shadow.Value = o.ShadowStrength * 100; Rear.Value = o.RearCutDb; Corner.Value = o.RearFrequency; Radius.Value = o.HeadRadius * 100;
        Delay.IsChecked = o.InterauralDelay; Headroom.Value = o.HeadroomDb; Lfe.Value = o.LfeGain * 100;
        DirectTrim.Value = o.DirectTrimDb; SteamTrim.Value = o.Hrtf == HeadphoneHrtf.Sofa ? o.SofaTrimDb : o.SteamTrimDb; Smooth.Value = o.SmoothingMs;
        FrontAngle.Value = o.FrontAngle;
        BrirTrim.Value = o.BrirTrimDb;
    }
    private void ReadHrtfOptions()
    {
        var updating = _updating; _updating = true;
        HrtfCombo.SelectedIndex = (int)_session.Options.Hrtf;
        SofaPathText.Text = string.IsNullOrWhiteSpace(_session.Options.SofaPath) ? LanguageService.T("SOFA未選択") : _session.Options.SofaPath;
        SofaPathText.ToolTip = SofaPathText.Text;
        SteamTrim.Value = _session.Options.Hrtf == HeadphoneHrtf.Sofa ? _session.Options.SofaTrimDb : _session.Options.SteamTrimDb;
        _updating = updating;
        UpdateLabels();
    }
    private void UpdateLabels()
    {
        ShadowLabel.Text = $"{HeadphoneText.T("Head shadow strength")} · {Shadow.Value:0}%";
        RearLabel.Text = $"{HeadphoneText.T("Rear high-frequency reduction")} · {Rear.Value:0.0} dB";
        CornerLabel.Text = $"{HeadphoneText.T("Rear shelf corner")} · {Corner.Value:0} Hz";
        RadiusLabel.Text = $"{HeadphoneText.T("Head radius")} · {Radius.Value:0.00} cm";
        HeadroomLabel.Text = $"{HeadphoneText.T("Shared headroom")} · {Headroom.Value:0.0} dB";
        LfeLabel.Text = $"{HeadphoneText.T("LFE contribution")} · {Lfe.Value:0}%";
        DirectTrimLabel.Text = $"{HeadphoneText.T("Direct comparison trim")} · {DirectTrim.Value:+0.0;-0.0;0.0} dB";
        SteamTrimLabel.Text = $"{(_session.Options.Hrtf == HeadphoneHrtf.Sofa ? LanguageService.T("SOFA比較トリム") : HeadphoneText.T("HRTF comparison trim"))} · {SteamTrim.Value:+0.0;-0.0;0.0} dB";
        SmoothLabel.Text = $"{HeadphoneText.T("Orientation smoothing")} · {Smooth.Value:0} ms";
        BrirTrimLabel.Text = $"BRIR comparison trim · {BrirTrim.Value:+0.0;-0.0;0.0} dB";
        FrontAngleLabel.Text = $"{HeadphoneText.T("Front L/R angle")} · ±{FrontAngle.Value:0}° ({HeadphoneText.T("apart")} {FrontAngle.Value * 2:0}°)";
        foreach (var (name, value) in SliderDefaults)
        {
            var button = (Button)FindName("Reset" + name);
            var title = ((TextBlock)FindName(name + "Label")).Text;
            button.ToolTip = $"{LanguageService.T("標準値に戻す")} · {title} → {value:0.###}";
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        }
    }
    private void Options_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => WriteOptions();
    private void Option_Checked(object sender, RoutedEventArgs e) => WriteOptions();
    private void WriteOptions()
    {
        if (_updating) return;
        var current = _session.Options;
        _session.SetOptions(current with
        {
            ShadowStrength = Shadow.Value / 100, RearCutDb = Rear.Value, RearFrequency = Corner.Value, HeadRadius = Radius.Value / 100,
            InterauralDelay = Delay.IsChecked == true, HeadroomDb = Headroom.Value, LfeGain = Lfe.Value / 100,
            DirectTrimDb = DirectTrim.Value, SteamTrimDb = current.Hrtf == HeadphoneHrtf.BuiltIn ? SteamTrim.Value : current.SteamTrimDb,
            SofaTrimDb = current.Hrtf == HeadphoneHrtf.Sofa ? SteamTrim.Value : current.SofaTrimDb,
            SmoothingMs = Smooth.Value, FrontAngle = FrontAngle.Value, BrirTrimDb = BrirTrim.Value
        });
        UpdateLabels();
    }
    private async void Hrtf_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || HrtfCombo.SelectedIndex < 0 || HrtfCombo.SelectedIndex == (int)_session.Options.Hrtf) return;
        var hrtf = (HeadphoneHrtf)HrtfCombo.SelectedIndex;
        if (hrtf == HeadphoneHrtf.Sofa && string.IsNullOrWhiteSpace(_session.Options.SofaPath)) await BrowseSofaAsync();
        else await SelectHrtfAsync(hrtf, _session.Options.SofaPath);
    }
    private async void BrowseSofa_Click(object sender, RoutedEventArgs e) => await BrowseSofaAsync();
    private async Task BrowseSofaAsync()
    {
        if (!_model.CanChoosePreviewMode || _sofaLoading) { ReadHrtfOptions(); return; }
        var dialog = new OpenFileDialog { Title = LanguageService.T("SOFAファイルを選択"), Filter = "SOFA (*.sofa)|*.sofa", CheckFileExists = true };
        if (File.Exists(_session.Options.SofaPath)) dialog.FileName = _session.Options.SofaPath;
        if (dialog.ShowDialog(this) == true) await SelectHrtfAsync(HeadphoneHrtf.Sofa, dialog.FileName);
        else ReadHrtfOptions();
    }
    internal async Task<bool> SelectHrtfAsync(HeadphoneHrtf hrtf, string? sofaPath = null)
    {
        if (!_model.CanChoosePreviewMode || _sofaLoading)
        {
            NoteText.Text = LanguageService.T("一時停止または停止してからHRTFを変更してください。");
            if (!_sofaLoading) ReadHrtfOptions();
            return false;
        }
        _sofaLoading = true;
        try
        {
            Refresh();
            string? path = null;
            if (hrtf == HeadphoneHrtf.Sofa)
            {
                NoteText.Text = LanguageService.T("SOFAを検証しています…");
                path = Path.GetFullPath(sofaPath ?? _session.Options.SofaPath);
                await Task.Run(() => SofaHrtf.Validate(path));
            }
            if (_closed) return false;
            // The main player can be operated while validation is running. Do not replace
            // the renderer that such a playback session has already created.
            if (!_model.CanChoosePreviewMode)
            {
                NoteText.Text = LanguageService.T("一時停止または停止してからHRTFを変更してください。");
                return false;
            }
            _session.SetOptions(_session.Options with { Hrtf = hrtf, SofaPath = path ?? _session.Options.SofaPath });
            _session.Save();
            NoteText.Text = LanguageService.T(hrtf == HeadphoneHrtf.Sofa ? "外部SOFAを検証して選択しました。" : "標準HRTFを選択しました。");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            if (!_closed) NoteText.Text = LanguageService.T("HRTFを変更できませんでした。") + " " + ex.Message;
            return false;
        }
        finally
        {
            _sofaLoading = false;
            if (!_closed) { ReadHrtfOptions(); Refresh(); }
        }
    }
    private void Enable_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (!_model.CanChoosePreviewMode)
        {
            _updating = true; EnableHeadphones.IsChecked = _session.Options.Enabled; _updating = false;
            return;
        }
        _session.SetOptions(_session.Options with { Enabled = EnableHeadphones.IsChecked == true });
        Refresh();
    }
    private void Method_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || MethodCombo.SelectedIndex < 0) return;
        if (!_model.CanChoosePreviewMode) { _updating = true; MethodCombo.SelectedIndex = (int)_session.Options.Method; _updating = false; return; }
        if (MethodCombo.SelectedIndex == 2 && string.IsNullOrEmpty(_session.Options.BrirHash))
        { NoteText.Text = "Load and apply a BRIR profile in Spatial Lab first."; ReadOptions(); return; }
        _session.SetOptions(_session.Options with { Method = (HeadphoneMethod)MethodCombo.SelectedIndex });
        SettingsTabs.SelectedIndex = MethodCombo.SelectedIndex;
        Refresh();
    }
    private void Tracker_Changed(object sender, RoutedEventArgs e) => ApplyTracker(false);
    private void Reconnect_Click(object sender, RoutedEventArgs e) => ApplyTracker(true);
    private void ApplyTracker(bool reconnect)
    {
        if (_updating) return;
        if (!int.TryParse(Port.Text, out var port) || port < 1024 || port > 65535) { NoteText.Text = HeadphoneText.T("Enter a UDP port between 1024 and 65535."); return; }
        _session.SetOptions(_session.Options with { UseTracker = UseTracker.IsChecked == true, UdpPort = port,
            InvertYaw = InvertYaw.IsChecked == true, InvertPitch = InvertPitch.IsChecked == true, InvertRoll = InvertRoll.IsChecked == true });
        if (reconnect) _session.RestartTracker();
        NoteText.Text = _session.TrackerError ?? "Tracking settings applied.";
    }
    private void Pose_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating) _session.SetKeyboard(Yaw.Value, Pitch.Value, Roll.Value);
    }
    private void Room_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Room.Focus();
        if (e.OriginalSource is FrameworkElement { Tag: string code } && ApplySpeakerClick(code, Keyboard.Modifiers))
            e.Handled = true;
    }
    private bool ApplySpeakerClick(string code, ModifierKeys modifiers)
    {
        if (!_model.CanUseExperimentalMixer) return false;
        var strip = _model.MixerStrips.FirstOrDefault(s => s.Code == code);
        if (strip is null) return false;
        var muted = _model.ExperimentalMixerEnabled && strip.Muted;
        var solo = _model.ExperimentalMixerEnabled && strip.Solo;
        _model.ExperimentalMixerEnabled = true;
        if (modifiers.HasFlag(ModifierKeys.Shift)) strip.Solo = !solo;
        else if (solo) { strip.Solo = false; strip.Muted = false; }
        else strip.Muted = !muted;
        DrawRoom();
        return true;
    }
    private void Room_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Home) { _session.Recenter(); e.Handled = true; return; }
        if (_session.Options.UseTracker) return;
        var p = _session.Pose; var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;
        var next = e.Key switch
        {
            Key.Left => p with { Yaw = p.Yaw - step }, Key.Right => p with { Yaw = p.Yaw + step },
            Key.Up => p with { Pitch = p.Pitch + step }, Key.Down => p with { Pitch = p.Pitch - step },
            Key.Q => p with { Roll = p.Roll - step }, Key.E => p with { Roll = p.Roll + step }, _ => null
        };
        if (next is not null) { _session.SetKeyboard(next.Yaw, next.Pitch, next.Roll); e.Handled = true; Refresh(); }
    }
    private void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        UseTracker.IsChecked = false;
        _session.SetOptions(_session.Options with { UseTracker = false });
        Room.Focus();
    }
    private void Center_Click(object sender, RoutedEventArgs e) { _session.Recenter(); Room.Focus(); Refresh(); }
    private void ResetDirect_Click(object sender, RoutedEventArgs e)
    {
        _session.SetOptions(_session.Options with { ShadowStrength = Defaults.ShadowStrength, RearCutDb = Defaults.RearCutDb,
            RearFrequency = Defaults.RearFrequency, HeadRadius = Defaults.HeadRadius, InterauralDelay = Defaults.InterauralDelay });
        _updating = true; ReadSoundOptions(); _updating = false; UpdateLabels();
    }
    private void ResetPlacement_Click(object sender, RoutedEventArgs e) => FrontAngle.Value = Defaults.FrontAngle;
    private void ResetSlider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && SliderDefaults.TryGetValue(name, out var value) && FindName(name) is Slider slider)
            slider.SetCurrentValue(Slider.ValueProperty, value);
    }
    private void ResetSound_Click(object sender, RoutedEventArgs e)
    {
        _session.SetOptions(_session.Options.ResetSound());
        _updating = true; ReadSoundOptions(); _updating = false; UpdateLabels();
        NoteText.Text = LanguageService.T("音響設定を標準値へ戻しました。HRTFモデルとトラッカー設定は保持します。");
    }
    private async void Play_Click(object sender, RoutedEventArgs e) => await _model.TogglePlaybackAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => _model.StopPlayback();
    private void Mark_Click(object sender, RoutedEventArgs e) { _mark = _model.PreviewSeconds; _markTrack = _model.SelectedTrack; Refresh(); }
    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_markTrack is not null && !ReferenceEquals(_markTrack, _model.SelectedTrack)) { NoteText.Text = HeadphoneText.T("Track changed. Mark a position in the current track first."); return; }
        _model.StopPlayback(); await _model.SeekAsync(_mark); await _model.TogglePlaybackAsync();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        WriteOptions(); _session.Save(); NoteText.Text = _session.TrackerError ?? HeadphoneText.T("Headphone settings saved separately from disc metadata.");
    }
    private void Refresh()
    {
        _updating = true;
        EnableHeadphones.IsChecked = _session.Options.Enabled;
        if (MethodCombo.SelectedIndex != (int)_session.Options.Method)
        {
            MethodCombo.SelectedIndex = (int)_session.Options.Method;
            SettingsTabs.SelectedIndex = MethodCombo.SelectedIndex;
        }
        _updating = false;
        EnableHeadphones.IsEnabled = _model.CanChoosePreviewMode;
        MethodCombo.IsEnabled = _model.CanChoosePreviewMode && _session.Options.Enabled;
        HrtfCombo.IsEnabled = BrowseSofaButton.IsEnabled = _model.CanChoosePreviewMode && !_sofaLoading;
        if (!_sofaLoading && HrtfCombo.SelectedIndex != (int)_session.Options.Hrtf) ReadHrtfOptions();
        var sofaText = string.IsNullOrWhiteSpace(_session.Options.SofaPath) ? LanguageService.T("SOFA未選択") : _session.Options.SofaPath;
        if (SofaPathText.Text != sofaText) { SofaPathText.Text = sofaText; SofaPathText.ToolTip = sofaText; }
        DirectControls.IsEnabled = _session.Options.Method == HeadphoneMethod.Direct;
        MethodHint.Text = !_session.Options.Enabled ? HeadphoneText.T("Headphones are disabled; standard playback is active.") : _model.IsPlaying ? HeadphoneText.T("Stop or pause before changing renderer.") : HeadphoneText.T("Renderer can be changed while stopped.");
        bool brir = _session.Options.Method == HeadphoneMethod.MeasuredBrir;
        ManualControls.IsEnabled = !_session.Options.UseTracker && !brir;
        UseTracker.IsEnabled = !brir; FrontAngle.IsEnabled = ResetFrontAngle.IsEnabled = ResetPlacementButton.IsEnabled = !brir;
        TrackingStatus.Text = _session.TrackerStatus;
        RoomModeText.Text = brir ? "MEASURED BRIR / FIXED HEAD" : _session.Options.UseTracker ? HeadphoneText.T("ROOM FIXED / HEAD TRACKING") : HeadphoneText.T("LISTENER FIXED / ROOM ROTATES");
        OutputStatusText.Text = _session.AudioStatus;
        var p = _session.Pose;
        PoseText.Text = $"Yaw {p.Yaw:+0.0;-0.0;0.0}°  ·  Pitch {p.Pitch:+0.0;-0.0;0.0}°  ·  Roll {p.Roll:+0.0;-0.0;0.0}°";
        _updating = true; Yaw.Value = p.Yaw; Pitch.Value = p.Pitch; Roll.Value = p.Roll; _updating = false;
        PlayButton.IsEnabled = (!_sofaLoading && _model.CanPlay) || _model.IsPlaying; StopButton.IsEnabled = _model.CanSeek;
        MarkButton.IsEnabled = _model.CanSeek; ReplayButton.IsEnabled = _model.CanSeek && !_sofaLoading;
        PlayButton.Content = _model.IsPlaying ? HeadphoneText.T("Pause") : HeadphoneText.T("Play");
        MarkText.Text = $"{HeadphoneText.T("Mark")} {TimeSpan.FromSeconds(_mark):mm\\:ss\\.f}";
        TrackText.Text = _model.SelectedTrack is { } t ? $"{t.Title}  ·  {_model.SelectedStream?.ChannelLayout}" : HeadphoneText.T("Open a disc, ISO or audio file in the main window.");
        UpdateLabels();
        DrawRoom();
    }
    private void DrawRoom()
    {
        var width = Room.ActualWidth; var height = Room.ActualHeight;
        if (width < 20 || height < 20) return;
        Room.Children.Clear();
        var cx = width / 2; var cy = height / 2; var radius = Math.Max(40, Math.Min(width, height) / 2 - 48);
        Ellipse Ellipse(double x, double y, double r, Brush fill, Brush? stroke = null, string? tag = null)
        {
            var shape = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = 1, Tag = tag };
            Canvas.SetLeft(shape, x - r); Canvas.SetTop(shape, y - r); Room.Children.Add(shape);
            return shape;
        }
        void Line(double x, double y, double xx, double yy, Brush color, double thick = 1, string? tag = null)
            => Room.Children.Add(new Line { X1 = x, Y1 = y, X2 = xx, Y2 = yy, Stroke = color, StrokeThickness = thick, Tag = tag });
        Ellipse(cx, cy, radius, Brushes.Transparent, new SolidColorBrush(Color.FromRgb(41, 60, 80)));
        Line(cx, cy - radius - 15, cx, cy + radius + 15, new SolidColorBrush(Color.FromRgb(26, 40, 55)));
        var channels = _model.SelectedStream is { } stream ? HeadphoneGeometry.Channels(stream) : ["FL", "FR", "FC", "LFE", "SL", "SR"];
        var hasSolo = _model.ExperimentalMixerEnabled && _model.MixerStrips.Any(s => s.Solo);
        foreach (var code in channels)
        {
            var speaker = HeadphoneGeometry.DisplaySpeaker(code, _session.Options.Method == HeadphoneMethod.MeasuredBrir ? new(0,0,0) : _session.Pose, _session.Options, channels);
            var x = cx + radius * speaker.X; var y = cy - radius * speaker.Z;
            if (code == "LFE") { x = cx; y = cy + 38; }
            var meter = _model.ChannelMeters.FirstOrDefault(m => m.Code == code);
            var strip = _model.MixerStrips.FirstOrDefault(s => s.Code == code);
            var clickable = _model.CanUseExperimentalMixer && strip is not null;
            var muted = _model.ExperimentalMixerEnabled && strip?.Muted == true;
            var solo = _model.ExperimentalMixerEnabled && strip?.Solo == true;
            var suppressed = muted || hasSolo && !solo;
            var strength = _model.IsPlaying && meter is not null ? meter.PeakPercent / 100 : 0;
            var color = suppressed ? new SolidColorBrush(Color.FromRgb(49, 56, 66)) :
                new SolidColorBrush(Color.FromRgb((byte)(176 + strength * 70), (byte)(96 + strength * 60), (byte)(34 + strength * 35)));
            var outline = solo ? Brushes.Gold : suppressed ? Brushes.SlateGray : Brushes.PeachPuff;
            if (code != "LFE") Line(x, y, cx, cy, new SolidColorBrush(suppressed ? Color.FromArgb(35, 110, 110, 110) : Color.FromArgb(60, 240, 150, 65)));
            var shape = Ellipse(x, y, code == "LFE" ? 9 : 12, color, outline, code);
            var hint = !clickable ? HeadphoneText.T("Open a disc, ISO or audio file in the main window.") :
                LanguageService.T(solo ? "クリックしてSolo解除（オンに戻す）" : muted ? "クリックしてMute解除" : "クリックしてMute") +
                " · " + LanguageService.T(solo ? "Shift＋クリックでSolo解除" : "Shift＋クリックでSolo");
            shape.Cursor = clickable ? Cursors.Hand : Cursors.Arrow;
            shape.ToolTip = $"{code} · {hint}";
            shape.StrokeThickness = solo ? 3 : 1;
            System.Windows.Automation.AutomationProperties.SetName(shape, $"{code} · {(solo ? "Solo" : muted ? "Mute" : "On")}");
            if (solo)
            {
                var badge = new TextBlock { Text = "S", FontSize = 11, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White, Width = 16, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
                Canvas.SetLeft(badge, x - 8); Canvas.SetTop(badge, y - 8); Room.Children.Add(badge);
            }
            if (muted)
            {
                Line(x - 6, y + 6, x + 6, y - 6, Brushes.LightGray, 2);
                Room.Children[^1].IsHitTestVisible = false;
            }
            var label = new TextBlock { Text = code, Foreground = suppressed ? Brushes.SlateGray : Brushes.PeachPuff,
                FontSize = 12, Tag = code, ToolTip = shape.ToolTip, Cursor = shape.Cursor };
            Canvas.SetLeft(label, x - 9); Canvas.SetTop(label, code == "LFE" ? y + 13 : y - 29); Room.Children.Add(label);
        }
        var yaw = _session.Options.UseTracker ? _session.Pose.Yaw * Math.PI / 180 : 0;
        var forward = new Vector(Math.Sin(yaw), -Math.Cos(yaw)); var right = new Vector(Math.Cos(yaw), Math.Sin(yaw));
        Ellipse(cx, cy, 18, new SolidColorBrush(Color.FromRgb(37, 68, 87)), Brushes.LightSkyBlue);
        Ellipse(cx - right.X * 21, cy - right.Y * 21, 4, Brushes.LightSkyBlue);
        Ellipse(cx + right.X * 21, cy + right.Y * 21, 4, Brushes.Orange);
        Line(cx, cy, cx + forward.X * 37, cy + forward.Y * 37, Brushes.White, 3, "ListenerForward");
    }
}
