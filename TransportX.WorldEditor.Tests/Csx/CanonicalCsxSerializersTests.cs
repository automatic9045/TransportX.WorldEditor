using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using Xunit;

namespace TransportX.WorldEditor.Tests.Csx;

public sealed class CanonicalCsxSerializersTests
{
    [Fact]
    public void InitCsxRoundTripsAllResourcesAndUserCode()
    {
        InitDocument doc = new InitDocument
        {
            UserCode = "Component<TrafficSignals>().AddController(\"4Forked1\", \"SignalControllers/4Forked1.xml\");",
        };
        doc.ModelLists.Add(new AssetListLoadReference(null, "Models.txt"));
        doc.SoundLists.Add(new AssetListLoadReference("se", "Sounds.txt"));
        doc.CrossSections.Add(new ResourceLoadReference("Road1L", "CrossSections/Road1L.xml"));
        doc.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("Pedestrians", "歩行者", "#FFFF00"));
        doc.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("Buses", "バス", "#00FF00"));
        doc.LaneTraffic.Groups.Add(new LaneTrafficGroupDefinition("Cars", ["Buses"], "#00FFFF"));
        doc.LaneLayouts.Add(new ResourceLoadReference("Layout1", "LaneLayouts/Layout1.xml"));

        string csx = InitCsxSerializer.Generate(doc);
        (InitDocument? parsed, IReadOnlyList<ParseDiagnostic> diag) = InitCsxSerializer.Parse("Init.csx", csx);

        Assert.Empty(diag);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.ModelLists);
        Assert.Equal("se", parsed.SoundLists[0].BundleKey);
        Assert.Single(parsed.CrossSections);
        Assert.Equal(2, parsed.LaneTraffic.Types.Count);
        Assert.Single(parsed.LaneTraffic.Groups);
        Assert.Single(parsed.LaneLayouts);
        Assert.Equal(doc.UserCode, parsed.UserCode);
    }

    [Fact]
    public void SplineTemplateCsxRoundTripsPropsExtrusionsAndUserCode()
    {
        SplineTemplateDocument doc = new SplineTemplateDocument
        {
            Key = "Spline1",
            RelativePath = "Templates/Splines/Spline1.csx",
            OutletLayoutKey = "Layout1",
            UserCode = "tSpline.SpeedLimit(1, 60);",
        };
        doc.Extrusions.Add(new SplineTemplateExtrusionRule
        {
            Key = "RoadL",
            CrossSectionKey = "Road1L",
            OffsetX = -1.5,
            OffsetY = 0.0,
            BaseInterval = 5.0,
            MaxDeviation = 0.01,
            MaxAngleDeltaDeg = 2.0,
            VIntervalScale = 1.0,
            VOffset = 0.2,
            From = 1.0,
        });
        doc.Extrusions.Add(new SplineTemplateExtrusionRule
        {
            Key = null,
            CrossSectionKey = "WhiteLine150",
            OffsetX = 0.0,
            OffsetY = -0.12,
            BaseInterval = 5.0,
            MaxDeviation = 0.01,
            MaxAngleDeltaDeg = 2.0,
            VIntervalScale = 1.0,
            VOffset = 0.0,
            From = 0.0,
        });
        SplineTemplatePropRule prop = new SplineTemplatePropRule
        {
            Key = "Poles",
            Pose = new PoseDegreesModel(-5.5, 0, 0, 0, 90, 0),
            From = 5.0,
            Span = 35.0,
            Interval = 35.0,
        };
        prop.ModelKeys.Add("StreetLight");
        doc.Props.Add(prop);

        SplineTemplatePropRule anonProp = new SplineTemplatePropRule
        {
            Key = null,
            Pose = new PoseDegreesModel(5.5, 0, 0, 0, 180, 0),
            From = 10.0,
            Span = 35.0,
            Interval = 35.0,
        };
        anonProp.ModelKeys.Add("RoadSign_NoParking");
        doc.Props.Add(anonProp);

        string csx = SplineTemplateCsxSerializer.Generate(doc);
        Assert.DoesNotContain("tSpline.PutExtrusion(null,", csx, System.StringComparison.Ordinal);
        Assert.DoesNotContain("tSpline.PutProp(null,", csx, System.StringComparison.Ordinal);
        Assert.Contains("tSpline.PutExtrusion(\"WhiteLine150\",", csx, System.StringComparison.Ordinal);
        Assert.Contains("tSpline.PutProp([\"RoadSign_NoParking\"],", csx, System.StringComparison.Ordinal);

        (SplineTemplateDocument? parsed, IReadOnlyList<ParseDiagnostic> diag) = SplineTemplateCsxSerializer.Parse("Spline1.csx", csx, doc.RelativePath);

        Assert.Empty(diag);
        Assert.NotNull(parsed);
        Assert.Equal("Spline1", parsed!.Key);
        Assert.Equal("Layout1", parsed.OutletLayoutKey);
        Assert.Equal(2, parsed.Extrusions.Count);
        Assert.Equal("RoadL", parsed.Extrusions[0].Key);
        Assert.Null(parsed.Extrusions[1].Key);
        Assert.Equal(-1.5, parsed.Extrusions[0].OffsetX, 6);
        Assert.Equal(2, parsed.Props.Count);
        Assert.Equal("Poles", parsed.Props[0].Key);
        Assert.Null(parsed.Props[1].Key);
        Assert.Equal(["StreetLight"], parsed.Props[0].ModelKeys);
        Assert.Equal(["RoadSign_NoParking"], parsed.Props[1].ModelKeys);
        Assert.Equal(doc.UserCode, parsed.UserCode);
    }

    [Fact]
    public void JunctionTemplateCsxRoundTripsAnchoredDistancesWidthAddCurveAndUserCode()
    {
        JunctionTemplateDocument doc = new JunctionTemplateDocument
        {
            Key = "3Forked1",
            RelativePath = "Templates/Junctions/3Forked1.csx",
            UserCode = "tJunction.SignalController(\"4Forked1\");",
        };
        doc.Ports.Add(new JunctionPortDefinition("S", "Layout1", new PoseDegreesModel(0, 0, 0, 0, 180, 0)));
        doc.Ports.Add(new JunctionPortDefinition("N", "Layout1", new PoseDegreesModel(0, 0, 24, 0, 0, 0)));
        doc.Relays.Add(new JunctionRelayDefinition("SEIn", "SEOut", "LayoutSingle", new PoseDegreesModel(-1.5, 0, 7, 0, 15, 0)));

        JunctionPathTemplateModel path = new JunctionPathTemplateModel
        {
            Key = "ES_L",
            FromPortKey = "S",
            FromPinIndex = 1,
            ToPortKey = "N",
            ToPinIndex = 2,
            TrafficDensityFactor = 0.25,
        };
        path.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.BezierTo, new PoseDegreesModel(7, -0.12, 8.5, 0, 90, 0), 0.7, "s_mid"));
        path.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity, 0.5, "s_end"));
        path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.Constant, 0, 0, AnchoredDistance.Absolute(5.0), "w_1"));
        path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.TransitionTo, 2.5, 3.0, AnchoredDistance.RelativeTo("s_mid", -2.0)));

        CurvePropPlacementRule pathProp = new CurvePropPlacementRule
        {
            Pose = PoseDegreesModel.FromTranslation(1.3, 0, 0),
            From = AnchoredDistance.RelativeTo("s_end", -4.0),
            Span = 1.0,
            Interval = 1.0,
            Count = 1,
        };
        pathProp.ModelKeys.Add("WhiteLine150");
        path.Props.Add(pathProp);

        path.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = "Kerb",
            CrossSectionKey = "DikeL",
            OffsetX = -9.75,
            OffsetY = 0.12,
            From = AnchoredDistance.Absolute(0.0),
            To = AnchoredDistance.RelativeTo("s_mid", 1.5),
        });
        path.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = null,
            CrossSectionKey = "WhiteLine150",
            OffsetX = 0.0,
            OffsetY = -0.12,
            From = AnchoredDistance.Absolute(0.0),
            To = null,
        });
        doc.Paths.Add(path);

        JunctionCurveTemplateModel curve = new JunctionCurveTemplateModel
        {
            Key = "SN",
            Origin = JunctionCurveOriginModel.FromPortPinWithOffset("S", 0, PoseDegreesModel.FromTranslation(0, -0.12, 0)),
        };
        curve.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, new PoseDegreesModel(0, -0.12, 24, 0, 0, 0), 0.5, "s_curve_sn"));
        curve.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = null,
            CrossSectionKey = "WhiteLine150",
            OffsetX = -4,
            OffsetY = 0,
            From = AnchoredDistance.Absolute(6.5),
            To = AnchoredDistance.RelativeTo("s_curve_sn", -6.5),
        });
        doc.Curves.Add(curve);

        doc.Props.Add(new JunctionStaticPropPlacement("Road1_Junction1_RoadL", new PoseDegreesModel(0, 0, 12, 0, 180, 0)));

        string csx = JunctionTemplateCsxSerializer.Generate(doc);
        Assert.DoesNotContain(".PutExtrusion(null,", csx, System.StringComparison.Ordinal);
        Assert.Contains("tPath_ES_L.PutExtrusion(\"WhiteLine150\",", csx, System.StringComparison.Ordinal);
        Assert.Contains("tCurve_SN.PutExtrusion(\"WhiteLine150\",", csx, System.StringComparison.Ordinal);
        Assert.Contains("out var s_s_mid", csx, System.StringComparison.Ordinal);
        Assert.Contains("out var s_w_1", csx, System.StringComparison.Ordinal);
        Assert.Contains("s_s_mid - 2", csx, System.StringComparison.Ordinal);
        Assert.Contains("s_s_end - 4", csx, System.StringComparison.Ordinal);

        (JunctionTemplateDocument? parsed, IReadOnlyList<ParseDiagnostic> diag) = JunctionTemplateCsxSerializer.Parse("3Forked1.csx", csx, doc.RelativePath);

        Assert.Empty(diag);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Ports.Count);
        Assert.Single(parsed.Relays);
        Assert.Single(parsed.Paths);
        Assert.Equal(0.25, parsed.Paths[0].TrafficDensityFactor!.Value, 6);
        Assert.Equal(2, parsed.Paths[0].Segments.Count);
        Assert.Equal("s_mid", parsed.Paths[0].Segments[0].OutputAnchorId);
        Assert.Equal(0.7, parsed.Paths[0].Segments[0].HandleScale, 6);
        Assert.Equal(2, parsed.Paths[0].WidthSegments.Count);
        Assert.Equal(AnchoredDistance.RelativeTo("s_mid", -2.0), parsed.Paths[0].WidthSegments[1].Length);
        Assert.Equal(AnchoredDistance.RelativeTo("s_end", -4.0), parsed.Paths[0].Props[0].From);
        Assert.Equal(2, parsed.Paths[0].Extrusions.Count);
        Assert.Equal("Kerb", parsed.Paths[0].Extrusions[0].Key);
        Assert.Null(parsed.Paths[0].Extrusions[1].Key);
        Assert.Equal(AnchoredDistance.RelativeTo("s_mid", 1.5), parsed.Paths[0].Extrusions[0].To);
        Assert.Single(parsed.Curves);
        Assert.Equal(JunctionCurveOriginKind.PortPinRelative, parsed.Curves[0].Origin.Kind);
        Assert.True(parsed.Curves[0].Origin.HasPortPinOffset);
        Assert.Null(parsed.Curves[0].Extrusions[0].Key);
        Assert.Equal(AnchoredDistance.RelativeTo("s_curve_sn", -6.5), parsed.Curves[0].Extrusions[0].To);
        Assert.Single(parsed.Props);
        Assert.Equal(doc.UserCode, parsed.UserCode);
    }

    [Fact]
    public void WorldCsxRoundTripsTopologicalOrderPreBuildConnectBezierAndUserCode()
    {
        WorldPlacementDocument doc = new WorldPlacementDocument
        {
            EnvironmentPath = "Environment1.xml",
            Avatar = new AvatarSettingsModel
            {
                XmlPath = "../LV290/Avatar_LV290N_Konan.xml",
                InitialPose = new WorldPoseDegreesModel(ChunkIndexModel.Zero, new PoseDegreesModel(10, 0.2, 10, 0, 0, 0)),
            },
            UserCode = "// custom trigger",
        };
        doc.ExtraLoadScripts.Add("Scripts/CustomTriggers.csx");
        doc.BackgroundModels.Add("Background");
        doc.Props.Add(new ChunkPropPlacement("prop_001", "BusStop_Ichigo", ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(-4.5, 0, 49.3), "一郷バス停"));

        SplinePlacementNode s1 = new SplinePlacementNode
        {
            Id = "spline_001",
            Key = "MainApproach",
            DisplayName = "駅前通り \"本線\" & <入口>",
            Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(10, 0, 0)),
            TemplateKey = "Spline1",
            PreBuildUserCode = "fSpline.SpeedLimit(1, 50);",
        };
        s1.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 40.0, "s_mid"));
        s1.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.ByRadius, 150.0, 60.0, "s_end"));
        s1.Gradients.Add(new SplineGradientSegment(SplineGradientSegmentKind.TransitionByPercent, 2.5, 100.0));
        s1.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.TransitionToPercent, 3.0, 0.0, 50.0));
        s1.PropSkips.Add(new SplineSkipRule("Poles"));
        s1.ExtrusionSkips.Add(new SplineSkipRule("RoadL", AnchoredDistance.RelativeTo("s_mid", -5.0), AnchoredDistance.RelativeTo("s_end", -10.0)));
        s1.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = "CenterDashed",
            CrossSectionKey = "WhiteLine150",
            From = AnchoredDistance.RelativeTo("s_mid", 2.0),
            To = null,
        });
        SplineInstancePropRule s1AnonProp = new SplineInstancePropRule
        {
            Key = null,
            Pose = new PoseDegreesModel(5.25, 0, 0, 0, 180, 0),
            From = AnchoredDistance.Absolute(10.0),
            Span = 0.0,
            Interval = 0.0,
            Count = 1,
        };
        s1AnonProp.ModelKeys.Add("RoadSign_NoParking");
        s1.Props.Add(s1AnonProp);

        SplineInstancePropRule s1KeyedProp = new SplineInstancePropRule
        {
            Key = "SpeedSign",
            Pose = new PoseDegreesModel(5.25, 0.6, 0, 0, 180, 0),
            From = AnchoredDistance.Absolute(10.0),
            Span = 0.0,
            Interval = 0.0,
            Count = 1,
        };
        s1KeyedProp.ModelKeys.Add("RoadSign_SpeedMax50");
        s1.Props.Add(s1KeyedProp);

        JunctionPlacementNode j1 = new JunctionPlacementNode
        {
            Id = "junction_001",
            DisplayName = "北口交差点",
            TemplateKey = "3Forked1",
            Origin = JunctionOriginModel.FromSpline("spline_001", "S"),
            PreBuildUserCode = "fJunction.Paths[\"NS_S\"].Signal(\"V_Car\");",
        };
        j1.Props.Add(new JunctionStaticPropPlacement("Sign1", PoseDegreesModel.FromTranslation(-4, 0, 5)));
        JunctionPathOverrideModel pathOv = new JunctionPathOverrideModel
        {
            PathKey = "SE_R",
            TrafficDensityFactor = 0.0,
        };
        pathOv.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = null,
            CrossSectionKey = "WhiteLine150",
            From = AnchoredDistance.Absolute(1.0),
            To = null,
        });
        j1.PathOverrides.Add(pathOv);

        SplinePlacementNode s2 = new SplinePlacementNode
        {
            Id = "spline_002",
            Origin = SplineOriginModel.FromJunction("junction_001", "E"),
            TemplateKey = "Spline1",
            ConnectBezier = new SplineConnectBezierModel(new PortReferenceModel("junction_001", PortTargetKind.JunctionPort, "N"), 0.4, "s_loop"),
        };
        s2.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 25.0));
        s2.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = null,
            CrossSectionKey = "WhiteLine150",
            From = AnchoredDistance.Absolute(0.0),
            To = null,
        });

        // Intentionally add out of topological order (s2 before j1 before s1)
        doc.Splines.Add(s2);
        doc.Junctions.Add(j1);
        doc.Splines.Add(s1);

        doc.Connections.Add(new ExplicitPortConnection(
            new PortReferenceModel("s1_dummy", PortTargetKind.SplineOutlet),
            new PortReferenceModel("junction_001", PortTargetKind.JunctionPort, "N")));
        doc.Connections.Clear();
        doc.Connections.Add(new ExplicitPortConnection(
            new PortReferenceModel("spline_002", PortTargetKind.SplineOutlet),
            new PortReferenceModel("junction_001", PortTargetKind.JunctionPort, "N")));

        string generated = WorldCsxSerializer.Generate(doc, ["Templates/Splines/Spline1.csx", "Templates/Junctions/3Forked1.csx"]);

        Assert.Contains("    // <user-prebuild>\n    fSpline.SpeedLimit(1, 50);\n    // </user-prebuild>", generated, System.StringComparison.Ordinal);
        Assert.Contains("    // <user-code>\n    // custom trigger\n    // </user-code>", generated, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".PutProp(null,", generated, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".PutExtrusion(null,", generated, System.StringComparison.Ordinal);
        Assert.Contains("    fSpline.PutProp([\"RoadSign_NoParking\"],", generated, System.StringComparison.Ordinal);
        Assert.Contains("    fSpline.PutProp(\"SpeedSign\", [\"RoadSign_SpeedMax50\"],", generated, System.StringComparison.Ordinal);
        Assert.Contains("    fSpline.PutExtrusion(\"WhiteLine150\",", generated, System.StringComparison.Ordinal);
        Assert.Contains("    fJunction.Paths[\"SE_R\"].PutExtrusion(\"WhiteLine150\",", generated, System.StringComparison.Ordinal);
        Assert.Contains("    // <display name=\"一郷バス停\" />", generated, System.StringComparison.Ordinal);
        Assert.Contains("    // <display name=\"駅前通り &quot;本線&quot; &amp; &lt;入口&gt;\" />", generated, System.StringComparison.Ordinal);
        Assert.Contains("    // <display name=\"北口交差点\" />", generated, System.StringComparison.Ordinal);
        Assert.Contains("out var s_s_mid", generated, System.StringComparison.Ordinal);
        Assert.Contains("out var s_s_end", generated, System.StringComparison.Ordinal);
        Assert.Contains("out var s_s_loop", generated, System.StringComparison.Ordinal);
        Assert.Contains("s_s_mid - 5", generated, System.StringComparison.Ordinal);

        int idxS1 = generated.IndexOf("    var fSpline =", System.StringComparison.Ordinal);
        int idxJ1 = generated.IndexOf("    var fJunction =", System.StringComparison.Ordinal);
        int idxS2 = generated.IndexOf("    fSpline =", System.StringComparison.Ordinal);
        Assert.True(0 <= idxS1 && idxS1 < idxJ1 && idxJ1 < idxS2);
        Assert.Equal(idxS1, generated.LastIndexOf("    var fSpline =", System.StringComparison.Ordinal));
        Assert.Equal(idxJ1, generated.LastIndexOf("    var fJunction =", System.StringComparison.Ordinal));

        (ParsedWorldCsx? parsed, IReadOnlyList<ParseDiagnostic> diag) = WorldCsxSerializer.Parse("World.csx", generated);
        Assert.Empty(diag);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Document.Props);
        Assert.Equal("一郷バス停", parsed.Document.Props[0].DisplayName);
        Assert.Equal(2, parsed.Document.Splines.Count);
        Assert.Single(parsed.Document.Junctions);
        Assert.Equal("MainApproach", parsed.Document.Splines[0].Key);
        Assert.Equal("駅前通り \"本線\" & <入口>", parsed.Document.Splines[0].DisplayName);
        Assert.Equal("北口交差点", parsed.Document.Junctions[0].DisplayName);
        Assert.Null(parsed.Document.Splines[1].DisplayName);
        Assert.Equal(2, parsed.Document.Splines[0].Props.Count);
        Assert.Null(parsed.Document.Splines[0].Props[0].Key);
        Assert.Equal("SpeedSign", parsed.Document.Splines[0].Props[1].Key);
        Assert.Single(parsed.Document.Splines[1].Extrusions);
        Assert.Null(parsed.Document.Splines[1].Extrusions[0].Key);
        Assert.Single(parsed.Document.Junctions[0].PathOverrides[0].Extrusions);
        Assert.Null(parsed.Document.Junctions[0].PathOverrides[0].Extrusions[0].Key);
        Assert.Equal(s1.PreBuildUserCode, parsed.Document.Splines[0].PreBuildUserCode);
        Assert.Equal(j1.PreBuildUserCode, parsed.Document.Junctions[0].PreBuildUserCode);
        Assert.Equal(AnchoredDistance.RelativeTo("s_mid", -5.0), parsed.Document.Splines[0].ExtrusionSkips[0].From);
        Assert.Equal("s_loop", parsed.Document.Splines[1].ConnectBezier!.OutputAnchorId);
        Assert.Equal(0.4, parsed.Document.Splines[1].ConnectBezier!.HandleScale, 6);
        Assert.Equal(doc.UserCode, parsed.Document.UserCode);
        Assert.Single(parsed.Document.Connections);
    }

    [Fact]
    public void WorldCsxRejectsTamperedCodeAndOrphanedPreBuildBlocksWithAccurateLines()
    {
        string tamperedFor = string.Join('\n',
        [
            "#load \"__Editor.csx\"",
            "{",
            "    // <editor-generated version=\"1\">",
            "    Camera.Locate(0, 0, 10, 10, 10, 0, 0, 0);",
            "    for (int i = 0; i < 3; i++) { Background.Add(\"Bg\"); }",
            "    // </editor-generated>",
            "}",
        ]);

        (ParsedWorldCsx? res1, IReadOnlyList<ParseDiagnostic> diag1) = WorldCsxSerializer.Parse("World.csx", tamperedFor);
        Assert.Null(res1);
        Assert.Contains(diag1, d => d.Severity == DiagnosticSeverity.Error && d.Line == 5);

        string orphanPreBuild = string.Join('\n',
        [
            "#load \"__Editor.csx\"",
            "{",
            "    // <editor-generated version=\"1\">",
            "    var f_s1 = Chunks[0, 0].BeginSpline(\"Spline1\", 0, 0, 0);",
            "    // <user-prebuild>",
            "    f_s1.SpeedLimit(1, 50);",
            "    // </user-prebuild>",
            "    f_s1.Curves.Straight(10);",
            "    var s1 = f_s1.Build();",
            "    // </editor-generated>",
            "}",
        ]);

        (ParsedWorldCsx? res2, IReadOnlyList<ParseDiagnostic> diag2) = WorldCsxSerializer.Parse("World.csx", orphanPreBuild);
        Assert.Null(res2);
        Assert.Contains(diag2, d => d.Severity == DiagnosticSeverity.Error && d.Line == 5);

        string unprefixedAnchorCsx = string.Join('\n',
        [
            "#load \"__Editor.csx\"",
            "{",
            "    // <editor-generated version=\"1\">",
            "    var fSpline = Chunks[0, 0].BeginSpline(\"Spline1\", 0, 0, 0);",
            "    fSpline.Curves.Straight(10, out var foo);",
            "    var s1 = fSpline.Build();",
            "    // </editor-generated>",
            "}",
        ]);

        (ParsedWorldCsx? res3, IReadOnlyList<ParseDiagnostic> diag3) = WorldCsxSerializer.Parse("World.csx", unprefixedAnchorCsx);
        Assert.Null(res3);
        Assert.Contains(diag3, d => d.Severity == DiagnosticSeverity.Error);
    }
}
