using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Disc2Flac;
using DiscChannelLab.Visualizer;

if (args.Length == 2 && args[0] == "--write-demo")
{
    string path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new DemoAudioSource(new VisualizationSettings()).Demo());
    Console.WriteLine("Synthetic demo packet written.");
    return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
var settings = new VisualizationSettings();
var stream = new AudioStreamInfo { Index = 0, Codec = "pcm_s16le", Channels = 6, ChannelLayout = "5.1(side)", SampleRate = 48000, BitDepth = 16 };
var codes = StereoMixSettings.ChannelNames(stream).ToArray();
var channels = codes.Select((c, i) => new ExperimentalChannel(c, .6, i % 2 == 0 ? -1 : 1, false)).ToArray();
channels[2] = channels[2] with { Muted = true };
var state = new PreviewMixState(StereoMixSettings.Default, null, channels);
var pcm = new byte[48000 / 2 * 6 * 2];
for (int i = 0; i < pcm.Length / 12; i++) for (int c = 0; c < 6; c++)
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * 6 + c) * 2, 2), (short)(6000 * Math.Sin(2 * Math.PI * (c == 3 ? 60 : 220 * (c + 1)) * i / 48000)));
async Task<(byte[] Output, List<PreviewPcmChunk> Frames)> Mix(bool observe, bool ahead = false)
{
    using var input = new MemoryStream(pcm); using var output = new MemoryStream();
    var frames = new List<PreviewPcmChunk>();
    await StereoPreviewMixer.CopyAsync(input, output, stream, () => state, () => .8, CancellationToken.None,
        visualization: observe ? new PreviewPcmTap(() => true, f => { if (f.ForecastRevision == 0) frames.Add(f); }, ahead) : null);
    return (output.ToArray(), frames);
}
var baseline = await Mix(false);
var tapped = await Mix(true);
Check(baseline.Output.SequenceEqual(tapped.Output), "Observer leaves playback PCM byte-identical");
var prefetched = await Mix(true, true);
Check(baseline.Output.SequenceEqual(prefetched.Output), "Read-ahead and future mixing leave actual mixed PCM byte-identical");
Check(tapped.Frames.Sum(f => f.Output.Length) == baseline.Output.Length, "Every submitted sample is accounted for");
Check(tapped.Frames.All(f => f.Channels.SequenceEqual(codes)), "Actual channel order is carried by the protocol");
Check(tapped.Frames.All(f => Enumerable.Range(0, f.Post.Length / 6).All(i => f.Post[i * 6 + 2] == 0)), "Muted Center is zero in Post");
Check(tapped.Frames.Any(f => Enumerable.Range(0, f.Input.Length / 12).Any(i => BinaryPrimitives.ReadInt16LittleEndian(f.Input.AsSpan(i * 12 + 4, 2)) != 0)), "Muted channel retains original Input");
var stereo = new byte[48000 / 10 * 4];
for (int i = 0; i < stereo.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(i * 2, 2), 10000);
var stereoFrames = new List<PreviewPcmChunk>();
using (var destination = new MemoryStream())
{
    await AudioNavigationService.CopyPcmWithVolumeAsync(new MemoryStream(stereo), destination, () => .5, CancellationToken.None,
        visualization: new PreviewPcmTap(() => true, stereoFrames.Add));
    Check(stereoFrames.All(f => f.Channels.SequenceEqual(new[] { "FL", "FR" }) && f.Post.All(x => x == 5000 / 32768f)), "Stereo fallback captures post-volume output");
}
// Exercise the real receiver parser and STFT with actual mixer packets.
using (var receiver = new LiveReceiver(null, settings, Console.WriteLine))
{
    receiver.Consume(VisualizerBridge.Encode(new VisualizerClock(1, 7, "Synthetic 5.1", "PCM", 48000, .5, 1, false, Environment.TickCount64)));
    foreach (var frame in tapped.Frames) receiver.Consume(VisualizerBridge.Encode((7, frame)));
    using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(receiver.Snapshot()));
    var data = snapshot.RootElement;
    Check(data.GetProperty("hasData").GetBoolean(), "Timestamped live STFT frame available");
    var input = data.GetProperty("input").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    var post = data.GetProperty("post").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    Check(input.Skip(32).Take(16).Max() > .1f && post.Skip(32).Take(16).All(x => x == 0), "Input / Post spectra reflect Mute separately");
    Check(data.GetProperty("output").GetArrayLength() == 32, "Final stereo spectrum has exactly two channels");
    var fastInput = data.GetProperty("fastInput").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    var fastPost = data.GetProperty("fastPost").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    Check(fastInput.Length == input.Length && fastPost.Length == post.Length && data.GetProperty("fastOutput").GetArrayLength() == 32,
        "Fast brightness frames preserve Input/Post/Output channel dimensions");
    Check(fastInput.Skip(32).Take(16).Max() > .1f && fastPost.Skip(32).Take(16).All(x => x == 0),
        "Fast brightness follows selected signal and keeps muted Post dark");
    receiver.Consume(VisualizerBridge.Encode(new VisualizerClock(1, 8, "Seek", "PCM", 48000, 12, 30, false, Environment.TickCount64)));
    receiver.Consume(VisualizerBridge.Encode((7, tapped.Frames[0])));
    using var afterSeek = JsonDocument.Parse(JsonSerializer.Serialize(receiver.Snapshot()));
    Check(!afterSeek.RootElement.GetProperty("hasData").GetBoolean(), "Seek rejects stale PCM from previous epoch");
    bool rejected = false;
    try { receiver.Consume([2, 0]); } catch (IOException) { rejected = true; }
    Check(rejected, "Truncated PCM packet rejected");
}
// Test a real OS named pipe, disconnect, and a new viewer connecting again.
using (var bridge = new VisualizerBridge())
{
    bridge.Start();
    using (var receiver = new LiveReceiver(bridge.PipeName, settings, Console.WriteLine))
    {
        receiver.Start();
        for (int i = 0; i < 100 && !bridge.IsConnected; i++) await Task.Delay(20);
        Check(bridge.IsConnected, "Same-user named pipe connects");
        bridge.PublishClock(9, "Live link", "PCM", 48000, .4, 1, false);
        foreach (var frame in tapped.Frames)
        {
            bridge.PublishPcm(9, frame);
            await Task.Delay(10);
        }
        await Task.Delay(40);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(receiver.Snapshot()));
        Check(snapshot.RootElement.GetProperty("hasData").GetBoolean(), "PCM travels through real IPC and is analyzed");
    }
    // Writing discovers a closed pipe without involving the playback path.
    for (int i = 0; i < 100 && bridge.IsConnected; i++) { bridge.PublishClock(9, "Closed", "PCM", 48000, 0, 1, false); await Task.Delay(20); }
    Check(!bridge.IsConnected, "Viewer disconnect is contained");
    using var reconnect = new LiveReceiver(bridge.PipeName, settings, Console.WriteLine);
    reconnect.Start();
    for (int i = 0; i < 100 && !bridge.IsConnected; i++) await Task.Delay(20);
    Check(bridge.IsConnected, "A new viewer reconnects");
}
Console.WriteLine($"{passed} link checks passed.");

// A viewer may join during playback. Decode ahead without changing the actual PCM or mixer latency.
var futurePackets = new System.Collections.Concurrent.ConcurrentQueue<PreviewPcmChunk>();
bool forecastEnabled = false;
var futureState = new PreviewMixState(StereoMixSettings.Default, null, codes.Select(c => new ExperimentalChannel(c, .6, 0, false)).ToArray());
var longPcm = new byte[48000 * 10 * 12];
for (int n = 0; n < longPcm.Length; n += pcm.Length) pcm.CopyTo(longPcm, n);
using (var decoded = new MemoryStream(longPcm))
await using (var ahead = new VisualizerLookaheadStream(decoded, codes, () => Volatile.Read(ref futureState), () => .8,
    new PreviewPcmTap(() => Volatile.Read(ref forecastEnabled), futurePackets.Enqueue, true), CancellationToken.None))
{
    var first = new byte[1024 * 12];
    int read = await ahead.ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(read == first.Length && first.SequenceEqual(longPcm.Take(read)), "First PCM is available without waiting for look-ahead");
    await Task.Delay(70);
    Check(decoded.Position < 48000 * 12 / 5, "No viewer: prefetch stays below 0.2 seconds");
    Volatile.Write(ref forecastEnabled, true);
    for (int n = 0; n < 300 && !futurePackets.Any(f => f.Seconds > 6); n++) await Task.Delay(20);
    var originalFuture = futurePackets.ToArray();
    Check(originalFuture.Any(f => f.Seconds > 6) && originalFuture.Max(f => f.Seconds) < 8.25,
        "Late viewer receives bounded multi-second look-ahead");
    var mutedState = futureState with { Channels = futureState.Channels!.Select(c => c.Code == "FC" ? c with { Muted = true } : c).ToArray() };
    Volatile.Write(ref futureState, mutedState);
    int oldRevision = originalFuture[0].ForecastRevision;
    for (int n = 0; n < 300 && !futurePackets.Any(f => f.ForecastRevision > oldRevision && f.Seconds > 6); n++) await Task.Delay(20);
    var changedFuture = futurePackets.Where(f => f.ForecastRevision > oldRevision).ToArray();
    Check(changedFuture.Length > 100 && changedFuture.All(f => Enumerable.Range(0, f.Post.Length / 6).All(n => f.Post[n * 6 + 2] == 0)),
        "Mixer changes regenerate future Post without touching playback PCM");
    using var forecastReceiver = new LiveReceiver(null, settings, Console.WriteLine);
    forecastReceiver.Consume(VisualizerBridge.Encode(new VisualizerClock(1, 25, "Future", "PCM", 48000, 0, 10, false, Environment.TickCount64)));
    foreach (var packet in changedFuture) forecastReceiver.Consume(VisualizerBridge.Encode((25, packet)));
    using var forecastSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(forecastReceiver.Snapshot()));
    var forecastData = forecastSnapshot.RootElement.GetProperty("lookahead");
    Check(forecastData.GetProperty("times").EnumerateArray().Last().GetDouble() > 6, "Protocol type 3 preserves future timestamps");
    Check(!forecastSnapshot.RootElement.GetProperty("hasData").GetBoolean(), "Future PCM never replaces the currently audible spectrum");
    var futureValues = forecastData.GetProperty("values").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    Check(Enumerable.Range(0, futureValues.Length / 96).All(n => futureValues.Skip(n * 96 + 32).Take(16).All(x => x == 0)), "Future spectrum honors Center mute");
    forecastReceiver.Consume(VisualizerBridge.Encode(new VisualizerClock(1, 26, "Seek", "PCM", 48000, 4, 10, false, Environment.TickCount64)));
    forecastReceiver.Consume(VisualizerBridge.Encode((25, changedFuture[^1])));
    using var cleared = JsonDocument.Parse(JsonSerializer.Serialize(forecastReceiver.Snapshot()));
    Check(cleared.RootElement.GetProperty("lookahead").GetProperty("times").GetArrayLength() == 0, "Seek rejects old future packets");
    using (var forecastBridge = new VisualizerBridge())
    using (var pipeReceiver = new LiveReceiver(forecastBridge.PipeName, settings, Console.WriteLine))
    {
        forecastBridge.Start(); pipeReceiver.Start();
        for (int n = 0; n < 100 && !forecastBridge.IsConnected; n++) await Task.Delay(20);
        Check(forecastBridge.IsConnected, "Forecast pipe connects");
        forecastBridge.PublishClock(27, "Ahead over pipe", "PCM", 48000, 0, 10, false);
        foreach (var packet in changedFuture) forecastBridge.PublishPcm(27, packet);
        bool receivedFuture = false;
        for (int n = 0; n < 300 && !receivedFuture; n++)
        {
            forecastBridge.PublishClock(27, "Ahead over pipe", "PCM", 48000, 0, 10, false);
            await Task.Delay(20);
            using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(pipeReceiver.Snapshot()));
            receivedFuture = snapshot.RootElement.GetProperty("lookahead").GetProperty("times").EnumerateArray().Any(t => t.GetDouble() > 6);
        }
        Check(receivedFuture, "A burst of multi-second forecasts survives the real named pipe");
    }
    using var preserved = new MemoryStream(); preserved.Write(first);
    var buffer = new byte[17003];
    while ((read = await ahead.ReadAsync(buffer)) > 0) preserved.Write(buffer, 0, read);
    Check(preserved.ToArray().SequenceEqual(longPcm), "Read-ahead preserves every PCM byte across arbitrary read boundaries");
    Check(await ahead.ReadAsync(buffer) == 0, "Repeated end-of-stream reads complete immediately");
}
Console.WriteLine($"{passed} link and look-ahead checks passed.");

if (args.Length == 2 && args[0] == "--host")
{
    using var bridge = new VisualizerBridge(); bridge.Start();
    var executable = Path.GetFullPath(args[1]);
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--headless"); start.ArgumentList.Add("--live-pipe"); start.ArgumentList.Add(bridge.PipeName);
    using var process = Process.Start(start) ?? throw new IOException("Viewer host did not start.");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        string endpointPath = Path.Combine(Path.GetDirectoryName(executable)!, "cache", "endpoint.json");
        bool ready = false;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(50);
            try
            {
                using var endpoint = JsonDocument.Parse(File.ReadAllText(endpointPath));
                if (endpoint.RootElement.GetProperty("pid").GetInt32() != process.Id) continue;
                client.BaseAddress = new Uri(endpoint.RootElement.GetProperty("address").GetString()!);
                client.DefaultRequestHeaders.Add("X-Visualizer-Token", endpoint.RootElement.GetProperty("token").GetString());
                ready = true; break;
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        Check(ready, "Published viewer host starts independently");
        for (int i = 0; i < 100 && !bridge.IsConnected; i++) await Task.Delay(20);
        Check(bridge.IsConnected, "Separate viewer process connects to the player's pipe");
        var script = await client.GetStringAsync("/app.js");
        Check(script.Contains("uniform mediump float uPoint") && script.Contains("function enterLive"), "Published assets include the shader fix and live controls");
        bridge.PublishClock(21, "Published host integration", "PCM", 48000, .25, 1, false);
        foreach (var frame in tapped.Frames) { bridge.PublishPcm(21, frame); await Task.Delay(10); }
        using var response = await client.PostAsync("/api/live", null);
        response.EnsureSuccessStatusCode();
        using var snapshot = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = snapshot.RootElement;
        Check(data.GetProperty("configured").GetBoolean() && data.GetProperty("connected").GetBoolean() && data.GetProperty("hasData").GetBoolean(), "Published HTTP API exposes live analysis from actual IPC");
        Check(data.GetProperty("channels").GetArrayLength() == 6 && data.GetProperty("output").GetArrayLength() == 32, "Browser receives original channels and final stereo spectrum");
        using var end = await client.PostAsync("/api/quit", null);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(process.ExitCode == 0, "Published host closes cleanly");
    }
    finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    Console.WriteLine($"{passed} total link checks passed.");
}
