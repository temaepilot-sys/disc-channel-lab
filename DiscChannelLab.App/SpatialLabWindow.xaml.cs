using Microsoft.Win32;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Disc2Flac;

public partial class SpatialLabWindow : Window
{
    readonly MainViewModel model;
    readonly SofaLibrary library = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly ToolPaths tools = new();
    readonly Dictionary<string, TranslateTransform3D> scenePoints = [];
    readonly string settingsPath = AppDataPaths.Current.PreferencePath("profiles/spatial-lab.json");
    CancellationTokenSource? operation, hrtfSelection, roomSelection;
    PreviewPlaybackControl? transport;
    ComparisonAudio? comparison;
    SpatialInput? displayInput;
    SpatialScene? displayScene;
    AdmWave? adm;
    bool ready, closed, libraryUpdating, roomUpdating;
    public SpatialLabWindow(MainViewModel owner)
    {
        model = owner; DataContext = owner; InitializeComponent(); InitializeSceneInteraction(); RefreshLibrary(); InitializeBrir();
        var current = HeadphoneSession.Current.Options;
        ReadRoom(current.Room with { Mode = current.Enabled && current.Method == HeadphoneMethod.SteamAudio ? current.Room.Mode : "Pure HRTF" });
        try
        {
            if (File.Exists(settingsPath))
            {
                var saved = JsonSerializer.Deserialize<LabPreferences>(File.ReadAllText(settingsPath), SpatialJson.Options);
                if (saved is not null)
                {
                    FixedYaw.Text = saved.Yaw.ToString(CultureInfo.InvariantCulture); FixedPitch.Text = saved.Pitch.ToString(CultureInfo.InvariantCulture);
                    FixedRoll.Text = saved.Roll.ToString(CultureInfo.InvariantCulture);
                    HrtfA.SelectedItem = library.Entries.FirstOrDefault(x => x.Id == saved.A) ?? library.Entries[0];
                    HrtfB.SelectedItem = library.Entries.FirstOrDefault(x => x.Id == saved.B) ?? library.Entries[0];
                    ShowSceneLabels.IsChecked = saved.ShowSourceLabels;
                }
            }
        }
        catch (Exception ex) { StatusText.Text = "Saved comparison settings were not applied: " + ex.Message; }
        ready = true;
        if (current.Enabled && current.Method == HeadphoneMethod.MeasuredBrir && brirRows.Count > 0) CompareBrir.IsChecked = true;
        UpdateSource(); UpdateActiveHrtf(); FollowMainSource(); UpdateLabAvailability(); UpdateComparisonPresentation();
        model.PropertyChanged += Model_Changed; HeadphoneSession.Current.PropertyChanged += Headphone_Changed;
        timer.Tick += (_, _) => Tick(); timer.Start();
        Closed += (_, _) => CloseLab();
    }
    record LabPreferences(SpatialRoomOptions Room, double Yaw, double Pitch, double Roll, string A, string B, bool ShowSourceLabels = true);
    static double Value(TextBox box, double min, double max)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < min || n > max)
            throw new InvalidDataException($"{box.Name}: enter a value from {min} to {max}.");
        return n;
    }
    HrtfEntry A => (HrtfEntry?)HrtfA.SelectedItem ?? HrtfEntry.BuiltIn;
    HrtfEntry B => (HrtfEntry?)HrtfB.SelectedItem ?? HrtfEntry.BuiltIn;
    HeadPose Pose() => new(Value(FixedYaw, -360, 360), Value(FixedPitch, -89, 89), Value(FixedRoll, -180, 180));
    SpatialRoomOptions Room()
    {
        var result = new SpatialRoomOptions { Mode = ((ComboBoxItem)RoomMode.SelectedItem).Content.ToString()!, Width = Value(RoomWidth, 2, 30), Depth = Value(RoomDepth, 2, 30),
            Height = Value(RoomHeight, 2, 15), ListenerHeight = Value(ListenerHeight, .2, 14), SpeakerRadius = Value(SpeakerRadius, .2, 10), ReflectionDb = Value(ReflectionDb, -60, 0),
            LowAbsorption = Value(LowAbsorption, 0, 1), MidAbsorption = Value(MidAbsorption, 0, 1), HighAbsorption = Value(HighAbsorption, 0, 1), SoloReflections = SoloReflections.IsChecked == true };
        result.Validate(); return result;
    }
    void ReadRoom(SpatialRoomOptions r)
    {
        bool previous = roomUpdating; roomUpdating = true;
        try
        {
            RoomMode.SelectedItem = RoomMode.Items.OfType<ComboBoxItem>().First(i => Equals(i.Content, r.Mode));
            RoomWidth.Text = r.Width.ToString(CultureInfo.InvariantCulture); RoomDepth.Text = r.Depth.ToString(CultureInfo.InvariantCulture); RoomHeight.Text = r.Height.ToString(CultureInfo.InvariantCulture);
            ListenerHeight.Text = r.ListenerHeight.ToString(CultureInfo.InvariantCulture); SpeakerRadius.Text = r.SpeakerRadius.ToString(CultureInfo.InvariantCulture);
            ReflectionDb.Text = r.ReflectionDb.ToString(CultureInfo.InvariantCulture); ReflectionLevel.Value = r.ReflectionDb;
            LowAbsorption.Text = r.LowAbsorption.ToString(CultureInfo.InvariantCulture); MidAbsorption.Text = r.MidAbsorption.ToString(CultureInfo.InvariantCulture);
            HighAbsorption.Text = r.HighAbsorption.ToString(CultureInfo.InvariantCulture); SoloReflections.IsChecked = r.SoloReflections;
        }
        finally { roomUpdating = previous; }
    }
    void RefreshLibrary()
    {
        string? a = (HrtfA.SelectedItem as HrtfEntry)?.Id, b = (HrtfB.SelectedItem as HrtfEntry)?.Id, selected = (LibraryGrid.SelectedItem as HrtfEntry)?.Id;
        libraryUpdating = true;
        try
        {
            LibraryGrid.ItemsSource = null; LibraryGrid.ItemsSource = library.Entries;
            HrtfA.ItemsSource = null; HrtfB.ItemsSource = null; HrtfA.ItemsSource = library.Entries; HrtfB.ItemsSource = library.Entries;
            HrtfA.SelectedItem = library.Entries.FirstOrDefault(x => x.Id == a) ?? library.Entries[0];
            HrtfB.SelectedItem = library.Entries.FirstOrDefault(x => x.Id == b) ?? library.Entries.Last();
            LibraryGrid.SelectedItem = library.Entries.FirstOrDefault(x => x.Id == selected) ?? ActiveEntry() ?? library.Entries[0];
        }
        finally { libraryUpdating = false; }
    }
    async Task Job(Func<CancellationToken, Task> action, bool pauseMainPlayback = true)
    {
        if (closed) return;
        if (operation is not null) { StatusText.Text = "Cancel the current lab task first."; return; }
        comparisonRequest++;
        await StopComparisonPlaybackAsync();
        if (closed) return;
        CancelPendingSettings();
        if (pauseMainPlayback) model.PausePlayback();
        using var stop = new CancellationTokenSource(); operation = stop; Pages.IsEnabled = false; LabCancelButton.IsEnabled = true; UpdateComparisonControls();
        using var mainInteraction = LockMainInteraction();
        try { await action(stop.Token); }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "Lab task cancelled. Main playback position is preserved."; }
        catch (Exception ex) { if (!closed) StatusText.Text = ex.Message; }
        finally
        {
            transport?.Dispose(); transport = null; operation = null;
            if (!closed) { LabCancelButton.IsEnabled = false; FollowMainSource(); UpdateLabAvailability(); UpdateComparisonControls(); }
        }
    }
    async void AddSofa_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SOFA HRTF|*.sofa", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await Job(async ct => { foreach (var file in dialog.FileNames) { StatusText.Text = "Inspecting " + file; await library.RegisterAsync(file, ct); } RefreshLibrary(); StatusText.Text = "SOFA files registered. Select a compatible row to listen."; });
    }
    async void Revalidate_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryGrid.SelectedItem is not HrtfEntry { Id: not "builtin" } entry) return;
        await Job(async ct => { await library.RegisterAsync(entry.Path, ct); RefreshLibrary(); StatusText.Text = "Revalidation completed."; });
    }
    void Remove_Click(object sender, RoutedEventArgs e) { if (LibraryGrid.SelectedItem is HrtfEntry entry) { library.Remove(entry); RefreshLibrary(); } }
    async void Library_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (closed) return;
        if (LibraryGrid.SelectedItem is HrtfEntry h && LibraryDetails is not null)
            LibraryDetails.Text = $"{h.Name}\n{h.Path}\nSHA-256: {h.Hash}\nSubject: {h.Subject}\nSource positions: {h.SourcePositionType} · azimuth {h.MinimumAzimuth:0.#}…{h.MaximumAzimuth:0.#}° · elevation {h.MinimumElevation:0.#}…{h.MaximumElevation:0.#}°\nLicense / source conditions: {h.License}\n{h.Status}: {h.Diagnostic}";
        if (ready && !libraryUpdating && operation is null) await ApplySelectedHrtfAsync();
    }
    async Task ApplySelectedHrtfAsync()
    {
        if (closed) return;
        if (LibraryGrid.SelectedItem is not HrtfEntry h) return;
        CancelRequest(hrtfSelection);
        using var request = new CancellationTokenSource(); hrtfSelection = request;
        try
        {
            if (operation is not null) throw new InvalidOperationException("Cancel the current lab task first.");
            if (h.Status != "Compatible") throw new InvalidDataException("This HRTF cannot be applied: " + h.Diagnostic);
            StatusText.Text = "Applying HRTF: " + h.Name + "…";
            await Task.Delay(100, request.Token); await model.ApplySpatialHrtfAsync(h, request.Token);
            if (closed || request.IsCancellationRequested) return;
            HrtfA.SelectedItem = h; UpdateActiveHrtf();
            StatusText.Text = "Main player HRTF: " + h.Name + (model.IsPlaying ? " · resumed at the held position." : " · use Play in the main window.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed && !request.IsCancellationRequested) StatusText.Text = ex.Message; }
        finally { if (ReferenceEquals(hrtfSelection, request)) hrtfSelection = null; }
    }
    HrtfEntry? ActiveEntry()
    {
        var options = HeadphoneSession.Current.Options;
        return options.Hrtf == HeadphoneHrtf.BuiltIn ? library.Entries[0] : library.Entries.FirstOrDefault(x =>
            x.Id != "builtin" && string.Equals(x.Path, options.SofaPath, StringComparison.OrdinalIgnoreCase));
    }
    void UpdateActiveHrtf()
    {
        var options = HeadphoneSession.Current.Options;
        if (model.IsAtmosStreamingEnabled && model.SelectedAtmosPresentation == AtmosPresentation.Stereo)
        {
            ActiveHrtfText.Text = "Main player: stereo presentation · HRTF bypassed";
            ActiveRoomText.Text = "Main reflections: OFF · stereo presentation";
            RoomDiagnosticText.Text = "Stereo presentation bypasses added reflections. Choose Objects or 5.1 in Source presentation on tab 2, then select Studio or Listening Room.";
            return;
        }
        if (options.Enabled && options.Method == HeadphoneMethod.MeasuredBrir)
        {
            ActiveHrtfText.Text = "Main player: Measured BRIR · " + BrirLibrary.Name(options) + " · fixed head";
            ActiveRoomText.Text = "Measured room response only · HRIR and added reflections bypassed";
            RoomDiagnosticText.Text = "The measured BRIR already includes its room response; the added first-order reflection model is bypassed."; return;
        }
        ActiveHrtfText.Text = !options.Enabled ? "Main player: Stereo · HRTF off" : options.Method == HeadphoneMethod.Direct
            ? "Main player: Direct · HRTF off" : "Main player HRTF: " + (ActiveEntry()?.Name ?? Path.GetFileNameWithoutExtension(options.SofaPath));
        bool on = options.Enabled && options.Method == HeadphoneMethod.SteamAudio && options.Room.Enabled;
        ActiveRoomText.Text = on ? $"Main reflections: ON · {options.Room.Mode} · {options.Room.ReflectionDb:0.#} dB per path" +
            (options.Room.SoloReflections ? " · REFLECTIONS ONLY (direct sound and LFE muted)" : " · direct + reflected sound")
            : "Main reflections: OFF · " + (!options.Enabled ? "Stereo" : options.Method != HeadphoneMethod.SteamAudio ? "Direct mode" : "Pure HRTF");
        if (!on) RoomDiagnosticText.Text = "No added room reflections. Choose Studio or Listening Room to hear them.";
        else UpdateRoomDiagnostics();
    }
    void Headphone_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || e.PropertyName != nameof(HeadphoneSession.Options)) return;
        void Refresh()
        {
            if (closed) return;
            UpdateActiveHrtf();
            if (roomSelection is null && operation is null)
            {
                var options = HeadphoneSession.Current.Options;
                ReadRoom(options.Room with { Mode = options.Enabled && options.Method == HeadphoneMethod.SteamAudio ? options.Room.Mode : "Pure HRTF" });
            }
        }
        if (Dispatcher.CheckAccess()) Refresh(); else Dispatcher.BeginInvoke(Refresh);
    }
    async void RoomMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (closed || !ready || roomUpdating) return;
        if (RoomMode.SelectedIndex > 0) ReadRoom(SpatialRoomOptions.Preset(((ComboBoxItem)RoomMode.SelectedItem).Content.ToString()!));
        else { roomUpdating = true; SoloReflections.IsChecked = false; roomUpdating = false; }
        await ApplyRoomEditsAsync();
    }
    async void RoomField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready || roomUpdating) return;
        if (ReferenceEquals(sender, ReflectionDb) && double.TryParse(ReflectionDb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && db is >= -60 and <= 0)
        { roomUpdating = true; ReflectionLevel.Value = db; roomUpdating = false; }
        await ApplyRoomEditsAsync();
    }
    async void RoomCheck_Changed(object sender, RoutedEventArgs e) { if (ready && !roomUpdating) await ApplyRoomEditsAsync(); }
    void ReflectionLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready && !roomUpdating) ReflectionDb.Text = e.NewValue.ToString("0.0", CultureInfo.InvariantCulture);
    }
    async Task ApplyRoomEditsAsync()
    {
        if (closed) return;
        CancelRequest(roomSelection); using var request = new CancellationTokenSource(); roomSelection = request;
        try
        {
            if (operation is not null) throw new InvalidOperationException("Cancel the current lab task first.");
            StatusText.Text = "Applying reflection settings…";
            await Task.Delay(350, request.Token); var room = Room();
            await model.ApplySpatialRoomAsync(room, request.Token);
            if (closed || request.IsCancellationRequested) return;
            SaveSettings(); UpdateActiveHrtf(); UpdateRoomDiagnostics();
            StatusText.Text = room.Enabled ? "Reflections applied to main playback. Use Reflections only or Prepare OFF / ON A/B to compare." : "Pure HRTF applied: no added reflections.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed && !request.IsCancellationRequested) StatusText.Text = "Not applied: " + ex.Message; }
        finally { if (ReferenceEquals(roomSelection, request)) roomSelection = null; }
    }
    async void ResetRoom_Click(object sender, RoutedEventArgs e) { ReadRoom(new()); await ApplyRoomEditsAsync(); }
    async void RoomCompare_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Room().Enabled) ReadRoom(new() { Mode = "Studio" });
            HrtfA.SelectedItem = ActiveEntry() ?? library.Entries[0];
            var pose = HeadphoneSession.Current.Pose;
            FixedYaw.Text = pose.Yaw.ToString(CultureInfo.InvariantCulture); FixedPitch.Text = pose.Pitch.ToString(CultureInfo.InvariantCulture); FixedRoll.Text = pose.Roll.ToString(CultureInfo.InvariantCulture);
            IntervalStart.Text = Math.Min(model.PreviewSeconds, Math.Max(0, model.PreviewMax - 5)).ToString("0.000", CultureInfo.InvariantCulture);
            CompareRoom.IsChecked = true; Pages.SelectedItem = ComparisonTab; await PrepareComparisonAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    void UpdateRoomDiagnostics()
    {
        var options = HeadphoneSession.Current.Options; var room = options.Room;
        if (!options.Enabled || options.Method != HeadphoneMethod.SteamAudio || !room.Enabled) return;
        try
        {
            var scene = model.CurrentAdmScene ?? (model.SelectedStream is { } stream ? SpatialScene.ChannelBed(HeadphoneGeometry.Channels(stream), 256, options.FrontAngle) : null);
            if (scene is null) { RoomDiagnosticText.Text = "Open audio in the main window to inspect reflection paths."; return; }
            long time = model.CurrentAdm is null ? 0 : (long)Math.Round(model.CurrentAdmSeconds * 48000);
            var paths = scene.Sources.Where(s => !s.IsLfe).Select(s => s.At(time).Position).SelectMany(p => Enumerable.Range(0, 6).Select(w => EarlyReflections.Path(p * (float)room.SpeakerRadius, room, w))).ToArray();
            if (paths.Length == 0) return;
            RoomDiagnosticText.Text = $"Applied paths: {paths.Length} · excess delay {paths.Min(p => p.DelaySamples) / 48:0.0}–{paths.Max(p => p.DelaySamples) / 48:0.0} ms · " +
                $"per-path level {20 * Math.Log10(paths.Min(p => p.RelativeGain)):0.0}…{20 * Math.Log10(paths.Max(p => p.RelativeGain)):0.0} dB relative to direct, before absorption.";
        }
        catch (InvalidDataException ex) { RoomDiagnosticText.Text = ex.Message; }
    }
    SpatialRenderSnapshot Snapshot(HrtfEntry entry) => model.SpatialSnapshot(entry, Pose(), Room());
    async Task<SpatialInput> Prepare(CancellationToken ct, bool whole = false)
    {
        double offset = whole ? 0 : Value(IntervalStart, 0, 24 * 3600), seconds = whole ? model.PreviewMax : Value(IntervalLength, 5, 60);
        StatusText.Text = "Preparing selected main-player track…";
        var source = await model.PrepareSpatialInputAsync(offset, seconds, ct); displayInput = source; SetScene(source.Scene); return source;
    }
    void SaveEvaluation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (comparison is not { } c) throw new InvalidOperationException("Prepare A/B before saving an evaluation.");
            string path = AppDataPaths.Current.PreferencePath("profiles/listening-tests.json");
            var records = File.Exists(path) ? JsonSerializer.Deserialize<List<ListeningRecord>>(File.ReadAllText(path), SpatialJson.Options) ?? [] : [];
            records.Add(new(DateTimeOffset.Now, c.A.Input.SourceName + " · PCM SHA-256 " + c.A.Input.PcmHash, c.A.Input.FirstSample, c.A.SourceFrames, c.A.Snapshot.HrtfHash, c.B.Snapshot.HrtfHash,
                c.MetricsA.IntegratedLufs ?? -200, c.MetricsB.IntegratedLufs ?? -200, c.GainADb, c.GainBDb, ((ComboBoxItem)Verdict.SelectedItem).Content.ToString()!,
                EvaluationNotes.Text + "\nEquipment: " + EquipmentNotes.Text, c.A.Snapshot, c.B.Snapshot));
            SpatialJson.Save(path, records); StatusText.Text = "Evaluation saved: " + path;
            SaveCompletion.Show(this, "評価記録を保存しました。", path);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    async void ExportWav_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Binaural float WAV|*.wav", FileName = "binaural-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (dialog.ShowDialog(this) != true) return;
        await Job(async ct =>
        {
            var snapshot = Snapshot(A);
            if (ExportBrir.IsChecked == true) snapshot = BrirSnapshot(await PrepareEditedBrir(ct), snapshot);
            var input = await Prepare(ct, ExportRange.SelectedIndex > 0);
            var audio = await SpatialRendering.RenderAsync(input, snapshot, KeepTail.IsChecked == true, ct);
            await SpatialRendering.ExportAsync(audio, dialog.FileName, tools.Ffmpeg, false, ct);
            StatusText.Text = $"Saved {dialog.FileName}\nPeak {20 * Math.Log10(Math.Max(audio.Peak, 1e-20)):0.00} dBFS · tail {audio.TailFrames / 48000d:0.000}s";
            SaveCompletion.Show(this, "バイノーラルWAVの書き出しが完了しました。", dialog.FileName);
        });
    }
    void SaveSettings() { var p = Pose(); SpatialJson.Save(settingsPath, new LabPreferences(Room(), p.Yaw, p.Pitch, p.Roll, A.Id, B.Id, ShowSceneLabels.IsChecked == true)); }
    void Cancel_Click(object sender, RoutedEventArgs e) { operation?.Cancel(); StopComparison_Click(sender, e); }
    void Model_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => Model_Changed(sender, e))); return; }
        if (e.PropertyName is nameof(MainViewModel.SelectedTrack) or nameof(MainViewModel.SelectedStream))
        {
            CancelPendingSettings(); CancelRequest(operation); CancelRequest(comparisonStop);
            InvalidateComparison("Source changed. Press Prepare A/B for the selected track.");
        }
        if (e.PropertyName is nameof(MainViewModel.SelectedAtmosPresentation) or nameof(MainViewModel.IsAtmosStreamingEnabled)) { UpdateActiveHrtf(); UpdateComparisonPresentation(); }
        if (e.PropertyName == nameof(MainViewModel.CanChooseAtmosPresentation)) UpdateComparisonControls();
        if (e.PropertyName is nameof(MainViewModel.AdmSoloSources) or nameof(MainViewModel.AdmSoloDescription)) { RefreshSceneSolo(); return; }
        if (e.PropertyName is nameof(MainViewModel.SelectedTrackTitle) or nameof(MainViewModel.CurrentAdm) or nameof(MainViewModel.IsAdmPlaybackEnabled))
            QueueMainSourceRefresh();
        if (e.PropertyName is nameof(MainViewModel.IsAdmPlaybackEnabled) or nameof(MainViewModel.CanOpenSpatialLab))
        { UpdateLabAvailability(); QueueMainSourceRefresh(); }
    }
    void FollowMainSource()
    {
        if (closed) return;
        var source = model.IsAdmPlaybackEnabled ? model.CurrentAdm : null;
        if (source is null)
        {
            adm = null; displayScene = null; scenePoints.Clear(); SceneView.Children.Clear(); ResetSourceInteraction(null); SceneTime.Value = 0;
            ProgrammeText.Text = "No ADM in the main player"; SceneDetails.Text = "Open ADM / audio / drive in the main window. This view follows its selected programme and playback time."; return;
        }
        adm = source; ProgrammeText.Text = adm.Programmes.First(p => p.Id == adm.ProgrammeId).Name;
        if (model.CurrentAdmScene is { } scene && !ReferenceEquals(displayScene, scene)) SetScene(scene);
    }
    void UpdateSource() => SourceText.Text = model.SelectedTrackTitle;
    void Tick()
    {
        if (closed) return;
        UpdateComparisonPosition();
        if (transport is { CanPause: true } t) UpdateScene((displayInput?.FirstSample / 48000d ?? 0) + t.EstimatedPosition);
        else if (operation is null && model.IsAdmPlaybackEnabled && model.CurrentAdm is not null)
        { if (model.CurrentAdmScene is { } scene && !ReferenceEquals(displayScene, scene)) SetScene(scene); UpdateScene(model.CurrentAdmSeconds); }
        UpdateRoomDiagnostics();
    }
    void SetScene(SpatialScene scene)
    {
        displayScene = scene; ResetSourceInteraction(scene); SceneTime.Maximum = scene.DurationSamples / 48000d;
        scenePoints.Clear(); SceneView.Children.Clear(); var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(130, 130, 130))); group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -2, 1)));
        group.Children.Add(Sphere(Colors.Gray, .13, new TranslateTransform3D()));
        for (int i = -4; i <= 4; i++) { group.Children.Add(GridLine(i * .5, true)); group.Children.Add(GridLine(i * .5, false)); }
        group.Children.Add(FrontFloorGuide());
        foreach (var source in scene.Sources)
        {
            var p = new TranslateTransform3D(); scenePoints.Add(source.Id, p);
            group.Children.Add(SourceSphere(source, p));
        }
        SceneView.Children.Add(new ModelVisual3D { Content = group }); UpdateScene(0); RefreshSceneSolo();
    }
    static GeometryModel3D GridLine(double p, bool x)
    {
        var mesh = new MeshGeometry3D(); double w = .006;
        mesh.Positions = x ? new([new(p-w,-.18,-2),new(p+w,-.18,-2),new(p+w,-.18,2),new(p-w,-.18,2)])
            : new([new(-2,-.18,p-w),new(2,-.18,p-w),new(2,-.18,p+w),new(-2,-.18,p+w)]);
        mesh.TriangleIndices = new([0,1,2,0,2,3]); var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(53,77,95)));
        return new(mesh, material) { BackMaterial = material };
    }
    static GeometryModel3D Sphere(Color color, double radius, Transform3D transform)
    {
        var mesh = new MeshGeometry3D(); const int n = 12;
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n * 2; i++)
        { double a = i * Math.PI / n, b = j * Math.PI / n; mesh.Positions.Add(new(radius * Math.Sin(b) * Math.Cos(a), radius * Math.Cos(b), radius * Math.Sin(b) * Math.Sin(a))); }
        for (int j = 0; j < n; j++) for (int i = 0; i < n * 2; i++)
        { int a = j * (n * 2 + 1) + i, b = a + n * 2 + 1; foreach (int k in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(k); }
        var material = new DiffuseMaterial(new SolidColorBrush(color)); return new(mesh, material) { BackMaterial = material, Transform = transform };
    }
    void UpdateScene(double seconds)
    {
        if (displayScene is null) return; SceneTime.Value = Math.Min(SceneTime.Maximum, seconds);
        long sample = (long)Math.Round(seconds * 48000); int active = 0;
        foreach (var source in displayScene.Sources)
        {
            var state = source.At(sample); if (state.Gain != 0) active++;
            if (scenePoints.TryGetValue(source.Id, out var p))
            {
                var display = AdmSceneCoordinates.ToDisplay(state.Position);
                p.OffsetX = display.X; p.OffsetY = state.Gain == 0 ? -1000 : display.Y; p.OffsetZ = display.Z;
            }
        }
        UpdateSourceLabels();
        SceneDetails.Text = $"{seconds:0.000}s · {active}/{displayScene.Sources.Length} active · 48 kHz · use the main timeline to seek";
    }
}
