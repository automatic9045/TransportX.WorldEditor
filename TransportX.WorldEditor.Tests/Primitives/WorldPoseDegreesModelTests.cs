using TransportX.WorldEditor.Core.Models.Primitives;
using Xunit;

namespace TransportX.WorldEditor.Tests.Primitives;

public sealed class WorldPoseDegreesModelTests
{
    [Fact]
    public void Normalized_LeavesInRangeCoordinatesUnchanged()
    {
        WorldPoseDegreesModel pose = new WorldPoseDegreesModel(
            new ChunkIndexModel(1, -2),
            new PoseDegreesModel(0.0, 5.0, 249.5, 10.0, 20.0, 30.0));

        WorldPoseDegreesModel normalized = pose.Normalized();

        Assert.Equal(pose, normalized);
    }

    [Fact]
    public void Normalized_ShiftsPositiveOverflowAndExactBoundary()
    {
        WorldPoseDegreesModel overflow = new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            new PoseDegreesModel(260.0, 1.5, 500.0, 0.0, 45.0, 0.0));

        WorldPoseDegreesModel normalized = overflow.Normalized();

        Assert.Equal(new ChunkIndexModel(1, 2), normalized.Chunk);
        Assert.Equal(10.0, normalized.LocalPose.X, 6);
        Assert.Equal(1.5, normalized.LocalPose.Y, 6);
        Assert.Equal(0.0, normalized.LocalPose.Z, 6);
        Assert.Equal(45.0, normalized.LocalPose.RotationY, 6);
    }

    [Fact]
    public void Normalized_ShiftsNegativeCoordinatesBackward()
    {
        WorldPoseDegreesModel negative = new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 1),
            new PoseDegreesModel(-1.0, 0.0, -250.5, 0.0, 0.0, 0.0));

        WorldPoseDegreesModel normalized = negative.Normalized();

        Assert.Equal(new ChunkIndexModel(-1, -1), normalized.Chunk);
        Assert.Equal(249.0, normalized.LocalPose.X, 6);
        Assert.Equal(249.5, normalized.LocalPose.Z, 6);
        Assert.Equal(-1.0, normalized.AbsoluteX, 6);
        Assert.Equal(-0.5, normalized.AbsoluteZ, 6);
    }
}
