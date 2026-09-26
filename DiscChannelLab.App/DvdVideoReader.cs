using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

public static class DvdVideoReader
{
    public static IReadOnlyList<PlaylistInfo> ReadAll(string root, FfprobeService probe, Action<string>? log = null)
    {
        var path = Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO");
        if (!File.Exists(path)) return [];
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 2048 || Encoding.ASCII.GetString(bytes, 0, 12) != "DVDVIDEO-VMG")
            throw new InvalidDataException("DVD-Video の IFO ヘッダーが不正です。");
        // VMGI_MAT.tt_srpt is at byte 196; TT_SRPT starts at the sector it names.
        var tableSector = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(196, 4));
        var table = checked((long)tableSector * 2048);
        if (table <= 0 || table + 8 > bytes.Length)
            throw new InvalidDataException("DVD-Video のタイトル表がありません。");
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan((int)table, 2));
        if (count is < 1 or > 99) throw new InvalidDataException("DVD-Video のタイトル数が不正です。");
        var result = new List<PlaylistInfo>();
        for (var title = 1; title <= count; title++)
        {
            try
            {
                var (duration, chapters) = probe.ProbeDvdVideoTitleAsync(root, title, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (duration <= 0) continue;
                if (!probe.HasPlayableDvdVideoAudioAsync(root, title, CancellationToken.None)
                    .GetAwaiter().GetResult())
                {
                    log?.Invoke($"DVD-Video タイトル {title} は音声を読み出せないため一覧から除外しました。");
                    continue;
                }
                result.Add(new PlaylistInfo
                {
                    Id = 20000 + title, Format = DiscFormat.DvdVideo,
                    DvdVideoTitle = title, DisplayOrder = result.Count + 1,
                    Clips = [], ChapterStarts = chapters, DurationTicks = duration
                });
            }
            catch (Exception ex) when (ex is FfToolException or InvalidDataException or IOException)
            {
                log?.Invoke($"DVD-Video タイトル {title} を読めません: {ex.Message}");
            }
        }
        return result;
    }

    public static string DiscKey(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "VIDEO_TS"), "*.IFO").OrderBy(Path.GetFileName))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
