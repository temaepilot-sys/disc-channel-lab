using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

namespace Disc2Flac;

/// <summary>Bounded decode read-ahead. Playback never waits for the optional visualization observer.
/// Raw PCM is kept unchanged; the real mixer still runs immediately before writing to ffplay.</summary>
public sealed class VisualizerLookaheadStream : Stream
{
    sealed record Block(long Frame, byte[] Bytes);
    const int FramesPerBlock = 1024, Capacity = 384; // 8.192 seconds, at most 6 MiB of 8-channel PCM.
    static int nextRevision;
    readonly Stream source;
    readonly string[] channels;
    readonly Func<PreviewMixState>? mix;
    readonly Func<double> volume;
    readonly PreviewPcmTap tap;
    readonly CancellationTokenSource stop;
    readonly object gate = new();
    readonly Queue<Block> pending = new();
    readonly SemaphoreSlim slots = new(Capacity), ready = new(0);
    readonly Task producer, observer;
    Block? current;
    int currentOffset;
    bool complete;
    Exception? failure;

    public VisualizerLookaheadStream(Stream source, string[] channels, Func<PreviewMixState>? mix,
        Func<double> volume, PreviewPcmTap tap, CancellationToken token)
    {
        this.source = source; this.channels = channels; this.mix = mix; this.volume = volume; this.tap = tap;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        producer = Task.Run(ProduceAsync); observer = Task.Run(ObserveAsync);
    }

    async Task ProduceAsync()
    {
        try
        {
            long frame = 0;
            while (!stop.IsCancellationRequested)
            {
                // Do not decode seconds ahead when no viewer is attached.
                while (!tap.Enabled())
                {
                    lock (gate) { if (pending.Count < 3) break; }
                    await Task.Delay(10, stop.Token).ConfigureAwait(false);
                }
                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                var bytes = new byte[FramesPerBlock * channels.Length * 2];
                int count = await source.ReadAtLeastAsync(bytes, bytes.Length, false, stop.Token).ConfigureAwait(false);
                if (count == 0) { slots.Release(); break; }
                if (count % (channels.Length * 2) != 0) throw new InvalidDataException("Truncated decoded PCM sample.");
                if (count != bytes.Length) Array.Resize(ref bytes, count);
                lock (gate) pending.Enqueue(new(frame, bytes));
                ready.Release(); frame += count / (channels.Length * 2);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { lock (gate) failure = e; }
        finally { lock (gate) complete = true; ready.Release(); }
    }

    async Task ObserveAsync()
    {
        PreviewMixState? previousMix = null;
        double previousGain = double.NaN;
        long nextFrame = -1;
        int revision = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!tap.Enabled()) { revision = 0; await Task.Delay(30, stop.Token).ConfigureAwait(false); continue; }
                var state = mix?.Invoke();
                var requested = volume();
                var gain = double.IsFinite(requested) ? Math.Clamp(requested, 0, 2) : 1;
                if (revision == 0 || state != previousMix || gain != previousGain)
                {
                    revision = Interlocked.Increment(ref nextRevision); nextFrame = -1;
                    previousMix = state; previousGain = gain;
                }
                Block[] blocks;
                lock (gate) blocks = pending.Where(b => b.Frame >= nextFrame).ToArray();
                var left = new double[channels.Length]; var right = new double[channels.Length];
                if (state is not null) StereoPreviewMixer.BuildWeights(channels, state, left, right);
                else { left[0] = 1; right[1] = 1; }
                foreach (var block in blocks)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (!tap.Enabled() || mix?.Invoke() != state || volume() != requested) break;
                    int frames = block.Bytes.Length / (channels.Length * 2);
                    var post = new float[frames * channels.Length]; var output = new byte[frames * 4];
                    for (int f = 0; f < frames; f++)
                    {
                        double l = 0, r = 0;
                        for (int c = 0; c < channels.Length; c++)
                        {
                            int sample = BinaryPrimitives.ReadInt16LittleEndian(block.Bytes.AsSpan((f * channels.Length + c) * 2, 2));
                            l += sample * left[c]; r += sample * right[c];
                            post[f * channels.Length + c] = (float)(sample / 32768d * Math.Sqrt(left[c] * left[c] + right[c] * right[c]) * gain);
                        }
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 4, 2), (short)Math.Clamp(Math.Round(l * gain), short.MinValue, short.MaxValue));
                        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 4 + 2, 2), (short)Math.Clamp(Math.Round(r * gain), short.MinValue, short.MaxValue));
                    }
                    tap.TryPublish(new(block.Frame / 48000d, channels, block.Bytes, post, output, revision));
                    nextFrame = block.Frame + frames;
                }
                await Task.Delay(30, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        { /* Losing an optional forecast never interrupts audio. */ }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); stop.Token.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        if (current is null)
        {
            while (true)
            {
                lock (gate)
                {
                    if (complete && pending.Count == 0)
                    {
                        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                        return 0;
                    }
                }
                await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    if (pending.TryDequeue(out current)) { slots.Release(); currentOffset = 0; break; }
                    if (complete)
                    {
                        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                        return 0;
                    }
                }
            }
        }
        int count = Math.Min(buffer.Length, current.Bytes.Length - currentOffset);
        current.Bytes.AsMemory(currentOffset, count).CopyTo(buffer); currentOffset += count;
        if (currentOffset == current.Bytes.Length) current = null;
        return count;
    }

    public override async ValueTask DisposeAsync()
    {
        stop.Cancel(); await Task.WhenAll(producer, observer).ConfigureAwait(false);
        stop.Dispose(); slots.Dispose(); ready.Dispose(); GC.SuppressFinalize(this);
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
