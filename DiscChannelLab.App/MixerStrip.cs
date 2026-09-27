using System.ComponentModel;

namespace Disc2Flac;

public sealed record ExperimentalChannel(
    [property: System.Text.Json.Serialization.JsonRequired] string Code,
    [property: System.Text.Json.Serialization.JsonRequired] double Gain,
    [property: System.Text.Json.Serialization.JsonRequired] double Pan,
    [property: System.Text.Json.Serialization.JsonRequired] bool Muted);
// All values refer to the same PCM window and are displayed together on the playback clock.
public sealed record MixerMeterFrame(double Seconds, double[] Input, double[] InputRms,
    double[] Left, double[] Right, double[] Output);

public sealed class MixerStrip : INotifyPropertyChanged
{
    private readonly Action _apply;
    private double _level;
    private double _pan;
    private bool _muted;
    public ChannelMeter Input { get; }
    public ChannelMeter PostLeft { get; } = new("L", "L");
    public ChannelMeter PostRight { get; } = new("R", "R");
    public string Code => Input.Code;
    public string Description => Input.Description;
    public double DefaultPan => Code switch
    {
        "FL" or "FLC" or "BL" or "SL" or "TFL" => -100,
        "FR" or "FRC" or "BR" or "SR" or "TFR" => 100,
        _ => 0
    };
    public MixerStrip(ChannelMeter input, double level, bool muted, Action apply)
    { Input = input; _level = level; _muted = muted; _pan = DefaultPan; _apply = apply; }

    public double Level
    {
        get => _level;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "0–100%");
            if (_level == value) return;
            _level = value; Notify(nameof(Level)); Notify(nameof(Fader)); _apply();
        }
    }
    public double Fader
    {
        get => 100 * Math.Sqrt(Level / 100);
        set => Level = 100 * VolumeCurve.Gain(Math.Clamp(value, 0, 100), perceived: true);
    }
    public double Pan
    {
        get => _pan;
        set
        {
            if (!double.IsFinite(value) || value is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "L100–R100");
            if (_pan == value) return;
            _pan = value; Notify(nameof(Pan)); Notify(nameof(PanText)); _apply();
        }
    }
    public string PanText => Pan < -0.05 ? $"L {Math.Abs(Pan):0.#}" : Pan > 0.05 ? $"R {Pan:0.#}" : "Center";
    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Notify(nameof(Muted)); _apply(); }
    }
    public ExperimentalChannel Snapshot() => new(Code, Level / 100, Pan / 100, Muted);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    public void RefreshLanguage() => Notify(nameof(Description));
}
