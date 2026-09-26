using System.Text;
using System.Diagnostics;
using System.ComponentModel;

namespace Disc2Flac;

public sealed class IsoMountService(ProcessRunner runner)
{
    public async Task<string> MountAsync(string imagePath, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath) || !Path.GetExtension(fullPath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("ISOファイルが見つかりません。", fullPath);
        var priorDrives = OpticalDrives();

        // Both the path and script are encoded so a file name cannot be interpreted as PowerShell code.
        var encodedPath = Convert.ToBase64String(Encoding.Unicode.GetBytes(fullPath));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $imagePath = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encodedPath}}'))
            $image = Get-DiskImage -ImagePath $imagePath -ErrorAction SilentlyContinue
            if ($null -eq $image -or -not $image.Attached) {
                Mount-DiskImage -ImagePath $imagePath -StorageType ISO -Access ReadOnly -ErrorAction Stop | Out-Null
            }
            $volume = Get-DiskImage -ImagePath $imagePath -ErrorAction Stop | Get-Volume -ErrorAction Stop | Select-Object -First 1
            if ($null -eq $volume -or [string]::IsNullOrWhiteSpace($volume.DriveLetter)) {
                throw 'ISOにドライブ文字が割り当てられていません。'
            }
            Write-Output ('ROOT=' + $volume.DriveLetter + ':\')
            """;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessResult result;
        try
        {
            result = await runner.RunAsync(powershell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodedScript], token);
        }
        catch (FfToolException ex)
        {
            var rootAfterPowerShell = await WaitForNewDiscAsync(priorDrives, TimeSpan.FromSeconds(2), token);
            if (rootAfterPowerShell is not null) return rootAfterPowerShell;
            try
            {
                using var shell = Process.Start(new ProcessStartInfo(fullPath)
                {
                    UseShellExecute = true,
                    Verb = "mount"
                });
                var rootAfterShell = await WaitForNewDiscAsync(priorDrives, TimeSpan.FromSeconds(15), token);
                if (rootAfterShell is not null) return rootAfterShell;
            }
            catch (Exception shellError) when (shellError is Win32Exception or InvalidOperationException)
            {
                throw new IOException("ISOを自動マウントできませんでした。エクスプローラーでISOをマウントし、表示されたドライブを選んでください。", shellError);
            }
            throw new IOException("ISOを自動マウントできませんでした。エクスプローラーでISOをマウントし、表示されたドライブを選んでください。", ex);
        }
        var root = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("ROOT=", StringComparison.Ordinal))?[5..].Trim();
        if (root is null || !DiscService.HasSupportedSource(root))
            throw new InvalidDataException("ISOを開きましたが、BDMV / AUDIO_TS / VIDEO_TS が見つかりません。");
        return root;
    }

    private static HashSet<string> OpticalDrives() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.CDRom)
        .Select(drive => drive.RootDirectory.FullName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<string?> WaitForNewDiscAsync(HashSet<string> priorDrives, TimeSpan timeout,
        CancellationToken token)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            token.ThrowIfCancellationRequested();
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.CDRom))
            {
                var root = drive.RootDirectory.FullName;
                if (!priorDrives.Contains(root) && DiscService.HasSupportedSource(root))
                    return root;
            }
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(300, token);
        } while (true);
    }
}
