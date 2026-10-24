using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;

namespace Disc2Flac;

public sealed record SpatialPlaybackStats(long Frames, long EmptyBuffers, long Clips, double MaxBlockMilliseconds, double PreflightP95Milliseconds,
    double MaxWriteGapMilliseconds, int[] EmptyBufferAtMilliseconds);
public sealed record SpatialBusFrame(double Seconds, float[] Samples);

/// <summary>Disk read-ahead is bounded and separate from MMCSS DSP/WASAPI; UI/3D never runs on this thread.</summary>
public static class SpatialPcmPlayer
{
    public static async Task<SpatialPlaybackStats> PlayAsync(SpatialInput input, SpatialRenderSnapshot settings, PreviewPlaybackControl transport,
        CancellationToken token, bool livePose = false, Func<IHeadphoneOutput>? outputFactory = null,
        Action<float[], int>? rawDspCapture = null, Func<HeadphoneOptions>? liveOptions = null, Func<double>? liveMaster = null,
        Action<SpatialBusFrame>? observe = null, Func<PreviewMixState>? liveOutputMix = null, Func<PreviewMixState>? liveSourceMix = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); token = stop.Token;
        double preflight = await CheckBudget(input, settings, token);
        using var queue = new HeadphonePcmBuffer(256 * input.StoredChannels * 4);
        using var renderer = await Task.Run(() => new SpatialBlockRenderer(input, settings), token);
        var observations = observe is null ? null : Channel.CreateBounded<SpatialBusFrame>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true });
        var observer = observations is null ? Task.CompletedTask : Task.Run(async () => { await foreach (var frame in observations.Reader.ReadAllAsync()) { try { observe!(frame); } catch { /* Observers cannot stop audio delivery. */ } } });
        var producer = Task.Run(async () =>
        {
            try
            {
                using var file = SpatialPcmSource.Open(input); long remaining = input.Frames;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(256, remaining); var bytes = queue.Rent(); Array.Clear(bytes);
                    await file.ReadExactlyAsync(bytes.AsMemory(0, n * input.StoredChannels * 4), token); await queue.AddAsync(bytes, token); remaining -= n;
                }
                queue.Complete();
            }
            catch (Exception ex) { queue.Complete(ex); }
        }, token);
        long framesWritten = 0, gaps = 0; double maximum = 0, maximumGap = 0;
        var emptyEvents = new int[64]; int emptyCount = 0;
        var consumer = HeadphoneAudioThread.Run(() =>
        {
            queue.WaitUntilReady(token);
            using var scheduling = new HeadphoneAudioThread.Scheduling();
            using var output = new HeadphonePlaybackOutput((outputFactory ?? AudioNavigationService.HeadphoneOutputFactory)());
            output.Prepare(token); transport.Attach(output);
            long lastWrite = 0, lastPauseTicks = 0; var deliveryWatch = Stopwatch.StartNew();
            try
            {
                foreach (var bytes in queue.ReadAll(token, () => transport.Apply(output, token)))
                {
                    try
                    {
                        token.ThrowIfCancellationRequested(); transport.Apply(output, token); output.WaitForSpace(256, token);
                        if (framesWritten >= transport.EndFrame) break;
                        long before = Stopwatch.GetTimestamp();
                        renderer.Render(bytes, livePose ? HeadphoneSession.Current.Pose : null, liveOptions?.Invoke(), liveMaster?.Invoke(), liveOutputMix?.Invoke(), liveSourceMix?.Invoke());
                        maximum = Math.Max(maximum, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                        // Test-only hook; ordinary observers must remain outside the audio thread.
                        rawDspCapture?.Invoke(renderer.Output, 256);
                        if (observations is not null && framesWritten % 1024 == 0) observations.Writer.TryWrite(new(framesWritten / 48000d, (float[])renderer.Output.Clone()));
                        foreach (ref float v in renderer.Output.AsSpan()) v = Math.Clamp(v, -1, 1);
                        output.Write(renderer.Output, 256); transport.Sample(output); framesWritten += 256;
                        RecordDelivery();
                    }
                    finally { queue.Recycle(bytes); }
                }
                int tails = renderer.TailBlocks; renderer.BeginTail();
                for (int b = 0; b < tails; b++)
                {
                    transport.Apply(output, token); output.WaitForSpace(256, token); renderer.Render([]);
                    rawDspCapture?.Invoke(renderer.Output, 256);
                    foreach (ref float v in renderer.Output.AsSpan()) v = Math.Clamp(v, -1, 1);
                    output.Write(renderer.Output, 256); transport.Sample(output);
                    RecordDelivery();
                }
                output.Drain(token); gaps = output.EmptyBufferCount;
            }
            finally { transport.Detach(); observations?.Writer.TryComplete(); }
            void RecordDelivery()
            {
                long now = Stopwatch.GetTimestamp(), pauseTicks = transport.PauseTicks;
                if (lastWrite != 0) maximumGap = Math.Max(maximumGap, Stopwatch.GetElapsedTime(lastWrite, now - (pauseTicks - lastPauseTicks)).TotalMilliseconds);
                lastWrite = now; lastPauseTicks = pauseTicks;
                if (output.EmptyBufferCount > emptyCount)
                {
                    if (emptyCount < emptyEvents.Length) emptyEvents[emptyCount] = (int)deliveryWatch.ElapsedMilliseconds;
                    emptyCount = (int)output.EmptyBufferCount;
                }
            }
        });
        _ = consumer.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await Task.WhenAll(consumer, producer); return new(framesWritten, gaps, renderer.Clips, maximum, preflight, maximumGap, emptyEvents.Take(Math.Min(emptyCount,64)).ToArray()); }
        finally { stop.Cancel(); observations?.Writer.TryComplete(); await observer; }
    }

    public static Task<double> CheckBudget(SpatialInput input, SpatialRenderSnapshot snapshot, CancellationToken token) => Task.Run(() =>
    {
        if (snapshot.Options.Method == HeadphoneMethod.MeasuredBrir)
            BrirLibrary.Prepare(snapshot.Options.BrirPath, snapshot.Options.BrirHash, token);
        using var probe = new SpatialBlockRenderer(input, snapshot); using var file = SpatialPcmSource.Open(input);
        var bytes = new byte[(int)Math.Min(input.Frames, 256) * input.StoredChannels * 4]; file.ReadExactly(bytes);
        var samples = new double[96];
        for (int i = -32; i < samples.Length; i++)
        {
            token.ThrowIfCancellationRequested(); long before = Stopwatch.GetTimestamp();
            probe.Render(bytes, new HeadPose(i % 31 - 15, i % 13 - 6, 0));
            if (i >= 0) samples[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
        }
        Array.Sort(samples); double p95 = samples[(int)(samples.Length * .95)];
        // Prepare terminal paths too. First-use JIT/PInvoke must not run when the final device buffer is draining.
        int tails = probe.TailBlocks; probe.BeginTail(); for (int t = 0; t < tails; t++) { token.ThrowIfCancellationRequested(); probe.Render([]); }
        if (p95 > 4.5) throw new InvalidOperationException($"Realtime DSP budget exceeded: p95 {p95:0.00} ms / 5.33 ms. Use Pure HRTF, another programme, or offline export. No sources were dropped.");
        return p95;
    }, token);

    public static async Task PlayCachedAsync(RenderedAudio audio, double gainDb, PreviewPlaybackControl transport, CancellationToken token)
    {
        // A/B uses cached shared-DSP PCM, a fixed pose, and one constant gain per version.
        var scene = SpatialScene.ChannelBed(["FL", "FR"], audio.SourceFrames);
        var input = new SpatialInput(audio.PcmPath, 2, scene, 0, audio.SourceFrames, audio.Input.SourceName, "comparison");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); token = stop.Token;
        using var queue = new HeadphonePcmBuffer(2048);
        var producer = Task.Run(async () =>
        {
            try
            {
                using var file = File.OpenRead(input.PcmPath); long remaining = input.Frames;
                while (remaining > 0) { int n = (int)Math.Min(256, remaining); var bytes = queue.Rent(); Array.Clear(bytes); await file.ReadExactlyAsync(bytes.AsMemory(0, n * 8), token); await queue.AddAsync(bytes, token); remaining -= n; }
                queue.Complete();
            }
            catch (Exception ex) { queue.Complete(ex); }
        }, token);
        var consumer = HeadphoneAudioThread.Run(() =>
        {
            queue.WaitUntilReady(token); using var scheduling = new HeadphoneAudioThread.Scheduling();
            using var output = new HeadphonePlaybackOutput(AudioNavigationService.HeadphoneOutputFactory()); output.Prepare(token); transport.Attach(output);
            var stereo = new float[512]; long remaining = input.Frames; float gain = (float)Math.Pow(10, gainDb / 20);
            try
            {
                foreach (var bytes in queue.ReadAll(token, () => transport.Apply(output, token)))
                {
                    try
                    {
                        int n = (int)Math.Min(256, remaining); transport.Apply(output, token); output.WaitForSpace(n, token);
                        for (int i = 0; i < n * 2; i++) stereo[i] = Math.Clamp(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4))) * gain, -1, 1);
                        output.Write(stereo, n); transport.Sample(output); remaining -= n;
                    }
                    finally { queue.Recycle(bytes); }
                }
                output.Drain(token);
            }
            finally { transport.Detach(); }
        });
        _ = consumer.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await Task.WhenAll(consumer, producer); } finally { stop.Cancel(); }
    }
}
