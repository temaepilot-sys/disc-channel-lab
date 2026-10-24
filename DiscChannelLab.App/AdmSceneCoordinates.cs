using System.Numerics;
using System.Windows.Media.Media3D;

namespace Disc2Flac;

/// <summary>
/// Audio uses X right, Y up, Z front. WPF's right-handed viewing space uses
/// -Z front. Convert only at the display boundary; never mirror PCM or metadata.
/// </summary>
public static class AdmSceneCoordinates
{
    public static Point3D ToDisplay(Vector3 position) => ToDisplay(position.X, position.Y, position.Z);
    public static Point3D ToDisplay(double x, double y, double z) => new(x, y, -z);
    public static Vector3D Front => new(0, 0, -1);
}
