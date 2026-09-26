using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

internal static class MeterSeekRegressionTests
{
    public static async Task PacingAsync()
    {
        async Task CheckAsync(bool mix)
        {
            var channels = mix ? 6 : 2;
            using var source = new DelayedPcmStream(new byte[48000 * channels * sizeof(short) / 2]);
            using var output = new MemoryStream();
            var audible = new Stopwatch();
            if (mix)
                await StereoPreviewMixer.CopyAsync(source, output, new AudioStreamInfo
                { Index = 0, Codec = "pcm_s16le", SampleRate = 48000, Channels = 6, ChannelLayout = "5.1", BitDepth = 16 },
                    () => new PreviewMixState(StereoMixSettings.Default, null), () => 1, CancellationToken.None, audible.Start);
            else
                await AudioNavigationService.CopyPcmWithVolumeAsync(source, output, () => 1, CancellationToken.None, audible.Start);
            if (output.Length != 48000 * 2 * sizeof(short) / 2 || audible.Elapsed.TotalSeconds < 0.35)
                throw new InvalidDataException($"{(mix ? "Live mixer" : "Stereo volume")}: delayed decoding caused a PCM burst; " +
                    $"0.5 seconds submitted in {audible.Elapsed.TotalSeconds:0.000}s after first audio.");
            Console.WriteLine($"{(mix ? "Live mixer" : "Stereo volume")}: 2.8s decoder delay; PCM paced over {audible.Elapsed.TotalSeconds:0.000}s.");
        }
        await Task.WhenAll(CheckAsync(false), CheckAsync(true));
    }

    private sealed class DelayedPcmStream(byte[] data) : MemoryStream(data)
    {
        private bool _started;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started) { _started = true; await Task.Delay(2800, cancellationToken); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    public static void Disc(string root)
    {
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            async Task RunAsync()
            {
                var model = new MainViewModel(new TrackEditsStore(new AppLog(), Path.Combine(Path.GetTempPath(),
                    "DiscChannelLab-meter-" + Guid.NewGuid().ToString("N"))));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) => model.AdvancePlaybackClock();
                try
                {
                    await model.OpenSourceAsync(root);
                    model.StopInspection();
                    await model.InspectionTask;
                    model.SelectedTrack = model.Tracks.Where(x => x.DurationSeconds > 310).MaxBy(x => x.DurationSeconds)
                        ?? throw new InvalidDataException("A track longer than 310 seconds is required.");
                    model.ContinuousPlayback = false;
                    var changed = 0;
                    foreach (var meter in model.ChannelMeters)
                        meter.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChannelMeter.PeakPercent) && meter.PeakPercent > 0) changed++; };
                    var clock = (Stopwatch)typeof(MainViewModel).GetField("_playClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                    var queue = (Queue<(double Position, double[] Peaks, double[] Rms)>)typeof(MainViewModel)
                        .GetField("_meterFrames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                    timer.Start();
                    await model.SeekAsync(5);
                    await model.TogglePlaybackAsync();
                    foreach (var position in new[] { 5d, 185d, 305d, 40d })
                    {
                        if (position != 5) await model.SeekAsync(position);
                        var timeout = Stopwatch.StartNew();
                        while (!clock.IsRunning && model.IsPlaying && timeout.Elapsed.TotalSeconds < 20) await Task.Delay(20);
                        if (!clock.IsRunning) throw new InvalidDataException($"Playback did not start at {position}s: {model.Status}");
                        changed = 0;
                        await Task.Delay(1600);
                        var ahead = queue.TryPeek(out var frame) ? frame.Position - model.PreviewSeconds : 0;
                        Console.WriteLine($"Seek {position:0}s: {changed} meter updates; next frame {ahead:0.000}s ahead; {model.Status}");
                        if (changed < 10 || ahead > 1)
                            throw new InvalidDataException($"Meters stopped or ran ahead after seeking to {position}s.");
                    }
                }
                finally { timer.Stop(); model.Cancel(); model.DetachLanguage(); }
            }
            try
            {
                var task = RunAsync();
                if (!task.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
