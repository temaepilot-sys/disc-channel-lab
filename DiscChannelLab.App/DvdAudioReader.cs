using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

// The DVD-Audio ATS title table is documented in libdvdread's ifo_types.h.
// All sector addresses here are relative to ATS_nn_1.AOB and use 2048-byte sectors.
public static class DvdAudioReader
{
    private const int SectorSize = 2048;

    public static IReadOnlyList<PlaylistInfo> ReadAll(string root, Action<string>? log = null)
    {
        var folder = Path.Combine(root, "AUDIO_TS");
        if (!File.Exists(Path.Combine(folder, "AUDIO_TS.IFO"))) return [];
        var result = new List<PlaylistInfo>();
        for (var set = 1; set <= 99; set++)
        {
            var ifo = Path.Combine(folder, $"ATS_{set:00}_0.IFO");
            if (!File.Exists(ifo)) continue;
            try
            {
                var files = DvdAudioSectors.Files(root, set);
                if (files.Count == 0) continue; // A video-zone link has no AOB of its own.
                var bytes = File.ReadAllBytes(ifo);
                if (bytes.Length < 2064 || Encoding.ASCII.GetString(bytes, 0, 12) != "DVDAUDIO-ATS")
                    throw new InvalidDataException("DVD-Audio の ATS ヘッダーが不正です。");
                var count = Be16(bytes, SectorSize);
                if (count is < 1 or > 99) throw new InvalidDataException("DVD-Audio のタイトル数が不正です。");
                var sectorCount = files.Sum(file => file.Length / SectorSize);
                for (var title = 0; title < count; title++)
                {
                    var index = SectorSize + 8 + title * 8;
                    Ensure(bytes, index, 8);
                    var record = checked(SectorSize + (int)Be32(bytes, index + 4));
                    Ensure(bytes, record, 16);
                    var programsCount = bytes[record + 2];
                    var cellCount = bytes[record + 3];
                    var pointerOffset = Be16(bytes, record + 12);
                    if (programsCount is < 1 or > 99 || cellCount < programsCount || cellCount > 255 ||
                        pointerOffset < 16 + programsCount * 20)
                        throw new InvalidDataException("DVD-Audio の曲・セル数が不正です。");
                    Ensure(bytes, record + 16, programsCount * 20);
                    Ensure(bytes, record + pointerOffset, cellCount * 12);
                    var cells = new (long Start, long End)[cellCount];
                    for (var c = 0; c < cellCount; c++)
                    {
                        var offset = record + pointerOffset + c * 12;
                        cells[c] = (Be32(bytes, offset + 4), Be32(bytes, offset + 8));
                        if (cells[c].Start > cells[c].End || cells[c].End >= sectorCount)
                            throw new InvalidDataException("DVD-Audio のセル位置が AOB の範囲外です。");
                    }
                    var firstCells = new int[programsCount];
                    var lengths = new uint[programsCount];
                    for (var p = 0; p < programsCount; p++)
                    {
                        var offset = record + 16 + p * 20;
                        firstCells[p] = bytes[offset + 4] - 1;
                        lengths[p] = Be32(bytes, offset + 10);
                        if (firstCells[p] < 0 || firstCells[p] >= cellCount ||
                            p > 0 && firstCells[p] <= firstCells[p - 1] || lengths[p] == 0)
                            throw new InvalidDataException("DVD-Audio の曲境界が不正です。");
                    }
                    var programs = new List<DvdAudioProgram>(programsCount);
                    long position = 0;
                    for (var p = 0; p < programsCount; p++)
                    {
                        var lastCell = p + 1 < programsCount ? firstCells[p + 1] - 1 : cellCount - 1;
                        var start = cells[firstCells[p]].Start;
                        var end = cells[lastCell].End;
                        if (p > 0 && start <= programs[^1].EndSector)
                            throw new InvalidDataException("DVD-Audio の曲セクターが重複しています。");
                        var next = checked(position + (long)Math.Round(lengths[p] / 2m, MidpointRounding.AwayFromZero));
                        programs.Add(new DvdAudioProgram(start, end, position, next));
                        position = next;
                    }
                    result.Add(new PlaylistInfo
                    {
                        Id = 10000 + set * 100 + title + 1,
                        Format = DiscFormat.DvdAudio,
                        DvdAudio = new DvdAudioTitle(set, title + 1, programs),
                        DisplayOrder = result.Count + 1,
                        Clips = [],
                        ChapterStarts = programs.Select(p => p.StartTicks).ToArray(),
                        DurationTicks = position
                    });
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException)
            {
                log?.Invoke($"DVD-Audio ATS {set:00} を読めません: {ex.Message}");
            }
        }
        return result;
    }

    public static string DiscKey(string root, Action<string>? log = null)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var folder = Path.Combine(root, "AUDIO_TS");
        string[] files;
        try { files = Directory.GetFiles(folder, "*.IFO"); }
        catch (IOException ex)
        {
            log?.Invoke($"DVD-Audio directory enumeration failed; reading known IFO paths: {ex.Message}");
            files = new[] { Path.Combine(folder, "AUDIO_TS.IFO") }.Concat(Enumerable.Range(1, 99)
                .Select(i => Path.Combine(folder, $"ATS_{i:00}_0.IFO"))).Where(File.Exists).ToArray();
        }
        if (files.Length == 0) throw new InvalidDataException("ディスク識別に必要なIFOを読み取れません。");
        foreach (var file in files.OrderBy(Path.GetFileName))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static ushort Be16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    private static uint Be32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void Ensure(byte[] bytes, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > bytes.Length - size)
            throw new InvalidDataException("DVD-Audio IFO の表がファイル範囲外です。");
    }
}

public static class DvdAudioSectors
{
    private const int SectorSize = 2048;

    public static IReadOnlyList<FileInfo> Files(string root, int titleSet)
    {
        var result = new List<FileInfo>();
        for (var index = 1; index <= 9; index++)
        {
            var path = Path.Combine(root, "AUDIO_TS", $"ATS_{titleSet:00}_{index}.AOB");
            if (!File.Exists(path)) break;
            var info = new FileInfo(path);
            if (info.Length % SectorSize != 0) throw new InvalidDataException($"AOB のサイズが不正です: {path}");
            result.Add(info);
        }
        return result;
    }

    public static async Task CopyAsync(string root, int titleSet, long firstSector, long lastSector,
        Stream destination, CancellationToken token, long? maxBytes = null)
    {
        if (firstSector < 0 || lastSector < firstSector) throw new ArgumentOutOfRangeException(nameof(firstSector));
        var files = Files(root, titleSet);
        var nextFileSector = 0L;
        var rangeBytes = checked((lastSector - firstSector + 1) * SectorSize);
        var remaining = maxBytes is { } limit ? Math.Min(limit, rangeBytes) : rangeBytes;
        var buffer = new byte[128 * 1024];
        foreach (var file in files)
        {
            var fileStart = nextFileSector;
            nextFileSector += file.Length / SectorSize;
            if (firstSector >= nextFileSector || lastSector < fileStart) continue;
            var first = Math.Max(firstSector, fileStart);
            var end = Math.Min(lastSector + 1, nextFileSector);
            var bytes = Math.Min(checked((end - first) * SectorSize), remaining);
            if (bytes <= 0) break;
            await using var input = file.OpenRead();
            input.Position = (first - fileStart) * SectorSize;
            while (bytes > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), token);
                if (read == 0) throw new EndOfStreamException($"AOB を最後まで読めませんでした: {file.FullName}");
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                bytes -= read;
                remaining -= read;
            }
            if (remaining == 0) break;
        }
        if (remaining > 0) throw new EndOfStreamException("必要な AOB セクターが見つかりません。");
    }

    public static (long FirstSector, long LastSector, long SkipTicks) Range(DvdAudioTitle title, long startTicks, long endTicks)
    {
        var first = title.Programs.FirstOrDefault(p => p.EndTicks > startTicks)
            ?? throw new InvalidDataException("DVD-Audio の曲開始位置がありません。");
        var last = title.Programs.LastOrDefault(p => p.StartTicks < endTicks)
            ?? throw new InvalidDataException("DVD-Audio の曲終了位置がありません。");
        return (first.StartSector, last.EndSector, startTicks - first.StartTicks);
    }
}
