using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static class AdmResourceTests
{
    public static void Run(string output, string path, bool baseline)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null; SpatialLabWindow? lab = null;
            var checks = new List<string>(); var metrics = new List<object>();
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                ThemeService.SetDark(true);
                var errors = new List<Exception>(); app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
                async Task Test()
                {
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main,
                        typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!);
                    main.Show(); await model.OpenAudioFilesAsync([Path.GetFullPath(path)]);
                    Check(await model.EnableAdmPlaybackAsync(), "A real saved ADM opens without modifying or decoding its source PCM");
                    ((Button)main.FindName("OpenAdmSceneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(300); lab = app.Windows.OfType<SpatialLabWindow>().Single();
                    var view = (Viewport3D)lab.FindName("SceneView"); var input = (Border)lab.FindName("SceneInput");
                    ((DispatcherTimer)Field(lab, "timer")).Stop(); ((DispatcherTimer)Field(lab, "sceneHeadTimer")).Stop();
                    var scene = model.CurrentAdmScene!;
                    var update = (Action<double>)typeof(SpatialLabWindow).GetMethod("UpdateScene", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .CreateDelegate(typeof(Action<double>), lab);
                    var setScene = (Action<SpatialScene>)typeof(SpatialLabWindow).GetMethod("SetScene", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .CreateDelegate(typeof(Action<SpatialScene>), lab);
                    var positions = scene.Sources.Select(s => s.At(Math.Min(scene.DurationSamples - 1, 48000)).Position).ToArray();
                    Check(scene.Sources.Length is > 0 and <= 32, "ADM source count stays within the supported renderer limit");
                    Console.WriteLine($"INPUT sources={scene.Sources.Length} keyframes={scene.Sources.Sum(s => s.Blocks.LongLength)} duration={scene.DurationSamples / 48000d:0.0}s tier={RenderCapability.Tier >> 16}");
                    for (int i = 0; i < 100; i++) update(i / 10d);
                    await Task.Delay(150); Collect(); var initial = Sample();
                    int rebuilt = 0; var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 36000; i++)
                    {
                        update((i / 10d) % (scene.DurationSamples / 48000d));
                        if (i % 100 == 0)
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            if (Sample().PrivateBytes > initial.PrivateBytes + 768L * 1024 * 1024) throw new InvalidDataException("Resource guard: private memory grew by more than 768 MiB");
                        }
                    }
                    await Task.Delay(200); Collect(); var afterFrames = Sample();
                    metrics.Add(new { phase = "36000 position updates / one virtual hour at 10 Hz", elapsedSeconds = watch.Elapsed.TotalSeconds, before = initial, after = afterFrames });
                    var rootModel = view.Children[0];
                    watch.Restart();
                    for (int i = 0; i < 1200; i++)
                    {
                        // Progressive decoding publishes new immutable keyframes with the same source layout.
                        setScene(scene with { Sources = scene.Sources.Select(s => s with { }).ToArray() });
                        if (!ReferenceEquals(rootModel, view.Children[0])) { rebuilt++; rootModel = view.Children[0]; }
                        update((i * 3d) % (scene.DurationSamples / 48000d));
                        if (i % 20 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                        if (Sample().PrivateBytes > initial.PrivateBytes + 768L * 1024 * 1024) throw new InvalidDataException("Resource guard: scene refresh memory exceeded the bound");
                    }
                    await Task.Delay(250); Collect(); var afterSnapshots = Sample();
                    metrics.Add(new { phase = "1200 metadata snapshots with unchanged source layout", elapsedSeconds = watch.Elapsed.TotalSeconds, geometryRebuilds = rebuilt, after = afterSnapshots });
                    Check(view.Children.Count == 2 && ((Canvas)lab.FindName("SceneLabels")).Children.Count == scene.Sources.Length * 2,
                        "Visual and label counts stay bounded during an accelerated hour of object movement");
                    Check(afterSnapshots.Gdi <= initial.Gdi + 20 && afterSnapshots.User <= initial.User + 20,
                        "GDI and USER resources stay bounded after scene updates and metadata refreshes");
                    Check(afterSnapshots.Handles <= initial.Handles + 100 && afterSnapshots.PrivateBytes <= initial.PrivateBytes + 192L * 1024 * 1024,
                        "Handles and retained private memory stay bounded after collection");
                    // Reproduce a missed mouse-up without injecting input into another process.
                    Check(Mouse.LeftButton == MouseButtonState.Released, "The capture-recovery test begins with the physical button released");
                    SetField(lab, "pointerButton", MouseButton.Left); SetField(lab, "pointerDragged", true);
                    input.CaptureMouse(); bool nativeCaptureGranted = input.IsMouseCaptured;
                    ((DispatcherTimer)Field(lab, "timer")).Start(); await Task.Delay(350);
                    bool captureReleased = !input.IsMouseCaptured;
                    bool dragCleared = lab.GetType().GetField("pointerButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab) is null;
                    metrics.Add(new { phase = "missed mouse-up recovery", nativeCaptureGranted, captureReleased, dragCleared });
                    input.ReleaseMouseCapture();
                    if (!baseline)
                    {
                        Check(captureReleased && dragCleared, "A missing mouse-up clears stale drag state and releases capture when granted by Windows");
                        void StaleDrag() { SetField(lab, "pointerButton", MouseButton.Right); SetField(lab, "pointerDragged", true); }
                        bool Cleared() => lab.GetType().GetField("pointerButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab) is null && !input.IsMouseCaptured;
                        StaleDrag(); lab.GetType().GetMethod("Scene_Deactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(lab, [lab, EventArgs.Empty]);
                        Check(Cleared(), "Switching away from the scene window clears drag state");
                        StaleDrag(); input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(lab), 0, Key.Escape)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                        Check(Cleared(), "Escape releases an unfinished scene interaction");
                        StaleDrag(); lab.WindowState = WindowState.Minimized;
                        Check(Cleared(), "Minimizing the scene cancels an unfinished drag"); lab.WindowState = WindowState.Normal;
                        StaleDrag(); ((ComboBox)lab.FindName("SceneControlMode")).SelectedIndex = 1;
                        Check(Cleared(), "Changing Camera / Listener control cancels an unfinished drag");
                        StaleDrag(); lab.Hide(); Check(Cleared(), "Hiding the scene releases its interaction state"); lab.Show();
                        ((DispatcherTimer)Field(lab, "timer")).Stop();
                        var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio,
                            Hrtf = HeadphoneHrtf.BuiltIn, Room = SpatialRoomOptions.Preset("Big Room") };
                        var snapshot = new SpatialRenderSnapshot(options, new(0, 0, 0), new(StereoMixSettings.Default, null), .3, HrtfEntry.BuiltIn.Hash);
                        var wave = model.CurrentAdm!;
                        long first = wave.Scene.DurationSamples * 3 / 4, frames = Math.Min(30 * 48000L, wave.Scene.DurationSamples - first);
                        using var transport = new PreviewPlaybackControl(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                        animation.Tick += (_, _) => update(first / 48000d + transport.EstimatedPosition); animation.Start();
                        double peak = 0;
                        try
                        {
                            var stats = await SpatialPcmPlayer.PlayAsync(SpatialInput.FromAdm(wave, first, frames), snapshot, transport, stop.Token,
                                outputFactory: () => new SpatialLabTests.SilentDevice(), rawDspCapture: (pcm, _) =>
                                { foreach (float value in pcm) peak = Math.Max(peak, Math.Abs(value)); });
                            metrics.Add(new { phase = "30 real seconds: saved ADM + built-in HRTF + Big Room reflections + animated scene", stats, peak,
                                output = "Real nonzero DSP processed; digital silence submitted to WASAPI to avoid audible test playback" });
                            Check(stats.EmptyBuffers == 0 && peak > 0 && stats.Frames >= frames,
                                "Actual multichannel HRTF plus Big Room DSP and scene updates run without empty output buffers in the 30-second test");
                        }
                        finally { animation.Stop(); }
                    }
                    Check(errors.Count == 0, "The resource run raises no dispatcher errors");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { baseline, passed = checks.Count, checks, metrics,
                        input = Path.GetFileName(path), sources = scene.Sources.Length, keyframes = scene.Sources.Sum(s => s.Blocks.LongLength),
                        renderTier = RenderCapability.Tier >> 16, wallClockHourAudition = false, audioPlayback = !baseline });
                }
                var task = Test(); while (!task.IsCompleted) Pump(10); task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { Mouse.Capture(null); lab?.Close(); model?.StopPlayback(); main?.Close(); Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("ADM resource checks failed", failure);
    }
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static ResourceSample Sample()
    { using var process = Process.GetCurrentProcess(); return new(process.PrivateMemorySize64, process.HandleCount, GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1)); }
    sealed record ResourceSample(long PrivateBytes, int Handles, int Gdi, int User);
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, uint flags);
    static void Pump(int ms)
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
}
