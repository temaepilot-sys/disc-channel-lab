using Disc2Flac;
using System.IO;
using System.Buffers.Binary;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

internal static class HeadphoneTests
{
    public static async Task RunAsync(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var options = new HeadphoneOptions { Enabled = true, ShadowStrength = 0, RearCutDb = 0, SmoothingMs = 0, HeadroomDb = -12 };
        var front = HeadphoneGeometry.Relative("FC", new HeadPose(90, 0, 0).Rotation);
        Check(front.X < -.999 && Math.Abs(front.Z) < .001, "Turning right moves the room's front-center source to the left ear");
        var up = HeadphoneGeometry.Relative("FC", new HeadPose(0, 45, 0).Rotation);
        Check(up.Y < -.7, "Looking up moves the fixed front speaker below the head");
        var pan = HeadphoneGeometry.Pan(Vector3.Normalize(new(.5f, .2f, .8f)));
        Check(Math.Abs(pan.Left * pan.Left + pan.Right * pan.Right - 1) < 1e-6, "Direct pan preserves equal power");
        Check(DirectHeadphoneSpatializer.HeadAlpha(Math.PI / 6, 1) > DirectHeadphoneSpatializer.HeadAlpha(5 * Math.PI / 6, 1), "Spherical head model reduces opposite-ear high frequencies");
        var maxDelay = HeadphoneGeometry.WoodworthDelay(Vector3.UnitX, .0875);
        Check(maxDelay > .00065 && maxDelay < .00066 && HeadphoneGeometry.WoodworthDelay(-Vector3.UnitX, .0875) == -maxDelay, "Woodworth delay is symmetric and approximately 0.656 ms at 90 degrees");
        string[] back51 = ["FL", "FR", "FC", "LFE", "BL", "BR"];
        string[] side51 = ["FL", "FR", "FC", "LFE", "SL", "SR"];
        string[] seven = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"];
        Check(Vector3.Distance(HeadphoneGeometry.Speaker("BL", back51), HeadphoneGeometry.Speaker("SL", side51)) < 1e-6,
            "A lone 5.1 surround pair uses 110 degrees for both back and side slot labels");
        Check(Math.Abs(Math.Atan2(HeadphoneGeometry.Speaker("BR", seven).X, HeadphoneGeometry.Speaker("BR", seven).Z) * 180 / Math.PI - 150) < .001,
            "7.1 retains a separate 150-degree back pair and 110-degree side pair");
        var wide = options with { FrontAngle = 90 };
        Check(HeadphoneGeometry.Speaker("FL", back51, wide.FrontAngle).X < -.999 && Math.Abs(HeadphoneGeometry.Speaker("FL", back51, wide.FrontAngle).Z) < .001,
            "Maximum front width places FL / FR directly to the left / right");
        var displayPose = new HeadPose(90, 0, 0);
        Check(HeadphoneGeometry.DisplaySpeaker("FC", displayPose, options, back51).X < -.999 &&
            HeadphoneGeometry.DisplaySpeaker("FC", displayPose, options with { UseTracker = true }, back51).Z > .999,
            "Keyboard rotates the room around a fixed listener; tracking keeps the room fixed");
        Check(new HeadphoneOptions { Enabled = true, FrontAngle = double.NaN }.Validated().FrontAngle == 30 &&
            new HeadphoneOptions { Enabled = true, FrontAngle = 200 }.Validated().FrontAngle == 90 &&
            JsonSerializer.Deserialize<HeadphoneOptions>("{}")!.FrontAngle == 30,
            "Front width validates bounds and older preference files retain the standard 30-degree default");
        var layouts = (IReadOnlyDictionary<string, string>)typeof(StereoMixSettings).GetField("Layouts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var layout in layouts)
        {
            var codes = layout.Value.Split(' ');
            var format = new AudioStreamInfo { Index = 0, Codec = "pcm_s24le", Channels = codes.Length, ChannelLayout = layout.Key, SampleRate = 48000, BitDepth = 24 };
            Check(HeadphoneGeometry.Channels(format).SequenceEqual(codes) && codes.All(c => Math.Abs(HeadphoneGeometry.Speaker(c).Length() - 1) < 1e-6), "Explicit speaker geometry: " + layout.Key);
        }
        var unusual = new AudioStreamInfo { Index = 0, Codec = "pcm_s24le", Channels = 2, ChannelLayout = "FC+LFE", SampleRate = 48000 };
        Check(HeadphoneGeometry.Channels(unusual).Length == 0, "Unusual two-channel layouts are never guessed as front left/right");
        double ShelfResponse(float frequency, double cut)
        {
            using var engine = new DirectHeadphoneSpatializer(1);
            var input = new float[256]; var l = new float[256]; var r = new float[256]; double energy = 0;
            var settings = options with { RearCutDb = cut, RearFrequency = 4000 };
            for (int block = 0; block < 40; block++)
            {
                for (int i = 0; i < 256; i++) input[i] = .1f * MathF.Sin(2 * MathF.PI * frequency * (block * 256 + i) / 48000);
                engine.Process(0, input, l, r, -Vector3.UnitZ, settings);
                if (block >= 20) energy += l.Sum(v => (double)v * v) + r.Sum(v => (double)v * v);
            }
            return energy;
        }
        var highChange = 10 * Math.Log10(ShelfResponse(10000, 6) / ShelfResponse(10000, 0));
        var lowChange = 10 * Math.Log10(ShelfResponse(100, 6) / ShelfResponse(100, 0));
        Check(highChange < -4.5 && Math.Abs(lowChange) < .1, "Rear shelf audibly reduces 10 kHz while preserving 100 Hz");
        var state = new PreviewMixState(StereoMixSettings.Default, null);
        var pcm = new[] { new float[256] }; pcm[0][0] = .2f;
        var rendered = new float[512];
        foreach (var method in new[] { HeadphoneMethod.Direct, HeadphoneMethod.SteamAudio })
        {
            var settings = options with { Method = method };
            double EarSeparation(double angle)
            {
                var o = settings with { FrontAngle = angle };
                using var renderer = new HeadphoneRenderer(["FL", "FR"], o);
                var input = new[] { new float[256], new float[256] }; input[0][0] = .2f;
                double left = 0, right = 0;
                for (int block = 0; block < 8; block++)
                {
                    renderer.Render(input, rendered, new(0, 0, 0), o, state, 1);
                    for (int i = 0; i < 256; i++) { left += rendered[2 * i] * rendered[2 * i]; right += rendered[2 * i + 1] * rendered[2 * i + 1]; }
                    Array.Clear(input[0]);
                }
                return left / Math.Max(right, 1e-20);
            }
            Check(EarSeparation(90) > EarSeparation(30) * 1.1, method + " widens the audible left/right separation when front angle increases");
            double LeftAt(double yaw)
            {
                using var renderer = new HeadphoneRenderer(["FC"], settings); double left = 0, right = 0;
                for (int block = 0; block < 5; block++)
                {
                    renderer.Render(pcm, rendered, new(yaw, 0, 0), settings, state, 1);
                    for (int i = 0; i < 256; i++) { left += rendered[2 * i] * rendered[2 * i]; right += rendered[2 * i + 1] * rendered[2 * i + 1]; }
                    Array.Clear(pcm[0]);
                }
                pcm[0][0] = .2f;
                return left / (left + right);
            }
            var toLeft = LeftAt(90); var toRight = LeftAt(-90);
            Check(double.IsFinite(toLeft) && toLeft > .6 && toRight < .4, method + " renders native left/right direction correctly");
            float[] Render(ExperimentalChannel channel)
            {
                using var renderer = new HeadphoneRenderer(["FC"], settings);
                renderer.Render(pcm, rendered, new(0, 0, 0), settings, new(StereoMixSettings.Default, null, [channel]), 1);
                return rendered.ToArray();
            }
            var normal = Render(new("FC", 1, 0, false)); var inverted = Render(new("FC", 1, 0, false, false, true));
            Check(normal.Select((v, i) => Math.Abs(v + inverted[i])).Max() < 1e-6 && normal.Any(v => Math.Abs(v) > 1e-5), method + " preserves exact mixer polarity inversion");
            Check(Render(new("FC", 1, 0, true)).All(v => Math.Abs(v) < 1e-7), method + " honors mute before spatialization");
            using var soloRenderer = new HeadphoneRenderer(["FL", "FR"], settings);
            var silentSolo = new[] { new float[256], new float[256] }; silentSolo[1][0] = .5f;
            soloRenderer.Render(silentSolo, rendered, new(0, 0, 0), settings,
                new(StereoMixSettings.Default, null, [new("FL", 1, 0, false, true), new("FR", 1, 0, false)]), 1);
            Check(rendered.All(v => Math.Abs(v) < 1e-7), method + " mutes non-solo channels");
        }
        var session = HeadphoneSession.Current;
        var packet = new byte[48];
        void PosePacket(double yaw, double pitch, double roll)
        {
            foreach (var (value, index) in new[] { (yaw, 3), (pitch, 4), (roll, 5) })
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(index * 8, 8), BitConverter.DoubleToInt64Bits(value));
        }
        using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port; reserve.Close();
            session.SetOptions(options with { UseTracker = true, UdpPort = port });
            using var sender = new UdpClient(); PosePacket(35, -12, 8);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port));
            for (int i = 0; i < 100 && session.Pose.Yaw != 35; i++) await Task.Delay(10);
            Check(session.Pose == new HeadPose(35, -12, 8), "Actual loopback UDP accepts opentrack's six-double yaw/pitch/roll packet");
            Check(!session.ReceivePacket(new byte[47]), "Truncated UDP packet is rejected");
            PosePacket(double.NaN, 0, 0); Check(!session.ReceivePacket(packet) && session.Pose.Yaw == 35, "Non-finite tracker input cannot corrupt orientation");
            session.Recenter(); PosePacket(45, -10, 10); session.ReceivePacket(packet);
            Check(session.Pose == new HeadPose(10, 2, 2), "Centering establishes a relative neutral head direction");
            await Task.Delay(550);
            Check(session.PacketAgeSeconds > .5 && session.Pose.Yaw == 10, "Lost tracking holds the last valid pose");
        }
        session.SetOptions(options); session.SetKeyboard(0, 0, 0); session.Save();
        using (var loaded = new HeadphoneSession()) Check(loaded.Options == options with { Enabled = false },
            "Headphone tuning round-trips, while every new session starts in ordinary stereo");
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var file = Path.Combine(root, "six-channels.flac");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "aevalsrc=0.1*sin(2*PI*220*t)|0.1*sin(2*PI*330*t)|0.1*sin(2*PI*440*t)|0.1*sin(2*PI*60*t)|0.1*sin(2*PI*550*t)|0.1*sin(2*PI*660*t):s=48000:c=5.1(side)", "-t", "1.2", "-c:a", "flac", "-y", file], CancellationToken.None);
        var disc = await service.AnalyzeAudioFilesAsync([file], CancellationToken.None); var playlist = disc.Playlists.Single();
        var (streams, _) = await service.AnalyzePlaylistAsync(disc, playlist, CancellationToken.None); var stream = streams.Single();
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        var originalFactory = AudioNavigationService.HeadphoneOutputFactory;
        try
        {
            foreach (var method in new[] { HeadphoneMethod.Direct, HeadphoneMethod.SteamAudio })
            {
                session.SetOptions(options with { Method = method });
                var sink = new CaptureOutput(); AudioNavigationService.HeadphoneOutputFactory = () => sink;
                var chunks = new List<PreviewPcmChunk>(); int meters = 0, starts = 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await navigation.PlayAsync(disc, playlist, stream, 9000, playlist.DurationTicks, timeout.Token,
                    segmentStarted: _ => starts++, mixedLevels: (_, _) => meters++, visualization: new(() => true, chunks.Add));
                var mediaFrames = sink.SubmittedFrames - 2 * HeadphonePlaybackOutput.MinimumGuardFrames;
                // Observations now collect 1024-frame packets off the audio thread.
                // A fast test sink may also drop optional observer packets by design.
                Check(starts == 1 && mediaFrames >= 48000 && mediaFrames < 50000 && meters > 0 && chunks.Count == meters &&
                    chunks.All(c => c.Input.Length is > 0 and <= 1024 * 6 * 2) &&
                    chunks.Zip(chunks.Skip(1)).All(p => p.First.Seconds < p.Second.Seconds),
                    method + " decodes and seeks actual 5.1 FLAC with matching meters and viewer blocks");
                Check(sink.Samples.All(float.IsFinite) && sink.Samples.Any(v => Math.Abs(v) > .001f) && chunks.All(c => c.Channels.SequenceEqual(new[] { "FL", "FR", "FC", "LFE", "SL", "SR" })),
                    method + " preserves explicit channel order and emits finite audible stereo");
                using var cancel = new CancellationTokenSource();
                sink = new CaptureOutput { OnWrite = () => cancel.Cancel() }; AudioNavigationService.HeadphoneOutputFactory = () => sink;
                bool canceled = false;
                try { await navigation.PlayAsync(disc, playlist, stream, 0, playlist.DurationTicks, cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && sink.Disposed, method + " cancels playback and releases output resources");
                foreach (var name in new[] { "stereo", "center", "quad", "surround50", "surround51", "surround61", "surround71" })
                {
                    var sample = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("DISCCHANNEL_FLAC_FIXTURES") ??
                        Path.Combine("verification-output", "flac-fixtures-sfiera"), name + ".flac"));
                    var sampleDisc = await service.AnalyzeAudioFilesAsync([sample], CancellationToken.None); var sampleTitle = sampleDisc.Playlists.Single();
                    var (sampleStreams, _) = await service.AnalyzePlaylistAsync(sampleDisc, sampleTitle, CancellationToken.None);
                    var sampleStream = sampleStreams.Single();
                    sink = new CaptureOutput(); AudioNavigationService.HeadphoneOutputFactory = () => sink;
                    await navigation.PlayAsync(sampleDisc, sampleTitle, sampleStream, 0, Math.Min(11250, sampleTitle.DurationTicks), timeout.Token);
                    Check(sink.Samples.All(float.IsFinite) && sink.Samples.Any(v => Math.Abs(v) > 1e-6f), method + " plays sfiera " + name + " with its explicit layout");
                }
            }
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = originalFactory; session.SetOptions(options); }
        using (var device = new WasapiHeadphoneOutput())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var silence = new float[512];
            for (int i = 0; i < 12; i++) { device.WaitForSpace(256, timeout.Token); device.Write(silence, 256); }
            device.Drain(timeout.Token);
            Check(device.PlayedFrames == 3072, $"Real Windows audio endpoint opens, plays silence and drains ({device.BufferMilliseconds:0.0} ms buffer)");
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        session.Dispose();
    }
    private sealed class CaptureOutput : IHeadphoneOutput
    {
        public List<float> Samples { get; } = [];
        public Action? OnWrite { get; init; }
        public bool Disposed { get; private set; }
        public long SubmittedFrames { get; private set; }
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 0;
        public void WaitForSpace(int frames, CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Write(float[] samples, int frames) { Samples.AddRange(samples.Take(frames * 2)); SubmittedFrames += frames; OnWrite?.Invoke(); }
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() => Disposed = true;
    }
}
