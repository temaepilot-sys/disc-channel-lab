using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscChannelLab.Visualizer;

[JsonConverter(typeof(ElectricFlowPreferencesConverter))]
public sealed record ElectricFlowPreferences
{
    public double Intensity { get; init; } = .8;
    public double Spread { get; init; } = .65;
    public double Amplitude { get; init; } = 1;
    public double Emission { get; init; } = 1;
    public double Gravity { get; init; } = 1;
    public double Width { get; init; } = 1.3;
    public double Glow { get; init; } = 1;
    public string Quality { get; init; } = "medium";
    public ElectricFlowPreferences Normalize() => this with
    {
        Intensity = double.IsFinite(Intensity) ? Math.Clamp(Intensity, 0, 2) : .8,
        Spread = double.IsFinite(Spread) ? Math.Clamp(Spread, .2, 1.2) : .65,
        Amplitude = double.IsFinite(Amplitude) ? Math.Clamp(Amplitude, 0, 4) : 1,
        Emission = double.IsFinite(Emission) ? Math.Clamp(Emission, .1, 4) : 1,
        Gravity = double.IsFinite(Gravity) ? Math.Clamp(Gravity, .25, 2.5) : 1,
        Width = double.IsFinite(Width) ? Math.Clamp(Width, .4, 3) : 1.3,
        Glow = double.IsFinite(Glow) ? Math.Clamp(Glow, 0, 2) : 1,
        Quality = Quality is "low" or "high" ? Quality : "medium"
    };
}

public sealed class ElectricFlowPreferencesConverter : JsonConverter<ElectricFlowPreferences>
{
    public override bool HandleNull => true;
    public override ElectricFlowPreferences Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        double Number(string key, double fallback) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double value) ? value : fallback;
        var quality = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
        return new ElectricFlowPreferences { Intensity = Number("intensity", .8), Spread = Number("spread", .65),
            Amplitude = Number("amplitude", 1), Emission = Number("emission", 1), Gravity = Number("gravity", 1),
            Width = Number("width", 1.3), Glow = Number("glow", 1), Quality = quality ?? "medium" }.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, ElectricFlowPreferences value, JsonSerializerOptions options)
    {
        var v = (value ?? new()).Normalize();
        writer.WriteStartObject(); writer.WriteNumber("intensity", v.Intensity); writer.WriteNumber("spread", v.Spread);
        writer.WriteNumber("amplitude", v.Amplitude); writer.WriteNumber("emission", v.Emission); writer.WriteNumber("gravity", v.Gravity);
        writer.WriteNumber("width", v.Width); writer.WriteNumber("glow", v.Glow);
        writer.WriteString("quality", v.Quality); writer.WriteEndObject();
    }
}
