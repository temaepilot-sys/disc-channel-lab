using PureHDF;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace Disc2Flac;

public sealed record HrtfEntry(string Id, string Name, string Path, string Hash, long Bytes, string Convention,
    int Directions, int Receivers, double SampleRate, string Subject, string License, string Status, string Diagnostic,
    string SourcePositionType = "unknown", double? MinimumAzimuth = null, double? MaximumAzimuth = null,
    double? MinimumElevation = null, double? MaximumElevation = null)
{
    public override string ToString() => $"{Name} · {Status}";
    public static HrtfEntry BuiltIn => new("builtin", "Steam Audio built-in", "", "builtin-4.8.1", 0,
        "built-in", 0, 2, 48000, "Valve built-in HRTF", "Steam Audio distribution notices", "Compatible", "Built-in reference");
}
public sealed record ListeningRecord(DateTimeOffset At, string Source, long StartSample, long Frames, string AHash, string BHash,
    double LoudnessA, double LoudnessB, double GainA, double GainB, string Verdict, string Notes, SpatialRenderSnapshot Conditions,
    SpatialRenderSnapshot? ConditionsB = null);

public sealed class SofaLibrary
{
    readonly string path = AppDataPaths.Current.PreferencePath("profiles/hrtf-library.json");
    public List<HrtfEntry> Entries { get; } = [HrtfEntry.BuiltIn];
    public SofaLibrary()
    {
        try { if (File.Exists(path)) Entries.AddRange((JsonSerializer.Deserialize<List<HrtfEntry>>(File.ReadAllText(path), SpatialJson.Options) ?? []).Where(x => x.Id != "builtin")); }
        catch (Exception ex) when (ex is IOException or JsonException) { LoadError = ex.Message; }
    }
    public string? LoadError { get; }
    public void Save() => SpatialJson.Save(path, Entries.Where(e => e.Id != "builtin").ToArray());
    public void Remove(HrtfEntry entry) { if (entry.Id != "builtin") { Entries.RemoveAll(e => e.Id == entry.Id); Save(); } }
    public async Task<HrtfEntry> RegisterAsync(string file, CancellationToken token)
    {
        var entry = await ProbeAsync(file, token);
        token.ThrowIfCancellationRequested();
        int index = Entries.FindIndex(x => x.Hash == entry.Hash);
        if (index >= 0) Entries[index] = entry with { Name = Entries[index].Name };
        else Entries.Add(entry);
        Save(); return entry;
    }
    static async Task<HrtfEntry> ProbeAsync(string file, CancellationToken token)
    {
        // Native failures must not terminate the player. Run the same pinned Steam Audio validation in a child.
        Directory.CreateDirectory(SpatialRendering.CacheRoot);
        string report = System.IO.Path.Combine(SpatialRendering.CacheRoot, Guid.NewGuid().ToString("N") + ".sofa-probe.json");
        string assembly = typeof(SofaLibrary).Assembly.Location;
        string executable = System.IO.Path.ChangeExtension(assembly, ".exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--sofa-probe"); info.ArgumentList.Add(System.IO.Path.GetFullPath(file)); info.ArgumentList.Add(report);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(info) ?? throw new IOException("Cannot start isolated SOFA inspector.");
        using var cancellation = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(report)) throw new InvalidDataException("SOFA inspector failed; current playback HRTF is unchanged.");
            return JsonSerializer.Deserialize<HrtfEntry>(await File.ReadAllTextAsync(report, token), SpatialJson.Options) ?? throw new InvalidDataException("Invalid SOFA report.");
        }
        finally { if (!process.HasExited) process.Kill(true); if (File.Exists(report)) File.Delete(report); }
    }
    public static HrtfEntry Inspect(string file, CancellationToken token)
    {
        var bytes = SofaHrtf.Read(file); token.ThrowIfCancellationRequested();
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string convention = "unknown", subject = "unknown", license = "Not provided"; int directions = 0, receivers = 0; double rate = 0;
        var failures = new List<string>(); string positionType = "unknown"; double? minAz = null, maxAz = null, minEl = null, maxEl = null;
        try
        {
            using var h5 = H5File.OpenRead(file);
            string Attribute(string name) { try { return h5.Attribute(name).Read<string>(); } catch { return ""; } }
            double[] Numbers(string name)
            {
                var d = h5.Dataset(name);
                if (d.Space.Dimensions.Aggregate(1UL, (a, b) => checked(a * b)) > 1_000_000) throw new InvalidDataException("SOFA metadata dimensions exceed limit.");
                return d.Type.Size == 4 ? d.Read<float[]>().Select(v => (double)v).ToArray() : d.Read<double[]>();
            }
            convention = Attribute("SOFAConventions"); subject = Attribute("ListenerShortName");
            if (subject.Length == 0) subject = Attribute("DatabaseName");
            license = Attribute("License"); if (license.Length == 0) license = "Not provided — check source dataset terms";
            var shape = h5.Dataset("Data.IR").Space.Dimensions;
            if (shape.Length != 3 || shape[0] > 100_000 || shape[1] != 2 || shape[2] > 262_144 || shape.Aggregate(1UL, (a,b) => checked(a*b)) > 32_000_000)
                failures.Add("Data.IR must be bounded M×2×N (max. 32 million IR samples).");
            else { directions = (int)shape[0]; receivers = (int)shape[1]; }
            var rates = Numbers("Data.SamplingRate"); if (rates.Length == 1 && double.IsFinite(rates[0]) && rates[0] is >= 8000 and <= 384000) rate = rates[0]; else failures.Add("Data.SamplingRate must contain one finite rate.");
            if (convention != "SimpleFreeFieldHRIR") failures.Add($"SOFAConventions={convention}; SimpleFreeFieldHRIR required.");
            var delays = Numbers("Data.Delay"); if (delays.Any(v => v != 0)) failures.Add("Data.Delay must be zero; delays will not be removed.");
            var positions = h5.Dataset("SourcePosition");
            positionType = positions.Attribute("Type").Read<string>();
            if (positionType == "spherical")
            {
                string units = positions.Attribute("Units").Read<string>();
                var points = Numbers("SourcePosition");
                if (points.Length != directions * 3 || points.Any(x => !double.IsFinite(x))) failures.Add("Invalid source direction coordinates.");
                else if (units.StartsWith("degree", StringComparison.OrdinalIgnoreCase) && directions > 0)
                {
                    var az = Enumerable.Range(0,directions).Select(i => points[i*3]).ToArray(); var el = Enumerable.Range(0,directions).Select(i => points[i*3+1]).ToArray();
                    minAz = az.Min(); maxAz = az.Max(); minEl = el.Min(); maxEl = el.Max();
                }
            }
            token.ThrowIfCancellationRequested();
            // Native creation remains the final compatibility check; metadata success alone is insufficient.
            if (failures.Count == 0) SofaHrtf.Validate(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex.Message); }
        return new(hash, System.IO.Path.GetFileNameWithoutExtension(file), System.IO.Path.GetFullPath(file), hash, bytes.Length,
            convention, directions, receivers, rate, subject, license, failures.Count == 0 ? "Compatible" : "Unsupported", string.Join("\n", failures), positionType, minAz, maxAz, minEl, maxEl);
    }
    public static HeadphoneOptions Apply(HrtfEntry e, HeadphoneOptions current)
    {
        if (e.Status != "Compatible") throw new InvalidDataException(e.Diagnostic);
        if (e.Id != "builtin" && (!File.Exists(e.Path) || Convert.ToHexStringLower(SHA256.HashData(SofaHrtf.Read(e.Path))) != e.Hash))
            throw new InvalidDataException("SOFA is missing or changed. Revalidate this library entry.");
        return current with { Enabled = true, Method = HeadphoneMethod.SteamAudio, Hrtf = e.Id == "builtin" ? HeadphoneHrtf.BuiltIn : HeadphoneHrtf.Sofa,
            SofaPath = e.Path, UseTracker = false };
    }
}
