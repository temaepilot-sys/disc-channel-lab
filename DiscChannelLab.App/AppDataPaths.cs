using System.Text.RegularExpressions;

namespace Disc2Flac;

/// <summary>Current storage location and read-only compatibility with earlier app names.</summary>
public sealed class AppDataPaths(string localApplicationData)
{
    public static AppDataPaths Current { get; } = new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string TemporaryRoot => Path.Combine(Path.GetTempPath(), "DiscChannelLab");
    public string Root { get; } = Path.Combine(localApplicationData, "DiscChannelLab");
    public string LegacyRoot { get; } = Path.Combine(localApplicationData, "BDDVD2Flac");
    public string EarlierTrackEdits { get; } = Path.Combine(localApplicationData, "Disc2Flac", "track-edits");
    public string TrackEdits => Path.Combine(Root, "track-edits");
    public string LegacyTrackEdits => Path.Combine(LegacyRoot, "track-edits");

    public string PreferencePath(string name) => Path.Combine(Root, name);
    public string PreferenceReadPath(string name)
    {
        var path = PreferencePath(name);
        return File.Exists(path) ? path : Path.Combine(LegacyRoot, name);
    }

    public LegacySettingsImportResult ImportLegacySettings()
    {
        int copied = 0;
        var errors = new List<string>();
        void CopyMissing(string source, string destination)
        {
            string? temporary = null;
            try
            {
                if (!File.Exists(source) || File.Exists(destination)) return;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) return;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(source, temporary);
                // Complete each copy before making it visible. Never overwrite a new-format app's edits.
                try { File.Move(temporary, destination, overwrite: false); copied++; }
                catch (IOException) when (File.Exists(destination)) { /* Another instance imported or saved it. */ }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add($"{source}: {ex.Message}"); }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        try
        {
            if (!Directory.Exists(LegacyRoot) || (File.GetAttributes(LegacyRoot) & FileAttributes.ReparsePoint) != 0)
                return new(copied, errors);
            foreach (var name in new[] { "language.txt", "theme-v2.txt" })
                CopyMissing(Path.Combine(LegacyRoot, name), PreferencePath(name));
            if (Directory.Exists(LegacyTrackEdits) && (File.GetAttributes(LegacyTrackEdits) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var disc in Directory.EnumerateDirectories(LegacyTrackEdits))
                {
                    try
                    {
                        var key = Path.GetFileName(disc);
                        if (!Regex.IsMatch(key, "^[0-9A-F]{64}$") || (File.GetAttributes(disc) & FileAttributes.ReparsePoint) != 0) continue;
                        foreach (var source in Directory.EnumerateFiles(disc, "*.json", SearchOption.TopDirectoryOnly))
                            CopyMissing(source, Path.Combine(TrackEdits, key, Path.GetFileName(source)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors.Add($"{disc}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { errors.Add(ex.Message); }
        // Logs and tool caches are not settings; leave them in place for the old app.
        return new(copied, errors);
    }
}

public sealed record LegacySettingsImportResult(int CopiedFiles, IReadOnlyList<string> Errors);
