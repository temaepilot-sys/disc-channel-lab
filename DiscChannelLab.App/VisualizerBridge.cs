using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Disc2Flac;

public sealed record PreviewPcmChunk(double Seconds, string[] Channels, byte[] Input, float[] Post, byte[] Output, int ForecastRevision = 0);

/// <summary>An optional observer. It never owns the playback stream or waits for a viewer.</summary>
public sealed record PreviewPcmTap(Func<bool> Enabled, Action<PreviewPcmChunk> Publish, bool Lookahead = false)
{
    public PreviewPcmTap AtOffset(double seconds) => new(Enabled, frame => Publish(frame with { Seconds = frame.Seconds + seconds }), Lookahead);
    public void TryPublish(PreviewPcmChunk frame)
    {
        // A failed visualizer must never abort audio playback.
        try { Publish(frame); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}

public sealed record VisualizerClock(int Version, int Epoch, string Name, string Codec, int SourceSampleRate,
    double Position, double Duration, bool Playing, long ClockMilliseconds);

public sealed class VisualizerBridge : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<object> _messages = Channel.CreateBounded<object>(new BoundedChannelOptions(12)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _worker;
    private readonly Channel<object> _forecasts = Channel.CreateBounded<object>(new BoundedChannelOptions(512)
        { FullMode = BoundedChannelFullMode.DropOldest });
    private int _forecastRevision;
    private readonly object _forecastGate = new();
    private volatile bool _connected;
    private Process? _viewer;
    public string PipeName { get; } = $"DiscChannelLab.SpaceSketch.v1.{Environment.ProcessId}.{Guid.NewGuid():N}";
    public bool IsConnected => _connected;
    public string? LastError { get; private set; }
    public void Start() => _worker ??= Task.Run(RunAsync);

    public void Open(string executable)
    {
        Start();
        if (_viewer is { HasExited: false }) return;
        _viewer?.Dispose();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--live-pipe"); start.ArgumentList.Add(PipeName);
        _viewer = Process.Start(start) ?? throw new IOException("3D viewer を起動できません。");
    }

    public void PublishClock(int epoch, string name, string codec, int sampleRate, double position, double duration, bool playing)
    {
        if (!_connected) return;
        _messages.Writer.TryWrite(new VisualizerClock(1, epoch, name, codec, sampleRate, position, duration, playing, Environment.TickCount64));
    }
    public void PublishPcm(int epoch, PreviewPcmChunk frame)
    {
        if (!_connected) return;
        if (frame.ForecastRevision == 0) { _messages.Writer.TryWrite((epoch, frame)); return; }
        lock (_forecastGate)
        {
            if (frame.ForecastRevision < _forecastRevision) return;
            if (frame.ForecastRevision != _forecastRevision)
            {
                _forecastRevision = frame.ForecastRevision;
                while (_forecasts.Reader.TryRead(out _)) { }
            }
            _forecasts.Writer.TryWrite((epoch, frame));
        }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                while (_messages.Reader.TryRead(out _)) { }
                while (_forecasts.Reader.TryRead(out _)) { }
                _connected = true; LastError = null;
                while (!_stop.IsCancellationRequested)
                {
                    if (!_messages.Reader.TryRead(out var message) && !_forecasts.Reader.TryRead(out message))
                    {
                        await Task.Delay(5, _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    var payload = Encode(message);
                    byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
                    await pipe.WriteAsync(length, _stop.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(payload, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastError = e.Message;
                try { await Task.Delay(300, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            finally { _connected = false; }
        }
    }

    public static byte[] Encode(object message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if (message is VisualizerClock clock)
        {
            writer.Write((byte)1);
            writer.Write(JsonSerializer.SerializeToUtf8Bytes(clock));
        }
        else if (message is ValueTuple<int, PreviewPcmChunk> data)
        {
            var (epoch, frame) = data;
            writer.Write((byte)(frame.ForecastRevision == 0 ? 2 : 3)); writer.Write(epoch);
            if (frame.ForecastRevision != 0) writer.Write(frame.ForecastRevision);
            writer.Write(frame.Seconds);
            writer.Write(string.Join(',', frame.Channels)); writer.Write(frame.Output.Length / 4);
            writer.Write(frame.Input);
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Post.AsSpan()));
            writer.Write(frame.Output);
        }
        else throw new ArgumentException("Unknown visualization message.");
        return stream.ToArray();
    }

    public void Dispose()
    {
        _connected = false; _stop.Cancel(); _messages.Writer.TryComplete(); _forecasts.Writer.TryComplete(); _viewer?.Dispose();
        // The viewer is independent; it remains open and reports the disconnected player.
    }
}
