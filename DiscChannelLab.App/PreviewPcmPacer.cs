using System.Diagnostics;

namespace Disc2Flac;

/// <summary>Bounds mixed PCM queued to the player and refills after decoder/scheduler stalls.
/// The player's actual output clock remains authoritative for the UI and visualization.</summary>
public sealed class PreviewPcmPacer(Func<bool>? linked = null, Func<double>? now = null, Action<string>? log = null) : IDisposable
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    double? origin;
    double? lastWrite;
    double maximumWriteGap;
    public void Submitted()
    {
        double time = now?.Invoke() ?? clock.Elapsed.TotalSeconds;
        if (lastWrite is { } previous) maximumWriteGap = Math.Max(maximumWriteGap, time - previous);
        lastWrite = time;
    }
    public void Dispose()
    {
        if (lastWrite is not null)
            log?.Invoke($"PCM DELIVERY max-write-gap-ms={maximumWriteGap * 1000:0.0} pacing-refill-estimates={RefillEvents} " +
                $"reserve-ms={(linked?.Invoke() == true ? 120 : 60)} (delivery timing, not hardware underruns)");
    }
    public int RefillEvents { get; private set; }
    public double DelaySeconds(long submittedFrames, int blockFrames)
    {
        double time = now?.Invoke() ?? clock.Elapsed.TotalSeconds;
        origin ??= time; // Decoder/seek startup cannot earn playback credit.
        double elapsed = time - origin.Value, submitted = submittedFrames / 48000d;
        // Once the estimated queue has drained, restore a small reserve instead
        // of retaining unlimited wall-clock credit and bursting all later PCM.
        // This is a pacing estimate, not a count of hardware underruns.
        if (elapsed > submitted + .03)
        {
            origin = time - submitted; elapsed = submitted; RefillEvents++;
        }
        double reserve = linked?.Invoke() == true ? .12 : .06;
        return Math.Max(0, submitted + blockFrames / 48000d - reserve - elapsed);
    }
}
