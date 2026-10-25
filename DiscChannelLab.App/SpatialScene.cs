using System.Numerics;
using System.Text.Json;

namespace Disc2Flac;

public enum SpatialSourceKind { ChannelBed, Object }
public sealed record SpatialKeyframe(long StartSample, long DurationSamples, Vector3 Position, float Gain = 1,
    bool Jump = true, long InterpolationSamples = 0, bool Cartesian = true);
public sealed record SpatialSource(string Id, string Name, int TrackIndex, SpatialSourceKind Kind,
    long StartSample, long EndSample, SpatialKeyframe[] Blocks, bool IsLfe = false)
{
    public int BlockIndex(long sample)
    {
        int lo = 0, hi = Blocks.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Blocks[mid].StartSample <= sample) lo = mid; else hi = mid - 1; }
        return lo;
    }
    public (Vector3 Position, float Gain) At(long sample)
    {
        int i = BlockIndex(sample); var b = Blocks[i];
        var previous = i == 0 ? b : Blocks[i - 1];
        long length = b.Jump ? b.InterpolationSamples : b.DurationSamples;
        double t = length <= 0 ? 1 : Math.Clamp((sample - b.StartSample) / (double)length, 0, 1);
        Vector3 p;
        if (b.Cartesian) p = Vector3.Lerp(ToWorld(previous), ToWorld(b), (float)t);
        else
        {
            var a = previous.Cartesian ? WorldToPolar(previous.Position) : previous.Position;
            var target = b.Position;
            float angle = (float)Math.IEEERemainder(target.X - a.X, 360);
            p = PolarToWorld(new(a.X + angle * (float)t, float.Lerp(a.Y, target.Y, (float)t), float.Lerp(a.Z, target.Z, (float)t)));
        }
        bool active = sample >= StartSample && sample < EndSample && sample >= Blocks[0].StartSample && sample < b.StartSample + b.DurationSamples;
        return (p, active ? float.Lerp(previous.Gain, b.Gain, (float)t) : 0);
    }
    public float GainAt(long sample)
    {
        if (sample < StartSample || sample >= EndSample || sample < Blocks[0].StartSample) return 0;
        int i = BlockIndex(sample); var b = Blocks[i];
        if (sample >= b.StartSample + b.DurationSamples) return 0;
        long length = b.Jump ? b.InterpolationSamples : b.DurationSamples;
        float t = length <= 0 ? 1 : (float)Math.Clamp((sample - b.StartSample) / (double)length, 0, 1);
        return float.Lerp(i == 0 ? b.Gain : Blocks[i - 1].Gain, b.Gain, t);
    }
    // Internal world axes: X right, Y up, Z front. ADM polar azimuth is positive LEFT.
    public static Vector3 PolarToWorld(Vector3 p)
    {
        double a = -p.X * Math.PI / 180, e = p.Y * Math.PI / 180;
        return new((float)(p.Z * Math.Sin(a) * Math.Cos(e)), (float)(p.Z * Math.Sin(e)), (float)(p.Z * Math.Cos(a) * Math.Cos(e)));
    }
    public static Vector3 WorldToPolar(Vector3 p) => new((float)(-Math.Atan2(p.X, p.Z) * 180 / Math.PI),
        (float)(Math.Atan2(p.Y, Math.Sqrt(p.X * p.X + p.Z * p.Z)) * 180 / Math.PI), p.Length());
    public static Vector3 ToWorld(SpatialKeyframe b) => b.Cartesian ? b.Position : PolarToWorld(b.Position);
}
public sealed record SpatialScene(int SampleRate, long DurationSamples, SpatialSource[] Sources, string Profile, string[] Warnings)
{
    public static SpatialScene ChannelBed(string[] channels, long frames, double frontAngle = 30) => new(48000, frames,
        channels.Select((c, i) => new SpatialSource(c, c, i, SpatialSourceKind.ChannelBed, 0, frames,
            [new(0, frames, HeadphoneGeometry.Speaker(c, channels, frontAngle))], c == "LFE")).ToArray(), "channel-bed", []);
}

public sealed record SpatialRoomOptions
{
    public string Mode { get; init; } = "Pure HRTF";
    public double Width { get; init; } = 4.5;
    public double Depth { get; init; } = 3.5;
    public double Height { get; init; } = 2.6;
    public double ListenerHeight { get; init; } = 1.2;
    public double SpeakerRadius { get; init; } = 1.2;
    public double ReflectionDb { get; init; } = -18;
    public double LowAbsorption { get; init; } = .15;
    public double MidAbsorption { get; init; } = .3;
    public double HighAbsorption { get; init; } = .55;
    public bool SoloReflections { get; init; }
    public bool Enabled => Mode != "Pure HRTF";
    public static SpatialRoomOptions Preset(string mode) => mode switch
    {
        "Pure HRTF" => new(),
        "Studio" => new() { Mode = mode },
        "Listening Room" => new() { Mode = mode, Width = 6, Depth = 5, Height = 3.2, ReflectionDb = -12, MidAbsorption = .35, HighAbsorption = .6 },
        // Deliberately audible first-order paths for comparison; not a measured hall or a late-reverberation model.
        "Big Room" => new() { Mode = mode, Width = 10, Depth = 14, Height = 6, ListenerHeight = 1.5,
            ReflectionDb = 0, LowAbsorption = .08, MidAbsorption = .15, HighAbsorption = .3 },
        _ => throw new InvalidDataException("Unknown room preset.")
    };
    public void Validate()
    {
        if (Mode is not ("Pure HRTF" or "Studio" or "Listening Room" or "Big Room") ||
            !double.IsFinite(Width + Depth + Height + ListenerHeight + SpeakerRadius + ReflectionDb + LowAbsorption + MidAbsorption + HighAbsorption) ||
            Width is < 2 or > 30 || Depth is < 2 or > 30 || Height is < 2 or > 15 ||
            ListenerHeight <= .2 || ListenerHeight >= Height - .2 || SpeakerRadius is < .2 or > 10 ||
            ReflectionDb is < -60 or > 0 || LowAbsorption is < 0 or > 1 || MidAbsorption is < 0 or > 1 || HighAbsorption is < 0 or > 1)
            throw new InvalidDataException("Invalid room dimensions, absorption or reflection gain.");
    }
}

public sealed record SpatialRenderSnapshot(HeadphoneOptions Options, HeadPose Pose, PreviewMixState Mix, double Master,
    string HrtfHash, string EngineVersion = "SpatialLab-2 / SteamAudio-4.8.1", int SampleRate = 48000, int BlockSize = 256)
{
    public PreviewMixState? OutputMix { get; init; }
    public BrirRenderMetadata? Brir { get; init; }
}
public sealed record BrirRenderMetadata(string Name, string Hash, BrirProfile Profile, BrirAsset[] Assets,
    int ResamplingLatencyFrames = 0, string Orientation = "Fixed measurement head orientation; no tracking",
    string Resampler = "48 kHz / windowed sinc, rate-scaled IR gain, measured delays preserved; non-48 kHz profiles add 4 ms uniformly");

public static class SpatialJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
