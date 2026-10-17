using System.IO;
using System.Text.Json;
using DiscChannelLab.Visualizer;
using Forms = System.Windows.Forms;

internal static class BrowserTests
{
    public static void Run(string folder)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new IOException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        var discovered = BrowserCatalog.Detect();
        File.WriteAllText(Path.Combine(root, "launch-fixtures.json"), JsonSerializer.Serialize(discovered.Select(browser =>
        {
            var smokeCache = Path.Combine(root, "smoke-cache"); BrowserLauncher.PrepareProfile(browser, smokeCache);
            var start = BrowserLauncher.CreateStartInfo(browser, "http://127.0.0.1:12345/#test", smokeCache);
            return new { browser.Name, engine = browser.Engine.ToString(), start.FileName, arguments = start.ArgumentList.ToArray(),
                profile = BrowserLauncher.ProfilePath(smokeCache, browser) };
        }), new JsonSerializerOptions { WriteIndented = true }));
        Check(discovered.Count > 0 && discovered.Select(b => b.Executable).Distinct(StringComparer.OrdinalIgnoreCase).Count() == discovered.Count,
            "Installed browsers are detected without duplicate registry/path entries");
        Check(BrowserCatalog.CommandExecutable("\"C:\\Program Files\\Firefox\\firefox.exe\" -osint -url \"%1\"") == @"C:\Program Files\Firefox\firefox.exe",
            "Registry command parsing preserves quoted paths without inheriting its arguments");
        Check(BrowserCatalog.CommandExecutable(@"C:\Program Files\Chrome\chrome.exe --single-argument %1") == @"C:\Program Files\Chrome\chrome.exe" &&
            BrowserCatalog.CommandExecutable("cmd /c start firefox") is null, "Unquoted paths parse safely without invoking a shell");
        var fake = Path.Combine(root, "portable browser"); Directory.CreateDirectory(fake);
        var firefoxPath = Path.Combine(fake, "firefox.exe"); var chromePath = Path.Combine(fake, "chrome.exe");
        File.WriteAllBytes(firefoxPath, []); File.WriteAllBytes(chromePath, []);
        var firefox = BrowserCatalog.FromPath(firefoxPath)!; var chrome = BrowserCatalog.FromPath(chromePath)!;
        Check(firefox.Engine == BrowserEngine.Firefox && chrome.Engine == BrowserEngine.Chromium && BrowserCatalog.FromPath(Path.Combine(fake, "missing.exe")) is null,
            "Portable browsers retain the correct engine and missing executables are rejected");
        var preference = Path.Combine(root, "BrowserSettings.json");
        Check(BrowserPreference.Load(preference) is null, "No browser is selected on first launch");
        BrowserPreference.Save(preference, firefox);
        Check(BrowserPreference.Load(preference) == firefox.Executable && BrowserCatalog.Detect(firefox.Executable).Any(b => b.Executable == firefox.Executable),
            "A portable browser choice survives detection on the next launch");
        File.WriteAllText(preference, "bad json"); Check(BrowserPreference.Load(preference) is null, "Damaged preferences do not force a default browser");
        var cache = Path.Combine(root, "cache");
        var fi = BrowserLauncher.CreateStartInfo(firefox, "http://127.0.0.1:12345/#test", cache);
        var ci = BrowserLauncher.CreateStartInfo(chrome, "http://127.0.0.1:12345/#test", cache);
        Check(!fi.UseShellExecute && fi.FileName == firefox.Executable && fi.ArgumentList.Contains("-no-remote") && fi.ArgumentList.Contains(BrowserLauncher.ProfilePath(cache, firefox)),
            "Firefox launches explicitly with a separate non-remote viewer profile");
        Check(!ci.UseShellExecute && ci.ArgumentList.Contains("--disable-sync") && ci.ArgumentList.Contains("--no-first-run") && ci.ArgumentList.Any(a => a.StartsWith("--user-data-dir=")),
            "Chromium launches with a separate profile and synchronization disabled");
        Check(BrowserLauncher.ProfilePath(cache, firefox) != BrowserLauncher.ProfilePath(cache, chrome) && BrowserLauncher.ProfilePath(cache, firefox).StartsWith(cache + Path.DirectorySeparatorChar),
            "Browser profiles are distinct and stay inside the viewer cache");
        BrowserLauncher.PrepareProfile(firefox, cache); BrowserLauncher.PrepareProfile(chrome, cache);
        Check(File.ReadAllText(Path.Combine(BrowserLauncher.ProfilePath(cache, firefox), "user.js")).Contains("\"identity.fxaccounts.enabled\", false"),
            "Only the viewer Firefox profile receives account and first-run suppression preferences");
        bool rejected = false;
        try { BrowserLauncher.CreateStartInfo(chrome, "https://example.com", cache); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "The launch helper refuses non-local viewer URLs");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.EnableVisualStyles();
                var uiPreference = Path.Combine(root, "ui-browser.json");
                using (var dialog = new BrowserSelectionDialog(uiPreference, false) { Opacity = 0, ShowInTaskbar = false })
                {
                    dialog.Show(); Forms.Application.DoEvents();
                    var controls = Descendants(dialog).ToArray();
                    var list = controls.OfType<Forms.ListBox>().Single();
                    var open = controls.OfType<Forms.Button>().Single(b => b.Text == "Open 3D viewer");
                    Check(list.SelectedIndex == -1 && !open.Enabled, "The first dialog does not preselect any browser");
                    var selected = list.Items.Cast<ViewerBrowser>().First(b => b.Engine == BrowserEngine.Firefox);
                    list.SelectedItem = selected; open.PerformClick();
                    Check(dialog.DialogResult == Forms.DialogResult.OK && BrowserPreference.Load(uiPreference) == selected.Executable,
                        "Explicitly choosing Firefox and pressing Open remembers that browser");
                }
                using (var dialog = new BrowserSelectionDialog(uiPreference, true) { Opacity = 0, ShowInTaskbar = false })
                {
                    dialog.Show(); Forms.Application.DoEvents();
                    Check(dialog.SelectedBrowser?.Engine == BrowserEngine.Firefox && dialog.Text.Contains("ブラウザー"), "The Japanese dialog restores Firefox while keeping the chooser visible");
                    dialog.Close();
                }
                BrowserPreference.Save(uiPreference, firefox with { Executable = Path.Combine(root, "gone", "firefox.exe") });
                using (var dialog = new BrowserSelectionDialog(uiPreference, false) { Opacity = 0, ShowInTaskbar = false })
                {
                    dialog.Show(); Forms.Application.DoEvents();
                    Check(dialog.SelectedBrowser is null, "A missing saved browser never silently falls back to Edge or Chrome"); dialog.Close();
                }
                using var screenshot = new BrowserSelectionDialog(Path.Combine(root, "screenshot-preference.json"), false) { ShowInTaskbar = false, Opacity = 0 };
                screenshot.Show(); Forms.Application.DoEvents();
                using var image = new System.Drawing.Bitmap(screenshot.ClientSize.Width, screenshot.ClientSize.Height);
                screenshot.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                image.Save(Path.Combine(root, "browser-picker.png")); screenshot.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Browser dialog tests timed out.");
        if (failure is not null) throw failure;
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks,
            installed = discovered.Select(b => new { b.Name, b.Engine }) }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static IEnumerable<Forms.Control> Descendants(Forms.Control root)
    { foreach (Forms.Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}
