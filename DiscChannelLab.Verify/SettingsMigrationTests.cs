using System.IO;
using Disc2Flac;

public static class SettingsMigrationTests
{
    public static void Run(string output)
    {
        var root = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(root);
        var log = new AppLog(Path.Combine(root, "test-logs"));
        int passed = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL: " + label);
            passed++; Console.WriteLine("PASS: " + label);
        }
        Check(Path.GetFileName(paths.Root) == "DiscChannelLab" && paths.ImportLegacySettings().CopiedFiles == 0,
            "Fresh install uses DiscChannelLab and does not require legacy settings");
        Directory.CreateDirectory(paths.LegacyRoot);
        File.WriteAllText(Path.Combine(paths.LegacyRoot, "language.txt"), "ja");
        File.WriteAllText(Path.Combine(paths.LegacyRoot, "theme-v2.txt"), "light");
        Directory.CreateDirectory(Path.Combine(paths.LegacyRoot, "tools"));
        File.WriteAllText(Path.Combine(paths.LegacyRoot, "tools", "cache.txt"), "Do not migrate");
        var playlist = new PlaylistInfo { Id = 7, Clips = [], ChapterStarts = [0, 45000], DurationTicks = 135000 };
        var disc = new DiscAnalysis { Root = root, AlbumTitle = "Disc", DiscKey = new string('A', 64),
            Format = DiscFormat.DvdAudio, Playlists = [playlist] };
        var legacy = new TrackEditsStore(log, paths.LegacyTrackEdits);
        legacy.SaveAlbumTitle(disc, "Saved disc name");
        legacy.SaveTitleName(disc, playlist, "Saved title name");
        legacy.SaveChapterTitles(disc, playlist, ["First", "Second"]);
        var tracks = new[] { new TrackRow { Number = 1, StartTicks = 0, EndTicks = 135000,
            Title = "Song", Artist = "Artist", IsSelected = false, IsChapter = true } };
        legacy.Save(disc, playlist, tracks);
        playlist.MergeShortTail = false;
        legacy.Save(disc, playlist, tracks);
        legacy.SaveMergeShortTail(disc, playlist);
        var before = Directory.EnumerateFiles(paths.LegacyRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Check(paths.PreferenceReadPath("language.txt") == Path.Combine(paths.LegacyRoot, "language.txt"),
            "Preference read fallback is available before migration");
        var imported = paths.ImportLegacySettings();
        Check(imported.CopiedFiles == 8 && imported.Errors.Count == 0, "Import language, theme and all six disc edit files");
        Check(File.ReadAllText(paths.PreferencePath("language.txt")) == "ja" &&
            File.ReadAllText(paths.PreferencePath("theme-v2.txt")) == "light", "Language and theme retained");
        var current = new TrackEditsStore(log, paths: paths);
        Check(current.LoadAlbumTitle(disc) == "Saved disc name" && current.LoadTitleName(disc, playlist) == "Saved title name" &&
            current.LoadChapterTitles(disc, playlist)!.SequenceEqual(new[] { "First", "Second" }), "Disc, title and chapter names retained for DVD");
        var loaded = current.Load(disc, playlist)!;
        Check(loaded.Count == 1 && loaded[0].Title == "Song" && loaded[0].Artist == "Artist" &&
            !loaded[0].IsSelected && loaded[0].StartTicks == 0 && loaded[0].EndTicks == 135000,
            "Original-boundary track edits retain names, artists, selections and times");
        Check(!current.LoadMergeShortTail(disc, playlist), "Reading option retained");
        playlist.MergeShortTail = true;
        Check(current.Load(disc, playlist)![0].Title == "Song", "Merged-boundary track edits retained separately");
        Check(before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "All legacy files remain byte-identical");
        Check(!Directory.Exists(Path.Combine(paths.Root, "tools")), "Old tool cache is not copied");
        current.SaveAlbumTitle(disc, "New name");
        File.WriteAllText(paths.PreferencePath("language.txt"), "en");
        var repeated = paths.ImportLegacySettings();
        Check(repeated.CopiedFiles == 0 && current.LoadAlbumTitle(disc) == "New name" &&
            File.ReadAllText(paths.PreferenceReadPath("language.txt")) == "en", "Repeated import never overwrites current edits or preferences");

        var blockedPaths = new AppDataPaths(Path.Combine(root, "partial"));
        Directory.CreateDirectory(blockedPaths.LegacyRoot);
        File.WriteAllText(Path.Combine(blockedPaths.LegacyRoot, "language.txt"), "ja");
        var blockedLegacy = new TrackEditsStore(log, blockedPaths.LegacyTrackEdits);
        blockedLegacy.SaveAlbumTitle(disc, "Fallback name");
        playlist.MergeShortTail = false;
        blockedLegacy.SaveMergeShortTail(disc, playlist);
        Directory.CreateDirectory(blockedPaths.Root);
        File.WriteAllText(blockedPaths.TrackEdits, "blocks destination directory");
        var partial = blockedPaths.ImportLegacySettings();
        Check(partial.Errors.Count > 0 && File.ReadAllText(blockedPaths.PreferencePath("language.txt")) == "ja",
            "Partial import reports failure and retains successful copies");
        var blockedStore = new TrackEditsStore(log, paths: blockedPaths);
        Check(blockedStore.LoadAlbumTitle(disc) == "Fallback name" && !blockedStore.LoadMergeShortTail(disc, playlist),
            "Legacy read fallback survives a failed disc copy");
        File.Move(blockedPaths.TrackEdits, blockedPaths.TrackEdits + ".test-backup");
        Check(blockedPaths.ImportLegacySettings().CopiedFiles == 2 &&
            File.Exists(Path.Combine(blockedPaths.TrackEdits, disc.DiscKey, "album.json")), "Next import retries only missing files");

        var earlierDisc = new DiscAnalysis { Root = root, AlbumTitle = "Earlier", DiscKey = new string('B', 64), Playlists = [playlist] };
        new TrackEditsStore(log, paths.EarlierTrackEdits).SaveAlbumTitle(earlierDisc, "Earlier Blu-ray");
        Check(current.LoadAlbumTitle(earlierDisc) == "Earlier Blu-ray", "Earlier Disc2Flac Blu-ray compatibility preserved");
        Check(new TrackEditsStore(log, Path.Combine(root, "isolated"), paths).LoadAlbumTitle(disc) is null,
            "Explicit custom store does not read real or legacy user settings");
        Check(!Directory.EnumerateFiles(paths.Root, "*.tmp", SearchOption.AllDirectories).Any(), "No partial migration files remain");
        Console.WriteLine($"{passed} settings migration checks passed. Fixtures: {root}");
    }
}
