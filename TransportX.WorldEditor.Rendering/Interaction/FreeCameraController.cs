using System;
using System.Numerics;
using TransportX.Cameras;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Rendering.Worlds;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Rendering.Interaction;

/// <summary>
/// Wraps TransportX's <see cref="TransportX.Cameras.Camera"/> and <see cref="FreeViewpoint"/>
/// for editor-driven mouse/keyboard camera navigation across 250m chunks.
/// </summary>
public sealed class FreeCameraController
{
    public TransportX.Cameras.Camera Camera { get; }
    public FreeViewpoint Viewpoint { get; }

    public WorldPose WorldPose => Viewpoint.WorldPose;

    public CameraPose CurrentCameraPose => new CameraPose(
        Viewpoint.WorldPose.Chunk,
        Viewpoint.WorldPose.Pose.Position,
        Viewpoint.Angle);

    public FreeCameraController()
    {
        Camera = new TransportX.Cameras.Camera();
        Viewpoint = new FreeViewpoint();
        LocateDefault();
    }

    public void Locate(CameraPose pose)
    {
        Viewpoint.Locate(pose);
        SyncCamera(TimeSpan.Zero);
    }

    public void LocateDefault()
    {
        Locate(new WorldPoseDegreesModel(
            ChunkIndexModel.Zero,
            PoseDegreesModel.FromTranslation(125.0, 10.0, 125.0)));
    }

    public void Locate(WorldPoseDegreesModel poseModel)
    {
        WorldPoseDegreesModel normalized = poseModel.Normalized();
        const float degToRad = MathF.PI / 180f;
        Vector2 angle = new Vector2(
            (float)normalized.LocalPose.RotationX * degToRad,
            (float)normalized.LocalPose.RotationY * degToRad);

        CameraPose camPose = new CameraPose(
            new ChunkIndex(normalized.Chunk.X, normalized.Chunk.Z),
            new Vector3((float)normalized.LocalPose.X, (float)normalized.LocalPose.Y, (float)normalized.LocalPose.Z),
            angle);

        Viewpoint.Locate(camPose);
        SyncCamera(TimeSpan.Zero);
    }

    public void RotateRightDrag(Vector2 deltaPixels, SizeI viewportSize)
    {
        Viewpoint.Rotate(deltaPixels, viewportSize);
        SyncCamera(TimeSpan.Zero);
    }

    public void PanMiddleDrag(Vector2 deltaPixels, SizeI viewportSize)
    {
        Viewpoint.Move(deltaPixels, viewportSize);
        SyncCamera(TimeSpan.Zero);
    }

    public void ZoomWheel(float wheelDelta)
    {
        Viewpoint.Zoom(wheelDelta);
        SyncCamera(TimeSpan.Zero);
    }

    public bool MoveKeyboard(
        bool forward,
        bool backward,
        bool left,
        bool right,
        bool up,
        bool down,
        bool fastShift,
        bool slowCtrl,
        float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
        {
            return false;
        }

        Vector3 localMove = Vector3.Zero;
        if (forward)
        {
            localMove.Z += 1f;
        }

        if (backward)
        {
            localMove.Z -= 1f;
        }

        if (right)
        {
            localMove.X += 1f;
        }

        if (left)
        {
            localMove.X -= 1f;
        }

        float worldUpMove = 0f;
        if (up)
        {
            worldUpMove += 1f;
        }

        if (down)
        {
            worldUpMove -= 1f;
        }

        if (localMove.LengthSquared() < 1e-6f && MathF.Abs(worldUpMove) < 1e-6f)
        {
            return false;
        }

        float speed = 28f;
        if (fastShift)
        {
            speed *= 4f;
        }
        else if (slowCtrl)
        {
            speed *= 0.25f;
        }

        WorldPose current = Viewpoint.WorldPose;
        Vector3 worldDelta = Vector3.Transform(localMove, current.Pose.Orientation);
        worldDelta.Y += worldUpMove;

        if (0f < worldDelta.LengthSquared())
        {
            worldDelta = Vector3.Normalize(worldDelta) * (speed * deltaSeconds);
        }

        WorldPose moved = new WorldPose(
            current.Chunk,
            new Pose(current.Pose.Position + worldDelta, current.Pose.Orientation));

        Viewpoint.Locate(new CameraPose(moved.Chunk, moved.Pose.Position, Viewpoint.Angle));
        SyncCamera(TimeSpan.FromSeconds(deltaSeconds));
        return true;
    }

    public void FocusOn(WorldPoseDegreesModel target, float distance = 12f)
    {
        WorldPoseDegreesModel normTarget = target.Normalized();
        float pitchRad = 0.35f;
        Quaternion lookRot = Quaternion.CreateFromYawPitchRoll(0f, pitchRad, 0f);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, lookRot);
        Vector3 targetLocal = new((float)normTarget.LocalPose.X, (float)normTarget.LocalPose.Y, (float)normTarget.LocalPose.Z);
        Vector3 camLocal = targetLocal - forward * float.Max(2f, distance);

        CameraPose camPose = new(
            new ChunkIndex(normTarget.Chunk.X, normTarget.Chunk.Z),
            camLocal,
            new Vector2(pitchRad, 0f));

        Viewpoint.Locate(camPose);
        SyncCamera(TimeSpan.Zero);
    }

    public void JumpToChunk(int chunkX, int chunkZ)
    {
        WorldPose current = Viewpoint.WorldPose;
        CameraPose camPose = new CameraPose(
            new ChunkIndex(chunkX, chunkZ),
            new Vector3(Chunk.Size * 0.5f, MathF.Max(15f, current.Pose.Position.Y), Chunk.Size * 0.5f),
            Viewpoint.Angle);

        Viewpoint.Locate(camPose);
        SyncCamera(TimeSpan.Zero);
    }

    public void SyncCamera(TimeSpan elapsed)
    {
        Camera.FieldOfView = Viewpoint.FieldOfView;
        Camera.UpdateView(Viewpoint.WorldPose);
        Camera.Tick(elapsed);
    }
}
