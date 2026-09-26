using System.Buffers.Binary;
using System.Text;

namespace Disc2Flac;

public static class MplsReader
{
    private const long TicksPerSecond = 45000;

    public static string NormalizeDiscRoot(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            full = Directory.GetParent(full)?.FullName ?? full;
        if (!Directory.Exists(Path.Combine(full, "BDMV", "PLAYLIST")))
            throw new InvalidDataException("BDMV/PLAYLIST が見つかりません。非保護の Blu-ray Audio を選んでください。");
        return full;
    }

    public static IReadOnlyList<PlaylistInfo> ReadAll(string root, Action<string>? log = null)
    {
        var folder = Path.Combine(root, "BDMV", "PLAYLIST");
        var playlists = new List<PlaylistInfo>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.mpls").OrderBy(x => x))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
            try { playlists.Add(Read(file, id)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                log?.Invoke($"プレイリスト {id:00000} を読めません: {ex.Message}");
            }
        }
        var ordered = playlists.OrderBy(x => x.IsRepeatedShortClipLoop)
            .ThenByDescending(x => x.DurationTicks).ThenByDescending(x => x.ChapterStarts.Count).ToArray();
        for (var i = 0; i < ordered.Length; i++) ordered[i].DisplayOrder = i + 1;
        return ordered;
    }

    public static PlaylistInfo Read(string path, int id)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || Encoding.ASCII.GetString(data, 0, 4) != "MPLS")
            throw new InvalidDataException("MPLS ヘッダーが不正です。");
        var playlistOffset = CheckedOffset(data, 8, 10);
        var markOffset = U32(data, 12) == 0 ? -1 : CheckedOffset(data, 12, 6);
        var itemCount = U16(data, playlistOffset + 6);
        if (itemCount is 0 or > 10000) throw new InvalidDataException("PlayItem 数が不正です。");
        var itemOffset = playlistOffset + 10;
        var clips = new List<ClipInfo>(itemCount);
        long position = 0;
        for (var i = 0; i < itemCount; i++)
        {
            var length = U16(data, itemOffset);
            if (length < 20 || itemOffset + 2 + length > data.Length)
                throw new InvalidDataException("PlayItem が途中で切れています。");
            var clipId = Encoding.ASCII.GetString(data, itemOffset + 2, 5);
            if (!clipId.All(char.IsAsciiDigit)) throw new InvalidDataException("Clip ID が不正です。");
            var inTicks = U32(data, itemOffset + 14);
            var outTicks = U32(data, itemOffset + 18);
            if (outTicks <= inTicks) throw new InvalidDataException("PlayItem の再生区間が不正です。");
            clips.Add(new ClipInfo(clipId, inTicks, outTicks, position));
            position += outTicks - inTicks;
            itemOffset += 2 + length;
        }

        var chapterStarts = new SortedSet<long> { 0 };
        var markCount = markOffset < 0 ? 0 : U16(data, markOffset + 4);
        var markEntry = markOffset + 6;
        for (var i = 0; i < markCount; i++, markEntry += 14)
        {
            Ensure(data, markEntry, 14);
            if (data[markEntry + 1] != 1) continue; // Entry mark = chapter.
            var clipIndex = U16(data, markEntry + 2);
            if (clipIndex >= clips.Count) continue;
            var clip = clips[clipIndex];
            var markTime = U32(data, markEntry + 4);
            if (markTime < clip.InTicks || markTime >= clip.OutTicks) continue;
            var absolute = clip.PlaylistStartTicks + markTime - clip.InTicks;
            if (absolute > 0 && absolute < position) chapterStarts.Add(absolute);
        }

        // Some discs place a final entry mark less than one second before the end.
        // Merge that tail into the previous chapter so it remains playable and exportable.
        if (chapterStarts.Count > 1 && position - chapterStarts.Max < TicksPerSecond)
            chapterStarts.Remove(chapterStarts.Max);

        if (position < TicksPerSecond) throw new InvalidDataException("再生時間が短すぎます。");
        return new PlaylistInfo { Id = id, Clips = clips, ChapterStarts = chapterStarts.ToArray(), DurationTicks = position };
    }

    private static int CheckedOffset(byte[] data, int index, int length)
    {
        var offset = U32(data, index);
        if (offset > int.MaxValue || offset + length > data.Length) throw new InvalidDataException("MPLS 内のオフセットが不正です。");
        return (int)offset;
    }

    private static ushort U16(byte[] data, int index)
    {
        Ensure(data, index, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index, 2));
    }

    private static uint U32(byte[] data, int index)
    {
        Ensure(data, index, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index, 4));
    }

    private static void Ensure(byte[] data, int index, int length)
    {
        if (index < 0 || (long)index + length > data.Length) throw new InvalidDataException("MPLS が途中で切れています。");
    }
}
