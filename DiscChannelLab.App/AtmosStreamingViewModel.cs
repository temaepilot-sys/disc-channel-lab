namespace Disc2Flac;

public sealed record AtmosPreviewChoice(AtmosPresentation Value, string Label);
public sealed partial class AudioNavigationService
{
    public ProgressiveAtmosSource? ProgressiveAtmos { get; set; }
    public AtmosPresentation AtmosPresentation { get; set; } = AtmosPresentation.Objects;
}
public sealed partial class MainViewModel
{
    AudioStreamInfo? atmosOriginalStream, atmosAdapter;
    bool changingAtmosPresentation, startingAtmos;
    public IReadOnlyList<AtmosPreviewChoice> AtmosPreviewChoices { get; } =
        [new(AtmosPresentation.Objects, "Objects · headphones"), new(AtmosPresentation.Surround51, "5.1 presentation · headphones"), new(AtmosPresentation.Stereo, "Stereo presentation · 2ch speakers")];
    public AtmosPresentation SelectedAtmosPresentation => _navigation.AtmosPresentation;
    public AtmosPreviewChoice SelectedAtmosPreview => AtmosPreviewChoices.First(p => p.Value == SelectedAtmosPresentation);
    public bool IsAtmosStreamingEnabled => _navigation.ProgressiveAtmos is { } live && _disc is not null && _loadedPlaylist is not null && live.IsFor(_disc, _loadedPlaylist);
    public bool CanChooseAtmosPresentation => !IsBusy && !changingAtmosPresentation && IsAtmosStreamingEnabled;
    public string AtmosPresentationHint => IsAtmosStreamingEnabled
        ? "Objects = moving sources; 5.1 = fixed speakers for HRIR / BRIR comparison; Stereo = unprocessed 2ch presentation. Switching keeps the track and position."
        : startingAtmos ? "Preparing Atmos source presentations; please wait."
        : CurrentAdm is not null ? "This ADM file contains its own scene, without separate 5.1 / stereo presentations. For measured speaker BRIR, open a channel-based source or the original TrueHD Atmos source."
        : IsAdmAvailable ? "Enable ADM playback above to choose the Atmos Objects / 5.1 / Stereo presentation."
        : "Presentation selection is available for TrueHD Atmos after Enable ADM playback. Other audio uses its original channel layout.";
    public bool CanChooseAudioStream => !IsBusy && !IsPlaying && !IsAtmosStreamingEnabled;
    public SpatialScene? CurrentAdmScene => IsAtmosStreamingEnabled ? _navigation.ProgressiveAtmos!.SceneFor(SelectedAtmosPresentation) : CurrentAdm?.Scene;
    public string AtmosBufferDescription => !IsAtmosStreamingEnabled ? "" :
        $"{(LanguageService.Instance.IsJapanese ? "準備済み" : "Prepared")} {BufferedTime(_navigation.ProgressiveAtmos!.PreparedFrames)} / {BufferedTime(_navigation.ProgressiveAtmos.TotalFrames)}";
    long AtmosRequestedFrame => IsAtmosStreamingEnabled
        ? Math.Clamp((long)Math.Round(CurrentAdmSeconds * 48000), 0, _navigation.ProgressiveAtmos!.TotalFrames) : 0;
    bool AtmosPositionNeedsPreparation
    {
        get
        {
            if (!IsAtmosStreamingEnabled) return false;
            var live = _navigation.ProgressiveAtmos!; long first = AtmosRequestedFrame;
            return first < live.TotalFrames && live.SafeFrames(SelectedAtmosPresentation) <
                Math.Min(live.TotalFrames, first + ProgressiveAtmosSource.StartupFrames);
        }
    }
    public bool IsAtmosPreparationPending => startingAtmos || IsAtmosStreamingEnabled &&
        (AtmosPositionNeedsPreparation || IsPlaying && _playbackControl is not { HasStarted: true });
    public string AtmosPreparationHint
    {
        get
        {
            bool ja = LanguageService.Instance.IsJapanese;
            if (startingAtmos) return ja ? "音声を準備しています。しばらくお待ちください。" : "Preparing object audio — please wait.";
            if (!IsAtmosPreparationPending) return "";
            string position = BufferedTime(AtmosRequestedFrame);
            return IsPlaying
                ? ja ? $"再生準備中です。しばらくお待ちください。\n指定位置 {position} の初回準備には時間がかかる場合があります。"
                    : $"Preparing playback — please wait.\nFirst preparation at {position} may take a while."
                : ja ? $"指定位置 {position} の音声準備が必要です。\nPlay後、再生開始まで時間がかかる場合があります。"
                    : $"Audio preparation needed at {position}.\nPlayback may take a while to start after Play.";
        }
    }
    void RefreshAtmosPreparationDisplay()
    {
        Changed(nameof(AtmosBufferDescription)); Changed(nameof(IsAtmosPreparationPending)); Changed(nameof(AtmosPreparationHint));
        Changed(nameof(AtmosPresentationHint));
    }
    static string BufferedTime(long frames) => TimeSpan.FromSeconds(frames / 48000d).ToString(@"hh\:mm\:ss");
    long lastAtmosProgress;
    void RefreshAtmosProgress()
    {
        if ((!IsAtmosStreamingEnabled && !startingAtmos) || System.Diagnostics.Stopwatch.GetElapsedTime(lastAtmosProgress).TotalMilliseconds < 500) return;
        lastAtmosProgress = System.Diagnostics.Stopwatch.GetTimestamp(); RefreshAtmosPreparationDisplay();
    }
    async Task<bool> EnableAtmosStreamingAsync()
    {
        var stream = Streams.FirstOrDefault(AtmosImportService.IsAtmos);
        if (_disc is null || _loadedPlaylist is null || stream is null) return false;
        double heldSeconds = PreviewSeconds;
        startingAtmos = true; RefreshAtmosPreparationDisplay();
        BeginWork("Starting object-audio preview · preparing a short read-ahead…");
        try
        {
            var tools = new ToolPaths(); var metadata = new AdmAlbumMetadata(_loadedPlaylist.TitleName, _disc.Artist, _disc.Root,
                Tracks.Select(t => new AdmChapter(ConversionService.ToSample(t.StartTicks, 48000), t.Title, t.Artist)).ToArray());
            var source = await ProgressiveAtmosSource.StartAsync(_disc, _loadedPlaylist, stream, tools,
                new FfprobeService(tools, new ProcessRunner(_log)), _log, metadata, _work!.Token);
            _navigation.ProgressiveAtmos = source; _navigation.AtmosPresentation = AtmosPresentation.Objects;
            atmosOriginalStream = SelectedStream;
            atmosAdapter = new() { Index = -1003, Codec = "adm", Profile = "Object audio · streaming → stereo headphones",
                Channels = 2, ChannelLayout = "stereo", SampleRate = 48000, BitDepth = 24, StartTimeTicks = 0, SampleCount = source.TotalFrames };
            admEnabled = true; admDisc = _disc; admPlaylist = _loadedPlaylist;
            Streams.Add(atmosAdapter); SelectedStream = atmosAdapter;
            // Selecting the adapter uses the normal stream-change reset. Restore
            // the user's position after that reset, just as disabling ADM does.
            PreviewSeconds = heldSeconds;
            return true;
        }
        catch (Exception ex) { HandleError(ex); return false; }
        finally { startingAtmos = false; EndWork(); RefreshAtmosPreparationDisplay(); }
    }
    public async Task ChangeAtmosPresentationAsync(AtmosPreviewChoice choice)
    {
        if (!CanChooseAtmosPresentation || choice.Value == SelectedAtmosPresentation || !Enum.IsDefined(choice.Value)) return;
        changingAtmosPresentation = true; Changed(nameof(CanChooseAtmosPresentation));
        int transportVersion = _transportVersion; var source = _navigation.ProgressiveAtmos;
        try
        {
        bool resume = IsPlaying; AdvancePlaybackClock(); PausePlayback();
        _playback?.Cancel();
        if (_playbackTask is { IsCompleted: false } previous) await previous;
        if (!IsAtmosStreamingEnabled || IsBusy || !ReferenceEquals(source, _navigation.ProgressiveAtmos)) return;
        if (transportVersion != _transportVersion) resume = false;
        _playbackEngineOptions = null;
        _navigation.AtmosPresentation = choice.Value; ClearAdmSourceSolo();
        if (choice.Value == AtmosPresentation.Objects && HeadphoneSession.Current.Options.Method == HeadphoneMethod.MeasuredBrir)
            HeadphoneSession.Current.SetOptions(HeadphoneSession.Current.Options with { Method = HeadphoneMethod.SteamAudio, Room = new() });
        if (atmosAdapter is not null)
        {
            Changed(nameof(SelectedStreamNote));
        }
        Changed(nameof(SelectedAtmosPreview)); Changed(nameof(SelectedAtmosPresentation)); Changed(nameof(CurrentAdm)); Changed(nameof(CurrentAdmScene));
        Changed(nameof(SelectedPreviewMode)); UpdateActions();
        Status = choice.Label + " · same track and position";
        if (resume) await StartPlaybackAsync(++_transportVersion);
        }
        finally { changingAtmosPresentation = false; Changed(nameof(CanChooseAtmosPresentation)); Changed(nameof(SelectedAtmosPreview)); }
    }
    public void ReleaseAtmosStreaming()
    {
        var source = _navigation.ProgressiveAtmos; _navigation.ProgressiveAtmos = null;
        if (source is not null) _ = DisposeLiveAsync(source);
        atmosAdapter = null; atmosOriginalStream = null;
        RefreshAtmosPreparationDisplay();
    }
    async Task DisposeLiveAsync(ProgressiveAtmosSource source)
    { try { await source.DisposeAsync(); } catch (Exception ex) { _log.Write("LIVE CACHE CLEANUP: " + ex); } }
}
