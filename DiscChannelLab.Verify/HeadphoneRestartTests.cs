using Disc2Flac;
using System.IO;
using System.Text.Json;

internal static class HeadphoneRestartTests
{
    public static async Task RunAsync(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var channels = new[] { "FL", "FR", "FC", "LFE", "SL", "SR" };
        var input = channels.Select(_ => new float[256]).ToArray();
        var stereo = new float[512];
        foreach (var method in Enum.GetValues<HeadphoneMethod>())
        {
            double restartedPeak = 0;
            var options = new HeadphoneOptions { Enabled = true, Method = method };
            for (int repeat = 0; repeat < 8; repeat++)
            {
                using (var previous = new HeadphoneRenderer(channels, options))
                    for (int block = 0; block < 6; block++)
                    {
                        for (int c = 0; c < channels.Length; c++)
                            for (int f = 0; f < 256; f++) input[c][f] = (float)(.3 * Math.Cos((f + block * 256) * (c + 1) * .06));
                        previous.Render(input, stereo, new(43, -12, 3), options, new(StereoMixSettings.Default, null), 1);
                    }
                foreach (var channel in input) Array.Clear(channel);
                using var next = new HeadphoneRenderer(channels, options);
                for (int block = 0; block < 6; block++)
                {
                    next.Render(input, stereo, new(0, 0, 0), options, new(StereoMixSettings.Default, null), 1);
                    restartedPeak = Math.Max(restartedPeak, stereo.Max(v => Math.Abs(v)));
                }
            }
            Check(restartedPeak < 1e-7, $"{method}: eight fresh six-channel renderers contain no preceding audio (peak={restartedPeak:G4})");
        }

        foreach (var latency in new[] { 0d, 80d })
        {
            var device = new QueuedOutput { StreamLatencyMilliseconds = latency };
            using var output = new HeadphonePlaybackOutput(device);
            output.Prepare(CancellationToken.None);
            var guard = output.GuardFrames;
            Check(guard >= HeadphonePlaybackOutput.MinimumGuardFrames && guard * 1000d / 48000 >= 30 + latency && device.Samples.All(v => v == 0),
                $"Output activation is entirely silent and covers endpoint latency ({latency} ms)");
            Check(output.SubmittedFrames == 0 && output.PlayedFrames == 0 && !output.HasStarted,
                "Silent device preparation does not advance the media clock or announce playback");
            for (int block = 0; block < 16; block++)
            {
                output.WaitForSpace(256, CancellationToken.None);
                output.Write(Enumerable.Repeat(-.2f, 512).ToArray(), 256);
                Check(output.PlayedFrames == Math.Clamp(device.PlayedFrames - guard, 0, output.SubmittedFrames),
                    $"Media clock excludes the silent prefix at block {block} (latency={latency})");
            }
            Check(output.HasStarted && output.SubmittedFrames == 4096 && output.PlayedFrames > 0,
                "Audible playback starts once and reports only the submitted source frames");
            output.Drain(CancellationToken.None);
            Check(output.PlayedFrames == 4096 && device.Samples.Take(guard * 2).All(v => v == 0) &&
                device.Samples.TakeLast(guard * 2).All(v => v == 0) && device.Samples.Any(v => v < -.1f) && device.Samples.All(v => v <= 0),
                "Drain leaves the downstream device silent without cutting, replaying or replacing the new track");
        }
        var canceledDevice = new QueuedOutput();
        using (var output = new HeadphonePlaybackOutput(canceledDevice))
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool canceled = false;
            try { output.Prepare(cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && canceledDevice.Samples.Count == 0, "Canceling before preparation submits no old or new source audio");
        }
        var duringPreparation = new QueuedOutput();
        using (var output = new HeadphonePlaybackOutput(duringPreparation))
        using (var cancel = new CancellationTokenSource())
        {
            duringPreparation.OnWrite = count => { if (count == 8) cancel.Cancel(); };
            bool canceled = false;
            try { output.Prepare(cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && output.PlayedFrames == 0 && !output.HasStarted, "Cancellation while silence is playing never announces the track");
        }
        Check(duringPreparation.Disposed && duringPreparation.Samples.All(v => v == 0) && duringPreparation.DrainCalls == 0,
            "A canceled silent startup closes without draining or playing a track fragment");
        var stopping = new QueuedOutput();
        using (var output = new HeadphonePlaybackOutput(stopping))
        {
            output.Prepare(CancellationToken.None);
            for (int block = 0; block < 8; block++)
            { output.WaitForSpace(256, CancellationToken.None); output.Write(Enumerable.Repeat(.2f, 512).ToArray(), 256); }
        }
        Check(stopping.Disposed && stopping.DrainCalls == 1 && stopping.Samples.TakeLast(HeadphonePlaybackOutput.MinimumGuardFrames * 2).All(v => v == 0),
            "Cancellation after music flushes a silent suffix before device disposal");

        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        var session = HeadphoneSession.Current; var previousOptions = session.Options; var factory = AudioNavigationService.HeadphoneOutputFactory;
        var music = Path.Combine(root, "previous-track.flac"); var silence = Path.Combine(root, "next-track-silence.flac");
        foreach (var (file, signal) in new[] { (music, "aevalsrc=0.25*cos(2*PI*800*t):s=88200:c=5.1(side)"),
            (silence, "anullsrc=r=88200:cl=5.1(side)") })
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", signal, "-t", "0.2", "-c:a", "flac", "-y", file], CancellationToken.None);
        try
        {
            foreach (var method in Enum.GetValues<HeadphoneMethod>())
            {
                session.SetOptions(new HeadphoneOptions { Enabled = true, Method = method });
                foreach (var file in new[] { music, silence, music, silence })
                {
                    var disc = await service.AnalyzeAudioFilesAsync([file], CancellationToken.None); var title = disc.Playlists.Single();
                    var stream = (await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None)).Streams.Single();
                    var device = new QueuedOutput(); AudioNavigationService.HeadphoneOutputFactory = () => device;
                    int starts = 0; var positions = new List<double>(); var chunks = new List<PreviewPcmChunk>();
                    await navigation.PlayAsync(disc, title, stream, 0, title.DurationTicks, CancellationToken.None,
                        segmentStarted: _ => starts++, playbackPosition: (_, seconds) => positions.Add(seconds), visualization: new(() => true, chunks.Add));
                    Check(device.Disposed && starts == 1 && chunks.Count > 0 && positions.All(v => v >= 0 && v <= .23),
                        method + ": consecutive FLAC starts keep media position and visualization on the source clock");
                    Check(file == silence ? device.Samples.All(v => v == 0) : device.Samples.Any(v => Math.Abs(v) > .001),
                        method + ": new silent tracks contain none of the preceding track, and music stays audible");
                }
            }
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = factory; session.SetOptions(previousOptions); }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class QueuedOutput : IHeadphoneOutput
    {
        public List<float> Samples { get; } = [];
        public Action<int>? OnWrite { get; set; }
        private int _writes;
        public bool Disposed { get; private set; }
        public int DrainCalls { get; private set; }
        public long SubmittedFrames => Samples.Count / 2;
        public long PlayedFrames { get; private set; }
        public double BufferMilliseconds => 30;
        public double StreamLatencyMilliseconds { get; init; }
        public bool HasStarted { get; private set; }
        public void WaitForSpace(int frames, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (SubmittedFrames - PlayedFrames + frames > 1440) { HasStarted = true; PlayedFrames += 256; }
        }
        public void Write(float[] samples, int frames) { Samples.AddRange(samples.Take(frames * 2)); OnWrite?.Invoke(++_writes); }
        public void Drain(CancellationToken token) { token.ThrowIfCancellationRequested(); PlayedFrames = SubmittedFrames; DrainCalls++; }
        public void Dispose() => Disposed = true;
    }
}
