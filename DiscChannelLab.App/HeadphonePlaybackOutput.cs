namespace Disc2Flac;

/// <summary>De-clicks transport boundaries, after spatialization and before the device buffer.</summary>
public sealed class HeadphonePlaybackOutput(IHeadphoneOutput device) : IHeadphoneOutput
{
    public const int FadeFrames = HeadphoneRenderer.SampleRate * 8 / 1000;
    public const int MinimumGuardFrames = HeadphoneRenderer.SampleRate * 64 / 1000;
    private int _attackFrame;
    private long _mediaFrames, _prefixFrames;
    private int _closingSilenceFrames;
    private bool _prepared;
    private readonly float[] _silence = new float[HeadphoneRenderer.BlockSize * 2];
    private float _lastLeft, _lastRight;
    private bool _drained, _disposed;
    // Transport guards are device housekeeping, not media. Keep the seek bar, meters
    // and visualizer timestamps on the source clock, including short/canceled starts.
    public long SubmittedFrames => _mediaFrames;
    public long PlayedFrames => Math.Clamp(device.PlayedFrames - _prefixFrames, 0, _mediaFrames);
    public double BufferMilliseconds => device.BufferMilliseconds;
    public double StreamLatencyMilliseconds => device.StreamLatencyMilliseconds;
    public bool HasStarted => _mediaFrames > 0 && device.HasStarted && device.PlayedFrames >= _prefixFrames;
    public long EmptyBufferCount => device.EmptyBufferCount;
    public int GuardFrames { get; } = (int)Math.Ceiling(Math.Clamp(Math.Max(64,
        device.BufferMilliseconds + device.StreamLatencyMilliseconds), 64, 500) *
        HeadphoneRenderer.SampleRate / (1000 * HeadphoneRenderer.BlockSize)) * HeadphoneRenderer.BlockSize;

    public void Prepare(CancellationToken token)
    {
        if (_prepared || _mediaFrames != 0) throw new InvalidOperationException("Output must be prepared once, before source audio.");
        _prepared = true;
        // Starting on music can expose a device/resampler/APO's previous output during
        // activation. Prime and run silence first, then queue the NEW source in order.
        // Do not drain here: the stream must stay running without an empty-buffer gap.
        for (int i = 0; i < GuardFrames; i += HeadphoneRenderer.BlockSize)
        {
            token.ThrowIfCancellationRequested();
            device.WaitForSpace(HeadphoneRenderer.BlockSize, token);
            device.Write(_silence, HeadphoneRenderer.BlockSize);
            _prefixFrames += HeadphoneRenderer.BlockSize;
        }
    }

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
        _mediaFrames += frames;
        if (frames > 0) { _lastLeft = interleaved[(frames - 1) * 2]; _lastRight = interleaved[(frames - 1) * 2 + 1]; }
    }

    public void Drain(CancellationToken token)
    {
        if (_drained) return;
        Release(token);
        // Padding==0 only describes the client queue. Leave the downstream converter,
        // Windows effects and hardware with silence before Stop/Reset and the next Play.
        if (_prepared && _mediaFrames > 0)
            while (_closingSilenceFrames < GuardFrames)
            {
                token.ThrowIfCancellationRequested();
                device.WaitForSpace(HeadphoneRenderer.BlockSize, token);
                device.Write(_silence, HeadphoneRenderer.BlockSize);
                _closingSilenceFrames += HeadphoneRenderer.BlockSize;
            }
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
            if (!_drained && device.HasStarted && _mediaFrames > 0)
            {
                // Playback cancellation must not cancel its own short release. This bounds
                // shutdown even if an output device has disappeared or stopped responding.
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                    Math.Max(150, GuardFrames * 1000d / HeadphoneRenderer.SampleRate + BufferMilliseconds + 100)));
                try { Drain(timeout.Token); }
                catch (OperationCanceledException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        }
        finally { device.Dispose(); }
    }
}
