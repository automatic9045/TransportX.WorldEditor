using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Primitives;

namespace TransportX.WorldEditor.Core.Models.Templates;

/// <summary>
/// Represents a named connection port on a <c>JunctionTemplate</c>.
/// </summary>
public sealed record JunctionPortDefinition(
    string Key,
    string LayoutKey,
    PoseDegreesModel Pose);

/// <summary>
/// Represents an internal waiting/relay port pair created by <c>tJunction.AddRelay(...)</c>.
/// </summary>
public sealed record JunctionRelayDefinition(
    string InputKey,
    string OutputKey,
    string LayoutKey,
    PoseDegreesModel Pose);

/// <summary>
/// Segment kind along a junction lane path or auxiliary curve.
/// </summary>
public enum JunctionCurveSegmentKind
{
    StraightTo,
    BezierTo,
    StraightToEnd,
    BezierToEnd,
}

/// <summary>
/// Represents a single geometry segment on a junction path or auxiliary curve.
/// </summary>
public sealed record JunctionCurveSegment(
    JunctionCurveSegmentKind Kind,
    PoseDegreesModel TargetPose,
    double HandleScale = 0.5,
    string? OutputAnchorId = null);

/// <summary>
/// Segment kind along a junction lane path's <c>Width</c> timeline.
/// </summary>
public enum JunctionWidthSegmentKind
{
    Constant,
    TransitionTo,
}

/// <summary>
/// Represents a lane width segment on <c>JunctionPathTemplate.Width</c>.
/// </summary>
public sealed record JunctionWidthSegment(
    JunctionWidthSegmentKind Kind,
    double LeftWidth,
    double RightWidth,
    AnchoredDistance Length,
    string? OutputAnchorId = null);

/// <summary>
/// Represents a repeating prop rule on a junction path, auxiliary curve, or path/curve override.
/// </summary>
public sealed class CurvePropPlacementRule
{
    public List<string> ModelKeys { get; } = new List<string>();
    public PoseDegreesModel Pose { get; set; } = PoseDegreesModel.Identity;
    public AnchoredDistance From { get; set; } = AnchoredDistance.Zero;
    public double Span { get; set; }
    public double Interval { get; set; } = 1.0;
    public int Count { get; set; } = int.MaxValue;
}

/// <summary>
/// Represents a bounded cross-section extrusion rule along a curve, path, or spline instance.
/// </summary>
public sealed class CurveExtrusionPlacementRule
{
    public string? Key { get; set; }
    public string CrossSectionKey { get; set; } = string.Empty;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double BaseInterval { get; set; } = 5.0;
    public double MaxDeviation { get; set; } = 0.01;
    public double MaxAngleDeltaDeg { get; set; } = 2.0;
    public double VIntervalScale { get; set; } = 1.0;
    public double VOffset { get; set; }
    public AnchoredDistance From { get; set; } = AnchoredDistance.Zero;
    public AnchoredDistance? To { get; set; }
}

/// <summary>
/// Represents an internal lane path wired inside a <c>JunctionTemplate</c>.
/// </summary>
public sealed class JunctionPathTemplateModel
{
    public string Key { get; set; } = string.Empty;
    public string FromPortKey { get; set; } = string.Empty;
    public int FromPinIndex { get; set; }
    public string ToPortKey { get; set; } = string.Empty;
    public int ToPinIndex { get; set; }
    public List<JunctionCurveSegment> Segments { get; } = new List<JunctionCurveSegment>();
    public List<JunctionWidthSegment> WidthSegments { get; } = new List<JunctionWidthSegment>();
    public List<CurvePropPlacementRule> Props { get; } = new List<CurvePropPlacementRule>();
    public List<CurveExtrusionPlacementRule> Extrusions { get; } = new List<CurveExtrusionPlacementRule>();
    public double? TrafficDensityFactor { get; set; }
}

/// <summary>
/// Origin mode of an auxiliary <c>AddCurve</c> inside a <c>JunctionTemplate</c>.
/// </summary>
public enum JunctionCurveOriginKind
{
    AbsolutePose,
    PortPinRelative,
}

/// <summary>
/// Specifies the starting pose or port-pin anchor of a <c>JunctionCurveTemplateModel</c>.
/// </summary>
public sealed record JunctionCurveOriginModel(
    JunctionCurveOriginKind Kind,
    PoseDegreesModel Pose,
    string? PortKey = null,
    int PinIndex = 0,
    bool HasPortPinOffset = false)
{
    public static JunctionCurveOriginModel Absolute(PoseDegreesModel pose)
    {
        return new JunctionCurveOriginModel(JunctionCurveOriginKind.AbsolutePose, pose);
    }

    public static JunctionCurveOriginModel FromPortPin(string portKey, int pinIndex)
    {
        return new JunctionCurveOriginModel(JunctionCurveOriginKind.PortPinRelative, PoseDegreesModel.Identity, portKey, pinIndex, false);
    }

    public static JunctionCurveOriginModel FromPortPinWithOffset(string portKey, int pinIndex, PoseDegreesModel offset)
    {
        return new JunctionCurveOriginModel(JunctionCurveOriginKind.PortPinRelative, offset, portKey, pinIndex, true);
    }
}

/// <summary>
/// Represents an auxiliary non-traffic pose curve added via <c>tJunction.AddCurve(...)</c>.
/// </summary>
public sealed class JunctionCurveTemplateModel
{
    public string Key { get; set; } = string.Empty;
    public JunctionCurveOriginModel Origin { get; set; } = JunctionCurveOriginModel.Absolute(PoseDegreesModel.Identity);
    public List<JunctionCurveSegment> Segments { get; } = new List<JunctionCurveSegment>();
    public List<CurvePropPlacementRule> Props { get; } = new List<CurvePropPlacementRule>();
    public List<CurveExtrusionPlacementRule> Extrusions { get; } = new List<CurveExtrusionPlacementRule>();
}

/// <summary>
/// Represents a static prop placement attached to a junction template or junction instance.
/// </summary>
public sealed record JunctionStaticPropPlacement(
    string ModelKey,
    PoseDegreesModel Pose);

/// <summary>
/// Represents a single-file junction template (<c>Templates/Junctions/&lt;Key&gt;.csx</c>).
/// </summary>
public sealed class JunctionTemplateDocument
{
    public string Key { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public List<JunctionPortDefinition> Ports { get; } = new List<JunctionPortDefinition>();
    public List<JunctionRelayDefinition> Relays { get; } = new List<JunctionRelayDefinition>();
    public List<JunctionPathTemplateModel> Paths { get; } = new List<JunctionPathTemplateModel>();
    public List<JunctionCurveTemplateModel> Curves { get; } = new List<JunctionCurveTemplateModel>();
    public List<JunctionStaticPropPlacement> Props { get; } = new List<JunctionStaticPropPlacement>();
    public string? UserCode { get; set; }
}
