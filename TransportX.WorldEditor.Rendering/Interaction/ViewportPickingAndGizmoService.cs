using System;
using System.Collections.Generic;
using System.Numerics;
using TransportX.Cameras;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Rendering.Worlds;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Rendering.Interaction;

/// <summary>
/// Active viewport manipulation mode.
/// </summary>
public enum EditorToolMode
{
    Select,
    Translate,
    Rotate,
    PlaceProp,
    PlaceSpline,
    PlaceJunction,
    ConnectPort,
}

/// <summary>
/// Active or hovered 3D gizmo handle axis.
/// </summary>
public enum GizmoAxis
{
    None,
    X,
    Y,
    Z,
    Yaw,
}

/// <summary>
/// World ray expressed relative to a reference 250m <see cref="OriginChunk"/>.
/// </summary>
public readonly record struct ViewportRay(
    ChunkIndex OriginChunk,
    Vector3 LocalOrigin,
    Vector3 Direction);

/// <summary>
/// Candidate prop bounding volume tested during 3D raycast picking.
/// </summary>
public readonly record struct PropPickCandidate(
    string Id,
    WorldPoseDegreesModel Pose,
    BoundingBox LocalBounds);

/// <summary>
/// Result of picking a placed static prop in the 3D scene.
/// </summary>
public readonly record struct PropPickHit(
    string PropId,
    float Distance);

/// <summary>
/// Candidate spline centerline polyline tested during 3D viewport raycast picking.
/// </summary>
public readonly record struct SplinePickCandidate(
    string SplineId,
    ChunkIndex Chunk,
    IReadOnlyList<Vector3> LocalCenterlinePoints);

/// <summary>
/// Result of picking a placed spline in the 3D viewport.
/// </summary>
public readonly record struct SplinePickHit(
    string SplineId,
    float Distance);

/// <summary>
/// Candidate placed junction bounding volume and path polylines tested during 3D viewport raycast picking.
/// </summary>
public readonly record struct JunctionPickCandidate(
    string JunctionId,
    ChunkIndex Chunk,
    WorldPoseDegreesModel Pose,
    BoundingBox LocalBounds,
    IReadOnlyList<IReadOnlyList<Vector3>> LocalPolylines);

/// <summary>
/// Result of picking a placed junction in the 3D viewport.
/// </summary>
public readonly record struct JunctionPickHit(
    string JunctionId,
    float Distance);

/// <summary>
/// Candidate network port marker tested during 3D ConnectPort picking.
/// </summary>
public readonly record struct PortPickCandidate(
    PortReferenceModel PortRef,
    ChunkIndex Chunk,
    Vector3 LocalPosition,
    bool IsConnected);

/// <summary>
/// Result of picking a <see cref="PortReferenceModel"/> marker in the 3D viewport.
/// </summary>
public readonly record struct PortPickHit(
    PortReferenceModel PortRef,
    float Distance);

/// <summary>
/// Pure-math 3D viewport raycasting, prop OBB picking, and translation/rotation gizmo
/// projection helpers with 250m chunk normalization and snap support.
/// </summary>
public static class ViewportPickingAndGizmoService
{
    public static ViewportRay CreateRay(
        float screenX,
        float screenY,
        int viewportWidth,
        int viewportHeight,
        in ViewContext viewContext)
    {
        int w = Math.Max(1, viewportWidth);
        int h = Math.Max(1, viewportHeight);

        Matrix4x4 viewProj = viewContext.View * viewContext.Projection;
        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invViewProj))
        {
            return new ViewportRay(viewContext.WorldPose.Chunk, viewContext.WorldPose.Pose.Position, Vector3.UnitZ);
        }

        float ndcX = (2f * screenX / w) - 1f;
        float ndcY = 1f - (2f * screenY / h);

        Vector4 nearH = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), invViewProj);
        Vector4 farH = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), invViewProj);

        Vector3 nearPos = MathF.Abs(nearH.W) < 1e-6f ? viewContext.WorldPose.Pose.Position : new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W;
        Vector3 farPos = MathF.Abs(farH.W) < 1e-6f ? nearPos + Vector3.UnitZ : new Vector3(farH.X, farH.Y, farH.Z) / farH.W;

        Vector3 dir = farPos - nearPos;
        if (dir.LengthSquared() < 1e-8f)
        {
            dir = Vector3.UnitZ;
        }
        else
        {
            dir = Vector3.Normalize(dir);
        }

        return new ViewportRay(viewContext.WorldPose.Chunk, nearPos, dir);
    }

    public static PropPickHit? PickNearestProp(
        ViewportRay ray,
        IEnumerable<PropPickCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        PropPickHit? best = null;
        foreach (PropPickCandidate candidate in candidates)
        {
            if (TryIntersectPropOBB(ray, candidate.Pose, candidate.LocalBounds, out float distance))
            {
                if (best is null || distance < best.Value.Distance)
                {
                    best = new PropPickHit(candidate.Id, distance);
                }
            }
        }

        return best;
    }

    public static SplinePickHit? PickNearestSpline(
        ViewportRay ray,
        IEnumerable<SplinePickCandidate> candidates,
        float maxRayToLineDistance = 2.5f)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        SplinePickHit? best = null;
        float bestRayDist = maxRayToLineDistance;
        float bestForward = float.MaxValue;

        foreach (SplinePickCandidate candidate in candidates)
        {
            if (candidate.LocalCenterlinePoints.Count < 2)
            {
                continue;
            }

            ChunkIndex delta = candidate.Chunk - ray.OriginChunk;
            Vector3 chunkOffset = delta.Position;

            for (int i = 0; i < candidate.LocalCenterlinePoints.Count - 1; i++)
            {
                Vector3 a = candidate.LocalCenterlinePoints[i] + chunkOffset;
                Vector3 b = candidate.LocalCenterlinePoints[i + 1] + chunkOffset;
                float dist = DistanceRayToSegment(ray.LocalOrigin, ray.Direction, a, b);
                if (dist <= maxRayToLineDistance)
                {
                    Vector3 mid = (a + b) * 0.5f;
                    float forwardT = Vector3.Dot(mid - ray.LocalOrigin, ray.Direction);
                    if (0f <= forwardT && (dist < bestRayDist - 0.35f || (float.Abs(dist - bestRayDist) <= 0.35f && forwardT < bestForward)))
                    {
                        bestRayDist = dist;
                        bestForward = forwardT;
                        best = new SplinePickHit(candidate.SplineId, forwardT);
                    }
                }
            }
        }

        return best;
    }

    public static JunctionPickHit? PickNearestJunction(
        ViewportRay ray,
        IEnumerable<JunctionPickCandidate> candidates,
        float maxRayToLineDistance = 2.5f)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        JunctionPickHit? best = null;
        float bestScore = float.MaxValue;

        foreach (JunctionPickCandidate candidate in candidates)
        {
            if (TryIntersectPropOBB(ray, candidate.Pose, candidate.LocalBounds, out float obbDist))
            {
                if (best is null || obbDist < bestScore)
                {
                    bestScore = obbDist;
                    best = new JunctionPickHit(candidate.JunctionId, obbDist);
                }

                continue;
            }

            ChunkIndex delta = candidate.Chunk - ray.OriginChunk;
            Vector3 chunkOffset = delta.Position;

            foreach (IReadOnlyList<Vector3> polyline in candidate.LocalPolylines)
            {
                if (polyline.Count < 2)
                {
                    continue;
                }

                for (int i = 0; i < polyline.Count - 1; i++)
                {
                    Vector3 a = polyline[i] + chunkOffset;
                    Vector3 b = polyline[i + 1] + chunkOffset;
                    float dist = DistanceRayToSegment(ray.LocalOrigin, ray.Direction, a, b);
                    if (dist <= maxRayToLineDistance)
                    {
                        Vector3 mid = (a + b) * 0.5f;
                        float forwardT = Vector3.Dot(mid - ray.LocalOrigin, ray.Direction);
                        if (0f <= forwardT && (best is null || forwardT < bestScore))
                        {
                            bestScore = forwardT;
                            best = new JunctionPickHit(candidate.JunctionId, forwardT);
                        }
                    }
                }
            }
        }

        return best;
    }

    public static PortPickHit? PickNearestPort(
        ViewportRay ray,
        IEnumerable<PortPickCandidate> candidates,
        float maxHitRadius = 1.9f)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        PortPickHit? best = null;
        float bestForward = float.MaxValue;

        foreach (PortPickCandidate candidate in candidates)
        {
            ChunkIndex delta = candidate.Chunk - ray.OriginChunk;
            Vector3 worldPos = candidate.LocalPosition + delta.Position + new Vector3(0f, 0.65f, 0f);
            Vector3 toPoint = worldPos - ray.LocalOrigin;
            float forwardT = Vector3.Dot(toPoint, ray.Direction);
            if (forwardT < 0f)
            {
                continue;
            }

            Vector3 closest = ray.LocalOrigin + ray.Direction * forwardT;
            float perpDist = Vector3.Distance(closest, worldPos);
            if (perpDist <= maxHitRadius && forwardT < bestForward)
            {
                bestForward = forwardT;
                best = new PortPickHit(candidate.PortRef, forwardT);
            }
        }

        return best;
    }

    public static bool TryIntersectPropOBB(
        ViewportRay ray,
        WorldPoseDegreesModel propPose,
        BoundingBox localBounds,
        out float distance)
    {
        Vector3 relPos = WorldCoordinateHelper.ToChunkRelativePosition(
            ray.OriginChunk,
            propPose.Chunk,
            new Vector3((float)propPose.LocalPose.X, (float)propPose.LocalPose.Y, (float)propPose.LocalPose.Z));

        Pose localTransportPose = WorldCoordinateHelper.ToLocalPose(propPose);
        Quaternion invRot = Quaternion.Conjugate(localTransportPose.Orientation);

        Vector3 localRayOrigin = Vector3.Transform(ray.LocalOrigin - relPos, invRot);
        Vector3 localRayDir = Vector3.Transform(ray.Direction, invRot);

        Vector3 min = localBounds.Min;
        Vector3 max = localBounds.Max;
        if ((max - min).LengthSquared() < 1e-4f)
        {
            min = new Vector3(-0.75f, 0f, -0.75f);
            max = new Vector3(0.75f, 1.5f, 0.75f);
        }

        return TryIntersectAabb(localRayOrigin, localRayDir, min, max, out distance);
    }

    public static GizmoAxis HitTestGizmo(
        ViewportRay ray,
        Vector3 gizmoCenterRelCameraChunk,
        EditorToolMode toolMode)
    {
        if (toolMode == EditorToolMode.Translate)
        {
            const float axisLength = 2.5f;
            const float threshold = 0.42f;

            GizmoAxis bestAxis = GizmoAxis.None;
            float bestDist = threshold;

            CheckAxis(GizmoAxis.X, Vector3.UnitX);
            CheckAxis(GizmoAxis.Y, Vector3.UnitY);
            CheckAxis(GizmoAxis.Z, Vector3.UnitZ);

            return bestAxis;

            void CheckAxis(GizmoAxis axis, Vector3 dir)
            {
                float d = DistanceRayToSegment(ray.LocalOrigin, ray.Direction, gizmoCenterRelCameraChunk, gizmoCenterRelCameraChunk + dir * axisLength);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestAxis = axis;
                }
            }
        }

        if (toolMode == EditorToolMode.Rotate)
        {
            if (TryIntersectHorizontalPlane(ray, gizmoCenterRelCameraChunk.Y, out Vector3 hitPos))
            {
                float radialDist = new Vector2(hitPos.X - gizmoCenterRelCameraChunk.X, hitPos.Z - gizmoCenterRelCameraChunk.Z).Length();
                const float ringRadius = 2.2f;
                if (MathF.Abs(radialDist - ringRadius) <= 0.48f)
                {
                    return GizmoAxis.Yaw;
                }
            }
        }

        return GizmoAxis.None;
    }

    public static bool TryComputeAxisLineParameter(
        ViewportRay ray,
        Vector3 gizmoOrigin,
        Vector3 axisDirection,
        out float axisParameter)
    {
        Vector3 u = ray.Direction;
        Vector3 v = Vector3.Normalize(axisDirection);
        Vector3 w0 = ray.LocalOrigin - gizmoOrigin;

        float a = Vector3.Dot(u, u);
        float b = Vector3.Dot(u, v);
        float c = Vector3.Dot(v, v);
        float d = Vector3.Dot(u, w0);
        float e = Vector3.Dot(v, w0);

        float denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-5f)
        {
            axisParameter = 0f;
            return false;
        }

        axisParameter = (a * e - b * d) / denom;
        return float.IsFinite(axisParameter);
    }

    public static bool TryComputeYawAngleDegrees(
        ViewportRay ray,
        Vector3 gizmoCenter,
        out float angleDeg)
    {
        if (!TryIntersectHorizontalPlane(ray, gizmoCenter.Y, out Vector3 hitPos))
        {
            angleDeg = 0f;
            return false;
        }

        Vector2 offset = new Vector2(hitPos.X - gizmoCenter.X, hitPos.Z - gizmoCenter.Z);
        if (offset.LengthSquared() < 1e-6f)
        {
            angleDeg = 0f;
            return false;
        }

        angleDeg = MathF.Atan2(offset.X, offset.Y) * (180f / MathF.PI);
        return true;
    }

    public static WorldPoseDegreesModel ApplyAxisTranslation(
        WorldPoseDegreesModel startPose,
        GizmoAxis axis,
        float rawDeltaMeters,
        double gridSnapMeters)
    {
        double delta = 0.0 < gridSnapMeters
            ? Math.Round(rawDeltaMeters / gridSnapMeters) * gridSnapMeters
            : rawDeltaMeters;

        PoseDegreesModel lp = startPose.LocalPose;
        PoseDegreesModel moved = axis switch
        {
            GizmoAxis.X => lp with { X = lp.X + delta },
            GizmoAxis.Y => lp with { Y = lp.Y + delta },
            GizmoAxis.Z => lp with { Z = lp.Z + delta },
            _ => lp,
        };

        return new WorldPoseDegreesModel(startPose.Chunk, moved).Normalized();
    }

    public static WorldPoseDegreesModel ApplyYawRotation(
        WorldPoseDegreesModel startPose,
        float rawDeltaDegrees,
        double angleSnapDegrees)
    {
        double targetYaw = startPose.LocalPose.RotationY + rawDeltaDegrees;
        if (0.0 < angleSnapDegrees)
        {
            targetYaw = Math.Round(targetYaw / angleSnapDegrees) * angleSnapDegrees;
        }

        targetYaw = NormalizeAngleDegrees(targetYaw);
        return startPose with
        {
            LocalPose = startPose.LocalPose with { RotationY = targetYaw },
        };
    }

    public static bool TryIntersectHorizontalPlane(ViewportRay ray, float planeY, out Vector3 hitPoint)
    {
        if (MathF.Abs(ray.Direction.Y) < 1e-6f)
        {
            hitPoint = Vector3.Zero;
            return false;
        }

        float t = (planeY - ray.LocalOrigin.Y) / ray.Direction.Y;
        if (t < 0f || 2000f < t)
        {
            hitPoint = Vector3.Zero;
            return false;
        }

        hitPoint = ray.LocalOrigin + ray.Direction * t;
        return true;
    }

    public static WorldPoseDegreesModel SnapGroundPlacement(
        ViewportRay ray,
        double gridSnapMeters,
        float groundY = 0f)
    {
        Vector3 hit;
        if (!TryIntersectHorizontalPlane(ray, groundY, out hit))
        {
            hit = ray.LocalOrigin + ray.Direction * 15f;
            hit.Y = groundY;
        }

        double x = 0.0 < gridSnapMeters ? Math.Round(hit.X / gridSnapMeters) * gridSnapMeters : Math.Round(hit.X, 3);
        double z = 0.0 < gridSnapMeters ? Math.Round(hit.Z / gridSnapMeters) * gridSnapMeters : Math.Round(hit.Z, 3);

        return new WorldPoseDegreesModel(
            new ChunkIndexModel(ray.OriginChunk.X, ray.OriginChunk.Z),
            PoseDegreesModel.FromTranslation(x, groundY, z)).Normalized();
    }

    private static bool TryIntersectAabb(
        Vector3 origin,
        Vector3 dir,
        Vector3 min,
        Vector3 max,
        out float tHit)
    {
        float tMin = 0f;
        float tMax = float.MaxValue;

        if (!ClipAxis(origin.X, dir.X, min.X, max.X, ref tMin, ref tMax)
            || !ClipAxis(origin.Y, dir.Y, min.Y, max.Y, ref tMin, ref tMax)
            || !ClipAxis(origin.Z, dir.Z, min.Z, max.Z, ref tMin, ref tMax))
        {
            tHit = 0f;
            return false;
        }

        tHit = tMin;
        return 0f <= tHit;
    }

    private static bool ClipAxis(float o, float d, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(d) < 1e-7f)
        {
            return min <= o && o <= max;
        }

        float invD = 1f / d;
        float t1 = (min - o) * invD;
        float t2 = (max - o) * invD;
        if (t2 < t1)
        {
            float tmp = t1;
            t1 = t2;
            t2 = tmp;
        }

        if (tMin < t1)
        {
            tMin = t1;
        }

        if (t2 < tMax)
        {
            tMax = t2;
        }

        return tMin <= tMax;
    }

    private static float DistanceRayToSegment(Vector3 rayOrigin, Vector3 rayDir, Vector3 segA, Vector3 segB)
    {
        Vector3 u = rayDir;
        Vector3 v = segB - segA;
        Vector3 w = rayOrigin - segA;

        float a = Vector3.Dot(u, u);
        float b = Vector3.Dot(u, v);
        float c = Vector3.Dot(v, v);
        float d = Vector3.Dot(u, w);
        float e = Vector3.Dot(v, w);
        float denom = a * c - b * b;

        float sc;
        float sN;
        float sD = denom;
        float tc;
        float tN;
        float tD = denom;

        if (denom < 1e-6f)
        {
            sN = 0f;
            sD = 1f;
            tN = e;
            tD = c;
        }
        else
        {
            sN = b * e - c * d;
            tN = a * e - b * d;
            if (sN < 0f)
            {
                sN = 0f;
                tN = e;
                tD = c;
            }
        }

        if (tN < 0f)
        {
            tN = 0f;
            if (-d < 0f)
            {
                sN = 0f;
            }
            else
            {
                sN = -d;
                sD = a;
            }
        }
        else if (tD < tN)
        {
            tN = tD;
            if ((-d + b) < 0f)
            {
                sN = 0f;
            }
            else
            {
                sN = -d + b;
                sD = a;
            }
        }

        sc = MathF.Abs(sN) < 1e-6f ? 0f : sN / sD;
        tc = MathF.Abs(tN) < 1e-6f ? 0f : tN / tD;

        Vector3 dP = w + (sc * u) - (tc * v);
        return dP.Length();
    }

    private static double NormalizeAngleDegrees(double deg)
    {
        double wrapped = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return Math.Round(wrapped, 3);
    }
}
