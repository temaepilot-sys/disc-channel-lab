using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ComparisonPresentationUiTests
{
    public static void Run(string output, string source, string brirProfile)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null; var checks = new List<string>();
        var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null; SpatialLabWindow? lab = null;
            var factory = AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app); ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors = new List<Exception>(); app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool value, string text) { if (!value) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
                void Complete(Task task) { while (!task.IsCompleted) Pump(20); task.GetAwaiter().GetResult(); }
                void Until(Func<bool> done, string text)
                {
                    var watch = Stopwatch.StartNew();
                    while (!done() && watch.Elapsed.TotalSeconds < 45) { model?.AdvancePlaybackClock(); Pump(20); }
                    if (!done()) throw new TimeoutException(text + " · " + model?.Status + " · " + ((TextBlock?)lab?.FindName("ComparisonFeedback"))?.Text);
                }
                Button Button(string name) => (Button)lab!.FindName(name);
                void Click(string name)
                {
                    var button = Button(name); Check(button.IsEnabled, "Clickable: " + name);
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                string Feedback() => ((TextBlock)lab!.FindName("ComparisonFeedback")).Text;
                TabControl Pages() => (TabControl)lab!.FindName("Pages");
                PreviewPlaybackControl? MainControl() => (PreviewPlaybackControl?)typeof(MainViewModel).GetField("_playbackControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model);
                bool Auditioning() => typeof(SpatialLabWindow).GetField("comparisonStop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab) is not null;
                void Prepared() => Until(() => Pages().IsEnabled && Button("ComparisonPlayAButton").IsEnabled, "prepare");

                model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
                main.Show(); lab = new SpatialLabWindow(model) { Owner = main, ShowActivated = false }; lab.Show();
                Pages().SelectedItem = lab.FindName("ComparisonTab"); Pump(100);
                AudioNavigationService.HeadphoneOutputFactory = () => new SpatialLabTests.SilentDevice();
                var mainCombo = (ComboBox)main.FindName("AtmosPresentationCombo");
                var labCombo = (ComboBox)lab.FindName("ComparisonPresentationCombo");
                Check(mainCombo.IsVisible && labCombo.IsVisible && !mainCombo.IsEnabled && !labCombo.IsEnabled && !Button("ComparisonUse51Button").IsEnabled,
                    "Both source selectors remain visible when no source is selected; unavailable controls are disabled");
                Check(((TextBlock)lab.FindName("ComparisonPresentationHint")).Text.Contains("TrueHD Atmos"),
                    "Disabled selector explains which sources provide presentations");
                Complete(model.OpenAudioFilesAsync([Path.GetFullPath(source)]));
                Check(model.IsAdmAvailable && !model.IsAdmPlaybackEnabled && !labCombo.IsEnabled && model.AtmosPresentationHint.Contains("Enable ADM playback"),
                    "A detected TrueHD source explains the explicit Enable ADM playback prerequisite");
                ((Button)main.FindName("AdmPlaybackButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Until(() => model.IsAtmosStreamingEnabled && model.IsAdmPlaybackEnabled && !model.IsBusy, "enable Atmos"); Pump(100);
                Check(mainCombo.IsEnabled && labCombo.IsEnabled && labCombo.Items.Count == 3 && Button("ComparisonUse51Button").IsEnabled,
                    "Enabling real Atmos activates Objects / 5.1 / Stereo in both windows and the inline 5.1 button");
                var point = mainCombo.TransformToAncestor(main).Transform(new Point());
                Check(point.Y >= 0 && point.Y + mainCombo.ActualHeight < main.ActualHeight,
                    "Main source selector appears above the preparation notice in the default window viewport");
                Save(main, Path.Combine(root, "01-main-source-selector.png"));
                typeof(SpatialLabWindow).GetMethod("ReadBrir", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lab, [Path.GetFullPath(brirProfile)]);
                ((CheckBox)lab.FindName("CompareBrir")).IsChecked = true; ((TextBox)lab.FindName("IntervalLength")).Text = "5";
                Click("ComparisonPrepareButton"); Until(() => Pages().IsEnabled, "Objects rejection");
                Check(model.SelectedAtmosPresentation == AtmosPresentation.Objects && Feedback().Contains("Use 5.1 for HRIR / BRIR") && Button("ComparisonUse51Button").IsEnabled,
                    "The reported Objects error points to an enabled button in the same comparison view");
                Save(lab, Path.Combine(root, "02-objects-recovery-button.png"));
                Complete(model.SeekAsync(2)); Complete(model.TogglePlaybackAsync()); Until(() => MainControl()?.HasStarted == true, "main playback");
                model.AdvancePlaybackClock(); double before = model.PreviewSeconds;
                Click("ComparisonUse51Button"); Until(() => Pages().IsEnabled && model.SelectedAtmosPresentation == AtmosPresentation.Surround51 && MainControl()?.HasStarted == true, "inline 5.1 switch");
                Check(model.IsPlaying && model.CurrentAdmScene?.Sources.Length == 6 && model.PreviewSeconds >= before - .06 && model.PreviewSeconds < before + 2,
                    "The inline 5.1 button changes real source PCM while retaining the playing track and position");
                Check(Equals(mainCombo.SelectedItem, model.SelectedAtmosPreview) && Equals(labCombo.SelectedItem, model.SelectedAtmosPreview) && !Button("ComparisonUse51Button").IsEnabled,
                    "Both selectors reflect the same 5.1 state and the already-selected shortcut is disabled");
                model.PausePlayback(); Click("ComparisonPrepareButton"); Prepared();
                var comparison = (ComparisonAudio)typeof(SpatialLabWindow).GetField("comparison", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab)!;
                Check(comparison.A.Input.StoredChannels == 6 && comparison.B.Input.StoredChannels == 6 && comparison.B.Snapshot.Options.Method == HeadphoneMethod.MeasuredBrir,
                    "Prepare now renders identical real 5.1 input through HRIR A and measured D1 BRIR B");
                Save(lab, Path.Combine(root, "03-5.1-brir-prepared.png"));
                Click("ComparisonPlayBButton"); Until(() => ((TextBlock)lab.FindName("ComparisonPosition")).Text.StartsWith("Playing B"), "BRIR audition");
                Check(Button("ComparisonPlayAButton").IsEnabled && Button("ComparisonPlayBButton").IsEnabled,
                    "Normal playback notifications do not invalidate a prepared comparison");
                labCombo.SetCurrentValue(Selector.SelectedItemProperty, model.AtmosPreviewChoices.First(p => p.Value == AtmosPresentation.Stereo));
                Until(() => Pages().IsEnabled && model.SelectedAtmosPresentation == AtmosPresentation.Stereo && !Auditioning(), "switch during audition");
                Check(!Button("ComparisonPlayAButton").IsEnabled && !model.IsPlaying && model.CurrentAdmScene?.Sources.Length == 2 && mainCombo.SelectedItem == labCombo.SelectedItem,
                    "Changing presentation stops the old audition, invalidates cached A/B and synchronizes both windows");
                Click("ComparisonPrepareButton"); Until(() => Pages().IsEnabled, "Stereo rejection");
                Check(Feedback().Contains("Stereo presentation bypasses") && Feedback().Contains("Use 5.1"),
                    "Stereo bypass failure also offers the working inline 5.1 recovery");
                mainCombo.SetCurrentValue(Selector.SelectedItemProperty, model.AtmosPreviewChoices.First(p => p.Value == AtmosPresentation.Objects));
                Until(() => model.SelectedAtmosPresentation == AtmosPresentation.Objects && Equals(labCombo.SelectedItem, model.SelectedAtmosPreview), "main to lab sync");
                Check(Button("ComparisonUse51Button").IsEnabled && model.CurrentAdmScene?.Sources.Length == 16,
                    "Main-window selection propagates back to the Lab without recursively switching or losing Objects");
                Complete(model.DisableAdmPlaybackAsync()); Pump(100);
                Check(mainCombo.IsVisible && labCombo.IsVisible && !mainCombo.IsEnabled && !labCombo.IsEnabled && !Button("ComparisonUse51Button").IsEnabled,
                    "Disabling ADM keeps the controls visible with an explanation instead of hiding them");
                Complete(model.OpenAudioFilesAsync([Path.GetFullPath("tests/spatial/SpatialLab-original-ADM-demo.wav")])); Complete(model.EnableAdmPlaybackAsync()); Pump(100);
                Check(!labCombo.IsEnabled && model.AtmosPresentationHint.Contains("without separate 5.1"),
                    "A standalone ADM scene explains that a separate 5.1 presentation cannot be invented");
                ThemeService.SetDark(false); Pump(100); Save(lab, Path.Combine(root, "04-native-adm-explanation-light.png"));
                Check(errors.Count == 0, "Selectors, inline recovery and real BRIR preparation have no WPF dispatcher errors");
                SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, humanListening = false, physicalOutput = "Silent WASAPI verification" });
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                model?.PausePlayback(); lab?.Close(); Pump(100);
                if (model is not null) { var disable = model.DisableAdmPlaybackAsync(); while (!disable.IsCompleted) Pump(20); }
                main?.Close(); AudioNavigationService.HeadphoneOutputFactory = factory; Application.Current?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Comparison presentation UI verification failed", failure);
    }
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Save(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
