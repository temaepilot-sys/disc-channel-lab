using System.Windows.Media.Media3D;

namespace Disc2Flac;

/// <summary>Inspection camera only. Never changes the listener pose or audio scene coordinates.</summary>
public sealed class AdmSceneCameraController(PerspectiveCamera camera)
{
    public Point3D Target { get; private set; }
    public double Yaw { get; private set; }
    public double Pitch { get; private set; }
    public double Distance { get; private set; }
    public void Reset()
    {
        Target = new(0, .4, 0);
        // Keep the rear view within the front speaker pair, so the default
        // picture places FL and FR on opposite sides of the listener too.
        var offset = (Vector3D)AdmSceneCoordinates.ToDisplay(2.5, 3.6, -8);
        Distance = offset.Length; Yaw = Math.Atan2(offset.X, offset.Z) * 180 / Math.PI;
        Pitch = Math.Asin(offset.Y / Distance) * 180 / Math.PI; Apply();
    }
    public void Orbit(double horizontalDegrees, double verticalDegrees)
    {
        Yaw = Math.IEEERemainder(Yaw + horizontalDegrees, 360);
        Pitch = Math.Clamp(Pitch + verticalDegrees, -89, 89); Apply();
    }
    public void Pan(double dx, double dy, double viewportHeight)
    {
        Vector3D forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        double units = 2 * Distance * Math.Tan(camera.FieldOfView * Math.PI / 360) / Math.Max(100, viewportHeight);
        Target += (up * dy - right * dx) * units; Apply();
    }
    public void Zoom(double notches)
    {
        Distance = Math.Clamp(Distance * Math.Exp(-notches * .12), .6, 60); Apply();
    }
    void Apply()
    {
        double yaw = Yaw * Math.PI / 180, pitch = Pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch)) * Distance;
        camera.Position = Target + offset; camera.LookDirection = -offset; camera.UpDirection = new(0, 1, 0);
    }
}
