using Disc2Flac;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

internal static class SpatialPromotionTests
{
    public static void Run(string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root); List<string> checks = [];
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        void Write(string path, string value, int ageHours = 0)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours)); }
        var paths = new AppDataPaths(Path.Combine(root, "profiles-" + Guid.NewGuid().ToString("N")));
        string Trial(string relative) => Path.Combine(paths.SpatialLabRoot, relative);
        string Current(string relative) => paths.PreferencePath(relative);
        Check(paths.ImportSpatialSettings() is { CopiedFiles: 0, Errors.Count: 0 } && !Directory.Exists(paths.Root), "Fresh installs do not invent a trial profile or migration marker");
        string key = new('C', 64), album = "track-edits/" + key + "/album.json", title = "track-edits/" + key + "/title.json";
        Write(Current(album), "\"Original album\"", 48); Write(Trial(album), "\"Trial album\"", 1);
        Write(Current(title), "\"Newer main title\""); Write(Trial(title), "\"Older trial title\"", 24);
        Write(Trial("track-edits/" + key + "/chapter.json"), "[\"Chapter name\"]", 1);
        string manifest = "{\n  \"Name\": \"Room\", \"File\": \"D:/responses.sofa\"\n}";
        Write(Trial("profiles/brir/response.json"), manifest, 1); Write(Trial("profiles/brir-last.json"), manifest, 1);
        Write(Trial("profiles/hrtf-library.json"), "[{\"Path\":\"D:/my-hrtf.sofa\"}]", 1);
        Write(Current("headphones.json"), "{\"FrontAngle\":30}", 48);
        Write(Trial("headphones.json"), JsonSerializer.Serialize(new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.MeasuredBrir, FrontAngle = 65, UdpPort = 4243, BrirPath = Trial("profiles/brir/response.json"), BrirHash = "retained-manifest-hash" }), 1);
        Write(Trial("truehdd-tool.json"), JsonSerializer.Serialize("D:/tools/truehdd.exe"), 1);
        Write(Trial("language.txt"), "ja", 1); Write(Trial("theme-v2.txt"), "dark", 1);
        Write(Trial("sacd-cache/large.flac"), "Not settings"); Write(Trial("tools/ffmpeg.exe"), "Not settings"); Write(Trial("logs/private.log"), "Not settings");
        var before = Directory.GetFiles(paths.SpatialLabRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var imported = paths.ImportSpatialSettings();
        Check(imported.Errors.Count == 0 && imported.CopiedFiles == 9, "Promotion imports only supported settings and newer metadata");
        var options = JsonSerializer.Deserialize<HeadphoneOptions>(File.ReadAllText(Current("headphones.json")))!;
        Check(!options.Enabled && options.FrontAngle == 65 && options.UdpPort == 4243 && options.Method == HeadphoneMethod.MeasuredBrir, "Saved tuning and tracking port migrate while startup remains ordinary stereo");
        Check(Path.GetFullPath(options.BrirPath) == Path.GetFullPath(Current("profiles/brir/response.json")) && options.BrirHash == "retained-manifest-hash", "Stored BRIR profile references follow the formal settings folder");
        Check(File.ReadAllText(Current("profiles/brir/response.json")) == manifest && File.ReadAllText(Current("profiles/brir-last.json")) == manifest, "Unchanged BRIR manifest bytes and their hash identity are preserved");
        Check(File.ReadAllText(Current("profiles/hrtf-library.json")).Contains("D:/my-hrtf.sofa") && File.ReadAllText(Current("truehdd-tool.json")).Contains("D:/tools/truehdd.exe"), "External dataset and tool references retain their actual locations");
        Check(File.ReadAllText(Current(album)) == "\"Trial album\"" && File.ReadAllText(Current(title)) == "\"Newer main title\"", "Newer trial metadata migrates while newer formal edits win");
        Check(File.ReadAllText(Current("imports/spatial-lab-backup/" + album)) == "\"Original album\"" && File.ReadAllText(Current("imports/spatial-lab-backup/headphones.json")).Contains("30"), "Every replaced original is retained in a promotion backup");
        Check(!Directory.Exists(Current("sacd-cache")) && !Directory.Exists(Current("tools")) && !Directory.Exists(Current("logs")), "Audio caches, executable files and logs are never migrated");
        Check(before.All(p => p.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key)))) , "All trial files remain byte-identical");
        Write(Current(album), "\"Later official edit\""); Write(Trial(album), "\"Later old-app edit\"");
        Check(paths.ImportSpatialSettings().CopiedFiles == 0 && File.ReadAllText(Current(album)) == "\"Later official edit\"", "One-time migration never replaces edits made after promotion");
        var partial = new AppDataPaths(Path.Combine(root, "partial-" + Guid.NewGuid().ToString("N")));
        string bad = Path.Combine(partial.SpatialLabRoot, "profiles/bad.json"); Write(bad, "invalid JSON");
        var failed = partial.ImportSpatialSettings(); Check(failed.Errors.Count == 1 && !File.Exists(partial.PreferencePath("spatial-lab-import-v1.txt")), "A malformed setting reports an error and leaves migration retryable");
        Write(bad, "{}" ); Check(partial.ImportSpatialSettings() is { CopiedFiles: 1, Errors.Count: 0 }, "A corrected setting is imported on the next attempt");
        Check(!Directory.EnumerateFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Any(), "Migration leaves no partial output files");
        SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks });
    }
}
