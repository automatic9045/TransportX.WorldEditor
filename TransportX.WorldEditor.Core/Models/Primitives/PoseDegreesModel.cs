namespace TransportX.WorldEditor.Core.Models.Primitives;

/// <summary>
/// Represents a local 6-DoF pose using meter translation and degree Euler angles (Yaw-Pitch-Roll).
/// </summary>
public readonly record struct PoseDegreesModel(
    double X,
    double Y,
    double Z,
    double RotationX,
    double RotationY,
    double RotationZ)
{
    public static PoseDegreesModel Identity { get; } = new(0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

    public static PoseDegreesModel FromTranslation(double x, double y, double z)
    {
        return new PoseDegreesModel(x, y, z, 0.0, 0.0, 0.0);
    }
}
