using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace Disc2Flac;

public partial class SpatialLabWindow
{
    readonly Dictionary<string, Border> sourceLabels = [];
    readonly Dictionary<string, Line> sourceLabelLinks = [];

    void SceneLabels_Click(object sender, RoutedEventArgs e) => UpdateSourceLabels();

    static string SourceCaption(SpatialSource source, int index)
    {
        var parts = source.Name.Split(" · ", StringSplitOptions.None);
        string name = parts[0];
        // Keep decoded object numbers, which need not match the PCM track or list order.
        if (source.Kind == SpatialSourceKind.Object && name.StartsWith("Object ", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out _)) return name;
        if (source.Kind == SpatialSourceKind.ChannelBed && parts[^1].Length is > 0 and <= 20) return parts[^1];
        // General ADM does not require a numeric object name. This is a view reference,
        // explicitly repeated in the table, rather than an invented ADM identifier.
        return "#" + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    void AddSourceLabel(string id, string caption)
    {
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 10, 17, 27)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
            Child = new TextBlock { Text = caption, FontSize = 11, FontWeight = FontWeights.SemiBold },
            Visibility = Visibility.Collapsed
        };
        var link = new Line { StrokeThickness = 1, Opacity = .65, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(link, -1);
        sourceLabelLinks.Add(id, link); sourceLabels.Add(id, label);
        SceneLabels.Children.Add(link); SceneLabels.Children.Add(label);
    }

    void UpdateSourceLabels()
    {
        if (closed || ShowSceneLabels.IsChecked != true || SceneView.ActualWidth <= 0 || SceneView.ActualHeight <= 0
            || SceneView.Children.Count == 0 || SceneView.Children[0] is not ModelVisual3D visual) return;
        var camera = (PerspectiveCamera)SceneView.Camera;
        var forward = camera.LookDirection; forward.Normalize();
        var projection = visual.TransformToAncestor(SceneView);
        var occupied = new List<Rect>(sourceLabels.Count);
        foreach (var (id, label) in sourceLabels)
        {
            var link = sourceLabelLinks[id]; link.Visibility = Visibility.Collapsed;
            if (!scenePoints.TryGetValue(id, out var p) || p.OffsetY == -1000) { label.Visibility = Visibility.Collapsed; continue; }
            var position = new Point3D(p.OffsetX, p.OffsetY, p.OffsetZ);
            double depth = Vector3D.DotProduct(position - camera.Position, forward);
            if (depth <= camera.NearPlaneDistance || depth >= camera.FarPlaneDistance
                || !projection.TryTransform(position, out var point) || !double.IsFinite(point.X + point.Y)
                || point.X < 0 || point.Y < 0 || point.X > SceneView.ActualWidth || point.Y > SceneView.ActualHeight)
            { label.Visibility = Visibility.Collapsed; continue; }
            label.Visibility = Visibility.Visible;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Screen-space text stays upright and readable at every inspection angle / zoom.
            var size = label.DesiredSize;
            double maxX = Math.Max(0, SceneView.ActualWidth - size.Width), maxY = Math.Max(0, SceneView.ActualHeight - size.Height);
            var preferred = new Rect(new Point(Math.Clamp(point.X + 9, 0, maxX), Math.Clamp(point.Y - size.Height - 5, 0, maxY)), size);
            var bounds = preferred;
            // A small bounded search prevents nearby source captions from covering each other.
            // Everything runs on the existing 10 Hz scene timer / camera events, never the audio thread.
            for (int candidate = 0; candidate < 64; candidate++)
            {
                int row = candidate % 16, column = candidate / 16;
                double x = column == 0 ? preferred.X : column % 2 == 1
                    ? point.X - (size.Width + 10) * ((column + 1) / 2) : preferred.X + (size.Width + 10) * (column / 2);
                double y = preferred.Y + (row == 0 ? 0 : (row % 2 == 1 ? -1 : 1) * ((row + 1) / 2) * (size.Height + 4));
                var proposed = new Rect(new Point(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY)), size);
                if (occupied.Any(r => r.IntersectsWith(proposed))) continue;
                bounds = proposed; break;
            }
            var reserved = bounds; reserved.Inflate(2, 2); occupied.Add(reserved);
            Canvas.SetLeft(label, bounds.X); Canvas.SetTop(label, bounds.Y);
            link.X1 = point.X; link.Y1 = point.Y;
            link.X2 = Math.Clamp(point.X, bounds.Left, bounds.Right); link.Y2 = Math.Clamp(point.Y, bounds.Top, bounds.Bottom);
            if (bounds.Location != preferred.Location) link.Visibility = Visibility.Visible;
        }
    }
}
