using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Disc2Flac;

public sealed class AdmSceneSourceRow(SpatialSource source, bool canSolo, Action<string, bool> apply) : INotifyPropertyChanged
{
    bool solo;
    public string Id => source.Id;
    public string Name => source.Name;
    public SpatialSourceKind Kind => source.Kind;
    public int TrackIndex => source.TrackIndex;
    public bool IsLfe => source.IsLfe;
    public bool CanSolo => canSolo;
    public string SoloLabel => "Solo " + Name;
    public bool IsSolo { get => solo; set { if (value != solo && CanSolo) apply(Id, value); } }
    public void Refresh(bool value) { if (solo == value) return; solo = value; PropertyChanged?.Invoke(this, new(nameof(IsSolo))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SpatialLabWindow
{
    AdmSceneCameraController sceneCamera = null!;
    readonly Dictionary<GeometryModel3D, string> sourceModels = [];
    readonly Dictionary<string, (SolidColorBrush Diffuse, SolidColorBrush Glow, ScaleTransform3D Scale, SpatialSourceKind Kind)> sourceStyles = [];
    readonly List<AdmSceneSourceRow> sourceRows = [];
    Point pointerDown, pointerLast;
    MouseButton? pointerButton;
    bool pointerDragged;
    string? pointerSource;
    void InitializeSceneInteraction()
    {
        sceneCamera = new((PerspectiveCamera)SceneView.Camera); sceneCamera.Reset(); RefreshSceneSolo();
        SceneView.Camera.Changed += (_, _) => UpdateFrontGuide(); UpdateFrontGuide();
    }
    public void ShowAdmScene()
    {
        if (!model.CanOpenAdmScene) return;
        Pages.SelectedItem = AdmSceneTab; SceneInput.Focus();
    }
    void UpdateLabAvailability()
    {
        if (operation is null) Pages.IsEnabled = model.CanOpenSpatialLab;
        if (!model.IsAdmPlaybackEnabled && ReferenceEquals(Pages.SelectedItem, AdmSceneTab)) Pages.SelectedItem = HrirLibraryTab;
        if (!model.CanOpenSpatialLab && operation is null) StatusText.Text = "Enable ADM playback in the main source controls to use ADM / spatial settings.";
        RefreshSceneSolo();
    }
    GeometryModel3D SourceSphere(SpatialSource source, TranslateTransform3D translation)
    {
        var scale = new ScaleTransform3D(1, 1, 1); var transform = new Transform3DGroup();
        transform.Children.Add(scale); transform.Children.Add(translation);
        var sphere = Sphere(Colors.DeepSkyBlue, .11, transform);
        var diffuse = new SolidColorBrush(); var glow = new SolidColorBrush { Opacity = .3 };
        var material = new MaterialGroup(); material.Children.Add(new DiffuseMaterial(diffuse)); material.Children.Add(new EmissiveMaterial(glow));
        sphere.Material = sphere.BackMaterial = material; sourceModels.Add(sphere, source.Id);
        sourceStyles.Add(source.Id, (diffuse, glow, scale, source.Kind)); return sphere;
    }
    void ResetSourceInteraction(SpatialScene? scene)
    {
        ObjectsGrid.ItemsSource = null;
        sourceModels.Clear(); sourceStyles.Clear(); sourceRows.Clear();
        if (scene is not null)
            foreach (var source in scene.Sources)
                sourceRows.Add(new(source, model.IsAdmPlaybackEnabled && ReferenceEquals(scene, model.CurrentAdmScene),
                    (id, value) => model.SetAdmSourceSolo(id, value)));
        ObjectsGrid.ItemsSource = scene is null ? null : sourceRows;
    }
    void RefreshSceneSolo()
    {
        if (SceneSoloText is null) return;
        var ids = model.AdmSoloSources.ToHashSet(StringComparer.Ordinal);
        SceneSoloText.Text = model.IsAdmPlaybackEnabled ? model.AdmSoloDescription : "Enable ADM playback in the main window";
        SceneSoloText.ToolTip = SceneSoloText.Text;
        SceneSoloClear.IsEnabled = model.IsAdmPlaybackEnabled && ids.Count != 0 && operation is null && !model.IsBusy;
        foreach (var row in sourceRows) row.Refresh(ids.Contains(row.Id));
        foreach (var (id, style) in sourceStyles)
        {
            bool selected = ids.Contains(id);
            var color = selected ? Colors.Gold : ids.Count != 0 ? Color.FromRgb(53, 69, 83)
                : style.Kind == SpatialSourceKind.Object ? Colors.DeepSkyBlue : Colors.DarkOrange;
            if (style.Diffuse.Color != color) { style.Diffuse.Color = color; style.Glow.Color = color; }
            double scale = selected ? 1.25 : 1;
            style.Scale.ScaleX = style.Scale.ScaleY = style.Scale.ScaleZ = scale;
        }
    }
    void ClearSceneSolo_Click(object sender, RoutedEventArgs e) => model.ClearAdmSourceSolo();
    string? PickSceneSource(Point point)
    {
        var hit = VisualTreeHelper.HitTest(SceneView, point) as RayMeshGeometry3DHitTestResult;
        return hit?.ModelHit is GeometryModel3D mesh && sourceModels.TryGetValue(mesh, out string? id) ? id : null;
    }
    void Scene_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!model.IsAdmPlaybackEnabled || operation is not null || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        SceneInput.Focus(); pointerDown = pointerLast = e.GetPosition(SceneView); pointerButton = e.ChangedButton;
        pointerDragged = false; pointerSource = e.ChangedButton == MouseButton.Left ? PickSceneSource(pointerDown) : null;
        SceneInput.CaptureMouse(); e.Handled = true;
    }
    static double SceneInputScale => (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? .2 : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 3 : 1;
    void Scene_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(SceneView);
        if (pointerButton is not null)
        {
            if (!pointerDragged && (point - pointerDown).Length > 4) pointerDragged = true;
            if (pointerDragged)
            {
                var delta = point - pointerLast;
                if (pointerButton == MouseButton.Left) sceneCamera.Orbit(delta.X * .35 * SceneInputScale, delta.Y * .35 * SceneInputScale);
                else sceneCamera.Pan(delta.X * SceneInputScale, delta.Y * SceneInputScale, SceneView.ActualHeight);
            }
            pointerLast = point; e.Handled = true; return;
        }
        var id = PickSceneSource(point);
        SceneInput.Cursor = id is null ? Cursors.Arrow : Cursors.Hand;
        SceneInput.ToolTip = id is null ? null : sourceRows.FirstOrDefault(s => s.Id == id)?.Name + " · click: Solo / release";
    }
    void Scene_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (pointerButton != e.ChangedButton) return;
        string? clicked = !pointerDragged && e.ChangedButton == MouseButton.Left ? pointerSource : null;
        pointerButton = null; pointerSource = null; SceneInput.ReleaseMouseCapture();
        if (clicked is not null) model.ToggleAdmSourceSolo(clicked, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        e.Handled = true;
    }
    void Scene_LostCapture(object sender, MouseEventArgs e) { pointerButton = null; pointerSource = null; pointerDragged = false; }
    void Scene_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!model.IsAdmPlaybackEnabled) return;
        sceneCamera.Zoom(e.Delta / 120d * SceneInputScale); e.Handled = true;
    }
    void SceneWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(Pages.SelectedItem, AdmSceneTab) || !model.IsAdmPlaybackEnabled || e.Key != Key.Home) return;
        sceneCamera.Reset(); e.Handled = true;
    }
    void Scene_KeyDown(object sender, KeyEventArgs e)
    {
        if (!model.IsAdmPlaybackEnabled) return;
        double step = 5 * SceneInputScale;
        switch (e.Key)
        {
            case Key.Left: sceneCamera.Orbit(-step, 0); break;
            case Key.Right: sceneCamera.Orbit(step, 0); break;
            case Key.Up: sceneCamera.Orbit(0, step); break;
            case Key.Down: sceneCamera.Orbit(0, -step); break;
            case Key.Home: sceneCamera.Reset(); break;
            case Key.Escape: model.ClearAdmSourceSolo(); break;
            default: return;
        }
        e.Handled = true;
    }
}
