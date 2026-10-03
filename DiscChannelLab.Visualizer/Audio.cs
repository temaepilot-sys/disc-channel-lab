using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DiscChannelLab.Visualizer;

public sealed record VisualizationSettings
{
    public int SampleRate { get; init; } = 48000;
    public int FftSize { get; init; } = 2048;
    public int HopSize { get; init; } = 512;
    public int BandCount { get; init; } = 16;
    public float MinimumDb { get; init; } = -70;
    public float MaximumDb { get; init; } = 0;
    public double AttackSeconds { get; init; } = .045;
    public double ReleaseSeconds { get; init; } = .3;
    public int PreviewSeconds { get; init; } = 120;

    [System.Text.Json.Serialization.JsonIgnore]
    public double[] Edges => Enumerable.Range(0, BandCount + 1).Select(i => 20 * Math.Pow(1000, (double)i / BandCount)).ToArray();
    [System.Text.Json.Serialization.JsonIgnore]
    public double[] Centers => Enumerable.Range(0, BandCount).Select(i => 20 * Math.Pow(1000, (i + .5) / BandCount)).ToArray();
    public void Validate()
    {
        if (SampleRate != 48000 || FftSize < 256 || FftSize > 8192 || (FftSize & (FftSize - 1)) != 0 ||
            HopSize < 128 || HopSize > FftSize || BandCount < 4 || BandCount > 32 ||
            !float.IsFinite(MinimumDb) || !float.IsFinite(MaximumDb) || MinimumDb >= MaximumDb ||
            !double.IsFinite(AttackSeconds) || !double.IsFinite(ReleaseSeconds) ||
            AttackSeconds <= 0 || ReleaseSeconds <= 0 || PreviewSeconds < 5 || PreviewSeconds > 120)
            throw new InvalidDataException("Visualization settings are out of range.");
    }
}

public static class ChannelMapper
{
    private static readonly Dictionary<string, string[]> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mono"] = ["FC"], ["stereo"] = ["FL", "FR"],
        ["2.1"] = ["FL", "FR", "LFE"], ["3.0"] = ["FL", "FR", "FC"],
        ["3.0(back)"] = ["FL", "FR", "BC"], ["3.1"] = ["FL", "FR", "FC", "LFE"],
        ["4.0"] = ["FL", "FR", "FC", "BC"], ["4.1"] = ["FL", "FR", "FC", "LFE", "BC"],
        ["quad"] = ["FL", "FR", "BL", "BR"], ["quad(side)"] = ["FL", "FR", "SL", "SR"],
        ["5.0"] = ["FL", "FR", "FC", "BL", "BR"], ["5.0(side)"] = ["FL", "FR", "FC", "SL", "SR"],
        ["5.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR"],
        ["5.1(back)"] = ["FL", "FR", "FC", "LFE", "BL", "BR"],
        ["5.1(side)"] = ["FL", "FR", "FC", "LFE", "SL", "SR"],
        ["6.0"] = ["FL", "FR", "FC", "BC", "SL", "SR"],
        ["6.0(front)"] = ["FL", "FR", "FLC", "FRC", "SL", "SR"],
        ["hexagonal"] = ["FL", "FR", "FC", "BL", "BR", "BC"],
        ["6.1"] = ["FL", "FR", "FC", "LFE", "BC", "SL", "SR"],
        ["6.1(back)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "BC"],
        ["6.1(front)"] = ["FL", "FR", "LFE", "FLC", "FRC", "SL", "SR"],
        ["7.0"] = ["FL", "FR", "FC", "BL", "BR", "SL", "SR"],
        ["7.0(front)"] = ["FL", "FR", "FC", "FLC", "FRC", "SL", "SR"],
        ["7.1"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
        ["7.1(wide)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "FLC", "FRC"],
        ["7.1(wide-side)"] = ["FL", "FR", "FC", "LFE", "FLC", "FRC", "SL", "SR"],
        ["octagonal"] = ["FL", "FR", "FC", "BL", "BR", "BC", "SL", "SR"],
        ["3.1.2"] = ["FL", "FR", "FC", "LFE", "TFL", "TFR"],
        ["5.1.2"] = ["FL", "FR", "FC", "LFE", "SL", "SR", "TFL", "TFR"],
        ["5.1.2(back)"] = ["FL", "FR", "FC", "LFE", "BL", "BR", "TFL", "TFR"]
    };

    public static string[] Map(string? layout, int channels)
    {
        if (layout != null && Layouts.TryGetValue(layout, out var codes) && codes.Length == channels) return codes;
        if (string.IsNullOrWhiteSpace(layout) || layout.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (channels == 1) return ["FC"];
            if (channels == 2) return ["FL", "FR"];
        }
        throw new InvalidDataException($"Unknown channel layout ({channels}ch / {layout ?? "Unknown"}). Loading stopped because channel order cannot be verified.");
    }

    // Conservative fixed stereo preview gain. These are NOT the main application's mixer settings.
    public static (float Left, float Right)[] StereoWeights(string[] codes)
    {
        var weights = codes.Select(code => code switch
        {
            "FL" => (1f, 0f), "FR" => (0f, 1f), "FC" => (.7071068f, .7071068f),
            "LFE" => (.25f, .25f), "BC" => (.5f, .5f),
            "SL" or "BL" or "FLC" or "TFL" => (.7071068f, 0f),
            "SR" or "BR" or "FRC" or "TFR" => (0f, .7071068f),
            _ => throw new InvalidDataException("Unsupported channel.")
        }).ToArray();
        float divisor = Math.Max(1, Math.Max(weights.Sum(w => w.Item1), weights.Sum(w => w.Item2)));
        return weights.Select(w => (w.Item1 / divisor, w.Item2 / divisor)).ToArray();
    }
}

public static class DispersionAngleMapper
{
    // HALF angle of the illustrative radiation cone, in degrees, not measured speaker directivity.
    public static readonly double[][] Knots = [[20, 180], [50, 150], [100, 120], [250, 90], [500, 70],
        [1000, 50], [2000, 35], [4000, 25], [8000, 15], [16000, 8], [20000, 5]];
    public static double Map(double hz)
    {
        for (int i = 1; i < Knots.Length; i++)
            if (hz <= Knots[i][0])
            {
                double t = Math.Clamp(Math.Log(Math.Max(20, hz) / Knots[i - 1][0]) / Math.Log(Knots[i][0] / Knots[i - 1][0]), 0, 1);
                return Knots[i - 1][1] + t * (Knots[i][1] - Knots[i - 1][1]);
            }
        return Knots[^1][1];
    }
}

public sealed class SpectrumAnalyzer
{
    readonly VisualizationSettings settings;
    readonly float[][] ring;
    readonly double[][] smoothed;
    readonly Complex[] transform;
    readonly double[] window;
    readonly (int Bin, double Weight)[][] bandWeights;
    readonly double powerScale;
    readonly Action<double, float[]>? onFrame;
    readonly Action<double, float[], float[]>? onDetailedFrame;
    readonly bool retainFrames;
    int position;
    long samples;
    public List<float[]> Frames { get; } = [];
    public List<float[]> FastFrames { get; } = [];
    public int ChannelCount => ring.Length;

    public SpectrumAnalyzer(int channels, VisualizationSettings settings, Action<double, float[]>? onFrame = null, bool retainFrames = true,
        Action<double, float[], float[]>? onDetailedFrame = null)
    {
        settings.Validate();
        this.settings = settings;
        this.onFrame = onFrame;
        this.onDetailedFrame = onDetailedFrame;
        this.retainFrames = retainFrames;
        ring = Enumerable.Range(0, channels).Select(_ => new float[settings.FftSize]).ToArray();
        smoothed = Enumerable.Range(0, channels).Select(_ => new double[settings.BandCount]).ToArray();
        transform = new Complex[settings.FftSize];
        window = Enumerable.Range(0, settings.FftSize).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / settings.FftSize)).ToArray();
        powerScale = 1 / (settings.FftSize * window.Sum(x => x * x));
        bandWeights = new (int Bin, double Weight)[settings.BandCount][];
        var edges = settings.Edges;
        double width = (double)settings.SampleRate / settings.FftSize;
        for (int b = 0; b < settings.BandCount; b++)
        {
            var contributions = new List<(int Bin, double Weight)>();
            for (int k = 0; k <= settings.FftSize / 2; k++)
            {
                double lo = Math.Max(0, (k - .5) * width), hi = Math.Min(settings.SampleRate / 2, (k + .5) * width);
                double weight = Math.Max(0, Math.Min(hi, edges[b + 1]) - Math.Max(lo, edges[b])) / (hi - lo);
                if (weight > 0) contributions.Add((k, weight * powerScale * (k == 0 || k == settings.FftSize / 2 ? 1 : 2)));
            }
            bandWeights[b] = contributions.ToArray();
        }
    }

    public void Push(ReadOnlySpan<float> frame)
    {
        for (int c = 0; c < ring.Length; c++) ring[c][position] = float.IsFinite(frame[c]) ? frame[c] : 0;
        position = (position + 1) % settings.FftSize;
        samples++;
        if (samples >= settings.FftSize && (samples - settings.FftSize) % settings.HopSize == 0) Analyze();
    }

    void Analyze()
    {
        var values = new float[ring.Length * settings.BandCount];
        // Reuse each FFT for an unsmoothed brightness frame. Forecast-only analysis
        // does not allocate or retain this additional array.
        var fastValues = retainFrames || onDetailedFrame is not null ? new float[values.Length] : null;
        for (int c = 0; c < ring.Length; c++)
        {
            for (int i = 0; i < transform.Length; i++) transform[i] = ring[c][(position + i) % transform.Length] * window[i];
            Fft(transform);
            for (int b = 0; b < settings.BandCount; b++)
            {
                double energy = 0;
                foreach (var (k, weight) in bandWeights[b])
                    energy += (transform[k].Real * transform[k].Real + transform[k].Imaginary * transform[k].Imaginary) * weight;
                smoothed[c][b] = Smooth(smoothed[c][b], energy, (double)settings.HopSize / settings.SampleRate,
                    settings.AttackSeconds, settings.ReleaseSeconds);
                // Mean-square dBFS: a full-scale sine has -3.01 dBFS total RMS energy.
                double db = 10 * Math.Log10(Math.Max(1e-20, smoothed[c][b]));
                values[c * settings.BandCount + b] = (float)Math.Clamp((db - settings.MinimumDb) / (settings.MaximumDb - settings.MinimumDb), 0, 1);
                if (fastValues is not null)
                {
                    double fastDb = 10 * Math.Log10(Math.Max(1e-20, energy));
                    fastValues[c * settings.BandCount + b] = (float)Math.Clamp((fastDb - settings.MinimumDb) / (settings.MaximumDb - settings.MinimumDb), 0, 1);
                }
            }
        }
        if (retainFrames) { Frames.Add(values); FastFrames.Add(fastValues!); }
        double timestamp = (samples - settings.FftSize / 2d) / settings.SampleRate;
        onFrame?.Invoke(timestamp, values);
        onDetailedFrame?.Invoke(timestamp, values, fastValues!);
    }

    public static double Smooth(double previous, double target, double dt, double attack, double release)
        => target + (previous - target) * Math.Exp(-dt / (target > previous ? attack : release));

    static void Fft(Complex[] data)
    {
        for (int i = 1, j = 0; i < data.Length; i++)
        {
            int bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int len = 2; len <= data.Length; len <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int start = 0; start < data.Length; start += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++, w *= step)
                {
                    Complex a = data[start + j], b = data[start + j + len / 2] * w;
                    data[start + j] = a + b;
                    data[start + j + len / 2] = a - b;
                }
            }
        }
    }
}

public sealed record ClipInfo(string Name, string Codec, int SourceSampleRate, string BitDepth, string Layout,
    string[] Channels, double SourceDuration, bool Demo);

public sealed class DemoAudioSource(VisualizationSettings settings)
{
    public byte[] Demo()
    {
        string[] codes = ["FL", "FR", "FC", "LFE", "SL", "SR"];
        var builder = new ClipBuilder(new("Sound around you — 5.1 synthetic demo", "Synthetic PCM", settings.SampleRate, "32 float", "5.1(side)", codes, 40, true), settings);
        float[] frame = new float[6];
        double[] notes = [130.8128, 164.8138, 195.9977, 261.6256, 329.6276];
        var random = new Random(42);
        for (int i = 0; i < 40 * settings.SampleRate; i++)
        {
            double t = (double)i / settings.SampleRate;
            double fade = Math.Min(1, Math.Min(t / .3, (40 - t) / .6));
            for (int c = 0; c < 6; c++)
            {
                if (c == 3)
                {
                    double pulse = .5 + .5 * Math.Sin(2 * Math.PI * .55 * t);
                    frame[c] = (float)(.5 * fade * pulse * Math.Sin(2 * Math.PI * 48 * t));
                    continue;
                }
                double n = notes[c == 4 ? 3 : c == 5 ? 4 : c];
                double envelope = .1 + .9 * Math.Pow(.5 + .5 * Math.Sin(t * .8 - c * 1.1), 3);
                double pingTime = (t + c * .17) % 1.25;
                double ping = Math.Exp(-pingTime * 8) * (Math.Sin(2 * Math.PI * n * 8 * t) + .25 * Math.Sin(2 * Math.PI * n * 19 * t));
                double shimmer = (random.NextDouble() * 2 - 1) * Math.Exp(-pingTime * 24) * .14;
                frame[c] = (float)(fade * envelope * (.25 * Math.Sin(2 * Math.PI * n * t) + .1 * Math.Sin(2 * Math.PI * 2 * n * t) + .15 * ping + shimmer));
            }
            builder.Add(frame);
        }
        return builder.Finish();
    }

}

public sealed class ClipBuilder(ClipInfo info, VisualizationSettings settings)
{
    readonly SpectrumAnalyzer analyzer = new(info.Channels.Length, settings);
    readonly (float Left, float Right)[] weights = ChannelMapper.StereoWeights(info.Channels);
    readonly MemoryStream audio = new();
    int frames;
    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        for (int i = 0; i < floats.Length; i += info.Channels.Length) Add(floats.Slice(i, info.Channels.Length));
    }
    public void Add(ReadOnlySpan<float> frame)
    {
        if (frames >= settings.PreviewSeconds * settings.SampleRate) return;
        analyzer.Push(frame);
        float left = 0, right = 0;
        for (int c = 0; c < frame.Length; c++)
        {
            float sample = float.IsFinite(frame[c]) ? Math.Clamp(frame[c], -1, 1) : 0;
            left += sample * weights[c].Left; right += sample * weights[c].Right;
        }
        Span<float> stereo = stackalloc float[2] { left, right };
        audio.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(stereo));
        frames++;
    }
    public byte[] Finish()
    {
        if (frames == 0) throw new InvalidDataException("No audio samples found.");
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = info.Name, codec = info.Codec, sourceSampleRate = info.SourceSampleRate, bitDepth = info.BitDepth,
            layout = info.Layout, channels = info.Channels, sourceDuration = info.SourceDuration, demo = info.Demo,
            sampleRate = settings.SampleRate, sampleFrames = frames, duration = (double)frames / settings.SampleRate,
            bandCount = settings.BandCount, centers = settings.Centers, analysisFrames = analyzer.Frames.Count,
            fastAnalysis = true,
            firstAnalysisTime = (double)settings.FftSize / 2 / settings.SampleRate,
            analysisStep = (double)settings.HopSize / settings.SampleRate,
            dispersion = settings.Centers.Select(DispersionAngleMapper.Map).ToArray(),
            fftSize = settings.FftSize, minimumDb = settings.MinimumDb, maximumDb = settings.MaximumDb
        });
        using var result = new MemoryStream();
        using var writer = new BinaryWriter(result, Encoding.UTF8, true);
        writer.Write(header.Length); writer.Write(header);
        while (result.Position % 4 != 0) writer.Write((byte)0);
        audio.Position = 0; audio.CopyTo(result);
        foreach (var frame in analyzer.Frames) result.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.AsSpan()));
        foreach (var frame in analyzer.FastFrames) result.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.AsSpan()));
        audio.Dispose();
        return result.ToArray();
    }
}
