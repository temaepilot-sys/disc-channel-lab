using Disc2Flac;
using System.IO;
using System.Text.Json;

internal static class HeadphoneBufferTests
{
    public static async Task RunAsync(string folder, string? realFile = null)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        using (var queue = new HeadphonePcmBuffer(8))
        {
            for (int i = 0; i < HeadphonePcmBuffer.PrebufferBlocks - 1; i++)
            { var block = queue.Rent(); block[0] = (byte)i; queue.Add(block, CancellationToken.None); }
            Check(!queue.Ready.IsCompleted, "A partial startup buffer cannot begin audio output");
            var last = queue.Rent(); last[0] = (byte)(HeadphonePcmBuffer.PrebufferBlocks - 1); queue.Add(last, CancellationToken.None);
            queue.WaitUntilReady(CancellationToken.None); queue.Complete();
            Check(queue.ReadAll(CancellationToken.None).Select(b => b[0]).SequenceEqual(Enumerable.Range(0, HeadphonePcmBuffer.PrebufferBlocks).Select(i => (byte)i)),
                "Startup prebuffer preserves every block in source order");
        }
        using (var queue = new HeadphonePcmBuffer(8))
        {
            queue.Add(new byte[8], CancellationToken.None); queue.Complete();
            queue.WaitUntilReady(CancellationToken.None);
            Check(queue.ReadAll(CancellationToken.None).Count() == 1, "Short tracks start at EOF without waiting for a full prebuffer");
        }
        using (var queue = new HeadphonePcmBuffer(8))
        {
            queue.Complete(new IOException("decoder fixture failed"));
            bool failed = false;
            try { queue.WaitUntilReady(CancellationToken.None); } catch (IOException ex) { failed = ex.Message == "decoder fixture failed"; }
            Check(failed, "Decoder failure is reported before opening an output device");
        }
        using (var queue = new HeadphonePcmBuffer(8))
        using (var cancel = new CancellationTokenSource())
        {
            var writer = Task.Run(() => { for (int i = 0; i <= HeadphonePcmBuffer.CapacityBlocks; i++) queue.Add(new byte[8], cancel.Token); });
            for (int i = 0; i < 100 && queue.Count < HeadphonePcmBuffer.CapacityBlocks; i++) await Task.Delay(10);
            Check(queue.Count == HeadphonePcmBuffer.CapacityBlocks && !writer.IsCompleted, "PCM read-ahead has a fixed memory bound and backpressure");
            cancel.Cancel(); bool canceled = false;
            try { await writer.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Canceling playback releases a decoder blocked on a full queue");
        }
        await HeadphoneAudioThread.Run(() =>
        {
            using var scheduling = new HeadphoneAudioThread.Scheduling();
            Check(!Thread.CurrentThread.IsThreadPoolThread && Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA,
                "Audio rendering has a dedicated MTA thread independent of decoder continuations");
            using var device = new WasapiHeadphoneOutput();
            var silence = new float[HeadphoneRenderer.BlockSize * 2];
            device.Write(silence, HeadphoneRenderer.BlockSize);
            if (device.StartupFrames > HeadphoneRenderer.BlockSize)
            {
                Thread.Sleep(45); // Longer than the device buffer: a premature start would already have run dry.
                Check(!device.HasStarted && device.Padding == HeadphoneRenderer.BlockSize,
                    "Windows output holds its first block without playing while initial buffering is incomplete");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int i = 1; i < 16; i++) { device.WaitForSpace(HeadphoneRenderer.BlockSize, timeout.Token); device.Write(silence, HeadphoneRenderer.BlockSize); }
            device.Drain(timeout.Token);
            Check(device.HasStarted && device.PlayedFrames == 16 * HeadphoneRenderer.BlockSize, "Primed Windows output plays and drains every submitted frame");
        });
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var synthetic = Path.Combine(root, "six-channels-88200.flac");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
            "aevalsrc=0.05*sin(2*PI*220*t)|0.05*sin(2*PI*330*t)|0.05*sin(2*PI*440*t)|0.05*sin(2*PI*60*t)|0.05*sin(2*PI*550*t)|0.05*sin(2*PI*660*t):s=88200:c=5.1",
            "-t", "3", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-y", synthetic], CancellationToken.None);
        var session = HeadphoneSession.Current; var previous = session.Options; var factory = AudioNavigationService.HeadphoneOutputFactory;
        var runs = new List<object>();
        try
        {
            foreach (var file in realFile is null ? new[] { synthetic } : new[] { synthetic, Path.GetFullPath(realFile) })
            {
                var disc = await service.AnalyzeAudioFilesAsync([file], CancellationToken.None); var title = disc.Playlists.Single();
                var inspected = await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None); var stream = inspected.Streams.Single();
                var navigation = new AudioNavigationService(tools, runner, probe, log);
                foreach (var method in Enum.GetValues<HeadphoneMethod>())
                {
                    session.SetOptions(new HeadphoneOptions { Enabled = true, Method = method });
                    SilentWindowsOutput? sink = null;
                    AudioNavigationService.HeadphoneOutputFactory = () => sink = new SilentWindowsOutput();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var chunks = new List<PreviewPcmChunk>();
                    await navigation.PlayAsync(disc, title, stream, 0, Math.Min(title.DurationTicks, 135000), timeout.Token,
                        liveMix: () => new(StereoMixSettings.Default, null), mixedLevels: (_, _) => { }, visualization: new(() => true, chunks.Add));
                    Check(sink is { Disposed: true, SameThread: true } && sink.SubmittedFrames > 0,
                        method + " keeps initialization, PCM writes, drain and disposal on the same Windows audio thread");
                    Check(sink!.Finite && sink.Peak > .0001f && chunks.Count > 0,
                        method + " processes audible finite PCM while the test device itself receives only silence");
                    Check(sink.EmptyBufferCount == 0, method + $" has no empty device buffers with meters and visualization (empty={sink.EmptyBufferCount}, max write gap={sink.MaximumWriteGapMs:0.0} ms)");
                    runs.Add(new { source = Path.GetFileName(file), method, sink.EmptyBufferCount, sink.SubmittedFrames, sink.BufferMilliseconds, sink.MaximumWriteGapMs });
                }
            }
            var disc2 = await service.AnalyzeAudioFilesAsync([synthetic], CancellationToken.None); var title2 = disc2.Playlists.Single();
            var stream2 = (await service.AnalyzePlaylistAsync(disc2, title2, CancellationToken.None)).Streams.Single();
            AudioNavigationService.HeadphoneOutputFactory = () => throw new IOException("device fixture failed");
            bool deviceFailure = false;
            try { await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc2, title2, stream2, 0, title2.DurationTicks, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException ex) { deviceFailure = ex.Message == "device fixture failed"; }
            Check(deviceFailure, "A failed output device cancels the bounded decoder without deadlocking");
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = factory; session.SetOptions(previous); }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, runs }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class SilentWindowsOutput : IHeadphoneOutput
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly WasapiHeadphoneOutput _device = new();
        private readonly float[] _silence = new float[HeadphoneRenderer.BlockSize * 2];
        public bool SameThread { get; private set; } = true;
        public bool Disposed { get; private set; }
        public bool Finite { get; private set; } = true;
        public float Peak { get; private set; }
        private long _lastWrite;
        public double MaximumWriteGapMs { get; private set; }
        public long SubmittedFrames => _device.SubmittedFrames;
        public long PlayedFrames => _device.PlayedFrames;
        public double BufferMilliseconds => _device.BufferMilliseconds;
        public bool HasStarted => _device.HasStarted;
        public long EmptyBufferCount => _device.EmptyBufferCount;
        private void CheckThread() => SameThread &= Environment.CurrentManagedThreadId == _owner;
        public void WaitForSpace(int frames, CancellationToken token) { CheckThread(); _device.WaitForSpace(frames, token); }
        public void Write(float[] samples, int frames)
        {
            CheckThread();
            foreach (var sample in samples) { Finite &= float.IsFinite(sample); Peak = Math.Max(Peak, Math.Abs(sample)); }
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastWrite != 0) MaximumWriteGapMs = Math.Max(MaximumWriteGapMs, System.Diagnostics.Stopwatch.GetElapsedTime(_lastWrite, now).TotalMilliseconds);
            _lastWrite = now;
            _device.Write(_silence, frames);
        }
        public void Drain(CancellationToken token) { CheckThread(); _device.Drain(token); }
        public void Dispose() { CheckThread(); Disposed = true; _device.Dispose(); }
    }
}
