using System.Globalization;
using System.Text.Json;

namespace Disc2Flac;

/// <summary>Validated, immutable settings captured once for an entire export job.</summary>
public sealed class ChannelMixExport
{
    private readonly ExperimentalChannel[] _channels;
    private readonly string _layout;
    public string Name { get; }
    public double MasterGain { get; }
    public string FileSuffix => $" [Mixer - {ConversionService.SafeName(Name, 40)}]";

    public ChannelMixExport(AudioStreamInfo source, IEnumerable<ExperimentalChannel> channels,
        string name, double masterGain = 1)
    {
        _channels = channels.ToArray();
        _layout = source.ChannelLayout;
        Name = string.IsNullOrWhiteSpace(name) ? "Mix" : name.Trim();
        if (Name.Length > 80 || Name.Any(char.IsControl)) throw new InvalidDataException("ミックス名は80文字以内で入力してください。");
        MasterGain = masterGain;
        Validate(source);
    }
    public void Validate(AudioStreamInfo source)
    {
        var names = StereoMixSettings.ChannelNames(source);
        if (!StereoMixSettings.Supports(source) || source.ChannelLayout != _layout ||
            !_channels.Select(x => x.Code).SequenceEqual(names) ||
            !double.IsFinite(MasterGain) || MasterGain is < 0 or > 2 ||
            _channels.Any(x => !double.IsFinite(x.Gain) || x.Gain is < 0 or > 1 ||
                               !double.IsFinite(x.Pan) || x.Pan is < -1 or > 1))
            throw new InvalidDataException("ミキサーの設定または音源のチャンネル配置が不正です。");
    }
    public string Filter(AudioStreamInfo source)
    {
        Validate(source);
        var left = new double[source.Channels]; var right = new double[source.Channels];
        StereoPreviewMixer.BuildWeights(StereoMixSettings.ChannelNames(source),
            new PreviewMixState(StereoMixSettings.Default, null, _channels), left, right);
        string Terms(double[] coefficients) => string.Join('+', coefficients.Select((gain, i) =>
            $"{(gain * MasterGain).ToString("0.################", CultureInfo.InvariantCulture)}*c{i}"));
        // Keep summing in floating point. Integer encoding saturates at full scale;
        // there is no auto-normalization, limiter, or integer wrap-around.
        return $"aformat=sample_fmts=dbl,pan=stereo|c0={Terms(left)}|c1={Terms(right)}";
    }
    public void AddTags(List<string> arguments)
    {
        var json = JsonSerializer.Serialize(new { Version = 2, Layout = _layout,
            Name, GainLaw = "independent-unity", PanLaw = "equal-power", MasterGain, Channels = _channels });
        arguments.AddRange(["-metadata", "DOWNMIX=individual channel mixer to stereo",
            "-metadata", $"MIXER_VARIANT={Name}", "-metadata", $"MIXER_SETTINGS={json}"]);
    }
}
