using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Disc2Flac;

public sealed partial class AppDataPaths
{
    public string SpatialLabRoot { get; } = Path.Combine(localApplicationData, "DiscChannelLab.SpatialLab");

    /// <summary>Import the approved Spatial Lab settings once, retaining replaced settings and the entire old profile.</summary>
    public LegacySettingsImportResult ImportSpatialSettings()
    {
        int copied = 0; List<string> errors = [];
        string marker = PreferencePath("spatial-lab-import-v1.txt");
        string backup = PreferencePath("imports/spatial-lab-backup");
        bool SafeTree(string path, string anchor)
        {
            path = Path.GetFullPath(path); anchor = Path.GetFullPath(anchor);
            if (path != anchor && !path.StartsWith(anchor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current, anchor, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        JsonNode? Rebase(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray()) { var before = obj[key]; var after = Rebase(before); if (!ReferenceEquals(before, after)) obj[key] = after; }
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++) { var before = array[i]; var after = Rebase(before); if (!ReferenceEquals(before, after)) array[i] = after; }
            else if (node is JsonValue value && value.TryGetValue<string>(out var text) && Path.IsPathFullyQualified(text))
            {
                string relative = Path.GetRelativePath(SpatialLabRoot, text);
                if (relative.StartsWith("profiles" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(relative).Equals(".json", StringComparison.OrdinalIgnoreCase) && SafeTree(text, SpatialLabRoot))
                    return JsonValue.Create(Path.Combine(Root, relative));
            }
            return node;
        }
        void CopySetting(string source, string relative)
        {
            string target = PreferencePath(relative), saved = Path.Combine(backup, relative); string? temporary = null;
            try
            {
                if (!File.Exists(source) || !SafeTree(source, SpatialLabRoot) || !SafeTree(target, Root) || !SafeTree(saved, Root)) return;
                if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("Settings file exceeds 16 MiB: " + relative);
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source)) return;
                string contents = File.ReadAllText(source);
                if (Path.GetExtension(relative).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonNode.Parse(contents); string original = json?.ToJsonString() ?? "null";
                    json = Rebase(json);
                    // Preserve unchanged BRIR manifest bytes: their SHA-256 is part of saved renderer identity.
                    if ((json?.ToJsonString() ?? "null") != original) contents = json?.ToJsonString(SpatialJson.Options) ?? "null";
                    if (relative == "headphones.json")
                        contents = JsonSerializer.Serialize((JsonSerializer.Deserialize<HeadphoneOptions>(contents) ?? new()).Validated() with { Enabled = false }, SpatialJson.Options);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && !File.Exists(saved))
                { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target, saved, false); }
                temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, contents); File.Move(temporary, target, true); temporary = null;
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { errors.Add(relative + ": " + ex.Message); }
            finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } }
        }
        void Profiles(string directory, int depth)
        {
            if (depth > 3 || !Directory.Exists(directory) || !SafeTree(directory, SpatialLabRoot)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json")) CopySetting(file, Path.GetRelativePath(SpatialLabRoot, file));
            foreach (string child in Directory.EnumerateDirectories(directory)) Profiles(child, depth + 1);
        }
        try
        {
            if (File.Exists(marker) || !Directory.Exists(SpatialLabRoot) || !SafeTree(SpatialLabRoot, localApplicationData) || !SafeTree(Root, localApplicationData)) return new(0, errors);
            Profiles(Path.Combine(SpatialLabRoot, "profiles"), 0);
            foreach (string name in new[] { "headphones.json", "sacd-tool.json", "truehdd-tool.json", "language.txt", "theme-v2.txt" })
                CopySetting(Path.Combine(SpatialLabRoot, name), name);
            string edits = Path.Combine(SpatialLabRoot, "track-edits");
            if (Directory.Exists(edits) && SafeTree(edits, SpatialLabRoot))
                foreach (string disc in Directory.EnumerateDirectories(edits))
                    if (Regex.IsMatch(Path.GetFileName(disc), "^[0-9A-Fa-f]{64}$") && SafeTree(disc, SpatialLabRoot))
                        foreach (string file in Directory.EnumerateFiles(disc, "*.json")) CopySetting(file, Path.GetRelativePath(SpatialLabRoot, file));
            if (errors.Count == 0)
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(marker, $"Imported {copied} Spatial Lab settings files. Replaced files are retained in imports/spatial-lab-backup. Audio, tools and caches were not copied.\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors.Add(ex.Message); }
        return new(copied, errors);
    }
}
