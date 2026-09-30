using System.Text.Json;

namespace DiscChannelLab.Visualizer;

public sealed record ViewerPreferences
{
    public int Version { get; init; } = 1;
    public string Signal { get; init; } = "post";
    public string View { get; init; } = "orbit";
    public double Spread { get; init; } = 20;
    public double ParticleSize { get; init; } = 1;
    public double Transparency { get; init; }
    public double Density { get; init; } = 1.3;
    public double Persistence { get; init; } = 1;
    public double DemoVolume { get; init; } = 25;
    public bool Grid { get; init; } = true;
    public bool Labels { get; init; } = true;
    public bool LfeRipples { get; init; } = true;
    public bool HideUi { get; init; }
    public CameraPreferences Camera { get; init; } = new();

    public void Validate()
    {
        if (Version != 1 || Signal is not ("post" or "input" or "output") ||
            View is not ("orbit" or "bird" or "top" or "listener" or "front" or "back"))
            throw new InvalidDataException("Unsupported viewer settings version or view mode.");
        Check(Spread, 0, 90); Check(ParticleSize, .3, 3); Check(Transparency, 0, 100);
        Check(Density, .3, 3); Check(Persistence, .5, 1.8); Check(DemoVolume, 0, 100);
        if (Camera is null || Camera.Target is null || Camera.Target.Length != 3)
            throw new InvalidDataException("Invalid saved camera.");
        Check(Camera.Yaw, 0, 2 * Math.PI); Check(Camera.Pitch, -Math.PI / 2, Math.PI / 2);
        Check(Camera.Distance, 2, 28);
        if (!Camera.Target.All(double.IsFinite)) throw new InvalidDataException("Invalid saved camera target.");
    }

    static void Check(double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException("A viewer setting is outside its supported range.");
    }
}

public sealed record CameraPreferences
{
    public double Yaw { get; init; } = .48;
    public double Pitch { get; init; } = .56;
    public double Distance { get; init; } = 12.8;
    public double[] Target { get; init; } = [0, .8, 0];
}

public sealed class ViewerPreferencesStore(string path)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly object gate = new();

    public ViewerPreferences? Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Saved viewer settings are too large.");
            var value = JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Saved viewer settings are empty.");
            value.Validate();
            return value;
        }
    }

    public void Save(ViewerPreferences value)
    {
        value.Validate();
        lock (gate)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
                // Replace only after the complete JSON has been written successfully.
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
