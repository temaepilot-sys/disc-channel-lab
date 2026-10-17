using System.Diagnostics;

namespace Disc2Flac;

/// <summary>Fixed-size counters only on the audio thread; formatting and I/O happen after output closes.</summary>
internal sealed class PlaybackDeliveryStats
{
    readonly long[] maximum = new long[5], slow = new long[5];
    long lastWrite, lastPause;
    readonly int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    internal enum Stage { Decode, DeviceSpace, Render, Write, Observe }
    public void Record(Stage stage, long start, long end)
    {
        long elapsed = end - start;
        maximum[(int)stage] = Math.Max(maximum[(int)stage], elapsed);
        if (elapsed > Stopwatch.Frequency / 100) slow[(int)stage]++;
    }
    public double MaximumWriteGapMs { get; private set; }
    public void Written(long now, long pauseTicks)
    {
        if (lastWrite != 0) MaximumWriteGapMs = Math.Max(MaximumWriteGapMs,
            Math.Max(0, now - lastWrite - (pauseTicks - lastPause)) * 1000d / Stopwatch.Frequency);
        lastWrite = now; lastPause = pauseTicks;
    }
    public override string ToString() => $"PLAYBACK DELIVERY max-write-gap-ms={MaximumWriteGapMs:0.0} " +
        string.Join(' ', Enum.GetValues<Stage>().Select(s => $"{s.ToString().ToLowerInvariant()}-max-ms={maximum[(int)s] * 1000d / Stopwatch.Frequency:0.0} {s.ToString().ToLowerInvariant()}-over10ms={slow[(int)s]}")) +
        $" gc={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}";
}
