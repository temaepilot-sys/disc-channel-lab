namespace Disc2Flac;

public sealed partial class MainViewModel
{
    public VisualizerBridge Visualization { get; } = new();

    private void PublishVisualizationClock()
    {
        if (!Visualization.IsConnected) return;
        var mix = Volatile.Read(ref _livePreviewMix);
        var settings = mix?.Channels;
        var hasSolo = settings?.Any(c => c.Solo) == true;
        var channels = SelectedStream is { } source ? HeadphoneGeometry.Channels(source) : [];
        var states = channels.Select(code =>
        {
            var setting = settings?.FirstOrDefault(c => c.Code == code);
            var muted = setting?.Muted == true;
            var enabled = !muted && (!hasSolo || setting?.Solo == true) &&
                (mix?.SoloChannel is null || mix.SoloChannel == code) && (setting is null || setting.Gain > 0);
            return new SpeakerPlaybackState(code, muted, setting?.Solo == true, enabled);
        }).ToArray();
        Visualization.PublishClock(_transportVersion, SelectedTrack?.Title ?? "DiscChannelLab", SelectedStream?.Codec ?? "",
            SelectedStream?.SampleRate ?? 48000, MeterPlaybackPosition, PreviewMax, IsPlaying && _playClock.IsRunning,
            channels, states);
    }
}
