using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Disc2Flac;

public partial class SpatialLabWindow
{
    readonly QuaternionRotation3D sceneHeadRotation = new();
    readonly DispatcherTimer sceneHeadTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    bool ListenerControlSelected => SceneControlMode.SelectedIndex == 1;
    bool SceneHeadCanTurn => model.IsAdmPlaybackEnabled && AdmFileFormat.UsesHeadOrientation(HeadphoneSession.Current.Options,
        model.IsAtmosStreamingEnabled ? model.SelectedAtmosPresentation : AtmosPresentation.Objects);

    void InitializeSceneListener()
    {
        // Read the shared pose on the UI thread. No scene work is done on the audio/UDP threads.
        sceneHeadTimer.Tick += (_, _) =>
        {
            if (IsVisible && ReferenceEquals(Pages.SelectedItem, AdmSceneTab)) RefreshSceneListener();
        };
        sceneHeadTimer.Start(); Closed += (_, _) => sceneHeadTimer.Stop();
        RefreshSceneListener();
    }

    ModelVisual3D CreateSceneListener()
    {
        var head = new Model3DGroup { Transform = new RotateTransform3D(sceneHeadRotation) };
        head.Children.Add(Sphere(Color.FromRgb(179, 191, 204), .17, new TranslateTransform3D()));
        head.Children.Add(Sphere(Color.FromRgb(225, 153, 69), .055, new TranslateTransform3D(-.18, 0, 0)));
        head.Children.Add(Sphere(Color.FromRgb(225, 153, 69), .055, new TranslateTransform3D(.18, 0, 0)));
        head.Children.Add(Sphere(Colors.Wheat, .05, new TranslateTransform3D(0, -.015, -.18)));
        // Face and arrow initially look along display -Z (audio +Z).
        var arrow = new MeshGeometry3D
        {
            Positions = new([new(-.025, -.10, -.22), new(.025, -.10, -.22), new(.025, -.10, -.43),
                new(.10, -.10, -.43), new(0, -.10, -.60), new(-.10, -.10, -.43), new(-.025, -.10, -.43)]),
            TriangleIndices = new([0, 1, 2, 0, 2, 6, 3, 4, 5])
        };
        var material = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(255, 190, 103)));
        arrow.Freeze(); material.Freeze();
        head.Children.Add(new GeometryModel3D(arrow, material) { BackMaterial = material });
        return new ModelVisual3D { Content = head };
    }

    void RefreshSceneListener()
    {
        if (ScenePoseText is null) return; // XAML selection events can run during InitializeComponent.
        var session = HeadphoneSession.Current; var options = session.Options;
        bool canTurn = SceneHeadCanTurn;
        var pose = canTurn ? session.Pose : new HeadPose(0, 0, 0);
        var q = pose.Rotation;
        // Display reflects audio Z. F * R * F corresponds to quaternion (-x, -y, z, w).
        var display = new Quaternion(-q.X, -q.Y, q.Z, q.W);
        if (sceneHeadRotation.Quaternion != display) sceneHeadRotation.Quaternion = display;
        SceneHeadCenter.IsEnabled = canTurn && operation is null;
        SceneHeadTracker.IsEnabled = canTurn && operation is null;
        SceneHeadTracker.IsChecked = options.UseTracker;
        string text = !canTurn
            ? LanguageService.T(options.Method == HeadphoneMethod.MeasuredBrir && options.Enabled
                ? "Measured BRIRは測定時の頭の向きで固定です。頭の回転にはDirectまたはHRTFを選んでください。"
                : model.IsAtmosStreamingEnabled && model.SelectedAtmosPresentation == AtmosPresentation.Stereo
                    ? "Stereo presentationでは頭の向きは音に反映されません。Objectsまたは5.1 presentationを選んでください。"
                    : "頭の向きを音に反映するには、メイン画面でDirectまたはHRTF再生を選んでください。")
            : $"Yaw {pose.Yaw:0.#}°  ·  Pitch {pose.Pitch:0.#}°  ·  Roll {pose.Roll:0.#}°  ·  {session.TrackerStatus}";
        if (ScenePoseText.Text != text) ScenePoseText.Text = text;
        string hint = LanguageService.T(ListenerControlSelected
            ? options.UseTracker && canTurn
                ? "opentrackで頭を操作中です。手動操作するにはopentrackをオフにしてください。Home：頭を正面へ。"
                : "リスナー：ドラッグ／矢印で左右・上下、右ドラッグ／Q・Eで傾き。Shift：大きく、Alt：微調整。Home：頭を正面へ。"
            : "カメラ：ドラッグ／矢印で回転、右ドラッグで移動、ホイールでズーム。Home：カメラ復帰。Shift：大きく、Alt：微調整。");
        if (SceneControlHint.Text != hint) SceneControlHint.Text = hint;
    }

    void SceneControlMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScenePoseText is null) return;
        // End the current drag before changing what it controls. Mode changes never recenter either pose.
        SceneInput.ReleaseMouseCapture(); RefreshSceneListener(); SceneInput.Focus();
    }

    void RotateSceneListener(double horizontal, double vertical, bool roll)
    {
        if (!SceneHeadCanTurn || operation is not null || HeadphoneSession.Current.Options.UseTracker) return;
        var session = HeadphoneSession.Current; var pose = session.Pose;
        session.SetKeyboard(pose.Yaw + (roll ? 0 : horizontal), pose.Pitch + (roll ? 0 : vertical),
            pose.Roll + (roll ? horizontal : 0));
        RefreshSceneListener();
    }

    bool SceneListenerKey(Key key, double step)
    {
        switch (key)
        {
            case Key.Left: RotateSceneListener(-step, 0, false); break;
            case Key.Right: RotateSceneListener(step, 0, false); break;
            case Key.Up: RotateSceneListener(0, step, false); break;
            case Key.Down: RotateSceneListener(0, -step, false); break;
            case Key.Q: RotateSceneListener(-step, 0, true); break;
            case Key.E: RotateSceneListener(step, 0, true); break;
            default: return false;
        }
        return true;
    }

    void SceneHeadCenter_Click(object sender, RoutedEventArgs e)
    {
        if (!SceneHeadCanTurn || operation is not null) return;
        HeadphoneSession.Current.Recenter(); RefreshSceneListener(); SceneInput.Focus();
    }

    void SceneHeadTracker_Click(object sender, RoutedEventArgs e)
    {
        if (!SceneHeadCanTurn || operation is not null) { RefreshSceneListener(); return; }
        var session = HeadphoneSession.Current;
        session.SetOptions(session.Options with { UseTracker = SceneHeadTracker.IsChecked == true });
        session.Save(); RefreshSceneListener(); SceneInput.Focus();
    }
}
