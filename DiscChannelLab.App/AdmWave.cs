using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json;

namespace Disc2Flac;

public sealed record AdmProgramme(string Id, string Name) { public override string ToString() => Name; }
public sealed record AdmWave(string Path, int Channels, int Bits, bool Float, long DataOffset, long DataBytes,
    SpatialScene Scene, AdmProgramme[] Programmes, string ProgrammeId)
{
    const int MaxXml = 128 * 1024 * 1024;
    public AdmAlbumMetadata? Album { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ProgressiveAtmosSource? Progressive { get; init; }
    public static bool HasMetadata(string path)
    {
        if (!File.Exists(path)) return false;
        using var f = File.OpenRead(path); using var r = new BinaryReader(f);
        if (f.Length < 12) return false;
        string magic = Encoding.ASCII.GetString(r.ReadBytes(4)); r.ReadUInt32();
        if (magic is not ("RIFF" or "RF64" or "BW64") || Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") return false;
        long data = -1;
        for (int i = 0; i < 10000 && f.Position + 8 <= f.Length; i++)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4)); uint size = r.ReadUInt32(); long at = f.Position;
            if (id is "axml" or "chna") return true;
            long n = size == uint.MaxValue && id == "data" ? data : size;
            if (n < 0 || n > f.Length - at) return false;
            if (id == "ds64" && n >= 28) { r.ReadUInt64(); data = checked((long)r.ReadUInt64()); }
            f.Position = at + n + (n & 1);
        }
        return false;
    }
    static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
    static string Attr(XElement e, string name, string fallback = "") => (string?)e.Attribute(name) ?? fallback;
    static double Number(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
            throw new InvalidDataException("Invalid finite ADM number: " + value);
        return n;
    }
    public static long Time(string text)
    {
        // Decimal/rational sample fractions are converted once, never via double seconds.
        var parts = text.Split(':'); if (parts.Length != 3) throw new InvalidDataException("Invalid ADM time: " + text);
        decimal seconds;
        if (parts[2].Contains('S'))
        {
            var p = parts[2].Split(new[] { '.', 'S' });
            if (p.Length != 3 || decimal.Parse(p[2], CultureInfo.InvariantCulture) <= 0) throw new InvalidDataException("Invalid ADM fractional time.");
            seconds = decimal.Parse(p[0], CultureInfo.InvariantCulture) + decimal.Parse(p[1], CultureInfo.InvariantCulture) / decimal.Parse(p[2], CultureInfo.InvariantCulture);
        }
        else seconds = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
        decimal total = decimal.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + decimal.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + seconds;
        if (total < 0 || seconds >= 60 || Number(parts[1]) is < 0 or >= 60) throw new InvalidDataException("ADM time out of range.");
        return checked((long)decimal.Round(total * 48000, 0, MidpointRounding.AwayFromZero));
    }
    static XDocument Xml(Stream stream) => XDocument.Load(XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml, MaxCharactersFromEntities = 0 }));
    public static AdmWave Open(string path, string? programmeId = null)
    {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file, Encoding.ASCII, true);
        if (file.Length < 12) throw new InvalidDataException("Truncated RIFF header.");
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4)); reader.ReadUInt32();
        if (magic is not ("RIFF" or "RF64" or "BW64") || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a BWF/RF64/BW64 WAVE.");
        byte[]? fmt = null, chna = null, axml = null; AdmAlbumMetadata? album = null; long data = 0, bytes = 0, ds64Data = -1;
        var sizes = new Dictionary<string, Queue<long>>(); int chunks = 0;
        while (file.Position + 8 <= file.Length)
        {
            if (++chunks > 10000) throw new InvalidDataException("Too many RIFF chunks.");
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint small = reader.ReadUInt32();
            long length = small == uint.MaxValue ? id == "data" ? ds64Data : sizes.TryGetValue(id, out var q) && q.Count > 0 ? q.Dequeue() : -1 : small;
            long start = file.Position;
            if (length < 0 || length > file.Length - start) throw new InvalidDataException("Invalid RIFF chunk size: " + id);
            if (id == "ds64")
            {
                if (length < 28 || length > 1024 * 1024) throw new InvalidDataException("Invalid ds64 chunk.");
                reader.ReadUInt64(); ds64Data = checked((long)reader.ReadUInt64()); reader.ReadUInt64(); uint count = reader.ReadUInt32();
                if (count > (length - 28) / 12) throw new InvalidDataException("Invalid ds64 table.");
                for (int i = 0; i < count; i++) { string key = Encoding.ASCII.GetString(reader.ReadBytes(4)); long n = checked((long)reader.ReadUInt64()); if (!sizes.TryGetValue(key, out var queue)) sizes[key] = queue = new(); queue.Enqueue(n); }
            }
            else if (id is "fmt " or "chna" or "axml")
            {
                int max = id == "axml" ? MaxXml : 1024 * 1024;
                if (length > max) throw new InvalidDataException(id + " exceeds metadata limit.");
                byte[] content = reader.ReadBytes((int)length);
                if (id == "fmt ") { if (fmt is not null) throw new InvalidDataException("Duplicate fmt chunk."); fmt = content; }
                if (id == "chna") { if (chna is not null) throw new InvalidDataException("Duplicate chna chunk."); chna = content; }
                if (id == "axml") { if (axml is not null) throw new InvalidDataException("Duplicate axml chunk."); axml = content; }
            }
            else if (id == "data") { if (data != 0) throw new InvalidDataException("Multiple data chunks unsupported."); data = start; bytes = length; }
            else if (id == "dclc")
            {
                if (album is not null || length > 4 * 1024 * 1024) throw new InvalidDataException("Invalid ADM chapter metadata.");
                album = JsonSerializer.Deserialize<AdmAlbumMetadata>(reader.ReadBytes((int)length), SpatialJson.Options);
            }
            file.Position = checked(start + length + (length & 1));
        }
        if (fmt is null || fmt.Length < 16 || chna is null || chna.Length < 4 || axml is null || data == 0) throw new InvalidDataException("ADM requires fmt, data, axml and chna chunks.");
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(fmt), channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4)), align = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12)), bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
        if (format == 0xfffe)
        {
            if (fmt.Length < 40) throw new InvalidDataException("Truncated extensible format.");
            var subtype = new Guid(fmt.AsSpan(24,16));
            format = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                : subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : throw new NotSupportedException("Unsupported WAVE extensible subformat.");
        }
        if (rate != 48000 || channels is < 1 or > 64 || format is not (1 or 3) || bits is not (16 or 24 or 32) || format == 3 && bits != 32 || align != channels * (bits / 8) || bytes % align != 0)
            throw new NotSupportedException("ADM supports 48 kHz PCM 16/24/32-bit or float32, up to 64 stored tracks / 32 active sources.");
        long frames = bytes / align;
        if (album is not null && (album.Chapters is null || album.Chapters.Length > 10000 || album.Chapters.Any(c => c.StartSample < 0 || c.StartSample >= frames) ||
            !album.Chapters.Select(c => c.StartSample).SequenceEqual(album.Chapters.Select(c => c.StartSample).Distinct().Order()))) throw new InvalidDataException("ADM chapters are out of order or outside PCM.");
        var links = new Dictionary<string, (int Track, string Format, string Pack)>(StringComparer.OrdinalIgnoreCase);
        int trackCount = BinaryPrimitives.ReadUInt16LittleEndian(chna), uidCount = BinaryPrimitives.ReadUInt16LittleEndian(chna.AsSpan(2));
        if (trackCount != channels || uidCount > 4096 || chna.Length != 4 + uidCount * 40) throw new InvalidDataException("Invalid CHNA track/UID count.");
        string Ascii(int offset, int count) => Encoding.ASCII.GetString(chna, offset, count).TrimEnd('\0', ' ');
        for (int i = 0; i < uidCount; i++)
        {
            int p = 4 + i * 40, track = BinaryPrimitives.ReadUInt16LittleEndian(chna.AsSpan(p)) - 1;
            if (track < 0 || track >= channels || !links.TryAdd(Ascii(p + 2, 12), (track, Ascii(p + 14, 14), Ascii(p + 28, 11)))) throw new InvalidDataException("Invalid or duplicate CHNA UID.");
        }
        using var memory = new MemoryStream(axml); var doc = Xml(memory);
        using var common = typeof(AdmWave).Assembly.GetManifestResourceStream("SpatialLab.CommonDefinitions.xml")!;
        var definitions = Xml(common);
        var index = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        void Index(XDocument d, bool user)
        {
            foreach (var e in d.Descendants())
            {
                var id = e.Attributes().FirstOrDefault(a => a.Name.LocalName == (e.Name.LocalName == "audioTrackUID" ? "UID" : e.Name.LocalName + "ID"));
                if (id is null) continue;
                if (index.ContainsKey(id.Value)) throw new InvalidDataException("Duplicate/redefined ADM ID: " + id.Value);
                index.Add(id.Value, e);
            }
        }
        Index(definitions, false); Index(doc, true);
        XElement Resolve(string id, string type)
        {
            if (!index.TryGetValue(id, out var e) || e.Name.LocalName != type) throw new InvalidDataException("Unresolved " + type + ": " + id);
            return e;
        }
        string Ref(XElement e, string kind) => Children(e, kind + "IDRef").SingleOrDefault()?.Value.Trim() ?? throw new InvalidDataException("Missing " + kind + " reference.");
        var programmes = doc.Descendants().Where(e => e.Name.LocalName == "audioProgramme")
            .Select(e => new AdmProgramme(Attr(e, "audioProgrammeID"), Attr(e, "audioProgrammeName"))).ToArray();
        if (programmes.Length == 0) throw new InvalidDataException("ADM has no audioProgramme.");
        programmeId ??= programmes[0].Id; var programme = Resolve(programmeId, "audioProgramme");
        long programmeStart = Time(Attr(programme, "start", "00:00:00"));
        var sources = new List<SpatialSource>(); var warnings = new List<string>
        { "Objects + DirectSpeakers subset; point-source rendering, no width/diffuse/HOA. Position/HRTF updates: 256 samples (5.33 ms). Source activation/gain are sample-timed.",
          "ADM distance is normalized scene distance; no automatic inverse-distance gain. This is not an Atmos-certified renderer." };
        var visited = new HashSet<string>();
        void Object(string objectId, int depth, long parentStart, long parentEnd, float parentGain)
        {
            if (depth > 16 || !visited.Add(objectId)) throw new InvalidDataException("Cyclic/shared/nested ADM object graph exceeds supported depth: " + objectId);
            var obj = Resolve(objectId, "audioObject");
            if (Children(obj, "audioComplementaryObjectIDRef").Any() || Children(obj, "positionOffset").Any() || Attr(obj, "interact", "0") != "0")
                throw new NotSupportedException("Complementary objects, positionOffset and interactive objects require explicit ADM support.");
            // Nested audioObject start remains programme-relative (EBU ADM timing), never parent-relative.
            long start = checked(programmeStart + Time(Attr(obj, "start", "00:00:00")));
            long end = obj.Attribute("duration") is { } duration ? checked(start + Time(duration.Value)) : parentEnd;
            if (start < parentStart || start > frames || end < start || end > parentEnd) throw new InvalidDataException("ADM object time outside PCM/parent object.");
            float objectGain = parentGain * Gain(Children(obj, "gain").SingleOrDefault());
            foreach (var nested in Children(obj, "audioObjectIDRef")) Object(nested.Value.Trim(), depth + 1, start, end, objectGain);
            foreach (var uid in Children(obj, "audioTrackUIDRef"))
            {
                if (uid.Value.Trim() == "ATU_00000000") continue;
                if (!links.TryGetValue(uid.Value.Trim(), out var link)) throw new InvalidDataException("Missing CHNA UID: " + uid.Value);
                var xmlUid = Resolve(uid.Value.Trim(), "audioTrackUID");
                var xmlPack = Children(xmlUid, "audioPackFormatIDRef").SingleOrDefault()?.Value.Trim();
                if (xmlPack is not null && !xmlPack.Equals(link.Pack, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CHNA/AXML pack reference mismatch.");
                var formatRef = Children(xmlUid, "audioTrackFormatIDRef").SingleOrDefault()?.Value.Trim()
                    ?? Children(xmlUid, "audioChannelFormatIDRef").SingleOrDefault()?.Value.Trim();
                if (formatRef is not null && !formatRef.Equals(link.Format, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("CHNA/AXML track reference mismatch.");
                XElement channel;
                if (link.Format.StartsWith("AC_", StringComparison.OrdinalIgnoreCase)) channel = Resolve(link.Format, "audioChannelFormat");
                else
                {
                    var trackFormat = Resolve(link.Format, "audioTrackFormat");
                    var stream = Resolve(Ref(trackFormat, "audioStreamFormat"), "audioStreamFormat");
                    channel = Resolve(Ref(stream, "audioChannelFormat"), "audioChannelFormat");
                }
                var pack = Resolve(link.Pack, "audioPackFormat");
                if (!Children(pack, "audioChannelFormatIDRef").Any(e => e.Value.Trim().Equals(Attr(channel, "audioChannelFormatID"), StringComparison.OrdinalIgnoreCase)))
                    throw new NotSupportedException("Nested/matrix packs or channel/pack mismatch.");
                string type = Attr(channel, "typeDefinition", Attr(channel, "typeLabel"));
                bool direct = type is "DirectSpeakers" or "0001";
                if (!direct && type is not ("Objects" or "0003")) throw new NotSupportedException("Unsupported ADM type: " + type);
                var blocks = new List<SpatialKeyframe>();
                foreach (var block in Children(channel, "audioBlockFormat"))
                {
                    if (blocks.Count >= 250000) throw new InvalidDataException("Too many ADM blocks.");
                    string[] unsupported = ["width", "height", "depth", "diffuse", "objectDivergence", "channelLock", "zoneExclusion", "screenRef", "headLocked", "headphoneVirtualise"];
                    foreach (var key in unsupported)
                        if (Children(block, key).Any(e => e.HasElements || !double.TryParse(e.Value, out double v) || v != 0) || Attr(block, key, "0") != "0") throw new NotSupportedException("Unsupported ADM attribute: " + key);
                    long rtime = Time(Attr(block, "rtime", "00:00:00")), at = checked(start + rtime);
                    long durationSamples = block.Attribute("duration") is { } d ? Time(d.Value) : end - at;
                    var p = Children(block, "position").ToArray();
                    if (Children(block, "cartesian").Any(e => e.Value.Trim() is not ("0" or "1"))) throw new InvalidDataException("Invalid ADM cartesian flag.");
                    bool cartesian = Children(block, "cartesian").Any(e => e.Value.Trim() == "1") || p.Any(e => Attr(e, "coordinate") == "X");
                    double Coordinate(string axis, double? fallback = null)
                    {
                        var values = p.Where(e => Attr(e, "coordinate") == axis && e.Attribute("bound") is null).ToArray();
                        if (values.Length != 1) return fallback ?? throw new InvalidDataException("ADM position missing/ambiguous: " + axis);
                        return Number(values[0].Value);
                    }
                    if (p.Any(e => e.Attribute("screenEdgeLock") is not null)) throw new NotSupportedException("ADM screenEdgeLock is unsupported.");
                    Vector3 position = cartesian ? new((float)Coordinate("X"), (float)Coordinate("Z"), (float)Coordinate("Y"))
                        : new((float)Coordinate("azimuth"), (float)Coordinate("elevation"), (float)Coordinate("distance", 1));
                    if (cartesian ? position.LengthSquared() > 10000 : position.X is < -360 or > 360 || position.Y is < -90 or > 90 || position.Z is < 0 or > 100)
                        throw new InvalidDataException("ADM position outside supported bounds.");
                    var jump = Children(block, "jumpPosition").SingleOrDefault();
                    if (jump is not null && jump.Value.Trim() is not ("0" or "1")) throw new InvalidDataException("Invalid jumpPosition flag.");
                    bool isJump = direct || jump?.Value.Trim() == "1";
                    long interpolation = isJump && jump?.Attribute("interpolationLength") is { } il ? Time(il.Value) : 0;
                    if (interpolation > durationSamples || durationSamples < 0 || at < start || at > end) throw new InvalidDataException("ADM block/interpolation time outside object.");
                    blocks.Add(new(at, Math.Min(durationSamples, end - at), position, Gain(Children(block, "gain").SingleOrDefault()) * objectGain, isJump, interpolation, cartesian));
                }
                if (blocks.Count == 0) throw new InvalidDataException("ADM channel has no position blocks.");
                if (blocks[^1].StartSample + blocks[^1].DurationSamples < end) throw new NotSupportedException("ADM blocks do not cover the object duration.");
                for (int b = 1; b < blocks.Count; b++)
                    if (blocks[b].StartSample != blocks[b-1].StartSample + blocks[b-1].DurationSamples || blocks[b].Cartesian != blocks[b-1].Cartesian)
                        throw new NotSupportedException("Noncontiguous ADM blocks or changing coordinate type are unsupported.");
                bool lfe = direct && (Children(channel, "audioBlockFormat").SelectMany(e => Children(e, "speakerLabel")).Any(e => e.Value.EndsWith(":LFE", StringComparison.OrdinalIgnoreCase))
                    || Children(channel, "frequency").Any(e => Attr(e, "typeDefinition") == "lowPass" && Number(e.Value) <= 120));
                string id = objectId + "/" + uid.Value.Trim();
                sources.Add(new(id, Attr(obj, "audioObjectName", objectId) + " · " + Attr(channel, "audioChannelFormatName"), link.Track,
                    direct ? SpatialSourceKind.ChannelBed : SpatialSourceKind.Object, start, end, blocks.ToArray(), lfe));
                if (sources.Count > 32) throw new NotSupportedException("Supports up to 32 active source streams.");
            }
        }
        foreach (var contentRef in Children(programme, "audioContentIDRef"))
            foreach (var objectRef in Children(Resolve(contentRef.Value.Trim(), "audioContent"), "audioObjectIDRef")) Object(objectRef.Value.Trim(), 0, programmeStart, frames, 1);
        if (sources.Count == 0) throw new InvalidDataException("Programme has no supported sources.");
        return new(System.IO.Path.GetFullPath(path), channels, bits, format == 3, data, bytes,
            new(48000, frames, sources.ToArray(), "ADM Objects/DirectSpeakers", warnings.ToArray()), programmes, programmeId) { Album = album };
    }
    static float Gain(XElement? element)
    {
        if (element is null) return 1;
        double n = Number(element.Value); string unit = Attr(element, "gainUnit", "linear");
        if (unit == "dB") n = Math.Pow(10, n / 20); else if (unit != "linear") throw new NotSupportedException("Unknown ADM gain unit.");
        if (n is < 0 or > 16) throw new InvalidDataException("ADM gain exceeds supported limit."); return (float)n;
    }
    public async Task CopyPcmAsync(Stream destination, long firstFrame, long count, CancellationToken token)
    {
        int bytesPerFrame = Channels * Bits / 8; using var source = File.OpenRead(Path);
        source.Position = checked(DataOffset + firstFrame * bytesPerFrame);
        var bytes = new byte[256 * bytesPerFrame]; var output = new byte[256 * Channels * 4];
        while (count > 0)
        {
            int frames = (int)Math.Min(256, count); await source.ReadExactlyAsync(bytes.AsMemory(0, frames * bytesPerFrame), token);
            for (int i = 0; i < frames * Channels; i++)
            {
                var p = bytes.AsSpan(i * (Bits / 8));
                float v = Float ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p)) : Bits switch
                { 16 => BinaryPrimitives.ReadInt16LittleEndian(p) / 32768f, 24 => ((p[0] | p[1] << 8 | p[2] << 16) << 8 >> 8) / 8388608f, _ => BinaryPrimitives.ReadInt32LittleEndian(p) / 2147483648f };
                if (!float.IsFinite(v)) throw new InvalidDataException("ADM PCM contains nonfinite samples.");
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 4), BitConverter.SingleToInt32Bits(v));
            }
            await destination.WriteAsync(output.AsMemory(0, frames * Channels * 4), token); count -= frames;
        }
    }
}
