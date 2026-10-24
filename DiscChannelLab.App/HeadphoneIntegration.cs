using System.ComponentModel;

namespace Disc2Flac;

public enum PreviewAudioMode { Stereo, HeadphonesDirect, HeadphonesHrtf, HeadphonesBrir }

public sealed class PreviewModeChoice(PreviewAudioMode mode, string label) : INotifyPropertyChanged
{
    public PreviewAudioMode Mode { get; } = mode;
    public string Label => LanguageService.T(label);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Label)));
}

public sealed partial class MainViewModel
{
    public IReadOnlyList<PreviewModeChoice> PreviewModes { get; } =
    [
        new(PreviewAudioMode.Stereo, "2ch Stereo"),
        new(PreviewAudioMode.HeadphonesDirect, "ヘッドフォン · Direct"),
        new(PreviewAudioMode.HeadphonesHrtf, "ヘッドフォン · HRTF"),
        new(PreviewAudioMode.HeadphonesBrir, "Headphones · Measured BRIR")
    ];
    public bool CanChoosePreviewMode => !IsBusy && !IsPlaying && !IsAtmosStreamingEnabled && (CurrentAdm is null || IsAdmPlaybackEnabled);
    public PreviewAudioMode SelectedPreviewMode
    {
        get => IsAtmosStreamingEnabled && SelectedAtmosPresentation == AtmosPresentation.Stereo || !HeadphonesEnabled ? PreviewAudioMode.Stereo : HeadphoneSession.Current.Options.Method == HeadphoneMethod.Direct
            ? PreviewAudioMode.HeadphonesDirect : HeadphoneSession.Current.Options.Method == HeadphoneMethod.MeasuredBrir ? PreviewAudioMode.HeadphonesBrir : PreviewAudioMode.HeadphonesHrtf;
        set
        {
            if (value == SelectedPreviewMode) return;
            if (!CanChoosePreviewMode || !Enum.IsDefined(value)) { Changed(); return; }
            var session = HeadphoneSession.Current;
            if (value == PreviewAudioMode.HeadphonesBrir && string.IsNullOrEmpty(session.Options.BrirHash))
            { Status = "Load and apply a BRIR profile in Spatial Lab → Measured BRIR first."; Changed(); return; }
            session.SetOptions(session.Options with
            {
                Enabled = value != PreviewAudioMode.Stereo,
                Method = value == PreviewAudioMode.Stereo ? session.Options.Method : value == PreviewAudioMode.HeadphonesDirect
                    ? HeadphoneMethod.Direct : value == PreviewAudioMode.HeadphonesBrir ? HeadphoneMethod.MeasuredBrir : HeadphoneMethod.SteamAudio
            });
        }
    }
    public bool HeadphonesEnabled => HeadphoneSession.Current.Options.Enabled;
    public string PreviewChannelHint => HeadphonesEnabled
        ? (LanguageService.Instance.IsJapanese ? "個別チャンネルも仮想スピーカーの方向から聞こえます。" : "An isolated channel retains its virtual speaker direction.")
        : LanguageService.T("個別チャンネルは左右のスピーカーに同じ音を出します");
    public string HeadphoneMixerHint => HeadphonesEnabled ? HeadphoneText.T("Gain, Mute, Solo and polarity apply before headphone rendering. Pan applies to stereo FLAC export.") : "";
    public string MixerBypassHint => HeadphonesEnabled
        ? HeadphoneText.T("Source unity gain is used; these faders are bypassed.")
        : LanguageService.T("標準ミックスを使用：このフェーダーは再生に適用されていません。");

    private void HeadphoneOptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Changed(nameof(SelectedPreviewMode));
        if (!HeadphonesEnabled && CurrentAdm is null) SaveBinauralFlac = false;
        Changed(nameof(CanChooseBinauralFlac)); Changed(nameof(SelectedStreamNote));
        if (_mixerIsHeadphone == HeadphonesEnabled) { PublishVisualizationClock(); return; }
        // A format change is only made between playback sessions.
        if (IsPlaying) PausePlayback();
        PreviewChannels.Clear();
        PreviewChannels.Add(new ChannelChoice(null, HeadphonesEnabled ? "ヘッドフォン · 全チャンネル" : "ステレオミックス"));
        if (SelectedStream is { } stream)
            foreach (var code in StereoMixSettings.ChannelNames(stream))
                PreviewChannels.Add(new ChannelChoice(code, ChannelChoice.DescribeRaw(code)));
        SelectedPreviewChannel = PreviewChannels[0];
        ChannelMeters.Clear();
        if (SelectedStream is { } source)
        {
            IReadOnlyList<string> codes = HeadphonesEnabled ? HeadphoneGeometry.Channels(source) : StereoMixSettings.ChannelNames(source);
            for (var i = 0; i < source.Channels; i++)
            {
                var code = i < codes.Count ? codes[i] : source.Channels == 1 ? "Mono" : source.Channels == 2 ? (i == 0 ? "L" : "R") : $"Ch{i + 1}";
                ChannelMeters.Add(new ChannelMeter(code, ChannelChoice.DescribeRaw(code)));
            }
        }
        RebuildMixerStrips();
        foreach (var name in new[] { nameof(HeadphonesEnabled), nameof(PreviewChannelHint), nameof(HeadphoneMixerHint), nameof(MixerBypassHint), nameof(SelectedStreamNote) }) Changed(name);
        UpdateActions();
        PublishVisualizationClock();
    }
}
