using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

if (args is ["disc-selection-test", var verifyPath, var expectedIdText])
{
    Exception? failure = null;
    var selectedId = 0;
    var selectedTrackCount = 0;
    var thread = new Thread(() =>
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var app = new App();
            app.InitializeComponent();
            var main = new MainWindow();
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
            var viewModel = (MainViewModel)main.DataContext;
            var task = viewModel.OpenSourceAsync(verifyPath);
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            selectedId = viewModel.SelectedPlaylist?.Id ?? 0;
            selectedTrackCount = viewModel.Tracks.Count;
            main.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    if (selectedId != int.Parse(expectedIdText) || selectedTrackCount == 0)
        throw new InvalidDataException($"Saved playlist selection failed: {selectedId:00000}, tracks={selectedTrackCount}");
    Console.WriteLine($"Saved playlist {selectedId:00000} selected with {selectedTrackCount} tracks");
    return 0;
}

if (args is ["disc-edits-check", var discPath])
{
    var checkLog = new AppLog();
    var checkPaths = new ToolPaths();
    var checkRunner = new ProcessRunner(checkLog);
    var checkedDisc = new DiscService(new FfprobeService(checkPaths, checkRunner), checkLog).Analyze(discPath);
    var edits = new TrackEditsStore(checkLog);
    Console.WriteLine($"Disc key: {checkedDisc.DiscKey}");
    Console.WriteLine($"Saved album: {edits.LoadAlbumTitle(checkedDisc) is not null}");
    foreach (var checkedPlaylist in checkedDisc.Playlists)
    {
        var rows = edits.Load(checkedDisc, checkedPlaylist);
        var chapters = edits.LoadChapterTitles(checkedDisc, checkedPlaylist);
        if (rows is not null || chapters is not null)
            Console.WriteLine($"Playlist {checkedPlaylist.Id:00000}: tracks={rows?.Count ?? 0}, chapters={chapters?.Count ?? 0}");
    }
    return 0;
}

if (args is ["title-ui-test", var titleSourcePath, var titlePlaylistIdText, var expectedTitleName])
{
    var model = new MainViewModel();
    await model.OpenSourceAsync(titleSourcePath);
    var uiTitlePlaylist = model.Playlists.Single(x => x.Id == int.Parse(titlePlaylistIdText));
    await model.ChoosePlaylistAsync(uiTitlePlaylist);
    if (model.TitleName != expectedTitleName || model.GroupByChapter)
        throw new InvalidDataException($"Title name or chapter-folder default is wrong: {model.TitleName}, {model.GroupByChapter}");
    Console.WriteLine($"Title name and chapter-folder default verified: {model.TitleName}");
    return 0;
}

if (args is ["convert-saved-title", var savedSourcePath, var savedOutputRoot, var savedPlaylistIdText])
{
    var model = new MainViewModel();
    await model.OpenSourceAsync(savedSourcePath);
    await model.ChoosePlaylistAsync(model.Playlists.Single(x => x.Id == int.Parse(savedPlaylistIdText)));
    model.OutputFolder = savedOutputRoot;
    model.IsCdSelected = true;
    var expectedFiles = model.Tracks.Count(x => x.IsSelected);
    if (expectedFiles == 0 || !model.CanConvert)
        throw new InvalidDataException("Saved title is not ready for conversion.");
    await model.ConvertAsync();
    if (string.IsNullOrWhiteSpace(model.SavedFolder) ||
        Directory.GetFiles(model.SavedFolder, "*.flac", SearchOption.TopDirectoryOnly).Length != expectedFiles)
        throw new InvalidDataException($"Saved title conversion failed: {model.Status}");
    Console.WriteLine($"Converted {expectedFiles} saved tracks: {model.SavedFolder}");
    return 0;
}

if (args is ["title-layout-test", var layoutSourcePath, var layoutOutputRoot, var layoutPlaylistIdText, var layoutTitleName])
{
    var checkLog = new AppLog();
    var checkPaths = new ToolPaths();
    var checkRunner = new ProcessRunner(checkLog);
    var checkProbe = new FfprobeService(checkPaths, checkRunner);
    var checkReader = new DiscService(checkProbe, checkLog);
    var checkedDisc = checkReader.Analyze(layoutSourcePath);
    checkedDisc.AlbumTitle = new TrackEditsStore(checkLog).LoadAlbumTitle(checkedDisc) ?? checkedDisc.AlbumTitle;
    var checkedPlaylist = checkedDisc.Playlists.Single(x => x.Id == int.Parse(layoutPlaylistIdText));
    var edits = new TrackEditsStore(checkLog, Path.Combine(layoutOutputRoot, ".test-edits"));
    edits.SaveTitleName(checkedDisc, checkedPlaylist, layoutTitleName);
    checkedPlaylist.TitleName = edits.LoadTitleName(checkedDisc, checkedPlaylist) ??
        throw new InvalidDataException("Title name was not persisted.");
    var (streams, tracks) = await checkReader.AnalyzePlaylistAsync(checkedDisc, checkedPlaylist, CancellationToken.None);
    var first = tracks.First();
    var excerpt = new TrackRow
    {
        Number = first.Number, StartTicks = first.StartTicks,
        EndTicks = Math.Min(first.EndTicks, first.StartTicks + 2 * 45000),
        IsChapter = first.IsChapter, IsSelected = true, Title = "Layout verification"
    };
    var layoutOutput = await new ConversionService(checkPaths, checkRunner, checkProbe, checkLog).ConvertAsync(
        checkedDisc, checkedPlaylist, streams.First(), [excerpt], OutputQuality.Cd,
        layoutOutputRoot, new Progress<ConversionProgress>(), CancellationToken.None);
    var expected = Path.Combine(layoutOutputRoot, ConversionService.SafeName(checkedDisc.AlbumTitle),
        ConversionService.SafeName(layoutTitleName));
    if (!Path.GetFullPath(layoutOutput).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
        Directory.GetFiles(expected, "*.flac", SearchOption.TopDirectoryOnly).Length != 1)
        throw new InvalidDataException($"Unexpected title folder: {layoutOutput}");
    var savedFile = Directory.GetFiles(expected, "*.flac", SearchOption.TopDirectoryOnly).Single();
    var probeResult = await checkRunner.RunAsync(checkPaths.Ffprobe,
        ["-v", "error", "-show_entries", "format_tags", "-of", "json", savedFile], CancellationToken.None);
    using var tagsDocument = JsonDocument.Parse(probeResult.Output);
    var tags = tagsDocument.RootElement.GetProperty("format").GetProperty("tags");
    if (tags.GetProperty("album").GetString() != layoutTitleName ||
        tags.GetProperty("DISC_TITLE").GetString() != checkedDisc.AlbumTitle ||
        tags.GetProperty("CHAPTERNUMBER").GetString() != first.Number.ToString())
        throw new InvalidDataException("FLAC album, disc, or chapter tags do not match the selected title.");
    Console.WriteLine($"Title layout and persistence verified: {layoutOutput}");
    return 0;
}

if (args is ["convert-bd-chapter", var chapterSourcePath, var chapterOutputRoot,
    var chapterPlaylistIdText, var chapterNumberText])
{
    var chapterLog = new AppLog();
    var chapterPaths = new ToolPaths();
    var chapterRunner = new ProcessRunner(chapterLog);
    var chapterProbe = new FfprobeService(chapterPaths, chapterRunner);
    var chapterReader = new DiscService(chapterProbe, chapterLog);
    var chapterDisc = chapterReader.Analyze(chapterSourcePath);
    var chapterPlaylist = chapterDisc.Playlists.Single(x => x.Id == int.Parse(chapterPlaylistIdText)
        && x.Format == DiscFormat.BluRay);
    var (chapterStreams, chapterTracks) = await chapterReader.AnalyzePlaylistAsync(
        chapterDisc, chapterPlaylist, CancellationToken.None);
    var chapterTrack = chapterTracks.Single(x => x.Number == int.Parse(chapterNumberText));
    var chapterOutput = await new ConversionService(chapterPaths, chapterRunner, chapterProbe, chapterLog)
        .ConvertAsync(chapterDisc, chapterPlaylist, chapterStreams.First(), [chapterTrack], OutputQuality.Cd,
            chapterOutputRoot, new Progress<ConversionProgress>(), CancellationToken.None);
    Console.WriteLine($"Converted full BD chapter {chapterTrack.Number}: {chapterOutput}");
    return 0;
}

if (args is ["meter-layout-test"])
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            ThemeService.Apply(false);
            var main = new MainWindow();
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
            ((MainViewModel)main.DataContext).ChannelMeters.Add(new ChannelMeter("FL", "Front left"));
            var root = (UIElement)main.Content;
            root.Measure(new Size(1200, 850));
            root.Arrange(new Rect(0, 0, 1200, 850));
            root.UpdateLayout();
            ((MainViewModel)main.DataContext).ChannelMeters[0].Update(-6, -12);
            root.UpdateLayout();
            main.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    Console.WriteLine("Populated channel meter layout verified");
    return 0;
}

if (args is ["playback-controls-test"])
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            var main = new MainWindow();
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
            var model = (MainViewModel)main.DataContext;
            model.SelectedStream = new AudioStreamInfo
            {
                Index = 0, Codec = "pcm_bluray", SampleRate = 48000, Channels = 6,
                ChannelLayout = "5.1", BitDepth = 24
            };
            typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPlaying))!
                .SetValue(model, true);
            var root = (UIElement)main.Content;
            root.Measure(new Size(1200, 850));
            root.Arrange(new Rect(0, 0, 1200, 850));
            root.UpdateLayout();
            if (!main.StereoMixExpander.IsEnabled)
                throw new InvalidDataException("Stereo mix panel is disabled during playback.");
            main.StereoMixExpander.IsExpanded = true;
            main.StereoMixExpander.IsExpanded = false;
            if (main.StereoMixExpander.IsExpanded)
                throw new InvalidDataException("Stereo mix panel did not close during playback.");
            var findRow = typeof(MainWindow).GetMethod("FindPlayableTrackRow", BindingFlags.Static | BindingFlags.NonPublic)!;
            var row = new DataGridRow();
            if (!ReferenceEquals(findRow.Invoke(null, [row]), row) ||
                findRow.Invoke(null, [new CheckBox()]) is not null ||
                findRow.Invoke(null, [new TextBox()]) is not null)
                throw new InvalidDataException("Track double-click hit testing failed.");
            main.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    Console.WriteLine("Playback panel and track double-click targets verified");
    return 0;
}

if (args is ["theme-test"])
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            ThemeService.Apply(false);
            var main = new MainWindow();
            if (((SolidColorBrush)main.Background).Color != Color.FromRgb(0xF5, 0xF7, 0xFA))
                throw new InvalidDataException("Light theme did not load.");
            ThemeService.Apply(true);
            var editor = new BulkEditWindow("", "Title", [], [], [], []);
            main.SourceCombo.ApplyTemplate();
            var sourcePopup = (Popup)main.SourceCombo.Template.FindName("PART_Popup", main.SourceCombo);
            var popupBorder = (Border)sourcePopup.Child;
            if (((SolidColorBrush)main.Background).Color != Color.FromRgb(0x10, 0x18, 0x20) ||
                ((SolidColorBrush)editor.Background).Color != Color.FromRgb(0x10, 0x18, 0x20) ||
                ((SolidColorBrush)main.TracksGrid.Background).Color != Color.FromRgb(0x1B, 0x26, 0x31) ||
                ((SolidColorBrush)editor.AlbumBox.Background).Color != Color.FromRgb(0x27, 0x38, 0x47) ||
                ((SolidColorBrush)popupBorder.Background).Color != Color.FromRgb(0x27, 0x38, 0x47))
                throw new InvalidDataException("Dark theme did not update both windows.");
            ThemeService.Apply(false);
            if (((SolidColorBrush)main.Background).Color != Color.FromRgb(0xF5, 0xF7, 0xFA) ||
                ((SolidColorBrush)popupBorder.Background).Color != Colors.White)
                throw new InvalidDataException("Light theme did not restore.");
            editor.Close();
            main.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    Console.WriteLine("Light and dark resources verified in both windows");
    return 0;
}

if (args is ["language-test"])
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            LanguageService.Instance.Apply(false);
            ThemeService.Apply(true);
            var main = new MainWindow();
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
            var model = (MainViewModel)main.DataContext;
            var root = (UIElement)main.Content;
            root.Measure(new Size(1200, 850));
            root.Arrange(new Rect(0, 0, 1200, 850));
            root.UpdateLayout();
            if (main.ThemeToggle.IsChecked != true || main.LanguageCombo.SelectedIndex != 0 ||
                main.ThemeToggle.Content?.ToString() != "Dark mode" ||
                main.TracksGrid.Columns[2].Header?.ToString() != "Track title (double-click to play)" ||
                model.PlayPauseText != "Play" || model.Status != "Checking optical drives")
                throw new InvalidDataException("English and dark defaults did not load.");
            model.SelectedStream = new AudioStreamInfo
            {
                Index = 0, Codec = "pcm_bluray", SampleRate = 48000, Channels = 6,
                ChannelLayout = "5.1", BitDepth = 24
            };
            if (model.PreviewChannels[0].Label != "Stereo mix" ||
                model.PreviewChannels[1].Label != "FL · front left" ||
                !model.SelectedStreamNote.StartsWith("Save the original 6ch layout", StringComparison.Ordinal) ||
                !VolumeCurve.Description(50, true).StartsWith("Effective gain", StringComparison.Ordinal) ||
                LanguageService.T("このディスクはコピー保護されているため処理できません。") !=
                    "This disc is copy-protected and cannot be processed." ||
                LanguageService.T("曲名は 2 行必要です。現在は 1 行です。") !=
                    "Expected 2 track-title lines; found 1.")
                throw new InvalidDataException("English playback labels were not translated.");
            var selectedChannel = model.SelectedPreviewChannel;
            var playlist = new PlaylistInfo
            {
                Id = 1, Clips = [], ChapterStarts = [0], DurationTicks = 45000
            };
            if (playlist.ChapterCountLabel != "1 chapter")
                throw new InvalidDataException("English chapter label did not load.");
            var editor = new BulkEditWindow("Album", "Title", [], [], [], []);
            if (editor.Title != "Edit tracks and chapter names" ||
                !editor.TargetText.Text.StartsWith("Edit all", StringComparison.Ordinal))
                throw new InvalidDataException("Bulk editor did not open in English.");
            editor.Close();
            LanguageService.Instance.Apply(true);
            root.UpdateLayout();
            if (main.ThemeToggle.Content?.ToString() != "ダークモード" ||
                main.TracksGrid.Columns[2].Header?.ToString() != "曲名（ダブルクリックで再生）" ||
                model.PlayPauseText != "再生" || model.Status != "光学ドライブを確認しています" ||
                model.PreviewChannels[0].Label != "ステレオミックス" ||
                !ReferenceEquals(selectedChannel, model.SelectedPreviewChannel) ||
                playlist.ChapterCountLabel != "1チャプター" ||
                !model.SelectedStreamNote.StartsWith("6ch の配置", StringComparison.Ordinal))
                throw new InvalidDataException("Japanese switching did not update the UI.");
            LanguageService.Instance.Apply(false);
            root.UpdateLayout();
            if (main.ThemeToggle.Content?.ToString() != "Dark mode" ||
                main.TracksGrid.Columns[2].Header?.ToString() != "Track title (double-click to play)")
                throw new InvalidDataException("English switching did not restore the UI.");
            main.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
    Console.WriteLine("English, Japanese and dark-mode defaults verified");
    return 0;
}

if (args is ["meter-test"])
{
    var frames = new List<(long Start, double Seconds, double[] Peaks, double[] Rms)>();
    var parser = new ChannelLevelLogParser(6, 45000,
        (start, seconds, peaks, rms) => frames.Add((start, seconds, peaks, rms)));
    var info = new ProcessStartInfo(new ToolPaths().Ffmpeg)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
    };
    foreach (var argument in new[]
    {
        "-hide_banner", "-nostdin", "-v", "info", "-f", "lavfi", "-i",
        "aevalsrc=sin(440*2*PI*t)|0|0|0|0|0:s=48000:d=0.3:c=5.1",
        "-filter_complex",
        "[0:0]asplit=2[levels_in][play_in];[levels_in]aresample=48000,atrim=end_sample=14400,asetpts=PTS-STARTPTS,asetnsamples=n=4800:p=0,astats=metadata=1:reset=1:measure_perchannel=Peak_level+RMS_level:measure_overall=none,ametadata=mode=print,anullsink;[play_in]anull[audio_out]",
        "-map", "[audio_out]", "-f", "null", "NUL"
    }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new IOException("FFmpeg did not start.");
    string? line;
    while ((line = await process.StandardError.ReadLineAsync()) is not null) parser.Consume(line);
    parser.Flush();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0 || frames.Count < 3 ||
        frames.Any(frame => frame.Start != 45000 || frame.Peaks.Length != 6 ||
            frame.Peaks[0] < -1 || !double.IsFinite(frame.Rms[0]) ||
            frame.Peaks.Skip(1).Any(double.IsFinite)) ||
        Math.Abs(frames[1].Seconds - frames[0].Seconds - 0.1) > 0.001)
        throw new InvalidDataException("Per-channel peak/RMS metering did not match the 5.1 test signal.");
    Console.WriteLine($"Meter verified: {frames.Count} frames, first channel active, five channels silent");
    return 0;
}

if (args is ["tools"])
{
    var embeddedPaths = new ToolPaths();
    Console.WriteLine($"FFmpeg: {embeddedPaths.Ffmpeg}");
    Console.WriteLine($"FFprobe: {embeddedPaths.Ffprobe}");
    Console.WriteLine($"FFplay: {embeddedPaths.Ffplay}");
    return 0;
}

if (args is ["volume-test"])
{
    var input = new byte[] { 0x10, 0x27, 0xf0, 0xd8, 0xff, 0x7f, 0x00, 0x80 };
    using var source = new MemoryStream(input);
    using var volumeOutput = new MemoryStream();
    await AudioNavigationService.CopyPcmWithVolumeAsync(source, volumeOutput, () => 2, CancellationToken.None);
    var samples = volumeOutput.ToArray();
    var expected = new byte[] { 0x20, 0x4e, 0xe0, 0xb1, 0xff, 0x7f, 0x00, 0x80 };
    if (!samples.SequenceEqual(expected)) throw new InvalidDataException("200% gain or clipping is incorrect.");
    Console.WriteLine("200% volume and 16-bit clipping verified");
    return 0;
}

if (args is ["volume-curve-test"])
{
    foreach (var (position, perceivedGain, linearGain) in new[]
    {
        (0d, 0d, 0d), (25d, 0.0625d, 0.25d), (50d, 0.25d, 0.5d),
        (100d, 1d, 1d), (150d, 1.5d, 1.5d), (200d, 2d, 2d)
    })
        if (Math.Abs(VolumeCurve.Gain(position, true) - perceivedGain) > 1e-10 ||
            Math.Abs(VolumeCurve.Gain(position, false) - linearGain) > 1e-10)
            throw new InvalidDataException($"Volume curve failed at {position}%.");

    var samples = new byte[48000 * 2 * sizeof(short) / 50];
    for (var i = 0; i < samples.Length; i += 2) { samples[i] = 0xe8; samples[i + 1] = 0x03; }
    using var source = new MemoryStream(samples);
    using var rampOutput = new MemoryStream();
    var calls = 0;
    await AudioNavigationService.CopyPcmWithVolumeAsync(source, rampOutput,
        () => ++calls == 1 ? 0 : 2, CancellationToken.None);
    var result = rampOutput.ToArray();
    short Sample(int frame, int channel) => BitConverter.ToInt16(result, (frame * 2 + channel) * sizeof(short));
    if (Sample(0, 0) <= 0 || Sample(0, 0) >= 100 || Sample(0, 0) != Sample(0, 1) ||
        Sample(479, 0) != 2000 || Sample(479, 1) != 2000 || Sample(500, 0) != 2000)
        throw new InvalidDataException("The 10 ms volume ramp or stereo balance is incorrect.");
    Console.WriteLine("Perceived/linear curves, unity/200% anchors, and 10 ms stereo ramp verified");
    return 0;
}

if (args is ["volume-response-test"])
{
    const int bytesPerSecond = 48000 * 2 * sizeof(short);
    var input = new byte[bytesPerSecond / 2];
    for (var i = 0; i < input.Length; i += 2) { input[i] = 0xe8; input[i + 1] = 0x03; }
    using var source = new MemoryStream(input);
    using var responseOutput = new MemoryStream();
    var gainPercent = 100;
    var switchTask = Task.Run(async () =>
    {
        await Task.Delay(200);
        Volatile.Write(ref gainPercent, 0);
    });
    await AudioNavigationService.CopyPcmWithVolumeAsync(source, responseOutput,
        () => Volatile.Read(ref gainPercent) / 100d, CancellationToken.None);
    await switchTask;
    var result = responseOutput.ToArray();
    var mutedAt = -1;
    for (var i = 0; i < result.Length; i += 2)
        if (result[i] == 0 && result[i + 1] == 0) { mutedAt = i; break; }
    var responseSeconds = mutedAt / (double)bytesPerSecond;
    if (responseSeconds < 0.18 || responseSeconds > 0.30)
        throw new InvalidDataException($"Volume response was too slow or early: {responseSeconds:0.000}s");
    Console.WriteLine($"Volume change at 0.20s affected audio at {responseSeconds:0.000}s");
    return 0;
}

if (args is ["mix-layouts"])
{
    var layouts = new (string Name, int Channels)[]
    {
        ("2.1", 3), ("3.0", 3), ("3.0(back)", 3), ("3.1", 4),
        ("4.0", 4), ("quad", 4), ("quad(side)", 4),
        ("5.0", 5), ("5.0(side)", 5), ("5.1", 6), ("5.1(side)", 6),
        ("6.0", 6), ("6.0(front)", 6), ("6.1", 7), ("6.1(back)", 7),
        ("7.0", 7), ("7.1", 8), ("7.1(wide)", 8), ("7.1(wide-side)", 8),
        ("3.1.2", 6), ("5.1.2", 8), ("5.1.2(back)", 8)
    };
    var ffmpeg = new ToolPaths().Ffmpeg;
    var mixRunner = new ProcessRunner(new AppLog());
    foreach (var (layout, channelCount) in layouts)
    {
        var stream = new AudioStreamInfo
        {
            Index = 0, Codec = "pcm_s16le", SampleRate = 48000,
            Channels = channelCount, ChannelLayout = layout, BitDepth = 16
        };
        if (!StereoMixSettings.Supports(stream)) throw new InvalidDataException($"Unsupported: {layout}");
        foreach (var settings in new[] { StereoMixSettings.Default, new StereoMixSettings(0.4, 0.6, 0.2) })
            await mixRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error",
                "-f", "lavfi", "-i", $"anullsrc=r=48000:cl={layout}",
                "-af", settings.PanFilter(stream), "-t", "0.05",
                "-ac", "2", "-f", "null", "NUL"], CancellationToken.None);
        foreach (var channel in StereoMixSettings.ChannelNames(stream))
            await mixRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error",
                "-f", "lavfi", "-i", $"anullsrc=r=48000:cl={layout}",
                "-af", StereoMixSettings.SoloFilter(stream, channel, stereo: false),
                "-t", "0.05", "-ac", "1", "-f", "null", "NUL"], CancellationToken.None);
        Console.WriteLine($"Mixed: {layout}");
    }
    return 0;
}

if (args.Length < 2 || args[0] is not ("scan" or "ui-scan" or "ui-channel" or "preview" or "preview-video" or "preview-solo" or "preview-video-solo" or "silence" or "convert" or "convert-video" or "convert-stereo" or "convert-video-stereo" or "convert-individual" or "convert-video-individual" or "convert-bd-stereo" or "convert-bd-individual" or "preview-bd" or "preview-bd-solo"))
{
    Console.Error.WriteLine("Usage: BDDVD2Flac.Verify scan|ui-scan|preview|preview-video|preview-bd|convert|convert-video|convert-stereo|convert-video-stereo|convert-bd-stereo <disc root> [output folder] [full] [chapter] [offset seconds]");
    return 2;
}

var root = args[1];
if (args[0] == "ui-channel")
{
    var model = new MainViewModel();
    await model.OpenSourceAsync(root);
    model.SelectedStream = model.Streams.First(StereoMixSettings.Supports);
    if (model.PreviewChannels.Count != model.SelectedStream.Channels + 1)
        throw new InvalidDataException("試聴チャンネル一覧が不正です。");
    model.SelectedPreviewChannel = model.PreviewChannels.First(x => x.Code == "FC");
    model.Volume = 200;
    model.SaveIndividualChannels = true;
    if (!model.SaveIndividualChannels || model.SaveStereoDownmix || model.Volume != 200)
        throw new InvalidDataException("チャンネル別保存または音量の画面設定が不正です。");
    model.SaveStereoDownmix = true;
    if (!model.SaveStereoDownmix || model.SaveIndividualChannels)
        throw new InvalidDataException("出力形式の切り替えが不正です。");
    Console.WriteLine($"UI channels={model.PreviewChannels.Count - 1}, solo={model.SelectedPreviewChannel.Code}, volume={model.Volume:0}%, stereo={model.SaveStereoDownmix}");
    return 0;
}
if (args[0] == "ui-scan")
{
    var model = new MainViewModel();
    await model.OpenSourceAsync(root);
    Console.WriteLine($"UI: {model.AlbumTitle}, {model.Playlists.Count} titles, {model.Tracks.Count} tracks, {model.Streams.Count} streams, canSave={model.CanConvert}, status={model.Status}");
    if (model.Playlists.Count == 0 || model.Tracks.Count == 0 || model.Streams.Count == 0)
        throw new InvalidDataException("画面用モデルに曲を表示できませんでした。");
    foreach (var title in model.Playlists.Skip(1))
    {
        await model.ChoosePlaylistAsync(title);
        Console.WriteLine($"  {title.TitleLabel}: {model.Tracks.Count} tracks / {model.Streams.Count} streams");
        if (model.Tracks.Count == 0 || model.Streams.Count == 0)
            throw new InvalidDataException($"{title.TitleLabel} を選択できませんでした。");
    }
    return 0;
}
var log = new AppLog();
var paths = new ToolPaths();
var runner = new ProcessRunner(log);
var probe = new FfprobeService(paths, runner);
var reader = new DiscService(probe, log);
var disc = reader.Analyze(root);
Console.WriteLine($"Disc: {disc.AlbumTitle}, {disc.Playlists.Count} titles");
if (args[0] is "convert-bd-stereo" or "convert-bd-individual" or "preview-bd" or "preview-bd-solo")
{
    PlaylistInfo? selectedPlaylist = null;
    AudioStreamInfo? selectedStream = null;
    foreach (var candidate in disc.Playlists.Where(x => x.Format == DiscFormat.BluRay))
    {
        try
        {
            var (candidateStreams, _) = await reader.AnalyzePlaylistAsync(disc, candidate, CancellationToken.None);
            selectedStream = candidateStreams.FirstOrDefault(StereoMixSettings.Supports);
            if (selectedStream is null) continue;
            selectedPlaylist = candidate;
            break;
        }
        catch (InvalidDataException) { }
    }
    if (selectedPlaylist is null || selectedStream is null)
        throw new InvalidDataException("多チャンネルの BD 音声が見つかりません。");
    var first = selectedPlaylist.ChapterStarts[0];
    if (args[0].StartsWith("preview-bd", StringComparison.Ordinal))
    {
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        var navigation = new AudioNavigationService(paths, runner, probe, log);
        var playbackTimer = Stopwatch.StartNew();
        double? decoderStartedAfter = null;
        await navigation.PlayAsync(disc, selectedPlaylist, selectedStream, first,
            first + 2 * 45000, CancellationToken.None,
            segmentStarted: _ => decoderStartedAfter ??= playbackTimer.Elapsed.TotalSeconds,
            volume: () => 1.5,
            soloChannel: args[0].EndsWith("-solo", StringComparison.Ordinal) ? "FC" : null);
        Console.WriteLine($"BD preview completed: {selectedPlaylist.DisplayName} / {selectedStream.DisplayName}; " +
            $"decoder started at {decoderStartedAfter:0.00}s, completed in {playbackTimer.Elapsed.TotalSeconds:0.00}s");
        return 0;
    }
    if (args.Length < 3) return 2;
    var excerpt = new TrackRow
    {
        Number = 1, StartTicks = first, EndTicks = first + 2 * 45000,
        IsChapter = false, IsSelected = true, Title = "BD stereo verification"
    };
    var bdConverter = new ConversionService(paths, runner, probe, log);
    var saved = await bdConverter.ConvertAsync(disc, selectedPlaylist, selectedStream,
        [excerpt], OutputQuality.Cd, args[2], new Progress<ConversionProgress>(p => Console.WriteLine(p.Message)),
        CancellationToken.None, saveStereoDownmix: args[0] == "convert-bd-stereo",
        saveIndividualChannels: args[0] == "convert-bd-individual");
    Console.WriteLine($"Saved: {saved}");
    return 0;
}
foreach (var title in disc.Playlists)
{
    Console.WriteLine($"  {title.DisplayName}");
    var (streams, tracks) = await reader.AnalyzePlaylistAsync(disc, title, CancellationToken.None);
    foreach (var stream in streams) Console.WriteLine($"    {stream.DisplayName}");
    Console.WriteLine($"    {tracks.Count} tracks");
}
if (args[0] == "scan") return 0;
if (args[0] == "silence")
{
    var target = disc.Playlists.First(x => x.Format == DiscFormat.DvdAudio);
    var (sources, _) = await reader.AnalyzePlaylistAsync(disc, target, CancellationToken.None);
    var navigation = new AudioNavigationService(paths, runner, probe, log);
    var track = new TrackRow { Number = 1, StartTicks = 30 * 45000 + 4500,
        EndTicks = 35 * 45000 + 4500, IsChapter = false, Title = "silence test" };
    var candidates = await navigation.FindSilenceAsync(disc, target, sources[0], track, CancellationToken.None);
    Console.WriteLine($"Silence analysis completed: {candidates.Count} candidates");
    return 0;
}
if (args[0] is "preview" or "preview-video" or "preview-solo" or "preview-video-solo")
{
    Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
    var target = disc.Playlists.First(x => x.Format ==
        (args[0].StartsWith("preview-video", StringComparison.Ordinal) ? DiscFormat.DvdVideo : DiscFormat.DvdAudio));
    var (sources, _) = await reader.AnalyzePlaylistAsync(disc, target, CancellationToken.None);
    var start = args[0].StartsWith("preview-video", StringComparison.Ordinal) ? 0 : 30 * 45000L + 4500;
    var navigation = new AudioNavigationService(paths, runner, probe, log);
    await navigation.PlayAsync(disc, target, sources[0], start, start + 2 * 45000,
        CancellationToken.None, volume: () => 1.5,
        soloChannel: args[0].EndsWith("-solo", StringComparison.Ordinal) ? "FC" : null);
    Console.WriteLine("Preview completed (dummy audio driver)");
    return 0;
}
if (args.Length < 3) return 2;
var playlist = disc.Playlists.First(x => x.Format == (args[0].StartsWith("convert-video", StringComparison.Ordinal) ? DiscFormat.DvdVideo : DiscFormat.DvdAudio));
var (audioStreams, _) = await reader.AnalyzePlaylistAsync(disc, playlist, CancellationToken.None);
var chapterIndex = args.Length >= 5 && int.TryParse(args[4], out var chapterNumber)
    ? Math.Clamp(chapterNumber, 1, playlist.ChapterStarts.Count) - 1 : 0;
var offsetSeconds = args.Length >= 6 && double.TryParse(args[5], System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var offset) ? offset : 0;
var startTicks = playlist.ChapterStarts[chapterIndex] +
    (long)Math.Round(offsetSeconds * 45000, MidpointRounding.AwayFromZero);
var selected = new TrackRow
{
    Number = chapterIndex + 1, StartTicks = startTicks,
    EndTicks = args.Length >= 4 && args[3] == "full"
        ? chapterIndex + 1 < playlist.ChapterStarts.Count ? playlist.ChapterStarts[chapterIndex + 1] : playlist.DurationTicks
        : Math.Min(playlist.DurationTicks, startTicks + 2 * 45000),
    IsChapter = false, IsSelected = true, Title = "Verification excerpt"
};
var converter = new ConversionService(paths, runner, probe, log);
var output = await converter.ConvertAsync(disc, playlist, audioStreams[0], [selected],
    audioStreams[0].CanMakeHighResolution ? OutputQuality.HighResolution : OutputQuality.Cd,
    args[2], new Progress<ConversionProgress>(p => Console.WriteLine(p.Message)), CancellationToken.None,
    saveStereoDownmix: args[0].EndsWith("-stereo", StringComparison.Ordinal),
    saveIndividualChannels: args[0].EndsWith("-individual", StringComparison.Ordinal));
Console.WriteLine($"Saved: {output}");
return 0;
