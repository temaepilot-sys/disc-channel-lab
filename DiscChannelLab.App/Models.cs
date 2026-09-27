using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Disc2Flac;

public enum OutputQuality { Cd, HighResolution }
public enum DiscFormat { BluRay, DvdAudio, DvdVideo }
public enum AudioAvailability { Pending, Analyzing, Ready, Partial, NoAudio, Unsupported, Failed, Protected }

public sealed record DvdAudioProgram(long StartSector, long EndSector, long StartTicks, long EndTicks);
public sealed record DvdAudioTitle(int TitleSet, int TitleNumber, IReadOnlyList<DvdAudioProgram> Programs);

public sealed record ClipInfo(string Id, long InTicks, long OutTicks, long PlaylistStartTicks);

public sealed class PlaylistInfo : INotifyPropertyChanged
{
    public required int Id { get; init; }
    public DiscFormat Format { get; init; } = DiscFormat.BluRay;
    public DvdAudioTitle? DvdAudio { get; init; }
    public int DvdVideoTitle { get; init; }
    private int _displayOrder;
    private string? _titleName;
    private IReadOnlyList<string?> _chapterTitles = [];
    public int DisplayOrder
    {
        get => _displayOrder;
        set
        {
            if (_displayOrder == value) return;
            _displayOrder = value;
            Changed(nameof(DisplayOrder));
            Changed(nameof(DisplayNumber));
            Changed(nameof(DisplayName));
        }
    }
    public required IReadOnlyList<ClipInfo> Clips { get; set; }
    public required IReadOnlyList<long> ChapterStarts { get; set; }
    internal int InspectionVersion { get; set; }
    public bool MergeShortTail { get; set; } = true;
    public bool HasShortTail => ChapterStarts.Count > 1 && DurationTicks - ChapterStarts[^1] < 45000;
    public IReadOnlyList<long> EffectiveChapterStarts => MergeShortTail && HasShortTail
        ? ChapterStarts.Take(ChapterStarts.Count - 1).ToArray() : ChapterStarts;
    private AudioAvailability _availability;
    private string _analysisDetails = "";
    public AudioAvailability Availability
    {
        get => _availability;
        set { _availability = value; Changed(nameof(Availability)); Changed(nameof(AvailabilityLabel)); }
    }
    public string AnalysisDetails
    {
        get => _analysisDetails;
        set { _analysisDetails = value; Changed(nameof(AnalysisDetails)); }
    }
    public string AvailabilityLabel => LanguageService.T(Availability switch
    {
        AudioAvailability.Analyzing => "音声を確認中",
        AudioAvailability.Ready => "再生確認済み",
        AudioAvailability.Partial => "音声あり・一部未確認",
        AudioAvailability.NoAudio => "音声なし",
        AudioAvailability.Unsupported => "対応対象外の音声",
        AudioAvailability.Failed => "解析失敗・再試行可能",
        AudioAvailability.Protected => "保護されたディスク",
        _ => "未確認"
    });
    public IReadOnlyList<string?> ChapterTitles
    {
        get => _chapterTitles;
        set
        {
            _chapterTitles = value;
            Changed(nameof(ChapterTitles));
            Changed(nameof(ChapterSummary));
            Changed(nameof(DisplayName));
        }
    }
    public required long DurationTicks { get; set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationTicks / 45000d);
    public string DisplayNumber => DisplayOrder.ToString("00");
    public string TitleLabel => Format == DiscFormat.DvdAudio ? $"A {DvdAudio?.TitleSet:00}/{DvdAudio?.TitleNumber:00}"
        : Format == DiscFormat.DvdVideo ? $"V {DvdVideoTitle:00}" : $"BD {DisplayNumber}";
    public string TitleName
    {
        get => _titleName ?? (Format == DiscFormat.DvdAudio
            ? $"DVD-Audio {DvdAudio?.TitleSet:00}-{DvdAudio?.TitleNumber:00}"
            : Format == DiscFormat.DvdVideo ? $"DVD-Video {DvdVideoTitle:00}" : TitleLabel);
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_titleName == name) return;
            _titleName = name;
            Changed(nameof(TitleName));
            Changed(nameof(DisplayName));
        }
    }
    public string DurationLabel => Duration.ToString(@"hh\:mm\:ss");
    public string ChapterCountLabel => LanguageService.T($"{ChapterStarts.Count}チャプター");
    public bool IsRepeatedShortClipLoop => Clips.Count >= 16 &&
        Clips.Count >= Clips.Select(clip => clip.Id).Distinct().Count() * 4 &&
        Clips.All(clip => clip.OutTicks - clip.InTicks <= 125L * 45000);
    public string? ChapterTitle(int number) => number >= 1 && number <= ChapterTitles.Count &&
        !string.IsNullOrWhiteSpace(ChapterTitles[number - 1]) ? ChapterTitles[number - 1]!.Trim() : null;
    public string ChapterSummary
    {
        get
        {
            var names = ChapterTitles.Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!.Trim()).ToArray();
            return names.Length > 0 ? string.Join(" / ", names)
                : LanguageService.T(IsRepeatedShortClipLoop ? "短いクリップの繰り返し" : "名称未設定");
        }
    }
    public string DisplayName => $"{(Format == DiscFormat.DvdAudio ? $"DVD-Audio {DvdAudio?.TitleSet:00}/{DvdAudio?.TitleNumber:00}" : Format == DiscFormat.DvdVideo ? $"DVD-Video {DvdVideoTitle:00}" : DisplayNumber)} · {DurationLabel} · {ChapterCountLabel} · {TitleName} · {ChapterSummary}";
    public override string ToString() => DisplayName;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage()
    {
        Changed(nameof(Duration));
        Changed(nameof(DurationLabel));
        Changed(nameof(AvailabilityLabel));
        Changed(nameof(ChapterCountLabel));
        Changed(nameof(ChapterSummary));
        Changed(nameof(DisplayName));
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AudioStreamInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
    public required int Index { get; init; }
    public required string Codec { get; init; }
    public string Profile { get; init; } = "";
    public string TransportId { get; init; } = "";
    public string Language { get; init; } = "";
    public IReadOnlyList<string> CoveredClipIds { get; init; } = [];
    public IReadOnlyList<string> InspectedClipIds { get; init; } = [];
    public string? SilentTailClipId { get; init; }
    public int TotalClips { get; init; }
    public bool HasPartialCoverage => TotalClips > 0 && CoveredClipIds.Count + (SilentTailClipId is null ? 0 : 1) < TotalClips;
    public AudioStreamInfo WithCoverage(IReadOnlyList<string> covered, IReadOnlyList<string> inspected, int total,
        string? silentTailClipId = null) => new()
    {
        Index = Index, Codec = Codec, Profile = Profile, TransportId = TransportId, Language = Language,
        SampleRate = SampleRate, Channels = Channels, ChannelLayout = ChannelLayout, BitDepth = BitDepth,
        StartTimeTicks = StartTimeTicks, SampleCount = SampleCount, CoveredClipIds = covered,
        InspectedClipIds = inspected, TotalClips = total, SilentTailClipId = silentTailClipId
    };
    public bool Covers(PlaylistInfo playlist, TrackRow track) => playlist.Clips
        .Where(clip => clip.PlaylistStartTicks < track.EndTicks && clip.PlaylistStartTicks + clip.OutTicks - clip.InTicks > track.StartTicks)
        .All(clip => TotalClips == 0 || CoveredClipIds.Contains(clip.Id) || IsSilentTail(playlist, clip));
    public bool IsSilentTail(PlaylistInfo playlist, ClipInfo clip) => SilentTailClipId == clip.Id &&
        playlist.Clips.LastOrDefault() == clip && clip.OutTicks > clip.InTicks &&
        clip.OutTicks - clip.InTicks <= 2 * 45000 &&
        clip.PlaylistStartTicks + clip.OutTicks - clip.InTicks == playlist.DurationTicks;
    public string SilenceInput => $"anullsrc=r={SampleRate}:cl={(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : ChannelLayout)}";
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required string ChannelLayout { get; init; }
    public int? BitDepth { get; init; }
    public long? StartTimeTicks { get; init; }
    public long? SampleCount { get; init; }
    public bool IsStereo => Channels == 2;
    public bool HasSupportedChannels => Channels is >= 1 and <= 8 && (Channels <= 2 || ChannelLayout != "unknown");
    public bool CanMakeCd => HasSupportedChannels && BitDepth >= 16 && SampleRate >= 44100;
    public bool CanMakeHighResolution => HasSupportedChannels && BitDepth >= 24 && SampleRate >= 88200;
    public string DisplayName => $"{CodecLabel} · {(Channels == 1 ? "Mono" : IsStereo ? "Stereo" : $"{Channels}ch ({ChannelLayout})")} · {SampleRate / 1000d:0.0}kHz / {(BitDepth is null ? LanguageService.T("深度不明") : $"{BitDepth}bit")}" +
        (HasPartialCoverage ? $" · {LanguageService.T("一部区間のみ")} #{Index}" : "") +
        (SilentTailClipId is not null ? $" · {LanguageService.T("末尾の映像区間は無音")}" : "");
    private string CodecLabel => LanguageService.T(Codec switch
    {
        "pcm_bluray" or "pcm_dvd" or "pcm_dvda" => "LPCM",
        "mlp" => "可逆圧縮音声",
        "dts" when Profile == "DTS-HD MA" => "可逆圧縮音声",
        _ => "圧縮音声"
    });
    public override string ToString() => DisplayName;
}

public sealed class TrackRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private string _title = "";
    private string? _artist;
    private int _number;
    private long _startTicks;
    private long _endTicks;

    public required int Number { get => _number; set { if (_number != value) { _number = value; OnPropertyChanged(); } } }
    public required long StartTicks { get => _startTicks; set { if (_startTicks != value) { _startTicks = value; OnPropertyChanged(); OnPropertyChanged(nameof(StartTime)); OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(DurationSeconds)); } } }
    public required long EndTicks { get => _endTicks; set { if (_endTicks != value) { _endTicks = value; OnPropertyChanged(); OnPropertyChanged(nameof(EndTime)); OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(DurationSeconds)); } } }
    public required bool IsChapter { get; init; }
    public bool IsSelected { get => _selected; set { if (_selected != value) { _selected = value; OnPropertyChanged(); } } }
    public string Title { get => _title; set { if (_title != value) { _title = value; OnPropertyChanged(); } } }
    public string? Artist { get => _artist; set { if (_artist != value) { _artist = value; OnPropertyChanged(); } } }
    public double DurationSeconds => (EndTicks - StartTicks) / 45000d;
    public string StartTime => FormatTime(StartTicks);
    public string EndTime => FormatTime(EndTicks);
    public string Duration => FormatTime(EndTicks - StartTicks);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatTime(long ticks) => TimeSpan.FromSeconds(ticks / 45000d).ToString(@"hh\:mm\:ss\.fff");
}

public sealed record SplitCandidate(long PositionTicks, string Description) : INotifyPropertyChanged
{
    public string DisplayName => LanguageService.T(Description);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
    public override string ToString() => DisplayName;
}

public sealed class DiscAnalysis
{
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public required string Root { get; init; }
    public DiscFormat Format { get; init; } = DiscFormat.BluRay;
    public string DiscKey { get; init; } = "";
    public required string AlbumTitle { get; set; }
    public string? Artist { get; set; }
    public string? Date { get; set; }
    public string? Genre { get; set; }
    public string? Publisher { get; set; }
    public string? Description { get; set; }
    public required IReadOnlyList<PlaylistInfo> Playlists { get; init; }
}

public sealed record ChapterMetadata(string? Title, string? Artist)
{
    public long? StartTicks { get; init; }
}
public sealed record PlaylistProbeResult(IReadOnlyList<AudioStreamInfo> Streams,
    IReadOnlyDictionary<int, ChapterMetadata> Chapters, IReadOnlyDictionary<string, string> Tags)
{
    public bool ConfirmedShortVideoOnly { get; init; }
    public int DetectedAudioCount { get; init; }
    public AudioAvailability Availability { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
