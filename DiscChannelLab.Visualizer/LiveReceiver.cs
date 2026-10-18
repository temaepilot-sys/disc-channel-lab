using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DiscChannelLab.Visualizer;

public sealed record PlayerClock(int Version, int Epoch, string Name, string Codec, int SourceSampleRate,
    double Position, double Duration, bool Playing, long ClockMilliseconds, HeadPoseValue? Head = null, string? HeadphoneRenderer = null, HeadphoneSceneValue? Scene = null,
    SpeakerPlaybackStateValue[]? SpeakerStates = null);
public sealed record SpeakerPlaybackStateValue(string Code, bool Muted, bool Solo, bool Enabled);
public sealed record HeadPoseValue(double Yaw, double Pitch, double Roll);
public sealed record HeadphoneSpeakerValue(string Code, double X, double Y, double Z);
public sealed record HeadphoneSceneValue(bool RotateWorld, double FrontAngle, HeadphoneSpeakerValue[] Speakers);

/// <summary>Receives PCM on its own worker; never decodes or plays audio in linked mode.</summary>
public sealed class LiveReceiver(string? pipeName, VisualizationSettings settings, Action<string> log, bool enableForecast = true) : IDisposable
{
    public event Action? OpenRequested;
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly Queue<(double Time, float[] Values, float[] FastValues)> frames = new();
    readonly Queue<(double Time, float[] Values)> forecasts = new();
    // Keep already received PCM, without decoding/FFT, while emission views are active.
    // The player can be eight seconds ahead: throwing these packets away makes a
    // later Listener view wait for that entire window to pass before it can emit.
    readonly Queue<(long Sequence, double Time, byte[] Payload)> rawForecasts = new();
    const int RawForecastLimit = 384, RawForecastByteLimit = 24 * 1024 * 1024;
    long rawSequence, warmVersion;
    int rawBytes, rawGeneration;
    bool warming;
    SpectrumAnalyzer? forecastAnalyzer;
    string[] forecastChannels = [];
    int forecastGeneration;
    long forecastRevision;
    double forecastExpected = -1, forecastBase, lastForecast = -1;
    PlayerClock? clock;
    string[] channels = [];
    SpectrumAnalyzer? analyzer;
    int analyzerEpoch = -1;
    double expectedTime = -1, baseTime;
    (double Time, float[] Values, float[] FastValues)? selected;
    bool connected;
    long revision;
    string? lastError;
    Task? worker;
    volatile bool forecastEnabled = enableForecast;
    public long ForecastPacketsAnalyzed { get; private set; }
    public (int Packets, int Bytes) ForecastCacheSize { get { lock (gate) return (rawForecasts.Count, rawBytes); } }
    public void SetForecastEnabled(bool enabled)
    {
        long version;
        lock (gate)
        {
            if (forecastEnabled == enabled) return;
            forecastEnabled = enabled;
            ResetForecast();
            version = ++warmVersion;
            warming = enabled && rawForecasts.Count > 0;
            if (!warming) return;
        }
        _ = Task.Run(() => WarmForecastsAsync(version));
    }

    async Task WarmForecastsAsync(long version)
    {
        long cursor = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                (long Sequence, double Time, byte[] Payload)[] batch;
                lock (gate)
                {
                    if (!forecastEnabled || version != warmVersion) return;
                    batch = rawForecasts.Where(p => p.Sequence > cursor && p.Time >= (clock?.Position ?? 0) - .15).Take(4).ToArray();
                    if (batch.Length == 0) { warming = false; return; }
                }
                foreach (var packet in batch) { ConsumePcm(packet.Payload, version); cursor = packet.Sequence; }
                // Yield between small batches so the audible PCM worker stays responsive.
                await Task.Delay(1, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { lock (gate) if (version == warmVersion) warming = false; }
    }

    void ClearRawForecasts()
    {
        rawForecasts.Clear(); rawBytes = rawGeneration = 0;
        ++warmVersion; warming = false;
    }
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
                lock (gate) { connected = true; clock = null; lastError = null; Reset(); ResetForecast(); ClearRawForecasts(); }
                log("DiscChannelLab live pipe connected");
                var size = new byte[4];
                while (!stop.IsCancellationRequested)
                {
                    await pipe.ReadExactlyAsync(size, stop.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(size);
                    if (length is < 1 or > 262144) throw new InvalidDataException("Invalid link packet size.");
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
        if (payload[0] == 4)
        {
            if (payload.Length != 1) throw new InvalidDataException("Invalid viewer open request.");
            OpenRequested?.Invoke();
            return;
        }
        if (payload[0] == 1)
        {
            var next = JsonSerializer.Deserialize<PlayerClock>(payload.AsSpan(1)) ?? throw new InvalidDataException("Clock is missing.");
            if (next.Version != 1 || !double.IsFinite(next.Position) || !double.IsFinite(next.Duration) || next.Position < 0 || next.Duration < 0)
                throw new InvalidDataException("Unsupported link protocol or invalid playback position.");
            if (next.Head is { } head && (!double.IsFinite(head.Yaw) || !double.IsFinite(head.Pitch) || !double.IsFinite(head.Roll)))
                throw new InvalidDataException("Invalid head orientation.");
            if (next.SpeakerStates is { } states && (states.Length > 8 || states.Any(s => s is null) || states.Select(s => s.Code).Distinct().Count() != states.Length ||
                states.Any(s => s.Code is not ("FL" or "FR" or "FC" or "LFE" or "SL" or "SR" or "BL" or "BR" or "BC" or "FLC" or "FRC" or "TFL" or "TFR"))))
                throw new InvalidDataException("Invalid speaker playback states.");
            if (next.Scene is { } scene && (scene.Speakers is null || scene.Speakers.Length > 8 || !double.IsFinite(scene.FrontAngle) ||
                scene.FrontAngle is < 15 or > 90 || scene.Speakers.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) ||
                    p.X * p.X + p.Y * p.Y + p.Z * p.Z is < .99 or > 1.01)))
                throw new InvalidDataException("Invalid headphone speaker positions.");
            lock (gate)
            {
                if (clock?.Epoch != next.Epoch) { Reset(); ResetForecast(); ClearRawForecasts(); }
                clock = next; connected = true;
            }
            return;
        }
        if (payload[0] is not (2 or 3)) throw new InvalidDataException("Unknown live packet type.");
        ConsumePcm(payload);
    }

    void ConsumePcm(byte[] payload, long? replayVersion = null)
    {
        bool forecast = payload[0] == 3;
        using var reader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        reader.ReadByte(); int epoch = reader.ReadInt32();
        int generation = forecast ? reader.ReadInt32() : 0;
        double time = reader.ReadDouble();
        string[] codes = reader.ReadString().Split(','); int count = reader.ReadInt32();
        string[] allowed = ["FL", "FR", "FC", "LFE", "SL", "SR", "BL", "BR", "BC", "FLC", "FRC", "TFL", "TFR"];
        if (codes.Length is < 1 or > 8 || codes.Distinct().Count() != codes.Length || codes.Any(c => !allowed.Contains(c)) ||
            count is < 1 or > 4096 || !double.IsFinite(time) || time < -.001 ||
            reader.BaseStream.Length - reader.BaseStream.Position != count * (codes.Length * 6 + 4))
            throw new InvalidDataException("Invalid linked PCM data.");
        SpectrumAnalyzer current;
        lock (gate)
        {
            if (clock is null || clock.Epoch != epoch) return; // Discard old seek/session data.
            if (forecast)
            {
                if (replayVersion is { } version && (version != warmVersion || !forecastEnabled)) return;
                if (generation <= 0 || generation < rawGeneration) return;
                if (replayVersion is null)
                {
                    if (generation != rawGeneration) { ClearRawForecasts(); ResetForecast(); rawGeneration = generation; }
                    rawForecasts.Enqueue((++rawSequence, time, payload)); rawBytes += payload.Length;
                    while (rawForecasts.Count > RawForecastLimit || rawBytes > RawForecastByteLimit ||
                        rawForecasts.TryPeek(out var oldest) && time - oldest.Time > 8.3)
                        rawBytes -= rawForecasts.Dequeue().Payload.Length;
                    if (!forecastEnabled || warming) return;
                }
                if (!forecastEnabled || generation <= 0 || generation < forecastGeneration) return;
                if (forecastAnalyzer is null || generation != forecastGeneration || !forecastChannels.SequenceEqual(codes) || Math.Abs(time - forecastExpected) > 2d / 48000)
                {
                    ResetForecast(); forecastGeneration = generation; forecastChannels = codes; forecastBase = time;
                    long analysisRevision = forecastRevision;
                    var forecastSettings = settings with { HopSize = Math.Min(settings.FftSize, Math.Max(settings.HopSize, 1920)) };
                    forecastAnalyzer = new SpectrumAnalyzer(codes.Length * 2 + 2, forecastSettings, (seconds, values) =>
                    {
                        lock (gate)
                        {
                            if (!forecastEnabled || analysisRevision != forecastRevision || generation != forecastGeneration || clock?.Epoch != epoch) return;
                            double timestamp = forecastBase + seconds;
                            if (timestamp - lastForecast < .04 - 1e-8) return; // <=25 Hz, tolerant of timestamp round-off.
                            lastForecast = timestamp; forecasts.Enqueue((timestamp, values));
                            while (forecasts.Count > 256) forecasts.Dequeue();
                        }
                    }, retainFrames: false);
                }
                forecastExpected = time + count / 48000d; current = forecastAnalyzer;
            }
            else
            {
                if (analyzer is null || analyzerEpoch != epoch || !channels.SequenceEqual(codes) || Math.Abs(time - expectedTime) > 2d / 48000)
                {
                    Reset(); channels = codes; analyzerEpoch = epoch; baseTime = time;
                    var currentSettings = settings with { HopSize = Math.Min(settings.FftSize, Math.Max(settings.HopSize, 1024)) };
                    analyzer = new SpectrumAnalyzer(codes.Length * 2 + 2, currentSettings, retainFrames: false, onDetailedFrame: (seconds, values, fastValues) =>
                    {
                        lock (gate)
                        {
                            frames.Enqueue((baseTime + seconds, values, fastValues));
                            while (frames.Count > 192) frames.Dequeue();
                        }
                    });
                }
                expectedTime = time + count / 48000d;
                current = analyzer!;
            }
        }
        int inputStart = (int)reader.BaseStream.Position;
        int postStart = inputStart + count * codes.Length * 2;
        int outputStart = postStart + count * codes.Length * 4;
        var sample = new float[codes.Length * 2 + 2];
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
        if (forecast) ForecastPacketsAnalyzed++;
    }

    void Reset()
    {
        frames.Clear(); selected = null; analyzer = null; expectedTime = -1; revision++;
    }

    void ResetForecast()
    {
        forecasts.Clear(); forecastAnalyzer = null; forecastExpected = lastForecast = -1; forecastRevision++;
        forecastChannels = []; forecastGeneration = 0;
    }

    public object Snapshot(string signal = "post", double lookaheadSeconds = 8.2)
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
            var fastValues = hasData ? selected!.Value.FastValues : values;
            int width = channels.Length * settings.BandCount;
            while (forecasts.TryPeek(out var old) && old.Time < position - .1) forecasts.Dequeue();
            signal = signal is "input" or "output" ? signal : "post";
            int forecastWidth = forecastChannels.Length * settings.BandCount;
            int start = signal == "input" ? 0 : signal == "post" ? forecastWidth : forecastWidth * 2;
            int length = signal == "output" ? settings.BandCount * 2 : forecastWidth;
            lookaheadSeconds = double.IsFinite(lookaheadSeconds) ? Math.Clamp(lookaheadSeconds, .5, 8.2) : 8.2;
            var ahead = forecasts.Where(f => f.Time <= position + lookaheadSeconds).ToArray();
            return new
            {
                configured = Configured, connected = fresh, playing, hasData, revision, epoch = clock?.Epoch ?? -1,
                position, duration = clock?.Duration ?? 0, name = clock?.Name ?? "DiscChannelLab", codec = clock?.Codec ?? "",
                sourceSampleRate = clock?.SourceSampleRate ?? 48000, sampleRate = 48000, channels,
                head = clock?.Head, headphoneRenderer = clock?.HeadphoneRenderer, scene = clock?.Scene,
                speakerStates = clock?.SpeakerStates?.Select(s => new { code = s.Code, muted = s.Muted, solo = s.Solo, enabled = s.Enabled }).ToArray(),
                bandCount = settings.BandCount, centers = settings.Centers, dispersion = settings.Centers.Select(DispersionAngleMapper.Map).ToArray(),
                input = values.Take(width).ToArray(), post = values.Skip(width).Take(width).ToArray(), output = values.Skip(width * 2).Take(settings.BandCount * 2).ToArray(),
                fastInput = fastValues.Take(width).ToArray(), fastPost = fastValues.Skip(width).Take(width).ToArray(),
                fastOutput = fastValues.Skip(width * 2).Take(settings.BandCount * 2).ToArray(),
                lookahead = new { revision = forecastRevision, signal, channels = signal == "output" ? new[] { "FL", "FR" } : forecastChannels,
                    times = ahead.Select(f => f.Time).ToArray(), values = ahead.SelectMany(f => f.Values.Skip(start).Take(length)).ToArray() },
                error = lastError
            };
        }
    }
    public void Dispose() { stop.Cancel(); }
}
