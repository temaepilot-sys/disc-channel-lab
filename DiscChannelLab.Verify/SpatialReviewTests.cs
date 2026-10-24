using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class SpatialReviewTests
{
    public static void Run(string output, string fullAdm)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? main = null; SpatialLabWindow? lab = null; MainViewModel? model = null;
            var originalFactory = AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                ThemeService.SetDark(true); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var checks = new List<string>(); var errors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
                void ImpulseChecks()
                {
                    var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.Direct, HeadroomDb = 0, ShadowStrength = 0, RearCutDb = 0, SmoothingMs = 0 };
                    var room = new SpatialRoomOptions { Mode = "Studio", LowAbsorption = 0, MidAbsorption = 0, HighAbsorption = 0 };
                    float[] Render(SpatialRoomOptions r, bool lfe = false)
                    {
                        var scene = new SpatialScene(48000, 1024, [new("test", "test", 0, SpatialSourceKind.ChannelBed, 0, 1024,
                            [new(0, 1024, lfe ? new Vector3(0, -10, 0) : new Vector3(0, 0, 1))], lfe)], "impulse", []);
                        using var dsp = new HeadphoneRenderer(["test"], options with { Room = r }, scene, clampOutput: false);
                        var result = new List<float>(); var block = new float[256]; block[0] = .25f; var stereo = new float[512];
                        for (int i = 0; i < 4; i++) { dsp.Render([block], stereo, new(0, 0, 0), options with { Room = r }, new(StereoMixSettings.Default, null), 1); result.AddRange(stereo); Array.Clear(block); }
                        return result.ToArray();
                    }
                    var dry = Render(new()); var wet = Render(room); var only = Render(room with { SoloReflections = true });
                    Check(dry[0] != 0 && only[0] == 0, "Reflections-only removes the direct impulse at sample zero");
                    int first = Enumerable.Range(0, only.Length / 2).First(i => Math.Abs(only[i * 2]) + Math.Abs(only[i * 2 + 1]) > 1e-9);
                    // Independent front-wall image: source z=1.2, wall z=1.75, image z=2.3; excess=1.1m.
                    double expected = 1.1 / 343 * 48000;
                    Check(Math.Abs(first - expected) <= 1, $"First reflected impulse follows the geometric excess delay ({first} samples, expected {expected:R})");
                    Check(wet.Zip(dry.Zip(only)).Max(x => Math.Abs(x.First - x.Second.First - x.Second.Second)) < 1e-7,
                        "Wet PCM equals dry plus isolated reflections");
                    Check(dry.SequenceEqual(Render(room with { Mode = "Pure HRTF" })), "Reflections OFF restores exactly the unmodified dry PCM");
                    var softer = Render(room with { SoloReflections = true, ReflectionDb = -24 });
                    double scale = Math.Pow(10, -6d / 20);
                    double scaleError = only.Zip(softer).Max(x => Math.Abs(x.Second - x.First * scale));
                    Check(scaleError < 1e-7, $"Reflection level changes produce the expected dB amplitude ratio (error {scaleError:R}, peaks {only.Max(Math.Abs):R}/{softer.Max(Math.Abs):R})");
                    Check(Render(room with { SoloReflections = true, LowAbsorption = 1, MidAbsorption = 1, HighAbsorption = 1 }).All(x => x == 0),
                        "Full absorption removes reflected PCM without affecting the dry path");
                    Check(Render(room with { SoloReflections = true }, true).All(x => x == 0) && Render(room, true).Any(x => x != 0),
                        "Reflections-only excludes LFE; ordinary wet playback preserves LFE without applying room bounds to its unused position");
                }
                async Task Until(Func<bool> done, string label)
                {
                    var watch = Stopwatch.StartNew();
                    while (!done()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(label + " / " + model?.Status); model?.AdvancePlaybackClock(); await Task.Delay(15); }
                }
                async Task Tests()
                {
                    ImpulseChecks();
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded); main.Show();
                    await model.OpenAudioFilesAsync([fullAdm]);
                    await model.EnableAdmPlaybackAsync();
                    lab = new SpatialLabWindow(model) { Owner = main, ShowActivated = false }; lab.Show();
                    var pages = (TabControl)lab.FindName("Pages"); var mode = (ComboBox)lab.FindName("RoomMode");
                    string[] removed = ["Play scene / current track", "Pause / Resume", "Seek scene (s)", "Export 24-bit FLAC…", "Apply selected HRTF", "Apply room to main player", "Save lab settings", "Create test scene…"];
                    var captions = Descendants<Button>(lab).Select(b => b.Content as string).ToArray();
                    Check(!captions.Any(c => removed.Contains(c!)) && lab.FindName("SeekSeconds") is null && lab.FindName("SourceKind") is null,
                        "Duplicate scene transport, apply/save and FLAC export controls are removed from Spatial Lab");
                    Check(lab.FindName("SceneTime") is ProgressBar, "ADM timeline is a read-only follower of the main player");
                    await model.SeekAsync(20); await Task.Delay(150);
                    Check(Math.Abs(((ProgressBar)lab.FindName("SceneTime")).Value - 20) < .1, "ADM inspector follows the main player's seek position");
                    pages.SelectedIndex = 2;
                    int sessions = 0; AudioNavigationService.HeadphoneOutputFactory = () => { Interlocked.Increment(ref sessions); return new SpatialLabTests.SilentDevice(); };
                    await model.TogglePlaybackAsync(); await Until(() => sessions == 1, "start real ADM output");
                    double position = model.PreviewSeconds;
                    mode.SelectedIndex = 1; await Until(() => sessions == 2 && HeadphoneSession.Current.Options.Room.Mode == "Studio", "automatic Studio apply");
                    Check(model.IsPlaying && model.PreviewSeconds >= position - .1 && ((TextBlock)lab.FindName("ActiveRoomText")).Text.Contains("ON"),
                        "Studio applies automatically during real ADM playback and resumes at the held position");
                    ((TextBox)lab.FindName("ReflectionDb")).Text = "-9";
                    await Until(() => sessions == 3 && HeadphoneSession.Current.Options.Room.ReflectionDb == -9, "reflection strength update");
                    Check(((Slider)lab.FindName("ReflectionLevel")).Value == -9, "Numeric reflection strength and slider stay in sync and reach the renderer");
                    ((CheckBox)lab.FindName("SoloReflections")).IsChecked = true;
                    await Until(() => sessions == 4 && HeadphoneSession.Current.Options.Room.SoloReflections, "reflections-only apply");
                    Check(((TextBlock)lab.FindName("ActiveRoomText")).Text.Contains("REFLECTIONS ONLY"), "Isolated reflections are explicitly identified in the active output status");
                    var beforeInvalid = HeadphoneSession.Current.Options.Room;
                    ((TextBox)lab.FindName("ListenerHeight")).Text = "10";
                    await Until(() => ((TextBox)lab.FindName("StatusText")).Text.StartsWith("Not applied"), "invalid draft rejected");
                    Check(HeadphoneSession.Current.Options.Room == beforeInvalid && sessions == 4 && model.IsPlaying, "Invalid room edits leave the active renderer and playback intact");
                    mode.SelectedIndex = 2; await Until(() => sessions == 5 && HeadphoneSession.Current.Options.Room.Mode == "Listening Room", "Listening Room preset");
                    Check(HeadphoneSession.Current.Options.Room.ReflectionDb == -12 && HeadphoneSession.Current.Options.Room.Width == 6,
                        "Listening Room has a distinct stronger preset than Studio");
                    await Task.Delay(150);
                    Check(((TextBlock)lab.FindName("RoomDiagnosticText")).Text.Contains("ms") && ((TextBlock)lab.FindName("RoomDiagnosticText")).Text.Contains("dB"),
                        "Applied reflection path delays and attenuated levels are visible");
                    Save(lab, Path.Combine(root, "early-reflections-applied.png"));
                    mode.SelectedIndex = 0; await Until(() => sessions == 6 && !HeadphoneSession.Current.Options.Room.Enabled, "OFF apply");
                    Check(model.IsPlaying && ((TextBlock)lab.FindName("ActiveRoomText")).Text.Contains("OFF"), "Pure HRTF removes reflections in the active playback renderer");
                    await model.TogglePlaybackAsync(); int pausedSessions = sessions;
                    mode.SelectedIndex = 1; await Until(() => HeadphoneSession.Current.Options.Room.Enabled, "paused room apply"); await Task.Delay(100);
                    Check(!model.IsPlaying && sessions == pausedSessions, "Room edits preserve a paused transport until the next main Play");
                    model.StopPlayback(); await Task.Delay(100);
                    string normal = Path.Combine(root, "normal-5.1.wav"); var tools = new ToolPaths();
                    await SpatialRendering.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
                        "aevalsrc=0.05*sin(2*PI*220*t)|0.04*sin(2*PI*330*t)|0.03*sin(2*PI*440*t)|0|0.04*sin(2*PI*550*t)|0.04*sin(2*PI*660*t):s=48000:d=6:c=5.1", "-c:a", "pcm_s24le", normal], CancellationToken.None);
                    await model.OpenAudioFilesAsync([normal]); await Task.Delay(100);
                    Check(((DataGrid)lab.FindName("ObjectsGrid")).Items.Count == 0, "Opening ordinary audio clears the previous ADM object display");
                    model.SaveBinauralFlac = true; model.IsHighResSelected = true; model.OutputFolder = Path.Combine(root, "flac");
                    Check(model.CanConvert && model.HighResOptionText.Contains("48 kHz"), "Main FLAC controls support shared headphone / reflection rendering at 24/48 for ordinary audio too");
                    var input = await model.PrepareSpatialInputAsync(0, model.PreviewMax, CancellationToken.None);
                    var snapshot = model.SpatialSnapshot(HrtfEntry.BuiltIn, HeadphoneSession.Current.Pose, HeadphoneSession.Current.Options.Room);
                    var reference = await SpatialRendering.RenderAsync(input, snapshot, false, CancellationToken.None);
                    Check(await model.ConvertAsync(), "The main Save FLAC path exports the binaural / reflection result after removal of the Lab FLAC button");
                    string flac = Directory.GetFiles(model.OutputFolder, "*.flac", SearchOption.AllDirectories).Single(), decoded = Path.Combine(root, "export.f32");
                    await SpatialRendering.RunAsync(tools.Ffmpeg, ["-v", "error", "-i", flac, "-f", "f32le", decoded], CancellationToken.None);
                    float[] expected = Floats(reference.PcmPath), actual = Floats(decoded); double error = expected.Zip(actual).Max(x => Math.Abs(x.First - x.Second));
                    Check(expected.Length == actual.Length && error < 5e-7, $"Consolidated FLAC matches the shared spatial DSP within 24-bit quantization (max {error:R})");
                    model.SaveMultichannelFlac = true;
                    Check(!model.SaveBinauralFlac && model.SaveMultichannelFlac, "Source multichannel export and binaural export remain mutually exclusive");
                    model.SaveBinauralFlac = true;
                    Check(!model.SaveMultichannelFlac && model.SaveBinauralFlac, "Selecting binaural export clears incompatible source export modes");
                    // Verify the user-facing OFF/ON preparation route, not only the individual DSP primitives.
                    ((TextBox)lab.FindName("IntervalLength")).Text = "5";
                    ((Button)lab.FindName("RoomCompareButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((TextBox)lab.FindName("StatusText")).Text.StartsWith("Room comparison ready"), "room A/B preparation");
                    var comparison = (ComparisonAudio)typeof(SpatialLabWindow).GetField("comparison", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lab)!;
                    Check(!comparison.A.Snapshot.Options.Room.Enabled && comparison.B.Snapshot.Options.Room.Enabled && !comparison.B.Snapshot.Options.Room.SoloReflections,
                        "OFF/ON A/B uses dry A and direct-plus-reflections B, even after the isolation diagnostic");
                    Check(comparison.A.Snapshot.HrtfHash == comparison.B.Snapshot.HrtfHash && comparison.A.Input == comparison.B.Input &&
                        !File.ReadAllBytes(comparison.A.PcmPath).SequenceEqual(File.ReadAllBytes(comparison.B.PcmPath)),
                        "Room A/B fixes source, HRTF and pose and produces different rendered PCM");
                    Check(!model.IsPlaying && model.PreviewSeconds < .01, "A/B preparation preserves the main song position without starting normal playback");
                    Save(lab, Path.Combine(root, "room-off-on-comparison.png")); Save(main, Path.Combine(root, "main-unified-flac.png"));
                    lab.Close(); lab = null;
                    Check(File.Exists(AppDataPaths.Current.PreferencePath("profiles/spatial-lab.json")), "Lab comparison settings save automatically without a duplicate Save button");
                    Check(errors.Count == 0, "No WPF dispatcher exceptions while changing live settings and preparing exports");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, maxExportError = error,
                        fullAdm = Path.GetFullPath(fullAdm), realTimeOutput = "Short silent WASAPI playback; no human listening evaluation" });
                }
                var task = Tests(); while (!task.IsCompleted) Pump(15); task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { model?.StopPlayback(); lab?.Close(); main?.Close(); AudioNavigationService.HeadphoneOutputFactory = originalFactory; Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Spatial review tests failed", failure);
    }
    static float[] Floats(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    static void Save(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
