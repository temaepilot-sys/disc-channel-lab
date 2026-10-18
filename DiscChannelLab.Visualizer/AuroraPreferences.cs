using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscChannelLab.Visualizer;

[JsonConverter(typeof(AuroraPreferencesConverter))]
public sealed record AuroraPreferences
{
    public string Layout { get; init; } = "curtain";
    public string Preset { get; init; } = "calm";
    public double Intensity { get; init; } = .75;
    public double Opacity { get; init; } = .55;
    public double Density { get; init; } = .6;
    public double Speed { get; init; } = .35;
    public double Detail { get; init; } = .4;
    public double Persistence { get; init; } = .65;
    public double HybridAmount { get; init; } = .25;
    public string Quality { get; init; } = "medium";
    public string Palette { get; init; } = "spectrum";
    public string FlowDirection { get; init; } = "none";

    public AuroraPreferences Normalize() => this with
    {
        Layout = Layout is "flow" ? "flow" : "curtain",
        Preset = Preset is "spatial" or "live" or "custom" ? Preset : "calm",
        Quality = Quality is "low" or "high" ? Quality : "medium",
        Palette = Palette is "calm" or "warm" or "spark" ? Palette : "spectrum",
        FlowDirection = FlowDirection is "down" or "up" ? FlowDirection : "none",
        Intensity = Limit(Intensity, .75, 2), Opacity = Limit(Opacity, .55, 1), Density = Limit(Density, .6, 1),
        Speed = Limit(Speed, .35, 2), Detail = Limit(Detail, .4, 1), Persistence = Limit(Persistence, .65, 1),
        HybridAmount = Limit(HybridAmount, .25, 1)
    };
    static double Limit(double x, double fallback, double max) => double.IsFinite(x) ? Math.Clamp(x, 0, max) : fallback;
}

// Tolerate malformed NEW fields without making previously valid camera/particle settings unusable.
public sealed class AuroraPreferencesConverter : JsonConverter<AuroraPreferences>
{
    public override bool HandleNull => true;
    public override AuroraPreferences Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        double Number(string key, double fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double x) ? x : fallback;
        string Text(string key, string fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        return new AuroraPreferences
        {
            Layout = Text("layout", "curtain"), Preset = Text("preset", "calm"), Quality = Text("quality", "medium"), Palette = Text("palette", "spectrum"),
            FlowDirection = Text("flowDirection", "none"),
            Intensity = Number("intensity", .75), Opacity = Number("opacity", .55), Density = Number("density", .6), Speed = Number("speed", .35),
            Detail = Number("detail", .4), Persistence = Number("persistence", .65), HybridAmount = Number("hybridAmount", .25)
        }.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, AuroraPreferences value, JsonSerializerOptions options)
    {
        var v = (value ?? new()).Normalize();
        writer.WriteStartObject();
        writer.WriteString("layout", v.Layout); writer.WriteString("preset", v.Preset);
        writer.WriteString("quality", v.Quality); writer.WriteString("palette", v.Palette);
        writer.WriteString("flowDirection", v.FlowDirection);
        writer.WriteNumber("intensity", v.Intensity); writer.WriteNumber("opacity", v.Opacity); writer.WriteNumber("density", v.Density);
        writer.WriteNumber("speed", v.Speed); writer.WriteNumber("detail", v.Detail); writer.WriteNumber("persistence", v.Persistence);
        writer.WriteNumber("hybridAmount", v.HybridAmount); writer.WriteEndObject();
    }
}

public sealed class VisualStyleConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    public static string Normalize(string? value) => value is "hybrid" or "spark-field" or "aurora-spark" ? value : "particles";
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return Normalize(doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null);
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Normalize(value));
}
