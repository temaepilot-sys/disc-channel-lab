using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Disc2Flac;

public sealed record SacdTrack(int Number, long DurationTicks, string? Title, string? Artist);
public sealed record SacdArea(int Channels, string Encoding, IReadOnlyList<SacdTrack> Tracks);
public sealed record SacdToc(string? Album, string? DiscTitle, string? Artist, string? Date,
    string? Genre, string? Publisher, IReadOnlyList<SacdArea> Areas);
public sealed record SacdTrackSource(string ImagePath, long ImageLength, long ImageWriteTicks,
    bool Multichannel, int TrackNumber, int Channels, long DurationTicks, string PcmPath);

/// <summary>External file-mode extraction only. No player communication, keys or disc authentication.</summary>
public sealed class SacdService(ToolPaths paths, ProcessRunner runner, AppLog log,
    string? cacheRoot = null, string? toolOverride = null, IReadOnlyList<string>? toolPrefix = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public const int PcmSampleRate = 88200;
    private readonly string _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(AppDataPaths.Current.Root, "sacd-cache"));
    public IProgress<string>? Progress { get; set; }
    public static string ToolPreferencePath => AppDataPaths.Current.PreferencePath("sacd-tool.json");

    public static string? FindTool(string? preferencePath = null)
    {
        preferencePath ??= ToolPreferencePath;
        try
        {
            if (File.Exists(preferencePath))
            {
                var saved = JsonSerializer.Deserialize<ToolPreference>(File.ReadAllText(preferencePath));
                if (saved is not null && File.Exists(saved.Path)) return saved.Path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "sacd_extract.exe");
        if (File.Exists(local)) return local;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), "sacd_extract.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static void SaveTool(string path, string? preferencePath = null)
    {
        preferencePath = Path.GetFullPath(preferencePath ?? ToolPreferencePath);
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("sacd_extract.exe が見つかりません。", path);
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        var temporary = preferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ToolPreference(path)));
            File.Move(temporary, preferencePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsSacdIso(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)) return false;
        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        // Master TOC at logical sector 510. Also recognize raw 2064-byte sector images.
        foreach (var offset in new long[] { 510L * 2048, 510L * 2064, 510L * 2064 + 12 })
        {
            if (input.Length < offset + 8) continue;
            input.Position = offset;
            input.ReadExactly(signature);
            if (signature.SequenceEqual("SACDMTOC"u8)) return true;
        }
        return false;
    }

    public async Task<DiscAnalysis> AnalyzeAsync(string imagePath, CancellationToken token)
    {
        imagePath = Path.GetFullPath(imagePath);
        if (!IsSacdIso(imagePath)) throw new InvalidDataException("SACD ISO の構造を確認できません。");
        var file = new FileInfo(imagePath);
        var key = Fingerprint(imagePath);
        Directory.CreateDirectory(_cacheRoot);
        var job = Path.Combine(_cacheRoot, ".toc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        SacdToc toc;
        try
        {
            Progress?.Report(LanguageService.T("SACDの曲情報を読み込んでいます"));
            var result = await RunToolAsync(["-P", "-i", imagePath], job, token);
            toc = ParseToc(result.Output);
        }
        finally { CleanJob(job); }
        var album = toc.DiscTitle ?? toc.Album ?? Path.GetFileNameWithoutExtension(imagePath);
        var playlists = new List<PlaylistInfo>();
        foreach (var area in toc.Areas.OrderBy(a => a.Channels == 2 ? 0 : 1))
        {
            long position = 0;
            var multi = area.Channels != 2;
            var clips = new List<ClipInfo>();
            var starts = new List<long>();
            foreach (var track in area.Tracks)
            {
                starts.Add(position);
                var pcm = Path.Combine(_cacheRoot, key, "pcm-88200-24-v1", multi ? "mch" : "2ch", $"{track.Number:000}.flac");
                clips.Add(new ClipInfo($"{track.Number:000}", 0, track.DurationTicks, position,
                    new(imagePath, file.Length, file.LastWriteTimeUtc.Ticks, multi, track.Number,
                        area.Channels, track.DurationTicks, pcm)));
                position = checked(position + track.DurationTicks);
            }
            playlists.Add(new PlaylistInfo
            {
                Id = multi ? 2 : 1, DisplayOrder = playlists.Count + 1, Format = DiscFormat.Sacd,
                SacdArea = area, TitleName = $"{toc.Album ?? album} [{(multi ? area.Channels + "ch" : "Stereo")}]",
                Clips = clips, ChapterStarts = starts, ChapterTitles = area.Tracks.Select(t => t.Title).ToArray(),
                DurationTicks = position, MergeShortTail = false
            });
        }
        return new DiscAnalysis
        {
            Root = imagePath, DiscKey = key, Format = DiscFormat.Sacd, AlbumTitle = album,
            Artist = toc.Artist, Date = toc.Date, Genre = toc.Genre, Publisher = toc.Publisher,
            Description = "SACD DSD → PCM 24-bit / 88.2 kHz; selected tracks extracted with an external tool.",
            Playlists = playlists, Diagnostics = [$"External SACD ISO reader; {playlists.Count} audio areas; PCM cache: 24-bit / 88.2 kHz."]
        };
    }

    public async Task<PlaylistProbeResult> InspectAsync(PlaylistInfo playlist, CancellationToken token)
    {
        var area = playlist.SacdArea ?? throw new InvalidDataException("SACDのエリア情報がありません。");
        var first = playlist.Clips.First().Sacd!;
        var path = await PrepareAsync(first, token);
        var stream = await new FfprobeService(paths, runner).ProbeFileAsync(path, token);
        var chapters = new Dictionary<int, ChapterMetadata>();
        for (var i = 0; i < area.Tracks.Count; i++)
        {
            var track = area.Tracks[i];
            chapters[track.Number] = new(track.Title, track.Artist) { StartTicks = playlist.ChapterStarts[i] };
        }
        return new PlaylistProbeResult([stream], chapters, new Dictionary<string, string>())
        {
            Availability = AudioAvailability.Ready, DetectedAudioCount = 1,
            Diagnostics = [$"SACD {area.Encoding}; {stream.Channels}ch ({stream.ChannelLayout}) → PCM 24-bit / 88.2 kHz.",
                "First track decoded; remaining tracks are prepared on demand."]
        };
    }

    public async Task<string> PrepareAsync(SacdTrackSource source, CancellationToken token)
    {
        var gate = Gates.GetOrAdd(source.PcmPath, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(token);
        try
        {
            var image = new FileInfo(source.ImagePath);
            if (!image.Exists || image.Length != source.ImageLength || image.LastWriteTimeUtc.Ticks != source.ImageWriteTicks)
                throw new IOException("SACD ISO が変更されています。ISOを開き直してください。");
            var probe = new FfprobeService(paths, runner);
            if (File.Exists(source.PcmPath))
            {
                try { ValidatePcm(await probe.ProbeFileAsync(source.PcmPath, token), source, exact: true); return source.PcmPath; }
                catch (Exception ex) when (FfprobeService.IsRecoverable(ex))
                { log.Write($"SACD CACHE INVALID: {ex.Message}"); }
            }
            var directory = Path.GetDirectoryName(source.PcmPath)!;
            Directory.CreateDirectory(directory);
            // Keep the external process working directory short. The disc hash and
            // PCM version already make the destination deep; adding artist/title
            // folders there can exceed Windows native working-directory limits.
            var job = Path.Combine(_cacheRoot, ".extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                Progress?.Report(LanguageService.T("SACDの選択曲を準備しています"));
                // -t/-y require the EuFlo 0.3.9.x command line. Run in a private working
                // directory so another program's sacd_extract.cfg cannot override choices.
                await RunToolAsync([source.Multichannel ? "-m" : "-2", "-s", "-c", "-t",
                    source.TrackNumber.ToString(CultureInfo.InvariantCulture), "-y", job, "-i", source.ImagePath], job, token);
                var files = OwnedFiles(job).Where(f => Path.GetExtension(f).Equals(".dsf", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length != 1) throw new InvalidDataException("sacd_extract が選択曲を1曲として出力できませんでした。EuFlo 0.3.9.x 互換版を使用してください。");
                var dsd = await probe.ProbeFileAsync(files[0], token);
                if (!dsd.Codec.StartsWith("dsd_", StringComparison.Ordinal) || !dsd.HasSupportedChannels ||
                    dsd.Channels != source.Channels || !HasKnownLayout(dsd))
                    throw new InvalidDataException("SACDのチャンネル配置を確定できません。チャンネル数だけからは推測しません。");
                var pcm = Path.Combine(job, "pcm.flac");
                var count = ConversionService.ToSample(source.DurationTicks, PcmSampleRate);
                await runner.RunAsync(paths.Ffmpeg,
                    ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-i", files[0], "-map", $"0:{dsd.Index}",
                     "-vn", "-sn", "-dn", "-af", $"aresample={PcmSampleRate},atrim=end_sample={count},asetpts=PTS-STARTPTS",
                     "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-y", pcm], token);
                var actual = await probe.ProbeFileAsync(pcm, token);
                ValidatePcm(actual, source);
                if (actual.ChannelLayout != dsd.ChannelLayout)
                    throw new InvalidDataException("DSDからPCMへの変換でチャンネル配置が変化しました。");
                // A few samples may be lost to decoder/resampler delay; never cover a
                // truncated track or a missing second with silence.
                var missing = count - actual.SampleCount!.Value;
                if (missing > 0)
                {
                    if (missing > PcmSampleRate / 75) throw new InvalidDataException("SACDの音声データが曲の長さより短いため処理を中止しました。");
                    var padded = Path.Combine(job, "padded.flac");
                    await runner.RunAsync(paths.Ffmpeg,
                        ["-hide_banner", "-nostdin", "-v", "error", "-i", pcm, "-map", "0:a:0",
                         "-af", $"apad=whole_len={count},atrim=end_sample={count}", "-c:a", "flac", "-sample_fmt", "s32",
                         "-bits_per_raw_sample", "24", "-y", padded], token);
                    ValidatePcm(await probe.ProbeFileAsync(padded, token), source, exact: true);
                    File.Move(padded, pcm, overwrite: true);
                    log.Write($"SACD resampler tail: {missing} samples padded.");
                }
                token.ThrowIfCancellationRequested();
                File.Move(pcm, source.PcmPath, overwrite: true);
                log.Write($"SACD PCM READY track={source.TrackNumber} multichannel={source.Multichannel} layout={actual.ChannelLayout}");
                return source.PcmPath;
            }
            finally { CleanJob(job); }
        }
        finally { gate.Release(); }
    }

    private static void ValidatePcm(AudioStreamInfo pcm, SacdTrackSource source, bool exact = false)
    {
        var count = ConversionService.ToSample(source.DurationTicks, PcmSampleRate);
        if (pcm.Codec != "flac" || pcm.SampleRate != PcmSampleRate || pcm.BitDepth != 24 ||
            pcm.Channels != source.Channels || !HasKnownLayout(pcm) || pcm.SampleCount is not { } samples ||
            samples > count || samples < count - (exact ? 0 : PcmSampleRate / 75))
            throw new InvalidDataException("SACD PCMキャッシュの形式または長さが一致しません。");
    }

    private static bool HasKnownLayout(AudioStreamInfo stream) =>
        stream.Channels == 2 && stream.ChannelLayout == "stereo" || StereoMixSettings.Supports(stream);

    private async Task<ProcessResult> RunToolAsync(IReadOnlyList<string> arguments, string directory, CancellationToken token)
    {
        var tool = toolOverride ?? FindTool() ?? throw new FileNotFoundException(
            "sacd_extract.exe が必要です。「SACD抽出ツール」でEuFlo 0.3.9.x 互換版を指定してください。");
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in (toolPrefix ?? []).Concat(arguments)) info.ArgumentList.Add(argument);
        log.Write($"SACD TOOL {Path.GetFileName(tool)} {string.Join(' ', arguments.Select(a => a.Contains(' ') ? '"' + a + '"' : a))}");
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("SACD抽出ツールを起動できません。");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, token);
        var readErr = process.StandardError.BaseStream.CopyToAsync(stderr, token);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(readOut, readErr);
            var result = new ProcessResult(DecodeToolText(stdout.ToArray()), DecodeToolText(stderr.ToArray()));
            if (process.ExitCode != 0)
                throw new FfToolException($"sacd_extract: {result.Error}\n{result.Output}");
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(readOut, readErr); } catch (OperationCanceledException) { }
        }
    }

    internal static string DecodeToolText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length > 10 && bytes.Where((b, i) => i % 2 == 1 && b == 0).Count() > bytes.Length / 4)
            return Encoding.Unicode.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    internal static string Fingerprint(string imagePath)
    {
        using var input = File.OpenRead(imagePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"SACD-ISO-v1:{input.Length}:"));
        var buffer = new byte[65536];
        foreach (var offset in new long[] { 0, 510L * 2048, Math.Max(0, input.Length - buffer.Length) }.Distinct())
        {
            input.Position = Math.Min(offset, input.Length);
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, input.Length - input.Position));
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static IReadOnlyList<string> OwnedFiles(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        void Read(string current)
        {
            foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if (!item.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("SACD一時フォルダー内のリンクは処理できません。");
                if (item is DirectoryInfo) Read(item.FullName); else files.Add(item.FullName);
            }
        }
        Read(directory);
        return files;
    }

    private void CleanJob(string directory)
    {
        try
        {
            // Only our freshly generated job directory. No shell deletion and no links.
            foreach (var file in OwnedFiles(directory)) File.Delete(file);
            void RemoveEmpty(string current)
            { foreach (var child in Directory.EnumerateDirectories(current)) RemoveEmpty(child); Directory.Delete(current); }
            RemoveEmpty(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"SACD TEMP CLEANUP: {ex.Message}"); }
    }

    public static string ClipPath(DiscAnalysis disc, ClipInfo clip) => clip.Sacd?.PcmPath ??
        Path.Combine(disc.Root, "BDMV", "STREAM", clip.Id + ".m2ts");

    private sealed record ToolPreference(string Path);

    public static SacdToc ParseToc(string text)
    {
        string? album = null, disc = null, artist = null, date = null, genre = null, publisher = null;
        var areas = new List<SacdArea>();
        var tracks = new List<SacdTrack>();
        var titles = new Dictionary<int, string>();
        var artists = new Dictionary<int, string>();
        int channels = 0, expected = 0, areaIndex = -1;
        string encoding = "DSD", section = "";
        void CompleteArea()
        {
            if (areaIndex < 0) return;
            if (channels is not (2 or 5 or 6) || expected == 0 || tracks.Count != expected)
                throw new InvalidDataException("SACDのチャンネル情報または曲一覧を読み取れません。EuFlo 0.3.9.x 互換版を確認してください。");
            areas.Add(new(channels, encoding, tracks.Select(t => t with
            { Title = titles.GetValueOrDefault(t.Number), Artist = artists.GetValueOrDefault(t.Number) }).ToArray()));
        }
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("Album Information:", StringComparison.Ordinal)) { section = "album"; continue; }
            if (line.StartsWith("Disc Information:", StringComparison.Ordinal)) { section = "disc"; continue; }
            var areaMatch = Regex.Match(line, @"^Area Information\s*\[(\d+)\]:$");
            if (areaMatch.Success)
            {
                CompleteArea(); areaIndex = int.Parse(areaMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                channels = expected = 0; tracks.Clear(); titles.Clear(); artists.Clear(); encoding = "DSD"; section = "area"; continue;
            }
            if (section == "area")
            {
                var speaker = Regex.Match(line, @"^Speaker config:\s*(\d+) Channel$");
                if (speaker.Success) channels = int.Parse(speaker.Groups[1].Value, CultureInfo.InvariantCulture);
                var count = Regex.Match(line, @"^Track Count:\s*(\d+)$");
                if (count.Success) expected = int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture);
                if (line.StartsWith("Frame format encoding:", StringComparison.Ordinal)) encoding = line[(line.IndexOf(':') + 1)..].Trim();
                var tag = Regex.Match(line, @"^(Title|Performer)\[(\d+)\]:\s*(.*)$");
                if (tag.Success)
                {
                    var number = int.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture);
                    (tag.Groups[1].Value == "Title" ? titles : artists)[number] = tag.Groups[3].Value.Trim();
                }
                var duration = Regex.Match(line, @"^Duration:\s*(\d+):(\d{2}):(\d{2})\s*\[mins:secs:frames\]$");
                if (duration.Success)
                {
                    var minutes = int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture);
                    var seconds = int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture);
                    var frames = int.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (seconds >= 60 || frames >= 75 || minutes > 360) throw new InvalidDataException("SACDの曲時間が不正です。");
                    var ticks = ((minutes * 60L + seconds) * 75 + frames) * 600;
                    if (ticks <= 0) throw new InvalidDataException("SACDの曲時間がありません。");
                    tracks.Add(new(tracks.Count + 1, ticks, null, null));
                }
                continue;
            }
            if (section is not ("album" or "disc")) continue;
            if (line.StartsWith("Title: ", StringComparison.Ordinal))
            { if (section == "album") album = line[7..].Trim(); else disc = line[7..].Trim(); }
            if (line.StartsWith("Artist: ", StringComparison.Ordinal)) artist ??= line[8..].Trim();
            if (line.StartsWith("Publisher: ", StringComparison.Ordinal)) publisher ??= line[11..].Trim();
            if (line.StartsWith("Creation date: ", StringComparison.Ordinal)) date = line[15..].Trim();
            if (line.StartsWith("Album genre: ", StringComparison.Ordinal)) genre = line[13..].Trim();
        }
        CompleteArea();
        if (areas.Count == 0 || areas.GroupBy(a => a.Channels == 2).Any(g => g.Count() > 1))
            throw new InvalidDataException("SACDのステレオ / マルチチャンネルのエリアを確定できません。");
        return new(album, disc, artist, date, genre, publisher, areas);
    }
}
