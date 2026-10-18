using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscChannelLab.Visualizer;

[JsonConverter(typeof(SparkFieldPreferencesConverter))]
public sealed record SparkFieldPreferences
{
    public double Density { get; init; } = 1;
    public double Intensity { get; init; } = 1;
    public double Streak { get; init; } = .3;
    public double Spread { get; init; } = .65;
    public double Drift { get; init; } = .45;
    public double Afterglow { get; init; } = 1;
    public double Speed { get; init; } = 1;
    public double StreakLength { get; init; } = 1;
    public double Twinkle { get; init; } = .6;
    public string Quality { get; init; } = "medium";
    public string Preset { get; init; } = "surround";
    public SparkFieldPreferences Normalize() => this with
    {
        Density = Limit(Density, .1, 2.5, 1), Intensity = Limit(Intensity, .1, 2.5, 1),
        Streak = Limit(Streak, 0, 1, .3), Spread = Limit(Spread, 0, 1.5, .65),
        Drift = Limit(Drift, 0, 1.5, .45), Afterglow = Limit(Afterglow, 0, 2, 1),
        Speed = Limit(Speed, .25, 2.5, 1),
        StreakLength = Limit(StreakLength, .4, 1.8, 1), Twinkle = Limit(Twinkle, 0, 1, .6),
        Quality = Quality is "low" or "high" ? Quality : "medium",
        Preset = Preset is "calm" or "tesla" or "custom" ? Preset : "surround"
    };
    private static double Limit(double value, double low, double high, double fallback) => double.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
}

public sealed class SparkFieldPreferencesConverter : JsonConverter<SparkFieldPreferences>
{
    public override bool HandleNull => true;
    public override SparkFieldPreferences Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        double Number(string key, double fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) ? number : fallback;
        string Text(string key, string fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        return new SparkFieldPreferences { Density = Number("density", 1), Intensity = Number("intensity", 1),
            Streak = Number("streak", .3), Spread = Number("spread", .65), Drift = Number("drift", .45),
            Afterglow = Number("afterglow", 1), Speed = Number("speed", 1), StreakLength = Number("streakLength", 1), Twinkle = Number("twinkle", .6),
            Quality = Text("quality", "medium"), Preset = Text("preset", "surround") }.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, SparkFieldPreferences value, JsonSerializerOptions options)
    {
        var v = (value ?? new()).Normalize();
        writer.WriteStartObject(); writer.WriteNumber("density", v.Density); writer.WriteNumber("intensity", v.Intensity);
        writer.WriteNumber("streak", v.Streak); writer.WriteNumber("spread", v.Spread); writer.WriteNumber("drift", v.Drift);
        writer.WriteNumber("afterglow", v.Afterglow); writer.WriteNumber("speed", v.Speed); writer.WriteNumber("streakLength", v.StreakLength); writer.WriteNumber("twinkle", v.Twinkle);
        writer.WriteString("quality", v.Quality); writer.WriteString("preset", v.Preset); writer.WriteEndObject();
    }
}
