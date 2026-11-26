using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Numerics;
using System.Text;
using TransportX.Cameras;
using TransportX.Collections;
using TransportX.Dependency;
using TransportX.Environment;
using TransportX.Extensions.Network.Elements;
using TransportX.Network;
using TransportX.Rendering;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Rendering.Assets;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Overlays;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.Worlds;
using Vortice.Mathematics;

using TxDiagnostics = TransportX.Diagnostics;
using TxDirectionalLight = TransportX.Worlds.DirectionalLight;
using TxExtensionsColor = TransportX.Extensions.Utilities.ColorExtensions;

namespace TransportX.WorldEditor.Rendering.Worlds;

/// <summary>
/// Runtime instance metadata pairing a placed editor <see cref="ChunkPropPlacement"/>
/// with its live <see cref="TransformedModel"/> and local model bounding box.
/// </summary>
public sealed class PropRenderEntry
{
    public ChunkPropPlacement Placement { get; set; }
    public ChunkIndex ChunkIndex { get; set; }
    public TransformedModel Model { get; set; }
    public BoundingBox LocalBounds { get; }

    public PropRenderEntry(
        ChunkPropPlacement placement,
        ChunkIndex chunkIndex,
        TransformedModel model,
        BoundingBox localBounds)
    {
        Placement = placement;
        ChunkIndex = chunkIndex;
        Model = model;
        LocalBounds = localBounds;
    }
}

/// <summary>
/// Incremental live-preview <see cref="WorldBase"/> executed inside the GUI editor viewport.
/// Reuses TransportX's 7-pass PBR <c>Renderer</c>, chunks, physics static handles,
/// and <c>DebugPass</c> wireframe overlays without full script compilation on every edit.
/// </summary>
public sealed class EditorWorld : WorldBase
{
    private readonly ModelCollection ModelsStore = new ModelCollection();
    private readonly Dictionary<string, PropRenderEntry> PropEntries = new(StringComparer.Ordinal);
    private readonly List<OverlayTransformedModel> PersistentOverlayHandles = new();
    private readonly List<(Chunk Chunk, OverlayTransformedModel Handle)> DynamicNetworkOverlays = new();
    private readonly List<IModel> OwnedDynamicNetworkHighlightModels = new();
    private readonly List<Chunk> GridChunks = new();

    private BuiltSplineNetworkResult? ActiveSplineNetwork;
    private WireframeDebugModel? ChunkGridModel;
    private WireframeDebugModel? FreePortMarkerModel;
    private WireframeDebugModel? ConnectedPortMarkerModel;
    private WireframeDebugModel? SelectedPortMarkerModel;
    private WireframeDebugModel? AnchorFlagModel;
    private OverlayTransformedModel? SelectionBoxOverlay;
    private OverlayTransformedModel? TranslateXOverlay;
    private OverlayTransformedModel? TranslateYOverlay;
    private OverlayTransformedModel? TranslateZOverlay;
    private OverlayTransformedModel? RotateYOverlay;
    private OverlayTransformedModel? PlacementCursorOverlay;
    private string LastModelsSignature = string.Empty;
    private string LastNetworkSignature = string.Empty;
    private string LastNetworkOverlayStateKey = string.Empty;
    private ChunkIndex? LastGridCameraChunk;
    private bool LastGridVisible;
    private int LastGridDrawChunkCount;
    private bool CurrentNormalVisible = true;
    private bool? LastNormalVisible;
    private BoundingBox? LastSelectionBounds;
    private bool Disposed;

    /// <summary>
    /// Chunk range matching <c>RendererOptions.DrawChunkCount</c> (grid draws radius = <c>GridDrawChunkCount - 1</c>).
    /// </summary>
    public int GridDrawChunkCount { get; set; } = 3;

    public override IModelCollection Models => ModelsStore;
    public override ISoundCollection Sounds { get; } = new SoundCollection();

    public IReadOnlyCollection<PropRenderEntry> Props => PropEntries.Values;
    public IReadOnlyCollection<SplineRenderEntry> Splines =>
        ActiveSplineNetwork is null ? Array.Empty<SplineRenderEntry>() : ActiveSplineNetwork.Entries.Values;
    public IReadOnlyCollection<JunctionRenderEntry> Junctions =>
        ActiveSplineNetwork is null ? Array.Empty<JunctionRenderEntry>() : ActiveSplineNetwork.JunctionEntries.Values;

    public EditorWorld(WorldBuilder builder)
        : base(new PluginLoadContext(typeof(EditorWorld).Assembly.Location), builder)
    {
        AppContext.Children.Add(WorldContext);
        InitializeOverlayModels();
    }

    private void InitializeOverlayModels()
    {
        Vortice.Direct3D11.ID3D11Device device = GraphicsHost.Device;

        ChunkGridModel = EditorWireframeBuilder.CreateChunkGrid(device);

        Material selMat = Material.Default();
        selMat.Name = "SelectionBox";
        BoundingBox initialBounds = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f));
        WireframeDebugModel selBox = EditorWireframeBuilder.CreateBoundingBox(device, selMat, initialBounds.Min, initialBounds.Max);
        selBox.Color = new Vector4(1f, 0.92f, 0.2f, 1f);
        LastSelectionBounds = initialBounds;
        SelectionBoxOverlay = new OverlayTransformedModel(selBox, Pose.Identity) { IsVisible = false };

        WireframeDebugModel gx = EditorWireframeBuilder.CreateAxisArrow(device, Vector3.UnitX, new Vector4(1f, 0.22f, 0.22f, 1f));
        WireframeDebugModel gy = EditorWireframeBuilder.CreateAxisArrow(device, Vector3.UnitY, new Vector4(0.25f, 0.95f, 0.35f, 1f));
        WireframeDebugModel gz = EditorWireframeBuilder.CreateAxisArrow(device, Vector3.UnitZ, new Vector4(0.25f, 0.55f, 1.0f, 1f));
        WireframeDebugModel ry = EditorWireframeBuilder.CreateRotationRing(device, Vector3.UnitY, new Vector4(0.35f, 0.9f, 1.0f, 1f));
        WireframeDebugModel cursor = EditorWireframeBuilder.CreatePlacementCursor(device, new Vector4(0.3f, 1.0f, 0.7f, 0.9f));

        TranslateXOverlay = new OverlayTransformedModel(gx, Pose.Identity) { IsVisible = false };
        TranslateYOverlay = new OverlayTransformedModel(gy, Pose.Identity) { IsVisible = false };
        TranslateZOverlay = new OverlayTransformedModel(gz, Pose.Identity) { IsVisible = false };
        RotateYOverlay = new OverlayTransformedModel(ry, Pose.Identity) { IsVisible = false };
        PlacementCursorOverlay = new OverlayTransformedModel(cursor, Pose.Identity) { IsVisible = false };

        FreePortMarkerModel = EditorWireframeBuilder.CreatePortMarker(device, new Vector4(0.25f, 1.0f, 0.45f, 1f));
        ConnectedPortMarkerModel = EditorWireframeBuilder.CreatePortMarker(device, new Vector4(0.35f, 0.85f, 1.0f, 0.9f), 0.65f);
        SelectedPortMarkerModel = EditorWireframeBuilder.CreatePortMarker(device, new Vector4(1.0f, 0.92f, 0.2f, 1f), 1.05f);
        SelectedPortMarkerModel.DebugName = "SelectedPortMarker";
        AnchorFlagModel = EditorWireframeBuilder.CreateAnchorMarker(device, new Vector4(1.0f, 0.78f, 0.18f, 1f));

        PersistentOverlayHandles.Add(SelectionBoxOverlay);
        PersistentOverlayHandles.Add(TranslateXOverlay);
        PersistentOverlayHandles.Add(TranslateYOverlay);
        PersistentOverlayHandles.Add(TranslateZOverlay);
        PersistentOverlayHandles.Add(RotateYOverlay);
        PersistentOverlayHandles.Add(PlacementCursorOverlay);
    }

    public void ClearWorldContents()
    {
        DetachAllPersistentOverlays();
        ClearDynamicNetworkOverlays();

        foreach (Chunk chunk in Chunks)
        {
            chunk.Network.Clear();
        }

        if (ActiveSplineNetwork is not null)
        {
            ActiveSplineNetwork.Dispose();
            ActiveSplineNetwork = null;
        }

        foreach (Chunk chunk in Chunks)
        {
            foreach (TransformedModel model in chunk.Models)
            {
                if (model is CollidableTransformedModel collidable)
                {
                    collidable.Dispose();
                }
            }

            chunk.Models.Clear();
        }

        PropEntries.Clear();
        BackgroundModels.Clear();
        GridChunks.Clear();
        LastGridCameraChunk = null;
        LastGridVisible = false;
        ReleaseAllModelBundles();
        LastModelsSignature = string.Empty;
        LastNetworkSignature = string.Empty;
        LastNetworkOverlayStateKey = string.Empty;
    }

    private void ReleaseAllModelBundles()
    {
        List<string> bundleKeys = new List<string>();
        foreach (IModelBundle bundle in ModelsStore.Bundles)
        {
            bundleKeys.Add(bundle.Key);
        }

        foreach (string key in bundleKeys)
        {
            ModelsStore.ReleaseBundle(key);
        }
    }

    public void RebuildFromProject(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        ClearWorldContents();
        Location = project.WorldInfoFilePath;
        BaseDirectory = project.WorldDirectory;

        EditorModelLoader.LoadProjectModels(project, GraphicsHost, PhysicsHost, ErrorCollector, ModelsStore);
        LastModelsSignature = ComputeModelsSignature(project);

        ApplyEnvironmentAndLighting(project);

        DefaultCameraPose = WorldCoordinateHelper.ToWorldPose(project.Placement.Camera.Pose);
        RebuildBackgroundModels(project);

        foreach (ChunkPropPlacement prop in project.Placement.Props)
        {
            AddOrReplacePropInternal(prop);
        }

        RebuildSplineNetworkInternal(project);
        Chunks.SetCameraChunk(Camera.WorldPose.Chunk, Options.SimulationChunkCount);
    }

    public void SyncFromProjectIncremental(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string sig = ComputeModelsSignature(project);
        if (!string.Equals(sig, LastModelsSignature, StringComparison.Ordinal)
            || !string.Equals(Location, project.WorldInfoFilePath, StringComparison.Ordinal))
        {
            RebuildFromProject(project);
            return;
        }

        ApplyEnvironmentAndLighting(project);
        DefaultCameraPose = WorldCoordinateHelper.ToWorldPose(project.Placement.Camera.Pose);
        RebuildBackgroundModels(project);

        HashSet<string> incomingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChunkPropPlacement prop in project.Placement.Props)
        {
            incomingIds.Add(prop.Id);
            if (PropEntries.TryGetValue(prop.Id, out PropRenderEntry? existing))
            {
                if (!string.Equals(existing.Placement.ModelKey, prop.ModelKey, StringComparison.Ordinal))
                {
                    AddOrReplaceProp(prop);
                }
                else
                {
                    UpdatePropPose(prop);
                }
            }
            else
            {
                AddOrReplacePropInternal(prop);
            }
        }

        List<string> toRemove = new List<string>();
        foreach (string existingId in PropEntries.Keys)
        {
            if (!incomingIds.Contains(existingId))
            {
                toRemove.Add(existingId);
            }
        }

        foreach (string remId in toRemove)
        {
            RemoveProp(remId);
        }

        string networkSig = ComputeNetworkSignature(project);
        if (!string.Equals(networkSig, LastNetworkSignature, StringComparison.Ordinal))
        {
            RebuildSplineNetworkInternal(project);
        }

        Chunks.SetCameraChunk(Camera.WorldPose.Chunk, Options.SimulationChunkCount);
    }

    private void RebuildSplineNetworkInternal(WorldProject project)
    {
        ClearDynamicNetworkOverlays();

        foreach (Chunk chunk in Chunks)
        {
            chunk.Network.Clear();
        }

        if (ActiveSplineNetwork is not null)
        {
            ActiveSplineNetwork.Dispose();
            ActiveSplineNetwork = null;
        }

        ActiveSplineNetwork = EditorSplineNetworkBuilder.Build(
            project,
            this,
            GraphicsHost,
            PhysicsHost,
            ModelsStore,
            ErrorCollector);
        LastNetworkSignature = ComputeNetworkSignature(project);
        LastNetworkOverlayStateKey = string.Empty;

        if (!CurrentNormalVisible)
        {
            ApplyNormalVisibility(false);
        }
    }

    private static string ComputeNetworkSignature(WorldProject project)
    {
        StringBuilder sb = new();
        foreach (LaneTrafficTypeDefinition t in project.Init.LaneTraffic.Types)
        {
            sb.Append(t.Key).Append('=').Append(t.ColorHex).Append(';');
        }

        foreach (LaneTrafficGroupDefinition g in project.Init.LaneTraffic.Groups)
        {
            sb.Append(g.Key).Append('=').Append(string.Join('|', g.TypeKeys)).Append(',').Append(g.ColorHex).Append(';');
        }

        foreach (LaneLayoutDocument layout in project.LaneLayouts.Values.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            sb.Append("L:").Append(layout.Key).Append(':').Append(layout.Lanes.Count).Append(':');
            foreach (LaneDefinition lane in layout.Lanes)
            {
                sb.Append(lane.AllowedTrafficKey).Append(',').Append((int)lane.Directions)
                  .Append(',').Append(lane.X).Append(',').Append(lane.Y)
                  .Append(',').Append(lane.LeftWidth).Append(',').Append(lane.RightWidth).Append('|');
            }
        }

        foreach (CrossSectionDocument cs in project.CrossSections.Values.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            sb.Append("CS:").Append(cs.Key).Append(':').Append(cs.Vertices.Count).Append(',').Append(cs.Surfaces.Count).Append(',');
            foreach (CrossSectionVertexModel v in cs.Vertices)
            {
                sb.Append(v.Key).Append('@').Append(v.X).Append(',').Append(v.Y).Append(';');
            }

            foreach (CrossSectionSurfaceModel s in cs.Surfaces)
            {
                sb.Append(s.MaterialName).Append('#').Append(s.ColliderMaterialName).Append('#').Append(s.VInterval).Append('#').Append(string.Join('+', s.VertexKeys)).Append(';');
            }
        }

        foreach (SplineTemplateDocument tpl in project.SplineTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            sb.Append("ST:").Append(tpl.Key).Append('>').Append(tpl.OutletLayoutKey).Append(':');
            foreach (SplineTemplateExtrusionRule ex in tpl.Extrusions)
            {
                sb.Append("STE:").Append(ex.Key).Append('/').Append(ex.CrossSectionKey).Append('/')
                  .Append(ex.OffsetX).Append(',').Append(ex.OffsetY).Append('/')
                  .Append(ex.BaseInterval).Append(',').Append(ex.MaxDeviation).Append(',').Append(ex.MaxAngleDeltaDeg).Append('/')
                  .Append(ex.VIntervalScale).Append(',').Append(ex.VOffset).Append('/')
                  .Append(ex.From).Append(';');
            }

            foreach (SplineTemplatePropRule pr in tpl.Props)
            {
                sb.Append("STP:").Append(pr.Key).Append('/').Append(string.Join(',', pr.ModelKeys)).Append('/')
                  .Append(pr.Pose.X).Append(',').Append(pr.Pose.Y).Append(',').Append(pr.Pose.Z).Append(',')
                  .Append(pr.Pose.RotationX).Append(',').Append(pr.Pose.RotationY).Append(',').Append(pr.Pose.RotationZ).Append('/')
                  .Append(pr.From).Append(',').Append(pr.Span).Append(',').Append(pr.Interval).Append(';');
            }
        }

        foreach (SplinePlacementNode sp in project.Placement.Splines)
        {
            sb.Append("SP:").Append(sp.Id).Append('#').Append(sp.TemplateKey).Append('#')
              .Append((int)sp.Origin.Kind).Append(':').Append(sp.Origin.SourceNodeId).Append(':')
              .Append(sp.Origin.Chunk.X).Append(',').Append(sp.Origin.Chunk.Z).Append(':')
              .Append(sp.Origin.Pose.X).Append(',').Append(sp.Origin.Pose.Y).Append(',').Append(sp.Origin.Pose.Z).Append(',')
              .Append(sp.Origin.Pose.RotationX).Append(',').Append(sp.Origin.Pose.RotationY).Append(',').Append(sp.Origin.Pose.RotationZ).Append('|');

            foreach (SplineCurveSegment c in sp.Curves)
            {
                sb.Append((int)c.Kind).Append(':').Append(c.Value).Append(':').Append(c.Length).Append(':').Append(c.OutputAnchorId).Append(';');
            }

            foreach (SplineGradientSegment g in sp.Gradients)
            {
                sb.Append('G').Append((int)g.Kind).Append(':').Append(g.Delta).Append(':').Append(g.Length).Append(':').Append(g.OutputAnchorId).Append(';');
            }

            foreach (SplineCantSegment ca in sp.Cants)
            {
                sb.Append('K').Append((int)ca.Kind).Append(':').Append(ca.Target).Append(':').Append(ca.Gauge).Append(':').Append(ca.Length).Append(':').Append(ca.OutputAnchorId).Append(';');
            }

            if (sp.ConnectBezier is not null)
            {
                sb.Append("CB:").Append(sp.ConnectBezier.TargetPort.ElementId).Append(':').Append((int)sp.ConnectBezier.TargetPort.Kind)
                  .Append(':').Append(sp.ConnectBezier.TargetPort.PortKey).Append(':').Append(sp.ConnectBezier.HandleScale).Append(':').Append(sp.ConnectBezier.OutputAnchorId).Append(';');
            }

            foreach (SplineInstancePropRule pr in sp.Props)
            {
                sb.Append("IP:").Append(pr.Key).Append('/').Append(string.Join(',', pr.ModelKeys)).Append('/')
                  .Append(pr.Pose.X).Append(',').Append(pr.Pose.Y).Append(',').Append(pr.Pose.Z).Append(',')
                  .Append(pr.Pose.RotationX).Append(',').Append(pr.Pose.RotationY).Append(',').Append(pr.Pose.RotationZ).Append('/')
                  .Append(pr.From.AnchorId).Append('+').Append(pr.From.Offset).Append('/')
                  .Append(pr.Span).Append('/').Append(pr.Interval).Append('/').Append(pr.Count).Append(';');
            }

            foreach (SplineSkipRule sk in sp.PropSkips)
            {
                sb.Append("PS:").Append(sk.Key).Append('/').Append(sk.From?.AnchorId).Append('+').Append(sk.From?.Offset).Append('-').Append(sk.To?.AnchorId).Append('+').Append(sk.To?.Offset).Append(';');
            }

            foreach (CurveExtrusionPlacementRule ex in sp.Extrusions)
            {
                AppendCurveExtrusionSignature(sb, "IE:", ex);
            }

            foreach (SplineSkipRule sk in sp.ExtrusionSkips)
            {
                sb.Append("ES:").Append(sk.Key).Append('/').Append(sk.From?.AnchorId).Append('+').Append(sk.From?.Offset).Append('-').Append(sk.To?.AnchorId).Append('+').Append(sk.To?.Offset).Append(';');
            }
        }

        foreach (JunctionTemplateDocument jt in project.JunctionTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            sb.Append("JT:").Append(jt.Key).Append(':');
            foreach (JunctionPortDefinition p in jt.Ports)
            {
                sb.Append("P:").Append(p.Key).Append('=').Append(p.LayoutKey).Append('@')
                  .Append(p.Pose.X).Append(',').Append(p.Pose.Y).Append(',').Append(p.Pose.Z).Append(',')
                  .Append(p.Pose.RotationX).Append(',').Append(p.Pose.RotationY).Append(',').Append(p.Pose.RotationZ).Append(';');
            }

            foreach (JunctionRelayDefinition r in jt.Relays)
            {
                sb.Append("R:").Append(r.InputKey).Append("->").Append(r.OutputKey).Append('=').Append(r.LayoutKey).Append('@')
                  .Append(r.Pose.X).Append(',').Append(r.Pose.Y).Append(',').Append(r.Pose.Z).Append(',')
                  .Append(r.Pose.RotationY).Append(';');
            }

            foreach (JunctionPathTemplateModel w in jt.Paths)
            {
                sb.Append("W:").Append(w.Key).Append(':').Append(w.FromPortKey).Append('[').Append(w.FromPinIndex).Append("]->")
                  .Append(w.ToPortKey).Append('[').Append(w.ToPinIndex).Append(']').Append('#').Append(w.Segments.Count).Append('#').Append(w.WidthSegments.Count).Append(';');
                foreach (JunctionCurveSegment s in w.Segments)
                {
                    sb.Append((int)s.Kind).Append(':').Append(s.TargetPose.X).Append(',').Append(s.TargetPose.Y).Append(',').Append(s.TargetPose.Z).Append(',').Append(s.HandleScale).Append(',').Append(s.OutputAnchorId).Append(';');
                }

                foreach (JunctionWidthSegment ws in w.WidthSegments)
                {
                    sb.Append((int)ws.Kind).Append(':').Append(ws.LeftWidth).Append(',').Append(ws.RightWidth).Append(',').Append(ws.Length.AnchorId).Append('+').Append(ws.Length.Offset).Append(';');
                }

                foreach (CurvePropPlacementRule pr in w.Props)
                {
                    AppendCurvePropSignature(sb, "WP:", pr);
                }

                foreach (CurveExtrusionPlacementRule ex in w.Extrusions)
                {
                    AppendCurveExtrusionSignature(sb, "WE:", ex);
                }
            }

            foreach (JunctionCurveTemplateModel c in jt.Curves)
            {
                sb.Append("AC:").Append(c.Key).Append(':').Append((int)c.Origin.Kind).Append(':').Append(c.Origin.PortKey).Append('[').Append(c.Origin.PinIndex).Append("]@")
                  .Append(c.Origin.Pose.X).Append(',').Append(c.Origin.Pose.Y).Append(',').Append(c.Origin.Pose.Z).Append(',').Append(c.Origin.Pose.RotationY).Append(';');
                foreach (JunctionCurveSegment s in c.Segments)
                {
                    sb.Append((int)s.Kind).Append(':').Append(s.TargetPose.X).Append(',').Append(s.TargetPose.Y).Append(',').Append(s.TargetPose.Z).Append(',').Append(s.HandleScale).Append(';');
                }

                foreach (CurvePropPlacementRule pr in c.Props)
                {
                    AppendCurvePropSignature(sb, "CP:", pr);
                }

                foreach (CurveExtrusionPlacementRule ex in c.Extrusions)
                {
                    AppendCurveExtrusionSignature(sb, "CE:", ex);
                }
            }

            foreach (JunctionStaticPropPlacement spProp in jt.Props)
            {
                sb.Append("JP:").Append(spProp.ModelKey).Append('@')
                  .Append(spProp.Pose.X).Append(',').Append(spProp.Pose.Y).Append(',').Append(spProp.Pose.Z).Append(',')
                  .Append(spProp.Pose.RotationX).Append(',').Append(spProp.Pose.RotationY).Append(',').Append(spProp.Pose.RotationZ).Append(';');
            }
        }

        foreach (JunctionPlacementNode jp in project.Placement.Junctions)
        {
            sb.Append("JN:").Append(jp.Id).Append('#').Append(jp.TemplateKey).Append('#')
              .Append((int)jp.Origin.Kind).Append(':').Append(jp.Origin.SourceNodeId).Append(':').Append(jp.Origin.SourcePortKey).Append(':').Append(jp.Origin.TargetPortKey).Append(':')
              .Append(jp.Origin.Chunk.X).Append(',').Append(jp.Origin.Chunk.Z).Append(':')
              .Append(jp.Origin.Pose.X).Append(',').Append(jp.Origin.Pose.Y).Append(',').Append(jp.Origin.Pose.Z).Append(',')
              .Append(jp.Origin.Pose.RotationX).Append(',').Append(jp.Origin.Pose.RotationY).Append(',').Append(jp.Origin.Pose.RotationZ).Append('|');

            foreach (JunctionStaticPropPlacement pr in jp.Props)
            {
                sb.Append("JIP:").Append(pr.ModelKey).Append('@')
                  .Append(pr.Pose.X).Append(',').Append(pr.Pose.Y).Append(',').Append(pr.Pose.Z).Append(',')
                  .Append(pr.Pose.RotationX).Append(',').Append(pr.Pose.RotationY).Append(',').Append(pr.Pose.RotationZ).Append(';');
            }

            foreach (JunctionPathOverrideModel pov in jp.PathOverrides)
            {
                sb.Append("JPO:").Append(pov.PathKey).Append('=').Append(pov.TrafficDensityFactor).Append('/').Append(pov.Props.Count).Append('/').Append(pov.Extrusions.Count).Append(';');
                foreach (CurvePropPlacementRule pr in pov.Props)
                {
                    AppendCurvePropSignature(sb, "JPOP:", pr);
                }

                foreach (CurveExtrusionPlacementRule ex in pov.Extrusions)
                {
                    AppendCurveExtrusionSignature(sb, "JPOE:", ex);
                }
            }

            foreach (JunctionCurveOverrideModel cov in jp.CurveOverrides)
            {
                sb.Append("JCO:").Append(cov.CurveKey).Append('/').Append(cov.Props.Count).Append('/').Append(cov.Extrusions.Count).Append(';');
                foreach (CurvePropPlacementRule pr in cov.Props)
                {
                    AppendCurvePropSignature(sb, "JCOP:", pr);
                }

                foreach (CurveExtrusionPlacementRule ex in cov.Extrusions)
                {
                    AppendCurveExtrusionSignature(sb, "JCOE:", ex);
                }
            }
        }

        foreach (ExplicitPortConnection conn in project.Placement.Connections)
        {
            sb.Append("CN:").Append(conn.From.ElementId).Append('.').Append((int)conn.From.Kind).Append('.').Append(conn.From.PortKey)
              .Append("->").Append(conn.To.ElementId).Append('.').Append((int)conn.To.Kind).Append('.').Append(conn.To.PortKey).Append(';');
        }

        return sb.ToString();
    }

    private static void AppendCurveExtrusionSignature(StringBuilder sb, string tag, CurveExtrusionPlacementRule ex)
    {
        sb.Append(tag).Append(ex.Key).Append('/').Append(ex.CrossSectionKey).Append('/')
          .Append(ex.OffsetX).Append(',').Append(ex.OffsetY).Append('/')
          .Append(ex.BaseInterval).Append(',').Append(ex.MaxDeviation).Append(',').Append(ex.MaxAngleDeltaDeg).Append('/')
          .Append(ex.VIntervalScale).Append(',').Append(ex.VOffset).Append('/')
          .Append(ex.From.AnchorId).Append('+').Append(ex.From.Offset).Append('-')
          .Append(ex.To?.AnchorId).Append('+').Append(ex.To?.Offset).Append(';');
    }

    private static void AppendCurvePropSignature(StringBuilder sb, string tag, CurvePropPlacementRule pr)
    {
        sb.Append(tag).Append(string.Join(',', pr.ModelKeys)).Append('/')
          .Append(pr.Pose.X).Append(',').Append(pr.Pose.Y).Append(',').Append(pr.Pose.Z).Append(',')
          .Append(pr.Pose.RotationX).Append(',').Append(pr.Pose.RotationY).Append(',').Append(pr.Pose.RotationZ).Append('/')
          .Append(pr.From.AnchorId).Append('+').Append(pr.From.Offset).Append('/')
          .Append(pr.Span).Append('/').Append(pr.Interval).Append('/').Append(pr.Count).Append(';');
    }

    private void RebuildBackgroundModels(WorldProject project)
    {
        BackgroundModels.Clear();
        foreach (string bgKey in project.Placement.BackgroundModels)
        {
            if (ModelsStore.TryGetModel(bgKey, out ModelResourceSet bgRes))
            {
                BackgroundModels.Add(new TransformedModel(bgRes, Pose.Identity) { IsVisible = CurrentNormalVisible });
            }
            else
            {
                ErrorCollector.Report(new TxDiagnostics.Error(
                    TxDiagnostics.ErrorLevel.Warning,
                    $"背景モデル '{bgKey}' が Models に存在しません。",
                    project.Placement.RelativePath));
            }
        }
    }

    private static string ComputeModelsSignature(WorldProject project)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(project.WorldDirectory).Append('|');
        foreach (Core.Models.Assets.ModelListDocument doc in project.ModelLists.Values)
        {
            sb.Append(doc.RelativePath).Append(':');
            foreach (Core.Models.Assets.ModelListEntry entry in doc.Entries)
            {
                sb.Append(entry.Key).Append('=')
                  .Append(entry.ModelPath).Append('#')
                  .Append((int)entry.Collider.Kind).Append('#')
                  .Append(entry.Collider.MakeLeftHanded).Append('#')
                  .Append(entry.Collider.CollisionModelPath).Append('#')
                  .Append(entry.Collider.SizeX).Append(',')
                  .Append(entry.Collider.SizeY).Append(',')
                  .Append(entry.Collider.SizeZ).Append(';');
            }
        }

        return sb.ToString();
    }

    public void ApplyEnvironmentAndLighting(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        DirectionalLightModel dl = project.Placement.DirectionalLight;
        Vector3 dir = new Vector3((float)dl.DirectionX, (float)dl.DirectionY, (float)dl.DirectionZ);
        if (dir.LengthSquared() < 1e-6f)
        {
            dir = -Vector3.UnitY;
        }
        else
        {
            dir = Vector3.Normalize(dir);
        }

        DirectionalLight = new TxDirectionalLight
        {
            Color = ParseColorRgb(dl.ColorHex, Vector3.One),
            Direction = dir,
            Intensity = (float)dl.Intensity,
        };

        string? envRel = project.Placement.EnvironmentPath;
        if (!string.IsNullOrWhiteSpace(envRel))
        {
            string normalized = WorldProject.NormalizeRelativePath(envRel);
            if (project.Environments.TryGetValue(normalized, out EnvironmentProfileDocument? envDoc))
            {
                DefaultEnvironment = ToRuntimeEnvironment(envDoc);
                return;
            }
        }

        DefaultEnvironment = EnvironmentProfile.Default;
    }

    public bool UpdatePropPose(ChunkPropPlacement updatedProp)
    {
        ArgumentNullException.ThrowIfNull(updatedProp);

        if (!PropEntries.TryGetValue(updatedProp.Id, out PropRenderEntry? entry))
        {
            return false;
        }

        WorldPoseDegreesModel normalized = new WorldPoseDegreesModel(updatedProp.Chunk, updatedProp.Pose).Normalized();
        ChunkIndex targetChunk = new ChunkIndex(normalized.Chunk.X, normalized.Chunk.Z);
        Pose targetPose = WorldCoordinateHelper.ToLocalPose(normalized);

        if (entry.ChunkIndex == targetChunk)
        {
            entry.Model.Pose = targetPose;
            entry.Placement = updatedProp with { Chunk = normalized.Chunk, Pose = normalized.LocalPose };
            return true;
        }

        // Crossed a 250m chunk boundary: relocate model into destination chunk
        if (Chunks.TryGetValue(entry.ChunkIndex, out Chunk? oldChunk))
        {
            oldChunk.Models.Remove(entry.Model);
        }

        Chunk newChunk = Chunks.GetOrAdd(targetChunk);
        entry.Model.Pose = targetPose;
        if (entry.Model is CollidableTransformedModel collidable)
        {
            collidable.SetFromCamera(targetChunk - Camera.WorldPose.Chunk);
        }

        newChunk.Models.Add(entry.Model);
        entry.ChunkIndex = targetChunk;
        entry.Placement = updatedProp with { Chunk = normalized.Chunk, Pose = normalized.LocalPose };
        return true;
    }

    public void AddOrReplaceProp(ChunkPropPlacement prop)
    {
        RemoveProp(prop.Id);
        AddOrReplacePropInternal(prop);
    }

    public void RemoveProp(string propId)
    {
        if (!PropEntries.TryGetValue(propId, out PropRenderEntry? entry))
        {
            return;
        }

        if (Chunks.TryGetValue(entry.ChunkIndex, out Chunk? chunk))
        {
            chunk.Models.Remove(entry.Model);
        }

        if (entry.Model is CollidableTransformedModel collidable)
        {
            collidable.Dispose();
        }

        PropEntries.Remove(propId);
    }

    public void UpdateOverlays(
        string? selectedPropId,
        EditorToolMode toolMode,
        GizmoAxis activeOrHoveredAxis,
        ViewportOverlayFlags overlayFlags,
        Vector3? placementCursorWorldPos,
        ChunkIndex cameraChunk)
    {
        UpdateOverlays(
            selectedPropId,
            null,
            null,
            toolMode,
            activeOrHoveredAxis,
            overlayFlags,
            placementCursorWorldPos,
            cameraChunk);
    }

    public void UpdateOverlays(
        string? selectedPropId,
        string? selectedSplineId,
        PortReferenceModel? pendingConnectPort,
        EditorToolMode toolMode,
        GizmoAxis activeOrHoveredAxis,
        ViewportOverlayFlags overlayFlags,
        Vector3? placementCursorWorldPos,
        ChunkIndex cameraChunk,
        string? selectedJunctionId = null)
    {
        bool showNormal = overlayFlags.HasFlag(ViewportOverlayFlags.Normal);
        bool showNetwork = overlayFlags.HasFlag(ViewportOverlayFlags.Network);
        bool showGrid = overlayFlags.HasFlag(ViewportOverlayFlags.ChunkGrid);
        bool showGizmos = overlayFlags.HasFlag(ViewportOverlayFlags.Gizmos);
        bool showAnchors = overlayFlags.HasFlag(ViewportOverlayFlags.Anchors);

        // Sync Camera VisualLayers for TransportX DebugPass:
        // Network controls TransportX ILanePath debug drawing exclusively,
        // while editor wireframe overlays render via Traffic so Grid/Gizmos/Anchors stay independent.
        ICamera.VisualLayers layers = ICamera.VisualLayers.None;
        if (showNormal)
        {
            layers |= ICamera.VisualLayers.Normal;
        }

        if (overlayFlags.HasFlag(ViewportOverlayFlags.Colliders))
        {
            layers |= ICamera.VisualLayers.Colliders;
        }

        if (showNetwork)
        {
            layers |= ICamera.VisualLayers.Network;
        }

        if (showGrid
            || showGizmos
            || showAnchors
            || showNetwork
            || toolMode == EditorToolMode.ConnectPort)
        {
            layers |= ICamera.VisualLayers.Traffic;
        }

        Camera.VisibleLayers = layers;

        RefreshNormalVisibility(showNormal);
        RefreshGridChunks(cameraChunk, showGrid);
        RefreshDynamicNetworkOverlays(selectedSplineId, selectedJunctionId, pendingConnectPort, overlayFlags, toolMode);
        DetachAllPersistentOverlays();

        if (!overlayFlags.HasFlag(ViewportOverlayFlags.Gizmos))
        {
            return;
        }

        if (selectedPropId is not null && PropEntries.TryGetValue(selectedPropId, out PropRenderEntry? entry))
        {
            Chunk chunk = Chunks.GetOrAdd(entry.ChunkIndex);

            // Selection OBB outline placed cleanly around prop bounds
            if (SelectionBoxOverlay is not null)
            {
                Vector3 size = entry.LocalBounds.Max - entry.LocalBounds.Min;
                if (size.LengthSquared() < 1e-4f)
                {
                    size = new Vector3(1.5f, 1.5f, 1.5f);
                }

                // Position SelectionBox at center of bounds (wireframe unit box fallback scaled/replaced)
                ReplaceSelectionBoxGeometry(entry.LocalBounds);
                SelectionBoxOverlay.Pose = entry.Model.Pose;
                SelectionBoxOverlay.IsVisible = true;
                chunk.Models.Add(SelectionBoxOverlay);
            }

            if (toolMode == EditorToolMode.Translate)
            {
                Pose gizmoPose = new Pose(entry.Model.Pose.Position);
                UpdateGizmoAxisOverlay(chunk, TranslateXOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.X ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(1f, 0.22f, 0.22f, 1f));
                UpdateGizmoAxisOverlay(chunk, TranslateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Y ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.95f, 0.35f, 1f));
                UpdateGizmoAxisOverlay(chunk, TranslateZOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Z ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.55f, 1.0f, 1f));
            }
            else if (toolMode == EditorToolMode.Rotate)
            {
                Pose gizmoPose = new Pose(entry.Model.Pose.Position);
                UpdateGizmoAxisOverlay(chunk, RotateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Yaw ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.35f, 0.9f, 1.0f, 1f));
            }
        }
        else if (selectedSplineId is not null
            && ActiveSplineNetwork is not null
            && ActiveSplineNetwork.Entries.TryGetValue(selectedSplineId, out SplineRenderEntry? splineEntry)
            && splineEntry.Node.Origin.Kind == SplineOriginKind.ChunkPose)
        {
            WorldPose originWp = WorldCoordinateHelper.ToWorldPose(splineEntry.Node.Origin.Chunk, splineEntry.Node.Origin.Pose);
            Chunk originChunk = Chunks.GetOrAdd(originWp.Chunk);
            Pose gizmoPose = new(originWp.Pose.Position);

            if (toolMode == EditorToolMode.Translate)
            {
                UpdateGizmoAxisOverlay(originChunk, TranslateXOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.X ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(1f, 0.22f, 0.22f, 1f));
                UpdateGizmoAxisOverlay(originChunk, TranslateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Y ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.95f, 0.35f, 1f));
                UpdateGizmoAxisOverlay(originChunk, TranslateZOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Z ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.55f, 1.0f, 1f));
            }
            else if (toolMode == EditorToolMode.Rotate)
            {
                UpdateGizmoAxisOverlay(originChunk, RotateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Yaw ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.35f, 0.9f, 1.0f, 1f));
            }
        }
        else if (selectedJunctionId is not null
            && ActiveSplineNetwork is not null
            && ActiveSplineNetwork.JunctionEntries.TryGetValue(selectedJunctionId, out JunctionRenderEntry? junctionEntry)
            && junctionEntry.Node.Origin.Kind == JunctionOriginKind.ChunkPose)
        {
            WorldPose originWp = WorldCoordinateHelper.ToWorldPose(junctionEntry.Node.Origin.Chunk, junctionEntry.Node.Origin.Pose);
            Chunk originChunk = Chunks.GetOrAdd(originWp.Chunk);
            Pose gizmoPose = new(originWp.Pose.Position);

            if (toolMode == EditorToolMode.Translate)
            {
                UpdateGizmoAxisOverlay(originChunk, TranslateXOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.X ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(1f, 0.22f, 0.22f, 1f));
                UpdateGizmoAxisOverlay(originChunk, TranslateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Y ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.95f, 0.35f, 1f));
                UpdateGizmoAxisOverlay(originChunk, TranslateZOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Z ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.25f, 0.55f, 1.0f, 1f));
            }
            else if (toolMode == EditorToolMode.Rotate)
            {
                UpdateGizmoAxisOverlay(originChunk, RotateYOverlay, gizmoPose,
                    activeOrHoveredAxis == GizmoAxis.Yaw ? new Vector4(1f, 0.95f, 0.25f, 1f) : new Vector4(0.35f, 0.9f, 1.0f, 1f));
            }
        }

        if ((toolMode == EditorToolMode.PlaceProp || toolMode == EditorToolMode.PlaceSpline || toolMode == EditorToolMode.PlaceJunction)
            && placementCursorWorldPos.HasValue
            && PlacementCursorOverlay is not null)
        {
            Chunk camChunk = Chunks.GetOrAdd(cameraChunk);
            PlacementCursorOverlay.Pose = new Pose(placementCursorWorldPos.Value);
            PlacementCursorOverlay.IsVisible = true;
            camChunk.Models.Add(PlacementCursorOverlay);
        }
    }

    private void RefreshNormalVisibility(bool showNormal)
    {
        CurrentNormalVisible = showNormal;
        if (LastNormalVisible == showNormal)
        {
            return;
        }

        LastNormalVisible = showNormal;
        ApplyNormalVisibility(showNormal);
    }

    private void ApplyNormalVisibility(bool showNormal)
    {
        foreach (TransformedModel bgModel in BackgroundModels)
        {
            bgModel.IsVisible = showNormal;
        }

        foreach (Chunk chunk in Chunks)
        {
            foreach (TransformedModel model in chunk.Models)
            {
                if (model is not OverlayTransformedModel)
                {
                    model.IsVisible = showNormal;
                }
            }

            foreach (NetworkElement element in chunk.Network)
            {
                foreach (TransformedModel model in element.Models)
                {
                    model.IsVisible = showNormal;
                }
            }
        }
    }

    private void RefreshDynamicNetworkOverlays(
        string? selectedSplineId,
        string? selectedJunctionId,
        PortReferenceModel? pendingConnectPort,
        ViewportOverlayFlags overlayFlags,
        EditorToolMode toolMode)
    {
        bool showNetwork = overlayFlags.HasFlag(ViewportOverlayFlags.Network);
        bool showGizmos = overlayFlags.HasFlag(ViewportOverlayFlags.Gizmos);
        bool isConnectPortTool = toolMode == EditorToolMode.ConnectPort;
        bool showPorts = showNetwork || isConnectPortTool;
        bool showAnchors = overlayFlags.HasFlag(ViewportOverlayFlags.Anchors);
        string pendingKey = pendingConnectPort is not null
            ? $"{pendingConnectPort.ElementId}:{(int)pendingConnectPort.Kind}:{pendingConnectPort.PortKey}"
            : string.Empty;
        string stateKey = $"{LastNetworkSignature}|{selectedSplineId}|{selectedJunctionId}|{pendingKey}|{showGizmos}|{showPorts}|{showAnchors}|{isConnectPortTool}";
        if (string.Equals(stateKey, LastNetworkOverlayStateKey, StringComparison.Ordinal))
        {
            return;
        }

        LastNetworkOverlayStateKey = stateKey;
        ClearDynamicNetworkOverlays();

        if (ActiveSplineNetwork is null)
        {
            return;
        }

        foreach (SplineRenderEntry entry in ActiveSplineNetwork.Entries.Values)
        {
            bool isSelected = string.Equals(entry.Node.Id, selectedSplineId, StringComparison.Ordinal);

            AddPortOverlayMarkers(entry.Ports, isSelected, showPorts, showGizmos, isConnectPortTool, pendingConnectPort);

            if (isSelected && showGizmos && 1 < entry.PrimaryChunkCenterlinePoints.Count)
            {
                WireframeDebugModel highlightModel = EditorWireframeBuilder.CreatePolyline(
                    GraphicsHost.Device,
                    entry.PrimaryChunkCenterlinePoints,
                    new Vector4(1f, 0.92f, 0.2f, 1f));
                OwnedDynamicNetworkHighlightModels.Add(highlightModel);
                Chunk chunk = Chunks.GetOrAdd(entry.PrimaryChunk);
                OverlayTransformedModel handle = new(highlightModel, Pose.Identity) { IsVisible = true };
                chunk.Models.Add(handle);
                DynamicNetworkOverlays.Add((chunk, handle));
            }

            if (isSelected && showAnchors && AnchorFlagModel is not null)
            {
                foreach (SplineAnchorWorldLocation anchor in entry.Anchors)
                {
                    Chunk chunk = Chunks.GetOrAdd(anchor.Chunk);
                    OverlayTransformedModel handle = new(AnchorFlagModel, new Pose(anchor.LocalPosition)) { IsVisible = true };
                    chunk.Models.Add(handle);
                    DynamicNetworkOverlays.Add((chunk, handle));
                }
            }
        }

        foreach (JunctionRenderEntry entry in ActiveSplineNetwork.JunctionEntries.Values)
        {
            bool isSelected = string.Equals(entry.Node.Id, selectedJunctionId, StringComparison.Ordinal);

            AddPortOverlayMarkers(entry.Ports, isSelected, showPorts, showGizmos, isConnectPortTool, pendingConnectPort);

            if (isSelected && showGizmos)
            {
                Chunk chunk = Chunks.GetOrAdd(entry.PrimaryChunk);
                bool hasPolylines = false;

                foreach (IReadOnlyList<Vector3> pathPoly in entry.PrimaryChunkPathPolylines)
                {
                    if (1 < pathPoly.Count)
                    {
                        hasPolylines = true;
                        WireframeDebugModel highlight = EditorWireframeBuilder.CreatePolyline(
                            GraphicsHost.Device,
                            pathPoly,
                            new Vector4(1f, 0.92f, 0.2f, 1f));
                        highlight.DebugName = "JunctionHighlight";
                        OwnedDynamicNetworkHighlightModels.Add(highlight);
                        OverlayTransformedModel handle = new(highlight, Pose.Identity) { IsVisible = true };
                        chunk.Models.Add(handle);
                        DynamicNetworkOverlays.Add((chunk, handle));
                    }
                }

                foreach (IReadOnlyList<Vector3> curvePoly in entry.PrimaryChunkCurvePolylines)
                {
                    if (1 < curvePoly.Count)
                    {
                        hasPolylines = true;
                        WireframeDebugModel highlight = EditorWireframeBuilder.CreatePolyline(
                            GraphicsHost.Device,
                            curvePoly,
                            new Vector4(1f, 0.72f, 0.2f, 1f));
                        highlight.DebugName = "JunctionHighlight";
                        OwnedDynamicNetworkHighlightModels.Add(highlight);
                        OverlayTransformedModel handle = new(highlight, Pose.Identity) { IsVisible = true };
                        chunk.Models.Add(handle);
                        DynamicNetworkOverlays.Add((chunk, handle));
                    }
                }

                if (!hasPolylines)
                {
                    Material mat = Material.Default();
                    mat.Name = "JunctionSelectionBox";
                    WireframeDebugModel box = EditorWireframeBuilder.CreateBoundingBox(
                        GraphicsHost.Device,
                        mat,
                        entry.LocalBounds.Min,
                        entry.LocalBounds.Max);
                    box.Color = new Vector4(1f, 0.92f, 0.2f, 1f);
                    box.DebugName = "JunctionHighlight";
                    OwnedDynamicNetworkHighlightModels.Add(box);
                    OverlayTransformedModel handle = new(box, entry.BuiltJunction.WorldPose.Pose) { IsVisible = true };
                    chunk.Models.Add(handle);
                    DynamicNetworkOverlays.Add((chunk, handle));
                }
            }
        }
    }

    private void AddPortOverlayMarkers(
        IReadOnlyList<SplinePortWorldLocation> ports,
        bool isSelected,
        bool showPorts,
        bool showGizmos,
        bool isConnectPortTool,
        PortReferenceModel? pendingConnectPort)
    {
        foreach (SplinePortWorldLocation port in ports)
        {
            if (showPorts)
            {
                WireframeDebugModel? baseModel = port.IsConnected ? ConnectedPortMarkerModel : FreePortMarkerModel;
                if (baseModel is not null)
                {
                    AttachDynamicOverlayModel(port.Chunk, new Pose(port.LocalPosition), baseModel);
                }
            }

            bool isPending = pendingConnectPort is not null && pendingConnectPort == port.PortRef;
            bool showSelectedPort = SelectedPortMarkerModel is not null
                && ((isSelected && showGizmos) || (isPending && (showGizmos || isConnectPortTool)));
            if (showSelectedPort)
            {
                AttachDynamicOverlayModel(port.Chunk, new Pose(port.LocalPosition), SelectedPortMarkerModel!);
            }
        }
    }

    private void AttachDynamicOverlayModel(ChunkIndex chunkIndex, Pose pose, WireframeDebugModel model)
    {
        Chunk chunk = Chunks.GetOrAdd(chunkIndex);
        OverlayTransformedModel handle = new(model, pose) { IsVisible = true };
        chunk.Models.Add(handle);
        DynamicNetworkOverlays.Add((chunk, handle));
    }

    private void ClearDynamicNetworkOverlays()
    {
        foreach ((Chunk chunk, OverlayTransformedModel handle) in DynamicNetworkOverlays)
        {
            chunk.Models.Remove(handle);
        }

        DynamicNetworkOverlays.Clear();

        foreach (IModel model in OwnedDynamicNetworkHighlightModels)
        {
            model.Dispose();
        }

        OwnedDynamicNetworkHighlightModels.Clear();
    }

    private void UpdateGizmoAxisOverlay(Chunk chunk, OverlayTransformedModel? overlay, Pose pose, Vector4 color)
    {
        if (overlay is null)
        {
            return;
        }

        if (overlay.Resource.Model is IDebugModel debugModel)
        {
            debugModel.Color = color;
        }

        overlay.Pose = pose;
        overlay.IsVisible = true;
        chunk.Models.Add(overlay);
    }

    private void ReplaceSelectionBoxGeometry(BoundingBox localBounds)
    {
        if (SelectionBoxOverlay is null)
        {
            return;
        }

        Vector3 min = localBounds.Min;
        Vector3 max = localBounds.Max;
        if ((max - min).LengthSquared() < 1e-4f)
        {
            min = new Vector3(-0.75f, 0f, -0.75f);
            max = new Vector3(0.75f, 1.5f, 0.75f);
        }
        else
        {
            min -= new Vector3(0.05f);
            max += new Vector3(0.05f);
        }

        BoundingBox desiredBounds = new BoundingBox(min, max);
        if (LastSelectionBounds.HasValue && LastSelectionBounds.Value == desiredBounds)
        {
            return;
        }

        LastSelectionBounds = desiredBounds;
        Material mat = Material.Default();
        mat.Name = "SelectionBox";
        WireframeDebugModel newBox = EditorWireframeBuilder.CreateBoundingBox(GraphicsHost.Device, mat, min, max);
        newBox.Color = new Vector4(1f, 0.92f, 0.2f, 1f);

        IModel oldModel = SelectionBoxOverlay.Resource.Model;
        SelectionBoxOverlay.Resource = new ModelResourceSet(newBox);
        oldModel.Dispose();
    }

    private void RefreshGridChunks(ChunkIndex cameraChunk, bool visible)
    {
        if (ChunkGridModel is null)
        {
            return;
        }

        if (LastGridCameraChunk.HasValue
            && LastGridCameraChunk.Value == cameraChunk
            && LastGridVisible == visible
            && LastGridDrawChunkCount == GridDrawChunkCount)
        {
            return;
        }

        LastGridCameraChunk = cameraChunk;
        LastGridVisible = visible;
        LastGridDrawChunkCount = GridDrawChunkCount;

        foreach (Chunk prev in GridChunks)
        {
            for (int i = prev.Models.Count - 1; 0 <= i; i--)
            {
                if (prev.Models[i] is OverlayTransformedModel ov && ReferenceEquals(ov.Resource.Model, ChunkGridModel))
                {
                    prev.Models.RemoveAt(i);
                }
            }
        }

        GridChunks.Clear();

        if (!visible)
        {
            return;
        }

        int radius = int.Max(0, GridDrawChunkCount - 1);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                ChunkIndex ci = cameraChunk + new ChunkIndex(dx, dz);
                Chunk chunk = Chunks.GetOrAdd(ci);
                chunk.Models.Add(new OverlayTransformedModel(ChunkGridModel, Pose.Identity) { IsVisible = true });
                GridChunks.Add(chunk);
            }
        }
    }

    private void DetachAllPersistentOverlays()
    {
        foreach (Chunk chunk in Chunks)
        {
            foreach (OverlayTransformedModel overlay in PersistentOverlayHandles)
            {
                chunk.Models.Remove(overlay);
            }
        }

        foreach (OverlayTransformedModel overlay in PersistentOverlayHandles)
        {
            overlay.IsVisible = false;
        }
    }

    private void AddOrReplacePropInternal(ChunkPropPlacement prop)
    {
        WorldPoseDegreesModel normalized = new WorldPoseDegreesModel(prop.Chunk, prop.Pose).Normalized();
        ChunkIndex chunkIdx = new ChunkIndex(normalized.Chunk.X, normalized.Chunk.Z);
        Pose pose = WorldCoordinateHelper.ToLocalPose(normalized);

        bool hasLoadedModel = ModelsStore.TryGetModel(prop.ModelKey, out ModelResourceSet found);
        ModelResourceSet resource = hasLoadedModel
            ? found
            : ModelResourceSet.Empty();

        if (!hasLoadedModel && !string.IsNullOrWhiteSpace(prop.ModelKey))
        {
            ErrorCollector.Report(new TxDiagnostics.Error(
                TxDiagnostics.ErrorLevel.Warning,
                $"配置プロップ '{prop.Id}' のモデルキー '{prop.ModelKey}' が読込済み Models に見つかりません。",
                prop.Id));
        }

        TransformedModel model = StaticTransformedModel.CreateStaticOrNonCollision(PhysicsHost, resource, pose);
        model.IsVisible = CurrentNormalVisible;
        if (model is CollidableTransformedModel collidable)
        {
            collidable.SetFromCamera(chunkIdx - Camera.WorldPose.Chunk);
        }

        BoundingBox bounds = resource.Model.BoundingBox;
        if ((bounds.Max - bounds.Min).LengthSquared() < 1e-4f)
        {
            bounds = new BoundingBox(new Vector3(-0.75f, 0f, -0.75f), new Vector3(0.75f, 1.5f, 0.75f));
        }

        Chunk chunk = Chunks.GetOrAdd(chunkIdx);
        chunk.Models.Add(model);

        PropEntries[prop.Id] = new PropRenderEntry(
            prop with { Chunk = normalized.Chunk, Pose = normalized.LocalPose },
            chunkIdx,
            model,
            bounds);
    }

    public override void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        ClearWorldContents();

        foreach (OverlayTransformedModel overlay in PersistentOverlayHandles)
        {
            overlay.Resource.Model.Dispose();
        }

        PersistentOverlayHandles.Clear();
        ChunkGridModel?.Dispose();
        ChunkGridModel = null;
        FreePortMarkerModel?.Dispose();
        FreePortMarkerModel = null;
        ConnectedPortMarkerModel?.Dispose();
        ConnectedPortMarkerModel = null;
        SelectedPortMarkerModel?.Dispose();
        SelectedPortMarkerModel = null;
        AnchorFlagModel?.Dispose();
        AnchorFlagModel = null;

        base.Dispose();
    }

    private static EnvironmentProfile ToRuntimeEnvironment(EnvironmentProfileDocument doc)
    {
        return new EnvironmentProfile
        {
            IBL = new IBLProfile
            {
                Intensity = doc.Ibl.Intensity,
                Saturation = doc.Ibl.Saturation,
            },
            Exposure = new ExposureProfile
            {
                Key = doc.Exposure.Key,
                Min = doc.Exposure.Min,
                Max = doc.Exposure.Max,
                DarkAdaptationSpeed = doc.Exposure.DarkAdaptationSpeed,
                LightAdaptationSpeed = doc.Exposure.LightAdaptationSpeed,
            },
            ToneMap = new ToneMapProfile
            {
                Contrast = doc.ToneMap.Contrast,
                Shoulder = doc.ToneMap.Shoulder,
                MaxLuminance = doc.ToneMap.MaxLuminance,
                MidtoneScale = doc.ToneMap.MidtoneScale,
            },
            Bloom = new BloomProfile
            {
                Threshold = doc.Bloom.Threshold,
                Intensity = doc.Bloom.Intensity,
                Scatter = doc.Bloom.Scatter,
                SoftKnee = doc.Bloom.SoftKnee,
                Tint = ParseColorRgb(doc.Bloom.TintHex, Vector3.One),
            },
        };
    }

    private static Vector3 ParseColorRgb(string? hex, Vector3 fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            System.Drawing.Color c = System.Drawing.ColorTranslator.FromHtml(hex);
            return TxExtensionsColor.ToVector3(c);
        }
        catch
        {
            return fallback;
        }
    }
}
