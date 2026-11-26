using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Resolved longitudinal distance (<c>s</c> in meters) for a declared <c>out var</c> anchor inside a spline.
/// </summary>
public readonly record struct ResolvedAnchorInfo(
    string AnchorId,
    double ResolvedS,
    string SourceTrack);

/// <summary>
/// Candidate port eligible for <c>ConnectBezier</c> connection from a spline outlet.
/// </summary>
public readonly record struct CompatiblePortCandidate(
    PortReferenceModel PortRef,
    string DisplayLabel,
    string LayoutKey);

/// <summary>
/// Pure-domain utilities for collecting spline anchors, expanding anchor references on deletion/rename,
/// filtering compatible topological <c>ConnectBezier</c> targets, and deep-cloning splines for Undo/Redo.
/// </summary>
public static class SplineAnchorUtilities
{
    public static IReadOnlyList<ResolvedAnchorInfo> CollectSplineAnchors(
        SplinePlacementNode spline,
        double? connectBezierArcLength = null)
    {
        ArgumentNullException.ThrowIfNull(spline);

        List<ResolvedAnchorInfo> result = new();
        double curveS = 0.0;
        foreach (SplineCurveSegment segment in spline.Curves)
        {
            curveS += segment.Length;
            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                result.Add(new ResolvedAnchorInfo(segment.OutputAnchorId, curveS, "Curves"));
            }
        }

        double gradientS = 0.0;
        foreach (SplineGradientSegment segment in spline.Gradients)
        {
            gradientS += segment.Length;
            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                result.Add(new ResolvedAnchorInfo(segment.OutputAnchorId, gradientS, "Gradients"));
            }
        }

        double cantS = 0.0;
        foreach (SplineCantSegment segment in spline.Cants)
        {
            cantS += segment.Length;
            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                result.Add(new ResolvedAnchorInfo(segment.OutputAnchorId, cantS, "Cants"));
            }
        }

        if (spline.ConnectBezier is not null && !string.IsNullOrEmpty(spline.ConnectBezier.OutputAnchorId))
        {
            double bezierEndS = curveS + (connectBezierArcLength ?? 0.0);
            result.Add(new ResolvedAnchorInfo(spline.ConnectBezier.OutputAnchorId, bezierEndS, "ConnectBezier"));
        }

        return result;
    }

    public static double ComputeCurveTotalLength(
        SplinePlacementNode spline,
        double? connectBezierArcLength = null)
    {
        ArgumentNullException.ThrowIfNull(spline);

        double total = 0.0;
        foreach (SplineCurveSegment segment in spline.Curves)
        {
            total += segment.Length;
        }

        if (spline.ConnectBezier is not null && connectBezierArcLength.HasValue)
        {
            total += connectBezierArcLength.Value;
        }

        return total;
    }

    public static int ExpandAnchorReferencesToAbsolute(
        SplinePlacementNode spline,
        string anchorId,
        double resolvedS)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (string.IsNullOrEmpty(anchorId))
        {
            return 0;
        }

        int expandedCount = 0;

        foreach (SplineInstancePropRule prop in spline.Props)
        {
            if (TryExpandDistance(prop.From, anchorId, resolvedS, out AnchoredDistance expandedFrom))
            {
                prop.From = expandedFrom;
                expandedCount++;
            }
        }

        for (int i = 0; i < spline.PropSkips.Count; i++)
        {
            if (TryExpandSkip(spline.PropSkips[i], anchorId, resolvedS, out SplineSkipRule updated, out int hits))
            {
                spline.PropSkips[i] = updated;
                expandedCount += hits;
            }
        }

        foreach (CurveExtrusionPlacementRule extrusion in spline.Extrusions)
        {
            if (TryExpandDistance(extrusion.From, anchorId, resolvedS, out AnchoredDistance expandedFrom))
            {
                extrusion.From = expandedFrom;
                expandedCount++;
            }

            if (extrusion.To.HasValue && TryExpandDistance(extrusion.To.Value, anchorId, resolvedS, out AnchoredDistance expandedTo))
            {
                extrusion.To = expandedTo;
                expandedCount++;
            }
        }

        for (int i = 0; i < spline.ExtrusionSkips.Count; i++)
        {
            if (TryExpandSkip(spline.ExtrusionSkips[i], anchorId, resolvedS, out SplineSkipRule updated, out int hits))
            {
                spline.ExtrusionSkips[i] = updated;
                expandedCount += hits;
            }
        }

        return expandedCount;
    }

    public static IReadOnlyList<CompatiblePortCandidate> FindCompatibleConnectBezierTargets(
        WorldProject project,
        SplinePlacementNode sourceSpline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceSpline);

        if (string.IsNullOrEmpty(sourceSpline.TemplateKey)
            || !project.SplineTemplates.TryGetValue(sourceSpline.TemplateKey, out SplineTemplateDocument? sourceTemplate)
            || string.IsNullOrEmpty(sourceTemplate.OutletLayoutKey)
            || !project.LaneLayouts.TryGetValue(sourceTemplate.OutletLayoutKey, out LaneLayoutDocument? sourceLayout))
        {
            return Array.Empty<CompatiblePortCandidate>();
        }

        List<CompatiblePortCandidate> candidates = new();

        foreach (SplinePlacementNode other in project.Placement.Splines)
        {
            if (string.Equals(other.Id, sourceSpline.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrEmpty(other.TemplateKey)
                || !project.SplineTemplates.TryGetValue(other.TemplateKey, out SplineTemplateDocument? otherTemplate)
                || string.IsNullOrEmpty(otherTemplate.OutletLayoutKey)
                || !project.LaneLayouts.TryGetValue(otherTemplate.OutletLayoutKey, out LaneLayoutDocument? otherOutletLayout))
            {
                continue;
            }

            LaneLayoutDocument otherInletLayout = otherOutletLayout.CreateOpposition();
            PortReferenceModel inletRef = new(other.Id, PortTargetKind.SplineInlet);
            if (!IsPortOccupiedByOther(project.Placement, inletRef, sourceSpline.Id)
                && sourceLayout.CanConnectTo(otherInletLayout)
                && !WouldCreateTopologyCycle(project.Placement, sourceSpline.Id, other.Id))
            {
                string label = string.IsNullOrEmpty(other.Key)
                    ? $"{other.Id}.Inlet ({otherTemplate.OutletLayoutKey})"
                    : $"{other.Key} [{other.Id}].Inlet ({otherTemplate.OutletLayoutKey})";
                candidates.Add(new CompatiblePortCandidate(inletRef, label, otherTemplate.OutletLayoutKey));
            }

            PortReferenceModel outletRef = new(other.Id, PortTargetKind.SplineOutlet);
            if (!IsPortOccupiedByOther(project.Placement, outletRef, sourceSpline.Id)
                && sourceLayout.CanConnectTo(otherOutletLayout)
                && !WouldCreateTopologyCycle(project.Placement, sourceSpline.Id, other.Id))
            {
                string label = string.IsNullOrEmpty(other.Key)
                    ? $"{other.Id}.Outlet ({otherTemplate.OutletLayoutKey})"
                    : $"{other.Key} [{other.Id}].Outlet ({otherTemplate.OutletLayoutKey})";
                candidates.Add(new CompatiblePortCandidate(outletRef, label, otherTemplate.OutletLayoutKey));
            }
        }

        foreach (JunctionPlacementNode junction in project.Placement.Junctions)
        {
            if (!project.JunctionTemplates.TryGetValue(junction.TemplateKey, out JunctionTemplateDocument? jTpl))
            {
                continue;
            }

            foreach (JunctionPortDefinition portDef in jTpl.Ports)
            {
                if (!project.LaneLayouts.TryGetValue(portDef.LayoutKey, out LaneLayoutDocument? portLayout))
                {
                    continue;
                }

                PortReferenceModel portRef = new(junction.Id, PortTargetKind.JunctionPort, portDef.Key);
                if (!IsPortOccupiedByOther(project.Placement, portRef, sourceSpline.Id)
                    && sourceLayout.CanConnectTo(portLayout)
                    && !WouldCreateTopologyCycle(project.Placement, sourceSpline.Id, junction.Id))
                {
                    string label = string.IsNullOrEmpty(junction.Key)
                        ? $"{junction.Id}.{portDef.Key} ({portDef.LayoutKey})"
                        : $"{junction.Key} [{junction.Id}].{portDef.Key} ({portDef.LayoutKey})";
                    candidates.Add(new CompatiblePortCandidate(portRef, label, portDef.LayoutKey));
                }
            }
        }

        return candidates;
    }

    public static bool IsPortOccupiedByOther(
        WorldPlacementDocument placement,
        PortReferenceModel port,
        string excludingSplineId)
    {
        ArgumentNullException.ThrowIfNull(placement);

        if (port.Kind == PortTargetKind.SplineInlet)
        {
            foreach (SplinePlacementNode s in placement.Splines)
            {
                if (string.Equals(s.Id, port.ElementId, StringComparison.Ordinal)
                    && s.Origin.Kind != SplineOriginKind.ChunkPose)
                {
                    return true;
                }

                if (!string.Equals(s.Id, excludingSplineId, StringComparison.Ordinal)
                    && s.ConnectBezier is not null
                    && s.ConnectBezier.TargetPort == port)
                {
                    return true;
                }
            }

            return false;
        }

        if (port.Kind == PortTargetKind.SplineOutlet)
        {
            foreach (SplinePlacementNode s in placement.Splines)
            {
                if (string.Equals(s.Id, port.ElementId, StringComparison.Ordinal)
                    && s.ConnectBezier is not null)
                {
                    return true;
                }

                if (s.Origin.Kind == SplineOriginKind.FromSplineOutlet
                    && string.Equals(s.Origin.SourceNodeId, port.ElementId, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(s.Id, excludingSplineId, StringComparison.Ordinal)
                    && s.ConnectBezier is not null
                    && s.ConnectBezier.TargetPort == port)
                {
                    return true;
                }
            }

            foreach (JunctionPlacementNode j in placement.Junctions)
            {
                if (j.Origin.Kind == JunctionOriginKind.FromSplineOutlet
                    && string.Equals(j.Origin.SourceNodeId, port.ElementId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // PortTargetKind.JunctionPort
        foreach (JunctionPlacementNode j in placement.Junctions)
        {
            if (string.Equals(j.Id, port.ElementId, StringComparison.Ordinal)
                && j.Origin.Kind != JunctionOriginKind.ChunkPose
                && string.Equals(j.Origin.TargetPortKey, port.PortKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (j.Origin.Kind == JunctionOriginKind.FromJunctionPort
                && string.Equals(j.Origin.SourceNodeId, port.ElementId, StringComparison.Ordinal)
                && string.Equals(j.Origin.SourcePortKey, port.PortKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (SplinePlacementNode s in placement.Splines)
        {
            if (s.Origin.Kind == SplineOriginKind.FromJunctionPort
                && string.Equals(s.Origin.SourceNodeId, port.ElementId, StringComparison.Ordinal)
                && string.Equals(s.Origin.SourcePortKey, port.PortKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(s.Id, excludingSplineId, StringComparison.Ordinal)
                && s.ConnectBezier is not null
                && s.ConnectBezier.TargetPort == port)
            {
                return true;
            }
        }

        return false;
    }

    public static bool WouldCreateTopologyCycle(
        WorldPlacementDocument placement,
        string sourceSplineId,
        string targetElementId)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (string.Equals(sourceSplineId, targetElementId, StringComparison.Ordinal))
        {
            return true;
        }

        // Check whether sourceSplineId is reachable as an upstream dependency of targetElementId
        // (If targetElementId depends on sourceSplineId, adding sourceSplineId -> targetElementId creates a cycle.)
        Dictionary<string, List<string>> upstreamParents = new(StringComparer.Ordinal);
        foreach (SplinePlacementNode s in placement.Splines)
        {
            if (!upstreamParents.TryGetValue(s.Id, out List<string>? parents))
            {
                parents = new();
                upstreamParents[s.Id] = parents;
            }

            if (s.Origin.Kind != SplineOriginKind.ChunkPose && !string.IsNullOrEmpty(s.Origin.SourceNodeId))
            {
                parents.Add(s.Origin.SourceNodeId);
            }

            if (s.ConnectBezier is not null && !string.Equals(s.Id, sourceSplineId, StringComparison.Ordinal))
            {
                parents.Add(s.ConnectBezier.TargetPort.ElementId);
            }
        }

        foreach (JunctionPlacementNode j in placement.Junctions)
        {
            if (!upstreamParents.TryGetValue(j.Id, out List<string>? parents))
            {
                parents = new();
                upstreamParents[j.Id] = parents;
            }

            if (j.Origin.Kind != JunctionOriginKind.ChunkPose && !string.IsNullOrEmpty(j.Origin.SourceNodeId))
            {
                parents.Add(j.Origin.SourceNodeId);
            }
        }

        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<string> queue = new();
        queue.Enqueue(targetElementId);

        while (0 < queue.Count)
        {
            string current = queue.Dequeue();
            if (string.Equals(current, sourceSplineId, StringComparison.Ordinal))
            {
                return true;
            }

            if (!visited.Add(current) || !upstreamParents.TryGetValue(current, out List<string>? parents))
            {
                continue;
            }

            foreach (string parent in parents)
            {
                queue.Enqueue(parent);
            }
        }

        return false;
    }

    public static SplinePlacementNode DeepCloneSpline(SplinePlacementNode source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SplinePlacementNode clone = new()
        {
            Id = source.Id,
            Key = source.Key,
            Origin = source.Origin,
            TemplateKey = source.TemplateKey,
            ConnectBezier = source.ConnectBezier,
            PreBuildUserCode = source.PreBuildUserCode,
        };

        clone.Curves.AddRange(source.Curves);
        clone.Gradients.AddRange(source.Gradients);
        clone.Cants.AddRange(source.Cants);

        foreach (SplineInstancePropRule prop in source.Props)
        {
            SplineInstancePropRule propCopy = new()
            {
                Key = prop.Key,
                Pose = prop.Pose,
                From = prop.From,
                Span = prop.Span,
                Interval = prop.Interval,
                Count = prop.Count,
            };
            propCopy.ModelKeys.AddRange(prop.ModelKeys);
            clone.Props.Add(propCopy);
        }

        clone.PropSkips.AddRange(source.PropSkips);

        foreach (CurveExtrusionPlacementRule ext in source.Extrusions)
        {
            CurveExtrusionPlacementRule extCopy = new()
            {
                Key = ext.Key,
                CrossSectionKey = ext.CrossSectionKey,
                OffsetX = ext.OffsetX,
                OffsetY = ext.OffsetY,
                BaseInterval = ext.BaseInterval,
                MaxDeviation = ext.MaxDeviation,
                MaxAngleDeltaDeg = ext.MaxAngleDeltaDeg,
                VIntervalScale = ext.VIntervalScale,
                VOffset = ext.VOffset,
                From = ext.From,
                To = ext.To,
            };
            clone.Extrusions.Add(extCopy);
        }

        clone.ExtrusionSkips.AddRange(source.ExtrusionSkips);
        return clone;
    }

    public static void CopySpline(SplinePlacementNode source, SplinePlacementNode destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        SplinePlacementNode copy = DeepCloneSpline(source);
        destination.Id = copy.Id;
        destination.Key = copy.Key;
        destination.Origin = copy.Origin;
        destination.TemplateKey = copy.TemplateKey;
        destination.ConnectBezier = copy.ConnectBezier;
        destination.PreBuildUserCode = copy.PreBuildUserCode;

        destination.Curves.Clear();
        destination.Curves.AddRange(copy.Curves);

        destination.Gradients.Clear();
        destination.Gradients.AddRange(copy.Gradients);

        destination.Cants.Clear();
        destination.Cants.AddRange(copy.Cants);

        destination.Props.Clear();
        destination.Props.AddRange(copy.Props);

        destination.PropSkips.Clear();
        destination.PropSkips.AddRange(copy.PropSkips);

        destination.Extrusions.Clear();
        destination.Extrusions.AddRange(copy.Extrusions);

        destination.ExtrusionSkips.Clear();
        destination.ExtrusionSkips.AddRange(copy.ExtrusionSkips);
    }

    public static SplineTemplateDocument DeepCloneSplineTemplate(SplineTemplateDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        SplineTemplateDocument clone = new()
        {
            Key = source.Key,
            RelativePath = source.RelativePath,
            OutletLayoutKey = source.OutletLayoutKey,
            UserCode = source.UserCode,
        };

        foreach (SplineTemplatePropRule prop in source.Props)
        {
            SplineTemplatePropRule copy = new()
            {
                Key = prop.Key,
                Pose = prop.Pose,
                From = prop.From,
                Span = prop.Span,
                Interval = prop.Interval,
            };
            copy.ModelKeys.AddRange(prop.ModelKeys);
            clone.Props.Add(copy);
        }

        foreach (SplineTemplateExtrusionRule ext in source.Extrusions)
        {
            SplineTemplateExtrusionRule copy = new()
            {
                Key = ext.Key,
                CrossSectionKey = ext.CrossSectionKey,
                OffsetX = ext.OffsetX,
                OffsetY = ext.OffsetY,
                BaseInterval = ext.BaseInterval,
                MaxDeviation = ext.MaxDeviation,
                MaxAngleDeltaDeg = ext.MaxAngleDeltaDeg,
                VIntervalScale = ext.VIntervalScale,
                VOffset = ext.VOffset,
                From = ext.From,
            };
            clone.Extrusions.Add(copy);
        }

        return clone;
    }

    public static void CopySplineTemplate(SplineTemplateDocument source, SplineTemplateDocument destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        SplineTemplateDocument copy = DeepCloneSplineTemplate(source);
        destination.Key = copy.Key;
        destination.RelativePath = copy.RelativePath;
        destination.OutletLayoutKey = copy.OutletLayoutKey;
        destination.UserCode = copy.UserCode;

        destination.Props.Clear();
        destination.Props.AddRange(copy.Props);

        destination.Extrusions.Clear();
        destination.Extrusions.AddRange(copy.Extrusions);
    }

    private static bool TryExpandDistance(
        AnchoredDistance dist,
        string anchorId,
        double resolvedS,
        out AnchoredDistance expanded)
    {
        if (dist.IsRelative && string.Equals(dist.AnchorId, anchorId, StringComparison.Ordinal))
        {
            expanded = AnchoredDistance.Absolute(resolvedS + dist.Offset);
            return true;
        }

        expanded = dist;
        return false;
    }

    private static bool TryExpandSkip(
        SplineSkipRule skip,
        string anchorId,
        double resolvedS,
        out SplineSkipRule updated,
        out int hits)
    {
        hits = 0;
        AnchoredDistance? newFrom = skip.From;
        AnchoredDistance? newTo = skip.To;

        if (skip.From.HasValue && TryExpandDistance(skip.From.Value, anchorId, resolvedS, out AnchoredDistance expFrom))
        {
            newFrom = expFrom;
            hits++;
        }

        if (skip.To.HasValue && TryExpandDistance(skip.To.Value, anchorId, resolvedS, out AnchoredDistance expTo))
        {
            newTo = expTo;
            hits++;
        }

        updated = 0 < hits ? skip with { From = newFrom, To = newTo } : skip;
        return 0 < hits;
    }
}
