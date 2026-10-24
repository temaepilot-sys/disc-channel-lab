using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Disc2Flac;

public sealed class SpatialBlockRenderer : IDisposable
{
    readonly SpatialInput input;
    readonly SpatialRenderSnapshot snapshot;
    readonly float[][] planar;
    readonly IPreviewPcmRenderer renderer;
    readonly FileStream? sofaLease;
    readonly double[] outputGains = [1, 1];
    long outputClips;
    HeadPose? latestPose;
    HeadphoneOptions? latestOptions;
    double? latestMaster;
    PreviewMixState? latestOutputMix;
    PreviewMixState? latestSourceMix;
    public float[] Output { get; } = new float[512];
    public int TailBlocks => renderer.TailBlocks;
    public void BeginTail() => renderer.BeginTail();
    public long Clips => renderer.ClippedSamples + outputClips;
    public SpatialBlockRenderer(SpatialInput source, SpatialRenderSnapshot settings)
    {
        input = source; snapshot = settings;
        planar = source.Scene.Sources.Select(_ => new float[256]).ToArray();
        try
        {
            if (settings.Options.Method != HeadphoneMethod.MeasuredBrir && settings.Options.Hrtf == HeadphoneHrtf.Sofa)
            {
                sofaLease = new(settings.Options.SofaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexStringLower(SHA256.HashData(sofaLease)) != settings.HrtfHash) throw new InvalidDataException("SOFA changed after the render snapshot; revalidate it.");
            }
            renderer = source.Progressive is not null && source.Presentation == AtmosPresentation.Stereo
                ? new StereoPcmRenderer(["FL", "FR"])
                : new HeadphoneRenderer(source.Scene.Sources.Select(s => s.Id).ToArray(), settings.Options, source.Scene,
                    clampOutput: false, startSample: source.FirstSample);
        }
        catch { sofaLease?.Dispose(); throw; }
    }
    public void Render(ReadOnlySpan<byte> bytes, HeadPose? livePose = null, HeadphoneOptions? liveOptions = null, double? liveMaster = null, PreviewMixState? liveOutputMix = null, PreviewMixState? liveSourceMix = null)
    {
        if (input.Progressive is { } live && renderer is HeadphoneRenderer spatial) spatial.UpdateScene(live.SceneFor(input.Presentation));
        int frames = bytes.Length / (input.StoredChannels * 4);
        if (frames > 256 || bytes.Length % (input.StoredChannels * 4) != 0) throw new InvalidDataException("Incomplete spatial PCM block.");
        for (int c = 0; c < planar.Length; c++)
        {
            int track = input.Scene.Sources[c].TrackIndex;
            Array.Clear(planar[c]);
            for (int f = 0; f < frames; f++)
                planar[c][f] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice((f * input.StoredChannels + track) * 4, 4)));
        }
        latestPose = livePose ?? latestPose; latestOptions = liveOptions ?? latestOptions; latestMaster = liveMaster ?? latestMaster; latestOutputMix = liveOutputMix ?? latestOutputMix;
        latestSourceMix = liveSourceMix ?? latestSourceMix;
        renderer.Render(planar, Output, latestPose ?? snapshot.Pose, latestOptions ?? snapshot.Options, latestSourceMix ?? snapshot.Mix, latestMaster ?? snapshot.Master);
        if ((latestOutputMix ?? snapshot.OutputMix) is { } mix)
        {
            bool solo = mix.Channels?.Any(c => c.Solo) == true;
            var left = mix.Channels?.FirstOrDefault(c => c.Code == "FL"); var right = mix.Channels?.FirstOrDefault(c => c.Code == "FR");
            double Gain(ExperimentalChannel? s, string code) => mix.SoloChannel is { } chosen && chosen != code || s is { Muted: true } || solo && s?.Solo != true ? 0 : s is null ? 1 : (double.IsFinite(s.Gain) ? Math.Clamp(s.Gain, 0, 1) : 0) * (s.InvertPolarity ? -1 : 1);
            double gl = Gain(left, "FL"), gr = Gain(right, "FR");
            for (int i = 0; i < Output.Length; i += 2)
            {
                outputGains[0] += (gl - outputGains[0]) / 240; outputGains[1] += (gr - outputGains[1]) / 240;
                Output[i] *= (float)outputGains[0]; Output[i + 1] *= (float)outputGains[1];
                if (Math.Abs(Output[i]) > 1 || Math.Abs(Output[i + 1]) > 1) outputClips++;
            }
        }
    }
    public void Dispose() { renderer.Dispose(); sofaLease?.Dispose(); }
}

public sealed record LoudnessMetrics(double? IntegratedLufs, double? TruePeakDbfs, double SamplePeak, long ClippedSamples);
public sealed record RenderedAudio(string PcmPath, long SourceFrames, long TailFrames, long ClippedSamples, double Peak,
    double RenderMilliseconds, double MaximumBlockMilliseconds, SpatialInput Input, SpatialRenderSnapshot Snapshot);
public sealed record ComparisonAudio(RenderedAudio A, RenderedAudio B, LoudnessMetrics MetricsA, LoudnessMetrics MetricsB, double GainADb, double GainBDb, bool Matched);

public static class SpatialRendering
{
    public static string CacheRoot => Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "spatial");
    public static async Task<SpatialInput> PrepareAdmAsync(AdmWave adm, long first, long frames, CancellationToken token)
    {
        if (first < 0 || frames <= 0 || first > adm.Scene.DurationSamples - frames) throw new InvalidDataException("ADM interval outside PCM.");
        Directory.CreateDirectory(CacheRoot); string path = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N") + ".f32");
        try
        {
            await using (var file = File.Create(path)) await adm.CopyPcmAsync(file, first, frames, token);
            await using var data = File.OpenRead(path);
            return new(path, adm.Channels, adm.Scene, first, frames, adm.Path + " · " + adm.ProgrammeId,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(data, token)));
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public static Task<RenderedAudio> RenderAsync(SpatialInput input, SpatialRenderSnapshot snapshot, bool keepTail, CancellationToken token) => Task.Run(() =>
    {
        Directory.CreateDirectory(CacheRoot); string path = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N") + ".stereo.f32");
        var watch = Stopwatch.StartNew(); double blockMs = 0, peak = 0; long written = 0;
        try
        {
            if (snapshot.Options.Method == HeadphoneMethod.MeasuredBrir)
            {
                var brir = BrirLibrary.Prepare(snapshot.Options.BrirPath, snapshot.Options.BrirHash, token);
                snapshot = snapshot with { Pose = new(0,0,0), HrtfHash = brir.Hash,
                    EngineVersion = "SpatialLab BRIR-1 / partitioned FFT 256-1024-4096", Options = snapshot.Options with { Room = new(), UseTracker = false },
                    Brir = new(brir.Profile.Name, brir.Hash, brir.Profile, brir.Assets, brir.ResamplingLatencyFrames) };
            }
            using var renderer = new SpatialBlockRenderer(input, snapshot);
            using var source = SpatialPcmSource.Open(input); using var destination = File.Create(path);
            using var pcmHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[256 * input.StoredChannels * 4]; long remaining = input.Frames;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested(); int n = (int)Math.Min(256, remaining);
                source.ReadExactly(bytes.AsSpan(0, n * input.StoredChannels * 4));
                pcmHash.AppendData(bytes.AsSpan(0, n * input.StoredChannels * 4));
                long before = Stopwatch.GetTimestamp(); renderer.Render(bytes.AsSpan(0, n * input.StoredChannels * 4));
                blockMs = Math.Max(blockMs, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                int count = keepTail ? 256 : n; Write(count); remaining -= n;
            }
            if (keepTail) { int tails = renderer.TailBlocks; renderer.BeginTail(); for (int b = 0; b < tails; b++) { token.ThrowIfCancellationRequested(); renderer.Render([]); Write(256); } }
            return new RenderedAudio(path, input.Frames, written - input.Frames, renderer.Clips, peak, watch.Elapsed.TotalMilliseconds, blockMs,
                input with { Scene = input.Progressive?.SceneFor(input.Presentation) ?? input.Scene, PcmHash = Convert.ToHexStringLower(pcmHash.GetHashAndReset()) }, snapshot);
            void Write(int count)
            {
                var samples = renderer.Output.AsSpan(0, count * 2);
                foreach (float v in samples) { if (!float.IsFinite(v)) throw new InvalidDataException("Nonfinite spatial DSP output."); peak = Math.Max(peak, Math.Abs(v)); }
                destination.Write(MemoryMarshal.AsBytes(samples)); written += count;
            }
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }, token);

    public static async Task<LoudnessMetrics> MeasureAsync(RenderedAudio audio, string ffmpeg, CancellationToken token, double gainDb = 0)
    {
        string gain = gainDb.ToString("R", CultureInfo.InvariantCulture);
        var result = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audio.PcmPath,
            "-af", $"volume={gain}dB,loudnorm=I=-23:TP=-1:LRA=7:print_format=json", "-f", "null", "-"], token);
        int first = result.IndexOf('{'), last = result.LastIndexOf('}');
        if (first < 0 || last < first) throw new InvalidDataException("FFmpeg did not return loudness measurements.");
        using var json = JsonDocument.Parse(result[first..(last + 1)]);
        double? Value(string key) => double.TryParse(json.RootElement.GetProperty(key).GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
        return new(Value("input_i"), Value("input_tp"), audio.Peak * Math.Pow(10, gainDb / 20), audio.ClippedSamples);
    }
    public static async Task<ComparisonAudio> CompareAsync(SpatialInput input, SpatialRenderSnapshot a, SpatialRenderSnapshot b, string ffmpeg, bool match, CancellationToken token, double maximumRelativeDb = 6)
    {
        var ra = await RenderAsync(input, a, false, token); RenderedAudio? rb = null;
        try
        {
            rb = await RenderAsync(input, b, false, token);
            var ma = await MeasureAsync(ra, ffmpeg, token); var mb = await MeasureAsync(rb, ffmpeg, token);
            double ga = 0, gb = 0; bool valid = match && ma.IntegratedLufs is not null && mb.IntegratedLufs is not null && Math.Abs(ma.IntegratedLufs.Value - mb.IntegratedLufs.Value) <= maximumRelativeDb;
            if (valid)
            {
                double target = Math.Min(ma.IntegratedLufs!.Value, mb.IntegratedLufs!.Value);
                ga = target - ma.IntegratedLufs.Value; gb = target - mb.IntegratedLufs.Value;
                double extra = Math.Max(0, Math.Max((ma.TruePeakDbfs ?? 0) + ga, (mb.TruePeakDbfs ?? 0) + gb) + 1);
                ga -= extra; gb -= extra; // A common attenuation preserves the loudness match; never boosts or limits.
            }
            return new(ra, rb, ma, mb, ga, gb, valid);
        }
        catch { File.Delete(ra.PcmPath); if (rb is not null) File.Delete(rb.PcmPath); throw; }
    }
    public static async Task ExportAsync(RenderedAudio audio, string destination, string ffmpeg, bool flac, CancellationToken token,
        int outputRate = 48000, int outputBits = 24, IReadOnlyDictionary<string, string>? tags = null)
    {
        if (outputRate is not (44100 or 48000) || outputBits is not (16 or 24)) throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (flac && audio.Peak > 1) throw new InvalidDataException($"PCM peak is {20 * Math.Log10(audio.Peak):0.00} dBFS. Reduce gain or export float WAV. Automatic normalization is OFF.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || File.Exists(destination + ".render.json")) throw new IOException("Destination exists. Choose a new filename.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        bool moved = false;
        try
        {
            List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", audio.PcmPath];
            if (flac) args.AddRange(["-af", $"aresample={outputRate}:osf={(outputBits == 16 ? "s16" : "s32")}:output_sample_bits={outputBits}:dither_method=triangular", "-c:a", "flac", "-sample_fmt", outputBits == 16 ? "s16" : "s32", "-bits_per_raw_sample", outputBits.ToString(), "-f", "flac"]);
            else args.AddRange(["-c:a", "pcm_f32le", "-rf64", "auto", "-f", "wav"]);
            if (tags is not null) foreach (var pair in tags) args.AddRange(["-metadata", pair.Key + "=" + pair.Value]);
            args.Add(pending); await RunAsync(ffmpeg, args, token);
            var metrics = await MeasureAsync(audio, ffmpeg, token);
            File.Move(pending, destination); moved = true;
            SpatialJson.Save(destination + ".render.json", new { schemaVersion = 1, audio.SourceFrames, audio.TailFrames, audio.Peak, audio.ClippedSamples,
                audio.RenderMilliseconds, audio.MaximumBlockMilliseconds, Input = new { audio.Input.SourceName, audio.Input.PcmHash, audio.Input.FirstSample, audio.Input.Frames },
                Scene = ExportScene(audio.Input), Settings = audio.Snapshot, Metrics = metrics, Encoding = flac ? $"FLAC {outputBits}-bit / {outputRate} Hz TPDF dither" : "WAV float32; values above unity retained",
                DspTap = "Shared DSP float bus, after mixer/master/spatial renderer, before endpoint guards/fades/clamp. Pose fixed; no tracker.",
                Quantizer = flac ? $"FFmpeg aresample/output_sample_bits={outputBits} triangular dither; seed is encoder-managed" : "none" });
        }
        catch { if (moved) File.Delete(destination); throw; }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    static SpatialScene ExportScene(SpatialInput input) => input.Wave is null ? input.Scene : input.Scene with
    { Sources = input.Scene.Sources.Select(s => { int first = Math.Max(0, s.BlockIndex(input.FirstSample) - 1), last = s.BlockIndex(input.FirstSample + input.Frames); return s with { Blocks = s.Blocks[first..(last + 1)] }; }).ToArray() };
    public static async Task<string> RunAsync(string file, IEnumerable<string> args, CancellationToken token)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start audio tool.");
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(token); var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); string text = await error; string stdout = await output; if (process.ExitCode != 0) throw new IOException(text + stdout); return text + (stdout.Length == 0 ? "" : "\n" + stdout); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
