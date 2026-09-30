using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DiscChannelLab.Visualizer;

public sealed record PlayerClock(int Version, int Epoch, string Name, string Codec, int SourceSampleRate,
    double Position, double Duration, bool Playing, long ClockMilliseconds);

/// <summary>Receives PCM on its own worker; never decodes or plays audio in linked mode.</summary>
public sealed class LiveReceiver(string? pipeName, VisualizationSettings settings, Action<string> log) : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly Queue<(double Time, float[] Values)> frames = new();
    PlayerClock? clock;
    string[] channels = [];
    SpectrumAnalyzer? analyzer;
    int analyzerEpoch = -1;
    double expectedTime = -1, baseTime;
    (double Time, float[] Values)? selected;
    bool connected;
    long revision;
    string? lastError;
    Task? worker;
    public bool Configured => !string.IsNullOrWhiteSpace(pipeName);
    public void Start() { if (Configured) worker = Task.Run(RunAsync); }

    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName!, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(1500, stop.Token).ConfigureAwait(false);
                lock (gate) { connected = true; clock = null; lastError = null; Reset(); }
                log("DiscChannelLab live pipe connected");
                var size = new byte[4];
                while (!stop.IsCancellationRequested)
                {
                    await pipe.ReadExactlyAsync(size, stop.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(size);
                    if (length is < 2 or > 262144) throw new InvalidDataException("Invalid link packet size.");
                    var payload = new byte[length];
                    await pipe.ReadExactlyAsync(payload, stop.Token).ConfigureAwait(false);
                    Consume(payload);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                lock (gate) { connected = false; lastError = e is TimeoutException ? null : e.Message; }
                if (e is not TimeoutException) log("Live receiver: " + e.Message);
                try { await Task.Delay(500, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            finally { lock (gate) connected = false; }
        }
    }

    public void Consume(byte[] payload)
    {
        if (payload.Length == 0) throw new InvalidDataException("Empty live packet.");
        if (payload[0] == 1)
        {
            var next = JsonSerializer.Deserialize<PlayerClock>(payload.AsSpan(1)) ?? throw new InvalidDataException("Clock is missing.");
            if (next.Version != 1 || !double.IsFinite(next.Position) || !double.IsFinite(next.Duration) || next.Position < 0 || next.Duration < 0)
                throw new InvalidDataException("Unsupported link protocol or invalid playback position.");
            lock (gate)
            {
                if (clock?.Epoch != next.Epoch) Reset();
                clock = next; connected = true;
            }
            return;
        }
        if (payload[0] != 2) throw new InvalidDataException("Unknown live packet type.");
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        reader.ReadByte(); int epoch = reader.ReadInt32(); double time = reader.ReadDouble();
        string[] codes = reader.ReadString().Split(','); int count = reader.ReadInt32();
        string[] allowed = ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR", "BC", "FLC", "FRC", "TFL", "TFR"];
        if (codes.Length is < 1 or > 8 || codes.Distinct().Count() != codes.Length || codes.Any(c => !allowed.Contains(c)) ||
            count is < 1 or > 4096 || !double.IsFinite(time) || time < -.001 ||
            reader.BaseStream.Length - reader.BaseStream.Position != count * (codes.Length * 6 + 4))
            throw new InvalidDataException("Invalid linked PCM data.");
        lock (gate)
        {
            if (clock is null || clock.Epoch != epoch) return; // Discard old seek/session data.
            if (analyzer is null || analyzerEpoch != epoch || !channels.SequenceEqual(codes) || Math.Abs(time - expectedTime) > 2d / 48000)
            {
                Reset(); channels = codes; analyzerEpoch = epoch; baseTime = time;
                analyzer = new SpectrumAnalyzer(codes.Length * 2 + 2, settings, (seconds, values) =>
                {
                    lock (gate)
                    {
                        frames.Enqueue((baseTime + seconds, values));
                        while (frames.Count > 192) frames.Dequeue();
                    }
                }, retainFrames: false);
            }
            expectedTime = time + count / 48000d;
        }
        int inputStart = (int)reader.BaseStream.Position;
        int postStart = inputStart + count * codes.Length * 2;
        int outputStart = postStart + count * codes.Length * 4;
        var sample = new float[codes.Length * 2 + 2];
        var current = analyzer!;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < codes.Length; c++)
            {
                sample[c] = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(inputStart + (i * codes.Length + c) * 2, 2)) / 32768f;
                sample[c + codes.Length] = BitConverter.ToSingle(payload, postStart + (i * codes.Length + c) * 4);
            }
            sample[^2] = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(outputStart + i * 4, 2)) / 32768f;
            sample[^1] = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(outputStart + i * 4 + 2, 2)) / 32768f;
            current.Push(sample);
        }
    }

    void Reset()
    {
        frames.Clear(); selected = null; analyzer = null; expectedTime = -1; revision++;
    }

    public object Snapshot()
    {
        lock (gate)
        {
            var age = clock is null ? double.PositiveInfinity : Math.Max(0, (Environment.TickCount64 - clock.ClockMilliseconds) / 1000d);
            bool fresh = connected && age < .5;
            bool playing = fresh && clock?.Playing == true;
            double position = Math.Clamp((clock?.Position ?? 0) + (playing ? Math.Min(age, .25) : 0), 0, clock?.Duration ?? 0);
            while (frames.TryPeek(out var frame) && frame.Time <= position) selected = frames.Dequeue();
            bool hasData = selected is { } value && Math.Abs(position - value.Time) < .25;
            var values = hasData ? selected!.Value.Values : new float[(channels.Length * 2 + 2) * settings.BandCount];
            int width = channels.Length * settings.BandCount;
            return new
            {
                configured = Configured, connected = fresh, playing, hasData, revision, epoch = clock?.Epoch ?? -1,
                position, duration = clock?.Duration ?? 0, name = clock?.Name ?? "DiscChannelLab", codec = clock?.Codec ?? "",
                sourceSampleRate = clock?.SourceSampleRate ?? 48000, sampleRate = 48000, channels,
                bandCount = settings.BandCount, centers = settings.Centers, dispersion = settings.Centers.Select(DispersionAngleMapper.Map).ToArray(),
                input = values.Take(width).ToArray(), post = values.Skip(width).Take(width).ToArray(), output = values.Skip(width * 2).Take(settings.BandCount * 2).ToArray(),
                error = lastError
            };
        }
    }
    public void Dispose() { stop.Cancel(); }
}
