using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Disc2Flac;

/// <summary>Source timestamps, in 48 kHz samples. Never substitutes container/video duration for audio.</summary>
public sealed record AudioTimestampCoverage(long Start, long End, long PacketCount = 0)
{
    public static async Task<AudioTimestampCoverage> ReadAsync(string ffprobe, string source, int streamIndex,
        AppLog log, CancellationToken token)
    {
        string index = streamIndex.ToString(CultureInfo.InvariantCulture);
        string text = await SpatialRendering.RunAsync(ffprobe, ["-v", "error", "-select_streams", index,
            "-show_entries", "stream=start_pts,duration_ts,time_base", "-of", "json", source], token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(text);
        var streams = json.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("Cannot identify the selected audio stream for timestamp verification.");
        var stream = streams[0];
        if (!stream.TryGetProperty("time_base", out var basis)) throw new InvalidDataException("Missing audio timestamp base.");
        var timeline = new PacketTimeline(basis.GetString() ?? "");
        if (stream.TryGetProperty("start_pts", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out long first) &&
            stream.TryGetProperty("duration_ts", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out long length) && length > 0)
            return new(timeline.Sample(first), timeline.Sample(checked(first + length)));

        // Some valid MPEG-TS TrueHD streams omit duration_ts. Read the selected audio packets to EOF,
        // using bounded memory, and require an intact continuous timeline before accepting empty edges.
        log.Write($"ADM AUDIO COVERAGE {Path.GetFileName(source)} stream={index}: incomplete stream duration; verifying packet timestamps.");
        var info = new ProcessStartInfo(ffprobe)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-select_streams", index, "-show_packets", "-show_entries",
            "packet=pts,duration,flags", "-of", "compact=p=0", source }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start audio timestamp verification.");
        using var cancel = token.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                timeline.Add(line);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
                throw new InvalidDataException($"Audio timestamp scan failed for {Path.GetFileName(source)}. " + error[^Math.Min(error.Length, 1800)..]);
            var result = timeline.Complete();
            log.Write($"ADM AUDIO COVERAGE {Path.GetFileName(source)} stream={index}: packets={result.PacketCount} start={result.Start} end={result.End} samples; continuous.");
            return result;
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Cannot verify audio timestamp coverage for clip {Path.GetFileName(source)}, stream {index}: {ex.Message}", ex);
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    static void Stop(Process process)
    { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }

    /// <summary>Streaming ffprobe compact output parser. Rejects gaps, overlaps, corrupt packets and unknown timestamps.</summary>
    public sealed class PacketTimeline
    {
        readonly long numerator, denominator;
        long start, end, count;

        public PacketTimeline(string timeBase)
        {
            var parts = timeBase.Split('/');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out numerator) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator) || numerator <= 0 || denominator <= 0)
                throw new InvalidDataException("Invalid audio timestamp base.");
        }

        public long Sample(long pts) => checked((long)decimal.Round(pts * (decimal)numerator * 48000 / denominator, 0, MidpointRounding.AwayFromZero));

        public void Add(string line)
        {
            string? pts = null, duration = null, flags = null;
            foreach (string field in line.Split('|'))
            {
                if (field.StartsWith("pts=", StringComparison.Ordinal)) pts = field[4..];
                else if (field.StartsWith("duration=", StringComparison.Ordinal)) duration = field[9..];
                else if (field.StartsWith("flags=", StringComparison.Ordinal)) flags = field[6..];
            }
            if (pts is null && duration is null && flags is null) return; // Blank lines / packet side data.
            if (!long.TryParse(pts, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) ||
                !long.TryParse(duration, NumberStyles.None, CultureInfo.InvariantCulture, out long length) || length <= 0 ||
                flags is null || flags.Contains('C') || flags.Contains('D'))
                throw new InvalidDataException($"Cannot verify audio packet {count + 1}: missing timestamps/duration or corrupt/discarded packet.");
            long first = Sample(at), last = Sample(checked(at + length));
            if (last <= first) throw new InvalidDataException("Invalid audio packet timestamp span.");
            if (count > 0 && first != end)
                throw new InvalidDataException($"Audio packet timestamp discontinuity at packet {count + 1}: expected {end}, got {first} samples. Refusing an unexplained audio gap or overlap.");
            if (count == 0) start = first;
            end = last; count++;
        }

        public AudioTimestampCoverage Complete() => count > 0 ? new(start, end, count)
            : throw new InvalidDataException("No timestamped audio packets were found in the selected stream.");
    }
}
