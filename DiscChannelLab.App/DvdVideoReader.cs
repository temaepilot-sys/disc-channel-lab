using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Disc2Flac;

public static class DvdVideoReader
{
    public static IReadOnlyList<PlaylistInfo> ReadAll(string root, FfprobeService probe, Action<string>? log = null,
        CancellationToken token = default)
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
            token.ThrowIfCancellationRequested();
            try
            {
                var (duration, chapters) = FfprobeService.WithTimeout(ct => probe.ProbeDvdVideoTitleAsync(root, title, ct), token)
                    .GetAwaiter().GetResult();
                if (duration <= 0) throw new InvalidDataException("タイトルの時間とチャプターを取得できませんでした。");
                result.Add(new PlaylistInfo
                {
                    Id = 20000 + title, Format = DiscFormat.DvdVideo,
                    DvdVideoTitle = title, DisplayOrder = result.Count + 1,
                    Clips = [], ChapterStarts = chapters, DurationTicks = duration
                });
            }
            catch (Exception ex) when (FfprobeService.IsRecoverable(ex))
            {
                log?.Invoke($"DVD-Video タイトル {title} を読めません: {ex.Message}");
                result.Add(new PlaylistInfo { Id = 20000 + title, Format = DiscFormat.DvdVideo,
                    DvdVideoTitle = title, DisplayOrder = result.Count + 1, Clips = [], ChapterStarts = [],
                    DurationTicks = 0, Availability = AudioAvailability.Failed, AnalysisDetails = ex.Message });
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
