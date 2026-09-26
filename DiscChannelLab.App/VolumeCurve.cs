namespace Disc2Flac;

public static class VolumeCurve
{
    public static double Gain(double sliderPercent, bool perceived)
    {
        var percent = Math.Clamp(double.IsFinite(sliderPercent) ? sliderPercent : 100, 0, 200);
        var linear = percent / 100;
        return perceived && percent < 100 ? linear * linear : linear;
    }

    public static string Description(double sliderPercent, bool perceived)
    {
        var gain = Gain(sliderPercent, perceived);
        if (gain == 0) return LanguageService.T("実効ゲイン 0%（消音）");
        var decibels = 20 * Math.Log10(gain);
        return LanguageService.T($"実効ゲイン {gain * 100:0.#}%（{decibels:+0.0;-0.0;0.0} dB）");
    }
}
