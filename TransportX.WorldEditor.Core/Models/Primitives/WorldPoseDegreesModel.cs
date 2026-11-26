using System;

namespace TransportX.WorldEditor.Core.Models.Primitives;

/// <summary>
/// Represents a world pose composed of a 250m chunk index and chunk-relative translation/rotation in degrees.
/// </summary>
public readonly record struct WorldPoseDegreesModel(ChunkIndexModel Chunk, PoseDegreesModel LocalPose)
{
    public static WorldPoseDegreesModel Zero { get; } = new(ChunkIndexModel.Zero, PoseDegreesModel.Identity);

    public double AbsoluteX => Chunk.WorldOriginX + LocalPose.X;
    public double AbsoluteY => LocalPose.Y;
    public double AbsoluteZ => Chunk.WorldOriginZ + LocalPose.Z;

    /// <summary>
    /// Normalizes local X and Z coordinates into the [0, 250) interval by shifting chunk indices.
    /// </summary>
    public WorldPoseDegreesModel Normalized()
    {
        int dx = (int)Math.Floor(LocalPose.X / ChunkIndexModel.ChunkSize);
        int dz = (int)Math.Floor(LocalPose.Z / ChunkIndexModel.ChunkSize);
        if (dx == 0 && dz == 0)
        {
            return this;
        }

        ChunkIndexModel normalizedChunk = Chunk + new ChunkIndexModel(dx, dz);
        PoseDegreesModel normalizedPose = LocalPose with
        {
            X = LocalPose.X - (dx * ChunkIndexModel.ChunkSize),
            Z = LocalPose.Z - (dz * ChunkIndexModel.ChunkSize),
        };
        return new WorldPoseDegreesModel(normalizedChunk, normalizedPose);
    }
}
