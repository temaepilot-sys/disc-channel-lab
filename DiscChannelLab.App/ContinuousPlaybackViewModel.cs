namespace Disc2Flac;

public sealed partial class MainViewModel
{
    private TrackRow[] _continuousRun = [];
    private long _continuousOriginTicks;
    private bool _switchingContinuousTrack;
    private double PlaybackTimelineMax => _continuousRun.Length > 0
        ? (_continuousRun[^1].EndTicks - _continuousOriginTicks) / 45000d : PreviewMax;

    private TrackRow[] PlanContinuousRun(PlaylistInfo playlist, TrackRow first, AudioStreamInfo stream)
    {
        var run = new List<TrackRow> { first };
        // The legacy FFplay path still advances one track at a time.
        if (!ContinuousPlayback || !(HeadphonesEnabled || stream.Channels == 2 || StereoMixSettings.Supports(stream))) return run.ToArray();
        var layout = HeadphoneGeometry.Channels(stream);
        for (int i = Tracks.IndexOf(first) + 1; i < Tracks.Count; i++)
        {
            var next = Tracks[i];
            if (next.StartTicks != run[^1].EndTicks || !stream.Covers(playlist, next)) break;
            if (playlist.Format == DiscFormat.AudioFile)
            {
                try
                {
                    var source = AudioStreamMatcher.Resolve(stream, AudioFileReader.StreamsForTrack(playlist, next));
                    if (!layout.SequenceEqual(HeadphoneGeometry.Channels(source))) break;
                }
                catch (InvalidDataException) { break; }
            }
            run.Add(next);
        }
        return run.ToArray();
    }

    private void UpdateContinuousLimit()
    {
        if (_playbackControl is not { } control || SelectedTrack is null || _continuousRun.Length == 0) return;
        var end = ContinuousPlayback ? _continuousRun[^1].EndTicks : SelectedTrack.EndTicks;
        control.EndFrame = Math.Max(0, ConversionService.ToSample(end - _continuousOriginTicks, 48000)
            - (long)Math.Round(_playbackStartSeconds * 48000));
    }

    private void UpdateContinuousPosition(double position)
    {
        position = Math.Clamp(position, 0, PlaybackTimelineMax);
        var end = _playbackControl is { } control ? Math.Min(PlaybackTimelineMax,
            _playbackStartSeconds + control.EndFrame / 48000d) : PlaybackTimelineMax;
        var ticks = _continuousOriginTicks + (long)Math.Round(position * 45000);
        var endTicks = _continuousOriginTicks + (long)Math.Round(end * 45000);
        // Keep transitions on the output-device clock. Decoder read-ahead must not select
        // a new song early or reset the shared device, DSP, meters or visualizer epoch.
        foreach (var candidate in _continuousRun)
        {
            if (candidate.StartTicks > ticks || candidate.StartTicks >= endTicks) break;
            if (SelectedTrack == candidate || (SelectedTrack is { } current && candidate.StartTicks < current.StartTicks)) continue;
            _switchingContinuousTrack = true;
            try { SelectedTrack = candidate; }
            finally { _switchingContinuousTrack = false; }
            Status = $"{candidate.Title} を再生しています";
            _log.Write($"CONTINUOUS track={candidate.Number} {candidate.Title}; retained output, decoder/DSP session");
        }
        var offset = SelectedTrack is { } track ? (track.StartTicks - _continuousOriginTicks) / 45000d : 0;
        PreviewSeconds = position - offset;
    }
}
