using System.Text;
using System.Text.Json;

namespace DiscChannelLab.Visualizer;

public static class SelfTests
{
    public static int Run()
    {
        int tests = 0;
        var report = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            tests++; report.Add("PASS: " + name);
        }
        try
        {
            var preferenceFile = Path.Combine(AppContext.BaseDirectory, "cache", "preferences-test-" + Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(preferenceFile)!);
            try
            {
                var store = new ViewerPreferencesStore(preferenceFile);
                Check(store.Load() is null, "Missing viewer settings use UI defaults");
                File.WriteAllText(preferenceFile, "{\"version\":1}");
                Check(store.Load() is { VisualStyle: "particles", Aurora.Layout: "curtain", Aurora.Intensity: .75, SyncReference: "listener", ParticleSpeed: 2, ListenerSize: 1.5, ListenerBrightness: 1.4, SpeakerBeams: false, BeamStrength: .8 }, "Legacy preferences retain particle view and default beam strength");
                var original = new ViewerPreferences { View = "listener", Spread = 12, Transparency = 65,
                    SyncReference = "speaker", ParticleSpeed = 3.2, ListenerSize = 2.4, ListenerBrightness = 4,
                    SpeakerBeams = true, BeamStrength = 1.6,
                    Camera = new CameraPreferences { Yaw = 1.25, Pitch = -.7, Distance = 8, Target = [.4, 1.2, -2] } };
                store.Save(original);
                var restored = new ViewerPreferencesStore(preferenceFile).Load()!;
                Check(restored.View == "listener" && restored.Spread == 12 && restored.Transparency == 65 &&
                    restored.SyncReference == "speaker" && restored.ParticleSpeed == 3.2 && restored.ListenerSize == 2.4 && restored.ListenerBrightness == 4 &&
                    restored.SpeakerBeams && restored.BeamStrength == 1.6 &&
                    restored.Camera.Pitch == -.7 && restored.Camera.Target.SequenceEqual(original.Camera.Target), "Viewer settings survive store restart");
                string previous = File.ReadAllText(preferenceFile);
                bool invalidRejected = false;
                try { store.Save(original with { ParticleSize = double.NaN }); } catch (InvalidDataException) { invalidRejected = true; }
                Check(invalidRejected && File.ReadAllText(preferenceFile) == previous, "Invalid settings do not overwrite saved preferences");
                invalidRejected = false;
                try { store.Save(original with { Version = 2 }); } catch (InvalidDataException) { invalidRejected = true; }
                Check(invalidRejected, "Unsupported viewer settings version rejected");
                foreach (var invalid in new[] { original with { SyncReference = "unknown" }, original with { ParticleSpeed = 0 }, original with { ParticleSpeed = double.NaN }, original with { ParticleSpeed = 4.1 }, original with { ListenerSize = .9 }, original with { ListenerSize = 3.1 }, original with { ListenerSize = double.NaN }, original with { ListenerBrightness = .9 }, original with { ListenerBrightness = 4.1 }, original with { ListenerBrightness = double.NaN }, original with { BeamStrength = -.1 }, original with { BeamStrength = 2.1 }, original with { BeamStrength = double.NaN } })
                {
                    invalidRejected = false;
                    try { store.Save(invalid); } catch (InvalidDataException) { invalidRejected = true; }
                    Check(invalidRejected && File.ReadAllText(preferenceFile) == previous, "Invalid synchronization/speed preserves saved preferences");
                }
                File.WriteAllText(preferenceFile, "{broken");
                invalidRejected = false;
                try { store.Load(); } catch (JsonException) { invalidRejected = true; }
                Check(invalidRejected && File.ReadAllText(preferenceFile) == "{broken", "Corrupt settings are reported and preserved");
                var free = original with { View = "free", Camera = new CameraPreferences { Yaw = 5.9, Pitch = -.8,
                    Distance = 4, Target = [-12.5, -3.4, 22] } };
                store.Save(free); restored = new ViewerPreferencesStore(preferenceFile).Load()!;
                Check(restored.View == "free" && restored.Camera.Yaw == free.Camera.Yaw &&
                    restored.Camera.Target.SequenceEqual(free.Camera.Target), "Free navigation position and orientation survive a store restart");
                store.Save(free with { Camera = free.Camera with { HomeView = "listener" } });
                Check(store.Load()!.Camera.HomeView == "listener", "Free camera retains its original preset for Home across store restarts");
                var aurora = original with { VisualStyle = "hybrid", Aurora = new AuroraPreferences { Layout = "flow", Preset = "custom",
                    Intensity = 1.1, Opacity = .73, Density = .2, Speed = 1.3, Detail = .6, Persistence = .8, HybridAmount = .4, Quality = "high", Palette = "warm", FlowDirection = "down" } };
                store.Save(aurora);
                restored = new ViewerPreferencesStore(preferenceFile).Load()!;
                Check(restored.VisualStyle == "hybrid" && restored.Aurora == aurora.Aurora && restored.Camera == aurora.Camera with { Target = restored.Camera.Target },
                    "Aurora settings and existing camera survive a store restart");
                store.Save(aurora with { Aurora = aurora.Aurora with { FlowDirection = "up" } });
                Check(store.Load()!.Aurora.FlowDirection == "up", "Upward flow survives JSON save/load");
                store.Save(aurora with { Aurora = aurora.Aurora with { FlowDirection = "invalid" } });
                Check(store.Load()!.Aurora.FlowDirection == "none", "Invalid flow defaults to Off");
                File.WriteAllText(preferenceFile, """{"version":1,"visualStyle":42,"aurora":{"layout":"dome","preset":"unknown","intensity":-10,"opacity":9,"speed":"NaN","detail":null,"persistence":"Infinity","quality":"ultra","palette":false}}""");
                restored = store.Load()!;
                Check(restored.VisualStyle == "particles" && restored.Aurora is { Layout: "curtain", Preset: "calm", Intensity: 0, Opacity: 1,
                    Speed: .35, Detail: .4, Persistence: .65, Quality: "medium", Palette: "spectrum" }, "Malformed new fields fall back or clamp without rejecting legacy settings");
                foreach (string json in new[] { "{\"aurora\":null}", "{\"aurora\":42}", "{\"aurora\":[]}", "{\"aurora\":\"bad\"}" })
                {
                    File.WriteAllText(preferenceFile, json);
                    Check(store.Load()!.Aurora == new AuroraPreferences(), "Missing or non-object Aurora preferences use safe defaults");
                }
                foreach (double x in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    store.Save(aurora with { Aurora = aurora.Aurora with { Intensity = x, Opacity = x, Speed = x, Detail = x } });
                    Check(store.Load()!.Aurora is { Intensity: .75, Opacity: .55, Speed: .35, Detail: .4 }, "Nonfinite Aurora numbers serialize as safe defaults");
                }
                File.WriteAllText(preferenceFile, "{\"aurora\":{\"intensity\":1e999,\"hybridAmount\":-1,\"density\":9}}");
                Check(store.Load()!.Aurora is { Intensity: .75, HybridAmount: 0, Density: 1 }, "Overflow and out-of-range Aurora values are normalized");
                var electric = original with { VisualStyle = "electric-flow", ElectricFlow = new() { Intensity = 1.2, Spread = .9, Quality = "high", Amplitude = 2.5, Emission = 1.7, Gravity = 1.2, Width = 2, Glow = 1.5 } };
                store.Save(electric); restored = store.Load()!;
                Check(restored.VisualStyle == "particles" && restored.ElectricFlow == electric.ElectricFlow, "Retired modes fall back to Particles while retaining old preference data");
                File.WriteAllText(preferenceFile, "{\"electricFlow\":{\"intensity\":\"bad\",\"spread\":-2,\"quality\":\"ultra\"}}");
                Check(store.Load()!.ElectricFlow is { Intensity: .8, Spread: .2, Quality: "medium" }, "Invalid Electric Flow fields safely default or clamp");
                store.Save(electric with { ElectricFlow = new() { Intensity = double.NaN, Spread = double.PositiveInfinity } });
                Check(store.Load()!.ElectricFlow == new ElectricFlowPreferences(), "Nonfinite Electric Flow settings normalize without changing legacy fields");
                File.WriteAllText(preferenceFile, "{\"electricFlow\":{\"amplitude\":99,\"emission\":-3,\"gravity\":0,\"width\":null,\"glow\":1e999}}");
                Check(store.Load()!.ElectricFlow is { Amplitude: 4, Emission: .1, Gravity: .25, Width: 1.3, Glow: 1 }, "New motion and halo fields clamp safely and migrate old preferences");
                var spark = original with { VisualStyle = "spark-field", SparkField = new() { Density = 1.4, Afterglow = 1.6, Speed = 1.25, Streak = .8, StreakLength = 1.5, Twinkle = .3, Quality = "high", Preset = "tesla" } };
                store.Save(spark); restored = store.Load()!;
                Check(restored.VisualStyle == "spark-field" && restored.SparkField == spark.SparkField, "Spark Field settings survive a store restart");
                var combined = spark with { VisualStyle = "aurora-spark", Aurora = new() { Palette = "spark", FlowDirection = "down", Opacity = .4 } };
                store.Save(combined); restored = new ViewerPreferencesStore(preferenceFile).Load()!;
                Check(restored.VisualStyle == "aurora-spark" && restored.Aurora == combined.Aurora && restored.SparkField == combined.SparkField,
                    "Aurora + Spark saves matching palette, flow and independent Spark settings together");
                store.Save(combined with { Aurora = combined.Aurora with { Palette = "spectrum" } });
                Check(store.Load()!.Aurora.Palette == "spectrum", "Combined mode can retain the original veil palette");
                File.WriteAllText(preferenceFile, "{\"sparkField\":{\"density\":99,\"intensity\":\"bad\",\"quality\":\"ultra\",\"afterglow\":-5,\"streakLength\":null,\"twinkle\":1e999}}");
                Check(store.Load()!.SparkField is { Density: 2.5, Intensity: 1, Quality: "medium", Afterglow: 0, StreakLength: 1, Twinkle: .6 }, "Invalid Spark Field fields safely default or clamp");
                File.WriteAllText(preferenceFile, "{\"sparkField\":null}");
                Check(store.Load() is { VisualStyle: "particles", SparkField: { Density: 1, Preset: "surround" } }, "Old or null Spark Field preferences keep the Particles default");
                store.Save(spark with { SparkField = new() { Drift = double.NaN, Intensity = double.PositiveInfinity, Preset = "unknown" } });
                Check(store.Load()!.SparkField == new SparkFieldPreferences(), "Nonfinite Spark Field settings normalize without altering legacy settings");
            }
            finally { if (File.Exists(preferenceFile)) File.Delete(preferenceFile); }
            var settings = new VisualizationSettings();
            settings.Validate();
            Check(settings.Edges.Length == 17 && Math.Abs(settings.Edges[^1] - 20000) < .001, "16 bands need 17 edges");
            Check(ChannelMapper.Map("5.1(side)", 6)[4] == "SL" && ChannelMapper.Map("5.1", 6)[4] == "BL", "Side / back mapping is explicit");
            Check(!ChannelMapper.Map("5.0", 5).Contains("LFE"), "5.0 has no invented LFE");
            bool rejected = false;
            try { ChannelMapper.Map(null, 6); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unknown multichannel layout rejected");
            rejected = false;
            try { ChannelMapper.Map("FC+LFE", 2); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "An explicit unusual two-channel layout is not silently treated as stereo");
            Check(DispersionAngleMapper.Map(100) > DispersionAngleMapper.Map(1000) && DispersionAngleMapper.Map(1000) > DispersionAngleMapper.Map(10000), "Dispersion narrows with frequency");
            Check(Enumerable.Range(20, 19980).All(f => DispersionAngleMapper.Map(f) >= DispersionAngleMapper.Map(f + 1)), "Dispersion globally monotonic");
            double smooth = 0;
            for (int i = 0; i < 10; i++) smooth = SpectrumAnalyzer.Smooth(smooth, 1, .01, .05, .3);
            Check(Math.Abs(smooth - SpectrumAnalyzer.Smooth(0, 1, .1, .05, .3)) < 1e-10, "Smoothing independent of time step");
            Check(SpectrumAnalyzer.Smooth(1, 0, .05, .05, .3) > SpectrumAnalyzer.Smooth(1, 0, .05, .05, .05), "Release is slower than attack");
            var silence = new SpectrumAnalyzer(2, settings);
            for (int i = 0; i < 5000; i++) silence.Push([0, 0]);
            Check(silence.Frames.SelectMany(x => x).All(x => x == 0), "Silence produces no bands or particles");
            var tone = new SpectrumAnalyzer(2, settings);
            float[] sample = new float[2];
            for (int i = 0; i < 24000; i++)
            {
                sample[0] = (float)Math.Sin(2 * Math.PI * 1000 * i / settings.SampleRate);
                sample[1] = sample[0] * .5f;
                tone.Push(sample);
            }
            var last = tone.Frames[^1];
            int band = Array.FindIndex(settings.Edges, edge => edge > 1000) - 1;
            Check(Array.IndexOf(last[..16], last[..16].Max()) == band, "1 kHz tone appears in correct log band");
            Check(Math.Abs((last[band] - last[16 + band]) * 70 - 6.0206) < .01, "Half amplitude gives -6.02 dB");
            Check(last.Skip(16).All(float.IsFinite), "Finite spectrum values");
            Check(tone.FastFrames.Count == tone.Frames.Count && tone.FastFrames[^1].All(x => float.IsFinite(x) && x >= 0 && x <= 1), "Fast light frames share analysis timestamps and valid ranges");
            for (int i = 0; i < 4800; i++) tone.Push([0, 0]);
            Check(tone.FastFrames[^1].All(x => x == 0) && tone.Frames[^1][band] > .5f, "Fast light frame detects silence without waiting for the emission release");
            var weights = ChannelMapper.StereoWeights(ChannelMapper.Map("7.1", 8));
            Check(weights.Sum(w => w.Left) <= 1.00001 && weights.Sum(w => w.Right) <= 1.00001, "Stereo preview fixed gain bound");
            var clip = new ClipBuilder(new("test", "PCM", 48000, "16", "stereo", ["FL", "FR"], .1, false), settings);
            for (int i = 0; i < 4800; i++) clip.Add([.25f, -.25f]);
            byte[] packed = clip.Finish();
            int headerSize = BitConverter.ToInt32(packed);
            using var doc = JsonDocument.Parse(packed.AsMemory(4, headerSize));
            int audioOffset = (4 + headerSize + 3) & ~3;
            Check(BitConverter.ToSingle(packed, audioOffset) == .25f && BitConverter.ToSingle(packed, audioOffset + 4) == -.25f, "Packet alignment / stereo samples");
            Check(doc.RootElement.GetProperty("sampleFrames").GetInt32() == 4800, "Packet frame count");
            int analysisBytes = doc.RootElement.GetProperty("analysisFrames").GetInt32() * settings.BandCount * 2 * sizeof(float);
            Check(doc.RootElement.GetProperty("fastAnalysis").GetBoolean() && packed.Length == audioOffset + 4800 * 8 + analysisBytes * 2,
                "Demo packet includes aligned fast light frames without changing audio samples");
            report.Add($"{tests} tests passed.");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            Console.WriteLine(string.Join(Environment.NewLine, report));
            return 0;
        }
        catch (Exception e)
        {
            report.Add(e.ToString());
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), report);
            Console.Error.WriteLine(e);
            return 1;
        }
    }
}
