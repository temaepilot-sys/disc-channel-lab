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

    private void RefreshAudioFolderStreams()
    {
        if (_loadedPlaylist is not { IsAudioFolder: true } folder || SelectedTrack is null) return;
        var available = AudioFileReader.StreamsForTrack(folder, SelectedTrack);
        // Keep the controls and mixer settings intact between files with the same format.
        static string FormatKey(AudioStreamInfo stream) =>
            $"{stream.Index}|{stream.Codec}|{stream.Profile}|{stream.Language}|{stream.TransportId}|{stream.SampleRate}|{stream.BitDepth}|{stream.UsesDecodedPcmDepth}|{stream.Channels}|{stream.ChannelLayout}";
        if (SelectedStream is not null && Streams.Select(FormatKey).SequenceEqual(available.Select(FormatKey))) return;
        var preferred = SelectedStream?.Index;
        Streams.Clear();
        foreach (var stream in available) Streams.Add(stream);
        SelectedStream = Streams.FirstOrDefault(s => s.Index == preferred && AudioFileReader.IsUsable(s))
            ?? Streams.FirstOrDefault(AudioFileReader.IsUsable) ?? Streams.FirstOrDefault();
    }

    private bool CanExportAudioTrack(TrackRow track)
    {
        if (_loadedPlaylist is null || SelectedStream is null) return false;
        if (_loadedPlaylist.IsAudioFolder && !AudioFileReader.StreamsForTrack(_loadedPlaylist, track).Any(AudioFileReader.IsUsable)) return false;
        var stream = AudioFileReader.StreamForTrack(_loadedPlaylist, track, SelectedStream);
        return stream.Covers(_loadedPlaylist, track) && QualityAvailable(stream);
    }
}
