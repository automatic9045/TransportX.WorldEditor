using System;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Worlds;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// On-demand offscreen Direct3D 11 renderer for a single <see cref="JunctionTemplateDocument"/>.
/// </summary>
public sealed class JunctionTemplatePreviewRenderer : TemplatePreviewRendererBase
{
    private const string PreviewJunctionId = "__tpl_preview_junction";

    public JunctionTemplatePreviewRenderer(int initialWidth = 640, int initialHeight = 480)
        : base("JunctionTemplatePreview.xml", initialWidth, initialHeight)
    {
    }

    public void SyncTemplate(WorldProject sourceProject, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(sourceProject);
        ArgumentNullException.ThrowIfNull(templateKey);

        if (!sourceProject.JunctionTemplates.TryGetValue(templateKey, out JunctionTemplateDocument? templateDoc))
        {
            ClearPreviewWorld();
            return;
        }

        WorldProject preview = CreateBasePreviewProject(sourceProject);
        JunctionTemplateDocument clonedTemplate = JunctionAnchorUtilities.DeepCloneJunctionTemplate(templateDoc);
        preview.JunctionTemplates[clonedTemplate.Key] = clonedTemplate;

        preview.Placement.Junctions.Add(new JunctionPlacementNode
        {
            Id = PreviewJunctionId,
            Key = "PreviewJunction",
            TemplateKey = clonedTemplate.Key,
            Origin = JunctionOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity),
        });

        ApplyPreviewProject(preview);
    }

    protected override bool TryGetPreferredOrbitBounds(out BoundingBox bounds)
    {
        foreach (JunctionRenderEntry entry in World.Junctions)
        {
            bounds = entry.LocalBounds;
            return true;
        }

        bounds = default;
        return false;
    }
}
