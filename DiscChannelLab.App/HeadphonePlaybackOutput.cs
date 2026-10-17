namespace Disc2Flac;

/// <summary>De-clicks transport boundaries, after spatialization and before the device buffer.</summary>
public sealed class HeadphonePlaybackOutput(IHeadphoneOutput device) : IHeadphoneOutput
{
    public const int FadeFrames = HeadphoneRenderer.SampleRate * 8 / 1000;
    private int _attackFrame;
    private float _lastLeft, _lastRight;
    private bool _drained, _disposed;
    public long SubmittedFrames => device.SubmittedFrames;
    public long PlayedFrames => device.PlayedFrames;
    public double BufferMilliseconds => device.BufferMilliseconds;
    public bool HasStarted => device.HasStarted;
    public long EmptyBufferCount => device.EmptyBufferCount;

    public void WaitForSpace(int frames, CancellationToken token) => device.WaitForSpace(frames, token);

    public void Write(float[] interleaved, int frames)
    {
        // A seek/resume may begin at any nonzero sample. Use a short raised-cosine attack;
        // steady playback, head tracking, mixer gains and exported files are unaffected.
        for (int frame = 0; frame < frames && _attackFrame < FadeFrames; frame++, _attackFrame++)
        {
            var gain = (float)(.5 - .5 * Math.Cos(Math.PI * _attackFrame / (FadeFrames - 1)));
            interleaved[frame * 2] *= gain;
            interleaved[frame * 2 + 1] *= gain;
        }
        device.Write(interleaved, frames);
        if (frames > 0) { _lastLeft = interleaved[(frames - 1) * 2]; _lastRight = interleaved[(frames - 1) * 2 + 1]; }
    }

    public void Drain(CancellationToken token)
    {
        Release(token);
        device.Drain(token);
        _drained = true;
    }

    private void Release(CancellationToken token)
    {
        if (Math.Max(Math.Abs(_lastLeft), Math.Abs(_lastRight)) < 1e-7f) return;
        // The queued source is discarded on Stop. Continue from the last SUBMITTED sample
        // rather than replaying an old PCM block or restarting the decoder during shutdown.
        var samples = new float[HeadphoneRenderer.BlockSize * 2];
        var left = _lastLeft; var right = _lastRight;
        for (int start = 0; start < FadeFrames; start += HeadphoneRenderer.BlockSize)
        {
            var frames = Math.Min(HeadphoneRenderer.BlockSize, FadeFrames - start);
            for (int frame = 0; frame < frames; frame++)
            {
                var gain = (float)(.5 + .5 * Math.Cos(Math.PI * (start + frame) / (FadeFrames - 1)));
                samples[frame * 2] = left * gain;
                samples[frame * 2 + 1] = right * gain;
            }
            device.WaitForSpace(frames, token);
            device.Write(samples, frames);
        }
        _lastLeft = _lastRight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_drained && HasStarted)
            {
                // Playback cancellation must not cancel its own short release. This bounds
                // shutdown even if an output device has disappeared or stopped responding.
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                try { Drain(timeout.Token); }
                catch (OperationCanceledException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        }
        finally { device.Dispose(); }
    }
}
