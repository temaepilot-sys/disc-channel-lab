namespace Disc2Flac;

/// <summary>Recover exact ADM sample anchors after the UI's 45 kHz tick conversion.</summary>
public static class AdmTimeline
{
    public static long ToTicks(long sample) => checked((long)decimal.Round(sample * (45000m / 48000)));

    public static long SampleAt(AdmWave wave, long ticks)
    {
        if (ticks < 0 || ticks > ToTicks(wave.Scene.DurationSamples)) throw new InvalidDataException("ADM interval outside PCM.");
        if (ticks == 0) return 0;
        if (ticks == ToTicks(wave.Scene.DurationSamples)) return wave.Scene.DurationSamples;
        long? anchor = null;
        foreach (var chapter in wave.Album?.Chapters ?? [])
        {
            if (ToTicks(chapter.StartSample) != ticks) continue;
            if (anchor is { } previous && previous != chapter.StartSample) throw new InvalidDataException("Ambiguous ADM chapter sample boundary.");
            anchor = chapter.StartSample;
        }
        return anchor ?? ConversionService.ToSample(ticks, 48000);
    }

    public static (long First, long Frames) Range(AdmWave wave, long startTicks, long endTicks)
    {
        if (endTicks <= startTicks) throw new InvalidDataException("Empty ADM interval.");
        long first = SampleAt(wave, startTicks), last = SampleAt(wave, endTicks);
        if (last <= first) throw new InvalidDataException("Empty ADM interval.");
        return (first, last - first);
    }
}
