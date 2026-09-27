using System.Collections.ObjectModel;

namespace Disc2Flac;

public sealed partial class MainViewModel
{
    public ObservableCollection<MixerStrip> MixerStrips { get; } = [];
    public ChannelMeter MixerOutputLeft { get; } = new("L", "L");
    public ChannelMeter MixerOutputRight { get; } = new("R", "R");
    private bool _experimentalMixerEnabled;
    private bool _saveMixerDownmix;
    private bool _includeMixerMaster;
    private string _mixerVariantName = "Mix 1";
    private bool _applyingMixerPreset;
    private readonly Dictionary<string, ExperimentalChannel[]> _rememberedMixerLayouts = new(StringComparer.Ordinal);
    public string MixerVariantName { get => _mixerVariantName; set => Set(ref _mixerVariantName, value); }
    public bool IncludeMixerMaster { get => _includeMixerMaster; set => Set(ref _includeMixerMaster, value); }
    public bool CanExportMixer => CanPrepareConversion && CanDownmixStereo;
    public bool SaveMixerDownmix
    {
        get => _saveMixerDownmix;
        set
        {
            if (!Set(ref _saveMixerDownmix, value && CanDownmixStereo)) return;
            if (_saveMixerDownmix) { SaveStereoDownmix = false; SaveIndividualChannels = false; }
        }
    }
    private ChannelMixExport CaptureMixerExport() => new(SelectedStream!, MixerStrips.Select(x => x.Snapshot()),
        MixerVariantName, IncludeMixerMaster ? VolumeCurve.Gain(Volume, PerceivedVolume) : 1);
    private readonly Queue<(double Position, MixerMeterFrame Frame)> _mixedFrames = new();
    public bool CanUseExperimentalMixer => SelectedStream is { } stream && StereoMixSettings.Supports(stream);
    public bool ExperimentalMixerEnabled
    {
        get => _experimentalMixerEnabled;
        set
        {
            if (!Set(ref _experimentalMixerEnabled, value && CanUseExperimentalMixer)) return;
            PublishExperimentalMix();
        }
    }
    private PreviewMixState PreviewState(StereoMixSettings mix, string? solo) =>
        new(mix, solo, ExperimentalMixerEnabled && CanUseExperimentalMixer ? MixerStrips.Select(x => x.Snapshot()).ToArray() : null);

    public void PublishExperimentalMix()
    {
        if (_applyingMixerPreset) return;
        if (ExperimentalMixerEnabled && CanUseExperimentalMixer && PreviewChannels.Count > 0) SelectedPreviewChannel = PreviewChannels[0];
        _activePlaybackMix = CurrentMix;
        _activePreviewChannel = SelectedPreviewChannel?.Code;
        Volatile.Write(ref _livePreviewMix, PreviewState(CurrentMix, _activePreviewChannel));
    }
    public MixerPreset CaptureMixerPreset() => new()
    {
        Format = "DiscChannelLab.MixerPreset", Version = 1, Name = MixerVariantName,
        MasterPercent = Volume, PerceivedMaster = PerceivedVolume, IncludeMasterInExport = IncludeMixerMaster,
        Channels = MixerStrips.Select(x => x.Snapshot()).ToArray()
    };
    public void ApplyMixerPreset(MixerPreset preset)
    {
        // Validate everything before changing any live controls or audio settings.
        preset.Validate();
        if (!CanUseExperimentalMixer || !preset.Channels.Select(x => x.Code).ToHashSet(StringComparer.Ordinal)
                .SetEquals(MixerStrips.Select(x => x.Code)))
            throw new InvalidDataException(LanguageService.T("設定ファイルと音源のチャンネル構成が異なります。同じ構成の音声を選択してください。"));
        _applyingMixerPreset = true;
        try
        {
            var channels = preset.Channels.ToDictionary(x => x.Code, StringComparer.Ordinal);
            foreach (var strip in MixerStrips)
            {
                var channel = channels[strip.Code];
                strip.Level = channel.Gain * 100; strip.Pan = channel.Pan * 100; strip.Muted = channel.Muted;
            }
            MixerVariantName = preset.Name;
            Volume = preset.MasterPercent; PerceivedVolume = preset.PerceivedMaster;
            IncludeMixerMaster = preset.IncludeMasterInExport;
            ExperimentalMixerEnabled = true;
        }
        finally { _applyingMixerPreset = false; }
        PublishExperimentalMix();
    }
    private void RebuildMixerStrips()
    {
        SaveMixerDownmix = false;
        if (MixerStrips.Count > 0)
            _rememberedMixerLayouts[string.Join(' ', MixerStrips.Select(x => x.Code))] = MixerStrips.Select(x => x.Snapshot()).ToArray();
        MixerStrips.Clear();
        if (CanUseExperimentalMixer)
        {
            var defaults = StereoPreviewMixer.StandardChannels(SelectedStream!);
            var key = string.Join(' ', defaults.Select(x => x.Code));
            var settings = (_rememberedMixerLayouts.TryGetValue(key, out var remembered) ? remembered : defaults)
                .ToDictionary(x => x.Code, StringComparer.Ordinal);
            foreach (var input in ChannelMeters)
            {
                var setting = settings[input.Code];
                var strip = new MixerStrip(input, setting.Gain * 100, setting.Muted, PublishExperimentalMix)
                    { Pan = setting.Pan * 100 };
                MixerStrips.Add(strip);
            }
        }
        // Loading a title temporarily clears SelectedStream. Keep the user's on/off
        // choice through that gap and through stereo/unsupported streams.
        Changed(nameof(CanUseExperimentalMixer));
        ResetMixedMeters();
        PublishExperimentalMix();
    }
    public void ResetExperimentalMixer()
    {
        if (!CanUseExperimentalMixer) return;
        var defaults = StereoPreviewMixer.StandardChannels(SelectedStream!).ToDictionary(x => x.Code);
        _applyingMixerPreset = true;
        try
        {
            foreach (var strip in MixerStrips)
            {
                var setting = defaults[strip.Code];
                strip.Level = setting.Gain * 100;
                strip.Pan = setting.Pan * 100;
                strip.Muted = setting.Muted;
            }
        }
        finally { _applyingMixerPreset = false; }
        PublishExperimentalMix();
    }
    private void QueueMixedFrame(double position, MixerMeterFrame frame)
    {
        _mixedFrames.Enqueue((position, frame));
        while (_mixedFrames.Count > 120) _mixedFrames.Dequeue();
        ApplyMixedFrames();
    }
    private void ApplyMixedFrames()
    {
        var position = MeterPlaybackPosition;
        while (_mixedFrames.TryPeek(out var item) && item.Position <= position)
        {
            _mixedFrames.Dequeue();
            // Mixer Input and the standard GUI bind to these same meter objects.
            // Input, Post and Master are applied within one UI dispatch, before rendering.
            for (var i = 0; i < Math.Min(ChannelMeters.Count, item.Frame.Input.Length); i++)
                ChannelMeters[i].Update(item.Frame.Input[i], item.Frame.InputRms[i]);
            for (var i = 0; i < Math.Min(MixerStrips.Count, item.Frame.Left.Length); i++)
            {
                MixerStrips[i].PostLeft.Update(item.Frame.Left[i], double.NegativeInfinity);
                MixerStrips[i].PostRight.Update(item.Frame.Right[i], double.NegativeInfinity);
            }
            MixerOutputLeft.Update(item.Frame.Output[0], double.NegativeInfinity);
            MixerOutputRight.Update(item.Frame.Output[1], double.NegativeInfinity);
        }
    }
    private void ResetMixedMeters()
    {
        _mixedFrames.Clear();
        foreach (var strip in MixerStrips)
        {
            strip.PostLeft.Update(double.NegativeInfinity, double.NegativeInfinity);
            strip.PostRight.Update(double.NegativeInfinity, double.NegativeInfinity);
        }
        MixerOutputLeft.Update(double.NegativeInfinity, double.NegativeInfinity);
        MixerOutputRight.Update(double.NegativeInfinity, double.NegativeInfinity);
    }
}
