using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class AtmosReadFailureTests
{
    public static async Task Run(string transport, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", output);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        byte[] media = File.ReadAllBytes(Path.GetFullPath(transport));
        var baseline = await Interrupted(false); var strict = await Interrupted(true);
        Check(baseline.Exit == 0 && baseline.Errors.Contains("premature", StringComparison.OrdinalIgnoreCase),
            "A real truncated HTTP source reproduces FFmpeg reporting a read failure while exiting successfully by default");
        Check(strict.Exit != 0 && strict.Errors.Length > 0,
            "Strict source reading returns failure for the same interrupted source instead of accepting partial audio");
        LanguageService.Instance.SetJapanese(false);
        var en = new AtmosSourceReadException("00000", strict.Errors);
        Check(en.Message.Contains("source could not be read completely") && en.Message.Contains("Clip: 00000"),
            "English read-failure feedback identifies the source/mount problem and affected clip");
        LanguageService.Instance.SetJapanese(true);
        var ja = new AtmosSourceReadException("00000", strict.Errors);
        Check(ja.Message.Contains("音声データを最後まで読み取れませんでした") && ja.Message.Contains("マウント状態"),
            "Japanese read-failure feedback explains source recovery");
        SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = checks.Count, checks, baseline.Exit, StrictExit = strict.Exit });

        async Task<(int Exit, string Errors)> Interrupted(bool strictRead)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var wire = client.GetStream(); var request = new List<byte>(); var one = new byte[1];
                while (request.Count < 8192)
                {
                    if (await wire.ReadAsync(one, timeout.Token) == 0) throw new IOException("Missing loopback request.");
                    request.Add(one[0]);
                    if (request.Count >= 4 && request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                }
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/MP2T\r\nContent-Length: {media.Length + 65536}\r\nConnection: close\r\n\r\n");
                await wire.WriteAsync(header, timeout.Token); await wire.WriteAsync(media, timeout.Token); await wire.FlushAsync(timeout.Token);
                // End before the declared length, modeling a source disappearing mid-read.
            }, timeout.Token);
            var info = new ProcessStartInfo(new ToolPaths().Ffmpeg)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning" };
            if (strictRead) args.Add("-xerror");
            args.AddRange(["-probesize", "32768", "-analyzeduration", "100000", "-seekable", "0", "-i", $"http://127.0.0.1:{port}/audio.m2ts",
                "-map", "0:a:0", "-c:a", "copy", "-f", "null", "NUL"]);
            foreach (string arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Cannot run the native read-failure test.");
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync(timeout.Token); var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await serve; await stdout;
            string text = await errors; File.WriteAllText(Path.Combine(output, strictRead ? "strict.stderr.txt" : "default.stderr.txt"), text);
            return (process.ExitCode, text);
        }
    }
}
