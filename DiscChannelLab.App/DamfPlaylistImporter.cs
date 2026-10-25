using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Disc2Flac;

public sealed record DamfSegment(string Path, long Frames, long? SourceStartTicks = null,
    long LeadingSilenceFrames = 0, long TrailingSilenceFrames = 0, long TrimEndFrames = 0);

public static partial class DamfImporter
{
    public const string PlaylistVersion = "independent-strict-playitems-1";
    sealed record ParsedSegment(string Audio, Caf AudioFormat, List<Source> Sources, DamfSegment Segment)
    { public long AudioFrames => AudioFormat.Frames - Segment.TrimEndFrames; }

    /// <summary>Join independently decoded play items. PCM and metadata share exact sample boundaries;
    /// object histories reset at each boundary rather than interpolating between unrelated decoder states.</summary>
    public static async Task WritePlaylistAsync(IReadOnlyList<DamfSegment> segments, string destination,
        AdmAlbumMetadata album, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        if (segments.Count is < 1 or > 1000 || segments.Any(s => s.Frames <= 0)) throw new InvalidDataException("Invalid ADM segment list.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("ADM destination already exists.");
        var warnings = new List<string>(); var parts = new List<ParsedSegment>(); var sources = new List<Source>();
        long totalFrames = 0;
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = ReadRoot(Path.GetFullPath(segment.Path), warnings); var caf = ReadCaf(parsed.Audio);
            if (caf.Channels != parsed.Sources.Count || segment.LeadingSilenceFrames < 0 || segment.TrailingSilenceFrames < 0 || segment.TrimEndFrames is < 0 or > 40 ||
                caf.Frames <= segment.TrimEndFrames || caf.Frames - segment.TrimEndFrames + segment.LeadingSilenceFrames + segment.TrailingSilenceFrames != segment.Frames)
                throw new InvalidDataException($"Decoded clip duration/layout mismatch: expected {segment.Frames} frames, got {caf.Frames}. Refusing to omit or pad audio.");
            ReadEvents(parsed.Metadata, parsed.Sources, caf.Frames, warnings, ct);
            foreach (var source in parsed.Sources)
            {
                var existing = sources.FirstOrDefault(s => s.Id == source.Id);
                if (existing is null) sources.Add(new(source.Id, source.Name, source.Bed, source.Lfe, []));
                else if (existing.Name != source.Name || existing.Bed != source.Bed || existing.Lfe != source.Lfe)
                    throw new NotSupportedException("A source ID changes speaker/object role between clips. Refusing to map it to the wrong channel.");
            }
            if (sources.Count > 32) throw new NotSupportedException("The combined ADM layout exceeds the 32 supported source slots.");
            parts.Add(new(parsed.Audio, caf, parsed.Sources, segment)); totalFrames = checked(totalFrames + segment.Frames);
        }
        long offset = 0;
        foreach (var part in parts)
        {
            foreach (var source in sources)
            {
                var local = part.Sources.FirstOrDefault(s => s.Id == source.Id);
                if (local is null) source.Events.Add(new(offset, source.Events.LastOrDefault()?.Position ?? System.Numerics.Vector3.Zero, 0, 0));
                else
                {
                    if (part.Segment.LeadingSilenceFrames > 0) source.Events.Add(new(offset, local.Events[0].Position, 0, 0));
                    foreach (var e in local.Events.Where(e => e.At < part.AudioFrames))
                        source.Events.Add(e with { At = checked(offset + part.Segment.LeadingSilenceFrames + e.At), Ramp = e.At == 0 ? 0 : e.Ramp });
                    if (part.Segment.TrailingSilenceFrames > 0)
                        source.Events.Add(new(offset + part.Segment.LeadingSilenceFrames + part.AudioFrames, source.Events[^1].Position, 0, 0));
                }
            }
            offset += part.Segment.Frames;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial", xmlPath = partial + ".xml";
        try
        {
            await Task.Run(() => WriteXml(xmlPath, sources, totalFrames, album, ct), ct);
            long xmlBytes = new FileInfo(xmlPath).Length;
            if (xmlBytes > 128L * 1024 * 1024) throw new NotSupportedException("ADM metadata exceeds 128 MiB; split the title before import.");
            int channels = sources.Count; long audioBytes = checked(totalFrames * channels * 3);
            byte[] chna = Chna(sources), chapters = JsonSerializer.SerializeToUtf8Bytes(album, SpatialJson.Options), fmt = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
            BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), 48000); BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), 48000 * channels * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * 3)); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 24);
            long Length(long n) => 8 + n + (n & 1);
            long totalBytes = 12 + 36 + Length(fmt.Length) + Length(xmlBytes) + Length(chna.Length) + Length(chapters.Length) + 8 + audioBytes + (audioBytes & 1);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                writer.Write("BW64"u8); writer.Write(uint.MaxValue); writer.Write("WAVEds64"u8); writer.Write(28u);
                writer.Write((ulong)(totalBytes - 8)); writer.Write((ulong)audioBytes); writer.Write((ulong)totalFrames); writer.Write(0u);
                void Chunk(string name, byte[] data) { writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write((uint)data.Length); writer.Write(data); if ((data.Length & 1) != 0) writer.Write((byte)0); }
                Chunk("fmt ", fmt); writer.Write("axml"u8); writer.Write((uint)xmlBytes);
                using (var xml = File.OpenRead(xmlPath)) await xml.CopyToAsync(output, ct);
                if ((xmlBytes & 1) != 0) writer.Write((byte)0);
                Chunk("chna", chna); Chunk("dclc", chapters); writer.Write("data"u8); writer.Write(uint.MaxValue);
                long written = 0, notified = 0; byte[] inputBytes = new byte[8192 * 32 * 3], mapped = new byte[8192 * channels * 3];
                foreach (var part in parts)
                {
                    await using var input = new FileStream(part.Audio, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                    input.Position = part.AudioFormat.Offset;
                    var map = part.Sources.Select(s => sources.FindIndex(target => target.Id == s.Id)).ToArray();
                    bool identity = map.Length == channels && map.Select((target, i) => target == i).All(x => x);
                    await Silence(part.Segment.LeadingSilenceFrames);
                    long remaining = part.AudioFrames;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested(); int frames = (int)Math.Min(8192, remaining), bytes = frames * part.AudioFormat.Channels * 3;
                        await input.ReadExactlyAsync(inputBytes.AsMemory(0, bytes), ct);
                        if (!part.AudioFormat.LittleEndian) for (int i = 0; i < bytes; i += 3) (inputBytes[i], inputBytes[i + 2]) = (inputBytes[i + 2], inputBytes[i]);
                        if (identity) await output.WriteAsync(inputBytes.AsMemory(0, bytes), ct);
                        else
                        {
                            Array.Clear(mapped, 0, frames * channels * 3);
                            for (int f = 0; f < frames; f++) for (int c = 0; c < map.Length; c++)
                                Buffer.BlockCopy(inputBytes, (f * map.Length + c) * 3, mapped, (f * channels + map[c]) * 3, 3);
                            await output.WriteAsync(mapped.AsMemory(0, frames * channels * 3), ct);
                        }
                        remaining -= frames; written += frames; Notify();
                    }
                    await Silence(part.Segment.TrailingSilenceFrames);
                }
                async Task Silence(long frames)
                {
                    if (frames <= 0) return; Array.Clear(mapped);
                    while (frames > 0) { ct.ThrowIfCancellationRequested(); int n = (int)Math.Min(8192, frames);
                        await output.WriteAsync(mapped.AsMemory(0, n * channels * 3), ct); written += n; frames -= n; Notify(); }
                }
                void Notify()
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (written == totalFrames || System.Diagnostics.Stopwatch.GetElapsedTime(notified, now).TotalMilliseconds >= 150)
                    { notified = now; progress?.Report(new(written / (double)totalFrames, "Writing complete position-aware ADM…")); }
                }
                if ((audioBytes & 1) != 0) writer.Write((byte)0); writer.Flush(); await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            // Validate metadata and all declared PCM bounds before publishing the completed file.
            var verified = AdmWave.Open(partial);
            if (verified.Scene.DurationSamples != totalFrames || verified.Channels != sources.Count) throw new InvalidDataException("Combined ADM validation failed.");
            File.Move(partial, destination);
            SpatialJson.Save(destination + ".import.json", new { Bridge = Version, PlaylistBridge = PlaylistVersion, Decoder = "truehdd 0.6.2",
                Frames = totalFrames, Channels = channels, Segments = segments.Count,
                Timeline = segments.Select(s => new { s.Frames, s.SourceStartTicks, s.LeadingSilenceFrames, s.TrailingSilenceFrames, s.TrimEndFrames }),
                TimelinePolicy = "Exact MPLS timeline. Silence is allowed only for source timestamp regions without audio, verified by the importer; end trimming is limited to one codec access unit beyond the requested endpoint. No resampling or decoder-error concealment.",
                Sources = sources.Select(s => new { s.Id, s.Name, s.Bed, Events = s.Events.Count }), Warnings = warnings.Distinct().ToArray(), Album = album });
        }
        finally { if (File.Exists(partial)) File.Delete(partial); if (File.Exists(xmlPath)) File.Delete(xmlPath); }
    }
}
