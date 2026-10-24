using System.Numerics;

namespace Disc2Flac;

public readonly record struct EarlyReflectionPath(Vector3 Image, Vector3 Direction, double DelaySamples, double RelativeGain);

/// <summary>Six first-order image sources. Delay is EXCESS path length: the unchanged dry path has no travel delay.</summary>
public sealed class EarlyReflections
{
    const int History = 32768;
    readonly float[][] history, delayed;
    readonly double[] low, high;
    readonly int[] positions;
    readonly SpatialRoomOptions room;
    readonly float[] left = new float[256], right = new float[256];
    public int TailFrames { get; }
    public EarlyReflections(int channels, SpatialRoomOptions settings)
    {
        settings.Validate(); room = settings;
        history = Enumerable.Range(0, channels).Select(_ => new float[History]).ToArray();
        delayed = Enumerable.Range(0, channels * 6).Select(_ => new float[256]).ToArray();
        positions = new int[channels]; low = new double[channels * 6]; high = new double[channels * 6];
        TailFrames = (int)Math.Ceiling(2 * Math.Sqrt(room.Width * room.Width + room.Depth * room.Depth + room.Height * room.Height) / 343 * 48000) + 4800;
    }
    public static EarlyReflectionPath Path(Vector3 source, SpatialRoomOptions room, int wall)
    {
        if (Math.Abs(source.X) >= room.Width / 2 || Math.Abs(source.Z) >= room.Depth / 2 ||
            source.Y <= -room.ListenerHeight || source.Y >= room.Height - room.ListenerHeight)
            throw new InvalidDataException("Speaker/source lies outside the room. Increase dimensions or reduce speaker radius.");
        Vector3 image = wall switch
        {
            0 => source with { X = (float)(-room.Width - source.X) },
            1 => source with { X = (float)(room.Width - source.X) },
            2 => source with { Z = (float)(-room.Depth - source.Z) },
            3 => source with { Z = (float)(room.Depth - source.Z) },
            4 => source with { Y = (float)(-2 * room.ListenerHeight - source.Y) },
            5 => source with { Y = (float)(2 * (room.Height - room.ListenerHeight) - source.Y) },
            _ => throw new ArgumentOutOfRangeException(nameof(wall))
        };
        double direct = Math.Max(.01, source.Length()), reflected = image.Length();
        return new(image, Vector3.Normalize(image), Math.Max(0, (reflected - direct) / 343 * 48000),
            Math.Pow(10, room.ReflectionDb / 20) * direct / reflected);
    }
    public void Add(int channel, float[] source, float[] outputLeft, float[] outputRight, Vector3 sourcePosition,
        Quaternion pose, HeadphoneOptions options, IHeadphoneSpatializer engine, int effectOffset)
    {
        var ring = history[channel]; int start = positions[channel];
        for (int f = 0; f < 256; f++) ring[(start + f) % History] = source[f];
        double mid = Math.Sqrt(1 - room.MidAbsorption), lo = Math.Sqrt(1 - room.LowAbsorption), hi = Math.Sqrt(1 - room.HighAbsorption);
        double aLow = 1 - Math.Exp(-2 * Math.PI * 250 / 48000), aHigh = 1 - Math.Exp(-2 * Math.PI * 4000 / 48000);
        for (int wall = 0; wall < 6; wall++)
        {
            int k = channel * 6 + wall; var path = Path(sourcePosition, room, wall);
            double delay = Math.Clamp(path.DelaySamples, 0, History - 257);
            int whole = (int)delay; double fraction = delay - whole;
            for (int f = 0; f < 256; f++)
            {
                int first = (start + f - whole + History) % History, second = (first - 1 + History) % History;
                double v = ring[first] * (1 - fraction) + ring[second] * fraction;
                low[k] += aLow * (v - low[k]); high[k] += aHigh * (v - high[k]);
                delayed[k][f] = (float)((mid * v + (lo - mid) * low[k] + (hi - mid) * (v - high[k])) * path.RelativeGain);
            }
            engine.Process(effectOffset + k, delayed[k], left, right,
                Vector3.Transform(path.Direction, Quaternion.Conjugate(pose)), options);
            for (int f = 0; f < 256; f++) { outputLeft[f] += left[f]; outputRight[f] += right[f]; }
        }
        positions[channel] = (start + 256) % History;
    }
    public void Flush(int channel, float[] outputLeft, float[] outputRight, IHeadphoneSpatializer engine, int offset)
    {
        for (int wall = 0; wall < 6; wall++)
        {
            engine.Flush(offset + channel * 6 + wall, left, right);
            for (int f = 0; f < 256; f++) { outputLeft[f] += left[f]; outputRight[f] += right[f]; }
        }
    }
}
