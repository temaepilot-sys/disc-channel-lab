using System.Text.Json;
using System.Text.Json.Serialization;

namespace Disc2Flac;

// Standalone presets intentionally contain no disc identity, paths or track metadata.
public sealed record MixerPreset
{
    public required string Format { get; init; }
    public required int Version { get; init; }
    public required string Name { get; init; }
    public required double MasterPercent { get; init; }
    public required bool PerceivedMaster { get; init; }
    public required bool IncludeMasterInExport { get; init; }
    public required ExperimentalChannel[] Channels { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public void Validate()
    {
        if (Format != "DiscChannelLab.MixerPreset" || Version is not (1 or 2) || Name is null || Name.Length > 80 ||
            !double.IsFinite(MasterPercent) || MasterPercent is < 0 or > 200 ||
            Channels is null || Channels.Length is < 3 or > 8 ||
            Channels.Any(c => c is null || string.IsNullOrWhiteSpace(c.Code) ||
                !double.IsFinite(c.Gain) || c.Gain is < 0 or > 1 ||
                !double.IsFinite(c.Pan) || c.Pan is < -1 or > 1 || (c.Muted && c.Solo) || (Version == 1 && c.Solo)) ||
            Channels.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count() != Channels.Length)
            throw new InvalidDataException(LanguageService.T("ミキサー設定ファイルの形式または値が不正です。"));
    }

    public static MixerPreset Load(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException(LanguageService.T("ミキサー設定ファイルの形式または値が不正です。"));
        var preset = JsonSerializer.Deserialize<MixerPreset>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException(LanguageService.T("ミキサー設定ファイルの形式または値が不正です。"));
        preset.Validate();
        return preset;
    }

    public void Save(string path)
    {
        Validate();
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
