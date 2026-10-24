using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Disc2Flac;

public sealed class AtmosImportService(ToolPaths paths, FfprobeService probe, AppLog log)
{
    public static bool IsAtmos(AudioStreamInfo stream) => stream.Codec == "truehd" && stream.Profile.Contains("Atmos", StringComparison.OrdinalIgnoreCase);
    static string Preference => AppDataPaths.Current.PreferencePath("truehdd-tool.json");
    public sealed record Prepared(string Path, long Bytes, long WriteTicks);
    static string Library => AppDataPaths.Current.PreferencePath("prepared-adm-library.json");
    static string SourceKey(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream)
    {
        var parts = playlist.Clips.Select(c => { string p = SacdService.ClipPath(disc, c); var f = new FileInfo(p); return $"{p}|{f.Length}|{f.LastWriteTimeUtc.Ticks}|{c.InTicks}|{c.OutTicks}"; });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DamfImporter.Version + "\n" + disc.DiscKey + "\n" + playlist.Id + "\n" + AudioStreamMatcher.Identity(stream) + "\n" + string.Join('\n', parts))));
    }
    public static void RegisterPrepared(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream, string path, string? libraryPath = null)
    {
        var wave = AdmWave.Open(path);
        if (!IsAtmos(stream) || Math.Abs(wave.Scene.DurationSamples - ConversionService.ToSample(playlist.DurationTicks, 48000)) > 480) throw new InvalidDataException("Prepared ADM does not match the source title duration.");
        string destination = libraryPath ?? Library; var records = ReadLibrary(destination); var info = new FileInfo(path);
        records[SourceKey(disc, playlist, stream)] = new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks); SpatialJson.Save(destination, records);
    }
    static Dictionary<string, Prepared> ReadLibrary(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Prepared>>(File.ReadAllText(path), SpatialJson.Options) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }
    public static string? FindTool()
    {
        try { if (File.Exists(Preference) && JsonSerializer.Deserialize<string>(File.ReadAllText(Preference)) is { } saved && File.Exists(saved)) return saved; } catch (Exception e) when (e is IOException or JsonException) { }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            foreach (var name in new[] { "tools/truehdd/truehdd.exe", "tools/truehdd-0.6.2/truehdd.exe" })
            { string candidate = Path.Combine(dir.FullName, name); if (File.Exists(candidate)) return candidate; }
        return null;
    }
    public static void SaveTool(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("truehdd.exe was not found.", path);
        SpatialJson.Save(Preference, path);
    }
    public async Task<string> PrepareAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo stream, IReadOnlyList<TrackRow> tracks,
        IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        if (!IsAtmos(stream)) throw new NotSupportedException("Position extraction currently supports TrueHD Atmos through external truehdd.");
        string key = SourceKey(disc, playlist, stream);
        foreach (string library in new[] { Library, Path.Combine(AppContext.BaseDirectory, "prepared-adm-library.json") })
            if (ReadLibrary(library).TryGetValue(key, out var record))
            { var info = new FileInfo(record.Path); if (info.Exists && info.Length == record.Bytes && info.LastWriteTimeUtc.Ticks == record.WriteTicks && AdmWave.HasMetadata(info.FullName)) { progress?.Report(new(1, "Using the existing full-title ADM.")); return info.FullName; } }
        string cache = Path.Combine(AppDataPaths.Current.Root, "sacd-cache", "atmos"), target = Path.Combine(cache, key + ".wav");
        Directory.CreateDirectory(cache);
        if (File.Exists(target) && File.Exists(target + ".import.json") && AdmWave.HasMetadata(target)) { progress?.Report(new(1, "Using prepared position-aware ADM.")); return target; }
        if (File.Exists(target)) File.Move(target, target + ".incomplete-" + Guid.NewGuid().ToString("N"));
        string tool = FindTool() ?? throw new FileNotFoundException("Select truehdd.exe before preparing Atmos ADM.");
        var toolVersion = await SpatialRendering.RunAsync(tool, ["--version"], token);
        if (!toolVersion.Contains("truehdd 0.6.2", StringComparison.Ordinal)) throw new NotSupportedException("This TrueHD object-audio bridge requires truehdd 0.6.2.");
        long expectedFrames = ConversionService.ToSample(playlist.DurationTicks, 48000);
        long needed = checked(expectedFrames * 32 * 3 * 2 + 1024L * 1024 * 1024);
        var drive = new DriveInfo(Path.GetPathRoot(cache)!);
        if (drive.AvailableFreeSpace < needed) throw new IOException($"ADM preparation needs up to {needed / 1073741824d:0.0} GiB free for decoded audio and ADM. Choose a shorter title or clear the audio cache.");
        string work = Path.Combine(cache, ".prepare-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        string basename = Path.Combine(work, "decoded"), damf = basename + ".atmos";
        var userToken = token;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); token = stop.Token;
        var decodeInfo = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--strict", "decode", "-", "--presentation", "3", "--output-path", basename, "--json" }) decodeInfo.ArgumentList.Add(arg);
        try
        {
            using var decoder = Process.Start(decodeInfo) ?? throw new IOException("Cannot start truehdd.");
            using var registration = token.Register(() => { try { if (!decoder.HasExited) decoder.Kill(true); } catch (InvalidOperationException) { } });
            var errors = decoder.StandardError.ReadToEndAsync(token); var summary = decoder.StandardOutput.ReadToEndAsync(token);
            var monitor = Task.Run(async () =>
            {
                try { while (!stop.IsCancellationRequested) { long bytes = File.Exists(damf + ".audio") ? new FileInfo(damf + ".audio").Length : 0;
                    progress?.Report(new(Math.Min(.69, bytes / (double)(expectedFrames * 16 * 3) * .7), $"Preparing Atmos ADM: decoded audio {bytes / 1073741824d:0.00} GiB…")); await Task.Delay(700, stop.Token); } }
                catch (OperationCanceledException) { }
            });
            try
            {
                foreach (var clip in playlist.Clips)
                {
                    token.ThrowIfCancellationRequested(); string source = SacdService.ClipPath(disc, clip);
                    var available = clip.File?.Streams ?? await probe.ProbeClipAsync(source, token); var actual = AudioStreamMatcher.Resolve(stream, available);
                    if (actual.Codec != "truehd") throw new InvalidDataException("Refusing to decode the compatible AC-3 track as Atmos.");
                    long seek = clip.InTicks - (actual.StartTimeTicks ?? (clip.File is not null ? 0 : throw new InvalidDataException("Unknown source start time.")));
                    if (seek < 0) throw new InvalidDataException("Invalid TrueHD clip interval.");
                    var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-probesize", "24000000", "-analyzeduration", "15000000" };
                    if (seek > 0) args.AddRange(["-ss", (seek / 45000m).ToString(CultureInfo.InvariantCulture)]);
                    args.AddRange(["-i", source, "-map", $"0:{actual.Index}", "-t", ((clip.OutTicks - clip.InTicks) / 45000m).ToString(CultureInfo.InvariantCulture), "-c:a", "copy", "-f", "truehd", "pipe:1"]);
                    var info = new ProcessStartInfo(paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args) info.ArgumentList.Add(arg);
                    using var extract = Process.Start(info) ?? throw new IOException("Cannot start TrueHD demuxer.");
                    using var cancelExtract = token.Register(() => { try { if (!extract.HasExited) extract.Kill(true); } catch (InvalidOperationException) { } });
                    var extractErrors = extract.StandardError.ReadToEndAsync(token);
                    try { await extract.StandardOutput.BaseStream.CopyToAsync(decoder.StandardInput.BaseStream, token); await extract.WaitForExitAsync(token); if (extract.ExitCode != 0) throw new FfToolException(await extractErrors); }
                    finally { if (!extract.HasExited) extract.Kill(true); }
                }
                decoder.StandardInput.Close(); await decoder.WaitForExitAsync(token);
                string details = await errors, result = await summary; log.Write(details);
                if (decoder.ExitCode != 0) throw new InvalidDataException("Atmos decoder failed; the normal channel track is still available. " + details[^Math.Min(details.Length, 1800)..]);
                using var json = JsonDocument.Parse(result); var presentation = json.RootElement.GetProperty("presentations").EnumerateArray().Single(p => p.GetProperty("index").GetInt32() == 3);
                if (json.RootElement.GetProperty("skippedFrames").GetInt64() != 0 || json.RootElement.GetProperty("sampleRate").GetInt32() != 48000 || presentation.GetProperty("format").GetString() != "damf") throw new InvalidDataException("Decoder did not produce an intact Atmos object presentation.");
                long samples = json.RootElement.GetProperty("samples").GetInt64();
                if (Math.Abs(samples - expectedFrames) > 480) throw new InvalidDataException("Decoded Atmos timeline differs from the playlist by more than 10 ms; refusing misaligned chapter metadata.");
            }
            finally { if (!decoder.HasExited) decoder.Kill(true); stop.Cancel(); await monitor; }
            var chapters = tracks.Select(t => new AdmChapter(ConversionService.ToSample(t.StartTicks, 48000), t.Title, t.Artist)).ToArray();
            var album = new AdmAlbumMetadata(playlist.TitleName ?? disc.AlbumTitle, disc.Artist, disc.Root, chapters);
            var copyProgress = new InlineProgress(p => progress?.Report(new(.7 + .3 * p.Fraction, p.Message)));
            await DamfImporter.WriteAsync(damf, target, album, copyProgress, userToken);
            return target;
        }
        finally
        {
            // Only the explicitly created private work directory is disposable; destination ADM is retained.
            if (Path.GetFullPath(work).StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { foreach (var file in Directory.EnumerateFiles(work)) { try { File.Delete(file); } catch (IOException) { } } try { Directory.Delete(work, false); } catch (IOException) { } }
        }
    }
    sealed class InlineProgress(Action<ConversionProgress> action) : IProgress<ConversionProgress> { public void Report(ConversionProgress value) => action(value); }
}
