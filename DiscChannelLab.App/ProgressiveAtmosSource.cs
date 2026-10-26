using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Disc2Flac;

public enum AtmosPresentation { Stereo = 0, Surround51 = 1, Objects = 3 }

/// <summary>
/// One stateful external decoder, started at the title's beginning. CAF PCM and
/// DAMF events are read while being written. Seeking never invents object state.
/// The raw-PCM disk cache grows only to the requested position plus read-ahead.
/// </summary>
public sealed class ProgressiveAtmosSource : IAsyncDisposable
{
    public const int SafetyFrames = 4800, StartupFrames = 24000, ReadAheadFrames = 4 * 48000;
    readonly ToolPaths tools;
    readonly FfprobeService probe;
    readonly AppLog log;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim metadataGate = new(1, 1);
    readonly string folder, basename;
    readonly SpatialScene stereo, surround;
    readonly System.Collections.Concurrent.ConcurrentDictionary<AtmosPresentation, CafHeader> headers = new();
    Task decoding = Task.CompletedTask;
    ExceptionDispatchInfo? failure;
    SpatialScene? scene;
    long demand, sceneHorizon;
    int ended, disposed;
    public DiscAnalysis Disc { get; }
    public PlaylistInfo Playlist { get; }
    public AudioStreamInfo Stream { get; }
    public long TotalFrames { get; }
    public AdmWave Wave { get; private set; } = null!;
    public SpatialScene Scene => Volatile.Read(ref scene) ?? throw new InvalidOperationException("Object metadata is not ready.");
    public bool Complete => Volatile.Read(ref ended) != 0 && failure is null;
    public long PreparedFrames => Available(AtmosPresentation.Objects);
    public long RequestedFrames => Volatile.Read(ref demand);
    public string CacheFolder => folder;
    public bool IsFor(DiscAnalysis disc, PlaylistInfo playlist) => ReferenceEquals(Disc, disc) && ReferenceEquals(Playlist, playlist);
    ProgressiveAtmosSource(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream, ToolPaths paths, FfprobeService audioProbe, AppLog logger)
    {
        Disc = disc; Playlist = playlist; Stream = stream; tools = paths; probe = audioProbe; log = logger;
        TotalFrames = ConversionService.ToSample(playlist.DurationTicks, 48000);
        folder = Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "atmos-live", Guid.NewGuid().ToString("N"));
        basename = Path.Combine(folder, "live");
        stereo = SpatialScene.ChannelBed(["FL", "FR"], TotalFrames) with { Sources =
            [new("FL", "Left", 0, SpatialSourceKind.ChannelBed, 0, TotalFrames, [new(0, TotalFrames, new(-1, 0, 0))]),
             new("FR", "Right", 1, SpatialSourceKind.ChannelBed, 0, TotalFrames, [new(0, TotalFrames, new(1, 0, 0))])] };
        surround = SpatialScene.ChannelBed(["FL", "FR", "FC", "LFE", "SL", "SR"], TotalFrames);
    }
    public static async Task<ProgressiveAtmosSource> StartAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream,
        ToolPaths tools, FfprobeService probe, AppLog log, AdmAlbumMetadata album, CancellationToken token)
    {
        if (!AtmosImportService.IsAtmos(stream)) throw new NotSupportedException("Position-aware preview requires TrueHD Atmos.");
        var source = new ProgressiveAtmosSource(disc, playlist, stream, tools, probe, log);
        try
        {
            Directory.CreateDirectory(source.folder);
            string tool = AtmosImportService.FindTool() ?? throw new FileNotFoundException("Choose truehdd.exe (0.6.2) for object audio.");
            string version = await SpatialRendering.RunAsync(tool, ["--version"], token);
            if (!version.Contains("truehdd 0.6.2", StringComparison.Ordinal)) throw new NotSupportedException("This adapter requires truehdd 0.6.2.");
            source.decoding = Task.Run(() => source.DecodeAsync(tool));
            await source.EnsureAsync(0, Math.Min(source.TotalFrames, StartupFrames), AtmosPresentation.Objects, token);
            source.Wave = new(disc.Root, source.Scene.Sources.Length, 24, false, 0,
                checked(source.TotalFrames * source.Scene.Sources.Length * 3), source.Scene,
                [new("live", album.Album)], "live") { Album = album, Progressive = source };
            return source;
        }
        catch { await source.DisposeAsync(); throw; }
    }
    public SpatialScene SceneFor(AtmosPresentation presentation) => presentation switch
    { AtmosPresentation.Stereo => stereo, AtmosPresentation.Surround51 => surround, _ => Scene };
    public int Channels(AtmosPresentation presentation) => presentation switch { AtmosPresentation.Stereo => 2, AtmosPresentation.Surround51 => 6, _ => Scene.Sources.Length };
    public static HeadphoneOptions Options(HeadphoneOptions value, AtmosPresentation presentation) => presentation != AtmosPresentation.Stereo
        ? AdmFileFormat.RenderOptions(value) : value with { Enabled = true, Method = HeadphoneMethod.Direct,
            Hrtf = HeadphoneHrtf.BuiltIn, SofaPath = "", ShadowStrength = 0, RearCutDb = 0, InterauralDelay = false,
            HeadroomDb = 0, DirectTrimDb = 0, SteamTrimDb = 0, SofaTrimDb = 0, Room = new() };
    public SpatialInput Input(long first, long frames, AtmosPresentation presentation)
    {
        if (first < 0 || frames <= 0 || first > TotalFrames - frames) throw new ArgumentOutOfRangeException(nameof(first));
        return new(AudioPath(presentation), Channels(presentation), SceneFor(presentation), first, frames,
            Disc.Root + " · " + presentation, "progressive; hash collected by offline renderer", first)
            { Wave = Wave, Presentation = presentation };
    }
    string AudioPath(AtmosPresentation p) => basename + "_p" + (int)p + (p == AtmosPresentation.Objects ? ".atmos.audio" : ".caf");
    string Damf => basename + "_p3.atmos";
    public void HoldAt(long sample) => Volatile.Write(ref demand, Math.Clamp(sample, 0, TotalFrames));
    public long SafeFrames(AtmosPresentation p)
    {
        long available = Available(p);
        return Complete ? available : Math.Max(0, available - SafetyFrames);
    }
    long Available(AtmosPresentation p)
    {
        try { if (!headers.TryGetValue(p, out var h)) { h = Header(AudioPath(p)); headers.TryAdd(p, h); } return Math.Min(TotalFrames, Math.Max(0, (new FileInfo(AudioPath(p)).Length - h.Offset) / (h.Channels * 3))); }
        catch (IOException) { return 0; }
    }
    public async Task EnsureAsync(long first, long last, AtmosPresentation p, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); stop.Token.ThrowIfCancellationRequested();
        if (last > TotalFrames || first < 0 || last <= first) throw new ArgumentOutOfRangeException(nameof(last));
        HoldAt(last);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var timer = Stopwatch.StartNew();
        while (SafeFrames(p) < last)
        {
            linked.Token.ThrowIfCancellationRequested(); failure?.Throw();
            if (Volatile.Read(ref ended) != 0) throw new EndOfStreamException("The selected presentation ended before the requested position.");
            if (timer.Elapsed.TotalMinutes > 10) throw new TimeoutException("Object-audio preparation timed out; stop and select the original channel audio.");
            await Task.Delay(20, linked.Token).ConfigureAwait(false);
        }
        failure?.Throw();
        if (p != AtmosPresentation.Objects && Volatile.Read(ref scene) is not null) return;
        if (Volatile.Read(ref sceneHorizon) >= last) return;
        await metadataGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (sceneHorizon >= last) return;
            // Read committed PCM first, then metadata. truehdd flushes events before
            // writing the corresponding PCM. Ignore in-progress events beyond this horizon.
            long horizon = SafeFrames(AtmosPresentation.Objects);
            var snapshot = await Task.Run(() => DamfImporter.ReadLiveScene(Damf, TotalFrames, horizon, linked.Token), linked.Token).ConfigureAwait(false);
            var header = Header(AudioPath(AtmosPresentation.Objects));
            if (snapshot.Sources.Length != header.Channels) throw new InvalidDataException("Object IDs and decoded CAF channel count differ.");
            var previous = Volatile.Read(ref scene);
            if (previous is not null && !previous.Sources.Select(s => s.Id).SequenceEqual(snapshot.Sources.Select(s => s.Id)))
                throw new NotSupportedException("The source layout changed. Use a separately prepared ADM for this title.");
            Volatile.Write(ref scene, snapshot); Volatile.Write(ref sceneHorizon, horizon);
        }
        finally { metadataGate.Release(); }
    }
    public Stream Open(SpatialInput input) => new GrowingCafStream(this, input);
    internal readonly record struct CafHeader(long Offset, int Channels, bool Little);
    internal static CafHeader Header(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> b = stackalloc byte[32]; f.ReadExactly(b[..8]);
        if (!b[..8].SequenceEqual("caff\0\u0001\0\0"u8)) throw new InvalidDataException("Invalid live CAF header.");
        int channels = 0; bool little = false;
        for (int i = 0; i < 16; i++)
        {
            f.ReadExactly(b[..12]); long size = BinaryPrimitives.ReadInt64BigEndian(b[4..]);
            if (b[..4].SequenceEqual("data"u8))
            { f.ReadExactly(b[..4]); if (channels == 0 || BinaryPrimitives.ReadInt32BigEndian(b) != 0) throw new InvalidDataException("Invalid live CAF data."); return new(f.Position, channels, little); }
            if (size < 0 || size > 65536) throw new InvalidDataException("Unexpected CAF chunk.");
            if (b[..4].SequenceEqual("desc"u8))
            {
                if (size != 32) throw new InvalidDataException("Invalid live CAF description.");
                f.ReadExactly(b);
                int flags = BinaryPrimitives.ReadInt32BigEndian(b[12..]); channels = BinaryPrimitives.ReadInt32BigEndian(b[24..]);
                if (BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(b)) != 48000 || !b.Slice(8, 4).SequenceEqual("lpcm"u8)
                    || flags is not (0 or 2) || channels is < 1 or > 32 || BinaryPrimitives.ReadInt32BigEndian(b[28..]) != 24
                    || BinaryPrimitives.ReadInt32BigEndian(b[16..]) != channels * 3 || BinaryPrimitives.ReadInt32BigEndian(b[20..]) != 1)
                    throw new NotSupportedException("Live preview requires packed 24-bit 48 kHz CAF.");
                little = flags == 2;
            }
            else f.Seek(size, SeekOrigin.Current);
        }
        throw new InvalidDataException("Missing live CAF audio.");
    }
    async Task DecodeAsync(string tool)
    {
        var token = stop.Token;
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--strict", "decode", "-", "--presentation", "0,1,3", "--output-path", basename, "--json", "--no-estimate-progress" }) info.ArgumentList.Add(arg);
            using var decoder = Process.Start(info) ?? throw new IOException("Cannot start the object-audio decoder.");
            try { decoder.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            using var cancel = token.Register(() => Kill(decoder));
            var errors = decoder.StandardError.ReadToEndAsync(token); var report = decoder.StandardOutput.ReadToEndAsync(token);
            foreach (var clip in Playlist.Clips)
            {
                string path = SacdService.ClipPath(Disc, clip);
                var streams = clip.File?.Streams ?? await probe.ProbeClipAsync(path, token);
                var stream = AudioStreamMatcher.Resolve(Stream, streams);
                if (!AtmosImportService.IsAtmos(stream)) throw new InvalidDataException("The selected clip is not TrueHD Atmos.");
                long skip = clip.InTicks - (stream.StartTimeTicks ?? (clip.File is not null ? 0 : throw new InvalidDataException("Unknown clip start time.")));
                if (skip < 0) throw new InvalidDataException("Invalid clip interval.");
                var args = new List<string> { "-hide_banner", "-nostdin", "-xerror", "-v", "error" };
                if (skip > 0) args.AddRange(["-ss", (skip / 45000m).ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["-i", path, "-map", $"0:{stream.Index}", "-t", ((clip.OutTicks - clip.InTicks) / 45000m).ToString(CultureInfo.InvariantCulture), "-c:a", "copy", "-f", "truehd", "pipe:1"]);
                var extractInfo = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in args) extractInfo.ArgumentList.Add(arg);
                using var extract = Process.Start(extractInfo) ?? throw new IOException("Cannot read the selected TrueHD stream.");
                using var cancelExtract = token.Register(() => Kill(extract));
                var extractErrors = extract.StandardError.ReadToEndAsync(token);
                try
                {
                    byte[] bytes = new byte[16384]; int n;
                    while ((n = await extract.StandardOutput.BaseStream.ReadAsync(bytes, token)) != 0)
                    {
                        while (Volatile.Read(ref demand) + ReadAheadFrames < TotalFrames &&
                            Available(AtmosPresentation.Objects) >= Volatile.Read(ref demand) + ReadAheadFrames)
                            await Task.Delay(25, token);
                        await decoder.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, n), token);
                    }
                    await extract.WaitForExitAsync(token);
                    string extractDiagnostic = await extractErrors;
                    if (!string.IsNullOrWhiteSpace(extractDiagnostic)) log.Write($"LIVE ADM DEMUX {clip.Id}: " + extractDiagnostic);
                    if (extract.ExitCode != 0)
                    {
                        Kill(decoder);
                        await decoder.WaitForExitAsync(CancellationToken.None);
                        throw new AtmosSourceReadException(clip.Id, extractDiagnostic);
                    }
                }
                finally { Kill(extract); }
            }
            decoder.StandardInput.Close(); await decoder.WaitForExitAsync(token);
            string diagnostic = await errors, json = await report; log.Write(diagnostic);
            if (decoder.ExitCode != 0) throw new InvalidDataException("Object-audio decoding stopped. " + diagnostic[^Math.Min(1200, diagnostic.Length)..]);
            using var result = JsonDocument.Parse(json); var value = result.RootElement;
            if (value.GetProperty("skippedFrames").GetInt64() != 0 || value.GetProperty("sampleRate").GetInt32() != 48000 ||
                Math.Abs(value.GetProperty("samples").GetInt64() - TotalFrames) > 480)
                throw new InvalidDataException("Decoded audio and title timeline differ; object playback was stopped.");
            foreach (var p in new[] { AtmosPresentation.Stereo, AtmosPresentation.Surround51, AtmosPresentation.Objects })
                if (Available(p) < TotalFrames) throw new InvalidDataException("A comparison presentation is incomplete.");
            SpatialJson.Save(Path.Combine(folder, "decode-result.json"), value);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref failure, ExceptionDispatchInfo.Capture(ex)); log.Write("LIVE ATMOS: " + ex); }
        finally { Volatile.Write(ref ended, 1); }
    }
    static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); await decoding.ConfigureAwait(false);
        // Only files in this uniquely created generated-cache directory are disposable.
        foreach (var file in Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : []) { try { File.Delete(file); } catch (IOException) { } }
        try { Directory.Delete(folder, false); } catch (IOException) { }
        stop.Dispose();
    }
    sealed class GrowingCafStream : Stream
    {
        readonly ProgressiveAtmosSource source; readonly SpatialInput input; readonly CafHeader header;
        readonly FileStream file; readonly byte[] raw; long position, remaining;
        public GrowingCafStream(ProgressiveAtmosSource owner, SpatialInput data)
        {
            source = owner; input = data; position = data.PcmOffsetFrames; remaining = data.Frames;
            header = Header(owner.AudioPath(data.Presentation));
            if (header.Channels != input.StoredChannels) throw new InvalidDataException("Presentation channel mapping changed.");
            file = new(owner.AudioPath(data.Presentation), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            raw = new byte[256 * header.Channels * 3]; file.Position = header.Offset + position * header.Channels * 3;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer) { var bytes = new byte[buffer.Length]; int n = Read(bytes, 0, bytes.Length); bytes.AsSpan(0, n).CopyTo(buffer); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int n = (int)Math.Min(Math.Min(256, remaining), buffer.Length / (header.Channels * 4)); if (n == 0) return 0;
            await source.EnsureAsync(position, position + n, input.Presentation, token).ConfigureAwait(false);
            await file.ReadExactlyAsync(raw.AsMemory(0, n * header.Channels * 3), token).ConfigureAwait(false);
            for (int i = 0; i < n * header.Channels; i++)
            {
                int at = i * 3, v = header.Little ? raw[at] | raw[at + 1] << 8 | raw[at + 2] << 16 : raw[at + 2] | raw[at + 1] << 8 | raw[at] << 16;
                BinaryPrimitives.WriteSingleLittleEndian(buffer.Span[(i * 4)..], (v << 8 >> 8) / 8388608f);
            }
            position += n; remaining -= n; return n * header.Channels * 4;
        }
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
