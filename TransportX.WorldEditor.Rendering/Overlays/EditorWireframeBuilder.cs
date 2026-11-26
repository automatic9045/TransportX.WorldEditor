using System;
using System.Collections.Generic;
using System.Numerics;
using TransportX.Rendering;
using TransportX.Spatial;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace TransportX.WorldEditor.Rendering.Overlays;

/// <summary>
/// <see cref="WireframeDebugModel"/> subclass that restores <see cref="PrimitiveTopology.TriangleList"/>
/// after drawing line lists so TransportX's subsequent fullscreen passes never remain stuck in
/// <see cref="PrimitiveTopology.LineList"/> mode when <c>RenderContext</c> elides redundant binds.
/// </summary>
public sealed class EditorWireframeDebugModel : WireframeDebugModel
{
    public EditorWireframeDebugModel(IReadOnlyList<IMesh> meshes)
        : base(meshes)
    {
    }

    public override void Draw(in DrawContext context)
    {
        base.Draw(in context);
        context.DeviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }
}

/// <summary>
/// <see cref="TransformedModel"/> subclass drawn on a debug <see cref="RenderLayer"/> (defaulting to <see cref="RenderLayer.Traffic"/>)
/// so editor overlays execute inside TransportX's <c>DebugPass</c> independently of traffic network debug lines.
/// </summary>
public sealed class OverlayTransformedModel : TransformedModel
{
    public RenderLayer TargetLayer { get; }

    public OverlayTransformedModel(WireframeDebugModel debugModel, Pose pose, RenderLayer targetLayer = RenderLayer.Traffic)
        : base(new ModelResourceSet(debugModel), pose)
    {
        TargetLayer = targetLayer;
    }

    public override void Draw(in TransformedDrawContext context)
    {
        if (!IsVisible || context.Layer != TargetLayer)
        {
            return;
        }

        Matrix4x4 world = (Pose * context.ChunkOffset.Pose).ToMatrix4x4();
        context.DrawModel(Resource.Model, world);
    }
}

/// <summary>
/// Factory building GPU wireframe line meshes for the 250m chunk grid, selection OBBs,
/// 3D translation arrows, and yaw rotation rings.
/// </summary>
public static class EditorWireframeBuilder
{
    public static WireframeDebugModel CreateChunkGrid(ID3D11Device device, float gridExtentMeters = 250f, float subStepMeters = 25f)
    {
        List<Vertex> vertices = new List<Vertex>();
        List<int> indices = new List<int>();
        Vector4 subColor = new Vector4(0.32f, 0.36f, 0.42f, 0.45f);
        Vector4 borderColor = new Vector4(0.25f, 0.72f, 1.0f, 0.85f);

        int steps = (int)MathF.Round(gridExtentMeters / subStepMeters);
        for (int i = 0; i <= steps; i++)
        {
            float coord = i * subStepMeters;
            bool isBorder = i == 0 || i == steps;
            Vector4 color = isBorder ? borderColor : subColor;
            float y = isBorder ? 0.03f : 0.015f;

            // X-parallel line at z = coord
            AddLine(vertices, indices, new Vector3(0f, y, coord), new Vector3(gridExtentMeters, y, coord), color);
            // Z-parallel line at x = coord
            AddLine(vertices, indices, new Vector3(coord, y, 0f), new Vector3(coord, y, gridExtentMeters), color);
        }

        return BuildModel(device, "ChunkGrid", vertices, indices, Vector4.One);
    }

    public static WireframeDebugModel CreateAxisArrow(
        ID3D11Device device,
        Vector3 direction,
        Vector4 color,
        float length = 2.5f)
    {
        Vector3 dir = Vector3.Normalize(direction);
        Vector3 upHint = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 side1 = Vector3.Normalize(Vector3.Cross(dir, upHint));
        Vector3 side2 = Vector3.Normalize(Vector3.Cross(dir, side1));

        List<Vertex> vertices = new List<Vertex>();
        List<int> indices = new List<int>();

        Vector3 tip = dir * length;
        Vector3 baseCone = dir * (length * 0.82f);
        float headRadius = length * 0.07f;

        AddLine(vertices, indices, Vector3.Zero, tip, Vector4.One);
        AddLine(vertices, indices, tip, baseCone + side1 * headRadius, Vector4.One);
        AddLine(vertices, indices, tip, baseCone - side1 * headRadius, Vector4.One);
        AddLine(vertices, indices, tip, baseCone + side2 * headRadius, Vector4.One);
        AddLine(vertices, indices, tip, baseCone - side2 * headRadius, Vector4.One);

        return BuildModel(device, "GizmoArrow", vertices, indices, color);
    }

    public static WireframeDebugModel CreateRotationRing(
        ID3D11Device device,
        Vector3 axis,
        Vector4 color,
        float radius = 2.2f,
        int segments = 48)
    {
        Vector3 n = Vector3.Normalize(axis);
        Vector3 refVec = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(n, refVec));
        Vector3 v = Vector3.Normalize(Vector3.Cross(n, u));

        List<Vertex> vertices = new List<Vertex>();
        List<int> indices = new List<int>();

        for (int i = 0; i < segments; i++)
        {
            float a0 = (i / (float)segments) * MathF.Tau;
            float a1 = ((i + 1) / (float)segments) * MathF.Tau;
            Vector3 p0 = (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * radius;
            Vector3 p1 = (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * radius;
            AddLine(vertices, indices, p0, p1, Vector4.One);
        }

        return BuildModel(device, "GizmoRing", vertices, indices, color);
    }

    public static WireframeDebugModel CreatePlacementCursor(ID3D11Device device, Vector4 color, float radius = 1.0f)
    {
        List<Vertex> vertices = new List<Vertex>();
        List<int> indices = new List<int>();
        AddLine(vertices, indices, new Vector3(-radius, 0.05f, 0f), new Vector3(radius, 0.05f, 0f), Vector4.One);
        AddLine(vertices, indices, new Vector3(0f, 0.05f, -radius), new Vector3(0f, 0.05f, radius), Vector4.One);
        AddLine(vertices, indices, Vector3.Zero, new Vector3(0f, 1.5f, 0f), Vector4.One);
        return BuildModel(device, "PlacementCursor", vertices, indices, color);
    }

    public static WireframeDebugModel CreatePortMarker(ID3D11Device device, Vector4 color, float radius = 0.85f)
    {
        List<Vertex> vertices = new();
        List<int> indices = new();
        float cy = 0.65f;
        Vector3 top = new(0f, cy + radius, 0f);
        Vector3 bottom = new(0f, cy - radius * 0.55f, 0f);
        Vector3 px = new(radius, cy, 0f);
        Vector3 nx = new(-radius, cy, 0f);
        Vector3 pz = new(0f, cy, radius);
        Vector3 nz = new(0f, cy, -radius);

        AddLine(vertices, indices, top, px, Vector4.One);
        AddLine(vertices, indices, top, nx, Vector4.One);
        AddLine(vertices, indices, top, pz, Vector4.One);
        AddLine(vertices, indices, top, nz, Vector4.One);
        AddLine(vertices, indices, bottom, px, Vector4.One);
        AddLine(vertices, indices, bottom, nx, Vector4.One);
        AddLine(vertices, indices, bottom, pz, Vector4.One);
        AddLine(vertices, indices, bottom, nz, Vector4.One);
        AddLine(vertices, indices, px, pz, Vector4.One);
        AddLine(vertices, indices, pz, nx, Vector4.One);
        AddLine(vertices, indices, nx, nz, Vector4.One);
        AddLine(vertices, indices, nz, px, Vector4.One);

        return BuildModel(device, "PortMarker", vertices, indices, color);
    }

    public static WireframeDebugModel CreateAnchorMarker(ID3D11Device device, Vector4 color, float height = 3.5f)
    {
        List<Vertex> vertices = new();
        List<int> indices = new();
        AddLine(vertices, indices, new Vector3(0f, 0.1f, 0f), new Vector3(0f, height, 0f), Vector4.One);
        AddLine(vertices, indices, new Vector3(-0.9f, height * 0.85f, 0f), new Vector3(0.9f, height * 0.85f, 0f), Vector4.One);
        AddLine(vertices, indices, new Vector3(0f, height * 0.85f, -0.9f), new Vector3(0f, height * 0.85f, 0.9f), Vector4.One);
        return BuildModel(device, "AnchorMarker", vertices, indices, color);
    }

    public static WireframeDebugModel CreatePolyline(
        ID3D11Device device,
        IReadOnlyList<Vector3> points,
        Vector4 color,
        float yLift = 0.22f)
    {
        List<Vertex> vertices = new();
        List<int> indices = new();
        Vector3 lift = new(0f, yLift, 0f);
        for (int i = 0; i < points.Count - 1; i++)
        {
            AddLine(vertices, indices, points[i] + lift, points[i + 1] + lift, Vector4.One);
        }

        if (vertices.Count == 0)
        {
            AddLine(vertices, indices, lift, lift + Vector3.UnitY * 0.1f, Vector4.One);
        }

        return BuildModel(device, "SplineHighlight", vertices, indices, color);
    }

    public static WireframeDebugModel CreateBoundingBox(
        ID3D11Device device,
        Material material,
        Vector3 min,
        Vector3 max)
    {
        Vector3[] points = new Vector3[]
        {
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
        };

        Vertex[] vertices = new Vertex[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            vertices[i] = new Vertex(points[i], Vector4.One);
        }

        int[] indices = new int[]
        {
            0, 1, 1, 3, 3, 2, 2, 0,
            4, 5, 5, 7, 7, 6, 6, 4,
            0, 4, 1, 5, 2, 6, 3, 7,
        };

        Mesh mesh = Mesh.Create(device, string.Empty, vertices, indices, material, PrimitiveTopology.LineList);
        return new EditorWireframeDebugModel(new IMesh[] { mesh });
    }

    private static void AddLine(
        List<Vertex> vertices,
        List<int> indices,
        Vector3 a,
        Vector3 b,
        Vector4 color)
    {
        int start = vertices.Count;
        vertices.Add(new Vertex(a, color));
        vertices.Add(new Vertex(b, color));
        indices.Add(start);
        indices.Add(start + 1);
    }

    private static WireframeDebugModel BuildModel(
        ID3D11Device device,
        string name,
        List<Vertex> vertices,
        List<int> indices,
        Vector4 color)
    {
        Material material = Material.Default();
        material.Name = name;
        Mesh mesh = Mesh.Create(device, name, vertices.ToArray(), indices.ToArray(), material, PrimitiveTopology.LineList);
        return new EditorWireframeDebugModel(new IMesh[] { mesh })
        {
            Color = color,
            DebugName = name,
        };
    }
}
