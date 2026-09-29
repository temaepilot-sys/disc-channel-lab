using System.ComponentModel;

namespace Disc2Flac;

/// <summary>Links absolute channel gain; other channel controls remain independent.</summary>
public sealed class MixerFaderLink : INotifyPropertyChanged
{
    public static IReadOnlyList<string> SupportedPairs { get; } = ["FL/FR", "SL/SR", "BL/BR"];
    private readonly MixerStrip _left;
    private readonly MixerStrip _right;
    private readonly Action _apply;
    private bool _isLinked;
    public string Pair => $"{_left.Code}/{_right.Code}";
    public string Label => $"{(IsLinked ? "✓ " : "")}{_left.Code} ↔ {_right.Code}";

    public MixerFaderLink(MixerStrip left, MixerStrip right, Action apply)
    {
        _left = left; _right = right; _apply = apply;
        left.FaderLink = right.FaderLink = this;
    }

    public bool IsLinked
    {
        get => _isLinked;
        set
        {
            if (_isLinked == value) return;
            _isLinked = value;
            // Enabling uses the left channel as the reference. Unlinking keeps both levels.
            if (value) SetLevel(_left.Level);
            PropertyChanged?.Invoke(this, new(nameof(IsLinked)));
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }

    internal void SetLevel(double value)
    {
        // Store both values before notifications or publishing a live audio snapshot.
        var leftChanged = _left.SetLevelSilently(value);
        var rightChanged = _right.SetLevelSilently(value);
        if (leftChanged) _left.NotifyLevel();
        if (rightChanged) _right.NotifyLevel();
        if (leftChanged || rightChanged) _apply();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
