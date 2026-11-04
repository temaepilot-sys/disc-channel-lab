using Disc2Flac;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

internal static class AdmSceneInteractionTests
{
    public static void Run(string output, string trueHd)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null; SpatialLabWindow? lab = null;
            var originalFactory = AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                ThemeService.SetDark(true); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors = new List<Exception>(); app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                var checks = new List<string>(); var metrics = new List<object>();
                void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
                async Task Until(Func<bool> done, string name)
                {
                    var watch = Stopwatch.StartNew();
                    while (!done()) { if (watch.Elapsed.TotalSeconds > 35) throw new TimeoutException(name); model?.AdvancePlaybackClock(); await Task.Delay(20); }
                }
                async Task Tests()
                {
                    string demo = Path.Combine(root, "original-adm.wav"); AdmDemo.Create(demo); var originalHash = SHA256.HashData(File.ReadAllBytes(demo));
                    model = new MainViewModel(); model.ChooseSpatialInput = _ => throw new InvalidOperationException("Automatic choice dialog must not run");
                    main = new MainWindow(model) { ShowActivated = false };
                    var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, loaded);
                    main.Show(); await model.OpenAudioFilesAsync([demo]); Pump(150);
                    var enable = (Button)main.FindName("AdmPlaybackButton"); var sceneButton = (Button)main.FindName("OpenAdmSceneButton");
                    Check(enable.IsEnabled && !sceneButton.IsEnabled && !model.CanExportAdm && !model.HeadphonesEnabled && !model.IsAdmPlaybackEnabled,
                        "Native ADM detection enables its source button without a dialog, automatic playback mode or active ADM tools");
                    Save(main, Path.Combine(root, "01-adm-detected.png"));
                    enable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => model.IsAdmPlaybackEnabled, "explicit enable"); Pump(100);
                    Check(sceneButton.IsEnabled && model.CanExportAdm && model.HeadphonesEnabled && model.ContinuousPlayback,
                        "Explicit source button enables HRTF playback, ADM scene and ADM save");
                    sceneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                    lab = app.Windows.OfType<SpatialLabWindow>().Single();
                    var pages = (TabControl)lab.FindName("Pages"); var view = (Viewport3D)lab.FindName("SceneView");
                    var camera = (PerspectiveCamera)view.Camera; var input = (System.Windows.Controls.Border)lab.FindName("SceneInput");
                    Check(ReferenceEquals(pages.SelectedItem, lab.FindName("AdmSceneTab")) && pages.SelectedIndex == 4 && ((TabItem)lab.FindName("AdmSceneTab")).IsEnabled,
                        "Dedicated scene button opens the enabled ADM scene tab");
                    var control = (AdmSceneCameraController)Field(lab, "sceneCamera");
                    var initialPosition = camera.Position; var initialDirection = camera.LookDirection; var initialPose = HeadphoneSession.Current.Pose;
                    control.Orbit(1250, 200); control.Pan(90, -40, view.ActualHeight); control.Zoom(8);
                    Check(control.Pitch == 89 && double.IsFinite(camera.Position.X) && control.Distance >= .6,
                        "Orbit reaches an overhead view, wraps rotation and keeps zoom finite");
                    input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(lab), 0, Key.Home) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check((camera.Position - initialPosition).Length < 1e-12 && (camera.LookDirection - initialDirection).Length < 1e-12 &&
                        HeadphoneSession.Current.Pose == initialPose, "Routed Home restores the original camera without changing the listener audio pose");
                    var session = HeadphoneSession.Current; var originalOptions = session.Options;
                    var mode = (ComboBox)lab.FindName("SceneControlMode");
                    var centerHead = (Button)lab.FindName("SceneHeadCenter");
                    var tracker = (CheckBox)lab.FindName("SceneHeadTracker");
                    void SceneKey(Key key, bool system = false)
                    {
                        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(lab), 0, key)
                            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        if (system) typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
                        input.RaiseEvent(args);
                    }
                    Check(mode.SelectedIndex == 0, "ADM Scene starts with existing camera controls selected");
                    SceneKey(Key.Right);
                    Check(session.Pose == initialPose && camera.Position != initialPosition,
                        "Camera-mode arrows change only the inspection camera");
                    control.Reset(); mode.SelectedIndex = 1;
                    SceneKey(Key.Right); SceneKey(Key.Up); SceneKey(Key.E);
                    Check(session.Pose == new HeadPose(initialPose.Yaw + 5, initialPose.Pitch + 5, initialPose.Roll + 5)
                        && camera.Position == initialPosition, "Listener-mode arrows and roll share the live audio pose without moving the camera");
                    Call(lab, "RotateSceneListener", 25d, -15d, false);
                    Check(session.Pose.Yaw == 30 && session.Pose.Pitch == -10,
                        "Listener drag deltas update yaw and pitch immediately");
                    SceneKey(Key.Right, system: true);
                    Check(session.Pose.Yaw == 35, "WPF system-key events used by Alt-modified arrows still reach listener controls");
                    SceneKey(Key.Left, system: true);
                    foreach (var pose in new[] { new HeadPose(90, 0, 0), new HeadPose(-90, 35, 20), new HeadPose(150, -70, -80) })
                    {
                        session.SetKeyboard(pose.Yaw, pose.Pitch, pose.Roll); Call(lab, "RefreshSceneListener");
                        var visualRotation = (QuaternionRotation3D)Field(lab, "sceneHeadRotation");
                        var transform = new RotateTransform3D(visualRotation);
                        foreach (var basis in new[] { System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY })
                        {
                            var audioDirection = System.Numerics.Vector3.Transform(basis, pose.Rotation);
                            var actual = transform.Transform((Vector3D)AdmSceneCoordinates.ToDisplay(basis));
                            Check((actual - (Vector3D)AdmSceneCoordinates.ToDisplay(audioDirection)).Length < 2e-6,
                                $"Head drawing matches audio quaternion at {pose}, axis {basis}");
                        }
                    }
                    var turnedPose = session.Pose; control.Orbit(40, 30); var movedCamera = camera.Position; SceneKey(Key.Home);
                    Check(camera.Position == movedCamera && session.Pose == new HeadPose(0, 0, 0) && mode.SelectedIndex == 1,
                        "Home in Listener mode resets all head axes without changing camera or control mode");
                    Check(((QuaternionRotation3D)Field(lab, "sceneHeadRotation")).Quaternion.IsIdentity,
                        "Listener Home immediately restores the 3D head's forward direction");
                    session.SetKeyboard(turnedPose.Yaw, turnedPose.Pitch, turnedPose.Roll); mode.SelectedIndex = 0; SceneKey(Key.Home);
                    Check(camera.Position == initialPosition && session.Pose == turnedPose,
                        "Home in Camera mode resets the camera while preserving head orientation");
                    mode.SelectedIndex = 1;
                    control.Orbit(10, 0); var orbited = camera.Position;
                    centerHead.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(session.Pose == new HeadPose(0, 0, 0) && camera.Position == orbited,
                        "Recenter head changes only the shared listener pose");
                    control.Reset();
                    // Exercise the actual loopback receiver, without claiming a physical tracker was connected.
                    session.SetOptions(session.Options with { UdpPort = FreePort() });
                    tracker.IsChecked = true; tracker.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    using (var udp = new System.Net.Sockets.UdpClient())
                    {
                        var packet = new byte[48];
                        double[] values = [123, 456, 789, 40, 15, -10];
                        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(i * 8), values[i]);
                        await udp.SendAsync(packet, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, session.Options.UdpPort));
                        await Until(() => session.PacketAgeSeconds < .5, "opentrack packet"); Call(lab, "RefreshSceneListener");
                        Check(session.Pose == new HeadPose(40, 15, -10) && session.Options.UseTracker,
                            "ADM checkbox starts the shared opentrack receiver; angular UDP data updates pose while XYZ remains unused");
                        SceneKey(Key.Left); Call(lab, "RotateSceneListener", 20d, 10d, false);
                        Check(session.Pose == new HeadPose(40, 15, -10), "Manual keys and dragging cannot fight active opentrack tracking");
                        SceneKey(Key.Home);
                        await udp.SendAsync(packet, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, session.Options.UdpPort));
                        await Task.Delay(100);
                        Check(session.Pose == new HeadPose(0, 0, 0), "Listener Home also establishes the opentrack reference orientation");
                    }
                    tracker.IsChecked = false; tracker.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    Check(!session.Options.UseTracker, "ADM tracker checkbox returns control to manual input");
                    session.SetOptions(originalOptions with { Method = HeadphoneMethod.MeasuredBrir }); Call(lab, "RefreshSceneListener");
                    var brirPose = session.Pose; SceneKey(Key.Right);
                    Check(!centerHead.IsEnabled && !tracker.IsEnabled && session.Pose == brirPose
                        && ((TextBlock)lab.FindName("ScenePoseText")).Text.Contains("BRIR"),
                        "Fixed measured BRIR disables listener rotation and explains its fixed orientation");
                    session.SetOptions(originalOptions with { Enabled = false }); Call(lab, "RefreshSceneListener");
                    SceneKey(Key.Right);
                    Check(!centerHead.IsEnabled && session.Pose == brirPose,
                        "Stereo playback does not present head rotation as audible");
                    Check(!AdmFileFormat.UsesHeadOrientation(originalOptions, AtmosPresentation.Stereo)
                        && AdmFileFormat.UsesHeadOrientation(originalOptions, AtmosPresentation.Surround51),
                        "Separate Stereo presentation bypasses listener control even when global HRTF remains selected");
                    session.SetOptions(originalOptions); session.Recenter(); Call(lab, "RefreshSceneListener");
                    mode.SelectedIndex = 0; control.Reset();
                    var wave = model.CurrentAdm!; var sources = wave.Scene.Sources.ToArray();
                    var visual = (ModelVisual3D)view.Children[0];
                    var drawnPoints = (Dictionary<string, TranslateTransform3D>)Field(lab, "scenePoints");
                    Point Project(SpatialSource source)
                    {
                        var p = drawnPoints[source.Id];
                        return visual.TransformToAncestor(view).Transform(new Point3D(p.OffsetX, p.OffsetY, p.OffsetZ));
                    }
                    var labels = (Dictionary<string, Border>)Field(lab, "sourceLabels");
                    var labelLinks = (Dictionary<string, System.Windows.Shapes.Line>)Field(lab, "sourceLabelLinks");
                    var labelLayer = (Canvas)lab.FindName("SceneLabels");
                    var labelledRows = ((DataGrid)lab.FindName("ObjectsGrid")).Items.Cast<AdmSceneSourceRow>().ToArray();
                    Check(!labelLayer.IsHitTestVisible && labels.Count == sources.Length && labelledRows.All(r =>
                        ((TextBlock)labels[r.Id].Child).Text == r.Label), "Sphere labels match the source table and never intercept camera or Solo input");
                    var movingLabelSource = sources.Single(s => s.Kind == SpatialSourceKind.Object);
                    var objectLabel = labels[movingLabelSource.Id];
                    Call(lab, "UpdateScene", 0d);
                    Check(objectLabel.Visibility == Visibility.Collapsed, "Object label hides before its source starts");
                    Call(lab, "UpdateScene", 2d);
                    Point LabelPosition() => new(Canvas.GetLeft(objectLabel), Canvas.GetTop(objectLabel));
                    bool LabelFollowsSource()
                    {
                        lab.UpdateLayout(); var anchor = Project(movingLabelSource);
                        var link = labelLinks[movingLabelSource.Id];
                        bool follows = objectLabel.Visibility == Visibility.Visible && Math.Abs(link.X1 - anchor.X) < 1
                            && Math.Abs(link.Y1 - anchor.Y) < 1 && Canvas.GetLeft(objectLabel) >= 0 && Canvas.GetTop(objectLabel) >= 0
                            && Canvas.GetLeft(objectLabel) + objectLabel.ActualWidth <= view.ActualWidth + 1
                            && Canvas.GetTop(objectLabel) + objectLabel.ActualHeight <= view.ActualHeight + 1;
                        if (!follows) Console.WriteLine($"Label mismatch: anchor={anchor} label={LabelPosition()} size={objectLabel.ActualWidth},{objectLabel.ActualHeight} viewport={view.ActualWidth},{view.ActualHeight} visibility={objectLabel.Visibility}");
                        return follows;
                    }
                    var labelAtTwo = LabelPosition();
                    Check(LabelFollowsSource(), "Active object label sits beside the actual projected WPF sphere");
                    Call(lab, "UpdateScene", 8d);
                    Check(LabelFollowsSource() && (LabelPosition() - labelAtTwo).Length > 10, "Seeking to a moving elevated source moves its label with it");
                    control.Orbit(35, 15); control.Zoom(1); control.Pan(15, 10, view.ActualHeight);
                    Check(LabelFollowsSource(), "Orbit, zoom and pan immediately reproject upright object labels");
                    lab.Width -= 110; lab.Height -= 70; lab.UpdateLayout();
                    Check(LabelFollowsSource(), "Labels follow the resized viewport without waiting for playback");
                    lab.Width += 110; lab.Height += 70; lab.UpdateLayout(); control.Reset();
                    camera.LookDirection = -camera.LookDirection;
                    Check(labels.Values.All(l => l.Visibility == Visibility.Collapsed), "Sources behind the inspection camera have no floating labels");
                    control.Reset();
                    Call(lab, "UpdateScene", 12d);
                    Check(labels.Values.All(l => l.Visibility == Visibility.Collapsed), "Labels hide when every source has ended");
                    var caption = typeof(SpatialLabWindow).GetMethod("SourceCaption", BindingFlags.NonPublic | BindingFlags.Static)!;
                    Check((string)caption.Invoke(null, [movingLabelSource with { Name = "Object 12 · Object 12" }, 2])! == "Object 12"
                        && (string)caption.Invoke(null, [movingLabelSource with { Name = "Object 0 · Object 0" }, 2])! == "Object 0"
                        && (string)caption.Invoke(null, [movingLabelSource, 2])! == "#3",
                        "Decoded object numbers survive independently of PCM order; unnamed numeric references are clearly marked");
                    Call(lab, "UpdateScene", model.CurrentAdmSeconds);
                    var bedLeft = sources.Single(s => s.TrackIndex == 0); var bedRight = sources.Single(s => s.TrackIndex == 1);
                    double defaultCenterX = visual.TransformToAncestor(view).Transform(new Point3D(0, 0, 0)).X;
                    Check(Project(bedLeft).X < defaultCenterX && Project(bedRight).X > defaultCenterX,
                        "Actual drawn left/right spheres appear on the matching side of the listener in the default rear view");
                    control.Orbit(-control.Yaw, -control.Pitch);
                    var centerScreen = visual.TransformToAncestor(view).Transform(new Point3D(0, 0, 0));
                    Check(Project(bedLeft).X < centerScreen.X && Project(bedRight).X > centerScreen.X,
                        "Looking straight forward from behind the listener places left audio on screen left and right audio on screen right");
                    var floor = ((Model3DGroup)visual.Content).Children.OfType<Model3DGroup>().Single();
                    Check(floor.Bounds.Z + floor.Bounds.SizeZ < 0 && camera.LookDirection.Z < 0,
                        "FRONT floor guide points ahead in the same converted display coordinates as the source spheres");
                    Call(lab, "UpdateScene", 8d);
                    var movingSource = sources.Single(s => s.Kind == SpatialSourceKind.Object);
                    var audioPoint = movingSource.At(8 * 48000).Position; var drawn = drawnPoints[movingSource.Id];
                    Check(audioPoint.Y > 0 && drawn.OffsetX == audioPoint.X && drawn.OffsetY == audioPoint.Y && drawn.OffsetZ == -audioPoint.Z,
                        "Moving and elevated object positions convert only at the WPF display boundary, preserving audio metadata");
                    Call(lab, "UpdateScene", model.CurrentAdmSeconds); control.Reset();
                    foreach (var method in new[] { HeadphoneMethod.Direct, HeadphoneMethod.SteamAudio })
                    {
                        var options = new HeadphoneOptions { Enabled = true, Method = method, SmoothingMs = 0 };
                        var bedScene = wave.Scene with { Sources = [bedLeft, bedRight] };
                        var earRatios = new double[2];
                        for (int active = 0; active < 2; active++)
                        {
                            using var renderer = new HeadphoneRenderer([bedLeft.Id, bedRight.Id], options, bedScene);
                            var impulse = new[] { new float[256], new float[256] }; impulse[active][0] = .2f;
                            var output = new float[512]; double le = 0, re = 0;
                            for (int b = 0; b < 8; b++)
                            {
                                renderer.Render(impulse, output, new(0, 0, 0), options, new(StereoMixSettings.Default, null), 1);
                                for (int f = 0; f < 256; f++) { le += output[2 * f] * output[2 * f]; re += output[2 * f + 1] * output[2 * f + 1]; }
                                Array.Clear(impulse[active]);
                            }
                            earRatios[active] = le / (le + re);
                        }
                        Check(earRatios[0] > .5 && earRatios[1] < .5,
                            method + ": the sources displayed on the left/right are louder in the matching headphone ear without swapping PCM");
                        using (var turnedRenderer = new HeadphoneRenderer([bedLeft.Id, bedRight.Id], options, bedScene))
                        {
                            var impulse = new[] { new float[256], new float[256] }; impulse[0][0] = .2f;
                            var output = new float[512]; double leftEnergy = 0, rightEnergy = 0;
                            for (int b = 0; b < 8; b++)
                            {
                                turnedRenderer.Render(impulse, output, new(-120, 0, 0), options, new(StereoMixSettings.Default, null), 1);
                                for (int f = 0; f < 256; f++) { leftEnergy += output[2 * f] * output[2 * f]; rightEnergy += output[2 * f + 1] * output[2 * f + 1]; }
                                Array.Clear(impulse[0]);
                            }
                            Check(rightEnergy > leftEnergy, method + ": turning left past a fixed left source places its broadband energy in the right ear");
                        }
                        metrics.Add(new { method = method.ToString(), leftSourceLeftEarRatio = earRatios[0], rightSourceLeftEarRatio = earRatios[1] });
                    }
                    Save(lab, Path.Combine(root, "05-adm-left-right-corrected.png"));
                    string? picked = null;
                    foreach (var source in sources)
                    {
                        var projected = Project(source);
                        var hit = (string?)Call(lab, "PickSceneSource", projected);
                        if (hit == source.Id) { picked = hit; break; }
                    }
                    Check(picked is not null, "Real WPF ray hit-testing identifies a projected source sphere");
                    SetField(lab, "pointerButton", MouseButton.Left); SetField(lab, "pointerSource", picked); SetField(lab, "pointerDragged", false);
                    Call(lab, "Scene_MouseUp", input, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                    Check(model.AdmSoloSources.SequenceEqual([picked!]), "Sphere click handler activates the hit source Solo");
                    model.ToggleAdmSourceSolo(picked!); Check(model.AdmSoloSources.Count == 0, "Clicking the sole selected source again releases Solo");
                    model.ToggleAdmSourceSolo(sources[0].Id); model.ToggleAdmSourceSolo(sources[1].Id, additive: true);
                    Check(model.AdmSoloSources.Count == 2, "Additive source selection retains multiple simultaneous Solo sources");
                    var rows = ((DataGrid)lab.FindName("ObjectsGrid")).Items.Cast<AdmSceneSourceRow>().ToArray();
                    rows[0].IsSolo = false; Check(!rows[0].IsSolo && rows[1].IsSolo, "Source table checkbox removes only its own Solo and follows live model state");
                    model.ToggleAdmSourceSolo(sources[0].Id);
                    Check(rows[0].IsSolo && !rows[1].IsSolo, "Ordinary source click replaces an existing Solo selection");
                    var styles = (System.Collections.IDictionary)Field(lab, "sourceStyles");
                    Check(styles.Count == sources.Length && ((TextBlock)lab.FindName("SceneSoloText")).Text.Contains(sources[0].Name),
                        "Scene has per-source visual feedback and identifies the active preview Solo");
                    Check(((TextBlock)labels[sources[0].Id].Child).Foreground == Brushes.Gold && labels[sources[1].Id].Opacity < 1,
                        "Labels highlight the Solo source and dim other sources without hiding their identities");
                    Save(lab, Path.Combine(root, "02-adm-scene-solo.png")); control.Orbit(50, 45); Save(lab, Path.Combine(root, "03-adm-scene-overhead.png"));
                    control.Reset(); model.ClearAdmSourceSolo();

                    // Compare actual source-mute DSP to independently zeroed input, including reflections.
                    foreach (var room in new[] { new SpatialRoomOptions(), new SpatialRoomOptions { Mode = "Studio" } })
                    {
                        var snapshot = new SpatialRenderSnapshot(new() { Enabled = true, Method = HeadphoneMethod.SteamAudio, SmoothingMs = 0, Room = room },
                            new(0, 0, 0), new(StereoMixSettings.Default, null), 1, HrtfEntry.BuiltIn.Hash);
                        var sourceInput = SpatialInput.FromAdm(wave, 0, 48000);
                        var monitor = new AdmSourceMonitor(); monitor.Set(wave, [sources[0].Id]);
                        using var selected = new SpatialBlockRenderer(sourceInput, snapshot); using var reference = new SpatialBlockRenderer(sourceInput, snapshot);
                        using var pcm = SpatialPcmSource.Open(sourceInput); byte[] block = new byte[256 * wave.Channels * 4]; double maxError = 0, peak = 0;
                        while (pcm.Read(block) is var n && n != 0)
                        {
                            byte[] zeroed = block[..n].ToArray();
                            for (int f = 0; f < n / (wave.Channels * 4); f++)
                                foreach (var other in sources.Where(s => s.Id != sources[0].Id))
                                    BinaryPrimitives.WriteSingleLittleEndian(zeroed.AsSpan((f * wave.Channels + other.TrackIndex) * 4), 0);
                            selected.Render(block.AsSpan(0, n), liveSourceMix: monitor.For(wave)); reference.Render(zeroed);
                            for (int i = 0; i < 512; i++) { maxError = Math.Max(maxError, Math.Abs(selected.Output[i] - reference.Output[i])); peak = Math.Max(peak, Math.Abs(selected.Output[i])); }
                        }
                        Check(maxError < 1e-7 && peak > .001, room.Mode + ": source Solo equals independently zeroed PCM before HRTF and reflections");
                        metrics.Add(new { room = room.Mode.ToString(), maxError, peak });
                    }
                    var sinks = new List<Capture>(); AudioNavigationService.HeadphoneOutputFactory = () => { var sink = new Capture(); sinks.Add(sink); return sink; };
                    await model.TogglePlaybackAsync(); await Until(() => sinks.Count == 1 && sinks[0].Count > 30000, "initial PCM");
                    model.ToggleAdmSourceSolo(sources[0].Id); await Task.Delay(850);
                    var onlyLeft = sinks[0].Latest(9600); double left220 = Tone(onlyLeft, 220), left330 = Tone(onlyLeft, 330);
                    Check(left220 > 1e-6 && left330 < left220 * .001, "Running main preview Solo suppresses the other real source tone in device-bound stereo PCM");
                    var frontLeftPose = session.Pose;
                    session.SetKeyboard(-120, 0, 0); await Task.Delay(450);
                    var turned = sinks[0].Latest(9600); double turnedLeft = EarTone(turned, 220, 0), turnedRight = EarTone(turned, 220, 1);
                    Check(turnedRight > turnedLeft * 1.1 && sinks.Count == 1,
                        "Head rotation changes the actual live ADM tone's ear balance without reopening output");
                    mode.SelectedIndex = 1; SceneKey(Key.Home); await Task.Delay(450);
                    var restored = sinks[0].Latest(9600);
                    double restoredLeft = EarTone(restored, 220, 0), restoredRight = EarTone(restored, 220, 1);
                    double baselineRatio = EarTone(onlyLeft, 220, 0) / (EarTone(onlyLeft, 220, 0) + EarTone(onlyLeft, 220, 1));
                    double restoredRatio = restoredLeft / (restoredLeft + restoredRight);
                    Console.WriteLine($"Live pose ear fractions: baseline={baselineRatio:G8}, turned={turnedLeft / (turnedLeft + turnedRight):G8}, restored={restoredRatio:G8}");
                    // A single low-frequency tone can sit on different ear-response notches; broadband direction is checked above.
                    Check(Math.Abs(restoredRatio - baselineRatio) < .0001 && sinks.Count == 1,
                        "Listener Home restores the original live audio response in the same DSP/output session");
                    metrics.Add(new { baselineLeftEarFraction = baselineRatio, turnedLeftEarFraction = turnedLeft / (turnedLeft + turnedRight),
                        restoredLeftEarFraction = restoredRatio, outputSessions = sinks.Count });
                    await model.TogglePlaybackAsync(); await Task.Delay(100); var pausedCount = sinks[0].Count;
                    mode.SelectedIndex = 1; SceneKey(Key.Right); await Task.Delay(100);
                    Check(session.Pose.Yaw == 5 && sinks[0].Count == pausedCount,
                        "Listener controls remain responsive while paused without advancing audio submission");
                    session.Recenter(); await model.TogglePlaybackAsync(); await Task.Delay(250);
                    Check(sinks.Count == 1 && sinks[0].Count > pausedCount,
                        "Resuming after a paused head turn reuses the same output session");
                    mode.SelectedIndex = 1; session.SetKeyboard(65, 15, -10); Call(lab, "RefreshSceneListener");
                    Save(lab, Path.Combine(root, "07-adm-listener-turned.png"));
                    LanguageService.Instance.Apply(true); Call(lab, "RefreshSceneListener");
                    Check(((ComboBoxItem)mode.Items[1]).Content.ToString() == "リスナー" && centerHead.Content.ToString() == "頭を正面へ",
                        "New listener controls switch between English and Japanese");
                    Save(lab, Path.Combine(root, "08-adm-listener-turned-ja.png")); LanguageService.Instance.Apply(false);
                    session.Recenter(); Call(lab, "RefreshSceneListener"); mode.SelectedIndex = 0;
                    model.ToggleAdmSourceSolo(sources[1].Id, additive: true); await Task.Delay(650);
                    var both = sinks[0].Latest(9600); Check(Tone(both, 220) > 1e-6 && Tone(both, 330) > 1e-6 && sinks.Count == 1,
                        "Adding a second Solo restores its tone within the same output/DSP session");
                    model.ClearAdmSourceSolo(); await Task.Delay(400);
                    Check(model.AdmSoloSources.Count == 0 && sinks.Count == 1 && sinks[0].Finite, "Clear Solo restores all sources without restarting or generating nonfinite PCM");
                    model.StopPlayback(); await Task.Delay(150);
                    metrics.Add(new { solo220Power = left220, suppressed330Power = left330, outputSessions = sinks.Count,
                        emptyBufferEvents = sinks[0].EmptyBufferCount });
                    // Standard stereo must not inherit the last headphone pose on native ADM input.
                    session.SetOptions(originalOptions with { Enabled = false }); model.ToggleAdmSourceSolo(sources[0].Id);
                    session.Recenter(); await model.TogglePlaybackAsync(); await Until(() => sinks.Count == 2 && sinks[1].Count > 30000, "stereo neutral");
                    var stereoNeutral = sinks[1].Latest(9600); model.StopPlayback(); await Task.Delay(100);
                    session.SetKeyboard(80, 25, -10); await model.TogglePlaybackAsync();
                    await Until(() => sinks.Count == 3 && sinks[2].Count > 30000, "stereo retained head pose");
                    var stereoTurned = sinks[2].Latest(9600);
                    double Fraction(float[] pcm) => EarTone(pcm, 220, 0) / (EarTone(pcm, 220, 0) + EarTone(pcm, 220, 1));
                    Check(Math.Abs(Fraction(stereoNeutral) - Fraction(stereoTurned)) < .0001,
                        "Native ADM standard stereo playback ignores a retained headphone pose");
                    double fixedStereoRatio = Fraction(stereoTurned); session.SetKeyboard(-100, 0, 0); await Task.Delay(350);
                    Check(Math.Abs(Fraction(sinks[2].Latest(9600)) - fixedStereoRatio) < .0001,
                        "Headphone-window pose edits cannot rotate native ADM standard stereo during playback");
                    model.StopPlayback(); await Task.Delay(100); model.ClearAdmSourceSolo(); session.SetOptions(originalOptions); session.Recenter();
                    var numbered = wave.Scene with { Sources = sources.Select(s => s.Kind == SpatialSourceKind.Object
                        ? s with { Name = "Object 12 · Object 12" } : s).ToArray() };
                    Call(lab, "SetScene", numbered); Call(lab, "UpdateScene", 2d);
                    lab.Height = 960; lab.UpdateLayout();
                    Check(((TextBlock)labels[movingLabelSource.Id].Child).Text == "Object 12"
                        && ((DataGrid)lab.FindName("ObjectsGrid")).Items.Cast<AdmSceneSourceRow>().Single(r => r.Id == movingLabelSource.Id).Label == "Object 12",
                        "Numbered metadata appears on both the actual 3D overlay and the table, independent of track 3");
                    var labelBounds = labels.Values.Where(l => l.Visibility == Visibility.Visible)
                        .Select(l => new Rect(new Point(Canvas.GetLeft(l), Canvas.GetTop(l)), l.RenderSize)).ToArray();
                    Check(!labelBounds.Where((r, i) => labelBounds.Skip(i + 1).Any(r.IntersectsWith)).Any(),
                        "Nearby sphere labels remain separately readable with leaders identifying displaced labels");
                    Save(lab, Path.Combine(root, "06-adm-object-number-labels.png"));
                    Call(lab, "SetScene", wave.Scene);
                    model.ToggleAdmSourceSolo(sources[0].Id); lab.Close(); lab = null;
                    Check(model.AdmSoloSources.Count == 0 && SHA256.HashData(File.ReadAllBytes(demo)).SequenceEqual(originalHash),
                        "Closing the Lab clears audition Solo and leaves the ADM source unchanged");
                    sceneButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                    lab = app.Windows.OfType<SpatialLabWindow>().Single();
                    Check(((ComboBox)lab.FindName("SceneControlMode")).SelectedIndex == 0
                        && ((Viewport3D)lab.FindName("SceneView")).Children.Count == 2,
                        "Closing and reopening the ADM scene recreates a working listener model and camera controls");
                    lab.Close(); lab = null;
                    await model.DisableAdmPlaybackAsync(); Pump(100);
                    Check(!model.IsAdmPlaybackEnabled && !sceneButton.IsEnabled && !model.CanExportAdm && model.CanPlay && !model.HeadphonesEnabled,
                        "Disabling ADM retains native audio with standard playback and disables ADM tools");
                    string ordinary = Path.Combine(root, "ordinary.wav");
                    // Generate plain stereo through the production FFmpeg tool, without ADM metadata.
                    var runner = new ProcessRunner(new AppLog(Path.Combine(root, "logs")));
                    await runner.RunAsync(new ToolPaths().Ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=500:duration=1", "-ac", "2", ordinary], CancellationToken.None);
                    await model.OpenAudioFilesAsync([ordinary]); Pump(100);
                    Check(!enable.IsEnabled && !sceneButton.IsEnabled && !model.CanExportAdm && !model.IsAdmAvailable && model.CanPlay && model.CanOpenSpatialLab,
                        "Ordinary audio disables every ADM-specific control while retaining standard audio and general HRTF/room access");
                    Save(main, Path.Combine(root, "04-ordinary-audio.png"));
                    if (File.Exists(trueHd))
                    {
                        await model.OpenAudioFilesAsync([Path.GetFullPath(trueHd)]); Pump(100);
                        Check(enable.IsEnabled && !sceneButton.IsEnabled && !model.IsAdmPlaybackEnabled && model.CurrentAdm is null,
                            "TrueHD Atmos detection enables only the explicit ADM source button");
                        await model.SeekAsync(3); Check(await model.EnableAdmPlaybackAsync() && model.CurrentAdm is { Channels: 16 } &&
                            Math.Abs(model.PreviewSeconds - 3) < .03, "Explicit Atmos enable prepares positional ADM and preserves the selected time");
                        await model.SeekAsync(5); await model.DisableAdmPlaybackAsync(); Pump(100);
                        Check(model.CurrentAdm is null && model.SelectedStream?.Codec == "truehd" && !sceneButton.IsEnabled && enable.IsEnabled &&
                            Math.Abs(model.PreviewSeconds - 5) < .03, "Disabling prepared Atmos restores its original source and current playback position");
                    }
                    Check(errors.Count == 0, "No WPF dispatcher exceptions throughout source changes, camera controls and live Solo");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, metrics, audio = "Captured PCM; silence sent to real WASAPI hardware" });
                }
                var task = Tests(); while (!task.IsCompleted) Pump(15); task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { model?.StopPlayback(); lab?.Close(); main?.Close(); AudioNavigationService.HeadphoneOutputFactory = originalFactory; Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("ADM scene / explicit mode tests failed", failure);
    }
    static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    static object? Call(object target, string name, params object[] values) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
    static double Tone(float[] samples, double frequency)
    {
        double energy = 0;
        for (int c = 0; c < 2; c++) { double re = 0, im = 0; for (int f = 0; f < samples.Length / 2; f++) { double a = 2 * Math.PI * frequency * f / 48000; re += samples[f * 2 + c] * Math.Cos(a); im += samples[f * 2 + c] * Math.Sin(a); } energy += (re * re + im * im) / Math.Pow(samples.Length / 2, 2); }
        return energy;
    }
    static double EarTone(float[] samples, double frequency, int channel)
    {
        double re = 0, im = 0;
        for (int f = 0; f < samples.Length / 2; f++)
        { double a = 2 * Math.PI * frequency * f / 48000; re += samples[2 * f + channel] * Math.Cos(a); im += samples[2 * f + channel] * Math.Sin(a); }
        return re * re + im * im;
    }
    static int FreePort()
    {
        using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
    static void Pump(int ms) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    static void Save(Window window, string path) { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file); }
    sealed class Capture : IHeadphoneOutput
    {
        readonly SpatialLabTests.SilentDevice device = new(); readonly object gate = new(); readonly List<float> samples = [];
        public bool Finite { get; private set; } = true; public int Count { get { lock (gate) return samples.Count; } }
        public float[] Latest(int count) { lock (gate) return samples.Skip(Math.Max(0, samples.Count - count)).ToArray(); }
        public long SubmittedFrames => device.SubmittedFrames; public long PlayedFrames => device.PlayedFrames; public double BufferMilliseconds => device.BufferMilliseconds;
        public double StreamLatencyMilliseconds => device.StreamLatencyMilliseconds; public bool HasStarted => device.HasStarted; public long EmptyBufferCount => device.EmptyBufferCount; public bool SupportsPause => true;
        public void Pause() => device.Pause(); public void Resume() => device.Resume(); public void WaitForSpace(int frames, CancellationToken token) => device.WaitForSpace(frames, token);
        public void Write(float[] pcm, int frames) { lock (gate) for (int i = 0; i < frames * 2; i++) { samples.Add(pcm[i]); Finite &= float.IsFinite(pcm[i]); } device.Write(pcm, frames); }
        public void Drain(CancellationToken token) => device.Drain(token); public void Dispose() => device.Dispose();
    }
}
