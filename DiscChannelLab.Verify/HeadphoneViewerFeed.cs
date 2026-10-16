using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Buffers.Binary;
using System.Text.Json;

internal static class HeadphoneViewerFeed
{
    public static async Task RunAsync(string viewer, string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        using var portReservation = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var fixturePort = ((System.Net.IPEndPoint)portReservation.Client.LocalEndPoint!).Port; portReservation.Close();
        var session = HeadphoneSession.Current; session.SetOptions(new HeadphoneOptions { Enabled = true, FrontAngle = 60, UdpPort = fixturePort }); session.SetKeyboard(35, 15, 8);
        using var bridge = new VisualizerBridge(); bridge.Start();
        var start = new ProcessStartInfo(Path.GetFullPath(viewer)) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--live-pipe"); start.ArgumentList.Add(bridge.PipeName); start.ArgumentList.Add("--headless");
        using var process = Process.Start(start) ?? throw new IOException("Viewer launch failed.");
        var channels = new[] { "FL", "FR", "FC", "LFE", "SL", "SR" };
        var timer = Stopwatch.StartNew(); int sample = 0; string lastControl = "";
        try
        {
            while (timer.Elapsed.TotalSeconds < 80 && !File.Exists(Path.Combine(root, "done")))
            {
                if (process.HasExited) throw new IOException($"Viewer exited ({process.ExitCode}).");
                var controlFile = Path.Combine(root, "control.json");
                if (File.Exists(controlFile))
                {
                    try
                    {
                        var contents = File.ReadAllText(controlFile);
                        if (contents != lastControl)
                        {
                            var control = JsonSerializer.Deserialize<Control>(contents, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                            session.SetOptions(session.Options with { Enabled = control.Enabled, FrontAngle = control.FrontAngle, UseTracker = control.UseTracker });
                            if (control.UseTracker)
                            {
                                var packet = new byte[48]; BitConverter.GetBytes(control.Yaw).CopyTo(packet, 24); BitConverter.GetBytes(control.Pitch).CopyTo(packet, 32); BitConverter.GetBytes(control.Roll).CopyTo(packet, 40);
                                session.ReceivePacket(packet);
                            }
                            else session.SetKeyboard(control.Yaw, control.Pitch, control.Roll);
                            lastControl = contents;
                        }
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
                bridge.PublishClock(1, "Synthetic 5.1 head-pose check", "PCM fixture", 48000, sample / 48000d, 1000, true, channels);
                var input = new byte[960 * channels.Length * 2]; var post = new float[960 * channels.Length]; var output = new byte[960 * 4];
                for (int frame = 0; frame < 960; frame++) for (int c = 0; c < channels.Length; c++)
                {
                    var value = .08 * Math.Sin(2 * Math.PI * (c == 3 ? 60 : 220 * (c + 1)) * (sample + frame) / 48000);
                    BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan((frame * channels.Length + c) * 2), (short)(value * 32768));
                    post[frame * channels.Length + c] = (float)value;
                }
                bridge.PublishPcm(1, new(sample / 48000d, channels, input, post, output)); sample += 960;
                await Task.Delay(20);
            }
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
    private sealed record Control(double FrontAngle, bool UseTracker, double Yaw, double Pitch, double Roll, bool Enabled = true);
}
