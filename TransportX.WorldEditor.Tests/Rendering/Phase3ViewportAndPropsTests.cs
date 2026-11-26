using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using R3;
using TransportX.Cameras;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.ViewModels.Dock;
using Vortice.Mathematics;
using Xunit;

namespace TransportX.WorldEditor.Tests.Rendering;

public sealed class Phase3ViewportAndPropsTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p3_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void WorldCoordinateHelper_ConvertsAndComputesChunkRelativeOffsetsCorrectly()
    {
        WorldPoseDegreesModel unnormalized = new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            new PoseDegreesModel(260.0, 5.0, -10.0, 0.0, 90.0, 0.0));

        WorldPose runtimePose = WorldCoordinateHelper.ToWorldPose(unnormalized);
        Assert.Equal(1, runtimePose.Chunk.X);
        Assert.Equal(-1, runtimePose.Chunk.Z);
        Assert.Equal(10f, runtimePose.Pose.Position.X, 3);
        Assert.Equal(5f, runtimePose.Pose.Position.Y, 3);
        Assert.Equal(240f, runtimePose.Pose.Position.Z, 3);

        Vector3 relFromZero = WorldCoordinateHelper.ToChunkRelativePosition(ChunkIndex.Zero, runtimePose);
        Assert.Equal(260f, relFromZero.X, 3);
        Assert.Equal(5f, relFromZero.Y, 3);
        Assert.Equal(-10f, relFromZero.Z, 3);
    }

    [Fact]
    public void FreeCameraController_MovesAcross250mChunkBoundaries_ClampsPitch_AndJumps()
    {
        FreeCameraController cam = new FreeCameraController();
        cam.Locate(new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            new PoseDegreesModel(125.0, 10.0, 245.0, 0.0, 0.0, 0.0)));

        // Move forward (+Z) for 0.5 seconds at base speed 28 m/s (+14m -> crosses 250m into Chunk Z = 1)
        bool moved = cam.MoveKeyboard(
            forward: true,
            backward: false,
            left: false,
            right: false,
            up: false,
            down: false,
            fastShift: false,
            slowCtrl: false,
            deltaSeconds: 0.5f);

        Assert.True(moved);
        Assert.Equal(0, cam.WorldPose.Chunk.X);
        Assert.Equal(1, cam.WorldPose.Chunk.Z);
        Assert.InRange(cam.WorldPose.Pose.Position.Z, 8.5f, 9.5f);

        // Extreme vertical drag clamps pitch inside (-pi/2, +pi/2)
        cam.RotateRightDrag(new Vector2(0f, -50000f), new SizeI(1280, 720));
        float halfPi = MathF.PI * 0.5f;
        Assert.InRange(cam.Viewpoint.Angle.X, -halfPi, halfPi);

        // Chunk jump
        cam.JumpToChunk(-3, 7);
        Assert.Equal(-3, cam.WorldPose.Chunk.X);
        Assert.Equal(7, cam.WorldPose.Chunk.Z);

        // Focus on target
        cam.FocusOn(new WorldPoseDegreesModel(
            new ChunkIndexModel(2, -1),
            PoseDegreesModel.FromTranslation(100.0, 0.0, 100.0)), 10f);
        Assert.Equal(2, cam.WorldPose.Chunk.X);
        Assert.Equal(-1, cam.WorldPose.Chunk.Z);
    }

    [Fact]
    public void ViewportPickingAndGizmoService_RayPicking_GizmoHitTest_AndSnappingWork()
    {
        FreeCameraController cam = new FreeCameraController();
        cam.Locate(new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            new PoseDegreesModel(100.0, 2.0, 90.0, 0.0, 0.0, 0.0)));

        ViewContext viewCtx = cam.Camera.CreateViewContext(new SizeI(800, 600));

        // Center pixel ray looks straight along +Z from (100, 2, 90)
        ViewportRay centerRay = ViewportPickingAndGizmoService.CreateRay(400f, 300f, 800, 600, viewCtx);
        Assert.Equal(0, centerRay.OriginChunk.X);
        Assert.Equal(0, centerRay.OriginChunk.Z);
        Assert.InRange(centerRay.Direction.Z, 0.99f, 1.01f);

        BoundingBox unitBox = new BoundingBox(new Vector3(-1f, 0f, -1f), new Vector3(1f, 3f, 1f));
        List<PropPickCandidate> props = new List<PropPickCandidate>
        {
            new PropPickCandidate("prop_far", new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(100.0, 0.0, 120.0)), unitBox),
            new PropPickCandidate("prop_near", new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(100.0, 0.0, 100.0)), unitBox),
            new PropPickCandidate("prop_side", new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(130.0, 0.0, 100.0)), unitBox),
        };

        PropPickHit? hit = ViewportPickingAndGizmoService.PickNearestProp(centerRay, props);
        Assert.NotNull(hit);
        Assert.Equal("prop_near", hit!.Value.PropId);

        // Gizmo hit-testing: ray passing near X-axis arrow of gizmo at (100, 2, 100)
        ViewportRay xArrowRay = new ViewportRay(ChunkIndex.Zero, new Vector3(101.5f, 2.0f, 90.0f), Vector3.UnitZ);
        GizmoAxis xHit = ViewportPickingAndGizmoService.HitTestGizmo(
            xArrowRay,
            new Vector3(100f, 2f, 100f),
            EditorToolMode.Translate);
        Assert.Equal(GizmoAxis.X, xHit);

        // Rotate ring hit-testing: ray hitting horizontal circle of radius 2.2 around (100, 0, 100)
        ViewportRay ringRay = new ViewportRay(
            ChunkIndex.Zero,
            new Vector3(102.2f, 5.0f, 100.0f),
            Vector3.Normalize(new Vector3(0f, -1f, 0f)));
        GizmoAxis yawHit = ViewportPickingAndGizmoService.HitTestGizmo(
            ringRay,
            new Vector3(100f, 0f, 100f),
            EditorToolMode.Rotate);
        Assert.Equal(GizmoAxis.Yaw, yawHit);

        // Axis translation with 0.5m grid snap & automatic 250m chunk boundary rollover
        WorldPoseDegreesModel startEdgePose = new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            PoseDegreesModel.FromTranslation(249.0, 0.0, 50.0));
        WorldPoseDegreesModel translated = ViewportPickingAndGizmoService.ApplyAxisTranslation(
            startEdgePose,
            GizmoAxis.X,
            2.42f, // rounds to +2.5m with 0.5m grid snap -> 251.5m -> Chunk(1, 0), LocalX = 1.5m
            0.5);
        Assert.Equal(1, translated.Chunk.X);
        Assert.Equal(0, translated.Chunk.Z);
        Assert.Equal(1.5, translated.LocalPose.X, 3);

        // Yaw rotation with 15-degree angle snap
        WorldPoseDegreesModel rotated = ViewportPickingAndGizmoService.ApplyYawRotation(
            startEdgePose,
            28.4f, // snaps to 30 degrees
            15.0);
        Assert.Equal(30.0, rotated.LocalPose.RotationY, 3);

        // Ground placement raycast on y = 0 with 1.0m grid snap
        ViewportRay downwardRay = new ViewportRay(
            new ChunkIndex(0, 0),
            new Vector3(249.2f, 10f, 12.6f),
            Vector3.Normalize(new Vector3(0.2f, -1f, 0f)));
        WorldPoseDegreesModel placed = ViewportPickingAndGizmoService.SnapGroundPlacement(downwardRay, 1.0, 0f);
        Assert.Equal(1, placed.Chunk.X);
        Assert.Equal(1.0, placed.LocalPose.X, 3);
        Assert.Equal(13.0, placed.LocalPose.Z, 3);
    }

    [Fact]
    public void PropPlacement_TwoWaySelection_InspectorNormalization_MergeableDragUndo_AndSaveRoundTrip()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "PropWorld", "Prop World", "");

            // Register a model key in Models.txt
            project.ModelLists["Models.txt"].Entries.Add(new ModelListEntry
            {
                Key = "BusStopPole",
                ModelPath = "Models/BusStopPole.glb",
                Collider = ModelColliderSpec.NonCollision(),
            });
            shell.Workspace.ProjectRevision.Value++;

            // Place prop via ViewportDocumentViewModel
            shell.ViewportDocument.JumpChunkX.Value = 0m;
            shell.ViewportDocument.JumpChunkZ.Value = 0m;
            shell.Workspace.PlacementModelKey.Value = "BusStopPole";
            shell.ViewportDocument.PlacePropAtCameraCommand.Execute(Unit.Default);

            Assert.Single(project.Placement.Props);
            ChunkPropPlacement placed = project.Placement.Props[0];
            Assert.Equal("prop_001", placed.Id);
            Assert.Equal("BusStopPole", placed.ModelKey);

            // Selection is synchronized across Workspace, SceneHierarchyTool, and InspectorTool
            Assert.Equal("prop_001", shell.Workspace.SelectedPropId.Value);
            Assert.NotNull(shell.SceneHierarchyTool.SelectedItem.Value);
            Assert.Equal("prop_001", shell.SceneHierarchyTool.SelectedItem.Value!.PropId);
            Assert.True(shell.InspectorTool.HasSelectedProp.Value);
            Assert.Equal("prop_001", shell.InspectorTool.SelectedPropIdText.Value);

            // Editing LocalX past 250m in Inspector automatically normalizes into Chunk(1, 0)
            shell.InspectorTool.PropLocalX.Value = 265.5m;
            ChunkPropPlacement? afterInspectorMove = shell.Workspace.FindProp("prop_001");
            Assert.NotNull(afterInspectorMove);
            Assert.Equal(1, afterInspectorMove!.Chunk.X);
            Assert.Equal(15.5, afterInspectorMove.Pose.X, 3);
            Assert.Equal(1m, shell.InspectorTool.PropChunkX.Value);
            Assert.Equal(15.5m, shell.InspectorTool.PropLocalX.Value);

            // Simulate multi-frame gizmo drag stroke with same merge key -> coalesces into single Undo step
            string dragKey = "GizmoDrag.prop_001.stroke1";
            shell.Workspace.SetPropPose(
                "prop_001",
                new WorldPoseDegreesModel(new ChunkIndexModel(1, 0), new PoseDegreesModel(16.0, 0.0, 125.0, 0.0, 15.0, 0.0)),
                dragKey);
            shell.Workspace.SetPropPose(
                "prop_001",
                new WorldPoseDegreesModel(new ChunkIndexModel(1, 0), new PoseDegreesModel(18.0, 0.0, 125.0, 0.0, 45.0, 0.0)),
                dragKey);

            Assert.Equal(18.0, shell.Workspace.FindProp("prop_001")!.Pose.X, 3);
            Assert.Equal(45.0, shell.Workspace.FindProp("prop_001")!.Pose.RotationY, 3);

            // Single Undo reverts the entire coalesced gizmo drag stroke back to 15.5m
            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(15.5, shell.Workspace.FindProp("prop_001")!.Pose.X, 3);
            Assert.Equal(0.0, shell.Workspace.FindProp("prop_001")!.Pose.RotationY, 3);

            // Save and reload World.csx -> verify placed prop round-trips cleanly
            shell.SaveCommand.Execute(Unit.Default);
            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.Single(reloaded.Project.Placement.Props);
            ChunkPropPlacement reloadedProp = reloaded.Project.Placement.Props[0];
            Assert.Equal("BusStopPole", reloadedProp.ModelKey);
            Assert.Equal(1, reloadedProp.Chunk.X);
            Assert.Equal(0, reloadedProp.Chunk.Z);
            Assert.Equal(15.5, reloadedProp.Pose.X, 3);

            // Delete prop & Undo restore
            shell.InspectorTool.DeleteSelectedPropCommand.Execute(Unit.Default);
            Assert.Empty(project.Placement.Props);
            Assert.False(shell.InspectorTool.HasSelectedProp.Value);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Single(project.Placement.Props);
            Assert.True(shell.InspectorTool.HasSelectedProp.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void FreeCameraController_RepeatedMouseRotation_ProducesFiniteViewProjection()
    {
        FreeCameraController cam = new FreeCameraController();
        SizeI viewportSize = new SizeI(1280, 720);

        for (int i = 0; i < 120; i++)
        {
            float dx = (i % 3 == 0) ? 18f : -27f;
            float dy = (i % 2 == 0) ? 14f : -19f;
            cam.RotateRightDrag(new Vector2(dx, dy), viewportSize);

            ViewContext ctx = cam.Camera.CreateViewContext(viewportSize);
            Matrix4x4 vp = ctx.View * ctx.Projection;
            float[] entries = new float[]
            {
                vp.M11, vp.M12, vp.M13, vp.M14,
                vp.M21, vp.M22, vp.M23, vp.M24,
                vp.M31, vp.M32, vp.M33, vp.M34,
                vp.M41, vp.M42, vp.M43, vp.M44,
            };

            foreach (float value in entries)
            {
                Assert.True(float.IsFinite(value));
            }

            float halfPi = MathF.PI * 0.5f;
            Assert.InRange(cam.Viewpoint.Angle.X, -halfPi, halfPi);
        }
    }

    [Fact]
    public void FreeCameraController_FocusOn_PositionsCameraAboveTargetLookingDown()
    {
        FreeCameraController cam = new();
        WorldPoseDegreesModel target = new(
            new ChunkIndexModel(1, 2),
            new PoseDegreesModel(100.0, 5.0, 80.0, 0.0, 0.0, 0.0));

        cam.FocusOn(target, distance: 12f);

        Assert.Equal(new ChunkIndex(1, 2), cam.Viewpoint.WorldPose.Chunk);
        Vector3 camPos = cam.Viewpoint.WorldPose.Pose.Position;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, cam.Viewpoint.WorldPose.Pose.Orientation);

        // Camera should sit above target (Y+ > target Y) and pitch downward (forward.Y < 0)
        Assert.True(5f < camPos.Y);
        Assert.True(0f < cam.Viewpoint.Angle.X);
        Assert.True(forward.Y < 0f);

        Vector3 toTarget = new Vector3(100f, 5f, 80f) - camPos;
        Vector3 dirToTarget = Vector3.Normalize(toTarget);
        Assert.Equal(forward.X, dirToTarget.X, 4);
        Assert.Equal(forward.Y, dirToTarget.Y, 4);
        Assert.Equal(forward.Z, dirToTarget.Z, 4);
    }
}
