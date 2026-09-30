namespace Disc2Flac;

public sealed partial class MainViewModel
{
    public VisualizerBridge Visualization { get; } = new();

    private void PublishVisualizationClock()
    {
        if (!Visualization.IsConnected) return;
        Visualization.PublishClock(_transportVersion, SelectedTrack?.Title ?? "DiscChannelLab", SelectedStream?.Codec ?? "",
            SelectedStream?.SampleRate ?? 48000, MeterPlaybackPosition, PreviewMax, IsPlaying && _playClock.IsRunning);
    }
}
