using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Models.Placement;

/// <summary>
/// Initial editor/player camera location stored in <c>World.csx</c>.
/// </summary>
public sealed class CameraSettingsModel
{
    public WorldPoseDegreesModel Pose { get; set; } = new WorldPoseDegreesModel(
        ChunkIndexModel.Zero,
        PoseDegreesModel.FromTranslation(125.0, 10.0, 125.0));
}

/// <summary>
/// Player vehicle avatar XML path and optional initial pose in <c>World.csx</c>.
/// </summary>
public sealed class AvatarSettingsModel
{
    public string XmlPath { get; set; } = string.Empty;
    public WorldPoseDegreesModel? InitialPose { get; set; }
}

/// <summary>
/// Global directional light settings in <c>World.csx</c>.
/// </summary>
public sealed class DirectionalLightModel
{
    public string ColorHex { get; set; } = "#FFFFFF";
    public double DirectionX { get; set; } = -1.0;
    public double DirectionY { get; set; } = -4.0;
    public double DirectionZ { get; set; } = 2.0;
    public double Intensity { get; set; } = 100000.0;
}

/// <summary>
/// Static prop placed directly into a world chunk via <c>Chunks[x, z].PutProp(...)</c>.
/// </summary>
public sealed record ChunkPropPlacement(
    string Id,
    string ModelKey,
    ChunkIndexModel Chunk,
    PoseDegreesModel Pose,
    string? DisplayName = null);

/// <summary>
/// Origin mode of a placed spline factory in <c>World.csx</c>.
/// </summary>
public enum SplineOriginKind
{
    ChunkPose,
    FromSplineOutlet,
    FromJunctionPort,
}

/// <summary>
/// Specifies how a spline factory begins (chunk coordinate or downstream from an existing network port).
/// </summary>
public sealed record SplineOriginModel(
    SplineOriginKind Kind,
    ChunkIndexModel Chunk,
    PoseDegreesModel Pose,
    string? SourceNodeId = null,
    string? SourcePortKey = null)
{
    public static SplineOriginModel FromChunk(ChunkIndexModel chunk, PoseDegreesModel pose)
    {
        return new SplineOriginModel(SplineOriginKind.ChunkPose, chunk, pose);
    }

    public static SplineOriginModel FromSpline(string sourceSplineId)
    {
        return new SplineOriginModel(SplineOriginKind.FromSplineOutlet, ChunkIndexModel.Zero, PoseDegreesModel.Identity, sourceSplineId);
    }

    public static SplineOriginModel FromJunction(string sourceJunctionId, string portKey)
    {
        return new SplineOriginModel(SplineOriginKind.FromJunctionPort, ChunkIndexModel.Zero, PoseDegreesModel.Identity, sourceJunctionId, portKey);
    }
}

/// <summary>
/// Horizontal curve segment kind on <c>SplineFactoryCommand.Curves</c>.
/// </summary>
public enum SplineCurveSegmentKind
{
    Straight,
    ByRadius,
    ByCurvature,
}

/// <summary>
/// Single horizontal alignment segment on a placed spline.
/// </summary>
public sealed record SplineCurveSegment(
    SplineCurveSegmentKind Kind,
    double Value,
    double Length,
    string? OutputAnchorId = null);

/// <summary>
/// Vertical gradient segment kind on <c>SplineFactoryCommand.Gradients</c>.
/// </summary>
public enum SplineGradientSegmentKind
{
    Constant,
    TransitionByRadian,
    TransitionByDegree,
    TransitionByPercent,
    TransitionByPermil,
}

/// <summary>
/// Single vertical gradient segment on a placed spline.
/// </summary>
public sealed record SplineGradientSegment(
    SplineGradientSegmentKind Kind,
    double Delta,
    double Length,
    string? OutputAnchorId = null);

/// <summary>
/// Cant / superelevation segment kind on <c>SplineFactoryCommand.Cants</c>.
/// </summary>
public enum SplineCantSegmentKind
{
    Constant,
    TransitionToRadian,
    TransitionToDegree,
    TransitionToPercent,
    TransitionToHeight,
}

/// <summary>
/// Single cant segment on a placed spline.
/// </summary>
public sealed record SplineCantSegment(
    SplineCantSegmentKind Kind,
    double Target,
    double Gauge,
    double Length,
    string? OutputAnchorId = null);

/// <summary>
/// Identifies a port endpoint kind on a placed spline or junction.
/// </summary>
public enum PortTargetKind
{
    SplineInlet,
    SplineOutlet,
    JunctionPort,
}

/// <summary>
/// Symbolic reference to a network port of a placed spline or junction.
/// </summary>
public sealed record PortReferenceModel(
    string ElementId,
    PortTargetKind Kind,
    string? PortKey = null);

/// <summary>
/// Terminal Bezier interpolation linking a spline factory end to an existing <c>NetworkPort</c>.
/// </summary>
public sealed record SplineConnectBezierModel(
    PortReferenceModel TargetPort,
    double HandleScale = 0.5,
    string? OutputAnchorId = null);

/// <summary>
/// Repeating prop override added on a placed spline instance.
/// </summary>
public sealed class SplineInstancePropRule
{
    public string? Key { get; set; }
    public List<string> ModelKeys { get; } = new List<string>();
    public PoseDegreesModel Pose { get; set; } = PoseDegreesModel.Identity;
    public AnchoredDistance From { get; set; } = AnchoredDistance.Zero;
    public double Span { get; set; }
    public double Interval { get; set; } = 1.0;
    public int Count { get; set; } = int.MaxValue;
}

/// <summary>
/// Interval exclusion override (<c>SkipProp</c> or <c>SkipExtrusion</c>) on a placed spline instance.
/// When <see cref="From"/> and <see cref="To"/> are both <see langword="null"/>, skips across the entire spline.
/// </summary>
public sealed record SplineSkipRule(
    string Key,
    AnchoredDistance? From = null,
    AnchoredDistance? To = null);

/// <summary>
/// Represents a placed spline factory/element node in <c>World.csx</c>.
/// </summary>
public sealed class SplinePlacementNode
{
    public string Id { get; set; } = string.Empty;
    public string? Key { get; set; }
    public string? DisplayName { get; set; }
    public SplineOriginModel Origin { get; set; } = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity);
    public string? TemplateKey { get; set; }
    public List<SplineCurveSegment> Curves { get; } = new List<SplineCurveSegment>();
    public List<SplineGradientSegment> Gradients { get; } = new List<SplineGradientSegment>();
    public List<SplineCantSegment> Cants { get; } = new List<SplineCantSegment>();
    public SplineConnectBezierModel? ConnectBezier { get; set; }
    public List<SplineInstancePropRule> Props { get; } = new List<SplineInstancePropRule>();
    public List<SplineSkipRule> PropSkips { get; } = new List<SplineSkipRule>();
    public List<CurveExtrusionPlacementRule> Extrusions { get; } = new List<CurveExtrusionPlacementRule>();
    public List<SplineSkipRule> ExtrusionSkips { get; } = new List<SplineSkipRule>();
    public string? PreBuildUserCode { get; set; }
}

/// <summary>
/// Origin mode of a placed junction factory in <c>World.csx</c>.
/// </summary>
public enum JunctionOriginKind
{
    ChunkPose,
    FromSplineOutlet,
    FromJunctionPort,
}

/// <summary>
/// Specifies how a junction instance is positioned and optionally auto-connected at creation.
/// </summary>
public sealed record JunctionOriginModel(
    JunctionOriginKind Kind,
    ChunkIndexModel Chunk,
    PoseDegreesModel Pose,
    string? SourceNodeId = null,
    string? SourcePortKey = null,
    string? TargetPortKey = null)
{
    public static JunctionOriginModel FromChunk(ChunkIndexModel chunk, PoseDegreesModel pose)
    {
        return new JunctionOriginModel(JunctionOriginKind.ChunkPose, chunk, pose);
    }

    public static JunctionOriginModel FromSpline(string sourceSplineId, string targetPortKey)
    {
        return new JunctionOriginModel(JunctionOriginKind.FromSplineOutlet, ChunkIndexModel.Zero, PoseDegreesModel.Identity, sourceSplineId, null, targetPortKey);
    }

    public static JunctionOriginModel FromJunction(string sourceJunctionId, string sourcePortKey, string targetPortKey)
    {
        return new JunctionOriginModel(JunctionOriginKind.FromJunctionPort, ChunkIndexModel.Zero, PoseDegreesModel.Identity, sourceJunctionId, sourcePortKey, targetPortKey);
    }
}

/// <summary>
/// Per-instance override applied to <c>fJunction.Paths["pathKey"]</c> before <c>Build()</c>.
/// </summary>
public sealed class JunctionPathOverrideModel
{
    public string PathKey { get; set; } = string.Empty;
    public double? TrafficDensityFactor { get; set; }
    public List<CurvePropPlacementRule> Props { get; } = new List<CurvePropPlacementRule>();
    public List<CurveExtrusionPlacementRule> Extrusions { get; } = new List<CurveExtrusionPlacementRule>();
}

/// <summary>
/// Per-instance override applied to <c>fJunction.Curves["curveKey"]</c> before <c>Build()</c>.
/// </summary>
public sealed class JunctionCurveOverrideModel
{
    public string CurveKey { get; set; } = string.Empty;
    public List<CurvePropPlacementRule> Props { get; } = new List<CurvePropPlacementRule>();
    public List<CurveExtrusionPlacementRule> Extrusions { get; } = new List<CurveExtrusionPlacementRule>();
}

/// <summary>
/// Represents a placed junction factory/element node in <c>World.csx</c>.
/// </summary>
public sealed class JunctionPlacementNode
{
    public string Id { get; set; } = string.Empty;
    public string? Key { get; set; }
    public string? DisplayName { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public JunctionOriginModel Origin { get; set; } = JunctionOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity);
    public List<JunctionStaticPropPlacement> Props { get; } = new List<JunctionStaticPropPlacement>();
    public List<JunctionPathOverrideModel> PathOverrides { get; } = new List<JunctionPathOverrideModel>();
    public List<JunctionCurveOverrideModel> CurveOverrides { get; } = new List<JunctionCurveOverrideModel>();
    public string? PreBuildUserCode { get; set; }
}

/// <summary>
/// Represents an explicit <c>Network.Connect(portA, portB);</c> statement after element creation.
/// </summary>
public sealed record ExplicitPortConnection(
    PortReferenceModel From,
    PortReferenceModel To);

/// <summary>
/// Represents the canonical editor-managed world scene and network placement (<c>World.csx</c>).
/// </summary>
public sealed class WorldPlacementDocument
{
    public string RelativePath { get; set; } = "World.csx";
    public List<string> ExtraLoadScripts { get; } = new List<string>();
    public CameraSettingsModel Camera { get; set; } = new CameraSettingsModel();
    public AvatarSettingsModel? Avatar { get; set; }
    public string? EnvironmentPath { get; set; }
    public DirectionalLightModel DirectionalLight { get; set; } = new DirectionalLightModel();
    public List<string> BackgroundModels { get; } = new List<string>();
    public List<ChunkPropPlacement> Props { get; } = new List<ChunkPropPlacement>();
    public List<SplinePlacementNode> Splines { get; } = new List<SplinePlacementNode>();
    public List<JunctionPlacementNode> Junctions { get; } = new List<JunctionPlacementNode>();
    public List<ExplicitPortConnection> Connections { get; } = new List<ExplicitPortConnection>();
    public string? UserCode { get; set; }
}
