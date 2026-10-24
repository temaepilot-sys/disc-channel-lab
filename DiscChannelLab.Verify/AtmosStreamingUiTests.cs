using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class AtmosStreamingUiTests
{
    public static void Run(string output, string raw)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null; var thread = new Thread(() =>
        {
            MainWindow? main = null; SpatialLabWindow? lab = null; MainViewModel? model = null;
            var previousFactory = AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app); ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var checks = new List<string>(); var errors = new List<Exception>(); var sinks = new List<Sink>();
                app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
                async Task Until(Func<bool> done, string text)
                {
                    var watch = Stopwatch.StartNew(); while (!done()) { if (watch.Elapsed.TotalSeconds > 25) throw new TimeoutException(text + " · " + model?.Status); model?.AdvancePlaybackClock(); await Task.Delay(20); }
                }
                var field = typeof(MainViewModel).GetField("_playbackControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
                PreviewPlaybackControl? Control() => model is null ? null : (PreviewPlaybackControl?)field.GetValue(model);
                async Task Tests()
                {
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded); main.Show();
                    await model.OpenAudioFilesAsync([Path.GetFullPath(raw)]);
                    Check(model.IsAdmAvailable && !model.IsAdmPlaybackEnabled, "Opening the source detects support without activating object audio");
                    ((Button)main.FindName("AdmPlaybackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(model.IsAtmosPreparationPending && model.AtmosPreparationHint.Contains("please wait"),
                        "Initial object-audio preparation immediately announces the wait");
                    await Until(() => model.IsAtmosStreamingEnabled && !model.IsBusy && model.IsAdmPlaybackEnabled, "enable");
                    Check(model.CanPlay && model.HeadphonesEnabled && model.CurrentAdm is { Progressive: not null, Channels: 16 },
                        "One explicit button prepares a short live object source and actually selects HRTF");
                    var combo = (ComboBox)main.FindName("AtmosPresentationCombo"); Pump(120);
                    Check(combo.IsVisible && combo.Items.Count == 3 && !model.CanChooseAudioStream,
                        "Presentation comparison is visible and the conflicting raw stream selector is disabled");
                    var preparedText = (TextBlock)main.FindName("PreparedTimeText");
                    var notice = (Border)main.FindName("AtmosPreparationNotice");
                    bool IsColor(string resource) => ((SolidColorBrush)preparedText.Foreground).Color == ((SolidColorBrush)app.FindResource(resource)).Color;
                    await model.SeekAsync(22.25); Pump(20);
                    Check(model.IsAtmosPreparationPending && notice.IsVisible && IsColor("ErrorBrush") && preparedText.FontWeight == FontWeights.Bold &&
                        model.AtmosPreparationHint.Contains("00:00:22"),
                        "An unprepared selected position shows a red bold Prepared time and a clear wait notice before Play");
                    Save(main, Path.Combine(root, "prepared-wait-dark-en.png"));
                    ThemeService.SetDark(false); LanguageService.Instance.SetJapanese(true); Pump(20);
                    Check(IsColor("ErrorBrush") && model.AtmosPreparationHint.Contains("準備が必要") && preparedText.Text.StartsWith("準備済み"),
                        "Preparation notice remains readable in the light theme and switches to Japanese");
                    Save(main, Path.Combine(root, "prepared-wait-light-ja.png"));
                    ThemeService.SetDark(true); LanguageService.Instance.SetJapanese(false);
                    await model.SeekAsync(0); Pump(20);
                    Check(!model.IsAtmosPreparationPending && !notice.IsVisible && IsColor("MutedBrush") && preparedText.FontWeight == FontWeights.Normal,
                        "Returning to prepared audio restores the normal time style and hides the wait notice");
                    bool sawPlayingPreparation = false;
                    model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.IsAtmosPreparationPending) && model.IsPlaying && model.IsAtmosPreparationPending) sawPlayingPreparation = true; };
                    AudioNavigationService.HeadphoneOutputFactory = () => { var sink = new Sink(); sinks.Add(sink); return sink; };
                    await model.TogglePlaybackAsync(); await Until(() => Control() is { HasStarted: true } && sinks.Count == 1 && sinks[0].Samples > 12000, "objects play");
                    var retained = Control(); await model.TogglePlaybackAsync(); await Until(() => retained!.DevicePaused, "pause"); double held = model.PreviewSeconds;
                    await model.TogglePlaybackAsync(); await Until(() => model.IsPlaying && !retained!.DevicePaused, "resume");
                    Check(ReferenceEquals(retained, Control()) && sinks.Count == 1 && model.PreviewSeconds >= held,
                        "Progressive Pause/Play retains the native DSP, output and held position");
                    sawPlayingPreparation = false;
                    await model.SeekAsync(22.25); await Until(() => Control() is { HasStarted: true } && sinks.Count == 2 && !notice.IsVisible, "forward seek");
                    Check(model.PreviewSeconds >= 22.25 && model.PreviewSeconds < 23.5, "Main timeline seeks beyond read-ahead and resumes at the requested media position");
                    Check(sawPlayingPreparation && !model.IsAtmosPreparationPending && IsColor("MutedBrush"),
                        "A real forward-seek start announces preparation and clears the warning after audio starts");
                    await model.SeekAsync(3.125); await Until(() => Control() is { HasStarted: true } && sinks.Count == 3, "backward seek");
                    Check(model.PreviewSeconds >= 3.125 && model.PreviewSeconds < 4.5, "Main timeline seeks backward into cached audio without resetting the song");
                    double before = model.PreviewSeconds; combo.SelectedItem = model.AtmosPreviewChoices.Single(p => p.Value == AtmosPresentation.Surround51);
                    await Until(() => model.SelectedAtmosPresentation == AtmosPresentation.Surround51 && Control() is { HasStarted: true } && sinks.Count == 4, "5.1 switch");
                    Console.WriteLine($"Switch position {before:0.000} -> {model.PreviewSeconds:0.000}; channels {model.CurrentAdmScene?.Sources.Length}");
                    Check(model.CurrentAdmScene?.Sources.Length == 6 && model.PreviewSeconds >= before - .06 && model.PreviewSeconds < before + 1.5,
                        "Selecting 5.1 changes real source channels and retains the current track position (within output-clock sampling tolerance)");
                    combo.SelectedItem = model.AtmosPreviewChoices.Single(p => p.Value == AtmosPresentation.Stereo);
                    await Until(() => model.SelectedAtmosPresentation == AtmosPresentation.Stereo && Control() is { HasStarted: true } && sinks.Count == 5, "stereo switch");
                    Check(model.CurrentAdmScene?.Sources.Length == 2 && model.SelectedPreviewMode == PreviewAudioMode.Stereo,
                        "Selecting Stereo shows its two source channels and the HRTF bypass mode");
                    ((Button)main.FindName("OpenAdmSceneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                    lab = app.Windows.OfType<SpatialLabWindow>().Single();
                    Check(((DataGrid)lab.FindName("ObjectsGrid")).Items.Count == 2 && ((TextBlock)lab.FindName("ActiveHrtfText")).Text.Contains("bypassed"),
                        "Object inspector and HRTF header describe the actual comparison presentation");
                    Save(lab, Path.Combine(root, "stereo-comparison.png"));
                    combo.SelectedItem = model.AtmosPreviewChoices.Single(p => p.Value == AtmosPresentation.Objects);
                    await Until(() => model.SelectedAtmosPresentation == AtmosPresentation.Objects && Control() is { HasStarted: true } && sinks.Count == 6, "objects return");
                    model.ToggleAdmSourceSolo(model.CurrentAdmScene!.Sources[1].Id);
                    Check(model.AdmSoloSources.Count == 1 && ((DataGrid)lab.FindName("ObjectsGrid")).Items.Count == 16,
                        "Returning to Objects restores all positional sources and live Solo");
                    Save(lab, Path.Combine(root, "objects-streaming.png")); lab.Close(); lab = null;
                    await model.TogglePlaybackAsync(); await Until(() => Control() is { DevicePaused: true }, "export pause");
                    model.OutputFolder = Path.Combine(root, "export"); model.IsHighResSelected = true;
                    Check(await model.ConvertAsync(), "Main FLAC export renders from the progressive source without requiring an ADM archive");
                    var tools = new ToolPaths(); var probe = new FfprobeService(tools, new ProcessRunner(new AppLog(Path.Combine(root, "probe-logs"))));
                    var flac = await probe.ProbeAudioFileAsync(Directory.GetFiles(model.OutputFolder, "*.flac", SearchOption.AllDirectories).Single(), CancellationToken.None);
                    Check(flac.Analysis.Streams[0] is { Channels: 2, SampleRate: 48000, BitDepth: 24 }, "Progressive FLAC has the selected real 24-bit / 48 kHz stereo format");
                    Check(await model.ExportAdmAsync(Path.Combine(root, "saved-position-aware.wav")), "Explicit Save ADM remains a separate successful full-title archival operation");
                    model.StopPlayback(); await Task.Delay(100);
                    await model.DisableAdmPlaybackAsync();
                    Check(!model.IsAtmosStreamingEnabled && model.CurrentAdm is null && model.SelectedStream?.Codec == "truehd" && model.CanChooseAudioStream,
                        "Disabling object audio restores the original stream and its controls");
                    Check(!model.IsAtmosPreparationPending && !notice.IsVisible && !preparedText.IsVisible,
                        "Disabling object audio clears preparation indicators without leaving stale red text");
                    string cache = Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "atmos-live");
                    Check(!Directory.Exists(cache) || !Directory.EnumerateDirectories(cache).Any(), "Disabling object audio releases session PCM and metadata caches");
                    var guide = new ObjectAudioGuideWindow { Owner = main, ShowActivated = false }; guide.Show(); Pump(80); Save(guide, Path.Combine(root, "object-audio-guide.png")); guide.Close();
                    Check(errors.Count == 0 && sinks.All(s => s.Finite), "Guide, selectors, scene, transport and exports have no WPF errors or nonfinite device PCM");
                    Save(main, Path.Combine(root, "source-controls.png"));
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, sessions = sinks.Count, humanListening = false });
                }
                var task = Tests(); while (!task.IsCompleted) Pump(15); task.GetAwaiter().GetResult();
            }
            catch (Exception e) { failure = e; }
            finally { model?.StopPlayback(); model?.ReleaseAtmosStreaming(); lab?.Close(); main?.Close(); AudioNavigationService.HeadphoneOutputFactory = previousFactory; Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Progressive Atmos UI tests failed", failure);
    }
    static void Pump(int n) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(n) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    static void Save(Window w, string path) { w.UpdateLayout(); var b = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); b.Render(w); var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(b)); using var f = File.Create(path); e.Save(f); }
    sealed class Sink : IHeadphoneOutput
    {
        readonly SpatialLabTests.SilentDevice output = new(); long samples;
        public long Samples => Interlocked.Read(ref samples); public bool Finite { get; private set; } = true;
        public long SubmittedFrames => output.SubmittedFrames; public long PlayedFrames => output.PlayedFrames; public double BufferMilliseconds => output.BufferMilliseconds;
        public double StreamLatencyMilliseconds => output.StreamLatencyMilliseconds; public bool HasStarted => output.HasStarted; public long EmptyBufferCount => output.EmptyBufferCount; public bool SupportsPause => true;
        public void Pause() => output.Pause(); public void Resume() => output.Resume(); public void WaitForSpace(int n, CancellationToken token) => output.WaitForSpace(n, token);
        public void Write(float[] pcm, int frames) { for (int i = 0; i < frames * 2; i++) Finite &= float.IsFinite(pcm[i]); Interlocked.Add(ref samples, frames * 2); output.Write(pcm, frames); }
        public void Drain(CancellationToken token) => output.Drain(token); public void Dispose() => output.Dispose();
    }
}
