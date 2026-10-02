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
                Check(store.Load() is { SyncReference: "listener", ParticleSpeed: 2, ListenerSize: 1.5, ListenerBrightness: 1.4, SpeakerBeams: false, BeamStrength: .8 }, "Legacy preferences retain particle view and default beam strength");
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
