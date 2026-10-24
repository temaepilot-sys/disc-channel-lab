using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

internal static class AtmosStreamingTests
{
    public static async Task Run(string output, string raw, string reference)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        var checks = new List<string>(); var measurements = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var token = timeout.Token;
        var tools = new ToolPaths(); var log = new AppLog(Path.Combine(root, "logs")); var probe = new FfprobeService(tools, new ProcessRunner(log));
        var reader = new AudioFileReader(probe, log); var disc = await reader.AnalyzeAsync([Path.GetFullPath(raw)], token); var playlist = disc.Playlists.Single();
        var stream = playlist.FileAnalysis!.Streams.Single(AtmosImportService.IsAtmos);
        var expected = AdmWave.Open(Path.GetFullPath(reference)); var album = new AdmAlbumMetadata("Progressive test", null, raw, [new(0, "Test")]);
        var watch = Stopwatch.StartNew();
        var source = await ProgressiveAtmosSource.StartAsync(disc, playlist, stream, tools, probe, log, album, token);
        try
        {
            double startup = watch.Elapsed.TotalSeconds;
            Check(source.Wave.Channels == 16 && source.TotalFrames == expected.Scene.DurationSamples, "Live mode discovers real object IDs and the original title duration without constructing a full ADM");
            await Task.Delay(250, token); long initialFrames = source.PreparedFrames;
            Check(initialFrames < source.TotalFrames && initialFrames < 12 * 48000, "Stateful decoder is throttled to a short read-ahead instead of writing the whole title");
            async Task<byte[]> Read(SpatialInput input)
            {
                using var pcm = SpatialPcmSource.Open(input); using var bytes = new MemoryStream();
                await pcm.CopyToAsync(bytes, token); return SHA256.HashData(bytes.ToArray());
            }
            async Task Compare(long first, long frames, string name)
            {
                var clock = Stopwatch.StartNew();
                var actual = await Read(source.Input(first, frames, AtmosPresentation.Objects));
                var correct = await Read(SpatialInput.FromAdm(expected, first, frames));
                Check(actual.SequenceEqual(correct), name + ": PCM is sample-exact against full-title decoding");
                double error = 0;
                for (long sample = first; sample < first + frames; sample += 997)
                    for (int c = 0; c < expected.Channels; c++)
                    {
                        var a = source.Scene.Sources[c].At(sample); var b = expected.Scene.Sources[c].At(sample);
                        error = Math.Max(error, Math.Max((a.Position - b.Position).Length(), Math.Abs(a.Gain - b.Gain)));
                    }
                Check(error < 1e-6, name + ": object position, gain and ramp timing match the complete ADM");
                measurements.Add(new { name, seconds = clock.Elapsed.TotalSeconds, preparedSeconds = source.PreparedFrames / 48000d, positionGainError = error });
            }
            await Compare(0, 48000, "Initial preview");
            await Compare(0, 12 * 48000, "Sequential playback across multiple read-ahead updates");
            await Compare(23 * 48000, 48000, "Forward seek into an unprepared range");
            await Compare(4 * 48000 + 123, 48000, "Backward seek to a cached non-block-aligned sample");
            foreach (var p in new[] { AtmosPresentation.Stereo, AtmosPresentation.Surround51 })
            {
                await source.EnsureAsync(2 * 48000, 3 * 48000, p, token);
                var input = source.Input(2 * 48000, 48000, p); using var pcm = SpatialPcmSource.Open(input); using var bytes = new MemoryStream(); await pcm.CopyToAsync(bytes, token);
                Check(bytes.Length == 48000 * source.Channels(p) * 4 && bytes.ToArray().Any(b => b != 0),
                    p + ": decoded presentation has explicit channel mapping and the same requested sample range");
            }
            var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio, SmoothingMs = 0 };
            var settings = new SpatialRenderSnapshot(options, new(0, 0, 0), new(StereoMixSettings.Default, null), 1, HrtfEntry.BuiltIn.Hash);
            var stereoInput = source.Input(48000, 48000, AtmosPresentation.Stereo);
            var stereoPcmHash = await Read(stereoInput);
            var stereoRender = await SpatialRendering.RenderAsync(stereoInput, settings with { Options = ProgressiveAtmosSource.Options(options, AtmosPresentation.Stereo) }, false, token);
            Check(SHA256.HashData(File.ReadAllBytes(stereoRender.PcmPath)).SequenceEqual(stereoPcmHash),
                "Stereo presentation bypasses HRTF, room and headphone headroom with sample-exact two-channel output");
            var liveInput = source.Input(8 * 48000, 2 * 48000, AtmosPresentation.Objects);
            var fullInput = SpatialInput.FromAdm(expected, 8 * 48000, 2 * 48000);
            var renderedLive = await SpatialRendering.RenderAsync(liveInput, settings, true, token);
            var renderedFull = await SpatialRendering.RenderAsync(fullInput, settings, true, token);
            Check(File.ReadAllBytes(renderedLive.PcmPath).SequenceEqual(File.ReadAllBytes(renderedFull.PcmPath)),
                "Progressive HRTF and tail output exactly equal the existing complete-ADM renderer");
            var realtime = new List<byte>(); var transport = new PreviewPlaybackControl(allowPendingPause: true);
            var stats = await SpatialPcmPlayer.PlayAsync(liveInput, settings, transport, token,
                outputFactory: () => new SpatialLabTests.SilentDevice(),
                rawDspCapture: (pcm, frames) => { var data = new byte[frames * 8]; Buffer.BlockCopy(pcm, 0, data, 0, data.Length); realtime.AddRange(data); });
            transport.Dispose();
            Check(realtime.SequenceEqual(File.ReadAllBytes(renderedLive.PcmPath)) && stats.EmptyBuffers == 0,
                "Live pre-device DSP PCM equals offline output and maintains one uninterrupted WASAPI session");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled = false;
            try { await source.EnsureAsync(0, 1, AtmosPresentation.Objects, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled seek does not continue to deliver cached audio");
            await Compare(source.TotalFrames - 24000, 24000, "Seek to title end and decoder finalization");
            Check(source.Complete, "All decoder frames validate successfully when playback reaches the end");
            measurements.Add(new { startupSeconds = startup, initialPreparedSeconds = initialFrames / 48000d, stats.EmptyBuffers, stats.MaxBlockMilliseconds, cacheBytes = Directory.EnumerateFiles(source.CacheFolder).Sum(p => new FileInfo(p).Length) });
            SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, measurements, fullAlbumListening = false });
        }
        finally
        {
            string cache = source.CacheFolder; await source.DisposeAsync();
            if (Directory.Exists(cache)) throw new InvalidDataException("Generated live cache was not released.");
        }
    }
}
