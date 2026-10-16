using System.Text.Json;
using System.Text.RegularExpressions;

namespace Disc2Flac;

public sealed partial class AppDataPaths
{
    public string HeadphonesLabRoot { get; } = Path.Combine(localApplicationData, "DiscChannelLab.HeadphonesLab");

    /// <summary>Import the approved trial's preferences once, preserving overwritten metadata in a backup.</summary>
    public LegacySettingsImportResult ImportHeadphoneSettings()
    {
        var errors = new List<string>();
        int copied = 0;
        var marker = PreferencePath("headphones-import-v1.txt");
        var backup = Path.Combine(Root, "imports", "headphones-lab-backup");
        bool SafeDirectory(string p) => Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0;
        void CopyJson(string source, string target, bool headphonePreferences = false)
        {
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) return;
            if (new FileInfo(source).Length > 16 * 1024 * 1024) return;
            if (File.Exists(target) && (headphonePreferences || File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source))) return;
            var contents = File.ReadAllText(source);
            using var json = JsonDocument.Parse(contents);
            if (headphonePreferences)
                contents = JsonSerializer.Serialize((JsonSerializer.Deserialize<HeadphoneOptions>(contents) ?? new()).Validated() with { Enabled = false }, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
                var saved = Path.Combine(backup, Path.GetRelativePath(Root, target));
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                if (!File.Exists(saved)) File.Copy(target, saved);
            }
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, contents); File.Move(temporary, target, true); copied++; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        try
        {
            if (File.Exists(marker) || !SafeDirectory(HeadphonesLabRoot)) return new(copied, errors);
            CopyJson(Path.Combine(HeadphonesLabRoot, "headphones.json"), PreferencePath("headphones.json"), true);
            var edits = Path.Combine(HeadphonesLabRoot, "track-edits");
            if (SafeDirectory(edits))
                foreach (var disc in Directory.EnumerateDirectories(edits))
                {
                    var key = Path.GetFileName(disc);
                    if (!Regex.IsMatch(key, "^[0-9A-Fa-f]{64}$") || !SafeDirectory(disc)) continue;
                    var targetDirectory = Path.Combine(TrackEdits, key);
                    if (Directory.Exists(targetDirectory) && !SafeDirectory(targetDirectory)) continue;
                    foreach (var source in Directory.EnumerateFiles(disc, "*.json"))
                        CopyJson(source, Path.Combine(targetDirectory, Path.GetFileName(source)));
                }
            Directory.CreateDirectory(Root);
            File.WriteAllText(marker, $"Imported {copied} settings files. Existing replaced metadata is backed up in imports/headphones-lab-backup.\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { errors.Add(ex.Message); }
        return new(copied, errors);
    }
}
