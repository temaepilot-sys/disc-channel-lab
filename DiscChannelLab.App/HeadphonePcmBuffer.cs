using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Disc2Flac;

/// <summary>Bounded read-ahead of unprocessed PCM. Pose and mixer controls are applied after this queue.</summary>
public sealed class HeadphonePcmBuffer(int blockBytes) : IDisposable
{
    public const int PrebufferBlocks = 32; // 171 ms at 48 kHz / 256 frames; not rendered audio latency.
    public const int CapacityBlocks = 256; // 1.37s raw PCM; controls still run at device submission.
    readonly SemaphoreSlim slots = new(CapacityBlocks);
    private readonly BlockingCollection<byte[]> _blocks = new(CapacityBlocks);
    private readonly ConcurrentQueue<byte[]> _recycled = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExceptionDispatchInfo? _failure;
    private int _produced;
    public Task Ready => _ready.Task;
    public int Count => _blocks.Count;
    public byte[] Rent() => _recycled.TryDequeue(out var block) ? block : new byte[blockBytes];
    public void Recycle(byte[] block) => _recycled.Enqueue(block);
    public void Add(byte[] block, CancellationToken token)
    {
        if (block.Length != blockBytes) throw new ArgumentException("Incorrect headphone PCM block size.");
        slots.Wait(token);
        AddReserved(block, token);
    }
    public async ValueTask AddAsync(byte[] block, CancellationToken token)
    {
        if (block.Length != blockBytes) throw new ArgumentException("Incorrect headphone PCM block size.");
        await slots.WaitAsync(token).ConfigureAwait(false);
        AddReserved(block, token);
    }
    void AddReserved(byte[] block, CancellationToken token)
    {
        try { _blocks.Add(block, token); }
        catch { slots.Release(); throw; }
        if (++_produced >= PrebufferBlocks) _ready.TrySetResult();
    }
    public void Complete(Exception? error = null)
    {
        if (error is not null) Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(error));
        _blocks.CompleteAdding();
        _ready.TrySetResult(); // Short tracks and failures also unblock startup.
    }
    public void WaitUntilReady(CancellationToken token)
    {
        _ready.Task.WaitAsync(token).GetAwaiter().GetResult();
        Volatile.Read(ref _failure)?.Throw();
    }
    public IEnumerable<byte[]> ReadAll(CancellationToken token, Action? whileWaiting = null)
    {
        while (!_blocks.IsCompleted)
        {
            if (!_blocks.TryTake(out var block, 10, token)) { whileWaiting?.Invoke(); continue; }
            slots.Release();
            Volatile.Read(ref _failure)?.Throw();
            yield return block;
        }
        Volatile.Read(ref _failure)?.Throw();
    }
    public void Dispose() { _blocks.Dispose(); slots.Dispose(); }
}
