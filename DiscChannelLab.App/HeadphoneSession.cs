using System.ComponentModel;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;

namespace Disc2Flac;

public enum HeadphoneMethod { Direct, SteamAudio }
public sealed record HeadphoneOptions
{
    public bool Enabled { get; init; }
    public HeadphoneMethod Method { get; init; }
    public double ShadowStrength { get; init; } = .35;
    public double RearCutDb { get; init; } = 6;
    public double RearFrequency { get; init; } = 4000;
    public double HeadRadius { get; init; } = .0875;
    public bool InterauralDelay { get; init; }
    public double HeadroomDb { get; init; } = -12;
    public double DirectTrimDb { get; init; }
    public double SteamTrimDb { get; init; }
    public double SmoothingMs { get; init; } = 8;
    public double LfeGain { get; init; } = .7;
    public double FrontAngle { get; init; } = 30;
    public int UdpPort { get; init; } = 4242;
    public bool InvertYaw { get; init; }
    public bool InvertPitch { get; init; }
    public bool InvertRoll { get; init; }
    public bool UseTracker { get; init; }

    public HeadphoneOptions Validated() => this with
    {
        Method = Enum.IsDefined(Method) ? Method : HeadphoneMethod.Direct,
        ShadowStrength = Limit(ShadowStrength, 0, 1, .35), RearCutDb = Limit(RearCutDb, 0, 18, 6),
        RearFrequency = Limit(RearFrequency, 500, 12000, 4000), HeadRadius = Limit(HeadRadius, .06, .12, .0875),
        HeadroomDb = Limit(HeadroomDb, -30, 0, -12), DirectTrimDb = Limit(DirectTrimDb, -12, 12, 0), SteamTrimDb = Limit(SteamTrimDb, -12, 12, 0),
        SmoothingMs = Limit(SmoothingMs, 0, 100, 8), LfeGain = Limit(LfeGain, 0, 1, .7),
        FrontAngle = Limit(FrontAngle, 15, 90, 30), UdpPort = Math.Clamp(UdpPort, 1024, 65535)
    };
    private static double Limit(double value, double low, double high, double fallback) => double.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
}

public sealed record HeadPose(double Yaw, double Pitch, double Roll)
{
    public Quaternion Rotation => Quaternion.CreateFromYawPitchRoll((float)(Yaw * Math.PI / 180), (float)(-Pitch * Math.PI / 180), (float)(-Roll * Math.PI / 180));
}

/// <summary>Processed opentrack output: x,y,z (ignored), yaw,pitch,roll in degrees; six little-endian doubles.</summary>
public sealed class HeadphoneSession : INotifyPropertyChanged, IDisposable
{
    public static HeadphoneSession Current { get; } = new();
    private readonly string _path = AppDataPaths.Current.PreferencePath("headphones.json");
    private HeadphoneOptions _options = new();
    private HeadPose _pose = new(0, 0, 0), _raw = new(0, 0, 0), _center = new(0, 0, 0);
    private CancellationTokenSource? _stop;
    private UdpClient? _udp;
    private long _lastPacket;
    private string? _error;
    private string _audioStatus = HeadphoneText.T("Output opens when playback starts.");
    public string AudioStatus => Volatile.Read(ref _audioStatus);
    internal void ReportOutput(double bufferMs, long clips)
    {
        Volatile.Write(ref _audioStatus, $"48 kHz float → stereo · device buffer {bufferMs:0.0} ms · clipped samples {clips:N0}");
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public HeadphoneOptions Options => Volatile.Read(ref _options);
    public HeadPose Pose => Volatile.Read(ref _pose);
    public string? TrackerError => _error;
    public double PacketAgeSeconds => _lastPacket == 0 ? double.PositiveInfinity : (Environment.TickCount64 - Interlocked.Read(ref _lastPacket)) / 1000d;
    public string TrackerStatus => !Options.UseTracker ? HeadphoneText.T("Keyboard") : _error is not null ? _error : PacketAgeSeconds > .5 ? HeadphoneText.T("Waiting / tracking lost — holding pose") : $"opentrack · {PacketAgeSeconds * 1000:0} ms";
    public HeadphoneSession()
    {
        AppDataPaths.Current.ImportHeadphoneSettings();
        // Keep tuning preferences, but always start a new session in ordinary stereo.
        try { if (File.Exists(_path)) _options = (JsonSerializer.Deserialize<HeadphoneOptions>(File.ReadAllText(_path)) ?? new()).Validated() with { Enabled = false }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _error = ex.Message; }
    }
    public void SetOptions(HeadphoneOptions options)
    {
        var previous = Options;
        Volatile.Write(ref _options, options.Validated());
        if (previous.Enabled != Options.Enabled || previous.UseTracker != Options.UseTracker || previous.UdpPort != Options.UdpPort) RestartTracker();
        PropertyChanged?.Invoke(this, new(nameof(Options)));
    }
    public void SetKeyboard(double yaw, double pitch, double roll)
    {
        if (Options.UseTracker) return;
        if (!double.IsFinite(yaw + pitch + roll)) return;
        Volatile.Write(ref _pose, new(Math.IEEERemainder(yaw, 360), Math.Clamp(pitch, -89, 89), Math.IEEERemainder(roll, 360)));
    }
    public void Recenter()
    {
        _center = _raw;
        Volatile.Write(ref _pose, new(0, 0, 0));
    }
    public bool ReceivePacket(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 48) return false;
        Span<double> values = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            values[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * 8)..]));
            if (!double.IsFinite(values[i]) || Math.Abs(values[i]) > 100000) return false;
        }
        var settings = Options;
        _raw = new(values[3] * (settings.InvertYaw ? -1 : 1), values[4] * (settings.InvertPitch ? -1 : 1), values[5] * (settings.InvertRoll ? -1 : 1));
        Interlocked.Exchange(ref _lastPacket, Environment.TickCount64);
        if (settings.UseTracker) Volatile.Write(ref _pose, new(Math.IEEERemainder(_raw.Yaw - _center.Yaw, 360), Math.Clamp(_raw.Pitch - _center.Pitch, -89, 89), Math.IEEERemainder(_raw.Roll - _center.Roll, 360)));
        return true;
    }
    public void RestartTracker()
    {
        _stop?.Cancel(); _udp?.Dispose(); _stop?.Dispose(); _udp = null; _stop = null; _error = null;
        if (!Options.Enabled || !Options.UseTracker) return;
        try
        {
            // Only same-machine senders are accepted. Never listen on LAN interfaces.
            var udp = _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Options.UdpPort));
            var stop = _stop = new CancellationTokenSource(); var token = stop.Token;
            _ = Task.Run(async () =>
            {
                try { while (!token.IsCancellationRequested) ReceivePacket((await udp.ReceiveAsync(token)).Buffer); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (SocketException ex) { if (!token.IsCancellationRequested) _error = ex.Message; }
            });
        }
        catch (SocketException ex) { _error = ex.Message; }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Options with { Enabled = false }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error = ex.Message; }
    }
    public void Dispose() { Save(); _stop?.Cancel(); _udp?.Dispose(); _stop?.Dispose(); _stop = null; _udp = null; }
}

public static class HeadphoneGeometry
{
    public static string[] Channels(AudioStreamInfo stream) => stream.Channels switch
    {
        1 when stream.ChannelLayout is "mono" or "FC" => ["FC"],
        2 when stream.ChannelLayout == "stereo" => ["FL", "FR"],
        _ => StereoMixSettings.ChannelNames(stream).ToArray()
    };
    public static Vector3 Speaker(string code, IReadOnlyCollection<string>? channels = null, double frontAngle = 30)
    {
        // BS.775: a single surround pair is at 100..120 degrees, regardless of
        // the source's BL/BR vs SL/SR slot names. Separate back + side pairs use
        // 150 and 110 degrees respectively (within BS.2051 System I ranges).
        var backAngle = channels is not null && !channels.Contains("SL") && !channels.Contains("SR") ? 110d : 150d;
        var (azimuth, elevation) = code switch
        {
            "FL" => (-frontAngle, 0d), "FR" => (frontAngle, 0), "FC" or "LFE" => (0, 0),
            "SL" => (-110, 0), "SR" => (110, 0), "BL" => (-backAngle, 0), "BR" => (backAngle, 0),
            "BC" => (180, 0), "FLC" => (-15, 0), "FRC" => (15, 0), "TFL" => (-30, 45), "TFR" => (30, 45),
            _ => throw new InvalidDataException("Unknown speaker: " + code)
        };
        var a = azimuth * Math.PI / 180; var e = elevation * Math.PI / 180;
        return new((float)(Math.Sin(a) * Math.Cos(e)), (float)Math.Sin(e), (float)(Math.Cos(a) * Math.Cos(e)));
    }
    public static Vector3 Relative(string code, Quaternion orientation, IReadOnlyCollection<string>? channels = null, double frontAngle = 30) =>
        Vector3.Normalize(Vector3.Transform(Speaker(code, channels, frontAngle), Quaternion.Conjugate(orientation)));
    public static Vector3 DisplaySpeaker(string code, HeadPose pose, HeadphoneOptions options, IReadOnlyCollection<string> channels) =>
        options.UseTracker ? Speaker(code, channels, options.FrontAngle) : Relative(code, pose.Rotation, channels, options.FrontAngle);
    public static HeadphoneScene Scene(IReadOnlyCollection<string> channels, HeadphoneOptions options) => new(!options.UseTracker, options.FrontAngle,
        channels.Select(code =>
        {
            var p = Speaker(code, channels, options.FrontAngle);
            return new HeadphoneSpeaker(code, p.X, p.Y, p.Z);
        }).ToArray());
    public static (double Left, double Right) Pan(Vector3 direction) => (Math.Sqrt(Math.Clamp((1 - direction.X) / 2, 0, 1)), Math.Sqrt(Math.Clamp((1 + direction.X) / 2, 0, 1)));
    public static double WoodworthDelay(Vector3 direction, double radius)
    {
        var azimuth = Math.Atan2(direction.X, direction.Z); var angle = Math.Abs(azimuth);
        if (angle > Math.PI / 2) angle = Math.PI - angle;
        return Math.CopySign(radius / 343 * (angle + Math.Sin(angle)), azimuth);
    }
}
