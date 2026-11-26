using System;
using System.Collections.Generic;
using System.Numerics;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Pure-math 3D local pose representation matching TransportX left-handed <c>Pose</c> multiplication.
/// </summary>
public readonly record struct Pose3D(Vector3 Position, Quaternion Orientation)
{
    public static Pose3D Identity => new Pose3D(Vector3.Zero, Quaternion.Identity);

    public static Pose3D FromDegrees(PoseDegreesModel pose)
    {
        float pitch = float.DegreesToRadians((float)pose.RotationX);
        float yaw = float.DegreesToRadians((float)pose.RotationY);
        float roll = float.DegreesToRadians((float)pose.RotationZ);
        return new Pose3D(
            new Vector3((float)pose.X, (float)pose.Y, (float)pose.Z),
            Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll));
    }

    public static Pose3D CreateRotationY(float radians)
    {
        return new Pose3D(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians));
    }

    /// <summary>
    /// Computes <c>local * parent</c> using the same order as <c>TransportX.Pose.operator *</c>.
    /// </summary>
    public static Pose3D Compose(Pose3D local, Pose3D parent)
    {
        Vector3 position = parent.Position + Vector3.Transform(local.Position, parent.Orientation);
        Quaternion orientation = parent.Orientation * local.Orientation;
        return new Pose3D(position, orientation);
    }

    public static Pose3D Inverse(Pose3D pose)
    {
        Quaternion inv = Quaternion.Inverse(pose.Orientation);
        Vector3 pos = Vector3.Transform(-pose.Position, inv);
        return new Pose3D(pos, inv);
    }
}

/// <summary>
/// Describes an available port or relay endpoint exposed by a <see cref="JunctionTemplateDocument"/>.
/// </summary>
public readonly record struct AvailableJunctionPortInfo(
    string PortKey,
    string LayoutKey,
    bool IsRelay,
    bool IsRelayOutput,
    PoseDegreesModel Pose);

/// <summary>
/// Result of sampling a junction wired path or auxiliary curve into local 3D points and <c>out var s</c> anchors.
/// </summary>
public sealed record JunctionSampledCurveResult(
    IReadOnlyList<Vector3> Points,
    double TotalLength,
    IReadOnlyList<ResolvedAnchorInfo> Anchors);

/// <summary>
/// Pure-domain utilities for <see cref="JunctionTemplateDocument"/> / <see cref="JunctionPlacementNode"/>:
/// pose sampling, anchor collection &amp; deletion expansion, port topology compatibility checks, and deep cloning.
/// </summary>
public static class JunctionAnchorUtilities
{
    public static IReadOnlyList<AvailableJunctionPortInfo> EnumerateAvailablePortKeys(JunctionTemplateDocument template)
    {
        ArgumentNullException.ThrowIfNull(template);

        List<AvailableJunctionPortInfo> ports = new();
        foreach (JunctionPortDefinition port in template.Ports)
        {
            ports.Add(new AvailableJunctionPortInfo(port.Key, port.LayoutKey, false, false, port.Pose));
        }

        foreach (JunctionRelayDefinition relay in template.Relays)
        {
            ports.Add(new AvailableJunctionPortInfo(relay.InputKey, relay.LayoutKey, true, false, relay.Pose));
            ports.Add(new AvailableJunctionPortInfo(relay.OutputKey, relay.LayoutKey, true, true, relay.Pose));
        }

        return ports;
    }

    public static bool TryGetPortDefinition(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        string portKey,
        out LaneLayoutDocument? layout,
        out Pose3D portPose)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(layouts);

        foreach (JunctionPortDefinition port in template.Ports)
        {
            if (string.Equals(port.Key, portKey, StringComparison.Ordinal))
            {
                layouts.TryGetValue(port.LayoutKey, out layout);
                portPose = Pose3D.FromDegrees(port.Pose);
                return true;
            }
        }

        foreach (JunctionRelayDefinition relay in template.Relays)
        {
            if (string.Equals(relay.InputKey, portKey, StringComparison.Ordinal))
            {
                layouts.TryGetValue(relay.LayoutKey, out layout);
                portPose = Pose3D.FromDegrees(relay.Pose);
                return true;
            }

            if (string.Equals(relay.OutputKey, portKey, StringComparison.Ordinal))
            {
                layout = layouts.TryGetValue(relay.LayoutKey, out LaneLayoutDocument? baseLayout)
                    ? baseLayout.CreateOpposition()
                    : null;
                Pose3D rawPose = Pose3D.FromDegrees(relay.Pose);
                portPose = Pose3D.Compose(Pose3D.CreateRotationY(float.Pi), rawPose);
                return true;
            }
        }

        layout = null;
        portPose = Pose3D.Identity;
        return false;
    }

    public static bool TryResolvePortPinPose(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        string portKey,
        int pinIndex,
        out Pose3D pinLocalPose)
    {
        if (!TryGetPortDefinition(template, layouts, portKey, out LaneLayoutDocument? layout, out Pose3D portPose)
            || layout is null
            || pinIndex < 0
            || layout.Lanes.Count <= pinIndex)
        {
            pinLocalPose = Pose3D.Identity;
            return false;
        }

        float laneX = (float)layout.Lanes[pinIndex].X;
        pinLocalPose = Pose3D.Compose(new Pose3D(new Vector3(laneX, 0f, 0f), Quaternion.Identity), portPose);
        return true;
    }

    public static JunctionSampledCurveResult SamplePathGeometry(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        JunctionPathTemplateModel path,
        int stepsPerBezier = 24)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(path);

        if (!TryResolvePortPinPose(template, layouts, path.FromPortKey, path.FromPinIndex, out Pose3D fromPinPose)
            || !TryResolvePortPinPose(template, layouts, path.ToPortKey, path.ToPinIndex, out Pose3D toPinPose))
        {
            return new JunctionSampledCurveResult(Array.Empty<Vector3>(), 0.0, Array.Empty<ResolvedAnchorInfo>());
        }

        Pose3D startPose = Pose3D.Compose(Pose3D.CreateRotationY(float.Pi), fromPinPose);
        return SampleCurveSegments(startPose, toPinPose, path.Segments, stepsPerBezier, finalizeWithBezierToEnd: true);
    }

    public static JunctionSampledCurveResult SampleCurveGeometry(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        JunctionCurveTemplateModel curve,
        int stepsPerBezier = 24)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(curve);

        Pose3D startPose;
        if (curve.Origin.Kind == JunctionCurveOriginKind.PortPinRelative)
        {
            if (string.IsNullOrEmpty(curve.Origin.PortKey)
                || !TryResolvePortPinPose(template, layouts, curve.Origin.PortKey, curve.Origin.PinIndex, out Pose3D pinPose))
            {
                return new JunctionSampledCurveResult(Array.Empty<Vector3>(), 0.0, Array.Empty<ResolvedAnchorInfo>());
            }

            Pose3D rotatedPin = Pose3D.Compose(Pose3D.CreateRotationY(float.Pi), pinPose);
            Pose3D offsetPose = curve.Origin.HasPortPinOffset ? Pose3D.FromDegrees(curve.Origin.Pose) : Pose3D.Identity;
            startPose = Pose3D.Compose(offsetPose, rotatedPin);
        }
        else
        {
            startPose = Pose3D.FromDegrees(curve.Origin.Pose);
        }

        return SampleCurveSegments(startPose, null, curve.Segments, stepsPerBezier, finalizeWithBezierToEnd: false);
    }

    public static JunctionSampledCurveResult SampleCurveSegments(
        Pose3D startPose,
        Pose3D? endPose,
        IReadOnlyList<JunctionCurveSegment> segments,
        int stepsPerBezier = 24,
        bool finalizeWithBezierToEnd = false)
    {
        ArgumentNullException.ThrowIfNull(segments);

        int safeSteps = int.Max(4, stepsPerBezier);
        List<Vector3> points = new();
        points.Add(startPose.Position);

        Pose3D currentPose = startPose;
        double totalLength = 0.0;
        List<ResolvedAnchorInfo> anchors = new();
        bool hasFinalized = false;

        foreach (JunctionCurveSegment segment in segments)
        {
            bool isToEnd = segment.Kind == JunctionCurveSegmentKind.StraightToEnd
                || segment.Kind == JunctionCurveSegmentKind.BezierToEnd;
            Pose3D targetPose = isToEnd && endPose.HasValue
                ? endPose.Value
                : Pose3D.FromDegrees(segment.TargetPose);

            bool isBezier = segment.Kind == JunctionCurveSegmentKind.BezierTo
                || segment.Kind == JunctionCurveSegmentKind.BezierToEnd;

            double segLen = AppendSegmentPoints(
                currentPose,
                targetPose,
                isBezier,
                (float)segment.HandleScale,
                safeSteps,
                points);

            totalLength += segLen;
            currentPose = targetPose;

            if (!string.IsNullOrEmpty(segment.OutputAnchorId))
            {
                anchors.Add(new ResolvedAnchorInfo(segment.OutputAnchorId, totalLength, "Curves"));
            }

            if (isToEnd)
            {
                hasFinalized = true;
                break;
            }
        }

        if (finalizeWithBezierToEnd && !hasFinalized && endPose.HasValue)
        {
            totalLength += AppendSegmentPoints(currentPose, endPose.Value, true, 0.5f, safeSteps, points);
        }

        return new JunctionSampledCurveResult(points, totalLength, anchors);
    }

    public static IReadOnlyList<ResolvedAnchorInfo> CollectPathAnchors(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        JunctionPathTemplateModel path)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(path);

        JunctionSampledCurveResult geometry = SamplePathGeometry(template, layouts, path);
        List<ResolvedAnchorInfo> anchors = new(geometry.Anchors);
        Dictionary<string, double> resolvedMap = new(StringComparer.Ordinal);
        foreach (ResolvedAnchorInfo info in geometry.Anchors)
        {
            resolvedMap[info.AnchorId] = info.ResolvedS;
        }

        double widthS = 0.0;
        foreach (JunctionWidthSegment widthSeg in path.WidthSegments)
        {
            if (widthSeg.Length.TryResolve(resolvedMap, out double stepLen))
            {
                widthS += stepLen;
            }
            else
            {
                widthS += widthSeg.Length.Offset;
            }

            if (!string.IsNullOrEmpty(widthSeg.OutputAnchorId))
            {
                anchors.Add(new ResolvedAnchorInfo(widthSeg.OutputAnchorId, widthS, "Widths"));
                resolvedMap[widthSeg.OutputAnchorId] = widthS;
            }
        }

        return anchors;
    }

    public static IReadOnlyList<ResolvedAnchorInfo> CollectCurveAnchors(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument> layouts,
        JunctionCurveTemplateModel curve)
    {
        JunctionSampledCurveResult geometry = SampleCurveGeometry(template, layouts, curve);
        return geometry.Anchors;
    }

    public static int ExpandPathAnchorReferencesToAbsolute(
        JunctionPathTemplateModel path,
        string anchorId,
        double resolvedS)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrEmpty(anchorId))
        {
            return 0;
        }

        int expandedCount = 0;

        for (int i = 0; i < path.WidthSegments.Count; i++)
        {
            JunctionWidthSegment widthSeg = path.WidthSegments[i];
            if (TryExpandDistance(widthSeg.Length, anchorId, resolvedS, out AnchoredDistance expandedLength))
            {
                path.WidthSegments[i] = widthSeg with { Length = expandedLength };
                expandedCount++;
            }
        }

        expandedCount += ExpandPropAndExtrusionAnchors(path.Props, path.Extrusions, anchorId, resolvedS);
        return expandedCount;
    }

    public static int ExpandCurveAnchorReferencesToAbsolute(
        JunctionCurveTemplateModel curve,
        string anchorId,
        double resolvedS)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (string.IsNullOrEmpty(anchorId))
        {
            return 0;
        }

        return ExpandPropAndExtrusionAnchors(curve.Props, curve.Extrusions, anchorId, resolvedS);
    }

    public static bool IsJunctionPortOccupied(
        WorldPlacementDocument placement,
        string junctionId,
        string portKey,
        string? excludingSplineId = null,
        string? excludingJunctionId = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrEmpty(portKey))
        {
            return true;
        }

        foreach (JunctionPlacementNode j in placement.Junctions)
        {
            if (string.Equals(j.Id, junctionId, StringComparison.Ordinal)
                && j.Origin.Kind != JunctionOriginKind.ChunkPose
                && string.Equals(j.Origin.TargetPortKey, portKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(j.Id, excludingJunctionId, StringComparison.Ordinal)
                && j.Origin.Kind == JunctionOriginKind.FromJunctionPort
                && string.Equals(j.Origin.SourceNodeId, junctionId, StringComparison.Ordinal)
                && string.Equals(j.Origin.SourcePortKey, portKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (SplinePlacementNode s in placement.Splines)
        {
            if (!string.Equals(s.Id, excludingSplineId, StringComparison.Ordinal)
                && s.Origin.Kind == SplineOriginKind.FromJunctionPort
                && string.Equals(s.Origin.SourceNodeId, junctionId, StringComparison.Ordinal)
                && string.Equals(s.Origin.SourcePortKey, portKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(s.Id, excludingSplineId, StringComparison.Ordinal)
                && s.ConnectBezier is not null
                && s.ConnectBezier.TargetPort.Kind == PortTargetKind.JunctionPort
                && string.Equals(s.ConnectBezier.TargetPort.ElementId, junctionId, StringComparison.Ordinal)
                && string.Equals(s.ConnectBezier.TargetPort.PortKey, portKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (ExplicitPortConnection conn in placement.Connections)
        {
            if ((conn.From.Kind == PortTargetKind.JunctionPort
                    && string.Equals(conn.From.ElementId, junctionId, StringComparison.Ordinal)
                    && string.Equals(conn.From.PortKey, portKey, StringComparison.Ordinal))
                || (conn.To.Kind == PortTargetKind.JunctionPort
                    && string.Equals(conn.To.ElementId, junctionId, StringComparison.Ordinal)
                    && string.Equals(conn.To.PortKey, portKey, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> FindCompatibleJunctionPortsForSplineOutlet(
        WorldProject project,
        SplinePlacementNode sourceSpline,
        string targetJunctionTemplateKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceSpline);

        if (sourceSpline.ConnectBezier is not null)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrEmpty(sourceSpline.TemplateKey)
            || !project.SplineTemplates.TryGetValue(sourceSpline.TemplateKey, out SplineTemplateDocument? sourceTemplate)
            || !project.LaneLayouts.TryGetValue(sourceTemplate.OutletLayoutKey, out LaneLayoutDocument? sourceLayout)
            || !project.JunctionTemplates.TryGetValue(targetJunctionTemplateKey, out JunctionTemplateDocument? jTpl))
        {
            return Array.Empty<string>();
        }

        List<string> compatible = new();
        foreach (JunctionPortDefinition port in jTpl.Ports)
        {
            if (project.LaneLayouts.TryGetValue(port.LayoutKey, out LaneLayoutDocument? targetLayout)
                && sourceLayout.CanConnectTo(targetLayout))
            {
                compatible.Add(port.Key);
            }
        }

        return compatible;
    }

    public static IReadOnlyList<string> FindCompatibleJunctionPortsForJunctionPort(
        WorldProject project,
        JunctionPlacementNode sourceJunction,
        string sourcePortKey,
        string targetJunctionTemplateKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceJunction);

        if (IsJunctionPortOccupied(project.Placement, sourceJunction.Id, sourcePortKey)
            || !project.JunctionTemplates.TryGetValue(sourceJunction.TemplateKey, out JunctionTemplateDocument? sourceTpl)
            || !project.JunctionTemplates.TryGetValue(targetJunctionTemplateKey, out JunctionTemplateDocument? targetTpl))
        {
            return Array.Empty<string>();
        }

        JunctionPortDefinition? sourcePort = FindTemplatePort(sourceTpl, sourcePortKey);
        if (sourcePort is null || !project.LaneLayouts.TryGetValue(sourcePort.LayoutKey, out LaneLayoutDocument? sourceLayout))
        {
            return Array.Empty<string>();
        }

        List<string> compatible = new();
        foreach (JunctionPortDefinition targetPort in targetTpl.Ports)
        {
            if (project.LaneLayouts.TryGetValue(targetPort.LayoutKey, out LaneLayoutDocument? targetLayout)
                && sourceLayout.CanConnectTo(targetLayout))
            {
                compatible.Add(targetPort.Key);
            }
        }

        return compatible;
    }

    public static IReadOnlyList<string> FindCompatibleSplineTemplatesForJunctionPort(
        WorldProject project,
        JunctionPlacementNode sourceJunction,
        string sourcePortKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceJunction);

        if (IsJunctionPortOccupied(project.Placement, sourceJunction.Id, sourcePortKey)
            || !project.JunctionTemplates.TryGetValue(sourceJunction.TemplateKey, out JunctionTemplateDocument? sourceTpl))
        {
            return Array.Empty<string>();
        }

        JunctionPortDefinition? sourcePort = FindTemplatePort(sourceTpl, sourcePortKey);
        if (sourcePort is null)
        {
            return Array.Empty<string>();
        }

        List<string> compatible = new();
        foreach (KeyValuePair<string, SplineTemplateDocument> kvp in project.SplineTemplates)
        {
            if (string.Equals(kvp.Value.OutletLayoutKey, sourcePort.LayoutKey, StringComparison.Ordinal))
            {
                compatible.Add(kvp.Key);
            }
        }

        compatible.Sort(StringComparer.OrdinalIgnoreCase);
        return compatible;
    }

    public static int FindExplicitConnectionIndexForPort(WorldPlacementDocument placement, PortReferenceModel port)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(port);

        for (int i = 0; i < placement.Connections.Count; i++)
        {
            ExplicitPortConnection conn = placement.Connections[i];
            if (conn.From == port || conn.To == port)
            {
                return i;
            }
        }

        return -1;
    }

    public static IReadOnlyList<CompatiblePortCandidate> FindCompatibleExplicitConnectTargets(
        WorldProject project,
        PortReferenceModel sourcePort)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourcePort);

        if (sourcePort.Kind == PortTargetKind.JunctionPort && !string.IsNullOrEmpty(sourcePort.PortKey))
        {
            if (IsJunctionPortOccupied(project.Placement, sourcePort.ElementId, sourcePort.PortKey))
            {
                return Array.Empty<CompatiblePortCandidate>();
            }
        }
        else if (SplineAnchorUtilities.IsPortOccupiedByOther(project.Placement, sourcePort, string.Empty)
            || IsPortInExplicitConnections(project.Placement, sourcePort))
        {
            return Array.Empty<CompatiblePortCandidate>();
        }

        if (!TryResolvePortLayout(project, sourcePort, out LaneLayoutDocument? sourceLayout) || sourceLayout is null)
        {
            return Array.Empty<CompatiblePortCandidate>();
        }

        List<CompatiblePortCandidate> candidates = new();

        foreach (SplinePlacementNode spline in project.Placement.Splines)
        {
            if (string.Equals(spline.Id, sourcePort.ElementId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrEmpty(spline.TemplateKey)
                || !project.SplineTemplates.TryGetValue(spline.TemplateKey, out SplineTemplateDocument? st)
                || !project.LaneLayouts.TryGetValue(st.OutletLayoutKey, out LaneLayoutDocument? outletLayout))
            {
                continue;
            }

            LaneLayoutDocument inletLayout = outletLayout.CreateOpposition();
            PortReferenceModel inletRef = new(spline.Id, PortTargetKind.SplineInlet);
            if (!SplineAnchorUtilities.IsPortOccupiedByOther(project.Placement, inletRef, string.Empty)
                && !IsPortInExplicitConnections(project.Placement, inletRef)
                && sourceLayout.CanConnectTo(inletLayout))
            {
                string label = string.IsNullOrEmpty(spline.Key)
                    ? $"{spline.Id}.Inlet ({st.OutletLayoutKey})"
                    : $"{spline.Key} [{spline.Id}].Inlet ({st.OutletLayoutKey})";
                candidates.Add(new CompatiblePortCandidate(inletRef, label, st.OutletLayoutKey));
            }

            PortReferenceModel outletRef = new(spline.Id, PortTargetKind.SplineOutlet);
            if (!SplineAnchorUtilities.IsPortOccupiedByOther(project.Placement, outletRef, string.Empty)
                && !IsPortInExplicitConnections(project.Placement, outletRef)
                && sourceLayout.CanConnectTo(outletLayout))
            {
                string label = string.IsNullOrEmpty(spline.Key)
                    ? $"{spline.Id}.Outlet ({st.OutletLayoutKey})"
                    : $"{spline.Key} [{spline.Id}].Outlet ({st.OutletLayoutKey})";
                candidates.Add(new CompatiblePortCandidate(outletRef, label, st.OutletLayoutKey));
            }
        }

        foreach (JunctionPlacementNode junction in project.Placement.Junctions)
        {
            if (string.Equals(junction.Id, sourcePort.ElementId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!project.JunctionTemplates.TryGetValue(junction.TemplateKey, out JunctionTemplateDocument? jt))
            {
                continue;
            }

            foreach (JunctionPortDefinition jPort in jt.Ports)
            {
                if (!project.LaneLayouts.TryGetValue(jPort.LayoutKey, out LaneLayoutDocument? portLayout))
                {
                    continue;
                }

                if (!IsJunctionPortOccupied(project.Placement, junction.Id, jPort.Key)
                    && sourceLayout.CanConnectTo(portLayout))
                {
                    PortReferenceModel portRef = new(junction.Id, PortTargetKind.JunctionPort, jPort.Key);
                    string label = string.IsNullOrEmpty(junction.Key)
                        ? $"{junction.Id}.{jPort.Key} ({jPort.LayoutKey})"
                        : $"{junction.Key} [{junction.Id}].{jPort.Key} ({jPort.LayoutKey})";
                    candidates.Add(new CompatiblePortCandidate(portRef, label, jPort.LayoutKey));
                }
            }
        }

        return candidates;
    }

    public static JunctionTemplateDocument DeepCloneJunctionTemplate(JunctionTemplateDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);

        JunctionTemplateDocument clone = new()
        {
            Key = source.Key,
            RelativePath = source.RelativePath,
            UserCode = source.UserCode,
        };

        clone.Ports.AddRange(source.Ports);
        clone.Relays.AddRange(source.Relays);
        clone.Props.AddRange(source.Props);

        foreach (JunctionPathTemplateModel path in source.Paths)
        {
            clone.Paths.Add(DeepClonePath(path));
        }

        foreach (JunctionCurveTemplateModel curve in source.Curves)
        {
            clone.Curves.Add(DeepCloneCurve(curve));
        }

        return clone;
    }

    public static void CopyJunctionTemplate(JunctionTemplateDocument source, JunctionTemplateDocument destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        JunctionTemplateDocument copy = DeepCloneJunctionTemplate(source);
        destination.Key = copy.Key;
        destination.RelativePath = copy.RelativePath;
        destination.UserCode = copy.UserCode;

        destination.Ports.Clear();
        destination.Ports.AddRange(copy.Ports);

        destination.Relays.Clear();
        destination.Relays.AddRange(copy.Relays);

        destination.Paths.Clear();
        destination.Paths.AddRange(copy.Paths);

        destination.Curves.Clear();
        destination.Curves.AddRange(copy.Curves);

        destination.Props.Clear();
        destination.Props.AddRange(copy.Props);
    }

    public static JunctionPlacementNode DeepCloneJunctionPlacement(JunctionPlacementNode source)
    {
        ArgumentNullException.ThrowIfNull(source);

        JunctionPlacementNode clone = new()
        {
            Id = source.Id,
            Key = source.Key,
            TemplateKey = source.TemplateKey,
            Origin = source.Origin,
            PreBuildUserCode = source.PreBuildUserCode,
        };

        clone.Props.AddRange(source.Props);

        foreach (JunctionPathOverrideModel pathOv in source.PathOverrides)
        {
            JunctionPathOverrideModel ovCopy = new()
            {
                PathKey = pathOv.PathKey,
                TrafficDensityFactor = pathOv.TrafficDensityFactor,
            };
            CloneRuleLists(pathOv.Props, pathOv.Extrusions, ovCopy.Props, ovCopy.Extrusions);
            clone.PathOverrides.Add(ovCopy);
        }

        foreach (JunctionCurveOverrideModel curveOv in source.CurveOverrides)
        {
            JunctionCurveOverrideModel ovCopy = new()
            {
                CurveKey = curveOv.CurveKey,
            };
            CloneRuleLists(curveOv.Props, curveOv.Extrusions, ovCopy.Props, ovCopy.Extrusions);
            clone.CurveOverrides.Add(ovCopy);
        }

        return clone;
    }

    public static void CopyJunctionPlacement(JunctionPlacementNode source, JunctionPlacementNode destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        JunctionPlacementNode copy = DeepCloneJunctionPlacement(source);
        destination.Id = copy.Id;
        destination.Key = copy.Key;
        destination.TemplateKey = copy.TemplateKey;
        destination.Origin = copy.Origin;
        destination.PreBuildUserCode = copy.PreBuildUserCode;

        destination.Props.Clear();
        destination.Props.AddRange(copy.Props);

        destination.PathOverrides.Clear();
        destination.PathOverrides.AddRange(copy.PathOverrides);

        destination.CurveOverrides.Clear();
        destination.CurveOverrides.AddRange(copy.CurveOverrides);
    }

    private static JunctionPathTemplateModel DeepClonePath(JunctionPathTemplateModel source)
    {
        JunctionPathTemplateModel copy = new()
        {
            Key = source.Key,
            FromPortKey = source.FromPortKey,
            FromPinIndex = source.FromPinIndex,
            ToPortKey = source.ToPortKey,
            ToPinIndex = source.ToPinIndex,
            TrafficDensityFactor = source.TrafficDensityFactor,
        };
        copy.Segments.AddRange(source.Segments);
        copy.WidthSegments.AddRange(source.WidthSegments);
        CloneRuleLists(source.Props, source.Extrusions, copy.Props, copy.Extrusions);
        return copy;
    }

    private static JunctionCurveTemplateModel DeepCloneCurve(JunctionCurveTemplateModel source)
    {
        JunctionCurveTemplateModel copy = new()
        {
            Key = source.Key,
            Origin = source.Origin,
        };
        copy.Segments.AddRange(source.Segments);
        CloneRuleLists(source.Props, source.Extrusions, copy.Props, copy.Extrusions);
        return copy;
    }

    private static void CloneRuleLists(
        IReadOnlyList<CurvePropPlacementRule> srcProps,
        IReadOnlyList<CurveExtrusionPlacementRule> srcExtrusions,
        IList<CurvePropPlacementRule> dstProps,
        IList<CurveExtrusionPlacementRule> dstExtrusions)
    {
        foreach (CurvePropPlacementRule prop in srcProps)
        {
            CurvePropPlacementRule pCopy = new()
            {
                Pose = prop.Pose,
                From = prop.From,
                Span = prop.Span,
                Interval = prop.Interval,
                Count = prop.Count,
            };
            pCopy.ModelKeys.AddRange(prop.ModelKeys);
            dstProps.Add(pCopy);
        }

        foreach (CurveExtrusionPlacementRule ext in srcExtrusions)
        {
            CurveExtrusionPlacementRule eCopy = new()
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
            dstExtrusions.Add(eCopy);
        }
    }

    private static JunctionPortDefinition? FindTemplatePort(JunctionTemplateDocument template, string portKey)
    {
        foreach (JunctionPortDefinition port in template.Ports)
        {
            if (string.Equals(port.Key, portKey, StringComparison.Ordinal))
            {
                return port;
            }
        }

        return null;
    }

    private static bool TryResolvePortLayout(
        WorldProject project,
        PortReferenceModel portRef,
        out LaneLayoutDocument? layout)
    {
        layout = null;
        if (portRef.Kind == PortTargetKind.JunctionPort)
        {
            foreach (JunctionPlacementNode j in project.Placement.Junctions)
            {
                if (string.Equals(j.Id, portRef.ElementId, StringComparison.Ordinal)
                    && project.JunctionTemplates.TryGetValue(j.TemplateKey, out JunctionTemplateDocument? jt))
                {
                    JunctionPortDefinition? def = FindTemplatePort(jt, portRef.PortKey ?? string.Empty);
                    if (def is not null && project.LaneLayouts.TryGetValue(def.LayoutKey, out LaneLayoutDocument? jl))
                    {
                        layout = jl;
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (SplinePlacementNode s in project.Placement.Splines)
        {
            if (string.Equals(s.Id, portRef.ElementId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(s.TemplateKey)
                && project.SplineTemplates.TryGetValue(s.TemplateKey, out SplineTemplateDocument? st)
                && project.LaneLayouts.TryGetValue(st.OutletLayoutKey, out LaneLayoutDocument? ol))
            {
                layout = portRef.Kind == PortTargetKind.SplineInlet ? ol.CreateOpposition() : ol;
                return true;
            }
        }

        return false;
    }

    private static bool IsPortInExplicitConnections(WorldPlacementDocument placement, PortReferenceModel port)
    {
        foreach (ExplicitPortConnection conn in placement.Connections)
        {
            if (conn.From == port || conn.To == port)
            {
                return true;
            }
        }

        return false;
    }

    private static double AppendSegmentPoints(
        Pose3D fromPose,
        Pose3D toPose,
        bool isBezier,
        float handleScale,
        int steps,
        List<Vector3> points)
    {
        if (!isBezier)
        {
            float dist = Vector3.Distance(fromPose.Position, toPose.Position);
            points.Add(toPose.Position);
            return dist;
        }

        Pose3D invFrom = Pose3D.Inverse(fromPose);
        Pose3D transition = Pose3D.Compose(toPose, invFrom);
        Vector3 p0 = Vector3.Zero;
        Vector3 p3 = transition.Position;
        float handleLength = p3.Length() * handleScale;
        Vector3 p1 = p0 + Vector3.UnitZ * handleLength;
        Vector3 rawToForward = Vector3.Transform(Vector3.UnitZ, transition.Orientation);
        Vector3 toForward = 1e-6f < rawToForward.LengthSquared()
            ? Vector3.Normalize(rawToForward)
            : Vector3.UnitZ;
        Vector3 p2 = p3 - toForward * handleLength;

        double segLen = 0.0;
        Vector3 prevWorld = fromPose.Position;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float u = 1f - t;
            float uu = u * u;
            float tt = t * t;
            Vector3 localPos = (uu * u) * p0
                + (3f * uu * t) * p1
                + (3f * u * tt) * p2
                + (tt * t) * p3;
            Vector3 worldPos = fromPose.Position + Vector3.Transform(localPos, fromPose.Orientation);
            segLen += Vector3.Distance(prevWorld, worldPos);
            points.Add(worldPos);
            prevWorld = worldPos;
        }

        return segLen;
    }

    private static int ExpandPropAndExtrusionAnchors(
        IList<CurvePropPlacementRule> props,
        IList<CurveExtrusionPlacementRule> extrusions,
        string anchorId,
        double resolvedS)
    {
        int expandedCount = 0;

        foreach (CurvePropPlacementRule prop in props)
        {
            if (TryExpandDistance(prop.From, anchorId, resolvedS, out AnchoredDistance expandedFrom))
            {
                prop.From = expandedFrom;
                expandedCount++;
            }
        }

        foreach (CurveExtrusionPlacementRule ext in extrusions)
        {
            if (TryExpandDistance(ext.From, anchorId, resolvedS, out AnchoredDistance expandedFrom))
            {
                ext.From = expandedFrom;
                expandedCount++;
            }

            if (ext.To.HasValue && TryExpandDistance(ext.To.Value, anchorId, resolvedS, out AnchoredDistance expandedTo))
            {
                ext.To = expandedTo;
                expandedCount++;
            }
        }

        return expandedCount;
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
}
