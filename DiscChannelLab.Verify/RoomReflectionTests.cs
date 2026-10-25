using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class RoomReflectionTests
{
    public static async Task Run(string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        List<string> checks = []; List<object> measurements = [];
        void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
        string[] codes = ["FL", "FR", "FC", "LFE", "SL", "SR"];
        const int frames = 48000 * 6;
        var random = new Random(7401); var source = codes.Select(_ => new float[frames]).ToArray();
        // Independent, decaying broadband attacks reveal early reflections without pre-recorded room sound.
        for (int c = 0; c < codes.Length; c++)
            for (int f = 0; f < frames; f++)
                source[c][f] = c == 3 ? 0 : (float)(.1 * (2 * random.NextDouble() - 1) * Math.Exp(-(f % 24000) / 3200d));
        var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio, HeadroomDb = -12, SmoothingMs = 0 };
        var mix = new PreviewMixState(StereoMixSettings.Default, null);
        var scene = SpatialScene.ChannelBed(codes, frames);
        var dry = Render(new());
        var studio = new SpatialRoomOptions { Mode = "Studio" };
        var listening = new SpatialRoomOptions { Mode = "Listening Room", Width = 6, Depth = 5, Height = 3.2,
            ReflectionDb = -12, MidAbsorption = .35, HighAbsorption = .6 };
        foreach (var room in new[] { studio, listening })
        {
            var wet = Render(room); var reflected = Render(room with { SoloReflections = true });
            double error = wet.Zip(dry.Zip(reflected)).Max(x => Math.Abs(x.First - x.Second.First - x.Second.Second));
            Check(error < 2e-7, room.Mode + ": native HRTF wet output equals dry plus isolated reflections");
            Check(reflected.Any(x => Math.Abs(x) > 1e-7), room.Mode + ": reflection bus contains nonzero PCM");
            Check(Render(room with { Mode = "Pure HRTF" }).SequenceEqual(dry), room.Mode + ": OFF restores bit-identical dry output");
            var reduced = Render(room with { SoloReflections = true, ReflectionDb = room.ReflectionDb - 6 });
            Check(reflected.Zip(reduced).Max(x => Math.Abs(x.Second - x.First * Math.Pow(10, -6d / 20))) < 1e-7,
                room.Mode + ": reflection level reaches native DSP with the correct dB gain");
            var paths = scene.Sources.Where(s => !s.IsLfe).SelectMany(s => Enumerable.Range(0, 6)
                .Select(w => EarlyReflections.Path(s.At(0).Position * (float)room.SpeakerRadius, room, w))).ToArray();
            double wetDry = Db(Rms(wet) / Rms(dry)), reflectionDry = Db(Rms(reflected) / Rms(dry));
            measurements.Add(new { room.Mode, room.ReflectionDb, ReflectionToDryRmsDb = reflectionDry, WetToDryRmsDb = wetDry,
                MinimumDelayMs = paths.Min(p => p.DelaySamples) / 48, MaximumDelayMs = paths.Max(p => p.DelaySamples) / 48,
                MinimumPathDbBeforeAbsorption = paths.Min(p => Db(p.RelativeGain)), MaximumPathDbBeforeAbsorption = paths.Max(p => Db(p.RelativeGain)) });
            Console.WriteLine($"MEASURE {room.Mode}: reflected/dry {reflectionDry:0.00} dB, wet/dry {wetDry:0.00} dB, delay {paths.Min(p => p.DelaySamples) / 48:0.0}–{paths.Max(p => p.DelaySamples) / 48:0.0} ms");
            Write(room.Mode.Replace(' ', '-').ToLowerInvariant() + ".f32", wet);
            Write(room.Mode.Replace(' ', '-').ToLowerInvariant() + "-reflections-only.f32", reflected);
        }
        var audition = Render(listening with { SoloReflections = true, ReflectionDb = -6 });
        var reference = Render(listening with { SoloReflections = true });
        double auditionIncrease = Db(Rms(audition) / Rms(reference));
        Check(Math.Abs(auditionIncrease - 6) < .001, "The suggested -6 dB audition raises only the reflection bus by exactly 6 dB");
        Write("listening-room-minus6-reflections-only.f32", audition);
        Write("listening-room-minus6.f32", Render(listening with { ReflectionDb = -6 }));
        var big = SpatialRoomOptions.Preset("Big Room"); big.Validate();
        var bigOnly = Render(big with { SoloReflections = true }); var bigWet = Render(big);
        var bigPaths = scene.Sources.Where(s => !s.IsLfe).SelectMany(s => Enumerable.Range(0, 6)
            .Select(w => EarlyReflections.Path(s.At(0).Position * (float)big.SpeakerRadius, big, w))).ToArray();
        double bigRatio = Db(Rms(bigOnly) / Rms(dry));
        Check(bigOnly.Zip(reference).Sum(x => (double)x.First * x.First) > 2 * reference.Sum(x => (double)x * x),
            "Big Room has substantially more audible reflected energy than Listening Room on the reference source");
        Check(bigPaths.Max(p => p.DelaySamples) / 48 > 30 && bigPaths.Min(p => p.DelaySamples) >= 0,
            "Big Room includes longer paths while retaining causal reflection timing");
        Check(bigWet.Zip(dry.Zip(bigOnly)).Max(x => Math.Abs(x.First - x.Second.First - x.Second.Second)) < 2e-7 && bigWet.All(float.IsFinite),
            "Big Room preserves the dry path and adds only the finite reflected bus");
        Check(SpatialRoomOptions.Preset("Studio") == studio && SpatialRoomOptions.Preset("Listening Room") == listening && !SpatialRoomOptions.Preset("Pure HRTF").Enabled,
            "Existing room presets and the default OFF state remain unchanged");
        measurements.Add(new { big.Mode, big.ReflectionDb, ReflectionToDryRmsDb = bigRatio, MinimumDelayMs = bigPaths.Min(p => p.DelaySamples) / 48,
            MaximumDelayMs = bigPaths.Max(p => p.DelaySamples) / 48 });
        Console.WriteLine($"MEASURE Big Room: reflected/dry {bigRatio:0.00} dB; maximum delay {bigPaths.Max(p => p.DelaySamples) / 48:0.0} ms");
        Write("big-room.f32", bigWet); Write("big-room-reflections-only.f32", bigOnly);
        Check(Render(studio with { SoloReflections = true, LowAbsorption = 1, MidAbsorption = 1, HighAbsorption = 1 }).All(x => x == 0),
            "Full absorption silences the reflected bus");
        Write("pure-hrtf.f32", dry);
        var tools = new ToolPaths();
        foreach (var pcm in Directory.GetFiles(root, "*.f32"))
        {
            string wav = Path.ChangeExtension(pcm, ".wav");
            if (!File.Exists(wav)) await SpatialRendering.RunAsync(tools.Ffmpeg,
                ["-v", "error", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", pcm, "-c:a", "pcm_f32le", wav], CancellationToken.None);
        }
        SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, measurements,
            Input = "Original synthesized independent six-channel decaying noise, silent LFE; not a listening assessment", SampleRate = 48000 });

        float[] Render(SpatialRoomOptions room)
        {
            using var dsp = new HeadphoneRenderer(codes, options with { Room = room }, scene, clampOutput: false);
            var input = codes.Select(_ => new float[256]).ToArray(); var block = new float[512]; var result = new float[frames * 2];
            var watch = Stopwatch.StartNew();
            for (int start = 0; start < frames; start += 256)
            {
                int count = Math.Min(256, frames - start);
                for (int c = 0; c < codes.Length; c++) { Array.Clear(input[c]); source[c].AsSpan(start, count).CopyTo(input[c]); }
                dsp.Render(input, block, new(0, 0, 0), options with { Room = room }, mix, 1);
                block.AsSpan(0, count * 2).CopyTo(result.AsSpan(start * 2));
            }
            Console.WriteLine($"RENDER {room.Mode} solo={room.SoloReflections}: {watch.Elapsed.TotalMilliseconds:0.0} ms / 6000 ms audio");
            return result;
        }
        void Write(string name, float[] samples) => File.WriteAllBytes(Path.Combine(root, name), MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
    }
    static double Rms(float[] samples) => Math.Sqrt(samples.Sum(x => (double)x * x) / samples.Length);
    static double Db(double value) => 20 * Math.Log10(Math.Max(1e-30, value));
}
