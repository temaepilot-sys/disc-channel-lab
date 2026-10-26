using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Disc2Flac;

/// <summary>Exports current edits without changing a prepared ADM, its PCM, or its spatial scene.</summary>
public static class AdmMetadataExport
{
    public static AdmAlbumMetadata FromTracks(DiscAnalysis disc, PlaylistInfo playlist, IReadOnlyList<TrackRow> tracks)
    {
        var original = playlist.Clips.FirstOrDefault()?.File?.Adm?.Album;
        var artists = tracks.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToArray();
        return new(playlist.TitleName, !string.IsNullOrWhiteSpace(disc.Artist) ? disc.Artist : artists.Length == 1 ? artists[0] : null, original?.Origin ?? disc.Root,
            tracks.Select(t => new AdmChapter(ConversionService.ToSample(t.StartTicks, 48000), t.Title, t.Artist)).ToArray())
        { DiscTitle = disc.AlbumTitle, Date = disc.Date, Genre = disc.Genre, Publisher = disc.Publisher, Description = disc.Description };
    }

    sealed record Chunk(string Id, uint HeaderSize, long Offset, long Length);

    public static async Task WriteAsync(string source, string destination, AdmAlbumMetadata album,
        IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("ADM destination already exists.");
        var wave = AdmWave.Open(source);
        if (album.Chapters is null || album.Chapters.Length > 10000 || album.Chapters.Any(c => c.StartSample < 0 || c.StartSample >= wave.Scene.DurationSamples) ||
            !album.Chapters.Select(c => c.StartSample).SequenceEqual(album.Chapters.Select(c => c.StartSample).Distinct().Order()))
            throw new InvalidDataException("ADM chapters are out of order or outside PCM.");
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(album, SpatialJson.Options);
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("ADM chapter metadata exceeds 4 MiB.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string pending = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            using (var reader = new BinaryReader(input, Encoding.UTF8, true))
            {
                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4)); reader.ReadUInt32(); reader.ReadUInt32();
                var chunks = new List<Chunk>(); var sizes = new Dictionary<string, Queue<long>>();
                long dataBytes = -1; byte[]? ds64 = null, xml = null;
                while (input.Position + 8 <= input.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (chunks.Count >= 10000) throw new InvalidDataException("Too many RIFF chunks.");
                    string id = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint small = reader.ReadUInt32(); long at = input.Position;
                    long n = small == uint.MaxValue ? id == "data" ? dataBytes : sizes.TryGetValue(id, out var q) && q.Count > 0 ? q.Dequeue() : -1 : small;
                    if (n < 0 || n > input.Length - at) throw new InvalidDataException("Invalid RIFF chunk size: " + id);
                    chunks.Add(new(id, small, at, n));
                    if (id == "ds64")
                    {
                        if (ds64 is not null || n is < 28 or > 1024 * 1024) throw new InvalidDataException("Invalid ds64 chunk.");
                        ds64 = reader.ReadBytes((int)n); dataBytes = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(ds64.AsSpan(8)));
                        uint count = BinaryPrimitives.ReadUInt32LittleEndian(ds64.AsSpan(24));
                        if (count > (n - 28) / 12) throw new InvalidDataException("Invalid ds64 table.");
                        for (int i = 0; i < count; i++)
                        {
                            int p = 28 + i * 12; string key = Encoding.ASCII.GetString(ds64, p, 4);
                            if (!sizes.TryGetValue(key, out var queue)) sizes[key] = queue = new();
                            queue.Enqueue(checked((long)BinaryPrimitives.ReadUInt64LittleEndian(ds64.AsSpan(p + 4))));
                        }
                    }
                    else if (id == "axml")
                    {
                        if (n > 128 * 1024 * 1024) throw new InvalidDataException("ADM XML exceeds metadata limit.");
                        byte[] original = reader.ReadBytes((int)n);
                        using var memory = new MemoryStream(original);
                        using var xr = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 * 1024 });
                        var doc = XDocument.Load(xr, LoadOptions.PreserveWhitespace);
                        var programme = doc.Descendants().Single(e => e.Name.LocalName == "audioProgramme" && (string?)e.Attribute("audioProgrammeID") == wave.ProgrammeId);
                        if ((string?)programme.Attribute("audioProgrammeName") == album.Album) xml = original;
                        else
                        {
                            programme.SetAttributeValue("audioProgrammeName", album.Album);
                            using var updated = new MemoryStream(); doc.Save(updated, SaveOptions.DisableFormatting); xml = updated.ToArray();
                            if (xml.Length > 128 * 1024 * 1024) throw new InvalidDataException("Updated ADM XML exceeds metadata limit.");
                        }
                    }
                    input.Position = checked(at + n + (n & 1));
                }
                if (xml is null || magic != "RIFF" && ds64 is null) throw new InvalidDataException("ADM metadata/header is missing.");
                long ChunkBytes(long n) => checked(8 + n + (n & 1));
                long total = checked(12 + ChunkBytes(metadata.Length) + chunks.Where(c => c.Id != "dclc").Sum(c => ChunkBytes(c.Id == "axml" ? xml.Length : c.Length)));
                bool addDs64 = magic == "RIFF" && total - 8 >= uint.MaxValue;
                if (addDs64)
                {
                    magic = "BW64"; total = checked(total + 36); ds64 = new byte[28];
                    BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(8), (ulong)wave.DataBytes);
                    BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(16), (ulong)wave.Scene.DurationSamples);
                }
                if (ds64 is not null) BinaryPrimitives.WriteUInt64LittleEndian(ds64, (ulong)(total - 8));
                using var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                writer.Write(Encoding.ASCII.GetBytes(magic)); writer.Write(magic == "RIFF" ? checked((uint)(total - 8)) : uint.MaxValue); writer.Write("WAVE"u8);
                void WriteChunk(string id, byte[] content)
                { writer.Write(Encoding.ASCII.GetBytes(id)); writer.Write((uint)content.Length); writer.Write(content); if ((content.Length & 1) != 0) writer.Write((byte)0); }
                if (ds64 is not null) WriteChunk("ds64", ds64);
                WriteChunk("dclc", metadata);
                progress?.Report(new(0, "Saving ADM with current track information…"));
                byte[] buffer = new byte[1024 * 1024]; long notified = 0;
                foreach (var chunk in chunks)
                {
                    token.ThrowIfCancellationRequested();
                    if (chunk.Id == "dclc") continue;
                    if (chunk.Id == "ds64") continue;
                    if (chunk.Id == "axml") { WriteChunk("axml", xml); continue; }
                    writer.Write(Encoding.ASCII.GetBytes(chunk.Id)); writer.Write(chunk.HeaderSize);
                    input.Position = chunk.Offset; long left = chunk.Length + (chunk.Length & 1);
                    while (left > 0)
                    {
                        int n = (int)Math.Min(left, buffer.Length); await input.ReadExactlyAsync(buffer.AsMemory(0, n), token);
                        await output.WriteAsync(buffer.AsMemory(0, n), token); left -= n;
                        if (output.Position - notified >= 64 * 1024 * 1024)
                        { notified = output.Position; progress?.Report(new(output.Position / (double)total, "Saving ADM with current track information…")); }
                    }
                }
                writer.Flush(); await output.FlushAsync(token);
                if (output.Length != total) throw new InvalidDataException("ADM export size mismatch.");
            }
            token.ThrowIfCancellationRequested();
            var saved = AdmWave.Open(pending);
            if (saved.DataBytes != wave.DataBytes || saved.Scene.DurationSamples != wave.Scene.DurationSamples ||
                saved.Channels != wave.Channels || !JsonSerializer.SerializeToUtf8Bytes(saved.Album, SpatialJson.Options).AsSpan().SequenceEqual(metadata))
                throw new InvalidDataException("ADM export metadata verification failed.");
            token.ThrowIfCancellationRequested(); File.Move(pending, destination);
            progress?.Report(new(1, "Position-aware ADM and current track information saved."));
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
