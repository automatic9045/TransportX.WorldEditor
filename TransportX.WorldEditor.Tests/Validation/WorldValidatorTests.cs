using System.Collections.Generic;
using System.Linq;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using Xunit;

namespace TransportX.WorldEditor.Tests.Validation;

public sealed class WorldValidatorTests
{
    private static LaneLayoutDocument CreateSymmetricLayout(string key = "Layout1")
    {
        LaneLayoutDocument layout = new LaneLayoutDocument { Key = key, RelativePath = $"{key}.xml" };
        layout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.Out, -2.0f, -0.12f, 2.0f, 1.75f));
        layout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.In, 2.0f, -0.12f, 1.75f, 2.0f));
        return layout;
    }

    private static LaneLayoutDocument CreateAsymmetricLayout(string key = "LayoutAsym")
    {
        LaneLayoutDocument layout = new LaneLayoutDocument { Key = key, RelativePath = $"{key}.xml" };
        layout.Lanes.Add(new LaneDefinition("Pedestrians", LaneFlowDirections.InOut, -5.0f, 0.0f, 1.5f, 1.0f));
        layout.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.Out, -1.75f, -0.12f, 1.75f, 1.75f));
        return layout;
    }

    [Fact]
    public void LaneLayoutCanConnectToValidatesMirrorSymmetry()
    {
        LaneLayoutDocument symmetric = CreateSymmetricLayout();
        Assert.True(symmetric.CanConnectTo(symmetric));

        LaneLayoutDocument asymmetric = CreateAsymmetricLayout();
        Assert.False(asymmetric.CanConnectTo(asymmetric));

        LaneLayoutDocument mirrored = asymmetric.CreateOpposition("LayoutAsym_Opp");
        Assert.True(asymmetric.CanConnectTo(mirrored));
        Assert.True(mirrored.CanConnectTo(asymmetric));

        LaneLayoutDocument wrongWidth = mirrored.CreateOpposition().CreateOpposition();
        wrongWidth.Lanes[0] = wrongWidth.Lanes[0] with { LeftWidth = wrongWidth.Lanes[0].LeftWidth + 0.5f };
        Assert.False(asymmetric.CanConnectTo(wrongWidth));
    }

    [Fact]
    public void ValidateRejectsMixedColliderMaterialsInCrossSection()
    {
        WorldProject project = new WorldProject();
        CrossSectionDocument cs = new CrossSectionDocument
        {
            Key = "MixedRoad",
            RelativePath = "CrossSections/MixedRoad.xml",
        };
        cs.Vertices.Add(new CrossSectionVertexModel { Key = "V0", X = 0.0f, Y = 0.0f });
        cs.Vertices.Add(new CrossSectionVertexModel { Key = "V1", X = 2.0f, Y = 0.0f });
        cs.Vertices.Add(new CrossSectionVertexModel { Key = "V2", X = 4.0f, Y = 0.0f });
        cs.Materials.Add(new CrossSectionMaterialModel { Name = "Mat" });
        cs.ColliderMaterials.Add(new CrossSectionColliderMaterialModel { Name = "RoadCol" });
        cs.ColliderMaterials.Add(new CrossSectionColliderMaterialModel { Name = "CurbCol" });

        CrossSectionSurfaceModel s1 = new CrossSectionSurfaceModel
        {
            MaterialName = "Mat",
            ColliderMaterialName = "RoadCol",
        };
        s1.VertexKeys.Add("V0");
        s1.VertexKeys.Add("V1");

        CrossSectionSurfaceModel s2 = new CrossSectionSurfaceModel
        {
            MaterialName = "Mat",
            ColliderMaterialName = "CurbCol",
        };
        s2.VertexKeys.Add("V1");
        s2.VertexKeys.Add("V2");

        cs.Surfaces.Add(s1);
        cs.Surfaces.Add(s2);
        project.CrossSections[cs.Key] = cs;

        IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "CROSS_SECTION_MIXED_COLLIDER_MATERIALS" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void ValidateEnforcesNoneModelPathRulesInModelList()
    {
        WorldProject project = new WorldProject();
        ModelListDocument list = new ModelListDocument { RelativePath = "Models.txt" };
        list.Entries.Add(new ModelListEntry { Key = "OkBox", ModelPath = null, Collider = ModelColliderSpec.BoxCollider(1f, 1f, 1f) });
        list.Entries.Add(new ModelListEntry { Key = "BadHull", ModelPath = null, Collider = ModelColliderSpec.ConvexHull() });
        list.Entries.Add(new ModelListEntry { Key = "UpperCol", ModelPath = "A.glb", Collider = ModelColliderSpec.MeshCollider(true, "Sub/ColMesh.glb") });
        project.ModelLists["Models.txt"] = list;

        IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "MODEL_NONE_INVALID_FOR_COLLIDER" && i.Message.Contains("BadHull"));
        Assert.DoesNotContain(issues, i => i.Code == "MODEL_NONE_INVALID_FOR_COLLIDER" && i.Message.Contains("OkBox"));
        Assert.Contains(issues, i => i.Code == "MODEL_COLLISION_PATH_UPPERCASE" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void ValidateFlagsEmptyAndDuplicateEntriesInCrossSectionLists()
    {
        WorldProject project = new();
        CrossSectionListDocument list1 = new() { RelativePath = "CrossSections.txt" };
        list1.Entries.Add(new CrossSectionListEntry("Road1L", "CrossSections/Road1L.xml"));
        list1.Entries.Add(new CrossSectionListEntry("", "CrossSections/EmptyKey.xml"));
        list1.Entries.Add(new CrossSectionListEntry("NoPath", ""));

        CrossSectionListDocument list2 = new() { RelativePath = "CrossSections_Sub.txt" };
        list2.Entries.Add(new CrossSectionListEntry("Road1L", "CrossSections/Other.xml"));

        project.CrossSectionLists[list1.RelativePath] = list1;
        project.CrossSectionLists[list2.RelativePath] = list2;

        IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "CROSS_SECTION_LIST_EMPTY_KEY");
        Assert.Contains(issues, i => i.Code == "CROSS_SECTION_LIST_EMPTY_PATH");
        Assert.Contains(issues, i => i.Code == "CROSS_SECTION_LIST_DUPLICATE_KEY" && i.Message.Contains("Road1L"));
    }

    [Fact]
    public void ValidateRejectsIntoJunctionAfterConnectBezier()
    {
        WorldProject project = new WorldProject();
        LaneLayoutDocument layout = CreateSymmetricLayout("Layout1");
        project.LaneLayouts[layout.Key] = layout;

        project.SplineTemplates["Spline1"] = new SplineTemplateDocument
        {
            Key = "Spline1",
            OutletLayoutKey = "Layout1",
        };

        JunctionTemplateDocument junTpl = new JunctionTemplateDocument { Key = "Jun1" };
        junTpl.Ports.Add(new JunctionPortDefinition("S", "Layout1", PoseDegreesModel.Identity));
        junTpl.Ports.Add(new JunctionPortDefinition("N", "Layout1", new PoseDegreesModel(0, 0, 20, 0, 0, 0)));
        project.JunctionTemplates[junTpl.Key] = junTpl;

        JunctionPlacementNode targetJunction = new JunctionPlacementNode
        {
            Id = "j_target",
            TemplateKey = "Jun1",
            Origin = JunctionOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity),
        };

        SplinePlacementNode bezierSpline = new SplinePlacementNode
        {
            Id = "s_bezier",
            TemplateKey = "Spline1",
            Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, new PoseDegreesModel(100, 0, 0, 0, 0, 0)),
            ConnectBezier = new SplineConnectBezierModel(new PortReferenceModel("j_target", PortTargetKind.JunctionPort, "S"), 0.5),
        };
        bezierSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 20.0));

        JunctionPlacementNode forbiddenJunction = new JunctionPlacementNode
        {
            Id = "j_invalid",
            TemplateKey = "Jun1",
            Origin = JunctionOriginModel.FromSpline("s_bezier", "N"),
        };

        project.Placement.Junctions.Add(targetJunction);
        project.Placement.Splines.Add(bezierSpline);
        project.Placement.Junctions.Add(forbiddenJunction);

        IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "SPLINE_CONNECT_BEZIER_INTO_JUNCTION" && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void DistanceAnchorResolverValidatesOrderAndResolvesOffsets()
    {
        SplinePlacementNode spline = new SplinePlacementNode { Id = "spline_001" };
        spline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 20.0, "s_mid"));
        spline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 30.0, "s_end"));

        SplineInstancePropRule prop = new SplineInstancePropRule
        {
            From = AnchoredDistance.RelativeTo("s_mid", -5.0),
            Span = 10.0,
            Interval = 2.0,
        };
        prop.ModelKeys.Add("Sign");
        spline.Props.Add(prop);

        List<ValidationIssue> issues = new List<ValidationIssue>();
        Dictionary<string, double> anchors = DistanceAnchorResolver.ValidateAndResolveSpline(spline, issues);

        Assert.Empty(issues);
        Assert.Equal(20.0, anchors["s_mid"], 6);
        Assert.Equal(50.0, anchors["s_end"], 6);
        Assert.True(prop.From.TryResolve(anchors, out double resolvedFrom));
        Assert.Equal(15.0, resolvedFrom, 6);

        JunctionPathTemplateModel badPath = new JunctionPathTemplateModel { Key = "P1" };
        badPath.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity, 0.5, "s_curve"));
        badPath.WidthSegments.Add(new JunctionWidthSegment(
            JunctionWidthSegmentKind.Constant,
            0.0,
            0.0,
            AnchoredDistance.RelativeTo("w_later", 0.0),
            "w_first"));
        badPath.WidthSegments.Add(new JunctionWidthSegment(
            JunctionWidthSegmentKind.Constant,
            0.0,
            0.0,
            AnchoredDistance.Absolute(5.0),
            "w_later"));

        List<ValidationIssue> pathIssues = new List<ValidationIssue>();
        DistanceAnchorResolver.ValidateAndResolveJunctionPath(badPath, pathIssues);
        Assert.Contains(pathIssues, i => i.Code == "ANCHOR_UNDECLARED");
    }

    [Fact]
    public void TopologicalSorterOrdersDependenciesAndDetectsCycles()
    {
        WorldPlacementDocument doc = new WorldPlacementDocument();
        SplinePlacementNode s2 = new SplinePlacementNode
        {
            Id = "s2",
            Origin = SplineOriginModel.FromJunction("j1", "N"),
        };
        JunctionPlacementNode j1 = new JunctionPlacementNode
        {
            Id = "j1",
            TemplateKey = "J",
            Origin = JunctionOriginModel.FromSpline("s1", "S"),
        };
        SplinePlacementNode s1 = new SplinePlacementNode
        {
            Id = "s1",
            Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity),
        };

        doc.Splines.Add(s2);
        doc.Junctions.Add(j1);
        doc.Splines.Add(s1);

        NetworkTopologicalSortResult sort = NetworkTopologicalSorter.Sort(doc);
        Assert.Empty(sort.CycleNodeIds);
        List<string> ids = sort.OrderedNodes.Select(n => n.Id).ToList();
        Assert.Equal(["s1", "j1", "s2"], ids);

        // Connected branches stay contiguous: A1 -> A2 -> A3 stays together before unrelated B1 -> B2
        WorldPlacementDocument branched = new WorldPlacementDocument();
        branched.Splines.Add(new SplinePlacementNode { Id = "A1", Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity) });
        branched.Splines.Add(new SplinePlacementNode { Id = "A2", Origin = SplineOriginModel.FromSpline("A1") });
        branched.Splines.Add(new SplinePlacementNode { Id = "B1", Origin = SplineOriginModel.FromChunk(new ChunkIndexModel(2, 0), PoseDegreesModel.Identity) });
        branched.Splines.Add(new SplinePlacementNode { Id = "B2", Origin = SplineOriginModel.FromSpline("B1") });
        branched.Splines.Add(new SplinePlacementNode { Id = "A3", Origin = SplineOriginModel.FromSpline("A2") });

        NetworkTopologicalSortResult branchedSort = NetworkTopologicalSorter.Sort(branched);
        Assert.Empty(branchedSort.CycleNodeIds);
        Assert.Equal(["A1", "A2", "A3", "B1", "B2"], branchedSort.OrderedNodes.Select(n => n.Id).ToList());

        WorldPlacementDocument cyclic = new WorldPlacementDocument();
        cyclic.Splines.Add(new SplinePlacementNode { Id = "a", Origin = SplineOriginModel.FromSpline("b") });
        cyclic.Splines.Add(new SplinePlacementNode { Id = "b", Origin = SplineOriginModel.FromSpline("a") });

        NetworkTopologicalSortResult cycleResult = NetworkTopologicalSorter.Sort(cyclic);
        Assert.Equal(2, cycleResult.CycleNodeIds.Count);
    }

    [Fact]
    public void ValidateNodeKeys_AllowsUnicodeCSharpIdentifiers_AndRejectsInvalidOrDuplicateKeys()
    {
        Assert.True(CSharpIdentifierUtilities.IsValidIdentifier("spline_001"));
        Assert.True(CSharpIdentifierUtilities.IsValidIdentifier("本線_1"));
        Assert.True(CSharpIdentifierUtilities.IsValidIdentifier("_駅北口α"));
        Assert.False(CSharpIdentifierUtilities.IsValidIdentifier(""));
        Assert.False(CSharpIdentifierUtilities.IsValidIdentifier("本線 1"));
        Assert.False(CSharpIdentifierUtilities.IsValidIdentifier("1本線"));
        Assert.False(CSharpIdentifierUtilities.IsValidIdentifier("road-1"));
        Assert.False(CSharpIdentifierUtilities.IsValidIdentifier("class"));

        Assert.True(CSharpIdentifierUtilities.IsValidAnchorId("fSpline"));
        Assert.True(CSharpIdentifierUtilities.IsValidAnchorId("class"));
        Assert.True(CSharpIdentifierUtilities.IsValidAnchorId("1"));
        Assert.True(CSharpIdentifierUtilities.IsValidAnchorId("中間_1"));
        Assert.False(CSharpIdentifierUtilities.IsValidAnchorId(""));
        Assert.False(CSharpIdentifierUtilities.IsValidAnchorId("a b"));
        Assert.False(CSharpIdentifierUtilities.IsValidAnchorId("a-b"));

        Assert.True(CSharpIdentifierUtilities.IsValidNodeKey("1本線"));
        Assert.True(CSharpIdentifierUtilities.IsValidNodeKey("road-1"));
        Assert.True(CSharpIdentifierUtilities.IsValidNodeKey("class"));
        Assert.True(CSharpIdentifierUtilities.IsValidNodeKey("本線 1"));
        Assert.False(CSharpIdentifierUtilities.IsValidNodeKey(""));
        Assert.False(CSharpIdentifierUtilities.IsValidNodeKey("   "));
        Assert.False(CSharpIdentifierUtilities.IsValidNodeKey(" leading"));
        Assert.False(CSharpIdentifierUtilities.IsValidNodeKey("bad\tkey"));
        Assert.False(CSharpIdentifierUtilities.IsValidNodeKey("bad\nkey"));

        WorldProject project = new WorldProject();
        project.Placement.Splines.Add(new SplinePlacementNode { Id = "s1", Key = null });
        project.Placement.Splines.Add(new SplinePlacementNode { Id = "s2", Key = "bad\tkey" });
        project.Placement.Splines.Add(new SplinePlacementNode { Id = "s3", Key = "1号線-北" });
        project.Placement.Splines.Add(new SplinePlacementNode { Id = "s4", Key = "交差点_A" });
        project.Placement.Junctions.Add(new JunctionPlacementNode { Id = "j1", Key = "交差点_A", TemplateKey = "MissingTpl" });

        IReadOnlyList<ValidationIssue> issues = WorldValidator.Validate(project);
        Assert.Contains(issues, i => i.Code == "PLACEMENT_MISSING_KEY");
        Assert.Contains(issues, i => i.Code == "PLACEMENT_INVALID_KEY");
        Assert.Contains(issues, i => i.Code == "PLACEMENT_DUPLICATE_KEY");
    }
}
