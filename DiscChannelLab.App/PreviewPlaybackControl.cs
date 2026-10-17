using System.Diagnostics;

namespace Disc2Flac;

/// <summary>Transport commands only; all device calls stay on the audio thread.</summary>
public sealed class PreviewPlaybackControl : IDisposable
{
    readonly AutoResetEvent wake = new(false);
    int attached, requestedPause, paused, started;
    long playedFrames, sampledAt;
    public bool CanPause => Volatile.Read(ref attached) == 1;
    public bool IsPaused => Volatile.Read(ref requestedPause) == 1;
    public bool DevicePaused => Volatile.Read(ref paused) == 1;
    public bool HasStarted => Volatile.Read(ref started) == 1;
    public double Position => Interlocked.Read(ref playedFrames) / 48000d;
    public double EstimatedPosition => Position + (DevicePaused || IsPaused || !CanPause ? 0 :
        Math.Clamp(Stopwatch.GetElapsedTime(Interlocked.Read(ref sampledAt)).TotalSeconds, 0, .05));
    public bool Pause()
    {
        if (!CanPause) return false;
        Volatile.Write(ref requestedPause, 1); Signal(); return true;
    }
    void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }
    public void Resume() { Volatile.Write(ref requestedPause, 0); Signal(); }
    internal void Attach(IHeadphoneOutput output) { Sample(output); Volatile.Write(ref attached, output.SupportsPause ? 1 : 0); }
    internal void Sample(IHeadphoneOutput output)
    {
        Interlocked.Exchange(ref playedFrames, output.PlayedFrames);
        Interlocked.Exchange(ref sampledAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref started, output.HasStarted ? 1 : 0);
    }
    internal void Apply(IHeadphoneOutput output, CancellationToken token)
    {
        if (!IsPaused) return;
        output.Pause(); Sample(output); Volatile.Write(ref paused, 1);
        try
        {
            while (IsPaused) { token.ThrowIfCancellationRequested(); wake.WaitOne(10); }
            token.ThrowIfCancellationRequested();
            output.Resume(); Sample(output);
        }
        finally { Volatile.Write(ref paused, 0); }
    }
    internal void Detach() { Volatile.Write(ref attached, 0); Signal(); }
    public void Dispose() { Volatile.Write(ref attached, 0); wake.Dispose(); }
}
