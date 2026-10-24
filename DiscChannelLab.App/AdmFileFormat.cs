namespace Disc2Flac;

public static class AdmFileFormat
{
    public static AudioFileProbe Probe(AdmWave wave)
    {
        long Ticks(long sample) => (long)Math.Round(sample * (45000m / 48000));
        var stream = new AudioStreamInfo { Index = 0, Codec = "adm", Profile = $"ADM · {wave.Scene.Sources.Count(s => s.Kind == SpatialSourceKind.Object)} objects / {wave.Channels} PCM tracks → stereo",
            SampleRate = 48000, Channels = 2, ChannelLayout = "stereo", BitDepth = 24, SampleCount = wave.Scene.DurationSamples };
        // Channels describes the adapter's stereo output, never an assumed order of object PCM tracks.
        var chapters = new Dictionary<int, ChapterMetadata>();
        var values = wave.Album?.Chapters ?? [];
        if (values.Length == 0 || values[0].StartSample != 0) values = [new(0, wave.Programmes.First(p => p.Id == wave.ProgrammeId).Name), .. values];
        for (int i = 0; i < values.Length; i++) chapters[i + 1] = new(values[i].Title, values[i].Artist ?? wave.Album?.Artist) { StartTicks = Ticks(values[i].StartSample) };
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["album"] = wave.Album?.Album ?? wave.Programmes.First(p => p.Id == wave.ProgrammeId).Name, ["title"] = wave.Album?.Album ?? Path.GetFileNameWithoutExtension(wave.Path) };
        if (wave.Album?.Artist is { } artist) tags["artist"] = artist;
        if (wave.Album?.Origin is { } origin) tags["description"] = "ADM source: " + origin;
        return new(Ticks(wave.Scene.DurationSamples), new([stream], chapters, tags) { DetectedAudioCount = 1, Availability = AudioAvailability.Ready, Diagnostics = wave.Scene.Warnings });
    }
    public static HeadphoneOptions RenderOptions(HeadphoneOptions value) => value.Enabled ? value : value with
    { Enabled = true, Method = HeadphoneMethod.Direct, Hrtf = HeadphoneHrtf.BuiltIn, SofaPath = "", ShadowStrength = 0, RearCutDb = 0, InterauralDelay = false, Room = new() };
}
