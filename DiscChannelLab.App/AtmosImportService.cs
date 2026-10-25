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
        string bridge = DamfImporter.Version + (playlist.Clips.Count > 1 ? "\n" + DamfImporter.PlaylistVersion : "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bridge + "\n" + disc.DiscKey + "\n" + playlist.Id + "\n" + AudioStreamMatcher.Identity(stream) + "\n" + string.Join('\n', parts))));
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
        if (playlist.Clips.Count > 1) return await PreparePlaylistAsync(disc, playlist, stream, tracks, tool, cache, target, progress, token);
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
                    catch (IOException ex) when (!token.IsCancellationRequested)
                    {
                        if (!decoder.HasExited) decoder.Kill(true);
                        await decoder.WaitForExitAsync(CancellationToken.None);
                        string diagnostic = await errors;
                        log.Write($"ADM DECODER STOP title={playlist.DisplayNumber} clip={clip.Id} exit={decoder.ExitCode}: " + diagnostic);
                        throw new InvalidDataException($"ADM decoder stopped at clip {clip.Id}. " + diagnostic[^Math.Min(diagnostic.Length, 2400)..], ex);
                    }
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
            await Task.Run(() => DamfImporter.WriteAsync(damf, target, album, copyProgress, userToken), userToken);
            return target;
        }
        finally
        {
            // Only the explicitly created private work directory is disposable; destination ADM is retained.
            if (Path.GetFullPath(work).StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { foreach (var file in Directory.EnumerateFiles(work)) { try { File.Delete(file); } catch (IOException) { } } try { Directory.Delete(work, false); } catch (IOException) { } }
        }
    }
    async Task<string> PreparePlaylistAsync(DiscAnalysis disc, PlaylistInfo playlist, AudioStreamInfo selected, IReadOnlyList<TrackRow> tracks,
        string tool, string cache, string destination, IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        string work = Path.Combine(cache, ".prepare-playlist-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var segments = new List<DamfSegment>(); long total = ConversionService.ToSample(playlist.DurationTicks, 48000);
        try
        {
            for (int i = 0; i < playlist.Clips.Count; i++)
            {
                var clip = playlist.Clips[i]; token.ThrowIfCancellationRequested();
                long first = ConversionService.ToSample(clip.PlaylistStartTicks, 48000);
                long last = i + 1 == playlist.Clips.Count ? total : ConversionService.ToSample(playlist.Clips[i + 1].PlaylistStartTicks, 48000);
                long frames = last - first; if (frames <= 0) throw new InvalidDataException("Non-contiguous Atmos playlist timeline.");
                string basename = Path.Combine(work, $"clip-{i:000}-{clip.Id}"), source = SacdService.ClipPath(disc, clip);
                var available = clip.File?.Streams ?? await probe.ProbeClipAsync(source, token);
                var actual = AudioStreamMatcher.Resolve(selected, available);
                if (!IsAtmos(actual)) throw new InvalidDataException("The selected clip is not TrueHD Atmos.");
                long seek = clip.InTicks - (actual.StartTimeTicks ?? (clip.File is not null ? 0 : throw new InvalidDataException("Unknown source start time.")));
                seek = Math.Max(0, seek); // An audio stream may legitimately start after the play item's video in-point.
                progress?.Report(new(.7 * first / total, $"Decoding clip {i + 1}/{playlist.Clips.Count} ({clip.Id}) independently…"));
                var decodeInfo = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "--strict", "decode", "-", "--presentation", "3", "--output-path", basename, "--json", "--no-estimate-progress" }) decodeInfo.ArgumentList.Add(arg);
                using var decoder = Process.Start(decodeInfo) ?? throw new IOException("Cannot start the object decoder.");
                using var cancelDecoder = token.Register(() => KillDecoder(decoder));
                try { decoder.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
                var errors = decoder.StandardError.ReadToEndAsync(token); var summary = decoder.StandardOutput.ReadToEndAsync(token);
                var args = new List<string> { "-hide_banner", "-nostdin", "-v", "warning", "-probesize", "24000000", "-analyzeduration", "15000000" };
                if (seek > 0) args.AddRange(["-ss", (seek / 45000m).ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["-i", source, "-map", $"0:{actual.Index}", "-t", ((clip.OutTicks - clip.InTicks) / 45000m).ToString(CultureInfo.InvariantCulture), "-c:a", "copy", "-f", "truehd", "pipe:1"]);
                var extractInfo = new ProcessStartInfo(paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in args) extractInfo.ArgumentList.Add(arg);
                using var extract = Process.Start(extractInfo) ?? throw new IOException("Cannot start TrueHD demuxer.");
                using var cancelExtract = token.Register(() => KillDecoder(extract));
                var extractErrors = extract.StandardError.ReadToEndAsync(token);
                using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                int index = i;
                var monitor = Task.Run(async () =>
                {
                    try
                    {
                        while (!monitorStop.IsCancellationRequested)
                        {
                            long ready = 0;
                            try { var h = ProgressiveAtmosSource.Header(basename + ".atmos.audio"); ready = Math.Clamp((new FileInfo(basename + ".atmos.audio").Length - h.Offset) / (h.Channels * 3), 0, frames); }
                            catch (Exception e) when (e is IOException or InvalidDataException) { }
                            progress?.Report(new(.7 * (first + ready) / total, $"Decoding clip {index + 1}/{playlist.Clips.Count} ({clip.Id}) · {ready / 48000d:0}/{frames / 48000d:0} s…"));
                            await Task.Delay(500, monitorStop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
                }, monitorStop.Token);
                try
                {
                    try { await extract.StandardOutput.BaseStream.CopyToAsync(decoder.StandardInput.BaseStream, token); }
                    catch (IOException ex) when (!token.IsCancellationRequested)
                    {
                        KillDecoder(decoder); await decoder.WaitForExitAsync(CancellationToken.None); string detail = await errors;
                        log.Write($"ADM SEGMENT STOP {clip.Id}: " + detail);
                        throw new InvalidDataException($"Object decoder stopped in clip {clip.Id}. " + detail[^Math.Min(detail.Length, 2400)..], ex);
                    }
                    await extract.WaitForExitAsync(token); if (extract.ExitCode != 0) throw new FfToolException(await extractErrors);
                    decoder.StandardInput.Close(); await decoder.WaitForExitAsync(token);
                    string diagnostic = await errors; log.Write($"ADM SEGMENT {clip.Id}: " + diagnostic);
                    if (decoder.ExitCode != 0) throw new InvalidDataException($"Object decoder failed in clip {clip.Id}. " + diagnostic[^Math.Min(diagnostic.Length, 2400)..]);
                    using var json = JsonDocument.Parse(await summary); var result = json.RootElement;
                    var presentation = result.GetProperty("presentations").EnumerateArray().Single(p => p.GetProperty("index").GetInt32() == 3);
                    if (result.GetProperty("skippedFrames").GetInt64() != 0 || result.GetProperty("sampleRate").GetInt32() != 48000 || presentation.GetProperty("format").GetString() != "damf")
                        throw new InvalidDataException($"Clip {clip.Id} did not produce intact object audio.");
                    long decodedFrames = result.GetProperty("samples").GetInt64();
                    if (decodedFrames <= 0) throw new InvalidDataException($"Clip {clip.Id} contains no decoded PCM.");
                    long leading = 0, trailing = 0, trim = 0;
                    if (decodedFrames != frames || actual.StartTimeTicks > clip.InTicks)
                    {
                        var coverage = await AudioCoverageAsync(source, actual.Index, token);
                        leading = Math.Max(0, coverage.Start - ConversionService.ToSample(clip.InTicks, 48000));
                        trailing = Math.Max(0, ConversionService.ToSample(clip.OutTicks, 48000) - coverage.End);
                        long difference = frames - leading - trailing - decodedFrames;
                        // Account only for proven empty timestamp regions and at most one AU beyond an out-point.
                        // Never fill decoder failures or an unexplained missing region with silence.
                        if (difference < 0 && difference >= -40 && coverage.End > ConversionService.ToSample(clip.OutTicks, 48000)) trim = -difference;
                        else if (Math.Abs(difference) <= 1) trailing += difference;
                        else throw new InvalidDataException($"Clip {clip.Id}: decoded {decodedFrames} samples, timeline {frames}, verified silence {leading + trailing}. Refusing an unexplained audio gap.");
                        if (leading < 0 || trailing < 0) throw new InvalidDataException("Invalid source timestamp coverage.");
                        log.Write($"ADM CLIP TIMELINE {clip.Id}: decoded={decodedFrames} output={frames} leading-silence={leading} trailing-silence={trailing} trim-beyond-outpoint={trim}");
                    }
                    segments.Add(new(basename + ".atmos", frames, clip.PlaylistStartTicks, leading, trailing, trim));
                }
                finally { KillDecoder(extract); KillDecoder(decoder); monitorStop.Cancel(); try { await monitor; } catch (OperationCanceledException) { } }
            }
            if (segments.Sum(s => s.Frames) != total) throw new InvalidDataException("The complete audio timeline differs from the playlist.");
            var chapters = tracks.Select(t => new AdmChapter(ConversionService.ToSample(t.StartTicks, 48000), t.Title, t.Artist)).ToArray();
            var album = new AdmAlbumMetadata(playlist.TitleName ?? disc.AlbumTitle, disc.Artist, disc.Root, chapters);
            await Task.Run(() => DamfImporter.WritePlaylistAsync(segments, destination, album,
                new InlineProgress(p => progress?.Report(new(.7 + .3 * p.Fraction, p.Message))), token), token);
            return destination;
        }
        finally
        {
            // Only files created in this unique work folder are removed; original clips and saved ADM remain intact.
            if (Path.GetFullPath(work).StartsWith(Path.GetFullPath(cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { foreach (var file in Directory.EnumerateFiles(work)) { try { File.Delete(file); } catch (IOException) { } } try { Directory.Delete(work, false); } catch (IOException) { } }
        }
    }
    async Task<(long Start, long End)> AudioCoverageAsync(string source, int index, CancellationToken token)
    {
        string text = await SpatialRendering.RunAsync(paths.Ffprobe, ["-v", "error", "-select_streams", index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "stream=start_pts,duration_ts,time_base", "-of", "json", source], token);
        using var json = JsonDocument.Parse(text); var stream = json.RootElement.GetProperty("streams").EnumerateArray().Single();
        if (!stream.TryGetProperty("start_pts", out var start) || !stream.TryGetProperty("duration_ts", out var duration) ||
            !stream.TryGetProperty("time_base", out var basis)) throw new InvalidDataException("Cannot verify the audio timestamp coverage of this clip.");
        var rational = basis.GetString()!.Split('/');
        if (rational.Length != 2 || !long.TryParse(rational[0], out long numerator) || !long.TryParse(rational[1], out long denominator) || numerator <= 0 || denominator <= 0)
            throw new InvalidDataException("Invalid audio timestamp base.");
        long Sample(long pts) => checked((long)decimal.Round(pts * (decimal)numerator * 48000 / denominator, 0, MidpointRounding.AwayFromZero));
        long first = start.GetInt64(), length = duration.GetInt64();
        if (length <= 0) throw new InvalidDataException("Invalid audio timestamp span.");
        return (Sample(first), Sample(checked(first + length)));
    }
    static void KillDecoder(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    sealed class InlineProgress(Action<ConversionProgress> action) : IProgress<ConversionProgress> { public void Report(ConversionProgress value) => action(value); }
}
