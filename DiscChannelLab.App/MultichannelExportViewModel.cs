namespace Disc2Flac;

public sealed partial class MainViewModel
{
    private bool _saveMultichannelFlac;
    public bool CanSaveMultichannel => SelectedStream is { Channels: > 2, CanMakeCd: true } && !IsBusy;
    public bool SaveMultichannelFlac
    {
        get => _saveMultichannelFlac;
        set
        {
            if (!Set(ref _saveMultichannelFlac, value && CanSaveMultichannel)) return;
            if (_saveMultichannelFlac)
            {
                SaveStereoDownmix = false; SaveIndividualChannels = false; SaveMixerDownmix = false;
                if (_quality == OutputQuality.HighResolution)
                    _quality = SelectedStream?.CanMakeOriginalPcm == true ? OutputQuality.OriginalPcm : OutputQuality.Cd;
            }
            else if (_quality == OutputQuality.OriginalPcm)
                _quality = SelectedStream?.CanMakeHighResolution == true ? OutputQuality.HighResolution : OutputQuality.Cd;
            Changed(nameof(HighResOptionText)); Changed(nameof(HighResHelpText));
            Changed(nameof(CanChooseHighRes)); Changed(nameof(IsCdSelected)); Changed(nameof(IsHighResSelected));
            Changed(nameof(SelectedStreamNote)); Changed(nameof(CanConvert)); Changed(nameof(CanExportMixer));
        }
    }
    public string HighResOptionText => LanguageService.T(SaveMultichannelFlac ? "元のPCM音質" : "ハイレゾ · 元の形式");
    public string HighResHelpText => LanguageService.T(SaveMultichannelFlac
        ? _disc?.Format == DiscFormat.Sacd ? "チャンネル数と配置を維持。SACDの元のPCMは24bit / 88.2kHzです。"
            : "元のPCM音質では、入力のサンプリング周波数・16/24bit・チャンネル配置を維持します。"
        : "ハイレゾは元音源が24bit / 88.2kHz以上のときに選べます。");
    private bool QualityAvailable(AudioStreamInfo stream) => _quality switch
    {
        OutputQuality.Cd => stream.CanMakeCd,
        OutputQuality.HighResolution => stream.CanMakeHighResolution,
        OutputQuality.OriginalPcm => SaveMultichannelFlac && stream.CanMakeOriginalPcm,
        _ => false
    };
}
