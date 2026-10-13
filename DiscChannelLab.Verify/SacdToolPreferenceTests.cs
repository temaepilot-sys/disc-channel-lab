using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class SacdToolPreferenceTests
{
    public static async Task RunAsync(string output)
    {
        var root = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var preference = Path.Combine(root, "settings", "sacd-tool.json");
        var first = Path.Combine(root, "日本語と spaces", "sacd_extract.exe");
        var second = Path.Combine(root, "replacement", "sacd_extract.exe");
        // These are path-selection fixtures, never executed as extractors.
        foreach (var path in new[] { first, second })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Tool path fixture");
        }
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL " + label);
            checks.Add(label); Console.WriteLine("PASS " + label);
        }
        async Task<string?> LoadAsync() => JsonSerializer.Deserialize<string>(
            await RunChildAsync(root, "sacd-tool-preference-load", preference));

        await RunChildAsync(root, "sacd-tool-preference-save", Path.GetRelativePath(root, first), preference);
        Check(await LoadAsync() == first, "A fresh process restores the absolute selected path, including Unicode and spaces");
        Check(await LoadAsync() == first, "Repeated launches reuse the same saved selection");
        await RunChildAsync(root, "sacd-tool-preference-save", second, preference);
        Check(await LoadAsync() == second, "A new selection replaces the previous path across processes");
        var unchanged = File.ReadAllText(preference);
        try { SacdService.SaveTool(Path.Combine(root, "missing.exe"), preference); throw new Exception("Missing tool accepted"); }
        catch (FileNotFoundException) { }
        Check(File.ReadAllText(preference) == unchanged && await LoadAsync() == second,
            "Invalid selections leave the saved working tool unchanged");
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Checks = checks }));
        Console.WriteLine($"{checks.Count} SACD tool preference checks passed. Fixtures: {root}");
    }

    private static async Task<string> RunChildAsync(string workingDirectory, params string[] arguments)
    {
        var host = Environment.ProcessPath!;
        var info = new ProcessStartInfo(host)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start preference test process");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var result = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"Preference test failed: {error}");
        return result.Trim();
    }
}
