using System.IO.Compression;
using System.Reflection;

namespace Disc2Flac;

internal static class EmbeddedTools
{
    private static readonly string[] ToolNames = ["ffmpeg.exe", "ffprobe.exe", "ffplay.exe"];
    private static string? _directory;

    public static string? GetDirectory()
    {
        if (_directory is not null) return _directory;
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.StartsWith("Disc2Flac.tools.", StringComparison.Ordinal) &&
            name.EndsWith(".zip", StringComparison.Ordinal));
        if (resource is null) return null;

        var id = resource["Disc2Flac.tools.".Length..^".zip".Length];
        if (id.Length != 16 || !id.All(Uri.IsHexDigit))
            throw new InvalidDataException("内蔵 FFmpeg の識別子が不正です。");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BDDVD2Flac", "tools", id);
        try { Directory.CreateDirectory(root); }
        catch (UnauthorizedAccessException)
        {
            root = Path.Combine(Path.GetTempPath(), "BDDVD2Flac", "tools", id);
            Directory.CreateDirectory(root);
        }
        using var mutex = new Mutex(false, $@"Local\BDDVD2Flac.Tools.{id}");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Previous extraction ended; verify files below. */ }
        try
        {
            using var source = assembly.GetManifestResourceStream(resource) ??
                throw new InvalidDataException("内蔵 FFmpeg を読み込めません。");
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            foreach (var name in ToolNames)
            {
                var entry = archive.GetEntry(name) ??
                    throw new InvalidDataException($"内蔵ツールに {name} がありません。");
                var output = Path.Combine(root, name);
                if (File.Exists(output) && new FileInfo(output).Length == entry.Length) continue;
                var temporary = Path.Combine(root, $".{name}.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var input = entry.Open())
                    using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                        input.CopyTo(destination);
                    if (new FileInfo(temporary).Length != entry.Length)
                        throw new InvalidDataException($"内蔵ツール {name} の展開に失敗しました。");
                    File.Move(temporary, output, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            _directory = root;
            return root;
        }
        finally { mutex.ReleaseMutex(); }
    }
}
