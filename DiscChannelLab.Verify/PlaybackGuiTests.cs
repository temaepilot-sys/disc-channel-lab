using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class PlaybackGuiTests
{
    public static void Run(string folder, string? source)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Pump(Test());
            }
            catch (Exception e) { failure = e; }
        });
        ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task Test()
        {
            var log = new AppLog(Path.Combine(root, "logs"));
            if (source is null)
            {
                source = Path.Combine(root, "six-channels.flac");
                await new ProcessRunner(log).RunAsync(new ToolPaths().Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
                    "aevalsrc=0.04*sin(2*PI*220*t)|0.04*sin(2*PI*330*t)|0.04*sin(2*PI*440*t)|0.04*sin(2*PI*60*t)|0.04*sin(2*PI*550*t)|0.04*sin(2*PI*660*t):s=96000:c=5.1",
                    "-t", "60", "-c:a", "flac", "-y", source], CancellationToken.None);
            }
            var model = new MainViewModel(new TrackEditsStore(log, Path.Combine(root, "metadata")));
            var main = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
            main.Show();
            var driveTimer = (DispatcherTimer)typeof(MainWindow).GetField("_driveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var playTimer = (DispatcherTimer)typeof(MainWindow).GetField("_playTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var factory = AudioNavigationService.HeadphoneOutputFactory;
            var results = new List<object>(); var options = HeadphoneSession.Current.Options;
            TimedOutput? device = null; HeadphonesWindow? lab = null;
            try
            {
                if (File.Exists(source)) await model.OpenAudioFilesAsync([source]);
                else
                {
                    model.Source = source;
                    await model.DetectInsertedDiscAsync();
                    model.SelectedPlaylist = model.Playlists.First(p => p.Format == DiscFormat.DvdVideo && p.DvdVideoTitle == 2);
                    await model.ChoosePlaylistAsync(model.SelectedPlaylist);
                    await Until(() => !model.IsBusy);
                    model.SelectedStream = model.Streams.First(s => s.Channels == 6);
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                model.SelectedTrack = model.Tracks.First();
                Console.WriteLine($"Source ready: tracks={model.Tracks.Count}, channels={model.SelectedStream?.Channels}, canPlay={model.CanPlay}");
                model.ContinuousPlayback = false;
                AudioNavigationService.HeadphoneOutputFactory = () => device = new TimedOutput();
                playTimer.Start();
                foreach (var mode in new[] { PreviewAudioMode.Stereo, PreviewAudioMode.HeadphonesHrtf })
                {
                    model.SelectedPreviewMode = mode;
                    lab = new HeadphonesWindow(model) { Owner = main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }; lab.Show();
                    Console.WriteLine($"Starting {mode}: canPlay={model.CanPlay}, status={model.Status}");
                    await model.TogglePlaybackAsync(); await Until(() => device?.HasStarted == true && model.PreviewSeconds > .1);
                    var sink = device!; var start = Stopwatch.GetTimestamp();
                    await Task.Delay(6000);
                    model.PausePlayback(); await Until(() => sink.Paused);
                    var heldFrames = sink.PlayedFrames; await Task.Delay(1000);
                    if (sink.PlayedFrames != heldFrames) throw new InvalidDataException("Paused device clock advanced.");
                    var resumes = sink.Resumes; var watch = Stopwatch.StartNew();
                    await model.TogglePlaybackAsync(); await Until(() => sink.Resumes > resumes && !sink.Paused);
                    var resumeMs = watch.Elapsed.TotalMilliseconds;
                    await Until(() => sink.PlayedFrames >= heldFrames + 480);
                    var outputClockResumeMs = watch.Elapsed.TotalMilliseconds;
                    await Task.Delay(6000);
                    model.StopPlayback(); await Until(() => sink.Disposed);
                    var duration = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    var result = new { mode, resumeMs, outputClockResumeMs, duration, sink.EmptyBufferCount, sink.MaximumWriteGapMs, sink.SubmittedFrames };
                    results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
                    if (sink.EmptyBufferCount != 0 || outputClockResumeMs >= 200)
                        throw new InvalidDataException("Real GUI playback has gaps or slow device-clock resume.");
                    lab.Close(); lab = null;
                }
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { driveTimer.Stop(); playTimer.Stop(); model.StopPlayback(); lab?.Close(); main.Close(); AudioNavigationService.HeadphoneOutputFactory = factory; HeadphoneSession.Current.SetOptions(options); }
        }
    }
    static async Task Until(Func<bool> predicate)
    {
        var start = Stopwatch.StartNew();
        while (!predicate()) { if (start.Elapsed.TotalSeconds > 20) throw new TimeoutException("GUI audio check timed out."); await Task.Delay(5); }
    }
    static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    sealed class TimedOutput : IHeadphoneOutput
    {
        readonly WasapiHeadphoneOutput device = new();
        readonly float[] silence = new float[512];
        public bool SupportsPause => true;
        public volatile bool Paused, Disposed;
        public int Resumes;
        long lastWrite;
        public double MaximumWriteGapMs;
        public void Pause() { device.Pause(); Paused = true; lastWrite = 0; }
        public void Resume() { device.Resume(); Paused = false; Resumes++; }
        public long SubmittedFrames => device.SubmittedFrames;
        public long PlayedFrames => device.PlayedFrames;
        public bool HasStarted => device.HasStarted;
        public double BufferMilliseconds => device.BufferMilliseconds;
        public double StreamLatencyMilliseconds => device.StreamLatencyMilliseconds;
        public long EmptyBufferCount => device.EmptyBufferCount;
        public void WaitForSpace(int frames, CancellationToken token) => device.WaitForSpace(frames, token);
        public void Write(float[] pcm, int frames)
        {
            var now = Stopwatch.GetTimestamp();
            if (lastWrite != 0) MaximumWriteGapMs = Math.Max(MaximumWriteGapMs, Stopwatch.GetElapsedTime(lastWrite, now).TotalMilliseconds);
            lastWrite = now;
            // Exercise the non-silent Windows render path at an inaudible level.
            for (int f = 0; f < frames * 2; f++) silence[f] = pcm[f] * 1e-8f;
            device.Write(silence, frames);
        }
        public void Drain(CancellationToken token) => device.Drain(token);
        public void Dispose() { device.Dispose(); Disposed = true; }
    }
}
