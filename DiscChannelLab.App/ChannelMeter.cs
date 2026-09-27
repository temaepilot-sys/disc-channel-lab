using System.ComponentModel;

namespace Disc2Flac;

public sealed class ChannelMeter(string code, string description) : INotifyPropertyChanged
{
    private double _peakDb = double.NegativeInfinity;
    private double _rmsDb = double.NegativeInfinity;

    public string Code { get; } = code;
    public string Description => LanguageService.T(description);
    public double PeakPercent => double.IsFinite(_peakDb) ? Math.Clamp((_peakDb + 60) * 100 / 60, 0, 100) : 0;
    public string PeakText => Format(_peakDb);
    public bool IsClipping => double.IsFinite(_peakDb) && _peakDb >= 0;
    public string Details => LanguageService.T($"{Description} · ピーク {Format(_peakDb)} / 実効 {Format(_rmsDb)}");
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLanguage()
    {
        PropertyChanged?.Invoke(this, new(nameof(Description)));
        PropertyChanged?.Invoke(this, new(nameof(Details)));
    }

    public void Update(double peakDb, double rmsDb)
    {
        peakDb = double.IsFinite(peakDb) ? Math.Clamp(peakDb, -120, 12) : double.NegativeInfinity;
        rmsDb = double.IsFinite(rmsDb) ? Math.Clamp(rmsDb, -120, 12) : double.NegativeInfinity;
        if (_peakDb == peakDb && _rmsDb == rmsDb) return;
        _peakDb = peakDb;
        _rmsDb = rmsDb;
        PropertyChanged?.Invoke(this, new(nameof(PeakPercent)));
        PropertyChanged?.Invoke(this, new(nameof(PeakText)));
        PropertyChanged?.Invoke(this, new(nameof(IsClipping)));
        PropertyChanged?.Invoke(this, new(nameof(Details)));
    }

    private static string Format(double db) => double.IsFinite(db) ? $"{db:0} dBFS" : "−∞ dBFS";
}
