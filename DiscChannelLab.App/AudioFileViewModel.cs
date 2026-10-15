namespace Disc2Flac;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, string[]> _audioCollections = new(StringComparer.OrdinalIgnoreCase);

    public async Task OpenAudioFilesAsync(IReadOnlyList<string> files)
    {
        if (IsBusy || files.Count == 0) return;
        var paths = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var first = paths[0];
        _audioCollections[first] = paths;
        if (!Sources.Contains(first)) Sources.Add(first);
        await OpenSourceAsync(first);
    }
}
