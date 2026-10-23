using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Disc2Flac;

public sealed record DvdVideoCell(long FirstSector, long LastSector);
public sealed record DvdVideoCellTitle(int TitleSet, IReadOnlyList<IReadOnlyList<DvdVideoCell>> Chapters);

/// <summary>Read validated, linear PGC cells without the DVD navigation VM's early PTT exits.
/// Multi-angle/interleaved or non-linear titles keep the existing navigation reader.</summary>
public static class DvdVideoSectors
{
    const int SectorSize = 2048;
    public static DvdVideoCellTitle? TryRead(string root, int title, int chapterCount, Action<string>? log = null)
    {
        try
        {
            var vmg = File.ReadAllBytes(Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO"));
            var tt = checked((int)U32(vmg, 196) * SectorSize);
            if (title < 1 || title > U16(vmg, tt)) throw new InvalidDataException("Invalid DVD title index.");
            var entry = tt + 8 + (title - 1) * 12;
            Require(vmg, entry, 12);
            if (vmg[entry + 1] != 1 || U16(vmg, entry + 2) != chapterCount) return null;
            int set = vmg[entry + 6], localTitle = vmg[entry + 7];
            if (set is < 1 or > 99 || localTitle is < 1 or > 99) return null;
            var data = File.ReadAllBytes(Path.Combine(root, "VIDEO_TS", $"VTS_{set:00}_0.IFO"));
            Require(data, 0, 236);
            if (Encoding.ASCII.GetString(data, 0, 12) != "DVDVIDEO-VTS") return null;
            var ptt = checked((int)U32(data, 200) * SectorSize);
            var pgcit = checked((int)U32(data, 204) * SectorSize);
            if (localTitle > U16(data, ptt)) return null;
            var titleStart = checked(ptt + (int)U32(data, ptt + 8 + (localTitle - 1) * 4));
            var titleEnd = localTitle < U16(data, ptt)
                ? checked(ptt + (int)U32(data, ptt + 8 + localTitle * 4))
                : checked(ptt + (int)U32(data, ptt + 4) + 1);
            Require(data, titleStart, checked(chapterCount * 4));
            if (titleStart + chapterCount * 4 > titleEnd) return null;
            var sectorCount = Files(root, set).Sum(f => f.Length / SectorSize);
            var chapters = new List<IReadOnlyList<DvdVideoCell>>();
            int? previousPgc = null; int previousProgram = 0, expectedPrograms = 0; long previousSector = -1;
            for (int chapter = 0; chapter < chapterCount; chapter++)
            {
                int pgcn = U16(data, titleStart + chapter * 4), pgn = U16(data, titleStart + chapter * 4 + 2);
                if (pgcn < 1 || pgcn > U16(data, pgcit) || (previousPgc is not null && pgcn != previousPgc) ||
                    pgn != previousProgram + 1) return null;
                var pgc = checked(pgcit + (int)U32(data, pgcit + 8 + (pgcn - 1) * 8 + 4));
                Require(data, pgc, 236);
                int programs = data[pgc + 2], cells = data[pgc + 3];
                if (programs < 1 || cells < programs || pgn > programs || data[pgc + 163] != 0) return null;
                expectedPrograms = programs;
                var programMap = checked(pgc + U16(data, pgc + 230));
                var cellTable = checked(pgc + U16(data, pgc + 232));
                Require(data, programMap, programs); Require(data, cellTable, cells * 24);
                int first = data[programMap + pgn - 1], last = pgn < programs ? data[programMap + pgn] - 1 : cells;
                if (first < 1 || last < first || last > cells) return null;
                var ranges = new List<DvdVideoCell>();
                for (int cell = first; cell <= last; cell++)
                {
                    int pos = cellTable + (cell - 1) * 24;
                    // Block/angle and interleaved cells require the navigation engine.
                    if ((data[pos] & 0xf4) != 0 || data[pos + 2] != 0 || data[pos + 3] != 0) return null;
                    long start = U32(data, pos + 8), end = U32(data, pos + 20);
                    if (start > end || end >= sectorCount || start <= previousSector) return null;
                    ranges.Add(new(start, end)); previousSector = end;
                }
                chapters.Add(ranges); previousPgc = pgcn; previousProgram = pgn;
            }
            if (previousProgram != expectedPrograms || !chapters.Any(c => c.Count > 1)) return null;
            log?.Invoke($"DVD-Video title {title}: linear IFO cell reader, {chapters.Sum(c => c.Count)} cells / {chapters.Count} chapters.");
            return new(set, chapters);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException)
        { log?.Invoke($"DVD-Video title {title}: cell reader unavailable; keep navigation: {ex.Message}"); return null; }
    }

    public static IReadOnlyList<FileInfo> Files(string root, int titleSet)
    {
        var files = new List<FileInfo>();
        for (int n = 1; n <= 9; n++)
        {
            var path = Path.Combine(root, "VIDEO_TS", $"VTS_{titleSet:00}_{n}.VOB");
            if (!File.Exists(path)) break;
            var file = new FileInfo(path);
            if (file.Length % SectorSize != 0) throw new InvalidDataException("DVD VOB size is not sector-aligned.");
            files.Add(file);
        }
        return files;
    }

    public static async Task CopyAsync(string root, DvdVideoCellTitle title, int firstChapter, int lastChapter,
        Stream destination, CancellationToken token, long? maxBytes = null)
    {
        if (firstChapter < 1 || lastChapter < firstChapter || lastChapter > title.Chapters.Count)
            throw new InvalidDataException("DVD chapter range is invalid.");
        var files = Files(root, title.TitleSet); var buffer = new byte[128 * 1024]; long copied = 0;
        foreach (var cell in title.Chapters.Skip(firstChapter - 1).Take(lastChapter - firstChapter + 1).SelectMany(c => c))
        {
            long baseSector = 0, remaining = checked((cell.LastSector - cell.FirstSector + 1) * SectorSize);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                long fileEnd = baseSector + file.Length / SectorSize;
                long first = Math.Max(cell.FirstSector, baseSector), end = Math.Min(cell.LastSector + 1, fileEnd);
                if (first < end)
                {
                    await using var input = file.OpenRead(); input.Position = (first - baseSector) * SectorSize;
                    long count = checked((end - first) * SectorSize);
                    while (count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int size = (int)Math.Min(buffer.Length, count);
                        if (maxBytes is { } limit) size = (int)Math.Min(size, Math.Max(0, limit - copied));
                        if (size == 0) return;
                        await input.ReadExactlyAsync(buffer.AsMemory(0, size), token);
                        DvdProgramStreamProtection.RequireClear(buffer.AsSpan(0, size));
                        await destination.WriteAsync(buffer.AsMemory(0, size), token);
                        count -= size; remaining -= size; copied += size;
                    }
                }
                baseSector = fileEnd;
            }
            if (remaining != 0) throw new EndOfStreamException("DVD cell sectors are missing.");
        }
    }

    public static (int First, int Last, long StartTicks) Range(PlaylistInfo playlist, long start, long end)
    {
        if (start < 0 || end <= start || end > playlist.DurationTicks || playlist.ChapterStarts.Count == 0)
            throw new InvalidDataException("DVD playback range is invalid.");
        int first = 1, last = 1;
        for (int i = 0; i < playlist.ChapterStarts.Count; i++)
        {
            if (playlist.ChapterStarts[i] <= start) first = i + 1;
            if (playlist.ChapterStarts[i] < end) last = i + 1;
        }
        return (first, last, playlist.ChapterStarts[first - 1]);
    }

    public static string StreamMap(AudioStreamInfo stream)
    {
        var id = stream.TransportId;
        if (!id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(id.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 0x1ff)
            throw new InvalidDataException("DVD audio stream ID is unavailable; automatic index guessing is disabled.");
        return $"0:i:{id}";
    }

    static ushort U16(byte[] bytes, int pos) { Require(bytes, pos, 2); return BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos, 2)); }
    static uint U32(byte[] bytes, int pos) { Require(bytes, pos, 4); return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4)); }
    static void Require(byte[] bytes, int pos, int length)
    {
        if (pos < 0 || length < 0 || pos > bytes.Length - length) throw new InvalidDataException("DVD IFO table is out of range.");
    }
}

/// <summary>Inspect MPEG-2 PES flags only. Never decrypt or modify a protected sector.</summary>
public static class DvdProgramStreamProtection
{
    public const string Message = "このタイトルには暗号化された音声・映像データが含まれるため処理できません。保護の解除は行いません。";
    public static void RequireClear(ReadOnlySpan<byte> data)
    {
        for (int sector = 0; sector + 14 <= data.Length; sector += 2048)
        {
            var block = data.Slice(sector, Math.Min(2048, data.Length - sector));
            if (BinaryPrimitives.ReadUInt32BigEndian(block) != 0x000001ba) continue;
            int pos = 14 + (block[13] & 7);
            while (pos + 9 <= block.Length && block[pos] == 0 && block[pos + 1] == 0 && block[pos + 2] == 1)
            {
                int id = block[pos + 3], length = BinaryPrimitives.ReadUInt16BigEndian(block.Slice(pos + 4, 2));
                if ((id == 0xbd || id is >= 0xc0 and <= 0xef) && (block[pos + 6] & 0xc0) == 0x80 && (block[pos + 6] & 0x30) != 0)
                    throw new ProtectedDiscException(LanguageService.T(Message));
                if (length == 0) break;
                pos += 6 + length;
            }
        }
    }
}
