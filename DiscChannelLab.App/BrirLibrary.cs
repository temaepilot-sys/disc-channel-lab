using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureHDF;
using PureHDF.Selections;

namespace Disc2Flac;

public sealed record BrirRoute(string Channel, string File, int Measurement = 0, int LeftEar = 0, int RightEar = 1);
public sealed record BrirProfile(string Name, BrirRoute[] Routes, string Source = "", string License = "", int Version = 1,
    bool LayoutConfirmed = false, bool CenteredDryLfe = false);
public sealed record BrirResponse(float[] Left, float[] Right);
public sealed record BrirAsset(string File, string Sha256, int Measurement, int LeftEar, int RightEar, int OriginalRate, int Frames);
public sealed record PreparedBrir(string Path, string Hash, BrirProfile Profile, Dictionary<string, BrirResponse> Responses, BrirAsset[] Assets, int ResamplingLatencyFrames = 0)
{
    public void RequireChannels(string[] channels, SpatialScene? scene = null)
    {
        if (scene?.Sources.Any(s => s.Kind != SpatialSourceKind.ChannelBed || s.Blocks.Length != 1) == true)
            throw new NotSupportedException("Measured BRIR requires a fixed speaker bed. For Atmos choose the 5.1 presentation; moving ADM objects need a different BRIR dataset/renderer.");
        var absent = channels.Where(c => !Responses.ContainsKey(c) && !(c == "LFE" && Profile.CenteredDryLfe)).ToArray();
        if (absent.Length > 0) throw new InvalidDataException("BRIR has no explicit measurement for: " + string.Join(", ", absent) + ". Add these routes, or select Pure HRIR. Channels are never guessed or omitted.");
    }
}

/// <summary>External user data only. SHA-256 includes the exact manifest and every source file.
/// Static measured routes are explicit; M in a SOFA file is never assumed to mean a speaker direction.</summary>
public static class BrirLibrary
{
    static readonly ConcurrentDictionary<string, PreparedBrir> prepared = new();
    public static string Name(HeadphoneOptions options) => prepared.TryGetValue(options.BrirHash, out var item) ? item.Profile.Name : System.IO.Path.GetFileName(options.BrirPath);
    public static readonly string[] Channels = ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR", "BC"];
    public static string LastProfilePath => AppDataPaths.Current.PreferencePath("profiles/brir-last.json");
    public static BrirProfile ReadProfile(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("BRIR manifest exceeds 256 KiB.");
        return JsonSerializer.Deserialize<BrirProfile>(File.ReadAllText(path), SpatialJson.Options) ?? throw new InvalidDataException("Empty BRIR manifest.");
    }
    public static Task<PreparedBrir> PrepareAsync(string path, string? expected = null, CancellationToken token = default) => Task.Run(() => Prepare(path, expected, token), token);
    public static PreparedBrir GetPrepared(HeadphoneOptions options) => prepared.TryGetValue(options.BrirHash, out var p) ? p
        : throw new InvalidOperationException("Load and validate the BRIR profile in Spatial Lab first.");
    public static PreparedBrir Prepare(string path, string? expected = null, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        using var manifestLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var leases = new ReadLeases(); var profile = ReadProfile(path);
        if (profile.Version != 1 || string.IsNullOrWhiteSpace(profile.Name) || profile.Routes is null || profile.Routes.Length is < 1 or > 9)
            throw new InvalidDataException("BRIR v1 requires a name and 1–9 explicitly mapped speaker routes.");
        if (!profile.LayoutConfirmed) throw new InvalidDataException("Confirm the measured speaker positions, neutral head orientation and left/right ear order from the dataset documentation first.");
        if (profile.Routes.Any(r => r is null || !Channels.Contains(r.Channel) || string.IsNullOrWhiteSpace(r.File) || r.Measurement < 0 || r.LeftEar is < 0 or > 1 || r.RightEar != 1 - r.LeftEar) || profile.Routes.Select(r => r.Channel).Distinct().Count() != profile.Routes.Length)
            throw new InvalidDataException("Invalid / duplicate BRIR channel, measurement index or left/right ear mapping.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(File.ReadAllBytes(path));
        var responses = new Dictionary<string, BrirResponse>(); List<BrirAsset> assets = [];
        var loaded = new Dictionary<string, (float[] L, float[] R, int Rate)>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in profile.Routes)
        {
            token.ThrowIfCancellationRequested();
            string file = System.IO.Path.GetFullPath(route.File, System.IO.Path.GetDirectoryName(path)!);
            if (!hashes.TryGetValue(file, out string? hash))
            {
                var lease = leases.Open(file);
                if (lease.Length is <= 0 or > 512L * 1024 * 1024) throw new InvalidDataException("BRIR source must be 1 byte to 512 MiB.");
                hash = Convert.ToHexStringLower(SHA256.HashData(lease)); hashes.Add(file, hash);
            }
            digest.AppendData(Encoding.UTF8.GetBytes(hash));
            string key = file + ":" + route.Measurement;
            if (!loaded.TryGetValue(key, out var data))
            {
                data = System.IO.Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".wav" => ReadWave(file, route.Measurement),
                    ".sofa" => ReadSofa(file, route.Measurement),
                    _ => throw new NotSupportedException("BRIR input: stereo PCM/float WAV, or SOFA FIR M×2×N. Select a BRIR manifest, not an arbitrary audio recording.")
                };
                data = (Resample(data.L, data.Rate, token), Resample(data.R, data.Rate, token), data.Rate); loaded.Add(key, data);
            }
            float[] l = route.LeftEar == 0 ? data.L : data.R, r = route.RightEar == 1 ? data.R : data.L;
            if (l.All(v => v == 0) && r.All(v => v == 0)) throw new InvalidDataException("BRIR contains only silence: " + file);
            if (l.Concat(r).Any(v => !float.IsFinite(v) || Math.Abs(v) > 64)) throw new InvalidDataException("BRIR contains nonfinite or excessive coefficients.");
            responses.Add(route.Channel, new(l, r)); assets.Add(new(file, hash, route.Measurement, route.LeftEar, route.RightEar, data.Rate, Math.Max(l.Length, r.Length)));
        }
        string identity = Convert.ToHexStringLower(digest.GetHashAndReset());
        if (!string.IsNullOrEmpty(expected) && identity != expected) throw new InvalidDataException("BRIR manifest or measurement changed. Revalidate it before playback / export.");
        int resamplingDelay = assets.Any(a => a.OriginalRate != 48000) ? 192 : 0;
        if (resamplingDelay != 0)
            for (int i = 0; i < assets.Count; i++) if (assets[i].OriginalRate == 48000)
            {
                var code = profile.Routes[i].Channel; var response = responses[code];
                float[] Pad(float[] ir) { var value = new float[ir.Length + 192]; Array.Copy(ir, 0, value, 192, ir.Length); return value; }
                responses[code] = new(Pad(response.Left), Pad(response.Right)); assets[i] = assets[i] with { Frames = assets[i].Frames + 192 };
            }
        var result = new PreparedBrir(path, identity, profile, responses, assets.ToArray(), resamplingDelay);
        // Keep a few immutable loaded profiles; real-time rendering uses only these arrays.
        if (prepared.Count >= 8 && !prepared.ContainsKey(identity)) prepared.Clear();
        prepared[identity] = result; return result;
    }
    static (float[] L, float[] R, int Rate) ReadWave(string path, int measurement)
    {
        if (measurement != 0) throw new InvalidDataException("Stereo WAV has one response (measurement index 0).");
        using var f = File.OpenRead(path); using var reader = new BinaryReader(f);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("BRIR WAV must be RIFF PCM / float.");
        reader.ReadUInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAV.");
        byte[]? fmt = null; long start = 0, bytes = 0;
        while (f.Position + 8 <= f.Length)
        {
            string chunk = new(reader.ReadChars(4)); uint length = reader.ReadUInt32(); long next = checked(f.Position + length + (length & 1));
            if (next > f.Length) throw new InvalidDataException("Truncated BRIR WAV.");
            if (chunk == "fmt ") { if (length is < 16 or > 4096) throw new InvalidDataException("Invalid WAV format chunk."); fmt = reader.ReadBytes((int)length); }
            if (chunk == "data") { start = f.Position; bytes = length; }
            f.Position = next;
        }
        if (fmt is null || bytes <= 0) throw new InvalidDataException("Missing WAV format / response.");
        int type = BinaryPrimitives.ReadUInt16LittleEndian(fmt), channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4)), align = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12)), bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
        if (type == 0xfffe)
        {
            if (fmt.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(18)) != bits ||
                new Guid(fmt.AsSpan(24, 16)) is var subtype && subtype != new Guid("00000001-0000-0010-8000-00aa00389b71") && subtype != new Guid("00000003-0000-0010-8000-00aa00389b71"))
                throw new InvalidDataException("Unsupported extensible WAV BRIR encoding.");
            type = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(20)); if (mask is not (0 or 3)) throw new InvalidDataException("Stereo WAV BRIR requires a left / right channel mask.");
        }
        if (channels != 2 || type is not (1 or 3) || bits is not (16 or 24 or 32) || type == 3 && bits != 32 || align != bits / 8 * 2 || bytes % align != 0 || rate is < 8000 or > 192000 || bytes / align > rate * 6)
            throw new InvalidDataException("BRIR WAV: stereo, 16/24/32-bit PCM or float32, 8–192 kHz, maximum 6 seconds.");
        int n = (int)(bytes / align); float[] l = new float[n], r = new float[n]; f.Position = start;
        float Sample() => type == 3 ? reader.ReadSingle() : bits switch { 16 => reader.ReadInt16() / 32768f, 32 => reader.ReadInt32() / 2147483648f, _ => ((reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16) << 8 >> 8) / 8388608f };
        for (int j = 0; j < n; j++) { l[j] = Sample(); r[j] = Sample(); }
        return (l, r, rate);
    }
    static (float[] L, float[] R, int Rate) ReadSofa(string path, int measurement)
    {
        using var h5 = H5File.OpenRead(path); var ir = h5.Dataset("Data.IR"); var shape = ir.Space.Dimensions;
        string convention = h5.Attribute("SOFAConventions").Read<string>();
        if (convention.Contains("FreeField", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This is free-field HRIR, not a measured room BRIR.");
        if (h5.Attribute("DataType").Read<string>() != "FIR" || shape.Length != 3 || shape[0] is 0 or > 100000 || shape[1] != 2 || shape[2] is 0 or > 1152000 || measurement >= (long)shape[0])
            throw new NotSupportedException("BRIR SOFA currently requires FIR M×2×N and an explicit 0-based measurement index. FIR-E / multiple emitters are not silently flattened.");
        double[] Numbers(string name)
        {
            var d = h5.Dataset(name); if (d.Space.Dimensions.Aggregate(1UL, (a,b) => checked(a*b)) > 1_000_000) throw new InvalidDataException("SOFA metadata too large.");
            return d.Type.Size == 4 ? d.Read<float[]>().Select(x => (double)x).ToArray() : d.Type.Size == 8 ? d.Read<double[]>() : throw new InvalidDataException("SOFA requires float/double coefficients.");
        }
        var rates = Numbers("Data.SamplingRate");
        var rateUnits = h5.Dataset("Data.SamplingRate").Attribute("Units").Read<string>();
        if (!rateUnits.Equals("hertz", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SOFA sampling rate units must be hertz.");
        if (rates.Length != 1 || !double.IsFinite(rates[0]) || rates[0] % 1 != 0 || rates[0] is < 8000 or > 192000 || shape[2] > rates[0] * 6)
            throw new InvalidDataException("SOFA requires one 8–192 kHz rate, maximum 6 seconds (no tail trimming).");
        // Receiver order is explicit in the manifest, never inferred from channel count.
        // Measurement index can represent head orientation OR source position. Dataset documentation must be checked.
        var delays = Numbers("Data.Delay");
        var delayShape = h5.Dataset("Data.Delay").Space.Dimensions;
        if (delayShape.Length != 2 || delayShape[1] != 2 || delayShape[0] != 1 && delayShape[0] != shape[0]) throw new InvalidDataException("SOFA Data.Delay must be 1×2 or M×2.");
        float[] Ear(int ear)
        {
            var selection = new HyperslabSelection(3, [(ulong)measurement, (ulong)ear, 0], [1, 1, shape[2]]);
            float[] raw = ir.Type.Size == 4 ? ir.Read<float[]>(fileSelection: selection) : ir.Type.Size == 8 ? ir.Read<double[]>(fileSelection: selection).Select(x => (float)x).ToArray() : throw new InvalidDataException("Invalid SOFA IR numeric type.");
            double delay = delays[delays.Length == 2 ? ear : measurement * 2 + ear];
            if (!double.IsFinite(delay) || delay < 0 || delay % 1 != 0 || delay + raw.Length > rates[0] * 6)
                throw new NotSupportedException("BRIR Data.Delay must be nonnegative whole samples within the 6 second limit. Fractional delay is not rounded or discarded.");
            var result = new float[raw.Length + (int)delay]; Array.Copy(raw, 0, result, (int)delay, raw.Length); return result;
        }
        return (Ear(0), Ear(1), (int)rates[0]);
    }
    static float[] Resample(float[] data, int rate, CancellationToken token)
    {
        if (rate == 48000) return data;
        int length = checked((int)Math.Ceiling(data.Length * (48000d / rate)) + 384);
        float[] result = new float[length]; double ratio = rate / 48000d, cutoff = Math.Min(1, 1 / ratio);
        int radius = (int)Math.Ceiling(32 / cutoff);
        // Retain both sinc wings even when a response starts at sample zero. All routes in a
        // resampled profile receive the same documented 192-frame (4 ms) conversion latency.
        // Rate scaling preserves IR transfer gain; this is not ordinary audio resampling.
        for (int j = 0; j < length; j++)
        {
            if ((j & 4095) == 0) token.ThrowIfCancellationRequested();
            double time = (j - 192) * ratio, sum = 0; int center = (int)Math.Floor(time);
            for (int k = Math.Max(0, center - radius); k <= Math.Min(data.Length - 1, center + radius); k++)
            {
                double d = time - k, x = d * cutoff, sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                if (Math.Abs(d) > radius) continue;
                double window = .5 + .5 * Math.Cos(Math.PI * d / radius);
                sum += data[k] * sinc * cutoff * window;
            }
            result[j] = (float)(sum * ratio);
        }
        return result;
    }
    sealed class ReadLeases : IDisposable
    {
        readonly List<FileStream> files = [];
        public FileStream Open(string path) { var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); files.Add(f); return f; }
        public void Dispose() { foreach (var f in files) f.Dispose(); }
    }
}
