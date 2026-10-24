using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

internal static class HeadphoneClickTests
{
    public static async Task RunAsync(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var device = new CaptureOutput();
        var endedCount = 0;
        using (var output = new HeadphonePlaybackOutput(device))
        {
            // A source starting away from zero is the audible regression, including negative polarity.
            for (int block = 0; block < 5; block++) output.Write(Enumerable.Range(0, 256).SelectMany(_ => new[] { .5f, -.25f }).ToArray(), 256);
            var beforeRelease = device.Samples.Count;
            Check(device.Samples[0] == 0 && device.Samples[1] == 0, "A nonzero source starts at silence in both ears");
            Check(device.Samples.Skip(HeadphonePlaybackOutput.FadeFrames * 2).Select((v, i) => v == (i % 2 == 0 ? .5f : -.25f)).All(v => v),
                "Samples after the brief transport attack are unchanged");
            Check(MaxStep(device.Samples, 0, beforeRelease) < .003, "Starting at a nonzero sample has no abrupt waveform step");
            output.Drain(CancellationToken.None);
            Check(device.Samples[beforeRelease] == .5f && device.Samples[beforeRelease + 1] == -.25f,
                "Release continues from the last submitted sample without replaying a PCM block");
            Check(device.Samples[^2] == 0 && device.Samples[^1] == 0 && MaxStep(device.Samples, beforeRelease - 2, device.Samples.Count) < .003,
                "Release reaches silence smoothly in both ears");
            endedCount = device.Samples.Count;
        }
        Check(device.Disposed && device.DrainCalls == 1 && device.Samples.Count == endedCount, "A drained output closes without playing another tail");
        var next = new CaptureOutput();
        using (var output = new HeadphonePlaybackOutput(next)) output.Write(Enumerable.Repeat(-.4f, 512).ToArray(), 256);
        Check(next.Samples[0] == 0 && next.Samples[^1] == 0 && next.Samples.All(v => v <= 0),
            "Restart has a fresh attack and cannot contain the previous stream's samples");
        var priming = new CaptureOutput { StartThreshold = 2048 };
        using (var output = new HeadphonePlaybackOutput(priming)) output.Write(Enumerable.Repeat(.25f, 512).ToArray(), 256);
        Check(priming.Disposed && priming.DrainCalls == 0 && priming.SubmittedFrames == 256,
            "Canceling an unstarted priming buffer discards it without starting unwanted playback");
        var stalled = new CaptureOutput { StallOnWait = true };
        var bounded = new HeadphonePlaybackOutput(stalled); bounded.Write(Enumerable.Repeat(.25f, 512).ToArray(), 256);
        var watch = Stopwatch.StartNew(); bounded.Dispose();
        Check(stalled.Disposed && watch.Elapsed < TimeSpan.FromSeconds(1), "A stalled device cannot make transport shutdown hang");

        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var file = Path.Combine(root, "nonzero-start.flac");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
            "aevalsrc=0.2*cos(2*PI*440*t)|0.15*cos(2*PI*660*t):s=88200:c=stereo", "-t", "1", "-c:a", "flac", "-y", file], CancellationToken.None);
        var disc = await service.AnalyzeAudioFilesAsync([file], CancellationToken.None); var title = disc.Playlists.Single();
        var stream = (await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None)).Streams.Single();
        var session = HeadphoneSession.Current; var previous = session.Options; var factory = AudioNavigationService.HeadphoneOutputFactory;
        try
        {
            foreach (var method in new[] { HeadphoneMethod.Direct, HeadphoneMethod.SteamAudio })
            {
                session.SetOptions(new HeadphoneOptions { Enabled = true, Method = method });
                foreach (var seek in new[] { 0L, 13781L })
                {
                    using var cancel = new CancellationTokenSource();
                    var guardSamples = HeadphonePlaybackOutput.MinimumGuardFrames * 2;
                    var capture = new CaptureOutput { OnWrite = count => { if (count == HeadphonePlaybackOutput.MinimumGuardFrames / 256 + 6) cancel.Cancel(); } };
                    AudioNavigationService.HeadphoneOutputFactory = () => capture;
                    var canceled = false;
                    try { await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc, title, stream, seek, title.DurationTicks, cancel.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Check(canceled && capture.Disposed && capture.DrainCalls == 1 && capture.Samples.Count > 3072,
                        $"{method} stops and drains its de-click release after cancellation at position {seek}");
                    Check(capture.Samples[0] == 0 && capture.Samples[1] == 0 && capture.Samples[^1] == 0 && capture.Samples[^2] == 0 && capture.Samples.Any(v => Math.Abs(v) > .001),
                        $"{method} file playback and seek begin and end at zero with audible content preserved");
                    Check(MaxStep(capture.Samples, guardSamples + 3070, capture.Samples.Count) < .003,
                        $"{method} cancellation does not cut the last nonzero sample abruptly");
                }
            }
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = factory; session.SetOptions(previous); }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double MaxStep(List<float> samples, int start, int end)
    {
        double maximum = 0;
        for (int i = start + 2; i < end; i++) maximum = Math.Max(maximum, Math.Abs(samples[i] - samples[i - 2]));
        return maximum;
    }

    private sealed class CaptureOutput : IHeadphoneOutput
    {
        public List<float> Samples { get; } = [];
        public int StartThreshold { get; init; } = 1;
        public Action<int>? OnWrite { get; init; }
        public bool StallOnWait { get; init; }
        private int _writes;
        public bool Disposed { get; private set; }
        public int DrainCalls { get; private set; }
        public long SubmittedFrames => Samples.Count / 2;
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 30;
        public bool HasStarted => SubmittedFrames >= StartThreshold;
        public void WaitForSpace(int frames, CancellationToken token)
        {
            if (StallOnWait) token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }
        public void Write(float[] samples, int frames) { Samples.AddRange(samples.Take(frames * 2)); OnWrite?.Invoke(++_writes); }
        public void Drain(CancellationToken token) { token.ThrowIfCancellationRequested(); DrainCalls++; }
        public void Dispose() => Disposed = true;
    }
}
