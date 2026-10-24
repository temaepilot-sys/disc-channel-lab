using System.Numerics;

namespace Disc2Flac;

/// <summary>Zero added latency, three FFT partition sizes. Measured leading silence is not removed.
/// All plans, spectra and histories are allocated before audio delivery. No worker joins or file I/O in Process.</summary>
public sealed class BrirConvolution
{
    readonly Tier[] tiers;
    readonly float[] pendingL = new float[32768], pendingR = new float[32768];
    long position;
    public int TailSamples { get; }
    public BrirConvolution(float[] left, float[] right, int schedulingPhase = 0)
    {
        if (left.Length == 0 || right.Length == 0 || Math.Max(left.Length, right.Length) > 48000 * 6 + 384)
            throw new InvalidDataException("BRIR must contain 1 sample to 6 seconds at 48 kHz (no implicit truncation).");
        TailSamples = Math.Max(left.Length, right.Length) - 1;
        List<Tier> parts = [];
        void Add(int size, int offset, int end)
        {
            int count = Math.Min(TailSamples + 1, end) - offset;
            if (count > 0) parts.Add(new(size, offset, left, right, count, schedulingPhase));
        }
        Add(256, 0, 1024); Add(1024, 1024, 16384); Add(4096, 16384, int.MaxValue);
        tiers = parts.ToArray();
    }
    public void Process(float[] input, float[] left, float[] right)
    {
        if (input.Length != 256 || left.Length != 256 || right.Length != 256) throw new ArgumentException("BRIR block must be 256 frames.");
        foreach (var tier in tiers) tier.Push(input, position, pendingL, pendingR);
        for (int f = 0; f < 256; f++)
        {
            int p = (int)((position + f) & 32767);
            left[f] = pendingL[p]; right[f] = pendingR[p]; pendingL[p] = pendingR[p] = 0;
        }
        position += 256;
    }

    sealed class Tier
    {
        readonly int block, offset, fft, partitions;
        readonly FftPlan plan;
        readonly float[] input, xr, xi, yr, yi, overlapL, overlapR;
        readonly float[][] historyR, historyI, hlR, hlI, hrR, hrI;
        int fill, cursor;
        public Tier(int size, int start, float[] left, float[] right, int count, int phase)
        {
            block = size; offset = start; fft = size * 2; partitions = (count + size - 1) / size;
            plan = new(fft); input = new float[block]; xr = new float[fft]; xi = new float[fft]; yr = new float[fft]; yi = new float[fft];
            // Start large tiers on different channel phases. Their offset already provides enough
            // look-ahead. Zero prehistory changes scheduling only, not measured audio/delay.
            fill = Math.Abs(phase % (size / 256)) * 256;
            overlapL = new float[block]; overlapR = new float[block];
            float[][] Buffers() => Enumerable.Range(0, partitions).Select(_ => new float[fft]).ToArray();
            historyR = Buffers(); historyI = Buffers(); hlR = Buffers(); hlI = Buffers(); hrR = Buffers(); hrI = Buffers();
            for (int p = 0; p < partitions; p++)
            {
                for (int j = 0; j < block; j++)
                {
                    int k = offset + p * block + j;
                    if (j + p * block < count) { hlR[p][j] = k < left.Length ? left[k] : 0; hrR[p][j] = k < right.Length ? right[k] : 0; }
                }
                plan.Transform(hlR[p], hlI[p], false); plan.Transform(hrR[p], hrI[p], false);
            }
        }
        public void Push(float[] data, long now, float[] outputL, float[] outputR)
        {
            Array.Copy(data, 0, input, fill, 256); fill += 256;
            if (fill < block) return;
            fill = 0; Array.Clear(xr); Array.Clear(xi); Array.Copy(input, xr, block); plan.Transform(xr, xi, false);
            Array.Copy(xr, historyR[cursor], fft); Array.Copy(xi, historyI[cursor], fft);
            long target = now + 256 - block + offset;
            Ear(hlR, hlI, overlapL, outputL, target); Ear(hrR, hrI, overlapR, outputR, target);
            cursor = (cursor + 1) % partitions;
        }
        void Ear(float[][] irR, float[][] irI, float[] overlap, float[] output, long target)
        {
            Array.Clear(yr); Array.Clear(yi);
            for (int p = 0; p < partitions; p++)
            {
                int h = cursor - p; if (h < 0) h += partitions;
                var a = historyR[h]; var b = historyI[h]; var c = irR[p]; var d = irI[p];
                int j = 0, width = Vector<float>.Count;
                for (; j <= fft - width; j += width)
                {
                    var ar = new Vector<float>(a, j); var ai = new Vector<float>(b, j);
                    var br = new Vector<float>(c, j); var bi = new Vector<float>(d, j);
                    (new Vector<float>(yr, j) + ar * br - ai * bi).CopyTo(yr, j);
                    (new Vector<float>(yi, j) + ar * bi + ai * br).CopyTo(yi, j);
                }
                for (; j < fft; j++) { yr[j] += a[j] * c[j] - b[j] * d[j]; yi[j] += a[j] * d[j] + b[j] * c[j]; }
            }
            plan.Transform(yr, yi, true);
            for (int j = 0; j < block; j++)
            {
                output[(int)((target + j) & 32767)] += yr[j] + overlap[j];
                overlap[j] = yr[j + block];
            }
        }
    }

    sealed class FftPlan
    {
        readonly int[] reverse;
        readonly float[] cos, sin;
        public FftPlan(int size)
        {
            reverse = new int[size]; cos = new float[size / 2]; sin = new float[size / 2];
            int bits = BitOperations.Log2((uint)size);
            for (int j = 0; j < size; j++) { int value = j, r = 0; for (int k = 0; k < bits; k++) { r = (r << 1) | (value & 1); value >>= 1; } reverse[j] = r; }
            for (int j = 0; j < cos.Length; j++) { cos[j] = (float)Math.Cos(2 * Math.PI * j / size); sin[j] = (float)Math.Sin(2 * Math.PI * j / size); }
        }
        public void Transform(float[] real, float[] imaginary, bool inverse)
        {
            int size = real.Length;
            for (int j = 0; j < size; j++) if (reverse[j] > j)
            { int k = reverse[j]; (real[j], real[k]) = (real[k], real[j]); (imaginary[j], imaginary[k]) = (imaginary[k], imaginary[j]); }
            for (int length = 2; length <= size; length *= 2)
            {
                int half = length / 2, stride = size / length;
                for (int start = 0; start < size; start += length) for (int j = 0; j < half; j++)
                {
                    int a = start + j, b = a + half, k = j * stride;
                    float c = cos[k], s = inverse ? sin[k] : -sin[k];
                    float tr = real[b] * c - imaginary[b] * s, ti = real[b] * s + imaginary[b] * c;
                    real[b] = real[a] - tr; imaginary[b] = imaginary[a] - ti; real[a] += tr; imaginary[a] += ti;
                }
            }
            if (inverse) { float scale = 1f / size; for (int j = 0; j < size; j++) { real[j] *= scale; imaginary[j] *= scale; } }
        }
    }
}

public sealed class BrirSpatializer : IHeadphoneSpatializer
{
    readonly BrirConvolution[] engines;
    public int TailSamples { get; }
    public BrirSpatializer(string[] channels, HeadphoneOptions options, SpatialScene? scene)
    {
        var profile = BrirLibrary.GetPrepared(options);
        profile.RequireChannels(channels, scene);
        engines = channels.Select((c,i) => { var ir = profile.Responses.TryGetValue(c, out var measured) ? measured : new BrirResponse([.70710678f], [.70710678f]); return new BrirConvolution(ir.Left, ir.Right, i); }).ToArray();
        TailSamples = engines.Max(x => x.TailSamples);
    }
    public void Process(int channel, float[] input, float[] left, float[] right, Vector3 direction, HeadphoneOptions options) => engines[channel].Process(input, left, right);
    public void Dispose() { }
}
