using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using R3;
using TransportX.Cameras;
using TransportX.Dependency;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Diagnostics;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.ViewModels.Editors;
using TransportX.WorldEditor.Views;
using TransportX.WorldEditor.Views.Controls;
using TransportX.WorldEditor.Views.Documents;
using TransportX.WorldEditor.Views.Editors;
using TransportX.WorldEditor.Views.Tools;
using TransportX.Worlds;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace TransportX.WorldEditor.Tests;

public sealed class Phase4SplineAndNetworkTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p4_{Guid.NewGuid():N}");
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
    public void SplineAnchorUtilities_CollectsAnchors_AndExpandsReferencesCorrectly()
    {
        SplinePlacementNode spline = new()
        {
            Id = "spline_001",
            TemplateKey = "Spline1",
        };
        spline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 40.0, "s_c1"));
        spline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.ByRadius, 120.0, 35.0, "s_c2"));
        spline.Gradients.Add(new SplineGradientSegment(SplineGradientSegmentKind.Constant, 0.0, 25.0, "s_g1"));
        spline.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.Constant, 0.0, 1.067, 15.0, "s_cant1"));
        spline.ConnectBezier = new SplineConnectBezierModel(
            new PortReferenceModel("spline_other", PortTargetKind.SplineInlet),
            0.5,
            "s_end");

        IReadOnlyList<ResolvedAnchorInfo> anchors = SplineAnchorUtilities.CollectSplineAnchors(spline, connectBezierArcLength: 22.5);
        Assert.Equal(5, anchors.Count);
        Assert.Contains(anchors, a => a.AnchorId == "s_c1" && double.Abs(a.ResolvedS - 40.0) < 1e-6);
        Assert.Contains(anchors, a => a.AnchorId == "s_c2" && double.Abs(a.ResolvedS - 75.0) < 1e-6);
        Assert.Contains(anchors, a => a.AnchorId == "s_g1" && double.Abs(a.ResolvedS - 25.0) < 1e-6);
        Assert.Contains(anchors, a => a.AnchorId == "s_cant1" && double.Abs(a.ResolvedS - 15.0) < 1e-6);
        Assert.Contains(anchors, a => a.AnchorId == "s_end" && double.Abs(a.ResolvedS - 97.5) < 1e-6);
        Assert.Equal(75.0, SplineAnchorUtilities.ComputeCurveTotalLength(spline), 6);

        SplineInstancePropRule propRule = new()
        {
            Key = "Light",
            From = AnchoredDistance.RelativeTo("s_c1", 5.0),
            Span = 10.0,
            Interval = 5.0,
        };
        propRule.ModelKeys.Add("LightPole");
        spline.Props.Add(propRule);
        spline.PropSkips.Add(new SplineSkipRule("Light", AnchoredDistance.RelativeTo("s_c1", -2.0), AnchoredDistance.RelativeTo("s_c2", 0.0)));
        spline.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = "Guard",
            CrossSectionKey = "Road1L",
            From = AnchoredDistance.RelativeTo("s_c1", 10.0),
            To = AnchoredDistance.RelativeTo("s_c1", 20.0),
        });
        spline.ExtrusionSkips.Add(new SplineSkipRule("RoadSurface", AnchoredDistance.RelativeTo("s_c1", 1.0), null));

        int expanded = SplineAnchorUtilities.ExpandAnchorReferencesToAbsolute(spline, "s_c1", 40.0);
        Assert.Equal(5, expanded);

        Assert.False(spline.Props[0].From.IsRelative);
        Assert.Equal(45.0, spline.Props[0].From.Offset, 6);

        Assert.False(spline.PropSkips[0].From!.Value.IsRelative);
        Assert.Equal(38.0, spline.PropSkips[0].From!.Value.Offset, 6);
        Assert.True(spline.PropSkips[0].To!.Value.IsRelative);
        Assert.Equal("s_c2", spline.PropSkips[0].To!.Value.AnchorId);

        Assert.False(spline.Extrusions[0].From.IsRelative);
        Assert.Equal(50.0, spline.Extrusions[0].From.Offset, 6);
        Assert.False(spline.Extrusions[0].To!.Value.IsRelative);
        Assert.Equal(60.0, spline.Extrusions[0].To!.Value.Offset, 6);

        Assert.False(spline.ExtrusionSkips[0].From!.Value.IsRelative);
        Assert.Equal(41.0, spline.ExtrusionSkips[0].From!.Value.Offset, 6);
    }

    [Fact]
    public void SplineAnchorUtilities_ConnectBezierTargets_FiltersIncompatibleLayoutsAndCycles()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "TopologyTest", "Topology", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            // Add an incompatible 1-lane layout & spline template
            LaneLayoutDocument narrowLayout = new()
            {
                Key = "NarrowWalk",
                RelativePath = "LaneLayouts/NarrowWalk.xml",
            };
            narrowLayout.Lanes.Add(new LaneDefinition("Pedestrians", LaneFlowDirections.InOut, 0f, 0f, 1.0f, 1.0f));
            project.LaneLayouts["NarrowWalk"] = narrowLayout;
            project.Init.LaneLayouts.Add(new ResourceLoadReference("NarrowWalk", narrowLayout.RelativePath));

            Assert.True(ws.AddNewSplineTemplate("WalkTpl", "Templates/Splines/WalkTpl.csx"));
            Assert.True(ws.MutateSplineTemplate("WalkTpl", "Set NarrowWalk", t => t.OutletLayoutKey = "NarrowWalk"));

            SplinePlacementNode firstSpline = project.Placement.Splines[0]; // spline_001 (Layout1 - symmetric!)
            SplinePlacementNode? chained = ws.AddChainedSplineFromOutlet(firstSpline.Id, "Spline1", 40.0); // spline_002 IntoSpline(spline_001)
            Assert.NotNull(chained);

            SplinePlacementNode? independent = ws.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(new ChunkIndexModel(0, 0), PoseDegreesModel.FromTranslation(180.0, 0.0, 200.0)),
                30.0);
            Assert.NotNull(independent);

            SplinePlacementNode? narrowSpline = ws.AddSplineAtChunkPose(
                "WalkTpl",
                new WorldPoseDegreesModel(new ChunkIndexModel(0, 0), PoseDegreesModel.FromTranslation(40.0, 0.0, 40.0)),
                25.0);
            Assert.NotNull(narrowSpline);

            // For spline_002 (chained = IntoSpline(spline_001)):
            // - firstSpline.Inlet is FREE & firstSpline is upstream, so chained -> firstSpline.Inlet forms a valid closed loop!
            // - firstSpline.Outlet is OCCUPIED by chained's IntoSpline -> MUST NOT appear!
            // - WalkTpl has NarrowWalk layout -> MUST NOT appear!
            // - independent (Spline1 / Layout1) Inlet & Outlet MUST appear!
            IReadOnlyList<CompatiblePortCandidate> targetsForChained =
                SplineAnchorUtilities.FindCompatibleConnectBezierTargets(project, chained!);

            Assert.DoesNotContain(targetsForChained, c => c.PortRef.ElementId == chained!.Id);
            Assert.Contains(targetsForChained, c => c.PortRef == new PortReferenceModel(firstSpline.Id, PortTargetKind.SplineInlet));
            Assert.DoesNotContain(targetsForChained, c => c.PortRef == new PortReferenceModel(firstSpline.Id, PortTargetKind.SplineOutlet));
            Assert.DoesNotContain(targetsForChained, c => c.PortRef.ElementId == narrowSpline!.Id);
            Assert.Contains(targetsForChained, c => c.PortRef == new PortReferenceModel(independent!.Id, PortTargetKind.SplineInlet));
            Assert.Contains(targetsForChained, c => c.PortRef == new PortReferenceModel(independent!.Id, PortTargetKind.SplineOutlet));

            // Reverse direction (firstSpline -> chained) MUST be detected as a topological cycle and rejected!
            Assert.True(SplineAnchorUtilities.WouldCreateTopologyCycle(project.Placement, firstSpline.Id, chained!.Id));
            Assert.False(ws.SetSplineConnectBezier(
                firstSpline.Id,
                new SplineConnectBezierModel(new PortReferenceModel(chained.Id, PortTargetKind.SplineOutlet))));

            // Attempting ConnectBezier to incompatible layout fails cleanly
            Assert.False(ws.SetSplineConnectBezier(
                chained.Id,
                new SplineConnectBezierModel(new PortReferenceModel(narrowSpline!.Id, PortTargetKind.SplineInlet))));

            // Valid ConnectBezier from chained -> independent.Inlet succeeds + supports Undo/Redo
            PortReferenceModel validPort = new(independent!.Id, PortTargetKind.SplineInlet);
            Assert.True(ws.SetSplineConnectBezier(chained.Id, new SplineConnectBezierModel(validPort, 0.75, "s_conn")));
            Assert.NotNull(chained.ConnectBezier);
            Assert.Equal(validPort, chained.ConnectBezier!.TargetPort);

            Assert.True(ws.Undo());
            Assert.Null(chained.ConnectBezier);
            Assert.True(ws.Redo());
            Assert.Equal(validPort, chained.ConnectBezier!.TargetPort);

            // Duplicate IntoSpline chain on an occupied Outlet must fail cleanly
            Assert.Null(ws.AddChainedSplineFromOutlet(firstSpline.Id));
            Assert.Null(ws.AddChainedSplineFromOutlet(chained.Id));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SplineTemplateEditor_EditsAndPersists_WithUndoRedoAndValidationPass()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "TemplateTest", "Template World", "Desc");
            AssetBrowserToolViewModel browser = shell.AssetBrowserTool;

            // Create new SplineTemplate from AssetBrowser tab 5
            browser.SelectedTabIndex.Value = 5;
            Assert.True(browser.CanCreateForActiveTab.Value);
            browser.NewResourceKey.Value = "ArterialSpline";
            browser.CreateActiveResourceCommand.Execute(Unit.Default);

            Assert.True(project.SplineTemplates.ContainsKey("ArterialSpline"));
            AssetBrowserEntry? createdEntry = browser.SplineTemplateItems.FirstOrDefault(e => e.Key == "ArterialSpline");
            Assert.NotNull(createdEntry);

            using SplineTemplateEditorDocumentViewModel? docVm =
                shell.OpenAsset(createdEntry!) as SplineTemplateEditorDocumentViewModel;
            Assert.NotNull(docVm);

            Assert.Equal("Layout1", docVm!.OutletLayoutKey.Value);
            docVm.AddExtrusionCommand.Execute(Unit.Default);
            Assert.True(2 <= project.SplineTemplates["ArterialSpline"].Extrusions.Count);

            docVm.AddPropCommand.Execute(Unit.Default);
            Assert.Single(project.SplineTemplates["ArterialSpline"].Props);
            docVm.PropKey.Value = "Gantry";
            docVm.PropModelKeysText.Value = "PoleA, PoleB";
            docVm.PropInterval.Value = 40m;
            docVm.UserCode.Value = "// custom template note";

            SplineTemplatePropRule savedRule = project.SplineTemplates["ArterialSpline"].Props[0];
            Assert.Equal("Gantry", savedRule.Key);
            Assert.Equal(["PoleA", "PoleB"], savedRule.ModelKeys);
            Assert.Equal(40.0, savedRule.Interval, 6);
            Assert.Equal("// custom template note", project.SplineTemplates["ArterialSpline"].UserCode);

            shell.UndoCommand.Execute(Unit.Default); // undo UserCode edit
            Assert.Null(project.SplineTemplates["ArterialSpline"].UserCode);
            shell.RedoCommand.Execute(Unit.Default);
            Assert.Equal("// custom template note", project.SplineTemplates["ArterialSpline"].UserCode);

            // Save & reload round-trip
            shell.SaveAllCommand.Execute(Unit.Default);
            Assert.Empty(project.DirtyFiles);

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.True(reloaded.Project.SplineTemplates.ContainsKey("ArterialSpline"));
            SplineTemplateDocument reloadedTpl = reloaded.Project.SplineTemplates["ArterialSpline"];
            Assert.Single(reloadedTpl.Props);
            Assert.Equal("Gantry", reloadedTpl.Props[0].Key);
            Assert.Equal(["PoleA", "PoleB"], reloadedTpl.Props[0].ModelKeys);
            Assert.Equal("// custom template note", reloadedTpl.UserCode);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SplineTimelineTool_MultiTrackCrud_AnchoredDistancePicker_AndAnchorDeletionExpansion()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "TimelineTest", "Timeline", "Desc");
            WorldWorkspaceService ws = shell.Workspace;
            TimelineToolViewModel timeline = shell.TimelineTool;

            Assert.False(timeline.HasSelectedSpline.Value);

            SplinePlacementNode starter = project.Placement.Splines[0]; // Curves[0] = Straight 50m (default empty anchor)
            ws.SelectSpline(starter.Id);

            Assert.True(timeline.HasSelectedSpline.Value);
            Assert.Single(starter.Curves);
            Assert.Null(starter.Curves[0].OutputAnchorId);

            timeline.SelectTrackItem(SplineTimelineTrackKind.Curves, 0);
            timeline.CurveAnchorId.Value = "s_end";
            Assert.Contains(timeline.AvailableAnchorNames, a => a == "s_end");

            // Add 2nd Curve segment with radius
            timeline.AddCurveCommand.Execute(Unit.Default);
            Assert.Equal(2, starter.Curves.Count);
            timeline.SelectTrackItem(SplineTimelineTrackKind.Curves, 1);
            timeline.CurveKind.Value = SplineCurveSegmentKind.ByRadius;
            timeline.CurveValue.Value = 150m;
            timeline.CurveLength.Value = 30m;
            timeline.CurveAnchorId.Value = "s_curve2";

            Assert.Equal(80.0, timeline.TotalCurveLength.Value, 6);
            Assert.Contains(timeline.AvailableAnchorNames, a => a == "s_curve2");

            // Add Gradient & Cant segments
            timeline.AddGradientCommand.Execute(Unit.Default);
            Assert.Single(starter.Gradients);
            timeline.AddCantCommand.Execute(Unit.Default);
            Assert.Single(starter.Cants);

            // Add Prop override & bind its From to s_end + 5m using the 3-part picker
            timeline.AddPropCommand.Execute(Unit.Default);
            Assert.Single(starter.Props);
            timeline.SelectTrackItem(SplineTimelineTrackKind.Props, 0);
            Assert.True(timeline.ShowFromPicker.Value);
            timeline.FromModeIndex.Value = 1; // Anchor-relative
            timeline.FromAnchorId.Value = "s_end";
            timeline.FromOffset.Value = 5m;

            Assert.True(starter.Props[0].From.IsRelative);
            Assert.Equal("s_end", starter.Props[0].From.AnchorId);
            Assert.Equal(5.0, starter.Props[0].From.Offset, 6);

            // Add PropSkip override referencing s_end .. s_curve2
            timeline.AddPropSkipCommand.Execute(Unit.Default);
            Assert.Single(starter.PropSkips);
            timeline.SelectTrackItem(SplineTimelineTrackKind.PropSkips, 0);
            timeline.FromModeIndex.Value = 1;
            timeline.FromAnchorId.Value = "s_end";
            timeline.FromOffset.Value = -10m;
            timeline.HasToDistance.Value = true;
            timeline.ToModeIndex.Value = 1;
            timeline.ToAnchorId.Value = "s_curve2";
            timeline.ToOffset.Value = 0m;

            // Add Extrusion override referencing s_end - 5m
            timeline.AddExtrusionCommand.Execute(Unit.Default);
            Assert.Single(starter.Extrusions);
            timeline.SelectTrackItem(SplineTimelineTrackKind.Extrusions, 0);
            timeline.FromModeIndex.Value = 1;
            timeline.FromAnchorId.Value = "s_end";
            timeline.FromOffset.Value = -5m;

            // Delete Curves[0] which defines "s_end" (resolved s = 50.0m):
            // Dependent Prop (s_end+5 => 55), PropSkip.From (s_end-10 => 40), Extrusion (s_end-5 => 45)
            // MUST expand to Absolute distances automatically!
            timeline.SelectTrackItem(SplineTimelineTrackKind.Curves, 0);
            timeline.RemoveSelectedItemCommand.Execute(Unit.Default);

            Assert.Single(starter.Curves);
            Assert.False(starter.Props[0].From.IsRelative);
            Assert.Equal(55.0, starter.Props[0].From.Offset, 6);
            Assert.False(starter.PropSkips[0].From!.Value.IsRelative);
            Assert.Equal(40.0, starter.PropSkips[0].From!.Value.Offset, 6);
            Assert.True(starter.PropSkips[0].To!.Value.IsRelative); // s_curve2 still exists
            Assert.False(starter.Extrusions[0].From.IsRelative);
            Assert.Equal(45.0, starter.Extrusions[0].From.Offset, 6);

            // Undo must restore BOTH Curves[0] AND all original anchor references!
            Assert.True(ws.Undo());
            Assert.Equal(2, starter.Curves.Count);
            Assert.Equal("s_end", starter.Curves[0].OutputAnchorId);
            Assert.True(starter.Props[0].From.IsRelative);
            Assert.Equal("s_end", starter.Props[0].From.AnchorId);
            Assert.Equal(5.0, starter.Props[0].From.Offset, 6);
            Assert.True(starter.Extrusions[0].From.IsRelative);
            Assert.Equal(-5.0, starter.Extrusions[0].From.Offset, 6);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Inspector_SceneHierarchy_AndViewportViewModels_SyncSplinesAndOverlayFlags()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "SyncTest", "Sync", "Desc");
            WorldWorkspaceService ws = shell.Workspace;
            SceneHierarchyToolViewModel hierarchy = shell.SceneHierarchyTool;
            InspectorToolViewModel inspector = shell.InspectorTool;
            ViewportDocumentViewModel viewport = shell.ViewportDocument;

            // Overlay toggles (Network & Anchors)
            Assert.True(viewport.ShowNetworkLayer.Value);
            Assert.True(viewport.ShowAnchorsLayer.Value);
            viewport.ShowNetworkLayer.Value = false;
            Assert.False(ws.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Network));
            viewport.ShowAnchorsLayer.Value = false;
            Assert.False(ws.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Anchors));
            viewport.ShowNetworkLayer.Value = true;
            viewport.ShowAnchorsLayer.Value = true;
            Assert.True(ws.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Network | ViewportOverlayFlags.Anchors));

            // Tool mode commands update ActiveToolHint
            viewport.PlaceSplineToolCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.PlaceSpline, ws.ActiveToolMode.Value);
            Assert.Contains("スプライン配置", viewport.ActiveToolHint.Value, StringComparison.Ordinal);

            viewport.ConnectPortToolCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.ConnectPort, ws.ActiveToolMode.Value);
            Assert.Contains("ポート接続", viewport.ActiveToolHint.Value, StringComparison.Ordinal);

            // Hierarchy selection sync with Workspace & mutual exclusion with Prop
            SceneItemEntry? splineRow = hierarchy.Items.FirstOrDefault(i => i.SplineId == "spline_001");
            Assert.NotNull(splineRow);
            hierarchy.SelectedItem.Value = splineRow;
            Assert.Equal("spline_001", ws.SelectedSplineId.Value);
            Assert.Null(ws.SelectedPropId.Value);

            Assert.True(inspector.HasSelectedSpline.Value);
            Assert.False(inspector.HasSelectedProp.Value);
            Assert.True(inspector.HasAnySelection.Value);

            // Inspector ChunkPose normalization & PreBuildUserCode
            inspector.SplineLocalX.Value = 260m; // crosses 250m chunk boundary -> Chunk X=1, Local X=10
            Assert.Equal(1, project.Placement.Splines[0].Origin.Chunk.X);
            Assert.Equal(10.0, project.Placement.Splines[0].Origin.Pose.X, 6);

            inspector.SplinePreBuildUserCode.Value = "// pre-build user hook";
            Assert.Equal("// pre-build user hook", project.Placement.Splines[0].PreBuildUserCode);

            // Extend IntoSpline from Inspector
            inspector.ExtendSplineFromOutletCommand.Execute(Unit.Default);
            Assert.Equal(2, project.Placement.Splines.Count);
            Assert.Equal("spline_002", ws.SelectedSplineId.Value);
            Assert.Equal(SplineOriginKind.FromSplineOutlet, project.Placement.Splines[1].Origin.Kind);
            Assert.False(inspector.IsSplineChunkPoseOrigin.Value);

            // Register a model in Models.txt then place a prop -> clears SelectedSplineId (mutual exclusion)
            project.ModelLists["Models.txt"].Entries.Add(new Core.Models.Assets.ModelListEntry
            {
                Key = "TestPole",
                ModelPath = "Models/TestPole.glb",
                Collider = Core.Models.Assets.ModelColliderSpec.NonCollision(),
            });
            ws.ProjectRevision.Value++;
            ws.PlacementModelKey.Value = "TestPole";

            viewport.PlacePropAtCameraCommand.Execute(Unit.Default);
            Assert.NotNull(ws.SelectedPropId.Value);
            Assert.Null(ws.SelectedSplineId.Value);
            Assert.True(inspector.HasSelectedProp.Value);
            Assert.False(inspector.HasSelectedSpline.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ViewportPicking_PicksNearestSplineCenterline_AndNetworkPort()
    {
        FreeCameraController cam = new();
        cam.Locate(new WorldPoseDegreesModel(
            new ChunkIndexModel(0, 0),
            new PoseDegreesModel(0.0, 0.65, -5.0, 0.0, 0.0, 0.0)));
        ViewContext viewCtx = cam.Camera.CreateViewContext(new SizeI(1280, 720));

        // Ray straight along +Z at Y=0.65m passes right over centerline X=0, Y=0, Z=0..50 and through raised port marker (0, 0.65, 20)
        ViewportRay centerRay = ViewportPickingAndGizmoService.CreateRay(640f, 360f, 1280, 720, viewCtx);

        ReadOnlySpan<SplinePickCandidate> splineCandidatesSpan =
        [
            new SplinePickCandidate(
                "spline_far",
                ChunkIndex.Zero,
                new List<Vector3> { new(15f, 0f, 0f), new(15f, 0f, 50f) }),
            new SplinePickCandidate(
                "spline_near",
                ChunkIndex.Zero,
                new List<Vector3> { new(0f, 0f, 0f), new(0f, 0f, 50f) }),
        ];

        List<SplinePickCandidate> splineCandidates = new();
        foreach (SplinePickCandidate item in splineCandidatesSpan)
        {
            splineCandidates.Add(item);
        }

        SplinePickHit? splineHit = ViewportPickingAndGizmoService.PickNearestSpline(centerRay, splineCandidates);
        Assert.NotNull(splineHit);
        Assert.Equal("spline_near", splineHit!.Value.SplineId);

        // Port at (0, 0, 20) matches raised marker center (0, 0.65, 20) right along center ray
        List<PortPickCandidate> portCandidates =
        [
            new(new PortReferenceModel("spline_far", PortTargetKind.SplineOutlet), ChunkIndex.Zero, new(20f, 0f, 20f), false),
            new(new PortReferenceModel("spline_near", PortTargetKind.SplineOutlet), ChunkIndex.Zero, new(0f, 0f, 20f), false),
        ];

        PortPickHit? portHit = ViewportPickingAndGizmoService.PickNearestPort(centerRay, portCandidates);
        Assert.NotNull(portHit);
        Assert.Equal("spline_near", portHit!.Value.PortRef.ElementId);
    }

    [Fact]
    public void AvaloniaUi_InstantiatesBindsMeasuresAndRendersPhase4ViewsCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Exception? capturedException = null;
        string tempDir = CreateTempDirectory();
        try
        {
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    using MainWindowViewModel shell = new();
                    WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "UiProbeWorld", "UI Probe", "Desc");
                    shell.Workspace.AddNewSplineTemplate("Spline2", "Templates/Splines/Spline2.csx");
                    shell.Workspace.SelectSpline(project.Placement.Splines[0].Id);

                    shell.TimelineTool.AddGradientCommand.Execute(Unit.Default);
                    shell.TimelineTool.AddCantCommand.Execute(Unit.Default);
                    shell.TimelineTool.AddPropCommand.Execute(Unit.Default);
                    shell.TimelineTool.AddPropSkipCommand.Execute(Unit.Default);
                    shell.TimelineTool.AddExtrusionCommand.Execute(Unit.Default);
                    shell.TimelineTool.AddExtrusionSkipCommand.Execute(Unit.Default);

                    AssetBrowserEntry? tplEntry = shell.AssetBrowserTool.SplineTemplateItems.FirstOrDefault();
                    Assert.NotNull(tplEntry);
                    using SplineTemplateEditorDocumentViewModel? tplVm =
                        shell.OpenAsset(tplEntry!) as SplineTemplateEditorDocumentViewModel;
                    Assert.NotNull(tplVm);

                    ViewLocator locator = new();
                    Control? tplView = locator.Build(tplVm);
                    Control? timelineView = locator.Build(shell.TimelineTool);
                    Control? inspectorView = locator.Build(shell.InspectorTool);
                    Control? viewportView = locator.Build(shell.ViewportDocument);
                    Control? hierarchyView = locator.Build(shell.SceneHierarchyTool);

                    Assert.IsType<SplineTemplateEditorView>(tplView);
                    Assert.IsType<SplineTimelineView>(timelineView);
                    Assert.IsType<InspectorView>(inspectorView);
                    Assert.IsType<ViewportDocumentView>(viewportView);
                    Assert.IsType<SceneHierarchyView>(hierarchyView);

                    MeasureAndArrange(tplView!, tplVm!, 900, 620);
                    MeasureAndArrange(timelineView!, shell.TimelineTool, 900, 320);
                    MeasureAndArrange(inspectorView!, shell.InspectorTool, 360, 760);
                    MeasureAndArrange(viewportView!, shell.ViewportDocument, 960, 600);
                    MeasureAndArrange(hierarchyView!, shell.SceneHierarchyTool, 320, 480);

                    // Also render SplineTimelineCanvas directly into an offscreen bitmap to exercise DrawingContext code
                    SplineTimelineCanvas canvas = new()
                    {
                        DataContext = shell.TimelineTool,
                    };
                    canvas.Measure(new Avalonia.Size(800, 300));
                    canvas.Arrange(new Avalonia.Rect(0, 0, canvas.DesiredSize.Width, canvas.DesiredSize.Height));
                    Assert.True(400.0 <= canvas.Bounds.Width);
                    Assert.True(180.0 <= canvas.Bounds.Height);

                    using RenderTargetBitmap rtb = new(
                        new PixelSize(int.Max(1, (int)canvas.Bounds.Width), int.Max(1, (int)canvas.Bounds.Height)),
                        new Avalonia.Vector(96, 96));
                    rtb.Render(canvas);

                    MainWindow mainWindow = new()
                    {
                        DataContext = shell,
                    };
                    mainWindow.Measure(new Avalonia.Size(1540, 920));
                    mainWindow.Arrange(new Avalonia.Rect(0, 0, 1540, 920));
                    Assert.True(1024.0 <= mainWindow.Bounds.Width);
                    Assert.True(640.0 <= mainWindow.Bounds.Height);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
            });

            Assert.Null(capturedException);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static void MeasureAndArrange(Control control, object dataContext, double width, double height)
    {
        control.DataContext = dataContext;
        control.Measure(new Avalonia.Size(width, height));
        control.Arrange(new Avalonia.Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Assert.True(0.0 < control.Bounds.Width);
        Assert.True(0.0 < control.Bounds.Height);
    }

    private sealed class DummyGraphicsClient : IGraphicsClient
    {
        public nint Hwnd => 0;
        public IDXGISwapChain1 SwapChain => null!;
        public TransportX.Rendering.Backend.RenderSurface? Surface => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void EditorWorld_RebuildIncrementalSyncAndDispose_DoesNotDoubleDisposeBepuStatics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "BepuDisposeTest", "Bepu Dispose", "Desc");

            using GraphicsHost graphicsHost = new();
            using DummyGraphicsClient graphicsClient = new();
            using PhysicsHost physicsHost = PhysicsHost.Create();
            using ViewportWindowAdapter windowAdapter = new(0, 800, 600);
            using DummyAudioHost audioHost = new();
            using DummyInputHost inputHost = new();
            DummyAudioClient audioClient = new();
            DummyInputClient inputClient = new();
            FreeCameraController cam = new();
            EditorErrorCollector errorCollector = new();
            PluginLoadContext appContext = new(typeof(EditorWorld).Assembly.Location);

            TransportX.Platform platform = new()
            {
                Window = windowAdapter,
                Input = inputHost.SilkContext,
            };

            WorldBuilder builder = new(new PreviewTestWorldInfo
            {
                InfoPath = Path.Combine(tempDir, "PreviewTest.xml"),
                Path = typeof(EditorWorld).Assembly.Location,
            })
            {
                Platform = platform,
                GraphicsHost = graphicsHost,
                GraphicsClient = graphicsClient,
                AudioHost = audioHost,
                AudioClient = audioClient,
                InputHost = inputHost,
                InputClient = inputClient,
                PhysicsHost = physicsHost,
                Options = new WorldOptions
                {
                    SimulationChunkCount = 2,
                    IsDebugMode = false,
                },
                ErrorCollector = errorCollector,
                AppContext = appContext,
                TimeManager = new TimeManager(),
                Camera = cam.Camera,
            };

            using EditorWorld world = new(builder);
            world.RebuildFromProject(project);
            Assert.Empty(errorCollector.Errors);
            Assert.NotEmpty(world.Splines);
            Assert.True(0 < physicsHost.Simulation.Statics.Count);

            // Mutate spline network incrementally (add IntoSpline chained spline + sync, then undo + sync)
            SplinePlacementNode? chained = shell.Workspace.AddChainedSplineFromOutlet("spline_001", "Spline1", 30.0);
            Assert.NotNull(chained);
            world.SyncFromProjectIncremental(project);
            Assert.Equal(2, world.Splines.Count);
            Assert.Empty(errorCollector.Errors);

            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Default,
                null,
                ChunkIndex.Zero);

            // Verify small radius curve uses adaptive sampling (not coarse 4m steps)
            SplinePlacementNode? smallCurveSpline = project.Placement.Splines.FirstOrDefault(s => string.Equals(s.Id, "spline_001", StringComparison.Ordinal));
            Assert.NotNull(smallCurveSpline);
            smallCurveSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.ByRadius, 3.0, 9.42, "small_r_end"));
            world.SyncFromProjectIncremental(project);
            SplineRenderEntry? smallCurveEntry = world.Splines.FirstOrDefault(e => string.Equals(e.Node.Id, "spline_001", StringComparison.Ordinal));
            Assert.NotNull(smallCurveEntry);
            Assert.True(12 < smallCurveEntry.PrimaryChunkCenterlinePoints.Count);

            Assert.True(shell.Workspace.Undo());
            world.SyncFromProjectIncremental(project);
            Assert.Single(world.Splines);

            // Verify Network toggle controls VisualLayers.Network independently of ChunkGrid & spline models,
            // and Normal toggle controls BackgroundModels, Chunk.Models, AND NetworkElement.Models (extrusions/spline props).
            shell.Workspace.AddBackgroundModel("sample_bg");
            world.SyncFromProjectIncremental(project);
            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Normal | ViewportOverlayFlags.Network | ViewportOverlayFlags.ChunkGrid,
                null,
                ChunkIndex.Zero);
            Assert.True(0 < world.Chunks.Sum(c => c.Network.Count));
            Assert.True(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Network));
            Assert.True(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Traffic));

            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Normal | ViewportOverlayFlags.ChunkGrid | ViewportOverlayFlags.Gizmos,
                null,
                ChunkIndex.Zero);
            Assert.True(0 < world.Chunks.Sum(c => c.Network.Count));
            Assert.False(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Network));
            Assert.True(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Normal));
            Assert.True(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Traffic));
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SplineHighlight");
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SelectedPortMarker");
            Assert.DoesNotContain(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "PortMarker");

            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Normal | ViewportOverlayFlags.ChunkGrid | ViewportOverlayFlags.Network,
                null,
                ChunkIndex.Zero);
            Assert.DoesNotContain(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SplineHighlight");
            Assert.DoesNotContain(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SelectedPortMarker");
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "PortMarker");
            foreach (Chunk chunk in world.Chunks)
            {
                foreach (TransportX.Network.NetworkElement element in chunk.Network)
                {
                    foreach (TransformedModel model in element.Models)
                    {
                        Assert.True(model.IsVisible);
                    }
                }
            }

            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.ChunkGrid,
                null,
                ChunkIndex.Zero);
            Assert.True(0 < world.Chunks.Sum(c => c.Network.Count));
            Assert.False(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Network));
            foreach (TransformedModel bg in world.BackgroundModels)
            {
                Assert.False(bg.IsVisible);
            }

            foreach (Chunk chunk in world.Chunks)
            {
                foreach (TransportX.Network.NetworkElement element in chunk.Network)
                {
                    foreach (TransformedModel model in element.Models)
                    {
                        Assert.False(model.IsVisible);
                    }
                }
            }

            world.UpdateOverlays(
                null,
                "spline_001",
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Default,
                null,
                ChunkIndex.Zero);
            Assert.True(0 < world.Chunks.Sum(c => c.Network.Count));
            Assert.True(cam.Camera.VisibleLayers.HasFlag(ICamera.VisualLayers.Network));
            foreach (TransformedModel bg in world.BackgroundModels)
            {
                Assert.True(bg.IsVisible);
            }

            foreach (Chunk chunk in world.Chunks)
            {
                foreach (TransportX.Network.NetworkElement element in chunk.Network)
                {
                    foreach (TransformedModel model in element.Models)
                    {
                        Assert.True(model.IsVisible);
                    }
                }
            }

            // Disposing EditorWorld (even twice, matching tab switch / DestroyNativeControlCore) MUST NOT throw ArgumentOutOfRangeException
            world.Dispose();
            world.Dispose();
            appContext.Unload();

            Assert.Equal(0, physicsHost.Simulation.Statics.Count);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void TimelineTool_CurveKindSwitch_UpdatesValueVisibility_AndRetainsSelectedBlock()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "TimelineCurveTest", "Timeline Curve", "Desc");
            SplinePlacementNode? spline = project.Placement.Splines.FirstOrDefault();
            Assert.NotNull(spline);

            shell.Workspace.SelectedSplineId.Value = spline.Id;
            TimelineToolViewModel timeline = shell.TimelineTool;

            SplineTimelineBlockItem? firstCurveBlock = timeline.TimelineBlocks.FirstOrDefault(b => b.Track == SplineTimelineTrackKind.Curves);
            Assert.NotNull(firstCurveBlock);
            timeline.SelectedBlock.Value = firstCurveBlock;

            timeline.CurveKind.Value = SplineCurveSegmentKind.Straight;
            Assert.False(timeline.CurveHasValueParameter.Value);

            timeline.CurveKind.Value = SplineCurveSegmentKind.ByRadius;
            Assert.True(timeline.CurveHasValueParameter.Value);
            Assert.Contains("半径", timeline.CurveValueLabel.Value, StringComparison.Ordinal);
            Assert.NotNull(timeline.SelectedBlock.Value);

            timeline.AddCurveCommand.Execute(Unit.Default);
            Assert.NotNull(timeline.SelectedBlock.Value);
            Assert.Equal(SplineTimelineTrackKind.Curves, timeline.SelectedBlock.Value.Track);

            // Gradient segment hides Delta when Constant, shows with unit label on transitions
            timeline.AddGradientCommand.Execute(Unit.Default);
            Assert.Equal(SplineTimelineTrackKind.Gradients, timeline.SelectedBlock.Value?.Track);
            timeline.GradientKind.Value = SplineGradientSegmentKind.Constant;
            Assert.False(timeline.GradientHasDeltaParameter.Value);

            timeline.GradientKind.Value = SplineGradientSegmentKind.TransitionByPermil;
            Assert.True(timeline.GradientHasDeltaParameter.Value);
            Assert.Contains("‰", timeline.GradientDeltaLabel.Value, StringComparison.Ordinal);

            timeline.GradientKind.Value = SplineGradientSegmentKind.Constant;
            Assert.False(timeline.GradientHasDeltaParameter.Value);

            // Cant segment hides Target and Gauge on Constant, shows Target only on angle/%, shows Gauge only on Height
            timeline.AddCantCommand.Execute(Unit.Default);
            Assert.Equal(SplineTimelineTrackKind.Cants, timeline.SelectedBlock.Value?.Track);
            timeline.CantKind.Value = SplineCantSegmentKind.Constant;
            Assert.False(timeline.CantHasTargetParameter.Value);
            Assert.False(timeline.CantHasGaugeParameter.Value);

            timeline.CantKind.Value = SplineCantSegmentKind.TransitionToDegree;
            Assert.True(timeline.CantHasTargetParameter.Value);
            Assert.False(timeline.CantHasGaugeParameter.Value);
            Assert.Contains("°", timeline.CantTargetLabel.Value, StringComparison.Ordinal);

            timeline.CantKind.Value = SplineCantSegmentKind.TransitionToHeight;
            Assert.True(timeline.CantHasTargetParameter.Value);
            Assert.True(timeline.CantHasGaugeParameter.Value);
            Assert.Contains("カント量", timeline.CantTargetLabel.Value, StringComparison.Ordinal);

            timeline.CantKind.Value = SplineCantSegmentKind.Constant;
            Assert.False(timeline.CantHasTargetParameter.Value);
            Assert.False(timeline.CantHasGaugeParameter.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void InspectorTool_CurveAddAndPoseEdit_PreservesTemplateKey_WithoutDuplicates()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "InspectorSyncTest", "Inspector Sync", "Desc");
            SplinePlacementNode? spline = project.Placement.Splines.FirstOrDefault();
            Assert.NotNull(spline);
            string expectedTemplate = spline.TemplateKey ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(expectedTemplate));

            shell.Workspace.SelectedSplineId.Value = spline.Id;
            InspectorToolViewModel inspector = shell.InspectorTool;

            Assert.Equal(expectedTemplate, inspector.SplineTemplateKey.Value);
            Assert.Equal(1, inspector.AvailableSplineTemplateKeys.Count(k => string.Equals(k, expectedTemplate, StringComparison.Ordinal)));

            shell.TimelineTool.AddCurveCommand.Execute(Unit.Default);
            Assert.Equal(expectedTemplate, spline.TemplateKey);
            Assert.Equal(expectedTemplate, inspector.SplineTemplateKey.Value);
            Assert.Equal(1, inspector.AvailableSplineTemplateKeys.Count(k => string.Equals(k, expectedTemplate, StringComparison.Ordinal)));

            inspector.SplineLocalX.Value = 25m;
            Assert.Equal(expectedTemplate, spline.TemplateKey);
            Assert.Equal(expectedTemplate, inspector.SplineTemplateKey.Value);
            Assert.Equal(1, inspector.AvailableSplineTemplateKeys.Count(k => string.Equals(k, expectedTemplate, StringComparison.Ordinal)));

            ModelListEditorDocumentViewModel? modelsDoc = shell.OpenDefaultModelList();
            Assert.NotNull(modelsDoc);
            modelsDoc.AddEntryCommand.Execute(Unit.Default);
            inspector.AddNewBackgroundCommand.Execute(Unit.Default);
            Assert.NotEmpty(inspector.AvailableModelKeys);
            Assert.False(string.IsNullOrWhiteSpace(inspector.SelectedBackgroundModelKey.Value));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private sealed class PreviewTestWorldInfo : IWorldInfo
    {
        public string InfoPath { get; init; } = string.Empty;
        public string Title { get; init; } = "Test";
        public string Description { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string? Identifier { get; init; }
        public string AppPath { get; init; } = string.Empty;
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    }
}
