using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class SpatialHrtfSwitchTests
{
    public static void Run(string output, string fixtureRoot)
    {
        string root = Path.GetFullPath(output), fixtures = Path.GetFullPath(fixtureRoot);
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? main = null; SpatialLabWindow? lab = null; MainViewModel? model = null;
            var originalFactory = AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app); ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors = new List<Exception>(); app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                var checks = new List<string>(); var outputs = new List<CapturedOutput>();
                void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
                async Task Until(Func<bool> done, string name)
                {
                    var clock = Stopwatch.StartNew();
                    while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(name); model?.AdvancePlaybackClock(); await Task.Delay(15); }
                }
                async Task Tests()
                {
                    var kemar = await Task.Run(() => SofaLibrary.Inspect(Path.Combine(fixtures, "MIT_KEMAR_large_pinna.sofa"), CancellationToken.None));
                    var cipic = await Task.Run(() => SofaLibrary.Inspect(Path.Combine(fixtures, "CIPIC_subject_003_hrir_final.sofa"), CancellationToken.None));
                    Check(kemar.Status == "Compatible" && cipic.Status == "Compatible", "Both real SOFA datasets pass native compatibility validation");
                    string changedFile = Path.Combine(root, "changed.sofa"); File.Copy(kemar.Path, changedFile);
                    using (var edit = new FileStream(changedFile, FileMode.Append)) edit.WriteByte(1);
                    var unsupported = kemar with { Id = "unsupported-test", Name = "Unsupported fixture", Status = "Unsupported", Diagnostic = "unsupported test data" };
                    var changed = kemar with { Id = "changed-test", Name = "Changed fixture", Path = changedFile };
                    var library = new SofaLibrary(); library.Entries.AddRange([kemar, cipic, unsupported, changed]); library.Save();
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded); main.Show();
                    string demo = Path.Combine(root, "original-adm.wav"); AdmDemo.Create(demo);
                    await model.OpenAudioFilesAsync([demo]); await model.EnableAdmPlaybackAsync();
                    var session = HeadphoneSession.Current;
                    session.SetOptions(session.Options with { HeadroomDb = -15, LfeGain = .45, SmoothingMs = 0 });
                    lab = new SpatialLabWindow(model) { Owner = main, ShowActivated = false }; lab.Show();
                    var grid = (DataGrid)lab.FindName("LibraryGrid"); var active = (TextBlock)lab.FindName("ActiveHrtfText");
                    HrtfEntry Entry(string id) => grid.Items.Cast<HrtfEntry>().Single(x => x.Id == id);
                    var builtin = Entry("builtin");
                    Check(active.Text.Contains(builtin.Name), "Library header shows the HRTF actually selected for main playback");
                    AudioNavigationService.HeadphoneOutputFactory = () => { var device = new CapturedOutput(); outputs.Add(device); return device; };
                    await model.TogglePlaybackAsync(); await Until(() => outputs.Count == 1 && outputs[0].SignalSamples > 8000, "initial playback");
                    model.AdvancePlaybackClock(); double position = model.PreviewSeconds;
                    grid.SelectedItem = Entry(kemar.Id);
                    await Until(() => outputs.Count == 2 && outputs[1].SignalSamples > 8000, "KEMAR playback switch");
                    Check(model.IsPlaying && session.Options.Hrtf == HeadphoneHrtf.Sofa && session.Options.SofaPath == kemar.Path,
                        "Clicking a SOFA row while ADM plays replaces the native renderer and resumes playback");
                    Check(model.PreviewSeconds >= position - .1 && model.PreviewSeconds < position + 1,
                        "HRTF switching retains the current song position instead of returning to its start");
                    Check(active.Text.Contains(kemar.Name) && ((ComboBox)lab.FindName("HrtfA")).SelectedItem is HrtfEntry { Id: var a } && a == kemar.Id,
                        "Active HRTF display and next scene/export A follow the selected library row");
                    Check(session.Options.HeadroomDb == -15 && session.Options.LfeGain == .45 && session.Pose == new HeadPose(0, 0, 0),
                        "HRTF changes preserve gain, LFE and head pose settings");
                    await model.TogglePlaybackAsync(); int pausedOutputs = outputs.Count; double pausedPosition = model.PreviewSeconds;
                    grid.SelectedItem = Entry(cipic.Id);
                    await Until(() => session.Options.SofaPath == cipic.Path && active.Text.Contains(cipic.Name), "paused HRTF choice");
                    await Task.Delay(150);
                    Check(!model.IsPlaying && outputs.Count == pausedOutputs && Math.Abs(model.PreviewSeconds - pausedPosition) < .01,
                        "Changing SOFA while paused keeps playback paused at the same position");
                    await model.TogglePlaybackAsync(); await Until(() => outputs.Count > pausedOutputs && outputs[^1].SignalSamples > 8000, "resume new HRTF");
                    Check(model.IsPlaying && outputs.Count == pausedOutputs + 1, "Play after a paused SOFA change builds the requested HRTF instead of retaining the old handle");
                    int rejectedOutputs = outputs.Count;
                    grid.SelectedItem = Entry(unsupported.Id); await Task.Delay(250);
                    Check(session.Options.SofaPath == cipic.Path && outputs.Count == rejectedOutputs && model.IsPlaying,
                        "An unsupported SOFA leaves the playing renderer untouched");
                    grid.SelectedItem = Entry(changed.Id);
                    await Until(() => ((TextBox)lab.FindName("StatusText")).Text.Contains("Revalidate", StringComparison.OrdinalIgnoreCase), "changed hash diagnostic");
                    Check(session.Options.SofaPath == cipic.Path && outputs.Count == rejectedOutputs && model.IsPlaying,
                        "A SOFA changed since validation is rejected before pausing or replacing playback");
                    grid.SelectedItem = builtin; await Until(() => outputs.Count > rejectedOutputs && outputs[^1].SignalSamples > 8000, "built-in switch");
                    Check(session.Options.Hrtf == HeadphoneHrtf.BuiltIn && active.Text.Contains(builtin.Name), "Built-in HRTF is selectable during playback too");
                    int beforeRapid = outputs.Count;
                    grid.SelectedItem = Entry(kemar.Id); grid.SelectedItem = Entry(cipic.Id);
                    await Until(() => outputs.Count > beforeRapid && outputs[^1].SignalSamples > 8000, "rapid switch");
                    Check(outputs.Count == beforeRapid + 1 && session.Options.SofaPath == cipic.Path, "Rapid row selection loads only the final HRTF");
                    model.StopPlayback(); await Task.Delay(150);

                    // Compare the actual production output for identical source time and settings, not just option flags.
                    async Task<float[]> Capture(HrtfEntry entry)
                    {
                        await model.ApplySpatialHrtfAsync(entry); await model.SeekAsync(1);
                        int count = outputs.Count; await model.TogglePlaybackAsync();
                        await Until(() => outputs.Count > count && outputs[^1].SampleCount >= 65536, "capture actual HRTF PCM");
                        model.PausePlayback(); float[] pcm = outputs[^1].Copy(65536); model.StopPlayback(); await Task.Delay(100); return pcm;
                    }
                    var first = await Capture(kemar); var second = await Capture(cipic);
                    double difference = Math.Sqrt(first.Zip(second).Average(x => Math.Pow(x.First - x.Second, 2)));
                    Check(difference > .00001 && first.All(float.IsFinite) && second.All(float.IsFinite),
                        $"Actual main-player PCM changes between real SOFAs at identical source time (RMS difference {difference:R})");
                    model.SelectedPreviewMode = PreviewAudioMode.Stereo;
                    Check(active.Text.Contains("HRTF off"), "Active display makes ordinary Stereo bypass visible");
                    grid.SelectedItem = Entry(kemar.Id); await Until(() => model.SelectedPreviewMode == PreviewAudioMode.HeadphonesHrtf, "Stereo to HRTF");
                    Check(session.Options.Method == HeadphoneMethod.SteamAudio && !model.IsPlaying,
                        "Selecting a SOFA enables HRTF even when the main player was in Stereo, without starting a stopped song");
                    var saved = JsonSerializer.Deserialize<HeadphoneOptions>(File.ReadAllText(AppDataPaths.Current.PreferencePath("headphones.json")))!;
                    Check(saved.SofaPath == kemar.Path && saved.Hrtf == HeadphoneHrtf.Sofa, "The applied SOFA is remembered in the private headphone settings");
                    string normal = Path.Combine(root, "normal-5.1.wav");
                    await SpatialRendering.RunAsync(new ToolPaths().Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
                        "aevalsrc=0.05*sin(2*PI*220*t)|0.04*sin(2*PI*330*t)|0.03*sin(2*PI*440*t)|0|0.04*sin(2*PI*550*t)|0.04*sin(2*PI*660*t):s=48000:d=8:c=5.1",
                        "-c:a", "pcm_s24le", normal], CancellationToken.None);
                    await model.OpenAudioFilesAsync([normal]);
                    Check(model.CurrentAdm is null && model.SelectedStream?.Channels == 6, "Ordinary 5.1 WAV remains on the existing non-ADM playback path");
                    int beforeNormal = outputs.Count; await model.TogglePlaybackAsync();
                    await Until(() => outputs.Count > beforeNormal && outputs[^1].SignalSamples > 8000, "normal headphone playback");
                    beforeNormal = outputs.Count; grid.SelectedItem = Entry(cipic.Id);
                    await Until(() => outputs.Count > beforeNormal && outputs[^1].SignalSamples > 8000, "normal SOFA switch");
                    Check(model.IsPlaying && session.Options.SofaPath == cipic.Path && outputs.Count == beforeNormal + 1,
                        "SOFA selection also rebuilds and resumes the normal multichannel headphone renderer");
                    model.StopPlayback(); await Task.Delay(100);
                    await Task.Delay(100); Save(lab, Path.Combine(root, "sofa-selection-applied.png"));
                    Check(errors.Count == 0 && outputs.All(x => x.Finite), "Real WPF interaction and all device-bound PCM remain free of exceptions and nonfinite samples");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, actualOutputDifferenceRms = difference,
                        outputSessions = outputs.Count, output = "Real WASAPI clock, captured pre-device PCM, silence sent to hardware" });
                }
                var task = Tests(); while (!task.IsCompleted) Pump(15); task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { model?.StopPlayback(); lab?.Close(); main?.Close(); AudioNavigationService.HeadphoneOutputFactory = originalFactory; Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Spatial HRTF switching tests failed", failure);
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
    sealed class CapturedOutput : IHeadphoneOutput
    {
        readonly SpatialLabTests.SilentDevice device = new(); readonly List<float> samples = []; readonly object gate = new();
        int signalSamples; public int SignalSamples => Volatile.Read(ref signalSamples);
        public int SampleCount { get { lock (gate) return samples.Count; } }
        public float[] Copy(int count) { lock (gate) return samples.Take(count).ToArray(); }
        public bool Finite { get; private set; } = true;
        public long SubmittedFrames => device.SubmittedFrames; public long PlayedFrames => device.PlayedFrames;
        public double BufferMilliseconds => device.BufferMilliseconds; public double StreamLatencyMilliseconds => device.StreamLatencyMilliseconds;
        public bool HasStarted => device.HasStarted; public long EmptyBufferCount => device.EmptyBufferCount; public bool SupportsPause => true;
        public void Pause() => device.Pause(); public void Resume() => device.Resume();
        public void WaitForSpace(int frames, CancellationToken token) => device.WaitForSpace(frames, token);
        public void Write(float[] pcm, int frames)
        {
            lock (gate) for (int i = 0; i < frames * 2; i++) { float v = pcm[i]; samples.Add(v); Finite &= float.IsFinite(v); if (Math.Abs(v) > 1e-7) signalSamples++; }
            device.Write(pcm, frames);
        }
        public void Drain(CancellationToken token) => device.Drain(token); public void Dispose() => device.Dispose();
    }
}
