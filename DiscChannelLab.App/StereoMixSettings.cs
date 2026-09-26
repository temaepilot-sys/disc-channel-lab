using System.Globalization;

namespace Disc2Flac;

/// <summary>Relative channel weights with a fixed gain based on the default mix.</summary>
public sealed record StereoMixSettings(double Center, double Surround, double Lfe, double Front = 1)
{
    public static StereoMixSettings Default { get; } = new(1 / Math.Sqrt(2), 1 / Math.Sqrt(2), 0);

    // FFmpeg standard layouts containing front left/right. Unknown layouts are not guessed.
    private static readonly IReadOnlyDictionary<string, string> Layouts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["2.1"] = "FL FR LFE", ["3.0"] = "FL FR FC", ["3.0(back)"] = "FL FR BC",
        ["3.1"] = "FL FR FC LFE", ["4.0"] = "FL FR FC BC", ["4.1"] = "FL FR FC LFE BC",
        ["quad"] = "FL FR BL BR", ["quad(side)"] = "FL FR SL SR",
        ["5.0"] = "FL FR FC BL BR", ["5.0(side)"] = "FL FR FC SL SR",
        ["5.1"] = "FL FR FC LFE BL BR", ["5.1(back)"] = "FL FR FC LFE BL BR",
        ["5.1(side)"] = "FL FR FC LFE SL SR",
        ["6.0"] = "FL FR FC BC SL SR", ["6.0(front)"] = "FL FR FLC FRC SL SR",
        ["hexagonal"] = "FL FR FC BL BR BC",
        ["6.1"] = "FL FR FC LFE BC SL SR", ["6.1(back)"] = "FL FR FC LFE BL BR BC",
        ["6.1(front)"] = "FL FR LFE FLC FRC SL SR",
        ["7.0"] = "FL FR FC BL BR SL SR", ["7.0(front)"] = "FL FR FC FLC FRC SL SR",
        ["7.1"] = "FL FR FC LFE BL BR SL SR",
        ["7.1(wide)"] = "FL FR FC LFE BL BR FLC FRC",
        ["7.1(wide-side)"] = "FL FR FC LFE FLC FRC SL SR",
        ["octagonal"] = "FL FR FC BL BR BC SL SR",
        ["3.1.2"] = "FL FR FC LFE TFL TFR",
        ["5.1.2"] = "FL FR FC LFE SL SR TFL TFR",
        ["5.1.2(back)"] = "FL FR FC LFE BL BR TFL TFR"
    };

    public static bool Supports(AudioStreamInfo stream) => stream.Channels is >= 3 and <= 8 &&
        Layouts.TryGetValue(stream.ChannelLayout, out var layout) &&
        layout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == stream.Channels;

    public static IReadOnlyList<string> ChannelNames(AudioStreamInfo stream) => Supports(stream)
        ? Layouts[stream.ChannelLayout].Split(' ') : [];

    public static string SoloFilter(AudioStreamInfo stream, string channel, bool stereo)
    {
        if (!ChannelNames(stream).Contains(channel, StringComparer.Ordinal))
            throw new InvalidOperationException("選択したチャンネルは音源にありません。");
        return stereo ? $"pan=stereo|FL={channel}|FR={channel}" : $"pan=mono|c0={channel}";
    }

    public string PanFilter(AudioStreamInfo stream)
    {
        if (!Supports(stream)) throw new InvalidOperationException("このチャンネル配置のステレオミックスには対応していません。");
        if (!Valid(Front) || !Valid(Center) || !Valid(Surround) || !Valid(Lfe))
            throw new ArgumentOutOfRangeException(nameof(Center), "ミックス比率は0～100%で指定してください。");
        var channels = Layouts[stream.ChannelLayout].Split(' ');
        var left = Terms(channels, "FL", ["BL", "SL", "TFL"], "FLC",
            ReferenceTotal(channels, left: true));
        var right = Terms(channels, "FR", ["BR", "SR", "TFR"], "FRC",
            ReferenceTotal(channels, left: false));
        return $"pan=stereo|FL={left}|FR={right}";
    }

    internal static double ReferenceTotal(IReadOnlyList<string> channels, bool left)
    {
        var surrounds = left ? new[] { "BL", "SL", "TFL" } : new[] { "BR", "SR", "TFR" };
        var wide = left ? "FLC" : "FRC";
        var total = Default.Front;
        if (channels.Contains(wide)) total += 0.5 * Default.Front;
        if (channels.Contains("FC")) total += Default.Center;
        if (surrounds.Any(channels.Contains)) total += Default.Surround;
        if (channels.Contains("BC")) total += Default.Surround / 2;
        if (channels.Contains("LFE")) total += Default.Lfe;
        return total;
    }

    private string Terms(string[] channels, string front, string[] surrounds, string wide, double divisor)
    {
        var parts = new List<string> { $"{Format(Front / divisor)}*{front}" };
        if (channels.Contains(wide)) parts.Add($"{Format(0.5 * Front / divisor)}*{wide}");
        if (Center > 0 && channels.Contains("FC")) parts.Add($"{Format(Center / divisor)}*FC");
        var presentSurrounds = surrounds.Where(channels.Contains).ToArray();
        if (Surround > 0)
        {
            foreach (var channel in presentSurrounds)
                parts.Add($"{Format(Surround / presentSurrounds.Length / divisor)}*{channel}");
            if (channels.Contains("BC")) parts.Add($"{Format(Surround / 2 / divisor)}*BC");
        }
        if (Lfe > 0 && channels.Contains("LFE")) parts.Add($"{Format(Lfe / divisor)}*LFE");
        return string.Join('+', parts);
    }

    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class ChannelChoice(string? code, string label) : System.ComponentModel.INotifyPropertyChanged
{
    public string? Code { get; } = code;
    public string Label => LanguageService.T(label);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Label)));

    public static string Describe(string channel) => LanguageService.T(DescribeRaw(channel));

    public static string DescribeRaw(string channel) => channel switch
    {
        "FL" => "FL · 前方左", "FR" => "FR · 前方右", "FC" => "FC · センター",
        "LFE" => "LFE · 低音", "BL" => "BL · 後方左", "BR" => "BR · 後方右",
        "SL" => "SL · 側方左", "SR" => "SR · 側方右", "BC" => "BC · 後方中央",
        "FLC" => "FLC · 前方左中央", "FRC" => "FRC · 前方右中央",
        "TFL" => "TFL · 上方前左", "TFR" => "TFR · 上方前右",
        _ => channel
    };
}
