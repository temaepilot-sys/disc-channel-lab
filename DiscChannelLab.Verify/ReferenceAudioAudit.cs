using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ReferenceAudioAudit
{
    public static async Task Run(string output, string prior, string sofa)
    {
        string root = Path.GetFullPath(output), previous = Path.GetFullPath(prior); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        var checks = new List<string>(); var metrics = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        var context = new AssemblyLoadContext("reference-prior", true);
        context.Resolving += (_, name) => { string path = Path.Combine(Path.GetDirectoryName(previous)!, name.Name + ".dll"); return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null; };
        var assembly = context.LoadFromAssemblyPath(previous);
        object Old(string type, object value) => JsonSerializer.Deserialize(JsonSerializer.Serialize(value), assembly.GetType("Disc2Flac." + type)!)!;
        string[] codes = ["FL", "FR", "FC", "LFE", "SL", "SR"];
        var pose = new HeadPose(0, 0, 0); var mix = new PreviewMixState(StereoMixSettings.Default, null);
        foreach (string mode in new[] { "Stereo", "Direct", "HRTF builtin", "HRTF SOFA" })
        {
            var options = new HeadphoneOptions { Enabled = mode != "Stereo", Method = mode == "Direct" ? HeadphoneMethod.Direct : HeadphoneMethod.SteamAudio,
                Hrtf = mode == "HRTF SOFA" ? HeadphoneHrtf.Sofa : HeadphoneHrtf.BuiltIn, SofaPath = mode == "HRTF SOFA" ? Path.GetFullPath(sofa) : "" };
            using IPreviewPcmRenderer current = mode == "Stereo" ? new StereoPcmRenderer(codes) : new HeadphoneRenderer(codes, options);
            var type = assembly.GetType("Disc2Flac." + (mode == "Stereo" ? "StereoPcmRenderer" : "HeadphoneRenderer"))!;
            using var old = (IDisposable)(mode == "Stereo" ? Activator.CreateInstance(type, new object[] { codes })! :
                Activator.CreateInstance(type, new object?[] { codes, Old("HeadphoneOptions", options), null, true, 0L })!);
            var render = type.GetMethod("Render")!; var oldPose = Old("HeadPose", pose); var oldOptions = Old("HeadphoneOptions", options); var oldMix = Old("PreviewMixState", mix);
            var input = codes.Select(_ => new float[256]).ToArray(); var actual = new float[512]; var expected = new float[512];
            using var gold = new MemoryStream(); double error = 0, peak = 0, energy = 0;
            int blocks = 24 + current.TailBlocks;
            for (int b = 0; b < blocks; b++)
            {
                foreach (var channel in input) Array.Clear(channel);
                if (b < 6) input[b][0] = .25f;
                if (b is >= 6 and < 24) for (int c = 0; c < 6; c++) for (int f = 0; f < 256; f++)
                    input[c][f] = (float)(.025 * Math.Sin(2 * Math.PI * (60 + c * 251) * (b * 256 + f) / 48000));
                current.Render(input, actual, pose, options, mix, 1);
                render.Invoke(old, [input, expected, oldPose, oldOptions, oldMix, 1d]);
                for (int i = 0; i < 512; i++) { error = Math.Max(error, Math.Abs(actual[i] - expected[i])); peak = Math.Max(peak, Math.Abs(actual[i])); energy += actual[i] * actual[i]; }
                byte[] bytes = new byte[2048]; Buffer.BlockCopy(expected, 0, bytes, 0, bytes.Length); gold.Write(bytes);
            }
            Check(error == 0 && double.IsFinite(peak), mode + ": current float PCM exactly equals the preserved pre-streaming build");
            string path = Path.Combine(root, mode.Replace(' ', '-') + ".golden.f32"); File.WriteAllBytes(path, gold.ToArray());
            metrics.Add(new { mode, maxError = error, peak, rms = Math.Sqrt(energy / (blocks * 512)), clips = current.ClippedSamples,
                goldenSha256 = Convert.ToHexStringLower(SHA256.HashData(gold.ToArray())), headroomDb = mode == "Stereo" ? 0 : options.HeadroomDb,
                lfeGain = mode == "Stereo" ? 0 : options.LfeGain, interpolation = mode.StartsWith("HRTF") ? "Bilinear (native enum 1)" : "none" });
        }
        context.Unload();
        var left = new double[6]; var right = new double[6]; StereoPreviewMixer.BuildWeights(codes, mix, left, right);
        double g = 1 / (1 + Math.Sqrt(2));
        Check(Math.Abs(left[0] - g) < 1e-12 && Math.Abs(left[2] - g / Math.Sqrt(2)) < 1e-12 && Math.Abs(left[4] - g / Math.Sqrt(2)) < 1e-12,
            "Existing 5.1 matrix is Lo/Ro ratios with a separate conservative common gain, not different front/center ratios");
        Check(left[3] == 0 && right[3] == 0, "Existing speaker stereo excludes source LFE, independently of subwoofer ownership");
        string[] seven = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"]; var l7 = new double[8]; var r7 = new double[8];
        StereoPreviewMixer.BuildWeights(seven, mix, l7, r7);
        Check(Math.Abs(l7[4] + l7[6] - left[4]) < 1e-12 && Math.Abs(r7[5] + r7[7] - right[5]) < 1e-12,
            "Existing 7.1 shares surround weight across side/back without doubling the surround bus");
        using (var stereo = new StereoPcmRenderer(["FL", "FR"]))
        {
            var input = new[] { new float[256], new float[256] }; var result = new float[512];
            for (int f = 0; f < 256; f++) { input[0][f] = (f - 128) / 256f; input[1][f] = -input[0][f]; }
            stereo.Render(input, result, new(95, 50, 20), new() { Enabled = true, HeadroomDb = -30, LfeGain = 1 }, mix, 1);
            Check(Enumerable.Range(0, 256).All(f => result[f * 2] == input[0][f] && result[f * 2 + 1] == input[1][f]),
                "Ordinary stereo is sample-exact and bypasses HRTF, pose, headphone headroom and subwoofer processing");
        }
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var runner = new ProcessRunner(log); var probe = new FfprobeService(tools, runner);
        var navigation = new AudioNavigationService(tools, runner, probe, log);
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            string path = Path.Combine(root, "generated-" + rate + ".wav");
            await SpatialRendering.RunAsync(tools.Ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", $"sine=frequency=1000:sample_rate={rate}:duration=1", "-af", "pan=stereo|c0=c0|c1=c0", "-c:a", "pcm_s24le", path], CancellationToken.None);
            var disc = await new AudioFileReader(probe, log).AnalyzeAsync([path], CancellationToken.None); var pl = disc.Playlists.Single(); var stream = pl.FileAnalysis!.Streams.Single();
            using var pcm = new MemoryStream(); await navigation.WriteSpatialPcmAsync(disc, pl, stream, 0, 45000, pcm, CancellationToken.None);
            Check(pcm.Length == 48000 * 2 * 4, rate + " Hz input: decoder produces exactly 48000 stereo float frames per second");
            var bytes = pcm.ToArray(); double squared = 0, max = 0;
            for (int f = 256; f < 48000 - 256; f++) { float v = BitConverter.ToSingle(bytes, f * 8); squared += v * v; max = Math.Max(max, Math.Abs(v)); }
            double rms = Math.Sqrt(squared / (48000 - 512));
            Check(Math.Abs(rms - .125 / Math.Sqrt(2)) < .0001 && max < .126, rate + " Hz input: resampling preserves the generated tone amplitude without a gain boost");
            metrics.Add(new { inputRate = rate, outputRate = 48000, frames = 48000, rms, peak = max });
        }
        SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, metrics,
            baselineAssembly = previous, baselineSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(previous))),
            scope = "PR-0 / v1.1; no new Reference preset, no default DSP changes", humanListening = false,
            pending = new[] { "Pink/noise/sweep and full direction/ITD measurement matrix", "Long-term 44.1/96 kHz endpoint soak", "Physical 2.0/2.1 audition", "Mobile foobar2000", "Continuous-album FLAC DSP history", "PR-S1 and later milestones" } });
    }
}
