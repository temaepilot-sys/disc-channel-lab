using System.Diagnostics;

namespace Disc2Flac;

public sealed record CacheUsage(long AudioBytes, long ViewerBytes, long ToolBytes, int ScanErrors)
{
    public long ClearableBytes => AudioBytes + ViewerBytes;
}
public sealed record CacheClearResult(CacheUsage Usage, long DeletedBytes, IReadOnlyList<string> Errors);

/// <summary>Only generated audio and viewer cache trees are disposable. Settings and tools are separate.</summary>
public sealed class CacheStorage(AppDataPaths paths, string viewerHome, Func<bool>? inUse = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _audio = Path.GetFullPath(Path.Combine(paths.Root, "sacd-cache"));
    private readonly string _viewer = Path.GetFullPath(Path.Combine(viewerHome, "cache"));
    private readonly string _tools = Path.GetFullPath(Path.Combine(paths.Root, "tools"));
    public string AudioPath => _audio;
    public string ViewerPath => _viewer;
    public bool IsInUse => (inUse ?? RuntimeInUse)();

    public static bool RuntimeInUse()
    {
        foreach (var name in new[] { "DiscChannelLab", "DiscChannelLab.Visualizer" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                    if (name == "DiscChannelLab.Visualizer" || process.Id != Environment.ProcessId) return true;
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    public async Task<CacheUsage> MeasureAsync()
    {
        await _gate.WaitAsync();
        try { return await Task.Run(Measure); }
        finally { _gate.Release(); }
    }
    public async Task<CacheClearResult> ClearAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (IsInUse) throw new InvalidOperationException("他のプレーヤーとビューアーを終了してからキャッシュをクリアしてください。");
                var errors = new List<string>(); long deleted = 0;
                foreach (var root in new[] { _audio, _viewer })
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var files = new List<string>(); var directories = new List<string>();
                        Walk(root, files, directories);
                        foreach (var path in files)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                if (IsInUse) throw new InvalidOperationException("キャッシュが使用中です。");
                                RequireOwnedPath(root, path);
                                var size = new FileInfo(path).Length;
                                File.Delete(path); deleted += size;
                            }
                            catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); }
                        }
                        foreach (var directory in directories.AsEnumerable().Reverse())
                        {
                            token.ThrowIfCancellationRequested();
                            try { RequireOwnedPath(root, directory); Directory.Delete(directory, recursive: false); }
                            catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); }
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex)) { errors.Add(ex.Message); }
                }
                return new CacheClearResult(Measure(), deleted, errors);
            });
        }
        finally { _gate.Release(); }
    }
    private CacheUsage Measure()
    {
        var errors = 0;
        long Size(string root)
        {
            long bytes = 0;
            try
            {
                var files = new List<string>(); var directories = new List<string>();
                Walk(root, files, directories);
                foreach (var file in files)
                {
                    try { RequireOwnedPath(root, file); bytes += new FileInfo(file).Length; }
                    catch (Exception ex) when (IsFileError(ex)) { errors++; }
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { errors++; }
            return bytes;
        }
        var audio = Size(_audio); var viewer = Size(_viewer); var tools = Size(_tools);
        return new(audio, viewer, tools, errors);
    }
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException;
    private static void Walk(string root, List<string> files, List<string> directories)
    {
        RequireOwnedPath(root, root);
        if (!Directory.Exists(root)) return;
        void Visit(string directory)
        {
            RequireOwnedPath(root, directory);
            directories.Add(directory);
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                RequireOwnedPath(root, item.FullName);
                if (item is DirectoryInfo) Visit(item.FullName); else files.Add(item.FullName);
            }
        }
        Visit(root);
    }
    private static void RequireOwnedPath(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("キャッシュ範囲外のファイルは操作できません。");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクを含むキャッシュは操作できません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.00} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MiB";
        if (bytes >= 1L << 10) return $"{bytes / 1024d:0.0} KiB";
        return $"{bytes} B";
    }
}
