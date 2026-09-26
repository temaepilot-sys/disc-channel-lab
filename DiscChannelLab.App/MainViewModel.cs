using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Disc2Flac;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppLog _log = new();
    private readonly DiscService _discService;
    private readonly ConversionService _conversion;
    private readonly AudioNavigationService _navigation;
    private readonly TrackEditsStore _editStore;
    private readonly IsoMountService _isoMount;
    private DiscAnalysis? _disc;
    private PlaylistInfo? _loadedPlaylist;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _playback;
    private Task? _playbackTask;
    private readonly Stopwatch _playClock = new();
    private readonly Queue<(double Position, double[] Peaks, double[] Rms)> _meterFrames = new();
    private double _playClockBase;
    private int _transportVersion;
    private bool _isScrubbing;
    private string _source = "";
    private string _selectedIsoPath = "";
    private string? _isoRoot;
    private string _albumTitle = "ディスクを入れてください";
    private string _titleName = "";
    private string _metadataDetails = "";
    private string _outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    private string _status = "光学ドライブを確認しています";
    private string _savedFolder = "";
    private bool _isBusy;
    private double _progress;
    private OutputQuality _quality = OutputQuality.Cd;
    private PlaylistInfo? _selectedPlaylist;
    private AudioStreamInfo? _selectedStream;
    private string? _lastReadyDrive;
    private string? _lastDiscSignature;
    private TrackRow? _selectedTrack;
    private SplitCandidate? _selectedCandidate;
    private double _previewSeconds;
    private bool _isPlaying;
    private bool _continuousPlayback = true;
    private double _volume = 100;
    private bool _perceivedVolume = true;
    private bool _groupByChapter;
    private bool _saveStereoDownmix;
    private bool _saveIndividualChannels;
    private ChannelChoice? _selectedPreviewChannel;
    private double _frontMixPercent = 100;
    private double _centerMixPercent = 100 / Math.Sqrt(2);
    private double _surroundMixPercent = 100 / Math.Sqrt(2);
    private double _lfeMixPercent;
    private bool _centerMixMuted;
    private bool _frontMixMuted;
    private bool _surroundMixMuted;
    private bool _lfeMixMuted;
    private StereoMixSettings? _activePlaybackMix;
    private string? _activePreviewChannel;
    private PreviewMixState _livePreviewMix = new(StereoMixSettings.Default, null);
    private bool _suspendEditSave;

    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];
    public ObservableCollection<AudioStreamInfo> Streams { get; } = [];
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public ObservableCollection<SplitCandidate> SplitCandidates { get; } = [];
    public ObservableCollection<ChannelChoice> PreviewChannels { get; } = [];
    public ObservableCollection<ChannelMeter> ChannelMeters { get; } = [];
    public IReadOnlyList<string?> ChapterTitles => _loadedPlaylist?.ChapterTitles ?? [];
    public string Source { get => _source; set => Set(ref _source, value); }
    public string SelectedIsoPath { get => _selectedIsoPath; private set => Set(ref _selectedIsoPath, value); }
    public string AlbumTitle { get => _disc is null ? LanguageService.T(_albumTitle) : _albumTitle; private set => Set(ref _albumTitle, value); }
    public string TitleName
    {
        get => _titleName;
        set
        {
            if (_disc is null || _loadedPlaylist is null) return;
            var title = value.Trim();
            if (title.Length == 0)
            {
                Status = "タイトル名を入力してください。";
                Changed();
                return;
            }
            if (title == _titleName) return;
            try { _editStore.SaveTitleName(_disc, _loadedPlaylist, title); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _log.Write($"TITLE NAME SAVE FAILED: {ex}");
                Status = $"タイトル名を保存できませんでした: {ex.Message}";
                Changed();
                return;
            }
            _loadedPlaylist.TitleName = title;
            Set(ref _titleName, title);
        }
    }
    public string MetadataDetails { get => _metadataDetails; private set => Set(ref _metadataDetails, value); }
    public string OutputFolder { get => _outputFolder; set { if (Set(ref _outputFolder, value)) Changed(nameof(CanConvert)); } }
    public bool GroupByChapter { get => _groupByChapter; set => Set(ref _groupByChapter, value); }
    public bool SaveStereoDownmix
    {
        get => _saveStereoDownmix;
        set
        {
            var enabled = value && CanDownmixStereo;
            if (!Set(ref _saveStereoDownmix, enabled)) return;
            if (enabled && _saveIndividualChannels)
            {
                _saveIndividualChannels = false;
                Changed(nameof(SaveIndividualChannels));
            }
            Changed(nameof(SelectedStreamNote));
        }
    }
    public bool SaveIndividualChannels
    {
        get => _saveIndividualChannels;
        set
        {
            var enabled = value && CanDownmixStereo;
            if (!Set(ref _saveIndividualChannels, enabled)) return;
            if (enabled && _saveStereoDownmix)
            {
                _saveStereoDownmix = false;
                Changed(nameof(SaveStereoDownmix));
            }
            Changed(nameof(SelectedStreamNote));
        }
    }
    public ChannelChoice? SelectedPreviewChannel
    {
        get => _selectedPreviewChannel;
        set => Set(ref _selectedPreviewChannel, value);
    }
    public bool CanChoosePreviewChannel => !IsBusy && PreviewChannels.Count > 1;
    public bool CanDownmixStereo => SelectedStream is { } stream && StereoMixSettings.Supports(stream) && !IsBusy;
    public double FrontMixPercent { get => _frontMixPercent; set => Set(ref _frontMixPercent, ClampPercent(value)); }
    public double CenterMixPercent { get => _centerMixPercent; set { if (Set(ref _centerMixPercent, ClampPercent(value))) Changed(nameof(CenterMixText)); } }
    public double SurroundMixPercent { get => _surroundMixPercent; set { if (Set(ref _surroundMixPercent, ClampPercent(value))) Changed(nameof(SurroundMixText)); } }
    public double LfeMixPercent { get => _lfeMixPercent; set { if (Set(ref _lfeMixPercent, ClampPercent(value))) Changed(nameof(LfeMixText)); } }
    public bool FrontMixMuted { get => _frontMixMuted; set => Set(ref _frontMixMuted, value); }
    public bool CenterMixMuted { get => _centerMixMuted; set => Set(ref _centerMixMuted, value); }
    public bool SurroundMixMuted { get => _surroundMixMuted; set => Set(ref _surroundMixMuted, value); }
    public bool LfeMixMuted { get => _lfeMixMuted; set => Set(ref _lfeMixMuted, value); }
    public string CenterMixText => $"{CenterMixPercent:0}%";
    public string SurroundMixText => $"{SurroundMixPercent:0}%";
    public string LfeMixText => $"{LfeMixPercent:0}%";
    private StereoMixSettings CurrentMix => new(CenterMixMuted ? 0 : CenterMixPercent / 100,
        SurroundMixMuted ? 0 : SurroundMixPercent / 100, LfeMixMuted ? 0 : LfeMixPercent / 100,
        FrontMixMuted ? 0 : FrontMixPercent / 100);
    private static double ClampPercent(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
    public string Status { get => LanguageService.T(_status); private set => Set(ref _status, value); }
    public string SavedFolder { get => _savedFolder; private set { if (Set(ref _savedFolder, value)) Changed(nameof(CanOpenSavedFolder)); } }
    public bool CanOpenSavedFolder => !string.IsNullOrWhiteSpace(SavedFolder);
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) UpdateActions(); } }
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) { Changed(nameof(PlayPauseText)); UpdateActions(); } } }
    public string PlayPauseText => LanguageService.T(IsPlaying ? "一時停止" : "再生");
    public bool ContinuousPlayback { get => _continuousPlayback; set => Set(ref _continuousPlayback, value); }
    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            if (Set(ref _volume, Math.Clamp(double.IsFinite(value) ? value : 100, 0, 200)))
            {
                Changed(nameof(VolumeText));
                Changed(nameof(VolumeDetails));
            }
        }
    }
    public string VolumeText => $"{Volume:0}%";
    public bool PerceivedVolume
    {
        get => Volatile.Read(ref _perceivedVolume);
        set
        {
            if (Set(ref _perceivedVolume, value)) Changed(nameof(VolumeDetails));
        }
    }
    public string VolumeDetails => VolumeCurve.Description(Volume, PerceivedVolume);
    public bool IsScrubbing { get => _isScrubbing; set => Set(ref _isScrubbing, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public PlaylistInfo? SelectedPlaylist { get => _selectedPlaylist; set => Set(ref _selectedPlaylist, value); }
    public TrackRow? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (!Set(ref _selectedTrack, value)) return;
            StopPlayback();
            SplitCandidates.Clear();
            SelectedCandidate = null;
            PreviewSeconds = 0;
            Changed(nameof(PreviewMax));
            Changed(nameof(PreviewLengthText));
            Changed(nameof(PreviewTimeText));
            Changed(nameof(SelectedTrackTitle));
            Changed(nameof(SelectedChapterText));
            UpdateActions();
        }
    }
    public SplitCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (!Set(ref _selectedCandidate, value) || value is null || SelectedTrack is null) return;
            PreviewSeconds = (value.PositionTicks - SelectedTrack.StartTicks) / 45000d;
        }
    }
    public double PreviewMax => Math.Max(0, SelectedTrack?.DurationSeconds ?? 0);
    public string SelectedTrackTitle => SelectedTrack is null ? LanguageService.T("曲を選択してください") : $"{SelectedTrack.Number:00}  {SelectedTrack.Title}";
    public string SelectedChapterText
    {
        get
        {
            if (_loadedPlaylist is null || SelectedTrack is null) return LanguageService.T("チャプターを選択してください");
            var start = 1;
            var end = 1;
            for (var i = 0; i < _loadedPlaylist.ChapterStarts.Count; i++)
            {
                if (_loadedPlaylist.ChapterStarts[i] <= SelectedTrack.StartTicks) start = i + 1;
                if (_loadedPlaylist.ChapterStarts[i] < SelectedTrack.EndTicks) end = i + 1;
            }
            return LanguageService.T(start == end
                ? $"チャプター {start:00}: {_loadedPlaylist.ChapterTitle(start) ?? LanguageService.T("名称未設定")}"
                : $"チャプター {start:00}–{end:00} にまたがる曲");
        }
    }
    public string PreviewLengthText => TimeSpan.FromSeconds(PreviewMax).ToString(@"hh\:mm\:ss\.fff");
    public double PreviewSeconds
    {
        get => _previewSeconds;
        set
        {
            var clamped = Math.Clamp(double.IsFinite(value) ? value : 0, 0, PreviewMax);
            if (!Set(ref _previewSeconds, clamped)) return;
            Changed(nameof(PreviewTimeText));
            Changed(nameof(CanSplit));
            Changed(nameof(CanPlay));
        }
    }
    public string PreviewTimeText
    {
        get => TimeSpan.FromSeconds(PreviewSeconds).ToString(@"hh\:mm\:ss\.fff");
        set
        {
            if (TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out var time)) PreviewSeconds = time.TotalSeconds;
            Changed();
        }
    }
    public AudioStreamInfo? SelectedStream
    {
        get => _selectedStream;
        set
        {
            if (!Set(ref _selectedStream, value)) return;
            StopPlayback();
            SaveStereoDownmix = false;
            SaveIndividualChannels = false;
            PreviewChannels.Clear();
            PreviewChannels.Add(new ChannelChoice(null, "ステレオミックス"));
            if (value is not null)
                foreach (var channel in StereoMixSettings.ChannelNames(value))
                    PreviewChannels.Add(new ChannelChoice(channel, ChannelChoice.DescribeRaw(channel)));
            SelectedPreviewChannel = PreviewChannels[0];
            ChannelMeters.Clear();
            if (value is not null)
            {
                var channelNames = StereoMixSettings.ChannelNames(value);
                for (var i = 0; i < value.Channels; i++)
                {
                    var code = i < channelNames.Count ? channelNames[i] : value.Channels == 1
                        ? "Mono" : value.Channels == 2 ? (i == 0 ? "L" : "R") : $"Ch{i + 1}";
                    ChannelMeters.Add(new ChannelMeter(code, ChannelChoice.DescribeRaw(code)));
                }
            }
            _meterFrames.Clear();
            SplitCandidates.Clear();
            IsCdSelected = true;
            Changed(nameof(CanChooseHighRes));
            Changed(nameof(SelectedStreamNote));
            UpdateActions();
        }
    }
    public bool CanChooseHighRes => SelectedStream?.CanMakeHighResolution == true && !IsBusy;
    public string SelectedStreamNote => LanguageService.T(SelectedStream is { HasSupportedChannels: false }
        ? "この音声のチャンネル数または配置には対応していません。"
        : SelectedStream is { Channels: > 2 } stream && StereoMixSettings.Supports(stream)
        ? SaveIndividualChannels
            ? $"{stream.Channels}ch をチャンネルごとのモノラル FLAC に保存します。"
            : SaveStereoDownmix
            ? $"{stream.Channels}ch を設定した比率で2chに変換して FLAC に保存します。"
            : $"{stream.Channels}ch の配置を維持して FLAC に保存します。試聴時は設定した比率でステレオに変換します。"
        : SelectedStream is { Channels: > 2 } other
        ? $"{other.Channels}ch の配置を維持して FLAC に保存します。試聴時は FFmpeg の標準設定でステレオに変換します。"
        : SelectedStream is { Codec: "ac3" or "eac3" or "mp2" } or { Codec: "dts", Profile: not "DTS-HD MA" }
        ? "元音声は非可逆圧縮です。FLAC保存後の音質は元音声を超えません。"
        : SelectedStream is { Channels: 1 }
            ? "モノラルのまま FLAC に保存します。試聴時は左右に同じ音を出します。"
            : "");
    public bool IsCdSelected { get => _quality == OutputQuality.Cd; set { if (value) { _quality = OutputQuality.Cd; Changed(); Changed(nameof(IsHighResSelected)); Changed(nameof(CanConvert)); } } }
    public bool IsHighResSelected { get => _quality == OutputQuality.HighResolution; set { if (value && CanChooseHighRes) { _quality = OutputQuality.HighResolution; Changed(); Changed(nameof(IsCdSelected)); Changed(nameof(CanConvert)); } } }
    public bool CanConvert => !IsBusy && !IsPlaying && _disc is not null && _loadedPlaylist is not null && SelectedStream is not null && !string.IsNullOrWhiteSpace(OutputFolder) && Tracks.Any(x => x.IsSelected) && (_quality == OutputQuality.Cd ? SelectedStream.CanMakeCd : SelectedStream.CanMakeHighResolution);
    public bool CanEditTracks => !IsBusy && !IsPlaying && Tracks.Count > 0;
    public bool CanPlay => !IsBusy && SelectedTrack is not null && SelectedStream is not null && _disc is not null && _loadedPlaylist is not null && _navigation.CanPlay;
    public bool CanSeek => !IsBusy && SelectedTrack is not null;
    public bool CanPreviousTrack => CanSeek && Tracks.IndexOf(SelectedTrack!) > 0;
    public bool CanNextTrack
    {
        get
        {
            if (!CanSeek) return false;
            var index = Tracks.IndexOf(SelectedTrack!);
            return index >= 0 && index < Tracks.Count - 1;
        }
    }
    public bool CanDetectSilence => !IsBusy && !IsPlaying && SelectedTrack is not null && SelectedStream is not null && _disc is not null && _loadedPlaylist is not null;
    public bool CanSplit => CanDetectSilence && PreviewSeconds >= 0.5 && PreviewSeconds <= PreviewMax - 0.5;
    public bool CanMerge
    {
        get
        {
            if (!CanDetectSilence || SelectedTrack is null) return false;
            var index = Tracks.IndexOf(SelectedTrack);
            return index >= 0 && index + 1 < Tracks.Count && Tracks[index + 1].StartTicks == SelectedTrack.EndTicks;
        }
    }
    public string LogPath => _log.FilePath;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(TrackEditsStore? editStore = null)
    {
        var paths = new ToolPaths();
        var runner = new ProcessRunner(_log);
        var probe = new FfprobeService(paths, runner);
        _discService = new DiscService(probe, _log);
        _conversion = new ConversionService(paths, runner, probe, _log);
        _navigation = new AudioNavigationService(paths, runner, probe, _log);
        _editStore = editStore ?? new TrackEditsStore(_log);
        _isoMount = new IsoMountService(runner);
        if (string.IsNullOrWhiteSpace(_outputFolder)) _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        _log.Write("Application Start");
        LanguageService.Instance.LanguageChanged += LanguageChanged;
        RefreshDrives();
    }

    public bool IsRefreshingLanguage { get; private set; }

    public void DetachLanguage() => LanguageService.Instance.LanguageChanged -= LanguageChanged;

    private void LanguageChanged(object? sender, EventArgs e)
    {
        IsRefreshingLanguage = true;
        try
        {
            foreach (var choice in PreviewChannels) choice.RefreshLanguage();
            foreach (var meter in ChannelMeters) meter.RefreshLanguage();
            foreach (var playlist in Playlists) playlist.RefreshLanguage();
            foreach (var stream in Streams) stream.RefreshLanguage();
            foreach (var candidate in SplitCandidates) candidate.RefreshLanguage();
            if (_disc is not null) RefreshMetadata();
            foreach (var name in new[] { nameof(AlbumTitle), nameof(TitleName), nameof(Status), nameof(PlayPauseText),
                nameof(SelectedTrackTitle), nameof(SelectedChapterText), nameof(SelectedStreamNote),
                nameof(VolumeDetails) }) Changed(name);
        }
        finally { IsRefreshingLanguage = false; }
    }

    public void RefreshDrives()
    {
        var drives = DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.CDRom).Select(x => x.RootDirectory.FullName).ToArray();
        foreach (var drive in drives) if (!Sources.Contains(drive)) Sources.Add(drive);
        foreach (var entry in Sources.Where(x => !drives.Contains(x) && !DiscService.HasSupportedSource(x)).ToArray()) Sources.Remove(entry);
        if (string.IsNullOrEmpty(Source) && drives.Length > 0) Source = drives[0];
    }

    public async Task DetectInsertedDiscAsync()
    {
        if (IsBusy) return;
        RefreshDrives();
        if (_isoRoot is not null)
        {
            if (DiscService.HasSupportedSource(_isoRoot)) return;
            ClearIsoSelection();
            ClearCurrentDisc();
        }
        var readyDrives = DriveInfo.GetDrives().Where(x =>
        {
            try { return x.DriveType == DriveType.CDRom && x.IsReady && DiscService.HasSupportedSource(x.RootDirectory.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }).ToArray();
        var ready = readyDrives.FirstOrDefault(x => x.RootDirectory.FullName.Equals(Source, StringComparison.OrdinalIgnoreCase))
                    ?? readyDrives.FirstOrDefault();
        var root = ready?.RootDirectory.FullName;
        if (root is null)
        {
            if (_lastReadyDrive is not null && Source == _lastReadyDrive) ClearCurrentDisc();
            _lastReadyDrive = null;
            _lastDiscSignature = null;
            return;
        }
        var signature = ReadDiscSignature(ready!);
        if (_lastReadyDrive == root && _lastDiscSignature == signature) return;
        _lastReadyDrive = root;
        _lastDiscSignature = signature;
        Source = root;
        await OpenSourceAsync(root);
    }

    private static string ReadDiscSignature(DriveInfo drive)
    {
        try
        {
            var root = drive.RootDirectory.FullName;
            var marker = new[] { Path.Combine(root, "BDMV", "index.bdmv"),
                Path.Combine(root, "AUDIO_TS", "AUDIO_TS.IFO"),
                Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO") }.First(File.Exists);
            var info = new FileInfo(marker);
            return $"{drive.VolumeLabel}|{marker}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return drive.RootDirectory.FullName;
        }
    }

    public async Task OpenSourceAsync(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path)) return;
        if (_isoRoot is not null && !Path.GetFullPath(path).Equals(_isoRoot, StringComparison.OrdinalIgnoreCase))
            ClearIsoSelection();
        BeginWork("ディスクを解析しています");
        try
        {
            ClearCurrentDisc();
            AlbumTitle = "ディスクを解析しています";
            Status = "ディスクを解析しています";
            Source = path;
            _disc = await Task.Run(() => _discService.Analyze(path), _work!.Token);
            _disc.AlbumTitle = _editStore.LoadAlbumTitle(_disc) ?? _disc.AlbumTitle;
            RefreshMetadata();
            Playlists.Clear();
            foreach (var playlist in _disc.Playlists)
            {
                playlist.ChapterTitles = _editStore.LoadChapterTitles(_disc, playlist) ??
                    Enumerable.Repeat<string?>(null, playlist.ChapterStarts.Count).ToArray();
                playlist.TitleName = _editStore.LoadTitleName(_disc, playlist) ?? playlist.TitleName;
                Playlists.Add(playlist);
            }
            var ambiguous = Playlists.Count > 1 &&
                            Playlists[1].DurationTicks >= Playlists[0].DurationTicks * 0.95 &&
                            Math.Abs(Playlists[0].ChapterStarts.Count - Playlists[1].ChapterStarts.Count) <= 2;
            SelectedPlaylist = Playlists.FirstOrDefault(playlist =>
                _editStore.Load(_disc, playlist) is not null ||
                _editStore.LoadChapterTitles(_disc, playlist) is not null ||
                _editStore.LoadTitleName(_disc, playlist) is not null) ?? Playlists.FirstOrDefault();
            if (SelectedPlaylist is not null) await LoadPlaylistCoreAsync(SelectedPlaylist, _work.Token);
            Status = PlaylistStatus(ambiguous);
        }
        catch (Exception ex) { HandleError(ex); }
        finally { EndWork(); }
    }

    public async Task OpenIsoAsync(string isoPath)
    {
        if (IsBusy) return;
        BeginWork("ISOをマウントしています");
        string root;
        try { root = await _isoMount.MountAsync(isoPath, _work!.Token); }
        catch (Exception ex)
        {
            HandleError(ex);
            return;
        }
        finally { EndWork(); }

        _isoRoot = root;
        SelectedIsoPath = Path.GetFullPath(isoPath);
        if (!Sources.Contains(root)) Sources.Add(root);
        Source = root;
        await OpenSourceAsync(root);
    }

    private void ClearIsoSelection()
    {
        _isoRoot = null;
        SelectedIsoPath = "";
    }

    public void ClearCurrentDisc()
    {
        StopPlayback();
        _navigation.ClearCache();
        _disc = null;
        _loadedPlaylist = null;
        _titleName = "";
        Changed(nameof(TitleName));
        SelectedPlaylist = null;
        Playlists.Clear();
        Streams.Clear();
        SelectedStream = null;
        foreach (var track in Tracks) track.PropertyChanged -= TrackChanged;
        Tracks.Clear();
        SelectedTrack = null;
        AlbumTitle = "ディスクを入れてください";
        MetadataDetails = "";
        Status = "対応するディスクを待っています";
        UpdateActions();
    }

    public async Task ChoosePlaylistAsync(PlaylistInfo? playlist)
    {
        if (IsBusy || playlist is null || playlist == _loadedPlaylist || _disc is null) return;
        BeginWork("アルバム候補を解析しています");
        try
        {
            await LoadPlaylistCoreAsync(playlist, _work!.Token);
            Status = PlaylistStatus(false);
        }
        catch (Exception ex) { HandleError(ex); }
        finally { EndWork(); }
    }

    private async Task LoadPlaylistCoreAsync(PlaylistInfo playlist, CancellationToken token)
    {
        if (_disc is null) return;
        StopPlayback();
        _loadedPlaylist = null;
        Streams.Clear();
        SelectedStream = null;
        foreach (var track in Tracks) track.PropertyChanged -= TrackChanged;
        Tracks.Clear();
        SelectedTrack = null;
        var result = await _discService.AnalyzePlaylistAsync(_disc, playlist, token);
        RefreshMetadata();
        foreach (var stream in result.Streams) Streams.Add(stream);
        SelectedStream = Streams.FirstOrDefault(x => x.IsStereo) ?? Streams.FirstOrDefault();
        var savedTracks = _editStore.Load(_disc, playlist);
        foreach (var track in savedTracks ?? result.Tracks)
        {
            track.PropertyChanged += TrackChanged;
            Tracks.Add(track);
        }
        _loadedPlaylist = playlist;
        _titleName = playlist.TitleName;
        Changed(nameof(TitleName));
        SelectedTrack = Tracks.FirstOrDefault();
        UpdateActions();
    }

    private string PlaylistStatus(bool ambiguous)
    {
        if (_loadedPlaylist is { } playlist && Tracks.Count == 1 &&
            Tracks[0].StartTicks == 0 && Tracks[0].EndTicks == playlist.DurationTicks)
            return "全編を1曲として表示しています。「FLACで保存」で1ファイルに保存します。";
        return ambiguous
            ? $"{Tracks.Count} 曲・チャプターを表示しています。左側でタイトルを確認してください。"
            : $"{Tracks.Count} 曲・チャプターを確認しました";
    }

    private void RefreshMetadata()
    {
        if (_disc is null) return;
        AlbumTitle = _disc.AlbumTitle;
        MetadataDetails = string.Join(Environment.NewLine, new[]
        {
            MetadataLine("アーティスト", _disc.Artist), MetadataLine("日付", _disc.Date),
            MetadataLine("ジャンル", _disc.Genre), MetadataLine("発売元", _disc.Publisher),
            MetadataLine("説明", _disc.Description)
        }.Where(x => x is not null));
    }

    private static string? MetadataLine(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{LanguageService.T(label)}: {value.Trim()}";

    public void SelectAll(bool selected)
    {
        _suspendEditSave = true;
        try { foreach (var track in Tracks) track.IsSelected = selected; }
        finally { _suspendEditSave = false; }
        SaveEdits();
        Changed(nameof(CanConvert));
    }

    public bool ApplyBulkEdit(BulkEditPlan plan)
    {
        if (!CanEditTracks || _disc is null || _loadedPlaylist is null ||
            plan.Tracks.Any(change => change.Index < 0 || change.Index >= Tracks.Count) ||
            plan.ArtistTargets.Any(index => index < 0 || index >= Tracks.Count) ||
            plan.ChapterTitles is { } titles && titles.Count != _loadedPlaylist.ChapterStarts.Count ||
            plan.TitleName is { } titleName && string.IsNullOrWhiteSpace(titleName)) return false;
        try
        {
            if (plan.ChapterTitles is { } chapterTitles &&
                !_loadedPlaylist.ChapterTitles.SequenceEqual(chapterTitles))
                _editStore.SaveChapterTitles(_disc, _loadedPlaylist, chapterTitles);
            if (_disc.AlbumTitle != plan.AlbumTitle)
                _editStore.SaveAlbumTitle(_disc, plan.AlbumTitle);
            if (plan.TitleName is { } requestedTitleName && _loadedPlaylist.TitleName != requestedTitleName.Trim())
                _editStore.SaveTitleName(_disc, _loadedPlaylist, requestedTitleName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Write($"METADATA EDIT SAVE FAILED: {ex}");
            Status = $"曲名情報を保存できませんでした: {ex.Message}";
            return false;
        }

        _suspendEditSave = true;
        try
        {
            foreach (var change in plan.Tracks)
            {
                var track = Tracks[change.Index];
                track.Title = change.Title;
                if (change.HasArtist) track.Artist = change.Artist;
            }
            if (plan.ApplyArtist)
                foreach (var index in plan.ArtistTargets) Tracks[index].Artist = plan.Artist;
        }
        finally { _suspendEditSave = false; }
        _disc.AlbumTitle = plan.AlbumTitle;
        if (plan.TitleName is { } editedTitleName)
        {
            _loadedPlaylist.TitleName = editedTitleName;
            _titleName = _loadedPlaylist.TitleName;
            Changed(nameof(TitleName));
        }
        if (plan.ChapterTitles is { } editedChapters) _loadedPlaylist.ChapterTitles = editedChapters;
        RefreshMetadata();
        Changed(nameof(SelectedChapterText));
        var saved = SaveEdits();
        if (saved) Status = $"曲情報とチャプター名を反映しました";
        return saved;
    }

    public Task NudgePreviewAsync(double seconds)
    {
        AdvancePlaybackClock();
        return SeekAsync(PreviewSeconds + seconds);
    }

    public void SplitAtPreview()
    {
        if (!CanSplit || SelectedTrack is null) return;
        var current = SelectedTrack;
        var index = Tracks.IndexOf(current);
        var boundary = current.StartTicks + (long)Math.Round(PreviewSeconds * 45000, MidpointRounding.AwayFromZero);
        if (boundary <= current.StartTicks || boundary >= current.EndTicks) return;
        var next = new TrackRow
        {
            Number = current.Number + 1,
            StartTicks = boundary,
            EndTicks = current.EndTicks,
            IsChapter = false,
            IsSelected = current.IsSelected,
            Title = LanguageService.T($"{current.Title} (分割)"),
            Artist = current.Artist
        };
        current.EndTicks = boundary;
        next.PropertyChanged += TrackChanged;
        Tracks.Insert(index + 1, next);
        RenumberTracks();
        SelectedTrack = next;
        Status = $"{Tracks.Count} 曲・区間になりました。新しい行の曲名を編集できます。";
        SaveEdits();
        UpdateActions();
    }

    public void MergeWithNext()
    {
        if (!CanMerge || SelectedTrack is null) return;
        var current = SelectedTrack;
        var index = Tracks.IndexOf(current);
        var next = Tracks[index + 1];
        current.EndTicks = next.EndTicks;
        next.PropertyChanged -= TrackChanged;
        Tracks.RemoveAt(index + 1);
        RenumberTracks();
        SplitCandidates.Clear();
        Changed(nameof(PreviewMax));
        Changed(nameof(PreviewLengthText));
        Status = $"{Tracks.Count} 曲・区間になりました。";
        SaveEdits();
        UpdateActions();
    }

    public async Task TogglePlaybackAsync()
    {
        if (IsPlaying) { PausePlayback(); return; }
        await StartPlaybackAsync(++_transportVersion);
    }

    public Task PreviousTrackAsync() => MoveTrackAsync(-1);
    public Task NextTrackAsync() => MoveTrackAsync(1);

    private async Task MoveTrackAsync(int offset)
    {
        if (!CanSeek || SelectedTrack is null) return;
        var target = Tracks.IndexOf(SelectedTrack) + offset;
        if (target < 0 || target >= Tracks.Count) return;
        var resume = IsPlaying;
        SelectedTrack = Tracks[target];
        if (resume) await StartPlaybackAsync(++_transportVersion);
        else Status = $"{SelectedTrack.Title} を選択しました";
    }

    public async Task PlaySelectedTrackAsync()
    {
        if (!CanPlay) return;
        StopPlayback();
        await StartPlaybackAsync(++_transportVersion);
    }

    public async Task SeekAsync(double seconds)
    {
        var resume = IsPlaying;
        if (resume) PausePlaybackCore(capturePosition: false);
        PreviewSeconds = seconds;
        var version = ++_transportVersion;
        if (resume && PreviewSeconds < PreviewMax) await StartPlaybackAsync(version);
        else if (resume) Status = "再生が終わりました";
    }

    private async Task StartPlaybackAsync(int version)
    {
        if (_playbackTask is { IsCompleted: false } previous) await previous;
        if (version != _transportVersion || !CanPlay || SelectedTrack is null || SelectedStream is null ||
            _disc is null || _loadedPlaylist is null) return;
        if (PreviewSeconds >= PreviewMax) PreviewSeconds = 0;
        var track = SelectedTrack;
        var stream = SelectedStream;
        var disc = _disc;
        var playlist = _loadedPlaylist;
        var mix = CurrentMix;
        var previewChannel = SelectedPreviewChannel?.Code;
        _activePlaybackMix = mix;
        _activePreviewChannel = previewChannel;
        Volatile.Write(ref _livePreviewMix, new PreviewMixState(mix, previewChannel));
        var start = track.StartTicks + (long)Math.Round(PreviewSeconds * 45000, MidpointRounding.AwayFromZero);
        var session = new CancellationTokenSource();
        _playback = session;
        ResetMeterLevels();
        IsPlaying = true;
        Status = $"{track.Title} を再生しています";
        _playbackTask = RunPlaybackAsync(session, disc, playlist, stream, track, start,
            mix, previewChannel, version);
    }

    private async Task RunPlaybackAsync(CancellationTokenSource session, DiscAnalysis disc, PlaylistInfo playlist,
        AudioStreamInfo stream, TrackRow track, long startTicks, StereoMixSettings mix,
        string? previewChannel, int version)
    {
        var completed = false;
        var uiContext = SynchronizationContext.Current;
        try
        {
            await _navigation.PlayAsync(disc, playlist, stream, startTicks, track.EndTicks, session.Token,
                positionTicks =>
                {
                    uiContext?.Post(_ =>
                    {
                        if (!ReferenceEquals(_playback, session) || !IsPlaying || version != _transportVersion) return;
                        _playClockBase = (positionTicks - track.StartTicks) / 45000d;
                        _playClock.Restart();
                    }, null);
                }, () => VolumeCurve.Gain(Volume, PerceivedVolume), mix, previewChannel,
                (segmentStart, seconds, peaks, rms) => uiContext?.Post(_ =>
                {
                    if (ReferenceEquals(_playback, session) && version == _transportVersion)
                        QueueMeterFrame((segmentStart - track.StartTicks) / 45000d + seconds +
                            AudioNavigationService.MeterWindowSeconds / 2, peaks, rms);
                }, null),
                (segmentStart, seconds) =>
                {
                    var sampledAt = Stopwatch.GetTimestamp();
                    uiContext?.Post(_ =>
                    {
                        if (!ReferenceEquals(_playback, session) || !IsPlaying || version != _transportVersion ||
                            !_playClock.IsRunning) return;
                        _playClockBase = Math.Clamp((segmentStart - track.StartTicks) / 45000d + seconds +
                            Stopwatch.GetElapsedTime(sampledAt).TotalSeconds, 0, PreviewMax);
                        _playClock.Restart();
                        AdvancePlaybackClock();
                    }, null);
                }, () => Volatile.Read(ref _livePreviewMix));
            if (ReferenceEquals(_playback, session) && IsPlaying)
            {
                completed = true;
                PreviewSeconds = PreviewMax;
                Status = "再生が終わりました";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_playback, session)) HandleError(ex); }
        finally
        {
            var nextIndex = Tracks.IndexOf(track) + 1;
            var advance = completed && ContinuousPlayback && version == _transportVersion &&
                          ReferenceEquals(_playback, session) && SelectedTrack == track &&
                          nextIndex > 0 && nextIndex < Tracks.Count;
            if (ReferenceEquals(_playback, session))
            {
                _playClock.Stop();
                _playback = null;
                IsPlaying = false;
            }
            session.Dispose();
            if (advance)
            {
                // The finished task is the task we are in; clear it before starting the next track.
                _playbackTask = null;
                SelectedTrack = Tracks[nextIndex];
                await StartPlaybackAsync(++_transportVersion);
            }
        }
    }

    public void AdvancePlaybackClock()
    {
        if (IsPlaying && !IsScrubbing && _playClock.IsRunning)
            PreviewSeconds = Math.Min(PreviewMax, _playClockBase + _playClock.Elapsed.TotalSeconds);
        ApplyMeterFrames();
    }

    private void QueueMeterFrame(double position, double[] peaks, double[] rms)
    {
        _meterFrames.Enqueue((position, peaks, rms));
        while (_meterFrames.Count > 120) _meterFrames.Dequeue();
        ApplyMeterFrames();
    }

    private void ApplyMeterFrames()
    {
        while (_meterFrames.TryPeek(out var frame) && frame.Position <= PreviewSeconds)
        {
            _meterFrames.Dequeue();
            for (var i = 0; i < Math.Min(ChannelMeters.Count, frame.Peaks.Length); i++)
                ChannelMeters[i].Update(frame.Peaks[i], frame.Rms[i]);
        }
    }

    private void ResetMeterLevels()
    {
        _meterFrames.Clear();
        foreach (var meter in ChannelMeters) meter.Update(double.NegativeInfinity, double.NegativeInfinity);
    }

    public async Task ChangePreviewChannelAsync()
    {
        if (!IsPlaying) return;
        if (SelectedStream is { } stream && StereoMixSettings.Supports(stream))
        {
            _activePreviewChannel = SelectedPreviewChannel?.Code;
            Volatile.Write(ref _livePreviewMix, new PreviewMixState(CurrentMix, _activePreviewChannel));
            _log.Write($"PREVIEW MIX channel={_activePreviewChannel ?? "stereo"} " +
                       $"mix={CurrentMix.Center:0.###}/{CurrentMix.Surround:0.###}/{CurrentMix.Lfe:0.###} front={CurrentMix.Front:0.###}");
            return;
        }
        AdvancePlaybackClock();
        await SeekAsync(PreviewSeconds);
    }

    public async Task ApplyStereoMixAsync()
    {
        if (!IsPlaying) return;
        if (CurrentMix == _activePlaybackMix && SelectedPreviewChannel?.Code == _activePreviewChannel) return;
        if (SelectedStream is { } stream && StereoMixSettings.Supports(stream))
        {
            _activePlaybackMix = CurrentMix;
            _activePreviewChannel = SelectedPreviewChannel?.Code;
            Volatile.Write(ref _livePreviewMix, new PreviewMixState(_activePlaybackMix, _activePreviewChannel));
            _log.Write($"PREVIEW MIX channel={_activePreviewChannel ?? "stereo"} " +
                       $"mix={_activePlaybackMix.Center:0.###}/{_activePlaybackMix.Surround:0.###}/{_activePlaybackMix.Lfe:0.###} front={_activePlaybackMix.Front:0.###}");
            return;
        }
        AdvancePlaybackClock();
        await SeekAsync(PreviewSeconds);
    }

    public async Task ResetStereoMixAsync()
    {
        FrontMixPercent = StereoMixSettings.Default.Front * 100;
        CenterMixPercent = StereoMixSettings.Default.Center * 100;
        SurroundMixPercent = StereoMixSettings.Default.Surround * 100;
        LfeMixPercent = StereoMixSettings.Default.Lfe * 100;
        FrontMixMuted = false;
        CenterMixMuted = false;
        SurroundMixMuted = false;
        LfeMixMuted = false;
        await ApplyStereoMixAsync();
    }

    public void PausePlayback()
    {
        PausePlaybackCore(capturePosition: true);
    }

    private void PausePlaybackCore(bool capturePosition)
    {
        if (!IsPlaying) return;
        if (capturePosition) AdvancePlaybackClock();
        _playClock.Stop();
        _playback?.Cancel();
        ++_transportVersion;
        IsPlaying = false;
        Status = "一時停止しました";
    }

    public void StopPlayback()
    {
        ++_transportVersion;
        _playClock.Stop();
        _playback?.Cancel();
        IsPlaying = false;
        PreviewSeconds = 0;
        ResetMeterLevels();
        if (SelectedTrack is not null) Status = "再生を停止しました";
    }

    public async Task FindSilenceAsync()
    {
        if (!CanDetectSilence || SelectedTrack is null || SelectedStream is null || _disc is null || _loadedPlaylist is null) return;
        var track = SelectedTrack;
        BeginWork("無音の境界候補を探しています");
        SplitCandidates.Clear();
        try
        {
            var candidates = await _navigation.FindSilenceAsync(_disc, _loadedPlaylist, SelectedStream, track, _work!.Token);
            if (SelectedTrack == track)
            {
                foreach (var candidate in candidates) SplitCandidates.Add(candidate);
                Status = candidates.Count == 0
                    ? "無音候補は見つかりませんでした。時刻を指定して分割できます。"
                    : $"{candidates.Count} 件の無音候補が見つかりました。聴いて位置を確認してください。";
            }
        }
        catch (Exception ex) { HandleError(ex); }
        finally { EndWork(); }
    }

    private void RenumberTracks()
    {
        for (var i = 0; i < Tracks.Count; i++) Tracks[i].Number = i + 1;
        Changed(nameof(SelectedTrackTitle));
        Changed(nameof(CanConvert));
    }

    public async Task ConvertAsync()
    {
        if (!CanConvert || _disc is null || _loadedPlaylist is null || SelectedStream is null) return;
        BeginWork("FLAC に変換しています");
        SavedFolder = "";
        Progress = 0;
        try
        {
            var chosen = Tracks.Where(x => x.IsSelected).ToArray();
            var progress = new Progress<ConversionProgress>(value => { Progress = value.Fraction; Status = value.Message; });
            SavedFolder = await _conversion.ConvertAsync(_disc, _loadedPlaylist, SelectedStream, chosen, _quality,
                OutputFolder, progress, _work!.Token, GroupByChapter, SaveStereoDownmix, CurrentMix,
                SaveIndividualChannels);
        }
        catch (Exception ex) { HandleError(ex); }
        finally { EndWork(); }
    }

    public void Cancel()
    {
        _work?.Cancel();
        StopPlayback();
    }
    public void OpenSavedFolder()
    {
        if (CanOpenSavedFolder && Directory.Exists(SavedFolder))
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { SavedFolder } });
    }

    private void BeginWork(string message)
    {
        StopPlayback();
        _work = new CancellationTokenSource();
        IsBusy = true;
        Status = message;
        Changed(nameof(CanChooseHighRes));
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        IsBusy = false;
        Changed(nameof(CanChooseHighRes));
        Changed(nameof(CanConvert));
    }

    private void HandleError(Exception exception)
    {
        if (exception is OperationCanceledException) { Status = "処理を中止しました"; return; }
        _log.Write($"ERROR {exception}");
        Status = exception switch
        {
            ProtectedDiscException => exception.Message,
            InvalidDataException => exception.Message,
            FileNotFoundException => exception.Message,
            FfToolException => "音声の解析または変換に失敗しました。詳細はログを確認してください。",
            _ => $"処理に失敗しました: {LanguageService.T(exception.Message)}"
        };
    }

    private void TrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackRow.IsSelected)) Changed(nameof(CanConvert));
        if (sender == SelectedTrack && e.PropertyName == nameof(TrackRow.Title)) Changed(nameof(SelectedTrackTitle));
        if (!_suspendEditSave && e.PropertyName is nameof(TrackRow.Title) or nameof(TrackRow.Artist) or nameof(TrackRow.IsSelected))
            SaveEdits();
        if (sender == SelectedTrack && e.PropertyName is nameof(TrackRow.StartTicks) or nameof(TrackRow.EndTicks))
        {
            Changed(nameof(PreviewMax));
            Changed(nameof(PreviewLengthText));
            Changed(nameof(SelectedChapterText));
            PreviewSeconds = Math.Min(PreviewSeconds, PreviewMax);
            UpdateActions();
        }
    }

    private bool SaveEdits()
    {
        if (_disc is null || _loadedPlaylist is null || Tracks.Count == 0) return false;
        try { _editStore.Save(_disc, _loadedPlaylist, Tracks.ToArray()); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.Write($"TRACK EDITS SAVE FAILED: {ex}");
            Status = $"曲名情報を保存できませんでした: {LanguageService.T(ex.Message)}";
            return false;
        }
    }

    private void UpdateActions()
    {
        Changed(nameof(CanConvert));
        Changed(nameof(CanEditTracks));
        Changed(nameof(CanPlay));
        Changed(nameof(CanSeek));
        Changed(nameof(CanPreviousTrack));
        Changed(nameof(CanNextTrack));
        Changed(nameof(CanDetectSilence));
        Changed(nameof(CanSplit));
        Changed(nameof(CanMerge));
        Changed(nameof(CanDownmixStereo));
        Changed(nameof(CanChoosePreviewChannel));
    }

    private bool Set<T>(ref T target, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return false;
        target = value;
        Changed(name);
        return true;
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
