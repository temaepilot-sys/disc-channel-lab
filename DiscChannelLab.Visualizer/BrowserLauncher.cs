using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiscChannelLab.Visualizer;

public enum BrowserEngine { Chromium, Firefox }
public sealed record ViewerBrowser(string Name, string Executable, BrowserEngine Engine)
{
    public override string ToString() => Name;
}

public static class BrowserCatalog
{
    public static ViewerBrowser? FromPath(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!File.Exists(path)) return null;
            var file = Path.GetFileName(path).ToLowerInvariant();
            var info = file switch
            {
                "firefox.exe" => ("Firefox", BrowserEngine.Firefox),
                "librewolf.exe" => ("LibreWolf", BrowserEngine.Firefox),
                "waterfox.exe" => ("Waterfox", BrowserEngine.Firefox),
                "floorp.exe" => ("Floorp", BrowserEngine.Firefox),
                "zen.exe" => ("Zen", BrowserEngine.Firefox),
                "msedge.exe" => ("Microsoft Edge", BrowserEngine.Chromium),
                "chrome.exe" => ("Google Chrome", BrowserEngine.Chromium),
                "chromium.exe" => ("Chromium", BrowserEngine.Chromium),
                "brave.exe" => ("Brave", BrowserEngine.Chromium),
                "vivaldi.exe" => ("Vivaldi", BrowserEngine.Chromium),
                "opera.exe" => ("Opera", BrowserEngine.Chromium),
                "launcher.exe" when Path.GetDirectoryName(path)!.Contains("Opera", StringComparison.OrdinalIgnoreCase) => ("Opera", BrowserEngine.Chromium),
                _ => ((string?)null, BrowserEngine.Chromium)
            };
            return info.Item1 is null ? null : new(name ?? info.Item1, path, info.Item2);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? CommandExecutable(string command)
    {
        var match = Regex.Match(Environment.ExpandEnvironmentVariables(command).Trim(), "^(?:\"([^\"]+\\.exe)\"|(.+?\\.exe)(?:\\s|$))", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    public static List<ViewerBrowser> Detect(string? savedPath = null)
    {
        var found = new Dictionary<string, ViewerBrowser>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string? name = null)
        {
            if (path is not null && FromPath(path, name) is { } browser) found.TryAdd(browser.Executable, browser);
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                foreach (var client in clients?.GetSubKeyNames() ?? [])
                {
                    using var registration = clients!.OpenSubKey(client);
                    using var command = registration?.OpenSubKey(@"shell\open\command");
                    Add(CommandExecutable(command?.GetValue("") as string ?? ""), registration?.GetValue("") as string);
                }
                foreach (var file in new[] { "firefox.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe", "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe" })
                {
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + file);
                    Add((key?.GetValue("") as string)?.Trim('"'));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        foreach (var relative in new[] { "Mozilla Firefox/firefox.exe", "LibreWolf/librewolf.exe", "Waterfox/waterfox.exe", "Floorp/floorp.exe",
            "Zen Browser/zen.exe", "Google/Chrome/Application/chrome.exe", "Microsoft/Edge/Application/msedge.exe",
            "BraveSoftware/Brave-Browser/Application/brave.exe", "Vivaldi/Application/vivaldi.exe", "Programs/Opera/launcher.exe", "Chromium/Application/chrome.exe" })
            Add(Path.Combine(basePath, relative));
        Add(savedPath);
        return found.Values.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(b => b.Executable).ToList();
    }
}

public static class BrowserPreference
{
    public static string? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            return JsonSerializer.Deserialize<Choice>(File.ReadAllText(path)) is { Version: 1 } choice ? choice.Executable : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Save(string path, ViewerBrowser browser)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Choice(1, browser.Executable)));
        File.Move(temporary, path, overwrite: true);
    }
    private sealed record Choice(int Version, string Executable);
}

public static class BrowserLauncher
{
    public static string ProfilePath(string cache, ViewerBrowser browser)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(browser.Executable.ToUpperInvariant())))[..16];
        return Path.Combine(Path.GetFullPath(cache), "browser-profiles", id);
    }
    public static ProcessStartInfo CreateStartInfo(ViewerBrowser browser, string page, string cache)
    {
        if (!Uri.TryCreate(page, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1")
            throw new ArgumentException("The viewer browser may only open its local server.");
        if (!File.Exists(browser.Executable)) throw new FileNotFoundException("The selected browser is no longer available.");
        var profile = ProfilePath(cache, browser);
        var start = new ProcessStartInfo(browser.Executable) { UseShellExecute = false };
        if (browser.Engine == BrowserEngine.Firefox)
        {
            foreach (var argument in new[] { "-no-remote", "-profile", profile, "-new-window", page }) start.ArgumentList.Add(argument);
        }
        else
        {
            foreach (var argument in new[] { "--app=" + page, "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                "--disable-sync", "--disable-background-networking", "--disable-features=SigninIntercept,msEdgeOnRampFRE,msEdgeFirstRunExperience", "--window-size=1360,900" })
                start.ArgumentList.Add(argument);
        }
        return start;
    }
    public static void PrepareProfile(ViewerBrowser browser, string cache)
    {
        var profile = ProfilePath(cache, browser); Directory.CreateDirectory(profile);
        if (browser.Engine == BrowserEngine.Firefox)
        {
            // This file belongs only to the viewer profile, never the user's normal Firefox profile.
            File.WriteAllText(Path.Combine(profile, "user.js"), """
                user_pref("browser.aboutwelcome.enabled", false);
                user_pref("browser.shell.checkDefaultBrowser", false);
                user_pref("browser.startup.homepage_override.mstone", "ignore");
                user_pref("browser.startup.page", 0);
                user_pref("browser.startup.homepage", "about:blank");
                user_pref("browser.newtabpage.enabled", false);
                user_pref("identity.fxaccounts.enabled", false);
                user_pref("datareporting.policy.dataSubmissionEnabled", false);
                user_pref("toolkit.telemetry.enabled", false);
                user_pref("app.shield.optoutstudies.enabled", false);
                """);
        }
        else
        {
            File.WriteAllText(Path.Combine(profile, "First Run"), "");
        }
    }
}
