using System;
using System.Collections.Generic;
using System.Numerics;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Rendering.Worlds;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// On-demand offscreen Direct3D 11 renderer for a single <see cref="SplineTemplateDocument"/>
/// evaluated along a sample straight or curved spline.
/// </summary>
public sealed class SplineTemplatePreviewRenderer : TemplatePreviewRendererBase
{
    private const string PreviewSplineId = "__tpl_preview_spline";

    public SplineTemplatePreviewRenderer(int initialWidth = 640, int initialHeight = 480)
        : base("SplineTemplatePreview.xml", initialWidth, initialHeight)
    {
    }

    public void SyncTemplate(WorldProject sourceProject, string templateKey, bool useCurvedSample = false)
    {
        ArgumentNullException.ThrowIfNull(sourceProject);
        ArgumentNullException.ThrowIfNull(templateKey);

        if (!sourceProject.SplineTemplates.TryGetValue(templateKey, out SplineTemplateDocument? templateDoc))
        {
            ClearPreviewWorld();
            return;
        }

        WorldProject preview = CreateBasePreviewProject(sourceProject);
        preview.SplineTemplates[templateDoc.Key] = templateDoc;

        SplinePlacementNode sampleSpline = new()
        {
            Id = PreviewSplineId,
            Key = "PreviewSpline",
            TemplateKey = templateDoc.Key,
            Origin = useCurvedSample
                ? SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(117.0, 0.0, 100.0))
                : SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(125.0, 0.0, 100.0)),
        };

        if (useCurvedSample)
        {
            sampleSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 12.0));
            sampleSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.ByRadius, 40.0, 28.0));
            sampleSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.ByRadius, -40.0, 22.0));
        }
        else
        {
            sampleSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 50.0));
        }

        preview.Placement.Splines.Add(sampleSpline);
        ApplyPreviewProject(preview);
    }

    protected override bool TryGetPreferredOrbitBounds(out BoundingBox bounds)
    {
        foreach (SplineRenderEntry entry in World.Splines)
        {
            IReadOnlyList<Vector3> pts = entry.PrimaryChunkCenterlinePoints;
            if (0 < pts.Count)
            {
                Vector3 chunkOffset = new(
                    entry.PrimaryChunk.X * 250.0f,
                    0.0f,
                    entry.PrimaryChunk.Z * 250.0f);
                Vector3 min = pts[0] + chunkOffset;
                Vector3 max = pts[0] + chunkOffset;
                for (int i = 1; i < pts.Count; i++)
                {
                    Vector3 p = pts[i] + chunkOffset;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                // Pad cross-section width/height so short/straight splines frame comfortably.
                Vector3 padding = new(6.0f, 3.0f, 6.0f);
                bounds = new BoundingBox(min - padding, max + padding);
                return true;
            }
        }

        bounds = default;
        return false;
    }
}
