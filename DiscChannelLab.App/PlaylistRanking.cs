namespace Disc2Flac;

public static class PlaylistRanking
{
    public static double Score(PlaylistInfo title)
    {
        var seconds = title.DurationTicks / 45000d;
        var lengths = title.ChapterStarts.Select((start, i) =>
            ((i + 1 < title.ChapterStarts.Count ? title.ChapterStarts[i + 1] : title.DurationTicks) - start) / 45000d);
        var score = Math.Min(seconds, 7200) / 60 + lengths.Count(x => x is >= 20 and <= 1800) * 3;
        if (title.IsRepeatedShortClipLoop) score -= 1000;
        if (seconds < 5) score -= 500;
        score += title.Availability switch
        {
            AudioAvailability.Ready => 2000, AudioAvailability.Partial => 1500,
            AudioAvailability.NoAudio or AudioAvailability.Unsupported or AudioAvailability.Protected => -5000,
            AudioAvailability.Failed => -2000, _ => 0
        };
        return score;
    }
}
