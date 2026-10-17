using Disc2Flac;
using System.IO;
using System.Text.Json;

internal static class HeadphoneSofaTests
{
    public static async Task RunAsync(string fixtures, string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidDataException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var kemar = Path.GetFullPath(Path.Combine(fixtures, "MIT_KEMAR_large_pinna.sofa"));
        var cipic = Path.GetFullPath(Path.Combine(fixtures, "CIPIC_subject_003_hrir_final.sofa"));
        var delayed = Path.GetFullPath(Path.Combine(fixtures, "CIPIC_subject_003_hrir_final_itdInDelayField.sofa"));
        var legacy = JsonSerializer.Deserialize<HeadphoneOptions>("{\"SteamTrimDb\":-3,\"HeadroomDb\":-18}")!.Validated();
        Check(legacy.Hrtf == HeadphoneHrtf.BuiltIn && legacy.SofaPath == "" && legacy.SteamTrimDb == -3 && legacy.HeadroomDb == -18 && legacy.SofaTrimDb == 0,
            "Existing preferences retain their sound values and default to the built-in HRTF");
        var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio, Hrtf = HeadphoneHrtf.Sofa, SofaPath = kemar,
            HeadroomDb = -18, ShadowStrength = .8, RearCutDb = 15, RearFrequency = 9000, HeadRadius = .1, InterauralDelay = true,
            LfeGain = .2, FrontAngle = 70, SmoothingMs = 40, DirectTrimDb = -6, SteamTrimDb = 5, SofaTrimDb = -4, UdpPort = 4399, InvertPitch = true };
        var reset = options.ResetSound(); var defaults = new HeadphoneOptions();
        Check(reset == defaults with { Enabled = options.Enabled, Method = options.Method, Hrtf = options.Hrtf, SofaPath = options.SofaPath,
            UdpPort = options.UdpPort, InvertPitch = options.InvertPitch },
            "Reset sound restores every audio default while retaining renderer, SOFA and tracker settings");
        Check((options with { Hrtf = (HeadphoneHrtf)77, SofaPath = null!, SofaTrimDb = double.NaN }).Validated() is
            { Hrtf: HeadphoneHrtf.BuiltIn, SofaPath: "", SofaTrimDb: 0 }, "Invalid saved HRTF fields have safe defaults");
        foreach (var file in new[] { kemar, cipic })
        {
            SofaHrtf.Validate(file);
            Check(true, "Real native SOFA loading and impulse validation: " + Path.GetFileName(file));
        }
        var unicode = Path.Combine(root, "日本語の耳モデル.sofa"); File.Copy(kemar, unicode, true);
        SofaHrtf.Validate(unicode);
        Check(true, "SOFA paths containing Japanese characters work through the native memory-loading API");
        var invalid = Path.Combine(root, "invalid.sofa"); File.WriteAllText(invalid, "This is not an acoustic data set.");
        var truncated = Path.Combine(root, "truncated.sofa"); File.WriteAllBytes(truncated, File.ReadAllBytes(kemar).Take(256).ToArray());
        foreach (var file in new[] { invalid, truncated, delayed,
            Path.GetFullPath(Path.Combine(fixtures, "D1_48K_24bit_0.3s_FIR_SOFA.sofa")), Path.Combine(root, "missing.sofa") })
        {
            bool rejected = false;
            try { SofaHrtf.Validate(file); } catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
            Check(rejected, "Unsupported, damaged or missing SOFA is reported without substituting the default: " + Path.GetFileName(file));
        }
        var builtin = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio };
        var external = builtin with { Hrtf = HeadphoneHrtf.Sofa, SofaPath = kemar };
        float[] Impulse(HeadphoneOptions o, bool invert = false, bool mute = false)
        {
            using var renderer = new HeadphoneRenderer(["FL"], o);
            var source = new[] { new float[256] }; source[0][0] = .2f;
            var output = new float[512]; var samples = new List<float>();
            var mix = new PreviewMixState(StereoMixSettings.Default, null, [new("FL", 1, 0, mute, false, invert)]);
            for (int block = 0; block < 10; block++)
            {
                renderer.Render(source, output, new(0, 0, 0), o, mix, 1); samples.AddRange(output); Array.Clear(source[0]);
            }
            return samples.ToArray();
        }
        var standard = Impulse(builtin); var custom = Impulse(external);
        Check(custom.All(float.IsFinite) && custom.Any(v => Math.Abs(v) > 1e-6) && custom.Zip(standard).Max(p => Math.Abs(p.First - p.Second)) > 1e-5,
            "Selecting a real SOFA changes the rendered waveform rather than merely changing the label");
        Check(Impulse(external with { SofaPath = unicode }).SequenceEqual(custom), "Unicode and original SOFA paths produce identical audio");
        var inverted = Impulse(external, invert: true);
        Check(custom.Zip(inverted).All(p => Math.Abs(p.First + p.Second) < 1e-6) && Impulse(external, mute: true).All(v => v == 0),
            "SOFA playback preserves mixer polarity inversion and Mute");
        var trimmed = Impulse(external with { SofaTrimDb = -6 });
        Check(custom.Zip(trimmed).All(p => Math.Abs(p.Second - p.First * Math.Pow(10, -6d / 20)) < 1e-6),
            "The SOFA comparison trim controls the actual rendered gain");
        Check(Impulse(external with { SteamTrimDb = 8 }).SequenceEqual(custom) && Impulse(builtin with { SofaTrimDb = 8 }).SequenceEqual(standard),
            "Built-in and external comparison trims are independent");
        Check(Impulse(builtin with { Method = HeadphoneMethod.Direct, Hrtf = HeadphoneHrtf.Sofa, SofaPath = "missing.sofa" }).Any(v => Math.Abs(v) > 1e-6),
            "Direct mode remains usable even if a remembered SOFA file is unavailable");
        using (var tailRenderer = new HeadphoneRenderer(["FL"], external))
        {
            var source = new[] { new float[256] }; source[0][0] = .2f;
            var output = new float[512]; var recorded = new List<float>();
            tailRenderer.Render(source, output, new(0, 0, 0), external, new(StereoMixSettings.Default, null), 1);
            var tailBlocks = tailRenderer.TailBlocks; recorded.AddRange(output); Array.Clear(source[0]);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers();
            for (int block = 0; block < tailBlocks + 4; block++)
            { tailRenderer.Render(source, output, new(0, 0, 0), external, new(StereoMixSettings.Default, null), 1); recorded.AddRange(output); }
            Check(recorded.Skip((tailBlocks + 1) * 512).All(v => Math.Abs(v) < 1e-6),
                "The reported SOFA tail length reaches silence without truncating the filter decay");
        }
        var session = HeadphoneSession.Current; var previous = session.Options;
        session.SetOptions(options); session.Save();
        using (var loaded = new HeadphoneSession())
            Check(loaded.Options == options.Validated() with { Enabled = false }, "SOFA path, model and separate trims round-trip while startup stays in 2ch Stereo");
        session.SetOptions(previous);

        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log);
        var probe = new FfprobeService(tools, runner); var service = new DiscService(probe, log);
        var music = Path.Combine(root, "six-channel.flac");
        await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i",
            "aevalsrc=0.04*cos(2*PI*440*t):s=88200:c=5.1(side)", "-t", "0.25", "-c:a", "flac", "-y", music], CancellationToken.None);
        var disc = await service.AnalyzeAudioFilesAsync([music], CancellationToken.None); var title = disc.Playlists.Single();
        var stream = (await service.AnalyzePlaylistAsync(disc, title, CancellationToken.None)).Streams.Single();
        var factory = AudioNavigationService.HeadphoneOutputFactory;
        try
        {
            foreach (var selected in new[] { builtin, external, builtin })
            {
                session.SetOptions(selected); var capture = new CaptureOutput(); AudioNavigationService.HeadphoneOutputFactory = () => capture;
                int starts = 0; var chunks = new List<PreviewPcmChunk>();
                await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc, title, stream, 4500, title.DurationTicks,
                    CancellationToken.None, segmentStarted: _ => starts++, visualization: new(() => true, chunks.Add));
                Check(capture.Disposed && capture.Samples.All(float.IsFinite) && capture.Samples.Any(v => Math.Abs(v) > 1e-6) && starts == 1 && chunks.Count > 0,
                    selected.Hrtf + ": actual six-channel FLAC seek uses the selected model with valid visualization and a single start");
                Check(capture.Samples.Take(HeadphonePlaybackOutput.MinimumGuardFrames * 2).All(v => v == 0) &&
                    capture.Samples.TakeLast(HeadphonePlaybackOutput.MinimumGuardFrames * 2).All(v => v == 0),
                    selected.Hrtf + ": model changes retain silent transport boundaries");
            }
            session.SetOptions(external with { SofaPath = Path.Combine(root, "missing.sofa") });
            bool opened = false; AudioNavigationService.HeadphoneOutputFactory = () => { opened = true; return new CaptureOutput(); };
            bool failed = false;
            try { await new AudioNavigationService(tools, runner, probe, log).PlayAsync(disc, title, stream, 0, title.DurationTicks, CancellationToken.None); }
            catch (IOException) { failed = true; }
            Check(failed && !opened, "Missing SOFA is reported before opening the audio device or playing any samples");
        }
        finally { AudioNavigationService.HeadphoneOutputFactory = factory; session.SetOptions(previous); }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class CaptureOutput : IHeadphoneOutput
    {
        public List<float> Samples { get; } = [];
        public bool Disposed { get; private set; }
        public long SubmittedFrames => Samples.Count / 2;
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 0;
        public void WaitForSpace(int frames, CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Write(float[] samples, int frames) => Samples.AddRange(samples.Take(frames * 2));
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() => Disposed = true;
    }
}
