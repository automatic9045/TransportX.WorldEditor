using System;
using System.Collections.Generic;
using System.Linq;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Validation;

/// <summary>
/// Performs static domain, schema, anchor, and network topology validation across a <see cref="WorldProject"/>.
/// </summary>
public static class WorldValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        List<ValidationIssue> issues = new List<ValidationIssue>();

        ValidateModelLists(project, issues);
        ValidateCrossSectionLists(project, issues);
        ValidateCrossSections(project, issues);
        ValidateJunctionTemplates(project, issues);
        ValidateSplineTemplates(project, issues);
        ValidatePlacement(project, issues);

        return issues;
    }

    private static void ValidateModelLists(WorldProject project, IList<ValidationIssue> issues)
    {
        foreach (KeyValuePair<string, ModelListDocument> kvp in project.ModelLists)
        {
            ModelListDocument list = kvp.Value;
            foreach (ModelListEntry entry in list.Entries)
            {
                bool requiresModelFile = entry.Collider.Kind == ModelColliderKind.BoundingBox
                    || entry.Collider.Kind == ModelColliderKind.ConvexHull
                    || entry.Collider.Kind == ModelColliderKind.ClosedModel
                    || entry.Collider.Kind == ModelColliderKind.OpenModel;

                if (requiresModelFile && string.IsNullOrWhiteSpace(entry.ModelPath))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "MODEL_NONE_INVALID_FOR_COLLIDER",
                        $"モデル '{entry.Key}' は描画用モデルが NONE ですが、コライダー '${entry.Collider.Kind}' は描画モデルを必要とします（NON 許可は NonCollision / Box / Cylinder / Sphere のみ）。",
                        list.RelativePath));
                }

                if (!string.IsNullOrEmpty(entry.Collider.CollisionModelPath)
                    && entry.Collider.CollisionModelPath.Any(char.IsUpper))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        "MODEL_COLLISION_PATH_UPPERCASE",
                        $"モデル '{entry.Key}' の衝突モデルパス '{entry.Collider.CollisionModelPath}' に大文字が含まれています。TransportX 本体は実行時に第3列を小文字化するため、環境により一致しない可能性があります。",
                        list.RelativePath));
                }
            }
        }
    }

    private static void ValidateCrossSectionLists(WorldProject project, IList<ValidationIssue> issues)
    {
        HashSet<string> usedKeys = new(StringComparer.Ordinal);
        foreach (CrossSectionListDocument list in project.CrossSectionLists.Values)
        {
            for (int i = 0; i < list.Entries.Count; i++)
            {
                CrossSectionListEntry entry = list.Entries[i];
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "CROSS_SECTION_LIST_EMPTY_KEY",
                        $"断面リスト '{list.RelativePath}' の {i + 1} 行目で断面キーが空です。",
                        list.RelativePath));
                }
                else if (!usedKeys.Add(entry.Key))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "CROSS_SECTION_LIST_DUPLICATE_KEY",
                        $"断面リスト '{list.RelativePath}' の断面キー '{entry.Key}' が重複しています。",
                        list.RelativePath));
                }

                if (string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "CROSS_SECTION_LIST_EMPTY_PATH",
                        $"断面リスト '{list.RelativePath}' の '{entry.Key}' で XML パスが空です。",
                        list.RelativePath));
                }
            }
        }
    }

    private static void ValidateCrossSections(WorldProject project, IList<ValidationIssue> issues)
    {
        foreach (KeyValuePair<string, CrossSectionDocument> kvp in project.CrossSections)
        {
            CrossSectionDocument cs = kvp.Value;
            HashSet<string> vertexKeys = new HashSet<string>(cs.Vertices.Select(v => v.Key), StringComparer.Ordinal);
            HashSet<string> materialNames = new HashSet<string>(cs.Materials.Select(m => m.Name), StringComparer.Ordinal);
            HashSet<string> colliderMaterialNames = new HashSet<string>(cs.ColliderMaterials.Select(m => m.Name), StringComparer.Ordinal);

            HashSet<string> usedColliderMaterials = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < cs.Surfaces.Count; i++)
            {
                CrossSectionSurfaceModel surface = cs.Surfaces[i];
                if (surface.VertexKeys.Count < 2)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "CROSS_SECTION_SURFACE_TOO_FEW_VERTICES",
                        $"CrossSection '{cs.Key}' の Surface[{i}] は頂点が 2 つ未満です。",
                        cs.RelativePath));
                }

                foreach (string vertexKey in surface.VertexKeys)
                {
                    if (!vertexKeys.Contains(vertexKey))
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "CROSS_SECTION_MISSING_VERTEX",
                            $"CrossSection '{cs.Key}' の Surface[{i}] が未定義の頂点 '{vertexKey}' を参照しています。",
                            cs.RelativePath));
                    }
                }

                if (!materialNames.Contains(surface.MaterialName))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "CROSS_SECTION_MISSING_MATERIAL",
                        $"CrossSection '{cs.Key}' の Surface[{i}] が未定義のマテリアル '{surface.MaterialName}' を参照しています。",
                        cs.RelativePath));
                }

                if (!string.IsNullOrEmpty(surface.ColliderMaterialName))
                {
                    if (!colliderMaterialNames.Contains(surface.ColliderMaterialName))
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "CROSS_SECTION_MISSING_COLLIDER_MATERIAL",
                            $"CrossSection '{cs.Key}' の Surface[{i}] が未定義の物理材質 '{surface.ColliderMaterialName}' を参照しています。",
                            cs.RelativePath));
                    }

                    usedColliderMaterials.Add(surface.ColliderMaterialName);
                }
            }

            if (1 < usedColliderMaterials.Count)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "CROSS_SECTION_MIXED_COLLIDER_MATERIALS",
                    $"CrossSection '{cs.Key}' 内で複数の異なる ColliderMaterial ({string.Join(", ", usedColliderMaterials)}) が混在しています。TransportX 本体は 1 断面につき単一の物理材質のみサポートしています。",
                    cs.RelativePath));
            }
        }
    }

    private static void ValidateSplineTemplates(WorldProject project, IList<ValidationIssue> issues)
    {
        foreach (KeyValuePair<string, SplineTemplateDocument> kvp in project.SplineTemplates)
        {
            SplineTemplateDocument template = kvp.Value;
            if (!string.IsNullOrEmpty(template.OutletLayoutKey) && !project.LaneLayouts.ContainsKey(template.OutletLayoutKey))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "TEMPLATE_MISSING_LAYOUT",
                    $"スプラインテンプレート '{template.Key}' が未登録の LaneLayout '{template.OutletLayoutKey}' を参照しています。",
                    template.RelativePath));
            }

            foreach (SplineTemplateExtrusionRule extrusion in template.Extrusions)
            {
                if (!project.CrossSections.ContainsKey(extrusion.CrossSectionKey))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "TEMPLATE_MISSING_CROSS_SECTION",
                        $"スプラインテンプレート '{template.Key}' が未登録の CrossSection '{extrusion.CrossSectionKey}' を参照しています。",
                        template.RelativePath));
                }
            }
        }
    }

    private static void ValidateJunctionTemplates(WorldProject project, IList<ValidationIssue> issues)
    {
        foreach (KeyValuePair<string, JunctionTemplateDocument> kvp in project.JunctionTemplates)
        {
            JunctionTemplateDocument template = kvp.Value;
            Dictionary<string, LaneLayoutDocument?> portLayouts = new Dictionary<string, LaneLayoutDocument?>(StringComparer.Ordinal);

            foreach (JunctionPortDefinition port in template.Ports)
            {
                project.LaneLayouts.TryGetValue(port.LayoutKey, out LaneLayoutDocument? layout);
                if (layout is null)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "TEMPLATE_MISSING_LAYOUT",
                        $"ジャンクションテンプレート '{template.Key}' の端子 '{port.Key}' が未登録の LaneLayout '{port.LayoutKey}' を参照しています。",
                        template.RelativePath));
                }

                portLayouts[port.Key] = layout;
            }

            foreach (JunctionRelayDefinition relay in template.Relays)
            {
                project.LaneLayouts.TryGetValue(relay.LayoutKey, out LaneLayoutDocument? layout);
                if (layout is null)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "TEMPLATE_MISSING_LAYOUT",
                        $"ジャンクションテンプレート '{template.Key}' の中継端子 '{relay.InputKey}'/'{relay.OutputKey}' が未登録の LaneLayout '{relay.LayoutKey}' を参照しています。",
                        template.RelativePath));
                    portLayouts[relay.InputKey] = null;
                    portLayouts[relay.OutputKey] = null;
                }
                else
                {
                    portLayouts[relay.InputKey] = layout;
                    portLayouts[relay.OutputKey] = layout.CreateOpposition();
                }
            }

            foreach (JunctionPathTemplateModel path in template.Paths)
            {
                ValidatePinReference(template, portLayouts, path.FromPortKey, path.FromPinIndex, path.Key, issues);
                ValidatePinReference(template, portLayouts, path.ToPortKey, path.ToPinIndex, path.Key, issues);
                DistanceAnchorResolver.ValidateAndResolveJunctionPath(path, issues, null, template.RelativePath);
            }

            foreach (JunctionCurveTemplateModel curve in template.Curves)
            {
                if (curve.Origin.Kind == JunctionCurveOriginKind.PortPinRelative && !string.IsNullOrEmpty(curve.Origin.PortKey))
                {
                    ValidatePinReference(template, portLayouts, curve.Origin.PortKey, curve.Origin.PinIndex, curve.Key, issues);
                }

                DistanceAnchorResolver.ValidateAndResolveJunctionCurve(curve, issues, null, template.RelativePath);
            }
        }
    }

    private static void ValidatePinReference(
        JunctionTemplateDocument template,
        IReadOnlyDictionary<string, LaneLayoutDocument?> portLayouts,
        string portKey,
        int pinIndex,
        string ownerKey,
        IList<ValidationIssue> issues)
    {
        if (!portLayouts.TryGetValue(portKey, out LaneLayoutDocument? layout))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "JUNCTION_MISSING_PORT",
                $"ジャンクションテンプレート '{template.Key}' の要素 '{ownerKey}' が未定義ポート '{portKey}' を参照しています。",
                template.RelativePath));
            return;
        }

        if (layout is not null && (pinIndex < 0 || layout.Lanes.Count <= pinIndex))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "JUNCTION_PIN_OUT_OF_RANGE",
                $"ジャンクションテンプレート '{template.Key}' の要素 '{ownerKey}' がポート '{portKey}' の無効なピン番号 {pinIndex} (0..{layout.Lanes.Count - 1}) を指定しています。",
                template.RelativePath));
        }
    }

    private static void ValidatePlacement(WorldProject project, IList<ValidationIssue> issues)
    {
        WorldPlacementDocument placement = project.Placement;

        NetworkTopologicalSortResult sortResult = NetworkTopologicalSorter.Sort(placement);
        if (0 < sortResult.CycleNodeIds.Count)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "NETWORK_TOPOLOGY_CYCLE",
                $"ワールドネットワークの接続グラフに循環依存があります: {string.Join(", ", sortResult.CycleNodeIds)}",
                placement.RelativePath));
        }

        Dictionary<string, SplinePlacementNode> splineById = new Dictionary<string, SplinePlacementNode>(StringComparer.Ordinal);
        foreach (SplinePlacementNode spline in placement.Splines)
        {
            if (!splineById.TryAdd(spline.Id, spline))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "PLACEMENT_DUPLICATE_ID",
                    $"スプラインノード ID '{spline.Id}' が重複しています。",
                    placement.RelativePath));
            }
        }

        Dictionary<string, JunctionPlacementNode> junctionById = new Dictionary<string, JunctionPlacementNode>(StringComparer.Ordinal);
        foreach (JunctionPlacementNode junction in placement.Junctions)
        {
            if (splineById.ContainsKey(junction.Id) || !junctionById.TryAdd(junction.Id, junction))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "PLACEMENT_DUPLICATE_ID",
                    $"ジャンクションノード ID '{junction.Id}' が他ノードと重複しています。",
                    placement.RelativePath));
            }
        }

        HashSet<string> usedNodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (SplinePlacementNode spline in placement.Splines)
        {
            ValidateNodeKey(spline.Id, spline.Key, "スプライン", usedNodeKeys, placement.RelativePath, issues);
        }

        foreach (JunctionPlacementNode junction in placement.Junctions)
        {
            ValidateNodeKey(junction.Id, junction.Key, "ジャンクション", usedNodeKeys, placement.RelativePath, issues);
        }

        foreach (SplinePlacementNode spline in placement.Splines)
        {
            DistanceAnchorResolver.ValidateAndResolveSpline(spline, issues, null, placement.RelativePath);

            SplineTemplateDocument? splineTemplate = null;
            if (!string.IsNullOrEmpty(spline.TemplateKey) && !project.SplineTemplates.TryGetValue(spline.TemplateKey, out splineTemplate))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "PLACEMENT_MISSING_TEMPLATE",
                    $"スプライン '{spline.Id}' が未ロードのテンプレート '{spline.TemplateKey}' を参照しています。",
                    placement.RelativePath));
            }

            if (spline.Origin.Kind == SplineOriginKind.FromSplineOutlet)
            {
                if (string.IsNullOrEmpty(spline.Origin.SourceNodeId) || !splineById.TryGetValue(spline.Origin.SourceNodeId, out SplinePlacementNode? sourceSpline))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "PLACEMENT_MISSING_SOURCE",
                        $"スプライン '{spline.Id}' の接続元スプライン '{spline.Origin.SourceNodeId}' が見つかりません。",
                        placement.RelativePath));
                }
                else
                {
                    if (sourceSpline.ConnectBezier is not null)
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "SPLINE_CONNECT_BEZIER_ALREADY_CONNECTED",
                            $"スプライン '{sourceSpline.Id}' は ConnectBezier を持つため、終端へさらにスプライン '{spline.Id}' を接続できません。",
                            placement.RelativePath));
                    }

                    CheckSplineInletLayoutCompatibility(
                        project,
                        GetSplineOutletLayout(project, sourceSpline),
                        GetSplineTemplateLayoutKey(project, sourceSpline),
                        spline,
                        splineTemplate,
                        issues);
                }
            }
            else if (spline.Origin.Kind == SplineOriginKind.FromJunctionPort)
            {
                if (string.IsNullOrEmpty(spline.Origin.SourceNodeId)
                    || !junctionById.TryGetValue(spline.Origin.SourceNodeId, out JunctionPlacementNode? sourceJunction))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "PLACEMENT_MISSING_SOURCE",
                        $"スプライン '{spline.Id}' の接続元ジャンクション '{spline.Origin.SourceNodeId}' が見つかりません。",
                        placement.RelativePath));
                }
                else
                {
                    (LaneLayoutDocument? portLayout, string? portLayoutKey) = ResolveJunctionPortLayout(project, sourceJunction, spline.Origin.SourcePortKey);
                    if (portLayout is null && !string.IsNullOrEmpty(spline.Origin.SourcePortKey))
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "PLACEMENT_MISSING_PORT",
                            $"スプライン '{spline.Id}' の接続元 '{sourceJunction.Id}.{spline.Origin.SourcePortKey}' が見つかりません。",
                            placement.RelativePath));
                    }
                    else
                    {
                        CheckSplineInletLayoutCompatibility(project, portLayout, portLayoutKey, spline, splineTemplate, issues);
                    }
                }
            }

            if (spline.ConnectBezier is not null)
            {
                LaneLayoutDocument? outletLayout = GetSplineOutletLayout(project, spline);
                LaneLayoutDocument? targetLayout = ResolvePortReferenceLayout(project, splineById, junctionById, spline.ConnectBezier.TargetPort, issues, placement.RelativePath);
                if (outletLayout is not null && targetLayout is not null && !outletLayout.CanConnectTo(targetLayout))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "LANE_LAYOUT_MISMATCH",
                        $"スプライン '{spline.Id}' の ConnectBezier 先ポート ({spline.ConnectBezier.TargetPort.ElementId}) と LaneLayout が鏡像一致しません。",
                        placement.RelativePath));
                }
            }
        }

        foreach (JunctionPlacementNode junction in placement.Junctions)
        {
            if (!project.JunctionTemplates.TryGetValue(junction.TemplateKey, out JunctionTemplateDocument? template))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "PLACEMENT_MISSING_TEMPLATE",
                    $"ジャンクション '{junction.Id}' が未ロードのテンプレート '{junction.TemplateKey}' を参照しています。",
                    placement.RelativePath));
                continue;
            }

            (LaneLayoutDocument? targetPortLayout, _) = ResolveJunctionPortLayout(project, junction, junction.Origin.TargetPortKey);

            if (junction.Origin.Kind == JunctionOriginKind.FromSplineOutlet)
            {
                if (string.IsNullOrEmpty(junction.Origin.SourceNodeId)
                    || !splineById.TryGetValue(junction.Origin.SourceNodeId, out SplinePlacementNode? sourceSpline))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "PLACEMENT_MISSING_SOURCE",
                        $"ジャンクション '{junction.Id}' の接続元スプライン '{junction.Origin.SourceNodeId}' が見つかりません。",
                        placement.RelativePath));
                }
                else
                {
                    if (sourceSpline.ConnectBezier is not null)
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "SPLINE_CONNECT_BEZIER_INTO_JUNCTION",
                            $"スプライン '{sourceSpline.Id}' は ConnectBezier を持つため IntoJunction でジャンクション '{junction.Id}' を接続できません（TransportX 本体で BezierSpline へのキャスト例外になります）。",
                            placement.RelativePath));
                    }

                    LaneLayoutDocument? splineOutletLayout = GetSplineOutletLayout(project, sourceSpline);
                    if (splineOutletLayout is not null && targetPortLayout is not null && !splineOutletLayout.CanConnectTo(targetPortLayout))
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "LANE_LAYOUT_MISMATCH",
                            $"スプライン '{sourceSpline.Id}' とジャンクション '{junction.Id}.{junction.Origin.TargetPortKey}' の LaneLayout が接続互換（鏡像一致）ではありません。",
                            placement.RelativePath));
                    }
                }
            }
            else if (junction.Origin.Kind == JunctionOriginKind.FromJunctionPort)
            {
                if (string.IsNullOrEmpty(junction.Origin.SourceNodeId)
                    || !junctionById.TryGetValue(junction.Origin.SourceNodeId, out JunctionPlacementNode? sourceJunction))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "PLACEMENT_MISSING_SOURCE",
                        $"ジャンクション '{junction.Id}' の接続元ジャンクション '{junction.Origin.SourceNodeId}' が見つかりません。",
                        placement.RelativePath));
                }
                else
                {
                    (LaneLayoutDocument? srcPortLayout, _) = ResolveJunctionPortLayout(project, sourceJunction, junction.Origin.SourcePortKey);
                    if (srcPortLayout is not null && targetPortLayout is not null && !srcPortLayout.CanConnectTo(targetPortLayout))
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error,
                            "LANE_LAYOUT_MISMATCH",
                            $"ジャンクション '{sourceJunction.Id}.{junction.Origin.SourcePortKey}' と '{junction.Id}.{junction.Origin.TargetPortKey}' の LaneLayout が接続互換（鏡像一致）ではありません。",
                            placement.RelativePath));
                    }
                }
            }
        }

        foreach (ExplicitPortConnection connection in placement.Connections)
        {
            LaneLayoutDocument? fromLayout = ResolvePortReferenceLayout(project, splineById, junctionById, connection.From, issues, placement.RelativePath);
            LaneLayoutDocument? toLayout = ResolvePortReferenceLayout(project, splineById, junctionById, connection.To, issues, placement.RelativePath);
            if (fromLayout is not null && toLayout is not null && !fromLayout.CanConnectTo(toLayout))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "LANE_LAYOUT_MISMATCH",
                    $"Network.Connect ({connection.From.ElementId} -> {connection.To.ElementId}) 間の LaneLayout が鏡像一致しません。",
                    placement.RelativePath));
            }
        }
    }

    private static void CheckSplineInletLayoutCompatibility(
        WorldProject project,
        LaneLayoutDocument? sourceOutletLayout,
        string? sourceLayoutKey,
        SplinePlacementNode spline,
        SplineTemplateDocument? splineTemplate,
        IList<ValidationIssue> issues)
    {
        if (splineTemplate is null)
        {
            return;
        }

        if (sourceOutletLayout is null || !project.LaneLayouts.TryGetValue(splineTemplate.OutletLayoutKey, out LaneLayoutDocument? destOutletLayout))
        {
            return;
        }

        LaneLayoutDocument destInletLayout = destOutletLayout.CreateOpposition();
        if (!sourceOutletLayout.CanConnectTo(destInletLayout))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "LANE_LAYOUT_MISMATCH",
                $"スプライン '{spline.Id}' (テンプレート '{spline.TemplateKey}', レイアウト '{splineTemplate.OutletLayoutKey}') の入口が接続元ポート '{sourceLayoutKey}' と接続互換（鏡像一致）ではありません。",
                project.Placement.RelativePath));
        }
        else if (!string.IsNullOrEmpty(sourceLayoutKey)
            && !string.Equals(sourceLayoutKey, splineTemplate.OutletLayoutKey, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "INTO_SPLINE_LAYOUT_KEY_DIFFERS",
                $"スプライン '{spline.Id}' の IntoSpline 先テンプレートのレイアウトキー ('{splineTemplate.OutletLayoutKey}') が接続元ポートのレイアウトキー ('{sourceLayoutKey}') と異なります。TransportX 本体の SplineFactory は参照同一性を検査します。",
                project.Placement.RelativePath));
        }
    }

    private static LaneLayoutDocument? ResolvePortReferenceLayout(
        WorldProject project,
        IReadOnlyDictionary<string, SplinePlacementNode> splineById,
        IReadOnlyDictionary<string, JunctionPlacementNode> junctionById,
        PortReferenceModel portRef,
        IList<ValidationIssue> issues,
        string sourcePath)
    {
        if (portRef.Kind == PortTargetKind.SplineInlet || portRef.Kind == PortTargetKind.SplineOutlet)
        {
            if (!splineById.TryGetValue(portRef.ElementId, out SplinePlacementNode? spline))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "PLACEMENT_MISSING_ELEMENT",
                    $"ポート参照先スプライン '{portRef.ElementId}' が見つかりません。",
                    sourcePath));
                return null;
            }

            LaneLayoutDocument? outlet = GetSplineOutletLayout(project, spline);
            if (outlet is null)
            {
                return null;
            }

            return portRef.Kind == PortTargetKind.SplineInlet ? outlet.CreateOpposition() : outlet;
        }

        if (!junctionById.TryGetValue(portRef.ElementId, out JunctionPlacementNode? junction))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "PLACEMENT_MISSING_ELEMENT",
                $"ポート参照先ジャンクション '{portRef.ElementId}' が見つかりません。",
                sourcePath));
            return null;
        }

        (LaneLayoutDocument? portLayout, _) = ResolveJunctionPortLayout(project, junction, portRef.PortKey);
        if (portLayout is null && !string.IsNullOrEmpty(portRef.PortKey))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "PLACEMENT_MISSING_PORT",
                $"ジャンクション '{junction.Id}' のポート '{portRef.PortKey}' が見つかりません。",
                sourcePath));
        }

        return portLayout;
    }

    private static string? GetSplineTemplateLayoutKey(WorldProject project, SplinePlacementNode spline)
    {
        if (string.IsNullOrEmpty(spline.TemplateKey) || !project.SplineTemplates.TryGetValue(spline.TemplateKey, out SplineTemplateDocument? template))
        {
            return null;
        }

        return template.OutletLayoutKey;
    }

    private static LaneLayoutDocument? GetSplineOutletLayout(WorldProject project, SplinePlacementNode spline)
    {
        string? layoutKey = GetSplineTemplateLayoutKey(project, spline);
        if (string.IsNullOrEmpty(layoutKey))
        {
            return null;
        }

        project.LaneLayouts.TryGetValue(layoutKey, out LaneLayoutDocument? layout);
        return layout;
    }

    private static (LaneLayoutDocument? Layout, string? LayoutKey) ResolveJunctionPortLayout(
        WorldProject project,
        JunctionPlacementNode junction,
        string? portKey)
    {
        if (string.IsNullOrEmpty(portKey) || !project.JunctionTemplates.TryGetValue(junction.TemplateKey, out JunctionTemplateDocument? template))
        {
            return (null, null);
        }

        JunctionPortDefinition? port = template.Ports.FirstOrDefault(p => string.Equals(p.Key, portKey, StringComparison.Ordinal));
        if (port is null)
        {
            return (null, null);
        }

        project.LaneLayouts.TryGetValue(port.LayoutKey, out LaneLayoutDocument? layout);
        return (layout, port.LayoutKey);
    }

    private static void ValidateNodeKey(
        string nodeId,
        string? nodeKey,
        string kindLabel,
        HashSet<string> usedKeys,
        string sourcePath,
        IList<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "PLACEMENT_MISSING_KEY",
                $"{kindLabel} '{nodeId}' の Key が未設定です。",
                sourcePath));
            return;
        }

        if (!CSharpIdentifierUtilities.TryValidateNodeKey(nodeKey, out string reason))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "PLACEMENT_INVALID_KEY",
                $"{kindLabel} '{nodeId}' の Key が不正です: {reason}",
                sourcePath));
            return;
        }

        if (!usedKeys.Add(nodeKey))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "PLACEMENT_DUPLICATE_KEY",
                $"{kindLabel} の Key '{nodeKey}' が他の要素と重複しています。",
                sourcePath));
        }
    }
}
