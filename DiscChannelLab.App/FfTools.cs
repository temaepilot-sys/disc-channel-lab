using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Disc2Flac;

public sealed class AppLog
{
    private readonly object _gate = new();
    public string FilePath { get; private set; }

    public AppLog(string? directory = null)
    {
        directory ??= Path.Combine(AppDataPaths.Current.Root, "logs");
        try { Directory.CreateDirectory(directory); }
        catch (UnauthorizedAccessException)
        {
            directory = Path.Combine(AppDataPaths.TemporaryRoot, "logs");
            Directory.CreateDirectory(directory);
        }
        FilePath = Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.log");
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            var entry = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            try { Append(entry); }
            catch (UnauthorizedAccessException)
            {
                var fallback = Path.Combine(AppDataPaths.TemporaryRoot, "logs");
                Directory.CreateDirectory(fallback);
                FilePath = Path.Combine(fallback, $"{DateTime.Now:yyyy-MM-dd}.log");
                Append(entry);
            }
        }
    }

    private void Append(string entry)
    {
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(entry);
    }
}

public sealed class ToolPaths
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public string? Ffplay { get; }

    public ToolPaths()
    {
        var embedded = EmbeddedTools.GetDirectory();
        Ffmpeg = Find("ffmpeg.exe", embedded);
        Ffprobe = Find("ffprobe.exe", embedded);
        Ffplay = FindOptional("ffplay.exe", embedded);
    }

    private static string Find(string name, string? embedded)
        => FindOptional(name, embedded) ?? throw new FileNotFoundException($"{name} が見つかりません。アプリの tools フォルダーに配置してください。");

    private static string? FindOptional(string name, string? embedded)
    {
        if (embedded is not null)
        {
            var bundled = Path.Combine(embedded, name);
            if (File.Exists(bundled)) return bundled;
        }
        var local = Path.Combine(AppContext.BaseDirectory, "tools", name);
        if (File.Exists(local)) return local;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var candidate = Path.Combine(entry.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        var fallback = Path.Combine(@"C:\ffmpeg\bin", name);
        if (File.Exists(fallback)) return fallback;
        return null;
    }
}

public sealed record ProcessResult(string Output, string Error);

public sealed class ProcessRunner(AppLog log)
{
    public async Task<ProcessResult> RunWithInputAsync(string executable, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task> writeInput, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        log.Write($"RUN {Path.GetFileName(executable)} {string.Join(" ", info.ArgumentList.Select(Quote))}");
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException($"{Path.GetFileName(executable)} を起動できません。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            try { await writeInput(process.StandardInput.BaseStream, cancellationToken); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232)
            {
                // ffprobe may finish after learning the stream format, before the sample is exhausted.
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var result = new ProcessResult(await output, await error);
            if (process.ExitCode != 0)
                throw new FfToolException(result.Error.Length == 0 ? "音声デコーダーが失敗しました。" : result.Error);
            return result;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, Action<string>? onOutput = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        log.Write($"RUN {Path.GetFileName(executable)} {string.Join(" ", info.ArgumentList.Select(Quote))}");

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException($"{Path.GetFileName(executable)} を起動できません。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            onOutput?.Invoke(line);
        }
        await process.WaitForExitAsync(cancellationToken);
        var error = await errors;
        if (process.ExitCode != 0)
        {
            log.Write($"FAIL exit={process.ExitCode} {error}");
            throw new FfToolException(error.Length == 0 ? $"{Path.GetFileName(executable)} が失敗しました。" : error);
        }
        if (error.Length > 0) log.Write($"STDERR {error}");
        return new ProcessResult(output.ToString(), error);
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}

public sealed class FfToolException(string message) : Exception(message);
public sealed class ProtectedDiscException(string message) : Exception(message);

public sealed partial class FfprobeService(ToolPaths paths, ProcessRunner runner)
{
    public static string BlurayInput(string root) => $"bluray:{root}";

    private async Task<PlaylistProbeResult> ProbePlaylistCoreAsync(string root, PlaylistInfo playlist, CancellationToken token)
    {
        ProcessResult result;
        if (playlist.Format == DiscFormat.DvdAudio && playlist.DvdAudio is { } audio)
        {
            var program = audio.Programs[0];
            result = await runner.RunWithInputAsync(paths.Ffprobe,
                ["-v", "error", "-f", "mpeg", "-probesize", "4000000", "-analyzeduration", "10000000",
                 "-of", "json", "-show_streams", "-show_format", "pipe:0"],
                (input, ct) => DvdAudioSectors.CopyAsync(root, audio.TitleSet, program.StartSector,
                    program.EndSector, input, ct, 4 * 1024 * 1024), token);
        }
        else if (playlist.Format == DiscFormat.DvdVideo)
            result = await runner.RunAsync(paths.Ffprobe,
                ["-v", "error", "-f", "dvdvideo", "-title", playlist.DvdVideoTitle.ToString(),
                 "-of", "json", "-show_streams", "-show_chapters", "-show_format", root], token);
        else
            result = await runner.RunAsync(paths.Ffprobe,
                ["-v", "error", "-of", "json", "-show_streams", "-show_chapters", "-show_format", "-playlist", playlist.Id.ToString(), BlurayInput(root)], token);
        using var document = JsonDocument.Parse(result.Output);
        var streams = ReadSupportedAudioStreams(document.RootElement, playlist.Format);
        var names = new Dictionary<int, ChapterMetadata>();
        if (document.RootElement.TryGetProperty("chapters", out var chapters))
        {
            var number = 1;
            foreach (var chapter in chapters.EnumerateArray())
            {
                if (chapter.TryGetProperty("tags", out var tags))
                {
                    var chapterTags = ReadTags(tags);
                    names[number] = new ChapterMetadata(Tag(chapterTags, "title"), Tag(chapterTags, "artist"))
                        { StartTicks = ParseTicks(String(chapter, "start_time")) };
                }
                number++;
            }
        }
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var formatTags))
            foreach (var tag in ReadTags(formatTags)) metadata[tag.Key] = tag.Value;
        if (document.RootElement.TryGetProperty("streams", out var streamElements))
            foreach (var stream in streamElements.EnumerateArray())
                if (String(stream, "codec_type") == "audio" && stream.TryGetProperty("tags", out var streamTags))
                    foreach (var tag in ReadTags(streamTags)) metadata.TryAdd(tag.Key, tag.Value);
        var detected = DescribeAudio(document.RootElement);
        return new PlaylistProbeResult(streams, names, metadata)
            { DetectedAudioCount = detected.Count, Diagnostics = detected };
    }

    public async Task<(long DurationTicks, IReadOnlyList<long> Chapters)> ProbeDvdVideoTitleAsync(
        string root, int title, CancellationToken token)
    {
        var result = await runner.RunAsync(paths.Ffprobe,
            ["-v", "error", "-f", "dvdvideo", "-title", title.ToString(),
             "-of", "json", "-show_streams", "-show_chapters", "-show_format", root], token);
        using var document = JsonDocument.Parse(result.Output);
        var json = document.RootElement;
        var duration = json.TryGetProperty("format", out var format) ? ParseTicks(String(format, "duration")) ?? 0 : 0;
        var starts = new List<long>();
        if (json.TryGetProperty("chapters", out var chapters))
            foreach (var chapter in chapters.EnumerateArray())
                if (ParseTicks(String(chapter, "start_time")) is { } start && start >= 0 && start < duration &&
                    (starts.Count == 0 || start > starts[^1])) starts.Add(start);
        if (duration <= 0) return (0, []);
        if (starts.Count == 0 || starts[0] != 0) starts.Insert(0, 0);
        return (duration, starts);
    }

    public async Task<bool> HasPlayableDvdVideoAudioAsync(string root, int title, CancellationToken token)
    {
        var result = await runner.RunAsync(paths.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1",
             "-f", "dvdvideo", "-title", title.ToString(), "-i", root,
             "-map", "0:a:0", "-t", "0.25", "-f", "null", "NUL"], token);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                         long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds) && microseconds > 0);
    }

    public async Task<IReadOnlyList<AudioStreamInfo>> ProbeClipAsync(string path, CancellationToken token)
    {
        return (await ProbeClipDetailedAsync(path, token)).Streams;
    }

    private static IReadOnlyList<AudioStreamInfo> ReadSupportedAudioStreams(JsonElement root,
        DiscFormat format = DiscFormat.BluRay, long? formatStartTicks = null)
    {
        var streams = new List<AudioStreamInfo>();
        if (root.TryGetProperty("streams", out var streamElements))
        {
            foreach (var stream in streamElements.EnumerateArray())
            {
                if (String(stream, "codec_type") != "audio") continue;
                var codec = String(stream, "codec_name");
                var profile = String(stream, "profile");
                // These compressed streams already use the external decoder on DVD.
                // Blu-ray playlist and direct-clip probes must accept them too.
                if (format == DiscFormat.BluRay && codec is not ("pcm_bluray" or "ac3" or "eac3") &&
                    !(codec == "dts" && profile == "DTS-HD MA") ||
                    format == DiscFormat.DvdAudio && codec is not ("pcm_dvd" or "pcm_dvda" or "mlp") ||
                    format == DiscFormat.DvdVideo && codec is not ("pcm_dvd" or "ac3" or "eac3" or "mp2" or "dts"))
                    continue;
                var depth = Number(stream, "bits_per_raw_sample");
                if (depth is null or 0)
                {
                    depth = String(stream, "sample_fmt") switch { "s16" => 16, "s32" => 24, _ => Number(stream, "bits_per_sample") };
                }
                if (depth is null or 0 && codec is ("ac3" or "eac3" or "mp2")) depth = 16;
                streams.Add(new AudioStreamInfo
                {
                    Index = Number(stream, "index") ?? -1,
                    Codec = codec,
                    Profile = profile,
                    TransportId = String(stream, "id"),
                    Language = stream.TryGetProperty("tags", out var streamTags) ? String(streamTags, "language") : "",
                    SampleRate = Number(stream, "sample_rate") ?? 0,
                    Channels = Number(stream, "channels") ?? 0,
                    ChannelLayout = String(stream, "channel_layout") is { Length: > 0 } layout ? layout : "unknown",
                    BitDepth = depth is > 0 ? depth : null,
                    StartTimeTicks = formatStartTicks ?? ParseTicks(String(stream, "start_time"))
                });
            }
        }
        return streams;
    }

    public async Task<AudioStreamInfo> ProbeFileAsync(string path, CancellationToken token)
    {
        var result = await runner.RunAsync(paths.Ffprobe, ["-v", "error", "-of", "json", "-show_streams", path], token);
        using var document = JsonDocument.Parse(result.Output);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().First(x => String(x, "codec_type") == "audio");
        var depth = Number(stream, "bits_per_raw_sample");
        if (depth is null or 0) depth = Number(stream, "bits_per_sample");
        return new AudioStreamInfo
        {
            Index = Number(stream, "index") ?? 0,
            Codec = String(stream, "codec_name"),
            SampleRate = Number(stream, "sample_rate") ?? 0,
            Channels = Number(stream, "channels") ?? 0,
            ChannelLayout = String(stream, "channel_layout") is { Length: > 0 } layout ? layout : "unknown",
            BitDepth = depth is > 0 ? depth : null,
            SampleCount = long.TryParse(String(stream, "duration_ts"), out var count) ? count : null
        };
    }

    private static string String(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static Dictionary<string, string> ReadTags(JsonElement tags)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags.ValueKind != JsonValueKind.Object) return result;
        foreach (var tag in tags.EnumerateObject())
            if (tag.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.Value.GetString()))
                result[tag.Name] = tag.Value.GetString()!.Trim();
        return result;
    }

    private static string? Tag(IReadOnlyDictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var value) ? value : null;

    private static int? Number(JsonElement element, string name) => int.TryParse(String(element, name), out var value) ? value : null;

    private static long? ParseTicks(string seconds) =>
        decimal.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value * 45000m, MidpointRounding.AwayFromZero) : null;
}
