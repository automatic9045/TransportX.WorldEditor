using System.Numerics;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models.Primitives;

namespace TransportX.WorldEditor.Rendering.Worlds;

/// <summary>
/// Converts between <see cref="WorldPoseDegreesModel"/> (editor domain) and TransportX runtime
/// <see cref="WorldPose"/> / <see cref="Pose"/> respecting the 250m chunk normalization and
/// <see cref="SixDoF.FromDegrees"/> rotation convention.
/// </summary>
public static class WorldCoordinateHelper
{
    public static Pose ToLocalPose(WorldPoseDegreesModel model)
    {
        SixDoF sixDof = SixDoF.FromDegrees(
            (float)model.LocalPose.X,
            (float)model.LocalPose.Y,
            (float)model.LocalPose.Z,
            (float)model.LocalPose.RotationX,
            (float)model.LocalPose.RotationY,
            (float)model.LocalPose.RotationZ);
        return sixDof.ToPose();
    }

    public static WorldPose ToWorldPose(WorldPoseDegreesModel model)
    {
        WorldPoseDegreesModel normalized = model.Normalized();
        return new WorldPose(
            new ChunkIndex(normalized.Chunk.X, normalized.Chunk.Z),
            ToLocalPose(normalized));
    }

    public static WorldPose ToWorldPose(Core.Models.Primitives.ChunkIndexModel chunk, PoseDegreesModel localPose)
    {
        return ToWorldPose(new WorldPoseDegreesModel(chunk, localPose));
    }

    public static WorldPoseDegreesModel ToModel(in WorldPose worldPose, float pitchDeg, float yawDeg, float rollDeg)
    {
        return new WorldPoseDegreesModel(
            new ChunkIndexModel(worldPose.Chunk.X, worldPose.Chunk.Z),
            new PoseDegreesModel(
                worldPose.Pose.Position.X,
                worldPose.Pose.Position.Y,
                worldPose.Pose.Position.Z,
                pitchDeg,
                yawDeg,
                rollDeg)).Normalized();
    }

    /// <summary>
    /// Computes the position of <paramref name="targetChunk"/> + <paramref name="targetLocalPos"/>
    /// expressed relative to <paramref name="referenceChunk"/>'s origin in left-handed meters.
    /// </summary>
    public static Vector3 ToChunkRelativePosition(
        ChunkIndex referenceChunk,
        ChunkIndexModel targetChunk,
        Vector3 targetLocalPos)
    {
        int dx = targetChunk.X - referenceChunk.X;
        int dz = targetChunk.Z - referenceChunk.Z;
        return new Vector3(
            dx * Chunk.Size + targetLocalPos.X,
            targetLocalPos.Y,
            dz * Chunk.Size + targetLocalPos.Z);
    }

    /// <summary>
    /// Computes camera-chunk-relative position for a <see cref="WorldPose"/>.
    /// </summary>
    public static Vector3 ToChunkRelativePosition(ChunkIndex referenceChunk, in WorldPose targetPose)
    {
        ChunkIndex delta = targetPose.Chunk - referenceChunk;
        return delta.Position + targetPose.Pose.Position;
    }
}
