using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using BepuPhysics.Constraints;
using TransportX.Collections;
using TransportX.Diagnostics;
using TransportX.Extensions.Mathematics;
using TransportX.Extensions.Network.Elements;
using TransportX.Extensions.Network.Paths;
using TransportX.Extensions.Utilities;
using TransportX.Mathematics;
using TransportX.Network;
using TransportX.Physics;
using TransportX.Rendering;
using TransportX.Rendering.Backend;
using TransportX.Rendering.Procedural;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Rendering.Worlds;

/// <summary>
/// Runtime world coordinates of a <see cref="SplineBase"/> inlet or outlet port for viewport overlays and picking.
/// </summary>
public readonly record struct SplinePortWorldLocation(
    PortReferenceModel PortRef,
    ChunkIndex Chunk,
    Vector3 LocalPosition,
    bool IsConnected);

/// <summary>
/// Runtime world coordinates of a resolved <c>out var s</c> anchor marker along a built spline.
/// </summary>
public readonly record struct SplineAnchorWorldLocation(
    string AnchorId,
    double S,
    ChunkIndex Chunk,
    Vector3 LocalPosition);

/// <summary>
/// Runtime metadata pairing a placed <see cref="SplinePlacementNode"/> with its built <see cref="SplineBase"/> segments.
/// </summary>
public sealed class SplineRenderEntry
{
    public SplinePlacementNode Node { get; }
    public IReadOnlyList<SplineBase> BuiltSegments { get; }
    public float TotalLength { get; }
    public IReadOnlyDictionary<string, double> ResolvedAnchors { get; }
    public IReadOnlyList<SplinePortWorldLocation> Ports { get; internal set; }
    public IReadOnlyList<SplineAnchorWorldLocation> Anchors { get; }
    public ChunkIndex PrimaryChunk { get; }
    public IReadOnlyList<Vector3> PrimaryChunkCenterlinePoints { get; }

    public SplineRenderEntry(
        SplinePlacementNode node,
        IReadOnlyList<SplineBase> builtSegments,
        float totalLength,
        IReadOnlyDictionary<string, double> resolvedAnchors,
        IReadOnlyList<SplinePortWorldLocation> ports,
        IReadOnlyList<SplineAnchorWorldLocation> anchors,
        ChunkIndex primaryChunk,
        IReadOnlyList<Vector3> primaryChunkCenterlinePoints)
    {
        Node = node;
        BuiltSegments = builtSegments;
        TotalLength = totalLength;
        ResolvedAnchors = resolvedAnchors;
        Ports = ports;
        Anchors = anchors;
        PrimaryChunk = primaryChunk;
        PrimaryChunkCenterlinePoints = primaryChunkCenterlinePoints;
    }
}

/// <summary>
/// Runtime metadata pairing a placed <see cref="JunctionPlacementNode"/> with its built <see cref="Junction"/> element.
/// </summary>
public sealed class JunctionRenderEntry
{
    public JunctionPlacementNode Node { get; }
    public Junction BuiltJunction { get; }
    public ChunkIndex PrimaryChunk { get; }
    public Vector3 LocalOrigin { get; }
    public BoundingBox LocalBounds { get; }
    public IReadOnlyList<SplinePortWorldLocation> Ports { get; internal set; }
    public IReadOnlyList<IReadOnlyList<Vector3>> PrimaryChunkPathPolylines { get; }
    public IReadOnlyList<IReadOnlyList<Vector3>> PrimaryChunkCurvePolylines { get; }

    public JunctionRenderEntry(
        JunctionPlacementNode node,
        Junction builtJunction,
        ChunkIndex primaryChunk,
        Vector3 localOrigin,
        BoundingBox localBounds,
        IReadOnlyList<SplinePortWorldLocation> ports,
        IReadOnlyList<IReadOnlyList<Vector3>> primaryChunkPathPolylines,
        IReadOnlyList<IReadOnlyList<Vector3>> primaryChunkCurvePolylines)
    {
        Node = node;
        BuiltJunction = builtJunction;
        PrimaryChunk = primaryChunk;
        LocalOrigin = localOrigin;
        LocalBounds = localBounds;
        Ports = ports;
        PrimaryChunkPathPolylines = primaryChunkPathPolylines;
        PrimaryChunkCurvePolylines = primaryChunkCurvePolylines;
    }
}

/// <summary>
/// Owns all procedural GPU/physics resources allocated while building the live network preview.
/// </summary>
public sealed class BuiltSplineNetworkResult : IDisposable
{
    private readonly List<NetworkElement> OwnedNetworkElements = new();
    private readonly List<ModelResourceSet> OwnedProceduralResources = new();
    private readonly List<ID3D11ShaderResourceView> CreatedTextures = new();
    private bool Disposed;

    public Dictionary<string, SplineRenderEntry> Entries { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JunctionRenderEntry> JunctionEntries { get; } = new(StringComparer.Ordinal);

    internal void TrackNetworkElement(NetworkElement element) => OwnedNetworkElements.Add(element);
    internal void TrackProceduralResource(ModelResourceSet resource) => OwnedProceduralResources.Add(resource);
    internal void TrackTexture(ID3D11ShaderResourceView texture) => CreatedTextures.Add(texture);

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        // Dispose network elements once here (frees StaticTransformedModel handles and ILanePath debug models);
        // EditorWorld clears chunk.Network before calling this so WorldBase.Chunks.Dispose never double-disposes.
        foreach (NetworkElement element in OwnedNetworkElements)
        {
            element.Dispose();
        }

        OwnedNetworkElements.Clear();

        foreach (ModelResourceSet resource in OwnedProceduralResources)
        {
            resource.Dispose();
        }

        OwnedProceduralResources.Clear();

        foreach (ID3D11ShaderResourceView tex in CreatedTextures)
        {
            tex.Dispose();
        }

        CreatedTextures.Clear();
    }
}

/// <summary>
/// Builds live <see cref="SplineBase"/> segments, cross-section extrusions, and spline props inside <see cref="EditorWorld"/>
/// using TransportX's own <see cref="SplineFactory"/> and <see cref="CrossSection"/> pipeline.
/// </summary>
public static class EditorSplineNetworkBuilder
{
    public static BuiltSplineNetworkResult Build(
        WorldProject project,
        EditorWorld world,
        IGraphicsHost graphicsHost,
        IPhysicsHost physicsHost,
        IModelCollection models,
        IErrorCollector errorCollector)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(graphicsHost);
        ArgumentNullException.ThrowIfNull(physicsHost);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(errorCollector);

        BuiltSplineNetworkResult result = new();

        HashSet<IModel> sharedBundleModels = CollectSharedBundleModels(models);
        Dictionary<string, LaneTrafficType> trafficTypes = new(StringComparer.Ordinal);
        Dictionary<string, LaneTrafficGroup> trafficGroups = new(StringComparer.Ordinal);
        Dictionary<LaneTrafficGroup, Vector4> groupColors = new();
        BuildLaneTraffic(project, trafficTypes, trafficGroups, groupColors);

        Dictionary<string, LaneLayout> runtimeLayouts = BuildRuntimeLaneLayouts(project, trafficGroups);
        Dictionary<string, CrossSection> runtimeCrossSections = BuildRuntimeCrossSections(project, graphicsHost, errorCollector, result);

        Dictionary<string, SplinePlacementNode> nodeMap = new(StringComparer.Ordinal);
        foreach (SplinePlacementNode splineNode in project.Placement.Splines)
        {
            nodeMap.TryAdd(splineNode.Id, splineNode);
        }

        Dictionary<string, JunctionPlacementNode> junctionMap = new(StringComparer.Ordinal);
        foreach (JunctionPlacementNode junctionNode in project.Placement.Junctions)
        {
            junctionMap.TryAdd(junctionNode.Id, junctionNode);
        }

        Dictionary<string, List<SplineBase>> builtSplinesById = new(StringComparer.Ordinal);
        Dictionary<string, Junction> builtJunctionsById = new(StringComparer.Ordinal);
        NetworkTopologicalSortResult sorted = NetworkTopologicalSorter.Sort(project.Placement);

        foreach (NetworkNodeRef nodeRef in sorted.OrderedNodes)
        {
            if (nodeRef.IsJunction)
            {
                if (junctionMap.TryGetValue(nodeRef.Id, out JunctionPlacementNode? jNode))
                {
                    BuildSingleJunction(
                        project,
                        world,
                        jNode,
                        graphicsHost,
                        physicsHost,
                        models,
                        errorCollector,
                        runtimeLayouts,
                        runtimeCrossSections,
                        groupColors,
                        sharedBundleModels,
                        builtSplinesById,
                        builtJunctionsById,
                        result);
                }

                continue;
            }

            if (nodeMap.TryGetValue(nodeRef.Id, out SplinePlacementNode? node))
            {
                BuildSingleSpline(
                    project,
                    world,
                    node,
                    graphicsHost,
                    physicsHost,
                    models,
                    errorCollector,
                    runtimeLayouts,
                    runtimeCrossSections,
                    groupColors,
                    sharedBundleModels,
                    builtSplinesById,
                    builtJunctionsById,
                    result);
            }
        }

        ConnectExplicitPorts(project, builtSplinesById, builtJunctionsById, errorCollector);
        RefreshPortConnectionStates(result);

        return result;
    }

    private static HashSet<IModel> CollectSharedBundleModels(IModelCollection models)
    {
        HashSet<IModel> set = new(ReferenceEqualityComparer.Instance);
        foreach (IModelBundle bundle in models.Bundles)
        {
            foreach (ModelResourceSet resource in bundle.Models.Values)
            {
                set.Add(resource.Model);
            }
        }

        return set;
    }

    private static void BuildLaneTraffic(
        WorldProject project,
        Dictionary<string, LaneTrafficType> trafficTypes,
        Dictionary<string, LaneTrafficGroup> trafficGroups,
        Dictionary<LaneTrafficGroup, Vector4> groupColors)
    {
        foreach (LaneTrafficTypeDefinition typeDef in project.Init.LaneTraffic.Types)
        {
            if (string.IsNullOrWhiteSpace(typeDef.Key) || trafficTypes.ContainsKey(typeDef.Key))
            {
                continue;
            }

            LaneTrafficType runtimeType = new(typeDef.DisplayName);
            trafficTypes[typeDef.Key] = runtimeType;

            LaneTrafficGroup singleGroup = new(runtimeType);
            trafficGroups[typeDef.Key] = singleGroup;
            groupColors[singleGroup] = ParseColorVector4(typeDef.ColorHex);
        }

        foreach (LaneTrafficGroupDefinition groupDef in project.Init.LaneTraffic.Groups)
        {
            if (string.IsNullOrWhiteSpace(groupDef.Key) || trafficGroups.ContainsKey(groupDef.Key))
            {
                continue;
            }

            List<LaneTrafficType> memberTypes = new();
            foreach (string typeKey in groupDef.TypeKeys)
            {
                if (trafficTypes.TryGetValue(typeKey, out LaneTrafficType? member))
                {
                    memberTypes.Add(member);
                }
            }

            LaneTrafficGroup group = new(memberTypes);
            trafficGroups[groupDef.Key] = group;
            groupColors[group] = ParseColorVector4(groupDef.ColorHex);
        }
    }

    private static Dictionary<string, LaneLayout> BuildRuntimeLaneLayouts(
        WorldProject project,
        IReadOnlyDictionary<string, LaneTrafficGroup> trafficGroups)
    {
        Dictionary<string, LaneLayout> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, LaneLayoutDocument> kvp in project.LaneLayouts)
        {
            List<Lane> lanes = new();
            foreach (LaneDefinition laneDef in kvp.Value.Lanes)
            {
                if (!trafficGroups.TryGetValue(laneDef.AllowedTrafficKey, out LaneTrafficGroup? group))
                {
                    group = new();
                }

                FlowDirections directions = laneDef.Directions switch
                {
                    LaneFlowDirections.In => FlowDirections.In,
                    LaneFlowDirections.Out => FlowDirections.Out,
                    _ => FlowDirections.InOut,
                };

                LaneWidth width = new((float)laneDef.LeftWidth, (float)laneDef.RightWidth);
                lanes.Add(new Lane(group, directions, new Vector2((float)laneDef.X, (float)laneDef.Y), width));
            }

            result[kvp.Key] = new LaneLayout(lanes);
        }

        return result;
    }

    private static Dictionary<string, CrossSection> BuildRuntimeCrossSections(
        WorldProject project,
        IGraphicsHost graphicsHost,
        IErrorCollector errorCollector,
        BuiltSplineNetworkResult result)
    {
        Dictionary<string, CrossSection> map = new(StringComparer.Ordinal);
        TextureFactory textureFactory = new(graphicsHost.Context, graphicsHost.WICFactory);

        foreach (KeyValuePair<string, CrossSectionDocument> kvp in project.CrossSections)
        {
            CrossSectionDocument csDoc = kvp.Value;
            string xmlFullPath = Path.Combine(project.WorldDirectory, csDoc.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string xmlDir = Path.GetDirectoryName(xmlFullPath) ?? project.WorldDirectory;

            Dictionary<string, CrossSectionVertexModel> vertexMap = new(StringComparer.Ordinal);
            foreach (CrossSectionVertexModel v in csDoc.Vertices)
            {
                vertexMap.TryAdd(v.Key, v);
            }

            Dictionary<string, Material> materialMap = new(StringComparer.Ordinal);
            foreach (CrossSectionMaterialModel matModel in csDoc.Materials)
            {
                TextureSlot ormSlot = TextureSlot.Empty;
                try
                {
                    string? occPath = ResolveTexturePath(xmlDir, matModel.OcclusionTexture.Path);
                    string? roughPath = ResolveTexturePath(xmlDir, matModel.RoughnessTexture.Path);
                    string? metalPath = ResolveTexturePath(xmlDir, matModel.MetallicTexture.Path);
                    ID3D11ShaderResourceView? merged = textureFactory.CreateMergedFromFiles(
                        occPath, roughPath, metalPath, true, errorCollector);
                    if (merged is not null)
                    {
                        result.TrackTexture(merged);
                        ormSlot = new TextureSlot(merged, TextureSampleMode.Texture);
                    }
                }
                catch (Exception ex)
                {
                    errorCollector.Report(new Error(ErrorLevel.Warning, $"ORM テクスチャ合成に失敗しました ({matModel.Name}): {ex.Message}", csDoc.RelativePath));
                }

                Vector4 baseColor = ParseColorVector4(matModel.BaseColorHex);
                Vector4 emissive4 = ParseColorVector4(matModel.EmissiveHex);

                Material runtimeMat = new()
                {
                    Name = matModel.Name,
                    BaseColor = baseColor,
                    Metallic = matModel.Metallic,
                    Roughness = matModel.Roughness,
                    Emissive = new Vector3(emissive4.X, emissive4.Y, emissive4.Z) * matModel.EmissiveStrength,
                    BaseColorTexture = LoadTextureSlot(textureFactory, xmlDir, matModel.BaseColorTexture, false, errorCollector, result, csDoc.RelativePath),
                    NormalTexture = LoadTextureSlot(textureFactory, xmlDir, matModel.NormalTexture, true, errorCollector, result, csDoc.RelativePath),
                    ORMTexture = ormSlot,
                    EmissiveTexture = LoadTextureSlot(textureFactory, xmlDir, matModel.EmissiveTexture, false, errorCollector, result, csDoc.RelativePath),
                };

                materialMap.TryAdd(matModel.Name, runtimeMat);
            }

            Dictionary<string, ColliderMaterial> colliderMap = new(StringComparer.Ordinal);
            foreach (CrossSectionColliderMaterialModel colModel in csDoc.ColliderMaterials)
            {
                SpringSettings spring = new(colModel.SpringFrequency, colModel.SpringDampingRatio);
                colliderMap.TryAdd(colModel.Name, new ColliderMaterial(colModel.FrictionCoefficient, colModel.MaxRecoveryVelocity, spring));
            }

            ColliderMaterial? singleColliderMaterial = null;
            List<CrossSectionSurface> surfaces = new();

            foreach (CrossSectionSurfaceModel surfaceModel in csDoc.Surfaces)
            {
                if (!materialMap.TryGetValue(surfaceModel.MaterialName, out Material? material))
                {
                    errorCollector.Report(new Error(ErrorLevel.Warning, $"CrossSection '{csDoc.Key}' のマテリアル '{surfaceModel.MaterialName}' が見つかりません。", csDoc.RelativePath));
                    continue;
                }

                ColliderMaterial? colMat = null;
                if (!string.IsNullOrEmpty(surfaceModel.ColliderMaterialName))
                {
                    if (!colliderMap.TryGetValue(surfaceModel.ColliderMaterialName, out ColliderMaterial resolvedCol))
                    {
                        errorCollector.Report(new Error(ErrorLevel.Warning, $"CrossSection '{csDoc.Key}' の物理材質 '{surfaceModel.ColliderMaterialName}' が見つかりません。", csDoc.RelativePath));
                        continue;
                    }

                    if (singleColliderMaterial is null)
                    {
                        singleColliderMaterial = resolvedCol;
                    }
                    else if (!singleColliderMaterial.Value.Equals(resolvedCol))
                    {
                        errorCollector.Report(new Error(ErrorLevel.Error, $"CrossSection '{csDoc.Key}' で複数の物理材質が混在しているためコライダーをスキップしました。", csDoc.RelativePath));
                        continue;
                    }

                    colMat = resolvedCol;
                }

                List<CrossSectionVertex> verts = new();
                foreach (string vKey in surfaceModel.VertexKeys)
                {
                    if (!vertexMap.TryGetValue(vKey, out CrossSectionVertexModel? vDef))
                    {
                        continue;
                    }

                    Vector2 pos = new(vDef.X, vDef.Y);
                    Vector2 rawNorm = new(vDef.NormalX, vDef.NormalY);
                    Vector2 norm = 1e-6f < rawNorm.LengthSquared() ? Vector2.Normalize(rawNorm) : Vector2.UnitY;
                    Vector4 color = ParseColorVector4(vDef.ColorHex);
                    CrossSectionVertex vert = new(pos, norm, vDef.U)
                    {
                        Color = color,
                    };
                    verts.Add(vert);
                }

                if (verts.Count < 2)
                {
                    continue;
                }

                surfaces.Add(new CrossSectionSurface(verts, surfaceModel.VInterval, surfaceModel.VOffset, material, colMat));
            }

            map[kvp.Key] = new CrossSection(surfaces);
        }

        return map;
    }

    private static TextureSlot LoadTextureSlot(
        TextureFactory factory,
        string xmlDir,
        CrossSectionTextureSlotModel slotModel,
        bool isLinear,
        IErrorCollector errorCollector,
        BuiltSplineNetworkResult result,
        string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(slotModel.Path))
        {
            return TextureSlot.Empty;
        }

        string fullPath = Path.GetFullPath(Path.Combine(xmlDir, slotModel.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(fullPath))
        {
            errorCollector.Report(new Error(ErrorLevel.Warning, $"断面テクスチャが見つかりません: {slotModel.Path}", sourceFile));
            return TextureSlot.Empty;
        }

        try
        {
            ID3D11ShaderResourceView srv = factory.CreateFromFile(fullPath, isLinear, errorCollector);
            result.TrackTexture(srv);
            TextureSampleMode sampleMode = slotModel.SampleMode switch
            {
                TextureSampleModeModel.None => TextureSampleMode.None,
                TextureSampleModeModel.Screen => TextureSampleMode.Screen,
                _ => TextureSampleMode.Texture,
            };
            return new TextureSlot(srv, sampleMode);
        }
        catch (Exception ex)
        {
            errorCollector.Report(new Error(ErrorLevel.Warning, $"断面テクスチャ '{slotModel.Path}' の読込に失敗しました: {ex.Message}", sourceFile));
            return TextureSlot.Empty;
        }
    }

    private static string? ResolveTexturePath(string xmlDir, string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(Path.Combine(xmlDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static void BuildSingleSpline(
        WorldProject project,
        EditorWorld world,
        SplinePlacementNode node,
        IGraphicsHost graphicsHost,
        IPhysicsHost physicsHost,
        IModelCollection models,
        IErrorCollector errorCollector,
        IReadOnlyDictionary<string, LaneLayout> runtimeLayouts,
        IReadOnlyDictionary<string, CrossSection> runtimeCrossSections,
        IReadOnlyDictionary<LaneTrafficGroup, Vector4> groupColors,
        HashSet<IModel> sharedBundleModels,
        Dictionary<string, List<SplineBase>> builtSplinesById,
        IReadOnlyDictionary<string, Junction> builtJunctionsById,
        BuiltSplineNetworkResult result)
    {
        SplineTemplateDocument? templateDoc = null;
        if (!string.IsNullOrEmpty(node.TemplateKey))
        {
            project.SplineTemplates.TryGetValue(node.TemplateKey, out templateDoc);
        }

        LaneLayout outletLayout = templateDoc is not null
            && !string.IsNullOrEmpty(templateDoc.OutletLayoutKey)
            && runtimeLayouts.TryGetValue(templateDoc.OutletLayoutKey, out LaneLayout? resolvedLayout)
            ? resolvedLayout
            : new LaneLayout();

        WorldPose startPose = WorldCoordinateHelper.ToWorldPose(new WorldPoseDegreesModel(node.Origin.Chunk, node.Origin.Pose));
        NetworkPort? sourcePort = null;

        if (node.Origin.Kind == SplineOriginKind.FromSplineOutlet && !string.IsNullOrEmpty(node.Origin.SourceNodeId))
        {
            if (builtSplinesById.TryGetValue(node.Origin.SourceNodeId, out List<SplineBase>? prevBuilt) && 0 < prevBuilt.Count)
            {
                SplineBase prevTail = prevBuilt[^1];
                NetworkPort prevOutlet = prevTail.Outlet;
                startPose = prevOutlet.Offset * prevTail.WorldPose;
                if (ReferenceEquals(prevOutlet.Layout, outletLayout) && !prevOutlet.IsConnected)
                {
                    sourcePort = prevOutlet;
                }
                else
                {
                    errorCollector.Report(new Error(
                        ErrorLevel.Warning,
                        $"スプライン '{node.Id}' の出口レイアウトが接続元 '{node.Origin.SourceNodeId}' と同一インスタンスでないため非接続状態でプレビューします。",
                        node.Id));
                }
            }
            else
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Warning,
                    $"スプライン '{node.Id}' の接続元 '{node.Origin.SourceNodeId}' が未構築です。",
                    node.Id));
            }
        }
        else if (node.Origin.Kind == SplineOriginKind.FromJunctionPort && !string.IsNullOrEmpty(node.Origin.SourceNodeId))
        {
            if (builtJunctionsById.TryGetValue(node.Origin.SourceNodeId, out Junction? prevJunction)
                && !string.IsNullOrEmpty(node.Origin.SourcePortKey)
                && prevJunction.Ports.TryGetValue(node.Origin.SourcePortKey, out NetworkPort? jPort))
            {
                Pose connPose = prevJunction.GetConnectionPose(jPort, Pose.CreateRotationY(float.Pi));
                startPose = prevJunction.WorldPose.ChangePose(connPose);
                if (ReferenceEquals(jPort.Layout, outletLayout) && !jPort.IsConnected)
                {
                    sourcePort = jPort;
                }
                else if (LayoutsEquivalent(jPort.Layout, outletLayout) && !jPort.IsConnected)
                {
                    outletLayout = jPort.Layout;
                    sourcePort = jPort;
                }
                else
                {
                    errorCollector.Report(new Error(
                        ErrorLevel.Warning,
                        $"スプライン '{node.Id}' をジャンクションポート '{node.Origin.SourceNodeId}.{node.Origin.SourcePortKey}' へ非接続状態でプレビューします。",
                        node.Id));
                }
            }
            else
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Warning,
                    $"スプライン '{node.Id}' の接続元ジャンクション '{node.Origin.SourceNodeId}.{node.Origin.SourcePortKey}' が未構築です。",
                    node.Id));
            }
        }

        SplineFactory factory = new(startPose, outletLayout, sourcePort)
        {
            DebugName = node.Key ?? node.Id,
        };

        Dictionary<string, double> anchors = new(StringComparer.Ordinal);
        foreach (SplineCurveSegment curveSeg in node.Curves)
        {
            float length = float.Max(0.01f, (float)curveSeg.Length);
            switch (curveSeg.Kind)
            {
                case SplineCurveSegmentKind.ByRadius:
                    if (1e-6 < double.Abs(curveSeg.Value))
                    {
                        factory.Curves.ByRadius((float)curveSeg.Value, length);
                    }
                    else
                    {
                        factory.Curves.Straight(length);
                    }
                    break;

                case SplineCurveSegmentKind.ByCurvature:
                    factory.Curves.ByCurvature((float)curveSeg.Value, length);
                    break;

                default:
                    factory.Curves.Straight(length);
                    break;
            }

            if (!string.IsNullOrEmpty(curveSeg.OutputAnchorId))
            {
                anchors[curveSeg.OutputAnchorId] = factory.Curves.S;
            }
        }

        foreach (SplineGradientSegment gradSeg in node.Gradients)
        {
            float length = float.Max(0.01f, (float)gradSeg.Length);
            switch (gradSeg.Kind)
            {
                case SplineGradientSegmentKind.Constant:
                    factory.Gradients.Constant(length);
                    break;
                case SplineGradientSegmentKind.TransitionByRadian:
                    factory.Gradients.TransitionBy((float)gradSeg.Delta, length);
                    break;
                case SplineGradientSegmentKind.TransitionByDegree:
                    factory.Gradients.TransitionBy((float)(gradSeg.Delta / 180.0 * double.Pi), length);
                    break;
                case SplineGradientSegmentKind.TransitionByPercent:
                    factory.Gradients.TransitionBy((float)double.Atan(gradSeg.Delta / 100.0), length);
                    break;
                case SplineGradientSegmentKind.TransitionByPermil:
                    factory.Gradients.TransitionBy((float)double.Atan(gradSeg.Delta / 1000.0), length);
                    break;
            }

            if (!string.IsNullOrEmpty(gradSeg.OutputAnchorId))
            {
                anchors[gradSeg.OutputAnchorId] = factory.Gradients.S;
            }
        }

        foreach (SplineCantSegment cantSeg in node.Cants)
        {
            float length = float.Max(0.01f, (float)cantSeg.Length);
            switch (cantSeg.Kind)
            {
                case SplineCantSegmentKind.Constant:
                    factory.Cants.Constant(length);
                    break;
                case SplineCantSegmentKind.TransitionToRadian:
                    factory.Cants.TransitionTo((float)cantSeg.Target, length);
                    break;
                case SplineCantSegmentKind.TransitionToDegree:
                    factory.Cants.TransitionTo((float)(cantSeg.Target / 180.0 * double.Pi), length);
                    break;
                case SplineCantSegmentKind.TransitionToPercent:
                    factory.Cants.TransitionTo((float)double.Atan(cantSeg.Target / 100.0), length);
                    break;
                case SplineCantSegmentKind.TransitionToHeight:
                    double gauge = 1e-6 < double.Abs(cantSeg.Gauge) ? cantSeg.Gauge : 1.0;
                    double ratio = double.Clamp(cantSeg.Target / gauge, -1.0, 1.0);
                    factory.Cants.TransitionTo((float)double.Asin(ratio), length);
                    break;
            }

            if (!string.IsNullOrEmpty(cantSeg.OutputAnchorId))
            {
                anchors[cantSeg.OutputAnchorId] = factory.Cants.S;
            }
        }

        if (node.ConnectBezier is not null)
        {
            NetworkPort? targetPort = ResolveRuntimePort(node.ConnectBezier.TargetPort, builtSplinesById, builtJunctionsById);
            if (targetPort is not null && outletLayout.CanConnectTo(targetPort.Layout) && !targetPort.IsConnected)
            {
                factory.InterpolateByBezier(targetPort, (float)node.ConnectBezier.HandleScale, out float bezierEndS);
                if (!string.IsNullOrEmpty(node.ConnectBezier.OutputAnchorId))
                {
                    anchors[node.ConnectBezier.OutputAnchorId] = bezierEndS;
                }
            }
            else if (targetPort is not null && !outletLayout.CanConnectTo(targetPort.Layout))
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Error,
                    $"スプライン '{node.Id}' の ConnectBezier 先ポート ({node.ConnectBezier.TargetPort.ElementId}) と LaneLayout が互換ではありません。",
                    node.Id));
            }
        }

        if (factory.Curves.Spans.Count == 0 && node.ConnectBezier is null)
        {
            return;
        }

        // Template extrusions and props
        if (templateDoc is not null)
        {
            foreach (SplineTemplateExtrusionRule extRule in templateDoc.Extrusions)
            {
                AddExtrusionToFactory(
                    factory,
                    extRule.Key,
                    extRule.CrossSectionKey,
                    extRule.OffsetX,
                    extRule.OffsetY,
                    extRule.BaseInterval,
                    extRule.MaxDeviation,
                    extRule.MaxAngleDeltaDeg,
                    extRule.VIntervalScale,
                    extRule.VOffset,
                    extRule.From,
                    float.MaxValue,
                    runtimeCrossSections);
            }

            foreach (SplineTemplatePropRule propRule in templateDoc.Props)
            {
                AddPropToFactory(
                    factory,
                    propRule.Key,
                    propRule.ModelKeys,
                    propRule.Pose,
                    propRule.From,
                    propRule.Span,
                    propRule.Interval,
                    int.MaxValue,
                    models,
                    physicsHost);
            }
        }

        // Instance extrusions and props
        foreach (CurveExtrusionPlacementRule extRule in node.Extrusions)
        {
            if (!extRule.From.TryResolve(anchors, out double resolvedFrom))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"スプライン '{node.Id}' の PutExtrusion.From アンカー '{extRule.From.AnchorId}' が未定義です。", node.Id));
                continue;
            }

            double resolvedTo = float.MaxValue;
            if (extRule.To.HasValue)
            {
                if (!extRule.To.Value.TryResolve(anchors, out double toVal))
                {
                    errorCollector.Report(new Error(ErrorLevel.Warning, $"スプライン '{node.Id}' の PutExtrusion.To アンカー '{extRule.To.Value.AnchorId}' が未定義です。", node.Id));
                    continue;
                }

                resolvedTo = toVal;
            }

            AddExtrusionToFactory(
                factory,
                extRule.Key,
                extRule.CrossSectionKey,
                extRule.OffsetX,
                extRule.OffsetY,
                extRule.BaseInterval,
                extRule.MaxDeviation,
                extRule.MaxAngleDeltaDeg,
                extRule.VIntervalScale,
                extRule.VOffset,
                resolvedFrom,
                resolvedTo,
                runtimeCrossSections);
        }

        foreach (SplineInstancePropRule propRule in node.Props)
        {
            if (!propRule.From.TryResolve(anchors, out double resolvedFrom))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"スプライン '{node.Id}' の PutProp.From アンカー '{propRule.From.AnchorId}' が未定義です。", node.Id));
                continue;
            }

            AddPropToFactory(
                factory,
                propRule.Key,
                propRule.ModelKeys,
                propRule.Pose,
                resolvedFrom,
                propRule.Span,
                propRule.Interval,
                propRule.Count,
                models,
                physicsHost);
        }

        // Apply SkipProp & SkipExtrusion rules matching SplineFactoryCommand logic
        ApplyPropSkips(factory, node.PropSkips, anchors);
        ApplyExtrusionSkips(factory, node.ExtrusionSkips, anchors);

        List<SplineBase> builtSegments;
        try
        {
            builtSegments = factory.Build(graphicsHost.Device, physicsHost);
        }
        catch (Exception ex)
        {
            errorCollector.Report(new Error(ErrorLevel.Error, $"スプライン '{node.Id}' の 3D 構築に失敗しました: {ex.Message}", node.Id));
            return;
        }

        if (builtSegments.Count == 0)
        {
            return;
        }

        builtSplinesById[node.Id] = builtSegments;

        float totalLength = 0f;
        HashSet<IModel> seenProceduralModels = new(ReferenceEqualityComparer.Instance);
        foreach (SplineBase seg in builtSegments)
        {
            Chunk chunk = world.Chunks.GetOrAddFor(seg);
            chunk.Network.Add(seg);
            result.TrackNetworkElement(seg);
            totalLength += seg.Length;

            foreach (ILanePath lanePath in seg.Paths)
            {
                lanePath.DebugColor = groupColors.TryGetValue(lanePath.AllowedTraffic, out Vector4 col) ? col : Vector4.One;
            }

            foreach (TransformedModel tm in seg.Models)
            {
                if (tm is not MergedStaticTransformedModel && !sharedBundleModels.Contains(tm.Resource.Model) && seenProceduralModels.Add(tm.Resource.Model))
                {
                    result.TrackProceduralResource(tm.Resource);
                }
            }
        }

        // Sample centerline points, inlet/outlet ports, and resolved anchor locations for overlays & picking
        SplineBase head = builtSegments[0];
        SplineBase tail = builtSegments[^1];
        ChunkIndex primaryChunk = head.WorldPose.Chunk;
        List<Vector3> centerlinePoints = SampleCenterlinePoints(builtSegments, primaryChunk);

        List<SplinePortWorldLocation> ports = new()
        {
            BuildPortLocation(new PortReferenceModel(node.Id, PortTargetKind.SplineInlet), head, head.Inlet),
            BuildPortLocation(new PortReferenceModel(node.Id, PortTargetKind.SplineOutlet), tail, tail.Outlet),
        };

        List<SplineAnchorWorldLocation> anchorLocations = BuildAnchorLocations(builtSegments, anchors);

        result.Entries[node.Id] = new SplineRenderEntry(
            node,
            builtSegments,
            totalLength,
            anchors,
            ports,
            anchorLocations,
            primaryChunk,
            centerlinePoints);
    }

    private static void BuildSingleJunction(
        WorldProject project,
        EditorWorld world,
        JunctionPlacementNode node,
        IGraphicsHost graphicsHost,
        IPhysicsHost physicsHost,
        IModelCollection models,
        IErrorCollector errorCollector,
        IReadOnlyDictionary<string, LaneLayout> runtimeLayouts,
        IReadOnlyDictionary<string, CrossSection> runtimeCrossSections,
        IReadOnlyDictionary<LaneTrafficGroup, Vector4> groupColors,
        HashSet<IModel> sharedBundleModels,
        IReadOnlyDictionary<string, List<SplineBase>> builtSplinesById,
        Dictionary<string, Junction> builtJunctionsById,
        BuiltSplineNetworkResult result)
    {
        if (!project.JunctionTemplates.TryGetValue(node.TemplateKey, out JunctionTemplateDocument? templateDoc))
        {
            errorCollector.Report(new Error(
                ErrorLevel.Error,
                $"ジャンクション '{node.Id}' のテンプレート '{node.TemplateKey}' が見つかりません。",
                node.Id));
            return;
        }

        Dictionary<string, PortDefinition> portDefMap = new(StringComparer.Ordinal);
        List<PortDefinition> portDefs = new();
        foreach (JunctionPortDefinition port in templateDoc.Ports)
        {
            if (string.IsNullOrWhiteSpace(port.Key) || portDefMap.ContainsKey(port.Key))
            {
                continue;
            }

            LaneLayout portLayout = !string.IsNullOrEmpty(port.LayoutKey)
                && runtimeLayouts.TryGetValue(port.LayoutKey, out LaneLayout? resolved)
                ? resolved
                : new LaneLayout();
            PortDefinition def = new(port.Key, portLayout, ToLocalPose(port.Pose));
            portDefMap[port.Key] = def;
            portDefs.Add(def);
        }

        foreach (JunctionRelayDefinition relay in templateDoc.Relays)
        {
            LaneLayout relayLayout = !string.IsNullOrEmpty(relay.LayoutKey)
                && runtimeLayouts.TryGetValue(relay.LayoutKey, out LaneLayout? resolved)
                ? resolved
                : new LaneLayout();
            Pose relayPose = ToLocalPose(relay.Pose);
            if (!string.IsNullOrWhiteSpace(relay.InputKey) && !portDefMap.ContainsKey(relay.InputKey))
            {
                PortDefinition inDef = new(relay.InputKey, relayLayout, relayPose);
                portDefMap[relay.InputKey] = inDef;
                portDefs.Add(inDef);
            }

            if (!string.IsNullOrWhiteSpace(relay.OutputKey) && !portDefMap.ContainsKey(relay.OutputKey))
            {
                PortDefinition outDef = new(relay.OutputKey, relayLayout.Opposition, Pose.CreateRotationY(float.Pi) * relayPose);
                portDefMap[relay.OutputKey] = outDef;
                portDefs.Add(outDef);
            }
        }

        Junction junction;
        if (node.Origin.Kind == JunctionOriginKind.FromSplineOutlet
            && !string.IsNullOrEmpty(node.Origin.SourceNodeId)
            && builtSplinesById.TryGetValue(node.Origin.SourceNodeId, out List<SplineBase>? prevSplines)
            && 0 < prevSplines.Count)
        {
            SplineBase prevTail = prevSplines[^1];
            string targetPortKey = node.Origin.TargetPortKey ?? string.Empty;
            if (prevTail is Spline concreteSpline
                && portDefMap.TryGetValue(targetPortKey, out PortDefinition? targetPortDef)
                && !concreteSpline.Outlet.IsConnected
                && concreteSpline.Outlet.Layout.CanConnectTo(targetPortDef.Layout))
            {
                junction = concreteSpline.ConnectNew(targetPortDef, wp => new Junction(wp, portDefs));
            }
            else
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Warning,
                    $"ジャンクション '{node.Id}' とスプライン '{node.Origin.SourceNodeId}' のポート '{targetPortKey}' を接続できないため非接続で構築します。",
                    node.Id));
                WorldPose fallbackPose = prevTail.Outlet.Offset * prevTail.WorldPose;
                junction = new Junction(fallbackPose, portDefs);
            }
        }
        else if (node.Origin.Kind == JunctionOriginKind.FromJunctionPort
            && !string.IsNullOrEmpty(node.Origin.SourceNodeId)
            && builtJunctionsById.TryGetValue(node.Origin.SourceNodeId, out Junction? prevJunction)
            && !string.IsNullOrEmpty(node.Origin.SourcePortKey)
            && prevJunction.Ports.TryGetValue(node.Origin.SourcePortKey, out NetworkPort? srcPort))
        {
            string targetPortKey = node.Origin.TargetPortKey ?? string.Empty;
            if (portDefMap.TryGetValue(targetPortKey, out PortDefinition? targetPortDef)
                && !srcPort.IsConnected
                && srcPort.Layout.CanConnectTo(targetPortDef.Layout))
            {
                junction = prevJunction.ConnectNew(srcPort, targetPortDef, wp => new Junction(wp, portDefs));
            }
            else
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Warning,
                    $"ジャンクション '{node.Id}' と '{node.Origin.SourceNodeId}.{node.Origin.SourcePortKey}' のポートを接続できないため非接続で構築します。",
                    node.Id));
                WorldPose fallbackPose = srcPort.Offset * prevJunction.WorldPose;
                junction = new Junction(fallbackPose, portDefs);
            }
        }
        else
        {
            WorldPose worldPose = WorldCoordinateHelper.ToWorldPose(node.Origin.Chunk, node.Origin.Pose);
            junction = new Junction(worldPose, portDefs);
        }

        junction.DebugName = node.Key ?? node.Id;
        builtJunctionsById[node.Id] = junction;

        foreach (JunctionRelayDefinition relay in templateDoc.Relays)
        {
            if (junction.Ports.TryGetValue(relay.InputKey, out NetworkPort? inPort)
                && junction.Ports.TryGetValue(relay.OutputKey, out NetworkPort? outPort)
                && !inPort.IsConnected
                && !outPort.IsConnected)
            {
                inPort.ConnectTo(outPort);
            }
        }

        Dictionary<string, JunctionPathOverrideModel> pathOverrides = new(StringComparer.Ordinal);
        foreach (JunctionPathOverrideModel pov in node.PathOverrides)
        {
            pathOverrides[pov.PathKey] = pov;
        }

        Dictionary<string, JunctionCurveOverrideModel> curveOverrides = new(StringComparer.Ordinal);
        foreach (JunctionCurveOverrideModel cov in node.CurveOverrides)
        {
            curveOverrides[cov.CurveKey] = cov;
        }

        List<TransformedModelTemplate> allPropTemplates = new();
        foreach (JunctionStaticPropPlacement staticProp in templateDoc.Props)
        {
            AppendStaticPropTemplate(staticProp, models, physicsHost, allPropTemplates);
        }

        foreach (JunctionStaticPropPlacement staticProp in node.Props)
        {
            AppendStaticPropTemplate(staticProp, models, physicsHost, allPropTemplates);
        }

        List<(IPoseCurve Curve, List<SplineExtrusion> Extrusions)> pendingExtrusions = new();
        List<IReadOnlyList<Vector3>> primaryPathPolylines = new();
        List<IReadOnlyList<Vector3>> primaryCurvePolylines = new();
        Chunk primaryChunkObj = world.Chunks.GetOrAddFor(junction);
        ChunkIndex primaryChunk = primaryChunkObj.Index;

        foreach (JunctionPathTemplateModel pathModel in templateDoc.Paths)
        {
            if (!portDefMap.TryGetValue(pathModel.FromPortKey, out PortDefinition? fromDef)
                || !portDefMap.TryGetValue(pathModel.ToPortKey, out PortDefinition? toDef)
                || !junction.Ports.TryGetValue(pathModel.FromPortKey, out NetworkPort? fromPort)
                || !junction.Ports.TryGetValue(pathModel.ToPortKey, out NetworkPort? toPort))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{node.Id}' パス '{pathModel.Key}' の接続ポートが見つかりません。", node.Id));
                continue;
            }

            if (pathModel.FromPinIndex < 0
                || fromPort.Pins.Count <= pathModel.FromPinIndex
                || pathModel.ToPinIndex < 0
                || toPort.Pins.Count <= pathModel.ToPinIndex)
            {
                errorCollector.Report(new Error(ErrorLevel.Error, $"ジャンクション '{node.Id}' パス '{pathModel.Key}' のピン番号が範囲外です。", node.Id));
                continue;
            }

            LanePin fromPin = fromPort.Pins[pathModel.FromPinIndex];
            LanePin toPin = toPort.Pins[pathModel.ToPinIndex];
            if (!fromPin.Definition.Directions.IsOppositeOf(toPin.Definition.Directions))
            {
                errorCollector.Report(new Error(ErrorLevel.Error, $"ジャンクション '{node.Id}' パス '{pathModel.Key}' の始点/終点ピンの進行方向が一致しません。", node.Id));
                continue;
            }

            Pose startPose = Pose.CreateRotationY(float.Pi) * fromDef.GetPinLocalPose(pathModel.FromPinIndex);
            Pose endPose = toDef.GetPinLocalPose(pathModel.ToPinIndex);

            Dictionary<string, double> pathAnchors = new(StringComparer.Ordinal);
            List<IPoseCurve> pathCurves = BuildSegmentPoseCurves(
                startPose,
                endPose,
                pathModel.Segments,
                finalizeWithBezierToEnd: true,
                pathAnchors);

            if (pathCurves.Count == 0)
            {
                continue;
            }

            CompositePoseCurve pathCompositeCurve = new(pathCurves);
            List<CompositeLanePath.WidthPoint> widthPoints = BuildPathWidthPoints(pathModel.WidthSegments, pathAnchors, errorCollector, node.Id);

            try
            {
                CompositeLanePath lanePath = new(pathModel.Key, fromPin, toPin, pathCurves, widthPoints);
                lanePath.DebugColor = groupColors.TryGetValue(lanePath.AllowedTraffic, out Vector4 col) ? col : Vector4.One;
                junction.Wire(lanePath);
            }
            catch (Exception ex)
            {
                errorCollector.Report(new Error(ErrorLevel.Error, $"ジャンクション '{node.Id}' パス '{pathModel.Key}' の接続に失敗しました: {ex.Message}", node.Id));
                continue;
            }

            primaryPathPolylines.Add(SamplePoseCurvePoints(pathCompositeCurve, junction.WorldPose, primaryChunk));

            List<SplineProp> pathProps = new();
            List<SplineExtrusion> pathExtrusions = new();
            CollectCurveRules(pathModel.Props, pathModel.Extrusions, pathAnchors, runtimeCrossSections, models, physicsHost, errorCollector, node.Id, pathProps, pathExtrusions);
            if (pathOverrides.TryGetValue(pathModel.Key, out JunctionPathOverrideModel? pov))
            {
                CollectCurveRules(pov.Props, pov.Extrusions, pathAnchors, runtimeCrossSections, models, physicsHost, errorCollector, node.Id, pathProps, pathExtrusions);
            }

            foreach (SplineProp prop in pathProps)
            {
                allPropTemplates.AddRange(prop.Build(physicsHost, pathCompositeCurve));
            }

            if (0 < pathExtrusions.Count)
            {
                pendingExtrusions.Add((pathCompositeCurve, pathExtrusions));
            }
        }

        foreach (JunctionCurveTemplateModel curveModel in templateDoc.Curves)
        {
            if (!TryResolveCurveStartPose(curveModel.Origin, portDefMap, out Pose curveStartPose))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{node.Id}' 補助曲線 '{curveModel.Key}' の起点ポートが未定義です。", node.Id));
                continue;
            }

            Dictionary<string, double> curveAnchors = new(StringComparer.Ordinal);
            List<IPoseCurve> auxCurves = BuildSegmentPoseCurves(
                curveStartPose,
                null,
                curveModel.Segments,
                finalizeWithBezierToEnd: false,
                curveAnchors);

            IPoseCurve compositeCurve = auxCurves.Count == 0 ? IPoseCurve.Empty : new CompositePoseCurve(auxCurves);
            if (0 < auxCurves.Count)
            {
                primaryCurvePolylines.Add(SamplePoseCurvePoints(compositeCurve, junction.WorldPose, primaryChunk));
            }

            List<SplineProp> curveProps = new();
            List<SplineExtrusion> curveExtrusions = new();
            CollectCurveRules(curveModel.Props, curveModel.Extrusions, curveAnchors, runtimeCrossSections, models, physicsHost, errorCollector, node.Id, curveProps, curveExtrusions);
            if (curveOverrides.TryGetValue(curveModel.Key, out JunctionCurveOverrideModel? cov))
            {
                CollectCurveRules(cov.Props, cov.Extrusions, curveAnchors, runtimeCrossSections, models, physicsHost, errorCollector, node.Id, curveProps, curveExtrusions);
            }

            foreach (SplineProp prop in curveProps)
            {
                allPropTemplates.AddRange(prop.Build(physicsHost, compositeCurve));
            }

            if (0 < curveExtrusions.Count && 1e-4f < compositeCurve.Length)
            {
                pendingExtrusions.Add((compositeCurve, curveExtrusions));
            }
        }

        try
        {
            if (0 < allPropTemplates.Count)
            {
                junction.AddProps(graphicsHost.Device, physicsHost, allPropTemplates);
            }

            foreach ((IPoseCurve extCurve, List<SplineExtrusion> extrusions) in pendingExtrusions)
            {
                junction.PutExtrusions(graphicsHost.Device, physicsHost, extCurve, extrusions);
            }
        }
        catch (Exception ex)
        {
            errorCollector.Report(new Error(ErrorLevel.Error, $"ジャンクション '{node.Id}' の 3D メッシュ構築に失敗しました: {ex.Message}", node.Id));
        }

        primaryChunkObj.Network.Add(junction);
        result.TrackNetworkElement(junction);

        HashSet<IModel> seenProceduralModels = new(ReferenceEqualityComparer.Instance);
        Vector3 boundsMin = new(float.MaxValue);
        Vector3 boundsMax = new(float.MinValue);
        bool hasAnyPoint = false;

        foreach (TransformedModel tm in junction.Models)
        {
            if (tm is CollidableTransformedModel collidable)
            {
                collidable.SetFromCamera(primaryChunk - world.Camera.WorldPose.Chunk);
            }

            if (tm is not MergedStaticTransformedModel
                && !sharedBundleModels.Contains(tm.Resource.Model)
                && seenProceduralModels.Add(tm.Resource.Model))
            {
                result.TrackProceduralResource(tm.Resource);
            }
        }

        foreach (IReadOnlyList<Vector3> poly in primaryPathPolylines)
        {
            AccumulateRelativeBounds(poly, junction.WorldPose.Pose, ref boundsMin, ref boundsMax, ref hasAnyPoint);
        }

        foreach (IReadOnlyList<Vector3> poly in primaryCurvePolylines)
        {
            AccumulateRelativeBounds(poly, junction.WorldPose.Pose, ref boundsMin, ref boundsMax, ref hasAnyPoint);
        }

        BoundingBox localBounds = hasAnyPoint
            ? new BoundingBox(boundsMin - new Vector3(1f, 0.25f, 1f), boundsMax + new Vector3(1f, 1.5f, 1f))
            : new BoundingBox(new Vector3(-3f, 0f, -3f), new Vector3(3f, 2f, 3f));

        List<SplinePortWorldLocation> ports = BuildJunctionPortLocations(node.Id, templateDoc, junction);

        result.JunctionEntries[node.Id] = new JunctionRenderEntry(
            node,
            junction,
            primaryChunk,
            junction.WorldPose.Pose.Position,
            localBounds,
            ports,
            primaryPathPolylines,
            primaryCurvePolylines);
    }

    private static void ConnectExplicitPorts(
        WorldProject project,
        IReadOnlyDictionary<string, List<SplineBase>> builtSplinesById,
        IReadOnlyDictionary<string, Junction> builtJunctionsById,
        IErrorCollector errorCollector)
    {
        foreach (ExplicitPortConnection conn in project.Placement.Connections)
        {
            NetworkPort? fromPort = ResolveRuntimePort(conn.From, builtSplinesById, builtJunctionsById);
            NetworkPort? toPort = ResolveRuntimePort(conn.To, builtSplinesById, builtJunctionsById);
            if (fromPort is null || toPort is null)
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Warning,
                    $"Network.Connect({conn.From.ElementId}, {conn.To.ElementId}) の対象ポートが見つかりません。",
                    conn.From.ElementId));
                continue;
            }

            if (fromPort.IsConnected || toPort.IsConnected)
            {
                continue;
            }

            if (!fromPort.Layout.CanConnectTo(toPort.Layout))
            {
                errorCollector.Report(new Error(
                    ErrorLevel.Error,
                    $"Network.Connect({conn.From.ElementId}, {conn.To.ElementId}) の LaneLayout が互換ではありません。",
                    conn.From.ElementId));
                continue;
            }

            fromPort.ConnectTo(toPort);
        }
    }

    private static void RefreshPortConnectionStates(BuiltSplineNetworkResult result)
    {
        foreach (SplineRenderEntry entry in result.Entries.Values)
        {
            if (entry.BuiltSegments.Count == 0)
            {
                continue;
            }

            SplineBase head = entry.BuiltSegments[0];
            SplineBase tail = entry.BuiltSegments[^1];
            entry.Ports = new SplinePortWorldLocation[]
            {
                BuildPortLocation(new PortReferenceModel(entry.Node.Id, PortTargetKind.SplineInlet), head, head.Inlet),
                BuildPortLocation(new PortReferenceModel(entry.Node.Id, PortTargetKind.SplineOutlet), tail, tail.Outlet),
            };
        }

        foreach (JunctionRenderEntry entry in result.JunctionEntries.Values)
        {
            List<SplinePortWorldLocation> updated = new(entry.Ports.Count);
            foreach (SplinePortWorldLocation port in entry.Ports)
            {
                bool connected = !string.IsNullOrEmpty(port.PortRef.PortKey)
                    && entry.BuiltJunction.Ports.TryGetValue(port.PortRef.PortKey, out NetworkPort? runtimePort)
                    && runtimePort.IsConnected;
                updated.Add(port with { IsConnected = connected });
            }

            entry.Ports = updated;
        }
    }

    private static Pose ToLocalPose(PoseDegreesModel pose)
    {
        return SixDoF.FromDegrees(
            (float)pose.X,
            (float)pose.Y,
            (float)pose.Z,
            (float)pose.RotationX,
            (float)pose.RotationY,
            (float)pose.RotationZ).ToPose();
    }

    private static bool LayoutsEquivalent(LaneLayout a, LaneLayout b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Lanes.Count != b.Lanes.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Lanes.Count; i++)
        {
            Lane la = a.Lanes[i];
            Lane lb = b.Lanes[i];
            if (la.Directions != lb.Directions
                || 1e-4f < float.Abs(la.Position.X - lb.Position.X)
                || 1e-4f < float.Abs(la.Position.Y - lb.Position.Y)
                || 1e-4f < float.Abs(la.Width.Left - lb.Width.Left)
                || 1e-4f < float.Abs(la.Width.Right - lb.Width.Right))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendStaticPropTemplate(
        JunctionStaticPropPlacement prop,
        IModelCollection models,
        IPhysicsHost physicsHost,
        List<TransformedModelTemplate> destination)
    {
        ModelResourceSet modelRes = models.TryGetModel(prop.ModelKey, out ModelResourceSet found)
            ? found
            : ModelResourceSet.Empty();
        destination.Add(StaticTransformedModelTemplate.CreateStaticOrNonCollision(physicsHost, modelRes, ToLocalPose(prop.Pose)));
    }

    private static bool TryResolveCurveStartPose(
        JunctionCurveOriginModel origin,
        IReadOnlyDictionary<string, PortDefinition> portDefs,
        out Pose startPose)
    {
        if (origin.Kind == JunctionCurveOriginKind.PortPinRelative)
        {
            if (string.IsNullOrEmpty(origin.PortKey)
                || !portDefs.TryGetValue(origin.PortKey, out PortDefinition? port)
                || origin.PinIndex < 0
                || port.Layout.Lanes.Count <= origin.PinIndex)
            {
                startPose = Pose.Identity;
                return false;
            }

            Pose offset = origin.HasPortPinOffset ? ToLocalPose(origin.Pose) : Pose.Identity;
            startPose = offset * Pose.CreateRotationY(float.Pi) * port.GetPinLocalPose(origin.PinIndex);
            return true;
        }

        startPose = ToLocalPose(origin.Pose);
        return true;
    }

    private static List<IPoseCurve> BuildSegmentPoseCurves(
        Pose startPose,
        Pose? endPose,
        IReadOnlyList<JunctionCurveSegment> segments,
        bool finalizeWithBezierToEnd,
        Dictionary<string, double> anchors)
    {
        List<IPoseCurve> curves = new();
        Pose current = startPose;
        double s = 0.0;
        bool finalized = false;

        foreach (JunctionCurveSegment seg in segments)
        {
            bool isToEnd = seg.Kind == JunctionCurveSegmentKind.StraightToEnd
                || seg.Kind == JunctionCurveSegmentKind.BezierToEnd;
            Pose target = isToEnd && endPose.HasValue ? endPose.Value : ToLocalPose(seg.TargetPose);
            bool isBezier = seg.Kind == JunctionCurveSegmentKind.BezierTo
                || seg.Kind == JunctionCurveSegmentKind.BezierToEnd;

            float dist = Vector3.Distance(current.Position, target.Position);
            if (isBezier)
            {
                if (1e-4f < dist)
                {
                    BezierPoseCurve bezier = new(current, target, (float)seg.HandleScale);
                    curves.Add(bezier);
                    s += bezier.Length;
                }
            }
            else if (1e-4f < dist)
            {
                LinearPoseCurve linear = new(current, target);
                curves.Add(linear);
                s += linear.Length;
            }

            current = target;
            if (!string.IsNullOrEmpty(seg.OutputAnchorId))
            {
                anchors[seg.OutputAnchorId] = s;
            }

            if (isToEnd)
            {
                finalized = true;
                break;
            }
        }

        if (finalizeWithBezierToEnd && !finalized && endPose.HasValue)
        {
            float dist = Vector3.Distance(current.Position, endPose.Value.Position);
            if (1e-4f < dist)
            {
                curves.Add(new BezierPoseCurve(current, endPose.Value, 0.5f));
            }
        }

        return curves;
    }

    private static List<CompositeLanePath.WidthPoint> BuildPathWidthPoints(
        IReadOnlyList<JunctionWidthSegment> widthSegments,
        Dictionary<string, double> anchors,
        IErrorCollector errorCollector,
        string elementId)
    {
        List<CompositeLanePath.WidthPoint> points = new();
        double s = 0.0;
        LaneWidth lastWidth = new(2f, 2f);

        foreach (JunctionWidthSegment seg in widthSegments)
        {
            if (!seg.Length.TryResolve(anchors, out double stepLen))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{elementId}' の Width アンカー '{seg.Length.AnchorId}' が未定義です。", elementId));
                stepLen = seg.Length.Offset;
            }

            s += double.Max(0.0, stepLen);
            LaneWidth nextWidth = seg.Kind == JunctionWidthSegmentKind.Constant
                ? lastWidth
                : new LaneWidth((float)seg.LeftWidth, (float)seg.RightWidth);
            lastWidth = nextWidth;
            points.Add(new CompositeLanePath.WidthPoint((float)s, nextWidth));

            if (!string.IsNullOrEmpty(seg.OutputAnchorId))
            {
                anchors[seg.OutputAnchorId] = s;
            }
        }

        return points;
    }

    private static void CollectCurveRules(
        IReadOnlyList<CurvePropPlacementRule> propRules,
        IReadOnlyList<CurveExtrusionPlacementRule> extrusionRules,
        IReadOnlyDictionary<string, double> anchors,
        IReadOnlyDictionary<string, CrossSection> runtimeCrossSections,
        IModelCollection models,
        IPhysicsHost physicsHost,
        IErrorCollector errorCollector,
        string nodeId,
        List<SplineProp> propsOut,
        List<SplineExtrusion> extrusionsOut)
    {
        foreach (CurvePropPlacementRule propRule in propRules)
        {
            if (propRule.ModelKeys.Count == 0)
            {
                continue;
            }

            if (!propRule.From.TryResolve(anchors, out double resolvedFrom))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{nodeId}' の PutProp.From アンカー '{propRule.From.AnchorId}' が未定義です。", nodeId));
                continue;
            }

            Pose pose = ToLocalPose(propRule.Pose);
            TransformedModelTemplate[] templates = new TransformedModelTemplate[propRule.ModelKeys.Count];
            for (int i = 0; i < templates.Length; i++)
            {
                string mk = propRule.ModelKeys[i];
                ModelResourceSet modelRes = models.TryGetModel(mk, out ModelResourceSet found) ? found : ModelResourceSet.Empty();
                templates[i] = StaticTransformedModelTemplate.CreateStaticOrNonCollision(physicsHost, modelRes, pose);
            }

            propsOut.Add(new SplineProp(templates, (float)resolvedFrom, (float)propRule.Span, (float)propRule.Interval, propRule.Count));
        }

        foreach (CurveExtrusionPlacementRule extRule in extrusionRules)
        {
            if (!extRule.From.TryResolve(anchors, out double resolvedFrom))
            {
                errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{nodeId}' の PutExtrusion.From アンカー '{extRule.From.AnchorId}' が未定義です。", nodeId));
                continue;
            }

            double resolvedTo = float.MaxValue;
            if (extRule.To.HasValue)
            {
                if (!extRule.To.Value.TryResolve(anchors, out double toVal))
                {
                    errorCollector.Report(new Error(ErrorLevel.Warning, $"ジャンクション '{nodeId}' の PutExtrusion.To アンカー '{extRule.To.Value.AnchorId}' が未定義です。", nodeId));
                    continue;
                }

                resolvedTo = toVal;
            }

            if (!runtimeCrossSections.TryGetValue(extRule.CrossSectionKey, out CrossSection? cs))
            {
                cs = CrossSection.Empty;
            }

            float clampedInterval = float.Max(0.25f, (float)extRule.BaseInterval);
            float clampedDeviation = float.Max(0.001f, (float)extRule.MaxDeviation);
            float angleRad = (float)(float.Max(0.1f, (float)extRule.MaxAngleDeltaDeg) * (float.Pi / 180f));
            float clampedTo = (float)double.Min(resolvedTo, float.MaxValue);
            AdaptiveCurveSampler sampler = new(clampedInterval, clampedDeviation, angleRad, (float)resolvedFrom, clampedTo);
            SplineExtrusion extrusion = new(cs, sampler, new Vector2((float)extRule.OffsetX, (float)extRule.OffsetY))
            {
                VIntervalScale = (float)extRule.VIntervalScale,
                VOffset = (float)extRule.VOffset - sampler.FromS,
                Key = extRule.Key,
            };
            extrusionsOut.Add(extrusion);
        }
    }

    private static List<Vector3> SamplePoseCurvePoints(
        IPoseCurve curve,
        in WorldPose junctionWorldPose,
        ChunkIndex referenceChunk)
    {
        List<Vector3> points = new();
        float segLen = curve.Length;
        if (segLen <= 1e-4f)
        {
            return points;
        }

        ICurveSampler sampler = new AdaptiveCurveSampler(4f, 0.02f, float.DegreesToRadians(3f), 0f, segLen);
        IReadOnlyList<float> sValues = sampler.Sample(curve, 0f);
        if (sValues.Count < 2)
        {
            ReadOnlySpan<float> fallback = [0f, segLen];
            for (int i = 0; i < fallback.Length; i++)
            {
                WorldPose wp = curve.GetPose(fallback[i]) * junctionWorldPose;
                points.Add(WorldCoordinateHelper.ToChunkRelativePosition(referenceChunk, wp));
            }
        }
        else
        {
            for (int i = 0; i < sValues.Count; i++)
            {
                WorldPose wp = curve.GetPose(sValues[i]) * junctionWorldPose;
                Vector3 rel = WorldCoordinateHelper.ToChunkRelativePosition(referenceChunk, wp);
                if (points.Count == 0 || 1e-4f < Vector3.DistanceSquared(points[^1], rel))
                {
                    points.Add(rel);
                }
            }
        }

        return points;
    }

    private static void AccumulateRelativeBounds(
        IReadOnlyList<Vector3> primaryChunkPoints,
        in Pose junctionPose,
        ref Vector3 min,
        ref Vector3 max,
        ref bool hasAnyPoint)
    {
        Pose invPose = Pose.Inverse(junctionPose);
        for (int i = 0; i < primaryChunkPoints.Count; i++)
        {
            Vector3 local = (new Pose(primaryChunkPoints[i]) * invPose).Position;
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
            hasAnyPoint = true;
        }
    }

    private static List<SplinePortWorldLocation> BuildJunctionPortLocations(
        string junctionId,
        JunctionTemplateDocument template,
        Junction junction)
    {
        List<SplinePortWorldLocation> locations = new();
        foreach (JunctionPortDefinition portDef in template.Ports)
        {
            if (junction.Ports.TryGetValue(portDef.Key, out NetworkPort? runtimePort))
            {
                WorldPose portWorldPose = runtimePort.Offset * junction.WorldPose;
                locations.Add(new SplinePortWorldLocation(
                    new PortReferenceModel(junctionId, PortTargetKind.JunctionPort, portDef.Key),
                    portWorldPose.Chunk,
                    portWorldPose.Pose.Position,
                    runtimePort.IsConnected));
            }
        }

        return locations;
    }

    private static SplinePortWorldLocation BuildPortLocation(PortReferenceModel portRef, SplineBase owner, NetworkPort port)
    {
        WorldPose portWorldPose = port.Offset * owner.WorldPose;
        return new SplinePortWorldLocation(portRef, portWorldPose.Chunk, portWorldPose.Pose.Position, port.IsConnected);
    }

    private static List<Vector3> SampleCenterlinePoints(IReadOnlyList<SplineBase> segments, ChunkIndex referenceChunk)
    {
        List<Vector3> points = new();
        foreach (SplineBase seg in segments)
        {
            float segLen = seg.Length;
            ICurveSampler sampler = new AdaptiveCurveSampler(4f, 0.02f, float.DegreesToRadians(3f), 0f, segLen);
            IReadOnlyList<float> sValues = sampler.Sample(seg, 0f);
            if (sValues.Count < 2)
            {
                float stepEnd = float.Max(0f, segLen);
                ReadOnlySpan<float> fallback = stepEnd <= 0f ? [0f] : [0f, stepEnd];
                for (int i = 0; i < fallback.Length; i++)
                {
                    AddSampledPoint(seg, fallback[i], referenceChunk, points);
                }
            }
            else
            {
                for (int i = 0; i < sValues.Count; i++)
                {
                    AddSampledPoint(seg, sValues[i], referenceChunk, points);
                }
            }
        }

        return points;
    }

    private static void AddSampledPoint(SplineBase seg, float s, ChunkIndex referenceChunk, List<Vector3> points)
    {
        Pose localPose = seg.GetPose(s);
        WorldPose wp = localPose * seg.WorldPose;
        Vector3 rel = WorldCoordinateHelper.ToChunkRelativePosition(referenceChunk, wp);
        if (points.Count == 0 || 1e-4f < Vector3.DistanceSquared(points[^1], rel))
        {
            points.Add(rel);
        }
    }

    private static List<SplineAnchorWorldLocation> BuildAnchorLocations(
        IReadOnlyList<SplineBase> segments,
        IReadOnlyDictionary<string, double> anchors)
    {
        List<SplineAnchorWorldLocation> result = new();
        foreach (KeyValuePair<string, double> kvp in anchors)
        {
            double remaining = kvp.Value;
            SplineBase targetSeg = segments[^1];
            float localS = targetSeg.Length;

            foreach (SplineBase seg in segments)
            {
                if (remaining <= seg.Length + 1e-3)
                {
                    targetSeg = seg;
                    localS = float.Clamp((float)remaining, 0f, seg.Length);
                    break;
                }

                remaining -= seg.Length;
            }

            Pose poseOnSeg = targetSeg.GetPose(localS);
            WorldPose wp = poseOnSeg * targetSeg.WorldPose;
            result.Add(new SplineAnchorWorldLocation(kvp.Key, kvp.Value, wp.Chunk, wp.Pose.Position));
        }

        return result;
    }

    private static NetworkPort? ResolveRuntimePort(
        PortReferenceModel targetPort,
        IReadOnlyDictionary<string, List<SplineBase>> builtSplinesById,
        IReadOnlyDictionary<string, Junction> builtJunctionsById)
    {
        if (targetPort.Kind == PortTargetKind.JunctionPort)
        {
            if (builtJunctionsById.TryGetValue(targetPort.ElementId, out Junction? junc)
                && !string.IsNullOrEmpty(targetPort.PortKey)
                && junc.Ports.TryGetValue(targetPort.PortKey, out NetworkPort? jPort))
            {
                return jPort;
            }

            return null;
        }

        if (!builtSplinesById.TryGetValue(targetPort.ElementId, out List<SplineBase>? built) || built.Count == 0)
        {
            return null;
        }

        return targetPort.Kind switch
        {
            PortTargetKind.SplineInlet => built[0].Inlet,
            PortTargetKind.SplineOutlet => built[^1].Outlet,
            _ => null,
        };
    }

    private static void AddExtrusionToFactory(
        SplineFactory factory,
        string? key,
        string crossSectionKey,
        double offsetX,
        double offsetY,
        double baseInterval,
        double maxDeviation,
        double maxAngleDeltaDeg,
        double vIntervalScale,
        double vOffset,
        double fromS,
        double toS,
        IReadOnlyDictionary<string, CrossSection> runtimeCrossSections)
    {
        if (!runtimeCrossSections.TryGetValue(crossSectionKey, out CrossSection? cs))
        {
            cs = CrossSection.Empty;
        }

        float clampedInterval = float.Max(0.25f, (float)baseInterval);
        float clampedDeviation = float.Max(0.001f, (float)maxDeviation);
        float angleRad = (float)(float.Max(0.1f, (float)maxAngleDeltaDeg) * (float.Pi / 180f));
        float clampedTo = (float)double.Min(toS, float.MaxValue);
        AdaptiveCurveSampler sampler = new(clampedInterval, clampedDeviation, angleRad, (float)fromS, clampedTo);
        SplineExtrusion extrusion = new(cs, sampler, new Vector2((float)offsetX, (float)offsetY))
        {
            VIntervalScale = (float)vIntervalScale,
            VOffset = (float)vOffset - sampler.FromS,
            Key = key,
        };
        factory.Extrusions.Add(extrusion);
    }

    private static void AddPropToFactory(
        SplineFactory factory,
        string? key,
        IReadOnlyList<string> modelKeys,
        PoseDegreesModel localPoseModel,
        double fromS,
        double span,
        double interval,
        int count,
        IModelCollection models,
        IPhysicsHost physicsHost)
    {
        if (modelKeys.Count == 0)
        {
            return;
        }

        SixDoF six = SixDoF.FromDegrees(
            (float)localPoseModel.X,
            (float)localPoseModel.Y,
            (float)localPoseModel.Z,
            (float)localPoseModel.RotationX,
            (float)localPoseModel.RotationY,
            (float)localPoseModel.RotationZ);
        Pose pose = six.ToPose();

        List<TransformedModelTemplate> templates = new(modelKeys.Count);
        foreach (string mk in modelKeys)
        {
            ModelResourceSet modelRes = models.TryGetModel(mk, out ModelResourceSet found) ? found : ModelResourceSet.Empty();
            templates.Add(StaticTransformedModelTemplate.CreateStaticOrNonCollision(physicsHost, modelRes, pose));
        }

        SplineProp prop = new(templates, (float)fromS, (float)span, (float)interval, count)
        {
            Key = key,
        };
        factory.Props.Add(prop);
    }

    private static void ApplyPropSkips(
        SplineFactory factory,
        IReadOnlyList<SplineSkipRule> skips,
        IReadOnlyDictionary<string, double> anchors)
    {
        foreach (SplineSkipRule skip in skips)
        {
            double rawFrom = 0.0;
            double rawTo = float.MaxValue;
            if (skip.From.HasValue && !skip.From.Value.TryResolve(anchors, out rawFrom))
            {
                continue;
            }

            if (skip.To.HasValue && !skip.To.Value.TryResolve(anchors, out rawTo))
            {
                continue;
            }

            if (rawTo <= rawFrom)
            {
                continue;
            }

            float skipFrom = (float)rawFrom;
            float skipTo = (float)double.Min(rawTo, float.MaxValue);

            for (int i = factory.Props.Count - 1; 0 <= i; i--)
            {
                SplineProp prop = factory.Props[i];
                if (!string.Equals(prop.Key, skip.Key, StringComparison.Ordinal))
                {
                    continue;
                }

                if (prop.Count <= 0 || skipTo < prop.From)
                {
                    continue;
                }

                if (prop.Count < int.MaxValue && prop.From + prop.Interval * (prop.Count - 1) < skipFrom)
                {
                    continue;
                }

                factory.Props.RemoveAt(i);
                int insertIndex = i;

                int beforeCount;
                if (skipFrom <= prop.From)
                {
                    beforeCount = 0;
                }
                else
                {
                    float rawBefore = float.Ceiling((skipFrom - prop.From) / prop.Interval);
                    beforeCount = prop.Count <= rawBefore ? prop.Count : (int)rawBefore;
                }

                if (0 < beforeCount)
                {
                    SplineProp beforeProp = new(prop.Models, prop.From, prop.Span, prop.Interval, beforeCount)
                    {
                        Key = prop.Key,
                    };
                    factory.Props.Insert(insertIndex++, beforeProp);
                }

                float rawResume = skipTo < prop.From ? 0f : float.Floor((skipTo - prop.From) / prop.Interval) + 1f;
                if (rawResume < prop.Count)
                {
                    int resumeIdx = (int)rawResume;
                    int afterCount = prop.Count == int.MaxValue ? int.MaxValue : prop.Count - resumeIdx;
                    float afterFrom = prop.From + prop.Interval * resumeIdx;

                    TransformedModelTemplate[] rotated = new TransformedModelTemplate[prop.Models.Count];
                    for (int j = 0; j < rotated.Length; j++)
                    {
                        rotated[j] = prop.Models[(j + resumeIdx) % rotated.Length];
                    }

                    SplineProp afterProp = new(rotated, afterFrom, prop.Span, prop.Interval, afterCount)
                    {
                        Key = prop.Key,
                    };
                    factory.Props.Insert(insertIndex, afterProp);
                }
            }
        }
    }

    private static void ApplyExtrusionSkips(
        SplineFactory factory,
        IReadOnlyList<SplineSkipRule> skips,
        IReadOnlyDictionary<string, double> anchors)
    {
        foreach (SplineSkipRule skip in skips)
        {
            double rawFrom = 0.0;
            double rawTo = float.MaxValue;
            if (skip.From.HasValue && !skip.From.Value.TryResolve(anchors, out rawFrom))
            {
                continue;
            }

            if (skip.To.HasValue && !skip.To.Value.TryResolve(anchors, out rawTo))
            {
                continue;
            }

            if (rawTo <= rawFrom)
            {
                continue;
            }

            float skipFrom = float.Max(0f, (float)rawFrom);
            float skipTo = (float)double.Min(rawTo, float.MaxValue);

            for (int i = factory.Extrusions.Count - 1; 0 <= i; i--)
            {
                SplineExtrusion ext = factory.Extrusions[i];
                if (!string.Equals(ext.Key, skip.Key, StringComparison.Ordinal))
                {
                    continue;
                }

                float samplerFrom = ext.Sampler.FromS;
                float samplerTo = ext.Sampler.ToS;
                if (skipTo <= samplerFrom || samplerTo <= skipFrom)
                {
                    continue;
                }

                factory.Extrusions.RemoveAt(i);
                int insertIndex = i;

                if (1e-3f < skipFrom - samplerFrom)
                {
                    ICurveSampler beforeSampler = BoundedCurveSampler.Create(ext.Sampler, samplerFrom, skipFrom);
                    SplineExtrusion beforeExt = new(ext.CrossSection, beforeSampler, ext.Offset)
                    {
                        VIntervalScale = ext.VIntervalScale,
                        VOffset = ext.VOffset,
                        Key = ext.Key,
                    };
                    factory.Extrusions.Insert(insertIndex++, beforeExt);
                }

                if (1e-3f < samplerTo - skipTo)
                {
                    ICurveSampler afterSampler = BoundedCurveSampler.Create(ext.Sampler, skipTo, samplerTo);
                    SplineExtrusion afterExt = new(ext.CrossSection, afterSampler, ext.Offset)
                    {
                        VIntervalScale = ext.VIntervalScale,
                        VOffset = ext.VOffset,
                        Key = ext.Key,
                    };
                    factory.Extrusions.Insert(insertIndex, afterExt);
                }
            }
        }
    }

    private static Vector4 ParseColorVector4(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Vector4.One;
        }

        try
        {
            System.Drawing.Color c = ColorTranslator.FromHtml(hex);
            return c.ToVector4();
        }
        catch
        {
            return Vector4.One;
        }
    }
}
