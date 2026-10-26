using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Disc2Flac;

public sealed record AdmChapter(long StartSample, string Title, string? Artist = null);
public sealed record AdmAlbumMetadata(string Album, string? Artist, string? Origin, AdmChapter[] Chapters)
{
    public string? DiscTitle { get; init; }
    public string? Date { get; init; }
    public string? Genre { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
}

/// <summary>Input adapter for truehdd 0.6.2 DAMF output. No audio codec or protection processing here.</summary>
public static partial class DamfImporter
{
    public const string Version = "truehdd-DAMF-point-source-2";
    sealed record Event(long At, Vector3 Position, float Gain, int Ramp);
    sealed record Source(int Id, string Name, bool Bed, bool Lfe, List<Event> Events);
    sealed record Caf(long Offset, long Bytes, int Channels, bool LittleEndian)
    { public long Frames => Bytes / (Channels * 3); }
    static double Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
        ? n : throw new InvalidDataException("Nonfinite DAMF number: " + s);
    static string Scalar(string s) => s.StartsWith('"') ? JsonSerializer.Deserialize<string>(s)! : s.StartsWith('\'') && s.EndsWith('\'') ? s[1..^1].Replace("''", "'") : s;
    static string LocalReference(string parent, string value)
    {
        value = Scalar(value);
        if (Path.IsPathRooted(value) || Path.GetFileName(value) != value || value is "." or "..") throw new InvalidDataException("DAMF references must be sibling filenames.");
        return Path.Combine(Path.GetDirectoryName(parent)!, value);
    }
    static Caf ReadCaf(string path)
    {
        using var f = File.OpenRead(path); Span<byte> header = stackalloc byte[32]; f.ReadExactly(header[..8]);
        if (!header[..8].SequenceEqual("caff\0\u0001\0\0"u8)) throw new InvalidDataException("Unsupported CAF version.");
        int channels = 0, flags = 0; long offset = 0, bytes = 0;
        while (f.Position < f.Length)
        {
            f.ReadExactly(header[..12]); string id = Encoding.ASCII.GetString(header[..4]); long n = BinaryPrimitives.ReadInt64BigEndian(header[4..]); long at = f.Position;
            if (n < 0 || n > f.Length - at) throw new InvalidDataException("Truncated CAF chunk.");
            if (id == "desc")
            {
                if (channels != 0 || n != 32) throw new InvalidDataException("Ambiguous CAF description."); f.ReadExactly(header);
                double rate = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(header)); flags = BinaryPrimitives.ReadInt32BigEndian(header[12..]);
                channels = BinaryPrimitives.ReadInt32BigEndian(header[24..]);
                if (rate != 48000 || !header.Slice(8, 4).SequenceEqual("lpcm"u8) || flags is not (0 or 2) || channels is < 1 or > 32 ||
                    BinaryPrimitives.ReadInt32BigEndian(header[16..]) != channels * 3 || BinaryPrimitives.ReadInt32BigEndian(header[20..]) != 1 || BinaryPrimitives.ReadInt32BigEndian(header[28..]) != 24)
                    throw new NotSupportedException("Atmos import accepts truehdd 48 kHz packed integer 24-bit CAF, at most 32 sources.");
            }
            if (id == "data") { if (offset != 0 || n < 4) throw new InvalidDataException("Ambiguous CAF data."); f.ReadExactly(header[..4]); if (BinaryPrimitives.ReadInt32BigEndian(header) != 0) throw new NotSupportedException("CAF edits unsupported."); offset = at + 4; bytes = n - 4; }
            f.Position = at + n;
        }
        if (channels == 0 || offset == 0 || bytes == 0 || bytes % (channels * 3) != 0) throw new InvalidDataException("Incomplete CAF audio.");
        return new(offset, bytes, channels, flags == 2);
    }
    static (string Audio, string Metadata, List<Source> Sources) ReadRoot(string path, List<string> warnings)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("DAMF header too large.");
        var lines = File.ReadAllLines(path); string audio = "", metadata = ""; var sources = new List<Source>(); bool objects = false; string? bed = null; int presentations = 0;
        if (!lines.Contains("version: 0.5.1") || !lines.Any(l => l.Trim() == "creationTool: truehdd")) throw new NotSupportedException("Use external truehdd 0.6.2 DAMF output.");
        foreach (var line in lines)
        {
            var s = line.Trim();
            if (s == "- type: home") presentations++;
            if (s.StartsWith("audio: ")) audio = LocalReference(path, s[7..]);
            if (s.StartsWith("metadata: ")) metadata = LocalReference(path, s[10..]);
            if (s.StartsWith("offset: ") && Number(s[8..]) != 0) throw new NotSupportedException("Nonzero DAMF timeline offset unsupported.");
            if (s.StartsWith("ffoa:") || s.StartsWith("trimMode:") || s.StartsWith("warpMode:")) warnings.Add("Delivery trims / warp / timecode are not applied by the point-source renderer.");
            if (s.StartsWith("objects:")) objects = true;
            if (s.StartsWith("- channel: ")) bed = Scalar(s[11..]);
            string? value = s.StartsWith("- ID: ") ? s[6..] : s.StartsWith("ID: ") ? s[4..] : null;
            if (value is not null)
            {
                int id = checked((int)Number(value)); if (sources.Any(x => x.Id == id)) throw new InvalidDataException("Duplicate DAMF source ID.");
                if (!objects && bed is null) throw new InvalidDataException("Missing bed label.");
                sources.Add(new(id, objects ? $"Object {id}" : bed!, !objects, !objects && bed == "LFE", [])); bed = null;
            }
        }
        if (presentations != 1 || sources.Count is < 1 or > 32 || audio == "" || metadata == "") throw new NotSupportedException("A single DAMF home presentation with 1–32 sources is required.");
        return (audio, metadata, sources);
    }
    static Vector3 BedPosition(string code) => code switch
    {
        "L" => new(-.5f, 0, .8660254f), "R" => new(.5f, 0, .8660254f), "C" or "LFE" => new(0, 0, 1),
        "Lss" => new(-1, 0, 0), "Rss" => new(1, 0, 0), "Lrs" => new(-.7071068f, 0, -.7071068f), "Rrs" => new(.7071068f, 0, -.7071068f),
        "Lts" => new(-.7071068f, .7071068f, 0), "Rts" => new(.7071068f, .7071068f, 0),
        _ => throw new NotSupportedException("Unsupported DAMF bed label: " + code)
    };
    static void ReadEvents(string path, List<Source> sources, long frames, List<string> warnings, CancellationToken ct, long? committedBefore = null)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("DAMF metadata exceeds 256 MiB.");
        var fields = new Dictionary<string, string>(); var byId = sources.ToDictionary(s => s.Id); int currentId = -1; long time = 0; int count = 0;
        var state = sources.ToDictionary(s => s.Id, s => (Position: s.Bed ? BedPosition(s.Name) : Vector3.Zero, Gain: 1f, Active: true, Ramp: 0));
        void Flush()
        {
            if (fields.Count == 0) return; ct.ThrowIfCancellationRequested();
            if (++count > 2_000_000) throw new InvalidDataException("Too many DAMF events.");
            if (fields.TryGetValue("ID", out var id)) currentId = checked((int)Number(id));
            if (!byId.TryGetValue(currentId, out var src)) throw new InvalidDataException("Unmapped DAMF object ID.");
            if (fields.TryGetValue("samplePos", out var at)) time = checked((long)Number(at));
            // A live writer may be in the middle of its newest event. PCM is committed only
            // after its metadata, and the live reader keeps an additional safety margin.
            if (committedBefore is { } horizon && time >= horizon) { fields.Clear(); return; }
            if (time < 0 || time >= frames || src.Events.Count > 200000 || src.Events.Count > 0 && time < src.Events[^1].At) throw new InvalidDataException("DAMF event outside the PCM timeline.");
            var st = state[currentId];
            if (fields.TryGetValue("pos", out var p))
            {
                double[] xyz = JsonSerializer.Deserialize<double[]>(p)!;
                if (xyz.Length != 3 || xyz.Any(v => !double.IsFinite(v) || Math.Abs(v) > 4)) throw new InvalidDataException("Invalid DAMF coordinates.");
                st.Position = new((float)xyz[0], (float)xyz[2], (float)xyz[1]);
            }
            if (fields.TryGetValue("gain", out var gain)) st.Gain = gain == "-inf" ? 0 : (float)Math.Pow(10, Number(gain) / 20);
            if (!float.IsFinite(st.Gain) || st.Gain > 16) throw new InvalidDataException("DAMF gain outside supported range.");
            if (fields.TryGetValue("active", out var active)) st.Active = active == "true" ? true : active == "false" ? false : throw new InvalidDataException("Invalid active flag.");
            if (fields.TryGetValue("rampLength", out var ramp)) st.Ramp = checked((int)Number(ramp));
            if (st.Ramp is < 0 or > 48000) throw new InvalidDataException("Invalid DAMF ramp.");
            foreach (var pair in fields)
            {
                bool unsupported = pair.Key switch
                {
                    "size" or "decorr" or "screenFactor" => Number(pair.Value) != 0,
                    "size3D" => JsonSerializer.Deserialize<double[]>(pair.Value)!.Any(v => v != 0),
                    "snap" => pair.Value != "false", "zones" => pair.Value != "all",
                    "depthFactor" => Number(pair.Value) != .25,
                    "headTrackMode" => pair.Value != "undefined", "binauralRenderMode" => pair.Value is not ("undefined" or "off"),
                    "trimBypass" => pair.Value != "false",
                    "ID" or "samplePos" or "pos" or "gain" or "active" or "rampLength" or "importance" or "dialog" or "music" or "elevation" => false,
                    _ => true
                };
                if (unsupported) warnings.Add("Point-source approximation: " + pair.Key + " is retained only in the decoder metadata, not rendered.");
            }
            state[currentId] = st;
            var value = new Event(time, st.Position, st.Active ? st.Gain : 0, st.Ramp);
            if (src.Events.Count > 0 && src.Events[^1].At == time) src.Events[^1] = value; else src.Events.Add(value);
            fields.Clear();
        }
        bool started = false;
        IEnumerable<string> lines = committedBefore is null ? File.ReadLines(path) : SharedLines(path);
        foreach (var line in lines)
        {
            if (line.Length > 4096) throw new InvalidDataException("Oversized DAMF metadata line.");
            if (line == "sampleRate: 48000") continue;
            if (line == "events:") { started = true; continue; }
            if (!started || string.IsNullOrWhiteSpace(line)) continue;
            string s;
            if (line.StartsWith("  - ")) { Flush(); s = line[4..]; }
            else if (line.StartsWith("    ")) s = line[4..]; else throw new InvalidDataException("Unsupported DAMF YAML structure.");
            int colon = s.IndexOf(':'); if (colon < 1 || !fields.TryAdd(s[..colon], Scalar(s[(colon + 1)..].Trim()))) throw new InvalidDataException("Duplicate or malformed DAMF field.");
        }
        Flush();
        foreach (var src in sources)
        {
            if (src.Events.Count == 0) { if (!src.Bed) throw new InvalidDataException("Object lacks initial metadata."); src.Events.Add(new(0, BedPosition(src.Name), 1, 0)); }
            if (src.Events[0].At != 0) throw new InvalidDataException("Missing initial source state; refusing to guess the pre-roll.");
        }
    }
    static string[] SharedLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("DAMF metadata exceeds 256 MiB.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        int end = Array.LastIndexOf(bytes, (byte)'\n');
        return end < 0 ? [] : Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n');
    }
    public static SpatialScene ReadLiveScene(string damf, long totalFrames, long committedFrames, CancellationToken token)
    {
        var warnings = new List<string>(); var root = ReadRoot(damf, warnings);
        ReadEvents(root.Metadata, root.Sources, totalFrames, warnings, token, committedFrames);
        var sources = root.Sources.Select((s, i) => new SpatialSource("DAMF_" + s.Id, s.Name, i,
            s.Bed ? SpatialSourceKind.ChannelBed : SpatialSourceKind.Object, 0, totalFrames, Blocks(s, totalFrames), s.Lfe)).ToArray();
        return new(48000, totalFrames, sources, "TrueHD Atmos · decoded point sources", warnings.Distinct().ToArray());
    }
    static SpatialKeyframe[] Blocks(Source source, long frames)
    {
        var result = new List<SpatialKeyframe>(); Vector3 actual = source.Events[0].Position; float gain = source.Events[0].Gain;
        for (int i = 0; i < source.Events.Count; i++)
        {
            var e = source.Events[i]; long end = i + 1 < source.Events.Count ? source.Events[i + 1].At : frames; long length = end - e.At;
            if (i == 0 || e.Ramp == 0) { actual = e.Position; gain = e.Gain; }
            long ramp = i == 0 ? 0 : Math.Min(length, e.Ramp);
            if (ramp > 0)
            {
                float t = ramp / (float)e.Ramp; var target = Vector3.Lerp(actual, e.Position, t); var targetGain = float.Lerp(gain, e.Gain, t);
                result.Add(new(e.At, ramp, target, targetGain, true, ramp)); actual = target; gain = targetGain;
            }
            if (length > ramp)
            {
                actual = e.Position; gain = e.Gain; result.Add(new(e.At + ramp, length - ramp, actual, gain));
            }
        }
        return result.ToArray();
    }
    public static string Time(long sample)
    {
        long seconds = Math.DivRem(sample, 48000, out var fraction);
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}.{fraction}S48000";
    }
    public static async Task WriteAsync(string damf, string destination, AdmAlbumMetadata album, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        damf = Path.GetFullPath(damf); destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("ADM destination already exists.");
        var warnings = new List<string>(); var parsed = ReadRoot(damf, warnings); var caf = ReadCaf(parsed.Audio);
        if (parsed.Sources.Count != caf.Channels) throw new InvalidDataException("DAMF IDs and CAF PCM track count disagree.");
        ReadEvents(parsed.Metadata, parsed.Sources, caf.Frames, warnings, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial", xmlPath = partial + ".xml";
        try
        {
            await Task.Run(() => WriteXml(xmlPath, parsed.Sources, caf.Frames, album, ct), ct);
            long xmlBytes = new FileInfo(xmlPath).Length; if (xmlBytes > 128L * 1024 * 1024) throw new NotSupportedException("ADM point-source metadata exceeds 128 MiB; split the title before import.");
            byte[] chna = Chna(parsed.Sources), chapters = JsonSerializer.SerializeToUtf8Bytes(album, SpatialJson.Options);
            byte[] fmt = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)caf.Channels);
            BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), 48000); BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), 48000 * caf.Channels * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(caf.Channels * 3)); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 24);
            long ChunkLength(long n) => 8 + n + (n & 1);
            long total = 12 + 36 + ChunkLength(fmt.Length) + ChunkLength(xmlBytes) + ChunkLength(chna.Length) + ChunkLength(chapters.Length) + 8 + caf.Bytes + (caf.Bytes & 1);
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            writer.Write("BW64"u8); writer.Write(uint.MaxValue); writer.Write("WAVE"u8); writer.Write("ds64"u8); writer.Write(28u);
            writer.Write((ulong)(total - 8)); writer.Write((ulong)caf.Bytes); writer.Write((ulong)caf.Frames); writer.Write(0u);
            void Header(string id, long bytes) { writer.Write(Encoding.ASCII.GetBytes(id)); writer.Write((uint)bytes); }
            void Chunk(string id, byte[] bytes) { Header(id, bytes.Length); writer.Write(bytes); if ((bytes.Length & 1) != 0) writer.Write((byte)0); }
            Chunk("fmt ", fmt); Header("axml", xmlBytes); using (var xml = File.OpenRead(xmlPath)) await xml.CopyToAsync(output, ct); if ((xmlBytes & 1) != 0) writer.Write((byte)0);
            Chunk("chna", chna); Chunk("dclc", chapters); writer.Write("data"u8); writer.Write(uint.MaxValue);
            using var input = new FileStream(parsed.Audio, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true); input.Position = caf.Offset;
            byte[] buffer = new byte[3 * 1024 * 1024]; long remaining = caf.Bytes;
            long lastProgress = 0;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested(); int n = (int)Math.Min(buffer.Length, remaining); await input.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
                if (!caf.LittleEndian) for (int i = 0; i < n; i += 3) (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
                await output.WriteAsync(buffer.AsMemory(0, n), ct); remaining -= n;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (remaining == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress, now).TotalMilliseconds >= 150)
                { lastProgress = now; progress?.Report(new(1 - remaining / (double)caf.Bytes, "Writing position-aware ADM…")); }
            }
            if ((caf.Bytes & 1) != 0) writer.Write((byte)0); writer.Flush(); await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested(); File.Move(partial, destination);
            SpatialJson.Save(destination + ".import.json", new { Bridge = Version, Decoder = "truehdd 0.6.2", Frames = caf.Frames, caf.Channels, Sources = parsed.Sources.Select(s => new { s.Id, s.Name, s.Bed, Events = s.Events.Count }), Warnings = warnings.Distinct().ToArray(), Album = album });
        }
        finally { if (File.Exists(partial)) File.Delete(partial); if (File.Exists(xmlPath)) File.Delete(xmlPath); }
    }
    static byte[] Chna(List<Source> sources)
    {
        using var m = new MemoryStream(); using var w = new BinaryWriter(m); w.Write((ushort)sources.Count); w.Write((ushort)sources.Count);
        for (int i = 0; i < sources.Count; i++) { string label = sources[i].Bed ? "0001" : "0003", id = $"{label}{0x1100 + i:X4}"; w.Write((ushort)(i + 1)); w.Write(Encoding.ASCII.GetBytes($"ATU_{i + 1:X8}")); w.Write(Encoding.ASCII.GetBytes($"AT_{id}_01")); w.Write(Encoding.ASCII.GetBytes($"AP_{id}")); w.Write((byte)0); }
        return m.ToArray();
    }
    static void WriteXml(string path, List<Source> sources, long frames, AdmAlbumMetadata album, CancellationToken ct)
    {
        using var w = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        const string ns = "urn:ebu:metadata-schema:ebuCore_2017";
        void Start(string name, params string[] attributes) { w.WriteStartElement(name, ns); for (int i = 0; i < attributes.Length; i += 2) w.WriteAttributeString(attributes[i], attributes[i + 1]); }
        void Element(string name, string value) => w.WriteElementString(name, ns, value);
        Start("ebuCoreMain"); Start("coreMetadata"); Start("format"); Start("audioFormatExtended", "version", "ITU-R_BS.2076-2");
        Start("audioProgramme", "audioProgrammeID", "APR_1001", "audioProgrammeName", album.Album); Element("audioContentIDRef", "ACO_1001"); w.WriteEndElement();
        Start("audioContent", "audioContentID", "ACO_1001", "audioContentName", "Decoded spatially coded point sources");
        for (int i = 0; i < sources.Count; i++) Element("audioObjectIDRef", $"AO_{0x1100 + i:X4}"); w.WriteEndElement();
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var source = sources[i]; string label = source.Bed ? "0001" : "0003", id = $"{label}{0x1100 + i:X4}", definition = source.Bed ? "DirectSpeakers" : "Objects";
            Start("audioObject", "audioObjectID", $"AO_{0x1100 + i:X4}", "audioObjectName", source.Name, "start", Time(0), "duration", Time(frames)); Element("audioPackFormatIDRef", "AP_" + id); Element("audioTrackUIDRef", $"ATU_{i + 1:X8}"); w.WriteEndElement();
            Start("audioPackFormat", "audioPackFormatID", "AP_" + id, "audioPackFormatName", source.Name, "typeDefinition", definition, "typeLabel", label); Element("audioChannelFormatIDRef", "AC_" + id); w.WriteEndElement();
            Start("audioChannelFormat", "audioChannelFormatID", "AC_" + id, "audioChannelFormatName", source.Name, "typeDefinition", definition, "typeLabel", label);
            if (source.Lfe) { Start("frequency", "typeDefinition", "lowPass"); w.WriteString("120"); w.WriteEndElement(); }
            int b = 0;
            foreach (var block in Blocks(source, frames))
            {
                if ((b++ & 1023) == 0) ct.ThrowIfCancellationRequested();
                Start("audioBlockFormat", "audioBlockFormatID", $"AB_{id}_{b:X8}", "rtime", Time(block.StartSample), "duration", Time(block.DurationSamples));
                Element("cartesian", "1");
                foreach (var (axis, value) in new[] { ("X", block.Position.X), ("Y", block.Position.Z), ("Z", block.Position.Y) }) { Start("position", "coordinate", axis); w.WriteString(value.ToString("R", CultureInfo.InvariantCulture)); w.WriteEndElement(); }
                Start("jumpPosition", "interpolationLength", Time(block.InterpolationSamples)); w.WriteString("1"); w.WriteEndElement(); Element("gain", block.Gain.ToString("R", CultureInfo.InvariantCulture));
                if (source.Lfe) Element("speakerLabel", "urn:itu:bs:2051:0:speaker:LFE"); w.WriteEndElement();
            }
            w.WriteEndElement();
            Start("audioStreamFormat", "audioStreamFormatID", "AS_" + id, "audioStreamFormatName", source.Name, "formatDefinition", "PCM", "formatLabel", "0001"); Element("audioChannelFormatIDRef", "AC_" + id); Element("audioTrackFormatIDRef", "AT_" + id + "_01"); w.WriteEndElement();
            Start("audioTrackFormat", "audioTrackFormatID", "AT_" + id + "_01", "audioTrackFormatName", source.Name, "formatDefinition", "PCM", "formatLabel", "0001"); Element("audioStreamFormatIDRef", "AS_" + id); w.WriteEndElement();
            Start("audioTrackUID", "UID", $"ATU_{i + 1:X8}", "sampleRate", "48000", "bitDepth", "24"); Element("audioTrackFormatIDRef", "AT_" + id + "_01"); Element("audioPackFormatIDRef", "AP_" + id); w.WriteEndElement();
        }
        w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
    }
}
