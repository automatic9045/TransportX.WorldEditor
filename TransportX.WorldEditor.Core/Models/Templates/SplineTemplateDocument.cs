using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Primitives;

namespace TransportX.WorldEditor.Core.Models.Templates;

/// <summary>
/// Represents a repeating prop rule configured on a <c>SplineTemplate</c>.
/// </summary>
public sealed class SplineTemplatePropRule
{
    public string? Key { get; set; }
    public List<string> ModelKeys { get; } = new List<string>();
    public PoseDegreesModel Pose { get; set; } = PoseDegreesModel.Identity;
    public double From { get; set; }
    public double Span { get; set; }
    public double Interval { get; set; }
}

/// <summary>
/// Represents a cross-section extrusion rule configured on a <c>SplineTemplate</c>.
/// </summary>
public sealed class SplineTemplateExtrusionRule
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
    public double From { get; set; }
}

/// <summary>
/// Represents a single-file spline template (<c>Templates/Splines/&lt;Key&gt;.csx</c>).
/// </summary>
public sealed class SplineTemplateDocument
{
    public string Key { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string OutletLayoutKey { get; set; } = string.Empty;
    public List<SplineTemplatePropRule> Props { get; } = new List<SplineTemplatePropRule>();
    public List<SplineTemplateExtrusionRule> Extrusions { get; } = new List<SplineTemplateExtrusionRule>();
    public string? UserCode { get; set; }
}
