using System.Buffers.Binary;

namespace Disc2Flac;

/// <summary>One producer/one consumer, fixed buffers. The audio thread never waits
/// for meters, UI callbacks, pipe consumers, or visualization sample conversion.</summary>
public sealed class PreviewAudioObserver : IDisposable
{
    const int Capacity = 8, Frames = 1024;
    sealed class Packet(int channels)
    {
        public readonly float[] Input = new float[Frames * channels], Left = new float[Frames * channels],
            Right = new float[Frames * channels], Post = new float[Frames * channels], Output = new float[Frames * 2];
        public long FirstFrame, PlayedFrames, Clips, Gaps;
        public int Count;
    }
    readonly Packet[] packets;
    readonly string[] channels;
    readonly long startTicks;
    readonly PreviewPcmTap? tap;
    readonly Action<long, MixerMeterFrame>? meters;
    readonly Action<long, double>? position;
    readonly PreviewPlaybackControl? transport;
    readonly double bufferMilliseconds;
    readonly AutoResetEvent available = new(false);
    readonly Thread worker;
    int write, read, partial, complete, disposed;
    long nextFrame;
    public long DroppedPackets { get; private set; }
    public long Errors { get; private set; }
    public PreviewAudioObserver(string[] channels, long startTicks, PreviewPcmTap? tap,
        Action<long, MixerMeterFrame>? meters, Action<long, double>? position,
        PreviewPlaybackControl? transport, double bufferMilliseconds)
    {
        this.channels = channels; this.startTicks = startTicks; this.tap = tap; this.meters = meters;
        this.position = position; this.transport = transport; this.bufferMilliseconds = bufferMilliseconds;
        packets = Enumerable.Range(0, Capacity).Select(_ => new Packet(channels.Length)).ToArray();
        worker = new Thread(Run) { IsBackground = true, Name = "DiscChannelLab playback observers", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    public void Capture(float[][] input, IPreviewPcmRenderer renderer, float[] output,
        int frames, long firstFrame, long playedFrames, long gaps)
    {
        if (Volatile.Read(ref write) - Volatile.Read(ref read) >= Capacity)
        { DroppedPackets++; partial = 0; return; }
        if (partial > 0 && firstFrame != nextFrame) partial = 0;
        int offset = 0;
        while (offset < frames)
        {
            if (Volatile.Read(ref write) - Volatile.Read(ref read) >= Capacity)
            { DroppedPackets++; partial = 0; return; }
            var packet = packets[write & (Capacity - 1)];
            if (partial == 0) packet.FirstFrame = firstFrame + offset;
            int count = Math.Min(frames - offset, Frames - partial);
            for (int f = 0; f < count; f++)
            {
                int source = offset + f, target = partial + f;
                packet.Output[target * 2] = output[source * 2]; packet.Output[target * 2 + 1] = output[source * 2 + 1];
                for (int c = 0; c < channels.Length; c++)
                {
                    int k = target * channels.Length + c;
                    packet.Input[k] = input[c][source]; packet.Left[k] = renderer.ChannelLeft[c][source]; packet.Right[k] = renderer.ChannelRight[c][source];
                    packet.Post[k] = renderer.PostSample(c, source);
                }
            }
            partial += count; offset += count;
            packet.PlayedFrames = playedFrames; packet.Clips = renderer.ClippedSamples; packet.Gaps = gaps;
            if (partial == Frames) Commit();
        }
        nextFrame = firstFrame + frames;
    }
    void Commit()
    {
        if (partial == 0) return;
        packets[write & (Capacity - 1)].Count = partial; partial = 0;
        Volatile.Write(ref write, write + 1); available.Set();
    }
    void Run()
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref read) == Volatile.Read(ref write))
                { if (Volatile.Read(ref complete) != 0) break; available.WaitOne(50); continue; }
                try { Process(packets[read & (Capacity - 1)]); }
                catch (Exception e) when (e is not OutOfMemoryException) { Errors++; }
                finally { Volatile.Write(ref read, read + 1); }
            }
        }
        finally { available.Dispose(); }
    }
    void Process(Packet p)
    {
        if (Volatile.Read(ref complete) == 0) HeadphoneSession.Current.ReportOutput(bufferMilliseconds, p.Clips, p.Gaps);
        position?.Invoke(startTicks, transport?.Position ?? p.PlayedFrames / 48000d);
        if (meters is not null)
        {
            var peak = new double[channels.Length]; var rms = new double[channels.Length];
            var left = new double[channels.Length]; var right = new double[channels.Length]; var final = new double[2];
            double Db(double v) => v > 0 ? 20 * Math.Log10(v) : -120;
            for (int f = 0; f < p.Count; f++)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    int k = f * channels.Length + c;
                    peak[c] = Math.Max(peak[c], Math.Abs(p.Input[k])); rms[c] += (double)p.Input[k] * p.Input[k];
                    left[c] = Math.Max(left[c], Math.Abs(p.Left[k])); right[c] = Math.Max(right[c], Math.Abs(p.Right[k]));
                }
                final[0] = Math.Max(final[0], Math.Abs(p.Output[f * 2])); final[1] = Math.Max(final[1], Math.Abs(p.Output[f * 2 + 1]));
            }
            meters(startTicks, new((p.FirstFrame + p.Count / 2d) / 48000, peak.Select(Db).ToArray(),
                rms.Select(s => Db(Math.Sqrt(s / p.Count))).ToArray(), left.Select(Db).ToArray(), right.Select(Db).ToArray(), final.Select(Db).ToArray()));
        }
        if (tap?.Enabled() != true) return;
        var source = new byte[p.Count * channels.Length * 2]; var post = new float[p.Count * channels.Length]; var output = new byte[p.Count * 4];
        short Pcm(float v) => (short)Math.Clamp((int)Math.Round(v * 32768), short.MinValue, short.MaxValue);
        for (int f = 0; f < p.Count; f++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                int k = f * channels.Length + c; BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(k * 2, 2), Pcm(p.Input[k])); post[k] = p.Post[k];
            }
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 4, 2), Pcm(p.Output[f * 2]));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 4 + 2, 2), Pcm(p.Output[f * 2 + 1]));
        }
        tap.TryPublish(new(startTicks / 45000d + p.FirstFrame / 48000d, channels, source, post, output));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Commit(); Volatile.Write(ref complete, 1);
        try { available.Set(); } catch (ObjectDisposedException) { }
        // A hostile/blocked observer cannot hold Stop or a new track hostage.
        worker.Join(250);
    }
}
