using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;

internal static class PlaybackTransportTests
{
    public static void Run(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { Pump(Test()); } catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;

        async Task Test()
        {
            var checks = new List<string>(); var latencies = new List<double>();
            void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
            var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
            var file = Path.Combine(root, "six-channels.flac");
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
                "aevalsrc=0.04*sin(2*PI*220*t)|0.04*sin(2*PI*330*t)|0.04*sin(2*PI*440*t)|0.04*sin(2*PI*60*t)|0.04*sin(2*PI*550*t)|0.04*sin(2*PI*660*t):s=88200:c=5.1(side)",
                "-t", "30", "-c:a", "flac", "-y", file], CancellationToken.None);
            var model = new MainViewModel(new TrackEditsStore(log, Path.Combine(root, "metadata")));
            var factory = AudioNavigationService.HeadphoneOutputFactory; var original = HeadphoneSession.Current.Options;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; timer.Tick += (_, _) => model.AdvancePlaybackClock();
            int opens = 0; SilentPauseOutput? sink = null;
            try
            {
                await model.OpenAudioFilesAsync([file]); model.ContinuousPlayback = false;
                AudioNavigationService.HeadphoneOutputFactory = () => { Interlocked.Increment(ref opens); return sink = new SilentPauseOutput(); };
                // A viewer that connects but never reads must not block audio or transport.
                model.Visualization.Start();
                using var blockedViewer = new NamedPipeClientStream(".", model.Visualization.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
                timer.Start();
                foreach (var mode in new[] { PreviewAudioMode.Stereo, PreviewAudioMode.HeadphonesHrtf })
                {
                    model.SelectedPreviewMode = mode;
                    await model.TogglePlaybackAsync();
                    await Until(() => sink?.HasStarted == true && model.PreviewSeconds > .1);
                    int initialOpens = opens;
                    for (int repeat = 0; repeat < 4; repeat++)
                    {
                        model.PausePlayback(); await Until(() => sink?.Paused == true);
                        var held = sink!; var played = held.PlayedFrames; var resumes = held.ResumeCalls;
                        await Task.Delay(120);
                        Check(!model.IsPlaying && held.PlayedFrames == played && !held.Disposed, mode + ": Pause retains the device and freezes its actual output clock");
                        Check(!model.CanClearCache, mode + ": a paused decoder keeps its audio cache protected");
                        if (!blockedViewer.IsConnected)
                        {
                            await blockedViewer.ConnectAsync(3000);
                            await Until(() => model.Visualization.IsConnected);
                        }
                        var beforeWrites = held.Writes; var watch = Stopwatch.StartNew();
                        await model.TogglePlaybackAsync();
                        await Until(() => held.ResumeCalls > resumes && held.Writes > beforeWrites);
                        latencies.Add(watch.Elapsed.TotalMilliseconds);
                        Check(opens == initialOpens && model.IsPlaying && watch.Elapsed.TotalMilliseconds < 200,
                            mode + $": warm resume {watch.Elapsed.TotalMilliseconds:0.0} ms without reopening output or decoding again");
                        await Task.Delay(80);
                    }
                    Check(sink!.EmptyBufferCount == 0, mode + ": no device gaps with a permanently blocked viewer");
                    if (mode == PreviewAudioMode.Stereo)
                    {
                        model.PausePlayback(); await Until(() => sink?.Paused == true);
                        model.SelectedPreviewChannel = model.PreviewChannels.Single(c => c.Code == "FL");
                        await model.ChangePreviewChannelAsync();
                        await model.TogglePlaybackAsync();
                        await Until(() => sink!.LastStereoDifference < 1e-8 && sink.LastPeak > .005);
                        Check(opens == initialOpens, "A channel selected while paused reaches resumed audio without recreating the decoder");
                        model.SelectedPreviewChannel = model.PreviewChannels[0];
                        await model.ChangePreviewChannelAsync();
                    }
                    model.PausePlayback(); await Until(() => sink?.Paused == true);
                    var previous = sink;
                    await model.SeekAsync(2); await model.TogglePlaybackAsync();
                    await Until(() => sink != previous && sink?.HasStarted == true && model.PreviewSeconds >= 2);
                    Check(previous!.Disposed && model.PreviewSeconds < 4, mode + ": seek while paused discards the held stream and starts at the requested position");
                    model.PausePlayback(); await Until(() => sink?.Paused == true);
                    model.StopPlayback(); await Until(() => sink?.Disposed == true);
                    model.AdvancePlaybackClock();
                    Check(model.PreviewSeconds == 0 && !model.IsPlaying, mode + ": Stop from pause releases the device and preserves the reset position");
                }
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks, resumeMilliseconds = latencies }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { timer.Stop(); model.Cancel(); model.DetachLanguage(); model.Visualization.Dispose(); AudioNavigationService.HeadphoneOutputFactory = factory; HeadphoneSession.Current.SetOptions(original); }
        }
    }
    static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("Playback test timed out."); await Task.Delay(5); }
    }
    static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    sealed class SilentPauseOutput : IHeadphoneOutput
    {
        readonly WasapiHeadphoneOutput device = new();
        readonly float[] silence = new float[512];
        public volatile bool Paused, Disposed;
        public int ResumeCalls, Writes;
        public double LastStereoDifference = 1, LastPeak;
        public bool SupportsPause => true;
        public void Pause() { device.Pause(); Paused = true; }
        public void Resume() { device.Resume(); Paused = false; Interlocked.Increment(ref ResumeCalls); }
        public long SubmittedFrames => device.SubmittedFrames;
        public long PlayedFrames => device.PlayedFrames;
        public double BufferMilliseconds => device.BufferMilliseconds;
        public double StreamLatencyMilliseconds => device.StreamLatencyMilliseconds;
        public bool HasStarted => device.HasStarted;
        public long EmptyBufferCount => device.EmptyBufferCount;
        public void WaitForSpace(int frames, CancellationToken token) => device.WaitForSpace(frames, token);
        public void Write(float[] samples, int frames)
        {
            double difference = 0, peak = 0;
            for (int f = 0; f < frames; f++)
            {
                difference = Math.Max(difference, Math.Abs(samples[f * 2] - samples[f * 2 + 1]));
                peak = Math.Max(peak, Math.Abs(samples[f * 2]));
            }
            LastStereoDifference = difference; LastPeak = peak;
            device.Write(silence, frames); Interlocked.Increment(ref Writes);
        }
        public void Drain(CancellationToken token) => device.Drain(token);
        public void Dispose() { device.Dispose(); Disposed = true; }
    }
}
