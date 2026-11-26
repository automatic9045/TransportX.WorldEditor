namespace TransportX.WorldEditor.Core.Models.Primitives;

/// <summary>
/// Represents a 250m x 250m chunk coordinate index in the TransportX world.
/// </summary>
public readonly record struct ChunkIndexModel(int X, int Z)
{
    public const double ChunkSize = 250.0;

    public static ChunkIndexModel Zero { get; } = new(0, 0);

    public double WorldOriginX => X * ChunkSize;
    public double WorldOriginZ => Z * ChunkSize;

    public static ChunkIndexModel operator +(ChunkIndexModel a, ChunkIndexModel b)
    {
        return new ChunkIndexModel(a.X + b.X, a.Z + b.Z);
    }

    public static ChunkIndexModel operator -(ChunkIndexModel a, ChunkIndexModel b)
    {
        return new ChunkIndexModel(a.X - b.X, a.Z - b.Z);
    }
}
