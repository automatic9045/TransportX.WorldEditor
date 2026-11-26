using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
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
using TransportX.WorldEditor.Serialization.Csx;
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
using Xunit;

namespace TransportX.WorldEditor.Tests;

public sealed class Phase5JunctionAndCurveTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p5_{Guid.NewGuid():N}");
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
    public void JunctionAnchorUtilities_ResolvesPathAndCurveAnchors_AndValidatesPortCompatibilityAndCycles()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AnchorUtilWorld", "Anchor Util", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            // Add symmetric single-lane layout for relay & incompatible template check
            LaneLayoutDocument relayLayout = new()
            {
                Key = "SingleRelay",
                RelativePath = "LaneLayouts/SingleRelay.xml",
            };
            relayLayout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.InOut, 0f, 0f, 1.75f, 1.75f));
            project.LaneLayouts["SingleRelay"] = relayLayout;
            project.Init.LaneLayouts.Add(new ResourceLoadReference("SingleRelay", relayLayout.RelativePath));

            Assert.True(ws.AddNewJunctionTemplate("JuncAlpha", "Templates/Junctions/JuncAlpha.csx"));
            Assert.True(ws.MutateJunctionTemplate("JuncAlpha", "Configure anchors and curve", tpl =>
            {
                tpl.Relays.Add(new JunctionRelayDefinition(
                    "RIn",
                    "ROut",
                    "SingleRelay",
                    new PoseDegreesModel(2.0, 0.0, 12.0, 0.0, 0.0, 0.0)));

                tpl.Paths.Clear();
                JunctionPathTemplateModel mainPath = new()
                {
                    Key = "SN_Mid",
                    FromPortKey = "S",
                    FromPinIndex = 0,
                    ToPortKey = "N",
                    ToPinIndex = 1,
                    TrafficDensityFactor = 0.75,
                };
                mainPath.Segments.Add(new JunctionCurveSegment(
                    JunctionCurveSegmentKind.StraightTo,
                    new PoseDegreesModel(0.0, 0.0, 10.0, 0.0, 0.0, 0.0),
                    0.5,
                    "s_mid"));
                mainPath.Segments.Add(new JunctionCurveSegment(
                    JunctionCurveSegmentKind.BezierToEnd,
                    PoseDegreesModel.Identity,
                    0.5,
                    "s_end"));
                mainPath.WidthSegments.Add(new JunctionWidthSegment(
                    JunctionWidthSegmentKind.Constant,
                    2.0,
                    2.0,
                    AnchoredDistance.RelativeTo("s_mid", 1.5),
                    "s_w1"));
                mainPath.Extrusions.Add(new CurveExtrusionPlacementRule
                {
                    Key = "Pave",
                    CrossSectionKey = "Road1L",
                    From = AnchoredDistance.RelativeTo("s_mid", 2.0),
                    To = AnchoredDistance.RelativeTo("s_w1", 0.5),
                });
                tpl.Paths.Add(mainPath);

                JunctionCurveTemplateModel auxCurve = new()
                {
                    Key = "Crosswalk_S",
                    Origin = JunctionCurveOriginModel.FromPortPinWithOffset("S", 0, PoseDegreesModel.FromTranslation(0.0, 0.0, 1.0)),
                };
                auxCurve.Segments.Add(new JunctionCurveSegment(
                    JunctionCurveSegmentKind.StraightTo,
                    new PoseDegreesModel(8.0, 0.0, 1.0, 0.0, 90.0, 0.0),
                    0.5,
                    "c_mid"));
                auxCurve.Extrusions.Add(new CurveExtrusionPlacementRule
                {
                    Key = "Stripe",
                    CrossSectionKey = "Road1L",
                    From = AnchoredDistance.RelativeTo("c_mid", -3.0),
                    To = AnchoredDistance.RelativeTo("c_mid", 0.0),
                });
                tpl.Curves.Add(auxCurve);
            }));

            JunctionTemplateDocument tplDoc = project.JunctionTemplates["JuncAlpha"];
            IReadOnlyList<AvailableJunctionPortInfo> portInfos = JunctionAnchorUtilities.EnumerateAvailablePortKeys(tplDoc);
            Assert.Contains(portInfos, p => p.PortKey == "S" && !p.IsRelay);
            Assert.Contains(portInfos, p => p.PortKey == "N" && !p.IsRelay);
            Assert.Contains(portInfos, p => p.PortKey == "RIn" && p.IsRelay && !p.IsRelayOutput);
            Assert.Contains(portInfos, p => p.PortKey == "ROut" && p.IsRelay && p.IsRelayOutput);

            JunctionSampledCurveResult sampledPath = JunctionAnchorUtilities.SamplePathGeometry(tplDoc, project.LaneLayouts, tplDoc.Paths[0]);
            Assert.True(4 < sampledPath.Points.Count);
            Assert.True(20.0 < sampledPath.TotalLength);

            IReadOnlyList<ResolvedAnchorInfo> pathAnchors = JunctionAnchorUtilities.CollectPathAnchors(tplDoc, project.LaneLayouts, tplDoc.Paths[0]);
            ResolvedAnchorInfo sMid = Assert.Single(pathAnchors, a => a.AnchorId == "s_mid");
            ResolvedAnchorInfo sW1 = Assert.Single(pathAnchors, a => a.AnchorId == "s_w1");
            Assert.True(0.0 < sMid.ResolvedS);
            Assert.Equal(sMid.ResolvedS + 1.5, sW1.ResolvedS, 4);

            JunctionSampledCurveResult sampledCurve = JunctionAnchorUtilities.SampleCurveGeometry(tplDoc, project.LaneLayouts, tplDoc.Curves[0]);
            Assert.True(2 <= sampledCurve.Points.Count);
            IReadOnlyList<ResolvedAnchorInfo> curveAnchors = JunctionAnchorUtilities.CollectCurveAnchors(tplDoc, project.LaneLayouts, tplDoc.Curves[0]);
            ResolvedAnchorInfo cMid = Assert.Single(curveAnchors, a => a.AnchorId == "c_mid");
            Assert.True(0.0 < cMid.ResolvedS);

            // Expand anchors into absolute offsets
            JunctionPathTemplateModel clonedPath = JunctionAnchorUtilities.DeepCloneJunctionTemplate(tplDoc).Paths[0];
            int pathExpanded = JunctionAnchorUtilities.ExpandPathAnchorReferencesToAbsolute(clonedPath, "s_mid", sMid.ResolvedS);
            Assert.Equal(2, pathExpanded);
            Assert.False(clonedPath.WidthSegments[0].Length.IsRelative);
            Assert.False(clonedPath.Extrusions[0].From.IsRelative);
            Assert.Equal(sMid.ResolvedS + 2.0, clonedPath.Extrusions[0].From.Offset, 5);
            Assert.True(clonedPath.Extrusions[0].To!.Value.IsRelative); // still relative to s_w1

            JunctionCurveTemplateModel clonedCurve = JunctionAnchorUtilities.DeepCloneJunctionTemplate(tplDoc).Curves[0];
            int curveExpanded = JunctionAnchorUtilities.ExpandCurveAnchorReferencesToAbsolute(clonedCurve, "c_mid", cMid.ResolvedS);
            Assert.Equal(2, curveExpanded);
            Assert.False(clonedCurve.Extrusions[0].From.IsRelative);
            Assert.False(clonedCurve.Extrusions[0].To!.Value.IsRelative);

            // Compatibility checks
            SplinePlacementNode firstSpline = project.Placement.Splines[0];
            IReadOnlyList<string> compatFromSpline = JunctionAnchorUtilities.FindCompatibleJunctionPortsForSplineOutlet(
                project,
                firstSpline,
                "JuncAlpha");
            Assert.Contains("S", compatFromSpline);
            Assert.Contains("N", compatFromSpline);

            // Chaining onto S marks S occupied, leaving N free
            JunctionPlacementNode? j1 = ws.AddChainedJunctionFromSplineOutlet(firstSpline.Id, "JuncAlpha", "S");
            Assert.NotNull(j1);
            Assert.True(JunctionAnchorUtilities.IsJunctionPortOccupied(project.Placement, j1.Id, "S"));
            Assert.False(JunctionAnchorUtilities.IsJunctionPortOccupied(project.Placement, j1.Id, "N"));

            // Spline Outlet already used by j1; adding ConnectBezier to incompatible layout fails; explicit compat works for N
            IReadOnlyList<string> compatFromJ1N = JunctionAnchorUtilities.FindCompatibleJunctionPortsForJunctionPort(
                project,
                j1,
                "N",
                "JuncAlpha");
            Assert.Contains("S", compatFromJ1N);

            IReadOnlyList<string> compatSplinesFromJ1N = JunctionAnchorUtilities.FindCompatibleSplineTemplatesForJunctionPort(
                project,
                j1,
                "N");
            Assert.Contains("Spline1", compatSplinesFromJ1N);

            // Independent spline with opposite free ports appears as explicit connect target for j1.N
            SplinePlacementNode? otherSpline = ws.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(180.0, 0.0, 125.0)));
            Assert.NotNull(otherSpline);
            IReadOnlyList<CompatiblePortCandidate> explicitTargets = JunctionAnchorUtilities.FindCompatibleExplicitConnectTargets(
                project,
                new PortReferenceModel(j1.Id, PortTargetKind.JunctionPort, "N"));
            Assert.Contains(explicitTargets, t => t.PortRef.ElementId == otherSpline.Id);

            // Incompatible template layout is excluded
            LaneLayoutDocument narrowLayout = new()
            {
                Key = "NarrowWalk",
                RelativePath = "LaneLayouts/NarrowWalk.xml",
            };
            narrowLayout.Lanes.Add(new LaneDefinition("Pedestrians", LaneFlowDirections.InOut, 0f, 0f, 1.0f, 1.0f));
            project.LaneLayouts["NarrowWalk"] = narrowLayout;
            project.Init.LaneLayouts.Add(new ResourceLoadReference("NarrowWalk", narrowLayout.RelativePath));
            Assert.True(ws.AddNewSplineTemplate("WalkTpl", "Templates/Splines/WalkTpl.csx"));
            Assert.True(ws.MutateSplineTemplate("WalkTpl", "Narrow", t => t.OutletLayoutKey = "NarrowWalk"));
            compatSplinesFromJ1N = JunctionAnchorUtilities.FindCompatibleSplineTemplatesForJunctionPort(project, j1, "N");
            Assert.DoesNotContain("WalkTpl", compatSplinesFromJ1N);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void JunctionTemplateEditor_EditsPortsRelaysPathsWidthTrafficDensityCurvesPropsAndUserCode_WithUndoRedoDirtyTabAndRoundTrip()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "JuncEditorWorld", "Junc Editor", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            Assert.True(ws.CreateNewJunctionTemplate("CustomJunc", out string? key, out string? relPath));
            Assert.Equal("CustomJunc", key);
            Assert.Equal("Templates/Junctions/CustomJunc.csx", relPath);

            using JunctionTemplateEditorDocumentViewModel editor = new(ws, "CustomJunc");
            int initialPorts = editor.Ports.Count;
            Assert.True(2 <= initialPorts);

            // Port add + move from 2D canvas
            editor.AddPortCommand.Execute(Unit.Default);
            Assert.Equal(initialPorts + 1, editor.Ports.Count);
            editor.MovePortFromCanvas(initialPorts, 18.0, 12.0);
            JunctionTemplateDocument doc = project.JunctionTemplates["CustomJunc"];
            Assert.Equal(18.0, doc.Ports[initialPorts].Pose.X, 4);
            Assert.Equal(12.0, doc.Ports[initialPorts].Pose.Z, 4);

            // Relay add + move from 2D canvas
            editor.AddRelayCommand.Execute(Unit.Default);
            Assert.Single(editor.Relays);
            editor.MoveRelayFromCanvas(0, -2.5, 9.0);
            Assert.Equal(-2.5, doc.Relays[0].Pose.X, 4);
            Assert.Equal(9.0, doc.Relays[0].Pose.Z, 4);

            // Path add + segment + width + traffic density + extrusion + prop
            int initialPaths = editor.Paths.Count;
            editor.AddPathCommand.Execute(Unit.Default);
            Assert.Equal(initialPaths + 1, editor.Paths.Count);
            editor.AddPathSegmentCommand.Execute(Unit.Default);
            Assert.Equal(2, editor.PathSegments.Count);
            editor.AddPathWidthCommand.Execute(Unit.Default);
            Assert.Single(editor.PathWidths);
            editor.PathTrafficDensityFactor.Value = 0.65m;
            Assert.NotNull(doc.Paths[initialPaths].TrafficDensityFactor);
            Assert.Equal(0.65, doc.Paths[initialPaths].TrafficDensityFactor!.Value, 4);
            editor.AddPathExtrusionCommand.Execute(Unit.Default);
            Assert.Single(editor.PathExtrusions);
            editor.AddPathPropCommand.Execute(Unit.Default);
            Assert.Single(editor.PathProps);

            // Auxiliary curve (AddCurve) with PortPinRelative origin + segment + extrusion
            editor.AddCurveCommand.Execute(Unit.Default);
            Assert.Single(editor.Curves);
            editor.CurveOriginKind.Value = JunctionCurveOriginKind.PortPinRelative;
            editor.CurveOriginPortKey.Value = "S";
            editor.AddCurveSegmentCommand.Execute(Unit.Default);
            Assert.Equal(2, editor.CurveSegments.Count);
            editor.AddCurveExtrusionCommand.Execute(Unit.Default);
            Assert.Single(editor.CurveExtrusions);

            // Static prop + user code
            editor.AddStaticPropCommand.Execute(Unit.Default);
            Assert.Single(editor.StaticProps);
            editor.UserCode.Value = "// Custom junction logic\n";

            Assert.EndsWith(" *", editor.Title, StringComparison.Ordinal);

            // Undo / Redo check on UserCode
            Assert.True(ws.Undo());
            Assert.True(string.IsNullOrEmpty(doc.UserCode));
            Assert.True(ws.Redo());
            Assert.Contains("Custom junction logic", doc.UserCode, StringComparison.Ordinal);

            // Save and reload via WorldProjectStorage
            shell.SaveAllCommand.Execute(Unit.Default);
            Assert.Empty(project.DirtyFiles);
            Assert.True(File.Exists(Path.Combine(project.WorldDirectory, "Templates", "Junctions", "CustomJunc.csx")));

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath!);
            Assert.DoesNotContain(reloaded.Diagnostics, d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
            Assert.True(reloaded.Project.JunctionTemplates.TryGetValue("CustomJunc", out JunctionTemplateDocument? roundTripped));
            Assert.NotNull(roundTripped);
            Assert.Equal(initialPorts + 1, roundTripped!.Ports.Count);
            Assert.Single(roundTripped.Relays);
            Assert.Equal(initialPaths + 1, roundTripped.Paths.Count);
            Assert.Equal(0.65, roundTripped.Paths[initialPaths].TrafficDensityFactor!.Value, 4);
            Assert.Single(roundTripped.Paths[initialPaths].WidthSegments);
            Assert.Single(roundTripped.Paths[initialPaths].Extrusions);
            Assert.Single(roundTripped.Paths[initialPaths].Props);
            Assert.Single(roundTripped.Curves);
            Assert.Equal(JunctionCurveOriginKind.PortPinRelative, roundTripped.Curves[0].Origin.Kind);
            Assert.Single(roundTripped.Curves[0].Extrusions);
            Assert.Single(roundTripped.Props);
            Assert.Contains("Custom junction logic", roundTripped.UserCode ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void WorldWorkspace_PlacesChainsAndConnectsJunctionsAndSplines_WithOverridesTrafficDensityAndValidationPass()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "JunctionNetworkWorld", "Junction Network", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            Assert.True(ws.CreateNewJunctionTemplate("MainCross", out string? tplKey, out _));
            Assert.Equal("MainCross", tplKey);

            // Add an auxiliary curve so CurveOverrides can target it
            Assert.True(ws.MutateJunctionTemplate("MainCross", "Add Aux Curve", tpl =>
            {
                JunctionCurveTemplateModel c = new()
                {
                    Key = "Crosswalk1",
                    Origin = JunctionCurveOriginModel.Absolute(PoseDegreesModel.FromTranslation(-4.0, 0.0, 1.0)),
                };
                c.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, PoseDegreesModel.FromTranslation(4.0, 0.0, 1.0)));
                tpl.Curves.Add(c);
            }));

            SplinePlacementNode firstSpline = project.Placement.Splines[0];

            // 1. IntoJunction from first spline outlet
            JunctionPlacementNode? j1 = ws.AddChainedJunctionFromSplineOutlet(firstSpline.Id, "MainCross", "S");
            Assert.NotNull(j1);
            Assert.Equal(JunctionOriginKind.FromSplineOutlet, j1.Origin.Kind);
            Assert.Equal(j1.Id, ws.SelectedJunctionId.Value);

            // Sync in SceneHierarchyToolViewModel
            Assert.Contains(shell.SceneHierarchyTool.Items, item => item.JunctionId == j1.Id);
            Assert.NotNull(shell.SceneHierarchyTool.SelectedItem.Value);
            Assert.Equal(j1.Id, shell.SceneHierarchyTool.SelectedItem.Value!.JunctionId);

            // 2. FromJunction outgoing spline from j1.N
            SplinePlacementNode? outSpline = ws.AddChainedSplineFromJunctionPort(j1.Id, "N", "Spline1", 40.0);
            Assert.NotNull(outSpline);
            Assert.Equal(SplineOriginKind.FromJunctionPort, outSpline.Origin.Kind);
            Assert.Equal(j1.Id, outSpline.Origin.SourceNodeId);
            Assert.Equal("N", outSpline.Origin.SourcePortKey);

            // 3. Standalone ChunkPose junction + move + rotate + explicit Network.Connect from a new free junction
            JunctionPlacementNode? jFree = ws.AddJunctionAtChunkPose(
                "MainCross",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(180.0, 0.0, 125.0)));
            Assert.NotNull(jFree);
            Assert.True(ws.MoveJunctionChunkPose(
                jFree.Id,
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, new PoseDegreesModel(190.0, 0.0, 130.0, 0.0, 45.0, 0.0))));
            Assert.Equal(190.0, jFree.Origin.Pose.X, 4);
            Assert.Equal(45.0, jFree.Origin.Pose.RotationY, 4);

            // Chain j2 from jFree.N (FromJunctionPort)
            JunctionPlacementNode? j2 = ws.AddChainedJunctionFromJunctionPort(jFree.Id, "N", "MainCross", "S");
            Assert.NotNull(j2);
            Assert.Equal(JunctionOriginKind.FromJunctionPort, j2.Origin.Kind);

            // Explicit Network.Connect(outSpline.Outlet, j2.N)
            Assert.True(ws.AddExplicitPortConnection(
                new PortReferenceModel(outSpline.Id, PortTargetKind.SplineOutlet),
                new PortReferenceModel(j2.Id, PortTargetKind.JunctionPort, "N")));
            Assert.Single(project.Placement.Connections);
            Assert.Single(shell.InspectorTool.ExplicitConnections);

            // Register a valid model entry in Models.txt so static prop selection & WorldValidator both succeed
            ModelListEditorDocumentViewModel? modelsDoc = shell.OpenDefaultModelList();
            Assert.NotNull(modelsDoc);
            modelsDoc!.AddEntryCommand.Execute(Unit.Default);

            // Inspector mutations on j1: PathOverride, CurveOverride, StaticProp, PreBuildUserCode
            ws.SelectJunction(j1.Id);
            Assert.True(shell.InspectorTool.HasSelectedJunction.Value);
            shell.InspectorTool.JunctionPathTrafficDensity.Value = 0.4m;
            shell.InspectorTool.ApplyJunctionPathOverrideCommand.Execute(Unit.Default);
            Assert.Single(j1.PathOverrides);
            Assert.Equal(0.4, j1.PathOverrides[0].TrafficDensityFactor!.Value, 4);

            shell.InspectorTool.AddJunctionCurveExtrusionCommand.Execute(Unit.Default);
            Assert.Single(j1.CurveOverrides);
            Assert.Single(j1.CurveOverrides[0].Extrusions);

            Assert.False(string.IsNullOrWhiteSpace(shell.InspectorTool.NewJunctionStaticPropModelKey.Value));
            shell.InspectorTool.AddJunctionStaticPropCommand.Execute(Unit.Default);
            Assert.Single(j1.Props);

            shell.InspectorTool.JunctionPreBuildUserCode.Value = "// Junction instance user code";
            Assert.Contains("Junction instance user code", j1.PreBuildUserCode ?? string.Empty, StringComparison.Ordinal);

            // Undo & Redo PreBuildUserCode
            Assert.True(ws.Undo());
            Assert.Empty(j1.PreBuildUserCode ?? string.Empty);
            Assert.True(ws.Redo());
            Assert.Contains("Junction instance user code", j1.PreBuildUserCode ?? string.Empty, StringComparison.Ordinal);

            // Save & validate zero errors + round-trip reload
            shell.SaveAllCommand.Execute(Unit.Default);
            Assert.Empty(project.DirtyFiles);
            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath!);
            Assert.DoesNotContain(reloaded.Diagnostics, d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
            WorldProject loadedProject = reloaded.Project;
            Assert.Equal(3, loadedProject.Placement.Junctions.Count);
            Assert.Single(loadedProject.Placement.Connections);
            JunctionPlacementNode loadedJ1 = loadedProject.Placement.Junctions.First(j => j.Id == j1.Id);
            Assert.Single(loadedJ1.PathOverrides);
            Assert.Single(loadedJ1.CurveOverrides);
            Assert.Single(loadedJ1.Props);

            IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(loadedProject);
            Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AssetBrowser_CreatesAndMultiImportsJunctionTemplates_AndOpensTab()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AssetBrowserJuncWorld", "Asset Browser Junc", "Desc");
            AssetBrowserToolViewModel browser = shell.AssetBrowserTool;

            // Switch to tab 6 ("ジャンクション")
            browser.SelectedTabIndex.Value = 6;
            Assert.True(browser.CanCreateForActiveTab.Value);
            Assert.True(browser.CanImportForActiveTab.Value);
            Assert.Contains("ジャンクション", browser.CreateButtonLabel.Value, StringComparison.Ordinal);

            // Create new junction template via command
            browser.NewResourceKey.Value = "CreatedJunc";
            browser.CreateActiveResourceCommand.Execute(Unit.Default);
            Assert.True(project.JunctionTemplates.ContainsKey("CreatedJunc"));
            Assert.Contains(browser.JunctionTemplateItems, e => e.Key == "CreatedJunc");

            // Prepare 2 standalone canonical junction .csx files outside project & multi-import
            string fileA = Path.Combine(externalDir, "ImportedA.csx");
            string fileB = Path.Combine(externalDir, "ImportedB.csx");
            JunctionTemplateDocument docA = new() { Key = "ImportedA" };
            docA.Ports.Add(new JunctionPortDefinition("S", "Layout1", new PoseDegreesModel(0.0, 0.0, 0.0, 0.0, 180.0, 0.0)));
            JunctionTemplateDocument docB = new() { Key = "ImportedB" };
            docB.Ports.Add(new JunctionPortDefinition("N", "Layout1", new PoseDegreesModel(0.0, 0.0, 20.0, 0.0, 0.0, 0.0)));
            File.WriteAllText(fileA, JunctionTemplateCsxSerializer.Generate(docA));
            File.WriteAllText(fileB, JunctionTemplateCsxSerializer.Generate(docB));

            int imported = browser.ImportExistingFromFiles(6, new string[] { fileA, fileB });
            Assert.Equal(2, imported);
            Assert.True(project.JunctionTemplates.ContainsKey("ImportedA"));
            Assert.True(project.JunctionTemplates.ContainsKey("ImportedB"));

            // Open & close document tab through MainWindowViewModel
            AssetBrowserEntry entryA = Assert.Single(browser.JunctionTemplateItems, e => e.Key == "ImportedA");
            EditorDocumentBase? opened = shell.OpenAsset(entryA);
            JunctionTemplateEditorDocumentViewModel juncDoc = Assert.IsType<JunctionTemplateEditorDocumentViewModel>(opened);
            Assert.Equal("ImportedA", juncDoc.TemplateKey);

            // Opening again returns the existing document instance, then close by AssetBrowserEntry
            Assert.Same(juncDoc, shell.OpenAsset(entryA));
            shell.CloseAssetDocument(entryA);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
            DeleteTempDirectory(externalDir);
        }
    }

    [Fact]
    public void EditorWorld_BuildsJunctionWithRelaysPathsCurvesExtrusionsAndConnections_AndDisposesCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "JunctionRenderWorld", "Junction Render", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            // Add layout & junction template with relay, wired paths (with Width), and AddCurve PutExtrusion
            LaneLayoutDocument relayLayout = new()
            {
                Key = "SingleRelay",
                RelativePath = "LaneLayouts/SingleRelay.xml",
            };
            relayLayout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.InOut, 0f, 0f, 1.75f, 1.75f));
            project.LaneLayouts["SingleRelay"] = relayLayout;
            project.Init.LaneLayouts.Add(new ResourceLoadReference("SingleRelay", relayLayout.RelativePath));

            Assert.True(ws.AddNewJunctionTemplate("RenderJunc", "Templates/Junctions/RenderJunc.csx"));
            Assert.True(ws.MutateJunctionTemplate("RenderJunc", "Configure relay, paths, widths, and aux curve", tpl =>
            {
                tpl.Relays.Add(new JunctionRelayDefinition(
                    "RIn",
                    "ROut",
                    "SingleRelay",
                    new PoseDegreesModel(0.0, 0.0, 12.0, 0.0, 0.0, 0.0)));

                JunctionPathTemplateModel firstPath = tpl.Paths[0];
                firstPath.WidthSegments.Add(new JunctionWidthSegment(
                    JunctionWidthSegmentKind.TransitionTo,
                    2.5,
                    2.5,
                    AnchoredDistance.Absolute(12.0)));
                firstPath.TrafficDensityFactor = 0.85;

                JunctionCurveTemplateModel auxCurve = new()
                {
                    Key = "Crosswalk",
                    Origin = JunctionCurveOriginModel.Absolute(PoseDegreesModel.FromTranslation(-5.0, 0.0, 2.0)),
                };
                auxCurve.Segments.Add(new JunctionCurveSegment(
                    JunctionCurveSegmentKind.StraightTo,
                    PoseDegreesModel.FromTranslation(5.0, 0.0, 2.0)));
                auxCurve.Extrusions.Add(new CurveExtrusionPlacementRule
                {
                    Key = "CwRoad",
                    CrossSectionKey = "Road1L",
                    From = AnchoredDistance.Absolute(0.0),
                    To = AnchoredDistance.Absolute(10.0),
                });
                tpl.Curves.Add(auxCurve);
            }));

            SplinePlacementNode firstSpline = project.Placement.Splines[0];
            JunctionPlacementNode? j1 = ws.AddChainedJunctionFromSplineOutlet(firstSpline.Id, "RenderJunc", "S");
            Assert.NotNull(j1);

            SplinePlacementNode? outSpline = ws.AddChainedSplineFromJunctionPort(j1.Id, "N", "Spline1", 30.0);
            Assert.NotNull(outSpline);

            JunctionPlacementNode? jFree = ws.AddJunctionAtChunkPose(
                "RenderJunc",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(180.0, 0.0, 150.0)));
            Assert.NotNull(jFree);
            Assert.True(ws.AddExplicitPortConnection(
                new PortReferenceModel(outSpline.Id, PortTargetKind.SplineOutlet),
                new PortReferenceModel(jFree.Id, PortTargetKind.JunctionPort, "S")));

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
            Assert.DoesNotContain(errorCollector.Errors, e => e.Level == TransportX.Diagnostics.ErrorLevel.Error || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal);
            Assert.Equal(2, world.Junctions.Count);
            Assert.True(0 < physicsHost.Simulation.Statics.Count);

            JunctionRenderEntry entry1 = Assert.Single(world.Junctions, e => e.Node.Id == j1.Id);
            Assert.NotEmpty(entry1.PrimaryChunkPathPolylines);
            Assert.NotEmpty(entry1.PrimaryChunkCurvePolylines);
            Assert.Contains(entry1.Ports, p => p.PortRef.PortKey == "N" && p.IsConnected);

            // Overlay selection renders JunctionHighlight in Gizmos layer and hides cleanly when Gizmos disabled
            world.UpdateOverlays(
                null,
                null,
                null,
                EditorToolMode.Translate,
                GizmoAxis.X,
                ViewportOverlayFlags.Default,
                null,
                ChunkIndex.Zero,
                jFree.Id);
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "JunctionHighlight");
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SelectedPortMarker");
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "PortMarker");

            world.UpdateOverlays(
                null,
                null,
                null,
                EditorToolMode.Select,
                GizmoAxis.None,
                ViewportOverlayFlags.Normal | ViewportOverlayFlags.ChunkGrid | ViewportOverlayFlags.Network,
                null,
                ChunkIndex.Zero,
                jFree.Id);
            Assert.DoesNotContain(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "JunctionHighlight");
            Assert.DoesNotContain(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "SelectedPortMarker");
            Assert.Contains(world.Chunks.SelectMany(c => c.Models), m => m.Resource.Model.DebugName == "PortMarker");

            // Picking tests hit the placed free junction
            JunctionRenderEntry freeEntry = Assert.Single(world.Junctions, e => e.Node.Id == jFree.Id);
            Vector3 targetPos = freeEntry.LocalOrigin + new Vector3(0f, 0.5f, 0f);
            ViewportRay ray = new(
                freeEntry.PrimaryChunk,
                targetPos + new Vector3(0f, 20f, 0f),
                new Vector3(0f, -1f, 0f));
            List<JunctionPickCandidate> candidates =
            [
                new(
                    freeEntry.Node.Id,
                    freeEntry.PrimaryChunk,
                    new WorldPoseDegreesModel(
                        new ChunkIndexModel(freeEntry.PrimaryChunk.X, freeEntry.PrimaryChunk.Z),
                        PoseDegreesModel.FromTranslation(freeEntry.LocalOrigin.X, freeEntry.LocalOrigin.Y, freeEntry.LocalOrigin.Z)),
                    freeEntry.LocalBounds,
                    freeEntry.PrimaryChunkPathPolylines),
            ];
            JunctionPickHit? pickHit = ViewportPickingAndGizmoService.PickNearestJunction(ray, candidates);
            Assert.True(pickHit.HasValue);
            Assert.Equal(jFree.Id, pickHit.Value.JunctionId);

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
    public void AvaloniaUi_InstantiatesBindsMeasuresAndRendersPhase5ViewsCleanly()
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
                    WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Phase5UiWorld", "Phase 5 UI", "Desc");
                    Assert.True(shell.Workspace.CreateNewJunctionTemplate("UiJunc", out string? tplKey, out _));
                    Assert.Equal("UiJunc", tplKey);

                    JunctionPlacementNode? placedJunc = shell.Workspace.AddJunctionAtChunkPose(
                        "UiJunc",
                        new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(125.0, 0.0, 125.0)));
                    Assert.NotNull(placedJunc);
                    shell.Workspace.SelectJunction(placedJunc.Id);

                    AssetBrowserEntry? juncEntry = shell.AssetBrowserTool.JunctionTemplateItems.FirstOrDefault();
                    Assert.NotNull(juncEntry);
                    using JunctionTemplateEditorDocumentViewModel? juncVm =
                        shell.OpenAsset(juncEntry!) as JunctionTemplateEditorDocumentViewModel;
                    Assert.NotNull(juncVm);

                    juncVm!.AddRelayCommand.Execute(Unit.Default);
                    juncVm.AddCurveCommand.Execute(Unit.Default);
                    juncVm.AddStaticPropCommand.Execute(Unit.Default);

                    ViewLocator locator = new();
                    Control? juncView = locator.Build(juncVm);
                    Control? inspectorView = locator.Build(shell.InspectorTool);
                    Control? viewportView = locator.Build(shell.ViewportDocument);
                    Control? hierarchyView = locator.Build(shell.SceneHierarchyTool);

                    Assert.IsType<JunctionTemplateEditorView>(juncView);
                    Assert.IsType<InspectorView>(inspectorView);
                    Assert.IsType<ViewportDocumentView>(viewportView);
                    Assert.IsType<SceneHierarchyView>(hierarchyView);

                    MeasureAndArrange(juncView!, juncVm, 980, 680);
                    MeasureAndArrange(inspectorView!, shell.InspectorTool, 360, 760);
                    MeasureAndArrange(viewportView!, shell.ViewportDocument, 960, 600);
                    MeasureAndArrange(hierarchyView!, shell.SceneHierarchyTool, 320, 480);

                    // Render JunctionPlanCanvas to offscreen bitmap to exercise 2D plan DrawingContext code
                    JunctionPlanCanvas canvas = new()
                    {
                        DataContext = juncVm,
                    };
                    canvas.Measure(new Avalonia.Size(640, 480));
                    canvas.Arrange(new Avalonia.Rect(0, 0, canvas.DesiredSize.Width, canvas.DesiredSize.Height));
                    Assert.True(320.0 <= canvas.Bounds.Width);
                    Assert.True(240.0 <= canvas.Bounds.Height);

                    using RenderTargetBitmap rtb = new(
                        new PixelSize(int.Max(1, (int)canvas.Bounds.Width), int.Max(1, (int)canvas.Bounds.Height)),
                        new Avalonia.Vector(96, 96));
                    rtb.Render(canvas);
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
