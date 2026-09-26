namespace Disc2Flac;

public sealed record PlaylistSegment(ClipInfo Clip, long StartTicks, long EndTicks);

public static class PlaylistSegments
{
    public static IReadOnlyList<PlaylistSegment> ForRange(PlaylistInfo playlist, long startTicks, long endTicks)
    {
        if (startTicks < 0 || endTicks <= startTicks || endTicks > playlist.DurationTicks)
            throw new ArgumentOutOfRangeException(nameof(startTicks), "再生区間がプレイリストの範囲外です。");
        var result = new List<PlaylistSegment>();
        foreach (var clip in playlist.Clips)
        {
            var clipEnd = clip.PlaylistStartTicks + clip.OutTicks - clip.InTicks;
            var start = Math.Max(startTicks, clip.PlaylistStartTicks);
            var end = Math.Min(endTicks, clipEnd);
            if (end > start) result.Add(new PlaylistSegment(clip, start, end));
        }
        if (result.Sum(x => x.EndTicks - x.StartTicks) != endTicks - startTicks)
            throw new InvalidDataException("再生区間に対応するクリップが不足しています。");
        return result;
    }

    public static long SourceTicks(PlaylistSegment segment) =>
        segment.Clip.InTicks + segment.StartTicks - segment.Clip.PlaylistStartTicks;
}
