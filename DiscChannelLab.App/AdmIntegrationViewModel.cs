namespace Disc2Flac;

public enum SpatialOpenChoice { Spatial, Standard, Cancel }
public sealed record SpatialInputOffer(bool IsAdmFile, string Name, int Sources, bool Prepared);

public sealed partial class MainViewModel
{
    public Func<SpatialInputOffer, Task<SpatialOpenChoice>>? ChooseSpatialInput { get; set; }
    // Kept as a compatibility hook for older verification callers; opening never invokes a choice dialog.
    bool admEnabled;
    DiscAnalysis? admDisc;
    PlaylistInfo? admPlaylist;
    record AdmReturnSource(string Root, string[] Files, int Playlist, int Stream, int Track, double Seconds);
    AdmReturnSource? admReturn;
    public AdmWave? CurrentAdm => IsAtmosStreamingEnabled ? _navigation.ProgressiveAtmos!.Wave : SelectedTrack is null ? null : _loadedPlaylist?.Clips.FirstOrDefault(c => c.PlaylistStartTicks <= SelectedTrack.StartTicks && c.PlaylistStartTicks + c.OutTicks - c.InTicks > SelectedTrack.StartTicks)?.File?.Adm;
    public double CurrentAdmSeconds => IsAtmosStreamingEnabled ? (SelectedTrack?.StartTicks ?? 0) / 45000d + PreviewSeconds : (_loadedPlaylist?.Clips.FirstOrDefault(c => c.File?.Adm?.Path == CurrentAdm?.Path)?.InTicks ?? 0) / 45000d +
        ((SelectedTrack?.StartTicks ?? 0) - (_loadedPlaylist?.Clips.FirstOrDefault(c => c.File?.Adm?.Path == CurrentAdm?.Path)?.PlaylistStartTicks ?? 0)) / 45000d + PreviewSeconds;
    public bool IsAdmAvailable => CurrentAdm is not null || Streams.Any(AtmosImportService.IsAtmos);
    public bool IsAdmPlaybackEnabled => admEnabled && ReferenceEquals(admDisc, _disc) && ReferenceEquals(admPlaylist, _loadedPlaylist) && CurrentAdm is not null;
    public bool CanExportAdm => !IsBusy && !IsPlaying && IsAdmPlaybackEnabled;
    public bool CanChooseSpatialSource => !IsBusy && !IsPlaying && IsAdmAvailable;
    public bool CanOpenAdmScene => !IsBusy && IsAdmPlaybackEnabled;
    public bool CanOpenSpatialLab => !IsBusy && (CurrentAdm is null || IsAdmPlaybackEnabled);
    public string AdmPlaybackButtonText => LanguageService.Instance.IsJapanese ? IsAdmPlaybackEnabled ? "ADM再生を解除" : "ADM再生を有効化" : IsAdmPlaybackEnabled ? "Disable ADM playback" : "Enable ADM playback";
    public string SpatialSourceDescription => IsAtmosStreamingEnabled ? "Object preview · " + SelectedAtmosPreview.Label : CurrentAdm is { } wave
        ? $"ADM {(IsAdmPlaybackEnabled ? "enabled" : "available; not enabled")} · {wave.Scene.Sources.Count(s => s.Kind == SpatialSourceKind.Object)} objects / {wave.Channels} PCM tracks · {wave.Album?.Chapters.Length ?? 1} chapters"
        : Streams.Any(AtmosImportService.IsAtmos) ? "TrueHD Atmos available · enable ADM to prepare positional playback" : "No position-aware ADM / TrueHD Atmos detected.";
    Task OfferSpatialInputAsync()
    {
        if (CurrentAdm is not null && !IsAdmPlaybackEnabled)
            HeadphoneSession.Current.SetOptions(HeadphoneSession.Current.Options with { Enabled = false });
        Changed(nameof(CurrentAdm)); UpdateActions();
        return Task.CompletedTask;
    }
    void SyncAdmPlaybackScope()
    {
        if (admEnabled && (!ReferenceEquals(admDisc, _disc) || !ReferenceEquals(admPlaylist, _loadedPlaylist)))
        { admEnabled = false; admReturn = null; ReleaseAtmosStreaming(); }
        _navigation.SpatialAdmWave = IsAdmPlaybackEnabled ? CurrentAdm : null;
        foreach (string name in new[] { nameof(IsAdmAvailable), nameof(IsAdmPlaybackEnabled), nameof(CanOpenAdmScene),
            nameof(CanOpenSpatialLab), nameof(AdmPlaybackButtonText), nameof(IsAtmosStreamingEnabled), nameof(CanChooseAtmosPresentation) }) Changed(name);
    }
    public async Task<bool> EnableAdmPlaybackAsync()
    {
        if (IsAdmPlaybackEnabled) return true;
        if (!CanChooseSpatialSource || _disc is null || _loadedPlaylist is null) return false;
        if (CurrentAdm is null)
        {
            if (!await EnableAtmosStreamingAsync()) return false;
        }
        admEnabled = true; admDisc = _disc; admPlaylist = _loadedPlaylist;
        if (IsAtmosStreamingEnabled) HeadphoneSession.Current.SetOptions(HeadphoneSession.Current.Options with { Enabled = true, Method = HeadphoneMethod.SteamAudio });
        else SelectedPreviewMode = PreviewAudioMode.HeadphonesHrtf;
        ContinuousPlayback = true;
        UpdateActions(); Changed(nameof(CurrentAdm)); Status = "Object playback ready. Press Play; decoding continues as needed. Save ADM prepares a permanent file."; return true;
    }
    public async Task DisableAdmPlaybackAsync()
    {
        if (!IsAdmPlaybackEnabled || IsBusy || IsPlaying) return;
        var origin = admReturn; double seconds = PreviewSeconds; int number = SelectedTrack?.Number ?? 1; StopPlayback();
        var live = _navigation.ProgressiveAtmos;
        if (live is not null)
        {
            var adapter = atmosAdapter; var original = atmosOriginalStream; _navigation.ProgressiveAtmos = null;
            if (adapter is not null) Streams.Remove(adapter);
            if (original is not null && Streams.Contains(original)) SelectedStream = original;
            atmosAdapter = null; atmosOriginalStream = null; await DisposeLiveAsync(live);
        }
        admEnabled = false; admDisc = null; admPlaylist = null; admReturn = null;
        ClearAdmSourceSolo(); HeadphoneSession.Current.SetOptions(HeadphoneSession.Current.Options with { Enabled = false });
        if (origin is not null)
        {
            if (origin.Files.Length != 0) await OpenAudioFilesAsync(origin.Files); else await OpenSourceAsync(origin.Root);
            if (Playlists.FirstOrDefault(p => p.Id == origin.Playlist) is { } playlist) await ChoosePlaylistAsync(playlist);
            if (Streams.FirstOrDefault(s => s.Index == origin.Stream) is { } stream) SelectedStream = stream;
            if (Tracks.FirstOrDefault(t => t.Number == number) is { } track) SelectedTrack = track;
        }
        await SeekAsync(Math.Min(seconds, PreviewMax)); UpdateActions(); Status = "ADM disabled. Standard playback is selected.";
    }
    public async Task ChooseSpatialSourceAsync()
    {
        if (IsAdmPlaybackEnabled) await DisableAdmPlaybackAsync(); else await EnableAdmPlaybackAsync();
    }
    async Task<string?> PrepareCurrentAtmosAsync()
    {
        if (IsBusy || _disc is null || _loadedPlaylist is null) return null;
        var stream = Streams.FirstOrDefault(AtmosImportService.IsAtmos); if (stream is null) return null;
        var disc = _disc; var playlist = _loadedPlaylist; var tracks = Tracks.ToArray();
        BeginWork("Preparing full-title position-aware ADM…"); Progress = 0;
        try
        {
            var tools = new ToolPaths(); var service = new AtmosImportService(tools, new FfprobeService(tools, new ProcessRunner(_log)), _log);
            return await service.PrepareAsync(disc, playlist, stream, tracks, new Progress<ConversionProgress>(p => { Progress = p.Fraction; Status = p.Message; }), _work!.Token);
        }
        catch (Exception ex) { HandleError(ex); return null; }
        finally { EndWork(); await RefreshCacheSizeAsync(); }
    }
    public async Task<bool> ExportAdmAsync(string destination)
    {
        if (!CanExportAdm || _disc is null || _loadedPlaylist is null) return false;
        var album = AdmMetadataExport.FromTracks(_disc, _loadedPlaylist, Tracks.ToArray());
        string? source = IsAtmosStreamingEnabled ? await PrepareCurrentAtmosAsync() : CurrentAdm?.Path; if (source is null) return false;
        destination = Path.GetFullPath(destination); if (File.Exists(destination)) { Status = "Choose a new ADM filename; existing files are preserved."; return false; }
        BeginWork("Saving full-title position-aware ADM…");
        try
        {
            var progress = new Progress<ConversionProgress>(p => { Progress = p.Fraction; Status = p.Message; });
            var token = _work!.Token;
            await Task.Run(() => AdmMetadataExport.WriteAsync(source, destination, album,
                progress, token), token);
            SavedFolder = Path.GetDirectoryName(destination)!;
            Status = "Position-aware ADM saved, including all chapters."; return true;
        }
        catch (Exception ex) { HandleError(ex); return false; }
        finally { EndWork(); }
    }
}
