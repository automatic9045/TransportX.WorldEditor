using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Validates declaration-before-use ordering and resolves symbolic <see cref="AnchoredDistance"/>
/// references within spline instances, junction paths, and junction auxiliary curves.
/// </summary>
public static class DistanceAnchorResolver
{
    public static Dictionary<string, double> ValidateAndResolveSpline(
        SplinePlacementNode spline,
        IList<ValidationIssue> issues,
        double? connectBezierArcLength = null,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(spline);
        ArgumentNullException.ThrowIfNull(issues);

        Dictionary<string, double> anchors = new Dictionary<string, double>(StringComparer.Ordinal);
        double curveS = 0.0;

        foreach (SplineCurveSegment segment in spline.Curves)
        {
            curveS += segment.Length;
            RegisterAnchor(anchors, segment.OutputAnchorId, curveS, issues, $"SPLINE:{spline.Id}", sourcePath);
        }

        double gradientS = 0.0;
        foreach (SplineGradientSegment segment in spline.Gradients)
        {
            gradientS += segment.Length;
            RegisterAnchor(anchors, segment.OutputAnchorId, gradientS, issues, $"SPLINE:{spline.Id}", sourcePath);
        }

        double cantS = 0.0;
        foreach (SplineCantSegment segment in spline.Cants)
        {
            cantS += segment.Length;
            RegisterAnchor(anchors, segment.OutputAnchorId, cantS, issues, $"SPLINE:{spline.Id}", sourcePath);
        }

        if (spline.ConnectBezier is not null && !string.IsNullOrEmpty(spline.ConnectBezier.OutputAnchorId))
        {
            double bezierEndS = curveS + (connectBezierArcLength ?? 0.0);
            RegisterAnchor(anchors, spline.ConnectBezier.OutputAnchorId, bezierEndS, issues, $"SPLINE:{spline.Id}", sourcePath);
        }

        foreach (SplineInstancePropRule prop in spline.Props)
        {
            ValidateAnchoredDistance(prop.From, anchors, issues, $"スプライン '{spline.Id}' の PutProp.From", sourcePath);
        }

        foreach (SplineSkipRule skip in spline.PropSkips)
        {
            ValidateSkipRule(skip, anchors, issues, $"スプライン '{spline.Id}' の SkipProp '{skip.Key}'", sourcePath);
        }

        foreach (CurveExtrusionPlacementRule extrusion in spline.Extrusions)
        {
            ValidateExtrusionRule(extrusion, anchors, issues, $"スプライン '{spline.Id}' の PutExtrusion", sourcePath);
        }

        foreach (SplineSkipRule skip in spline.ExtrusionSkips)
        {
            ValidateSkipRule(skip, anchors, issues, $"スプライン '{spline.Id}' の SkipExtrusion '{skip.Key}'", sourcePath);
        }

        return anchors;
    }

    public static Dictionary<string, double> ValidateAndResolveJunctionPath(
        JunctionPathTemplateModel path,
        IList<ValidationIssue> issues,
        IReadOnlyDictionary<string, double>? knownCurveAnchors = null,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(issues);

        Dictionary<string, double> anchors = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (JunctionCurveSegment segment in path.Segments)
        {
            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                double value = knownCurveAnchors is not null && knownCurveAnchors.TryGetValue(segment.OutputAnchorId, out double known)
                    ? known
                    : 0.0;
                RegisterAnchor(anchors, segment.OutputAnchorId, value, issues, $"JUNCTION_PATH:{path.Key}", sourcePath);
            }
        }

        double widthS = 0.0;
        foreach (JunctionWidthSegment widthSeg in path.WidthSegments)
        {
            if (!widthSeg.Length.TryResolve(anchors, out double resolvedLength))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "ANCHOR_UNDECLARED",
                    $"進路パス '{path.Key}' の Width セグメントが未宣言または後方宣言のアンカー '{widthSeg.Length.AnchorId}' を参照しています。",
                    sourcePath));
            }
            else
            {
                widthS += resolvedLength;
            }

            RegisterAnchor(anchors, widthSeg.OutputAnchorId, widthS, issues, $"JUNCTION_PATH:{path.Key}", sourcePath);
        }

        foreach (CurvePropPlacementRule prop in path.Props)
        {
            ValidateAnchoredDistance(prop.From, anchors, issues, $"進路パス '{path.Key}' の PutProp.From", sourcePath);
        }

        foreach (CurveExtrusionPlacementRule extrusion in path.Extrusions)
        {
            ValidateExtrusionRule(extrusion, anchors, issues, $"進路パス '{path.Key}' の PutExtrusion", sourcePath);
        }

        return anchors;
    }

    public static Dictionary<string, double> ValidateAndResolveJunctionCurve(
        JunctionCurveTemplateModel curve,
        IList<ValidationIssue> issues,
        IReadOnlyDictionary<string, double>? knownCurveAnchors = null,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(issues);

        Dictionary<string, double> anchors = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (JunctionCurveSegment segment in curve.Segments)
        {
            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                double value = knownCurveAnchors is not null && knownCurveAnchors.TryGetValue(segment.OutputAnchorId, out double known)
                    ? known
                    : 0.0;
                RegisterAnchor(anchors, segment.OutputAnchorId, value, issues, $"JUNCTION_CURVE:{curve.Key}", sourcePath);
            }
        }

        foreach (CurvePropPlacementRule prop in curve.Props)
        {
            ValidateAnchoredDistance(prop.From, anchors, issues, $"補助曲線 '{curve.Key}' の PutProp.From", sourcePath);
        }

        foreach (CurveExtrusionPlacementRule extrusion in curve.Extrusions)
        {
            ValidateExtrusionRule(extrusion, anchors, issues, $"補助曲線 '{curve.Key}' の PutExtrusion", sourcePath);
        }

        return anchors;
    }

    private static void RegisterAnchor(
        Dictionary<string, double> anchors,
        string? anchorId,
        double value,
        IList<ValidationIssue> issues,
        string scopeLabel,
        string? sourcePath)
    {
        if (string.IsNullOrEmpty(anchorId))
        {
            return;
        }

        if (!anchors.TryAdd(anchorId, value))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "ANCHOR_DUPLICATE",
                $"スコープ '{scopeLabel}' 内でアンカー識別子 '{anchorId}' が重複定義されています。",
                sourcePath));
        }
    }

    private static void ValidateSkipRule(
        SplineSkipRule skip,
        IReadOnlyDictionary<string, double> anchors,
        IList<ValidationIssue> issues,
        string context,
        string? sourcePath)
    {
        if (skip.From.HasValue)
        {
            ValidateAnchoredDistance(skip.From.Value, anchors, issues, $"{context}.From", sourcePath);
        }

        if (skip.To.HasValue)
        {
            ValidateAnchoredDistance(skip.To.Value, anchors, issues, $"{context}.To", sourcePath);
        }
    }

    private static void ValidateExtrusionRule(
        CurveExtrusionPlacementRule extrusion,
        IReadOnlyDictionary<string, double> anchors,
        IList<ValidationIssue> issues,
        string context,
        string? sourcePath)
    {
        ValidateAnchoredDistance(extrusion.From, anchors, issues, $"{context}.From", sourcePath);
        if (extrusion.To.HasValue)
        {
            ValidateAnchoredDistance(extrusion.To.Value, anchors, issues, $"{context}.To", sourcePath);
        }
    }

    private static void ValidateAnchoredDistance(
        AnchoredDistance distance,
        IReadOnlyDictionary<string, double> anchors,
        IList<ValidationIssue> issues,
        string context,
        string? sourcePath)
    {
        if (!distance.IsRelative)
        {
            return;
        }

        if (!anchors.ContainsKey(distance.AnchorId!))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "ANCHOR_UNDECLARED",
                $"{context} が未宣言のアンカー '{distance.AnchorId}' を参照しています。",
                sourcePath));
        }
    }
}
