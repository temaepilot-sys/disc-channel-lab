using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Disc2Flac;

public enum OutputQuality { Cd, HighResolution }
public enum DiscFormat { BluRay, DvdAudio, DvdVideo }

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
    public required IReadOnlyList<ClipInfo> Clips { get; init; }
    public required IReadOnlyList<long> ChapterStarts { get; init; }
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
    public required long DurationTicks { get; init; }
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
    public bool IsRepeatedShortClipLoop => ChapterStarts.Count == 1 && Clips.Count >= 20 &&
        Clips.All(clip => clip.Id == Clips[0].Id && clip.OutTicks - clip.InTicks <= 60 * 45000);
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
    public string DisplayName => $"{CodecLabel} · {(Channels == 1 ? "Mono" : IsStereo ? "Stereo" : $"{Channels}ch ({ChannelLayout})")} · {SampleRate / 1000d:0.0}kHz / {(BitDepth is null ? LanguageService.T("深度不明") : $"{BitDepth}bit")}";
    private string CodecLabel => LanguageService.T(Codec switch
    {
        "pcm_bluray" or "pcm_dvd" => "LPCM",
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

public sealed record ChapterMetadata(string? Title, string? Artist);
public sealed record PlaylistProbeResult(IReadOnlyList<AudioStreamInfo> Streams,
    IReadOnlyDictionary<int, ChapterMetadata> Chapters, IReadOnlyDictionary<string, string> Tags);
