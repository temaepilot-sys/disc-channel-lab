using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class HeadphoneUiTests
{
    public static void Run(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var storedSettings = Path.Combine(root, "profile", "DiscChannelLab", "headphones.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storedSettings)!);
        File.WriteAllText(storedSettings, "{\"Enabled\":true,\"Method\":1,\"FrontAngle\":65}");
        var checks = new List<string>(); Exception? failure = null;
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var thread = new Thread(() =>
        {
            MainWindow? main = null; HeadphonesWindow? lab = null; MixerWindow? mixer = null;
            var factory = AudioNavigationService.HeadphoneOutputFactory;
            MainViewModel? model = null;
            try
            {
                var app = new App(); app.InitializeComponent(); ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var dispatcherErrors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
                model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
                main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
                main.Show();
                var previewMode = (ComboBox)main.FindName("PreviewModeCombo");
                Check(!model.HeadphonesEnabled && model.SelectedPreviewMode == PreviewAudioMode.Stereo &&
                    Equals(previewMode.SelectedValue, PreviewAudioMode.Stereo) && model.PreviewModes[0].Label == "2ch Stereo",
                    "Saved headphone-enabled preferences still start the main GUI in 2ch Stereo");
                Check(HeadphoneSession.Current.Options.FrontAngle == 65 && HeadphoneSession.Current.Options.Method == HeadphoneMethod.SteamAudio,
                    "Stereo startup retains the saved headphone renderer and speaker tuning");
                var source = Path.GetFullPath(Path.Combine(root, "..", "dsp-final", "six-channels.flac"));
                if (!File.Exists(source)) source = Path.GetFullPath(Path.Combine(root, "..", "dsp-initial", "six-channels.flac"));
                Pump(model.OpenAudioFilesAsync([source]));
                Check(model.CanPlay && !model.HeadphonesEnabled && Equals(previewMode.SelectedValue, PreviewAudioMode.Stereo),
                    "Opening multichannel audio keeps 2ch Stereo selected");
                main.Width = main.MinWidth; main.UpdateLayout();
                var mainSurface = (FrameworkElement)main.Content;
                var channelPicker = (ComboBox)main.FindName("PreviewChannelCombo");
                var modeBounds = previewMode.TransformToAncestor(mainSurface).TransformBounds(new Rect(previewMode.RenderSize));
                var channelBounds = channelPicker.TransformToAncestor(mainSurface).TransformBounds(new Rect(channelPicker.RenderSize));
                Check(modeBounds.Right <= mainSurface.ActualWidth && channelBounds.Right <= mainSurface.ActualWidth &&
                    !modeBounds.IntersectsWith(channelBounds),
                    "Preview mode and channel selectors remain separate and visible at the minimum window width");
                var mainBitmap = new RenderTargetBitmap((int)mainSurface.ActualWidth, (int)mainSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                mainBitmap.Render(mainSurface); var mainPng = new PngBitmapEncoder(); mainPng.Frames.Add(BitmapFrame.Create(mainBitmap));
                using (var imageFile = File.Create(Path.Combine(root, "stereo-preview-minimum-width.png"))) mainPng.Save(imageFile);
                main.Width = 1140; main.UpdateLayout();
                previewMode.SelectedValue = PreviewAudioMode.HeadphonesDirect;
                Check(model.CanPlay && model.MixerStrips.Count == 6 && model.ExperimentalMixerEnabled && model.MixerStrips.All(s => s.Level == 100),
                    "Preview Audio selects Direct with unity headphone faders for real six-channel FLAC");
                lab = new HeadphonesWindow(model) { Owner = main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                lab.Show(); Pump(Task.Delay(100));
                Check(lab.IsLoaded && lab.IsVisible, "Headphones comparison window opens with real application styles");
                var enable = (CheckBox)lab.FindName("EnableHeadphones");
                previewMode.SelectedValue = PreviewAudioMode.HeadphonesHrtf; Pump(Task.Delay(80));
                Check(HeadphoneSession.Current.Options.Method == HeadphoneMethod.SteamAudio && enable.IsChecked == true &&
                    ((ComboBox)lab.FindName("MethodCombo")).SelectedIndex == 1,
                    "Preview Audio HRTF selection synchronizes the open headphones window");
                previewMode.SelectedValue = PreviewAudioMode.HeadphonesDirect; Pump(Task.Delay(80));
                Check(HeadphoneSession.Current.Options.Method == HeadphoneMethod.Direct && ((ComboBox)lab.FindName("MethodCombo")).SelectedIndex == 0,
                    "Preview Audio Direct selection synchronizes both selectors");
                model.MixerStrips[0].Level = 45;
                previewMode.SelectedValue = PreviewAudioMode.Stereo; Pump(Task.Delay(80));
                Check(!model.HeadphonesEnabled && !model.ExperimentalMixerEnabled && model.CanPlay,
                    "Preview Audio 2ch Stereo restores ordinary playback and its original mixer enable state");
                Check(enable.IsChecked == false && !((ComboBox)lab.FindName("MethodCombo")).IsEnabled,
                    "Switching to 2ch Stereo also disables headphone rendering in the separate window");
                var expected = StereoPreviewMixer.StandardChannels(model.SelectedStream!);
                Check(model.MixerStrips.Zip(expected).All(p => Math.Abs(p.First.Level - p.Second.Gain * 100) < .0001),
                    "Normal playback retains the original standard downmix coefficients");
                model.ExperimentalMixerEnabled = true; model.MixerStrips[0].Level = 23;
                enable.IsChecked = true;
                Check(model.ExperimentalMixerEnabled && model.MixerStrips[0].Level == 45,
                    "Headphone fader bank and enable choice survive switching to normal playback");
                enable.IsChecked = false;
                Check(model.ExperimentalMixerEnabled && model.MixerStrips[0].Level == 23,
                    "Normal fader bank and enable choice survive switching to headphones");
                enable.IsChecked = true; model.ResetExperimentalMixer();
                LanguageService.Instance.SetJapanese(true); Pump(Task.Delay(80));
                Check((string)enable.Content == "ヘッドフォン再生を有効にする" && ((TextBlock)lab.FindName("FrontAngleLabel")).Text.Contains("前方L・R"),
                    "Main language switch updates headphone controls and live numeric labels to Japanese");
                LanguageService.Instance.SetJapanese(false); Pump(Task.Delay(80));
                Check((string)enable.Content == "Enable headphone rendering", "Headphone controls switch back to English");
                var mode = (ComboBox)lab.FindName("MethodCombo");
                mode.SelectedIndex = 1;
                Check(HeadphoneSession.Current.Options.Method == HeadphoneMethod.SteamAudio, "Stopped renderer selector chooses Steam Audio");
                Check(Equals(previewMode.SelectedValue, PreviewAudioMode.HeadphonesHrtf),
                    "Changing renderer in the headphones window updates Preview Audio");
                mode.SelectedIndex = 0;
                ((Slider)lab.FindName("Rear")).Value = 9;
                Check(HeadphoneSession.Current.Options.RearCutDb == 9, "Rear shelf slider updates live audio settings");
                ((Slider)lab.FindName("FrontAngle")).Value = 90;
                Check(HeadphoneSession.Current.Options.FrontAngle == 90, "Shared front L/R angle slider changes the virtual speaker geometry");
                ((Button)lab.FindName("ResetPlacementButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(HeadphoneSession.Current.Options.FrontAngle == 30, "Standard placement button restores 30 degrees per front speaker");
                var room = (Canvas)lab.FindName("Room");
                void Key(Key key) => room.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(room)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Key(System.Windows.Input.Key.Right); Key(System.Windows.Input.Key.Up); Key(System.Windows.Input.Key.E);
                Check(HeadphoneSession.Current.Pose == new HeadPose(5, 5, 5), "Real room keyboard events change yaw, pitch and roll");
                var nose = room.Children.OfType<System.Windows.Shapes.Line>().Single(s => Equals(s.Tag, "ListenerForward"));
                var fc = room.Children.OfType<System.Windows.Shapes.Ellipse>().Single(s => Equals(s.Tag, "FC"));
                Check(Math.Abs(nose.X2 - nose.X1) < 1e-6 && Canvas.GetLeft(fc) + fc.Width / 2 < nose.X1,
                    "Actual keyboard GUI keeps the listener facing forward and moves the front speaker to the left");
                var trackerPort = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                var portNumber = ((System.Net.IPEndPoint)trackerPort.Client.LocalEndPoint!).Port; trackerPort.Dispose();
                ((TextBox)lab.FindName("Port")).Text = portNumber.ToString();
                ((CheckBox)lab.FindName("UseTracker")).IsChecked = true;
                var poseBytes = new byte[48]; BitConverter.GetBytes(35d).CopyTo(poseBytes, 24); HeadphoneSession.Current.ReceivePacket(poseBytes);
                Pump(Task.Delay(80));
                nose = room.Children.OfType<System.Windows.Shapes.Line>().Single(s => Equals(s.Tag, "ListenerForward"));
                fc = room.Children.OfType<System.Windows.Shapes.Ellipse>().Single(s => Equals(s.Tag, "FC"));
                Check(nose.X2 - nose.X1 > 10 && Math.Abs(Canvas.GetLeft(fc) + fc.Width / 2 - nose.X1) < 1e-6,
                    "Actual tracking GUI keeps speakers fixed and rotates the listener as before");
                ((CheckBox)lab.FindName("UseTracker")).IsChecked = false;
                Key(System.Windows.Input.Key.Home);
                Check(HeadphoneSession.Current.Pose == new HeadPose(0, 0, 0), "Home returns keyboard orientation to center");
                mixer = new MixerWindow(model) { Owner = main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; mixer.Show();
                model.MixerStrips[0].Solo = true; model.MixerStrips[1].Solo = true; model.MixerStrips[0].Muted = true;
                Check(!model.MixerStrips[0].Solo && model.MixerStrips[0].Muted && model.MixerStrips[1].Solo, "Mixer retains multi-solo and same-channel mute exclusion");
                model.ResetExperimentalMixer();
                Check(model.MixerStrips.All(s => s.Level == 100 && !s.Solo && !s.Muted), "Headphone mixer reset restores source unity, not stereo attenuation");
                model.PreviewSeconds = .3;
                ((Button)lab.FindName("MarkButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                mode.SelectedIndex = 1;
                AudioNavigationService.HeadphoneOutputFactory = () => new PacedSilentSink();
                model.ContinuousPlayback = false;
                ((Button)lab.FindName("ReplayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump(Task.Delay(170));
                Check(model.IsPlaying && model.PreviewSeconds >= .3 && !mode.IsEnabled && !enable.IsEnabled && !previewMode.IsEnabled,
                    "Play from mark starts the chosen renderer at the saved position and locks mode selection during playback");
                lab.Close(); lab = null;
                Check(model.IsPlaying && main.IsVisible, "Comparison window remains modeless and can close during playback");
                model.StopPlayback(); Pump(Task.Delay(150));
                lab = new HeadphonesWindow(model) { Owner = main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; lab.Show(); Pump(Task.Delay(80));
                Check(((ComboBox)lab.FindName("MethodCombo")).IsEnabled, "Renderer selector unlocks after stopping");
                ((Button)lab.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(Task.Delay(100));
                ((Button)lab.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(Task.Delay(100));
                Check(!model.IsPlaying && model.PreviewSeconds > 0, "Comparison Play / Pause preserves the playback position");
                lab.UpdateLayout();
                var visual = (FrameworkElement)lab.Content;
                ((Grid)visual).Background = lab.Background;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(root, "headphones-window.png"))) png.Save(output);
                Check(dispatcherErrors.Count == 0, "GUI interaction completes without dispatcher exceptions");
                HeadphoneSession.Current.Save();
                using (var saved = JsonDocument.Parse(File.ReadAllText(storedSettings)))
                    Check(!saved.RootElement.GetProperty("Enabled").GetBoolean() && model.HeadphonesEnabled,
                        "Saving tuning does not change live playback and stores stereo as the next startup mode");
                Check(File.Exists(AppDataPaths.Current.PreferencePath("headphones.json")) && Path.GetFileName(AppDataPaths.Current.Root) == "DiscChannelLab",
                    "Headphone settings save in the main DiscChannelLab profile separately from disc metadata");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                model?.StopPlayback(); Pump(Task.Delay(100));
                lab?.Close(); mixer?.Close(); main?.Close();
                AudioNavigationService.HeadphoneOutputFactory = factory;
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Headphone GUI check timed out.");
        if (failure is not null) throw failure;
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Pump(Task task)
    {
        do
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        } while (!task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private sealed class PacedSilentSink : IHeadphoneOutput
    {
        public long SubmittedFrames { get; private set; }
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 0;
        public void WaitForSpace(int frames, CancellationToken token) { token.ThrowIfCancellationRequested(); Thread.Sleep(5); }
        public void Write(float[] interleaved, int frames) => SubmittedFrames += frames;
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() { }
    }
}
