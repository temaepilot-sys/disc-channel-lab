using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Disc2Flac;

public partial class SpatialLabWindow
{
    // Audio front is +Z; WPF display front is -Z. Neither follows headphone pose.
    void UpdateFrontGuide()
    {
        var camera = (PerspectiveCamera)SceneView.Camera;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        var front = AdmSceneCoordinates.Front;
        var direction = new Vector(Vector3D.DotProduct(front, right), -Vector3D.DotProduct(front, up));
        if (direction.Length < .001) direction = new(0, Vector3D.DotProduct(front, forward) >= 0 ? -1 : 1);
        direction.Normalize(); var perpendicular = new Vector(-direction.Y, direction.X);
        var tip = new Point(29, 29) + direction * 20;
        var shaft = new Point(29, 29) - direction * 10;
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(shaft, false, false); drawing.LineTo(tip, true, false);
            drawing.BeginFigure(tip - direction * 7 + perpendicular * 5, false, false);
            drawing.LineTo(tip, true, false); drawing.LineTo(tip - direction * 7 - perpendicular * 5, true, false);
        }
        geometry.Freeze(); SceneFrontArrow.Data = SceneFrontGlow.Data = geometry;
    }

    static Model3DGroup FrontFloorGuide()
    {
        var group = new Model3DGroup();
        // A quiet amber arc and chevron on the front edge of the floor; static, no audio work.
        MeshGeometry3D Arc(double width)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i <= 36; i++)
            {
                double angle = (-35 + i * 70d / 36) * Math.PI / 180;
                foreach (double radius in new[] { 2.05 - width / 2, 2.05 + width / 2 })
                    mesh.Positions.Add(AdmSceneCoordinates.ToDisplay(radius * Math.Sin(angle), -.165, radius * Math.Cos(angle)));
                if (i != 0) { int p = (i - 1) * 2; foreach (int index in new[] { p, p + 1, p + 2, p + 2, p + 1, p + 3 }) mesh.TriangleIndices.Add(index); }
            }
            return mesh;
        }
        GeometryModel3D Light(MeshGeometry3D mesh, byte alpha)
        {
            var material = new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(alpha, 255, 190, 103)));
            return new(mesh, material) { BackMaterial = material };
        }
        group.Children.Add(Light(Arc(.065), 35)); group.Children.Add(Light(Arc(.012), 200));
        var chevron = new MeshGeometry3D
        {
            Positions = new([AdmSceneCoordinates.ToDisplay(-.18, -.16, 2.1), AdmSceneCoordinates.ToDisplay(0, -.16, 2.39),
                AdmSceneCoordinates.ToDisplay(.18, -.16, 2.1), AdmSceneCoordinates.ToDisplay(0, -.16, 2.19)]),
            TriangleIndices = new([0, 1, 3, 1, 2, 3])
        };
        group.Children.Add(Light(chevron, 235)); group.Freeze(); return group;
    }
}
