using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Serialization.Diagnostics;
using DiagnosticSeverity = TransportX.WorldEditor.Serialization.Diagnostics.DiagnosticSeverity;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Parsed representation of <c>World.csx</c> including raw <c>#load</c> directives before
/// classification into Init / Template / Extra scripts by <see cref="Storage.WorldProjectStorage"/>.
/// </summary>
public sealed record ParsedWorldCsx(
    WorldPlacementDocument Document,
    IReadOnlyList<string> LoadDirectives);

/// <summary>
/// Parses and generates the canonical <c>World.csx</c> placement script using Roslyn ASTs,
/// topological ordering, reusable factory variables (<c>fSpline</c> / <c>fJunction</c>),
/// SSA-style built node variables, and id-less <c>// &lt;user-prebuild&gt;</c> blocks.
/// </summary>
public static class WorldCsxSerializer
{
    private abstract record PendingFactoryNode(string FactoryVarName);

    private sealed record PendingSplineFactory(string FactoryVarName, SplinePlacementNode Node)
        : PendingFactoryNode(FactoryVarName);

    private sealed record PendingJunctionFactory(string FactoryVarName, JunctionPlacementNode Node)
        : PendingFactoryNode(FactoryVarName);

    public static (ParsedWorldCsx? Result, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string filePath,
        string sourceText,
        string relativePath = "World.csx")
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sourceText);

        CsxSlicedFile sliced = CsxMarkerSlicer.Slice(filePath, sourceText);
        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>(sliced.Diagnostics);

        if (!RoslynCsxHelpers.TryParseEditorGeneratedBlock(
            filePath,
            sliced,
            diagnostics,
            out SyntaxList<StatementSyntax> statements,
            out SyntaxToken closingToken,
            out IReadOnlyDictionary<int, CsxPreBuildBlock> preBuildByIndex))
        {
            return (null, diagnostics);
        }

        WorldPlacementDocument doc = new WorldPlacementDocument
        {
            RelativePath = relativePath,
            UserCode = sliced.UserCode,
        };

        Dictionary<string, PendingFactoryNode> activeFactories = new Dictionary<string, PendingFactoryNode>(StringComparer.Ordinal);
        HashSet<string> declaredFactoryVars = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> splineBuiltVars = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> junctionBuiltVars = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> usedVarNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<int> boundPreBuildIndices = new HashSet<int>();
        int propCounter = 0;

        foreach (StatementSyntax statement in statements)
        {
            List<int> attachedPreBuilds = RoslynCsxHelpers.ExtractPreBuildPlaceholderIndices(statement.GetLeadingTrivia());
            string? statementDisplayName = RoslynCsxHelpers.ExtractStatementDisplayName(statement);

            if (statement is EmptyStatementSyntax)
            {
                ReportOrphanPreBuilds(attachedPreBuilds, preBuildByIndex, filePath, diagnostics);
                continue;
            }

            if (statement is LocalDeclarationStatementSyntax localDecl)
            {
                if (localDecl.Declaration.Variables.Count != 1
                    || localDecl.Declaration.Variables[0].Initializer?.Value is not ExpressionSyntax initExpr)
                {
                    ReportOrphanPreBuilds(attachedPreBuilds, preBuildByIndex, filePath, diagnostics);
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        RoslynCsxHelpers.GetActualLine(sliced, statement),
                        RoslynCsxHelpers.GetColumn(statement),
                        "ローカル変数宣言は単一変数の初期化のみ許可されます。"));
                    continue;
                }

                string varName = localDecl.Declaration.Variables[0].Identifier.ValueText;
                if (!usedVarNames.Add(varName))
                {
                    ReportOrphanPreBuilds(attachedPreBuilds, preBuildByIndex, filePath, diagnostics);
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        RoslynCsxHelpers.GetActualLine(sliced, localDecl),
                        RoslynCsxHelpers.GetColumn(localDecl),
                        $"変数 '{varName}' が再宣言されています (正規形は SSA 非再代入のみサポート)。"));
                    continue;
                }

                if (initExpr is InvocationExpressionSyntax buildInv
                    && TryParseBuildStatement(buildInv, activeFactories, out PendingFactoryNode? pendingFactory))
                {
                    string? preBuildCode = ResolvePreBuildForBuildStatement(
                        attachedPreBuilds,
                        preBuildByIndex,
                        boundPreBuildIndices,
                        filePath,
                        diagnostics);

                    SetFactoryDisplayName(pendingFactory!, statementDisplayName, onlyIfUnset: true);

                    if (pendingFactory is PendingSplineFactory ps)
                    {
                        ps.Node.Id = varName;
                        if (string.IsNullOrWhiteSpace(ps.Node.Key))
                        {
                            ps.Node.Key = varName;
                        }

                        ps.Node.PreBuildUserCode = preBuildCode;
                        splineBuiltVars[varName] = ps.Node.Id;
                        doc.Splines.Add(ps.Node);
                        activeFactories.Remove(ps.FactoryVarName);
                        continue;
                    }

                    if (pendingFactory is PendingJunctionFactory pj)
                    {
                        pj.Node.Id = varName;
                        if (string.IsNullOrWhiteSpace(pj.Node.Key))
                        {
                            pj.Node.Key = varName;
                        }

                        pj.Node.PreBuildUserCode = preBuildCode;
                        junctionBuiltVars[varName] = pj.Node.Id;
                        doc.Junctions.Add(pj.Node);
                        activeFactories.Remove(pj.FactoryVarName);
                        continue;
                    }
                }

                ReportOrphanPreBuilds(attachedPreBuilds, preBuildByIndex, filePath, diagnostics);

                if (initExpr is InvocationExpressionSyntax createInv
                    && TryParseFactoryStart(createInv, varName, splineBuiltVars, junctionBuiltVars, out PendingFactoryNode? startedFactory))
                {
                    SetFactoryDisplayName(startedFactory!, statementDisplayName);
                    declaredFactoryVars.Add(varName);
                    activeFactories[varName] = startedFactory!;
                    continue;
                }

                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    RoslynCsxHelpers.GetActualLine(sliced, statement),
                    RoslynCsxHelpers.GetColumn(statement),
                    $"World.csx の <editor-generated> 内で解釈できない変数初期化です: {statement}"));
                continue;
            }

            ReportOrphanPreBuilds(attachedPreBuilds, preBuildByIndex, filePath, diagnostics);

            if (statement is ExpressionStatementSyntax exprStmt)
            {
                if (exprStmt.Expression is AssignmentExpressionSyntax factoryAssign
                    && factoryAssign.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && factoryAssign.Left is IdentifierNameSyntax reassignTarget
                    && declaredFactoryVars.Contains(reassignTarget.Identifier.ValueText)
                    && factoryAssign.Right is InvocationExpressionSyntax reassignInv
                    && TryParseFactoryStart(
                        reassignInv,
                        reassignTarget.Identifier.ValueText,
                        splineBuiltVars,
                        junctionBuiltVars,
                        out PendingFactoryNode? reassignedFactory))
                {
                    string factoryName = reassignTarget.Identifier.ValueText;
                    if (activeFactories.ContainsKey(factoryName))
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Error,
                            filePath,
                            RoslynCsxHelpers.GetActualLine(sliced, exprStmt),
                            RoslynCsxHelpers.GetColumn(exprStmt),
                            $"ファクトリ変数 '{factoryName}' に対して '.Build()' が呼ばれる前に再代入されました。"));
                        continue;
                    }

                    SetFactoryDisplayName(reassignedFactory!, statementDisplayName);
                    activeFactories[factoryName] = reassignedFactory!;
                    continue;
                }

                if (TryParseExpressionStatement(
                    exprStmt.Expression,
                    statementDisplayName,
                    doc,
                    activeFactories,
                    splineBuiltVars,
                    junctionBuiltVars,
                    ref propCounter))
                {
                    continue;
                }
            }

            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                RoslynCsxHelpers.GetActualLine(sliced, statement),
                RoslynCsxHelpers.GetColumn(statement),
                $"World.csx の <editor-generated> 内で許可されていないステートメントです: {statement}"));
        }

        List<int> eofPreBuilds = RoslynCsxHelpers.ExtractPreBuildPlaceholderIndices(closingToken.LeadingTrivia);
        ReportOrphanPreBuilds(eofPreBuilds, preBuildByIndex, filePath, diagnostics);

        foreach (KeyValuePair<int, CsxPreBuildBlock> kvp in preBuildByIndex)
        {
            if (!boundPreBuildIndices.Contains(kvp.Key))
            {
                bool alreadyReported = diagnostics.Any(d => d.Line == kvp.Value.MarkerStartLine && d.FilePath == filePath);
                if (!alreadyReported)
                {
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        filePath,
                        kvp.Value.MarkerStartLine,
                        1,
                        "'// <user-prebuild>' ブロックに対応する '.Build();' が見つかりません。"));
                }
            }
        }

        if (0 < activeFactories.Count)
        {
            foreach (PendingFactoryNode unbuilt in activeFactories.Values)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    sliced.EditorGeneratedStartLine,
                    1,
                    $"ファクトリ変数 '{unbuilt.FactoryVarName}' に対して '.Build()' が呼ばれていません。"));
            }
        }

        if (RoslynCsxHelpers.HasError(diagnostics))
        {
            return (null, diagnostics);
        }

        return (new ParsedWorldCsx(doc, sliced.LoadDirectives), diagnostics);
    }

    /// <summary>
    /// Best-effort extraction of camera, lighting, environment, background, and static chunk props
    /// from hand-written <c>World.csx</c> scripts without <c>&lt;editor-generated&gt;</c> markers.
    /// </summary>
    public static void PopulateFallback(string sourceText, string relativePath, WorldPlacementDocument doc)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(doc);

        doc.RelativePath = relativePath;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText);
        SyntaxNode root = tree.GetRoot();
        Dictionary<string, string> emptyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int propCounter = 0;

        foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            (ExpressionSyntax rootReceiver, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(inv);
            if (calls.Count != 1)
            {
                continue;
            }

            if (rootReceiver is IdentifierNameSyntax rootGlobal)
            {
                TryParseGlobalInvocation(rootGlobal.Identifier.ValueText, calls[0], doc, emptyMap, emptyMap);
                continue;
            }

            if (TryParseChunksIndexer(rootReceiver, out ChunkIndexModel chunk))
            {
                InvocationExpressionSyntax call = calls[0];
                SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
                if (RoslynCsxHelpers.GetInvokedMethodName(call) == "PutProp"
                    && (args.Count == 4 || args.Count == 7)
                    && RoslynCsxHelpers.TryGetString(args[0].Expression, out string modelKey)
                    && TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
                {
                    propCounter++;
                    StatementSyntax? propStmt = inv.FirstAncestorOrSelf<StatementSyntax>();
                    string? propDisplayName = propStmt is null ? null : RoslynCsxHelpers.ExtractStatementDisplayName(propStmt);
                    doc.Props.Add(new ChunkPropPlacement($"prop_{propCounter:D3}", modelKey, chunk, pose, propDisplayName));
                }
            }
        }
    }

    public static string Generate(
        WorldPlacementDocument document,
        IEnumerable<string> templateLoadPaths,
        string editorCsxLoadPath = "__Editor.csx",
        string initLoadPath = "Init.csx")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(templateLoadPaths);

        List<string> loads = new List<string>();
        HashSet<string> seenLoads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLoad(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seenLoads.Add(path.Replace('\\', '/')))
            {
                loads.Add(path.Replace('\\', '/'));
            }
        }

        AddLoad(editorCsxLoadPath);
        AddLoad(initLoadPath);
        foreach (string templatePath in templateLoadPaths)
        {
            AddLoad(templatePath);
        }

        foreach (string extra in document.ExtraLoadScripts)
        {
            AddLoad(extra);
        }

        StringBuilder sb = new StringBuilder();

        WorldPoseDegreesModel cam = document.Camera.Pose;
        sb.Append("    Camera.Locate(")
            .Append(cam.Chunk.X).Append(", ")
            .Append(cam.Chunk.Z).Append(", ")
            .Append(RoslynCsxHelpers.FormatPoseArgs(cam.LocalPose))
            .Append(");\n");

        if (document.Avatar is not null && !string.IsNullOrEmpty(document.Avatar.XmlPath))
        {
            sb.Append("    Avatar.Load(").Append(RoslynCsxHelpers.FormatStringLiteral(document.Avatar.XmlPath)).Append(");\n");
            if (document.Avatar.InitialPose.HasValue)
            {
                WorldPoseDegreesModel ap = document.Avatar.InitialPose.Value;
                sb.Append("    Avatar.Locate(")
                    .Append(ap.Chunk.X).Append(", ")
                    .Append(ap.Chunk.Z).Append(", ")
                    .Append(RoslynCsxHelpers.FormatPoseArgs(ap.LocalPose))
                    .Append(");\n");
            }
        }

        if (!string.IsNullOrEmpty(document.EnvironmentPath))
        {
            sb.Append("    Environment.SetDefault(").Append(RoslynCsxHelpers.FormatStringLiteral(document.EnvironmentPath)).Append(");\n");
        }

        DirectionalLightModel light = document.DirectionalLight;
        sb.Append("    DirectionalLight.SetColor(").Append(RoslynCsxHelpers.FormatStringLiteral(light.ColorHex)).Append(");\n");
        sb.Append("    DirectionalLight.SetDirection(")
            .Append(RoslynCsxHelpers.FormatNumber(light.DirectionX)).Append(", ")
            .Append(RoslynCsxHelpers.FormatNumber(light.DirectionY)).Append(", ")
            .Append(RoslynCsxHelpers.FormatNumber(light.DirectionZ)).Append(");\n");
        sb.Append("    DirectionalLight.SetIntensity(").Append(RoslynCsxHelpers.FormatNumber(light.Intensity)).Append(");\n");

        foreach (string bg in document.BackgroundModels)
        {
            sb.Append("    Background.Add(").Append(RoslynCsxHelpers.FormatStringLiteral(bg)).Append(");\n");
        }

        if (0 < document.Props.Count)
        {
            sb.Append('\n');
            foreach (ChunkPropPlacement prop in document.Props)
            {
                if (!string.IsNullOrWhiteSpace(prop.DisplayName))
                {
                    sb.Append(RoslynCsxHelpers.FormatDisplayDirective("    ", prop.DisplayName));
                }

                sb.Append("    Chunks[")
                    .Append(prop.Chunk.X).Append(", ").Append(prop.Chunk.Z)
                    .Append("].PutProp(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(prop.ModelKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatPoseArgs(prop.Pose))
                    .Append(");\n");
            }
        }

        Dictionary<string, SplinePlacementNode> splineById = document.Splines.ToDictionary(s => s.Id, StringComparer.Ordinal);
        Dictionary<string, JunctionPlacementNode> junctionById = document.Junctions.ToDictionary(j => j.Id, StringComparer.Ordinal);
        NetworkTopologicalSortResult sort = NetworkTopologicalSorter.Sort(document);

        List<NetworkNodeRef> ordered = new List<NetworkNodeRef>(sort.OrderedNodes);
        HashSet<string> emittedIds = new HashSet<string>(ordered.Select(n => n.Id), StringComparer.Ordinal);
        foreach (SplinePlacementNode s in document.Splines)
        {
            if (emittedIds.Add(s.Id))
            {
                ordered.Add(new NetworkNodeRef(false, s.Id));
            }
        }

        foreach (JunctionPlacementNode j in document.Junctions)
        {
            if (emittedIds.Add(j.Id))
            {
                ordered.Add(new NetworkNodeRef(true, j.Id));
            }
        }

        HashSet<string> usedVars = new HashSet<string>(StringComparer.Ordinal) { "fSpline", "fJunction" };
        HashSet<string> declaredAnchors = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> splineBuiltVarById = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> junctionBuiltVarById = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (NetworkNodeRef item in ordered)
        {
            string builtVar = AllocateUniqueVar(item.Id, usedVars, item.IsJunction ? "junction" : "spline");
            if (item.IsJunction)
            {
                junctionBuiltVarById[item.Id] = builtVar;
            }
            else
            {
                splineBuiltVarById[item.Id] = builtVar;
            }
        }

        bool declaredSplineFactory = false;
        bool declaredJunctionFactory = false;

        foreach (NetworkNodeRef item in ordered)
        {
            sb.Append('\n');
            if (!item.IsJunction)
            {
                SplinePlacementNode spline = splineById[item.Id];
                string builtVar = splineBuiltVarById[spline.Id];
                bool declareFactory = !declaredSplineFactory;
                declaredSplineFactory = true;
                EmitSplineNode(sb, spline, "fSpline", declareFactory, builtVar, splineBuiltVarById, junctionBuiltVarById, declaredAnchors);
            }
            else
            {
                JunctionPlacementNode junction = junctionById[item.Id];
                string builtVar = junctionBuiltVarById[junction.Id];
                bool declareFactory = !declaredJunctionFactory;
                declaredJunctionFactory = true;
                EmitJunctionNode(sb, junction, "fJunction", declareFactory, builtVar, splineBuiltVarById, junctionBuiltVarById);
            }
        }

        if (0 < document.Connections.Count)
        {
            sb.Append('\n');
            foreach (ExplicitPortConnection conn in document.Connections)
            {
                sb.Append("    Network.Connect(")
                    .Append(FormatPortReference(conn.From, splineBuiltVarById, junctionBuiltVarById))
                    .Append(", ")
                    .Append(FormatPortReference(conn.To, splineBuiltVarById, junctionBuiltVarById))
                    .Append(");\n");
            }
        }

        return RoslynCsxHelpers.WrapCanonicalCsxFile(loads, sb.ToString(), document.UserCode);
    }

    private static string AllocateUniqueVar(string desired, HashSet<string> used, string fallbackPrefix)
    {
        string baseName = string.IsNullOrWhiteSpace(desired)
            ? fallbackPrefix
            : RoslynCsxHelpers.SanitizeIdentifier(desired);
        string candidate = baseName;
        int i = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName}_{i}";
            i++;
        }

        return candidate;
    }

    private static string FormatOutAnchor(string anchorId, HashSet<string> declaredAnchors)
    {
        return declaredAnchors.Add(anchorId) ? $"out var {anchorId}" : $"out {anchorId}";
    }

    private static void EmitSplineNode(
        StringBuilder sb,
        SplinePlacementNode spline,
        string factoryVar,
        bool declareFactory,
        string builtVar,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars,
        HashSet<string> declaredAnchors)
    {
        if (!string.IsNullOrWhiteSpace(spline.DisplayName))
        {
            sb.Append(RoslynCsxHelpers.FormatDisplayDirective("    ", spline.DisplayName));
        }

        sb.Append(declareFactory ? "    var " : "    ").Append(factoryVar).Append(" = ");
        switch (spline.Origin.Kind)
        {
            case SplineOriginKind.ChunkPose:
                sb.Append("Chunks[").Append(spline.Origin.Chunk.X).Append(", ").Append(spline.Origin.Chunk.Z).Append("].BeginSpline(")
                    .Append(RoslynCsxHelpers.FormatNullableStringLiteral(spline.TemplateKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatPoseArgs(spline.Origin.Pose)).Append(')');
                break;

            case SplineOriginKind.FromSplineOutlet:
                string srcSplineVar = splineBuiltVars.TryGetValue(spline.Origin.SourceNodeId ?? string.Empty, out string? sv)
                    ? sv
                    : RoslynCsxHelpers.SanitizeIdentifier(spline.Origin.SourceNodeId ?? "spline");
                sb.Append(srcSplineVar).Append(".IntoSpline(")
                    .Append(RoslynCsxHelpers.FormatNullableStringLiteral(spline.TemplateKey)).Append(')');
                break;

            case SplineOriginKind.FromJunctionPort:
                string srcJunVar = junctionBuiltVars.TryGetValue(spline.Origin.SourceNodeId ?? string.Empty, out string? jv)
                    ? jv
                    : RoslynCsxHelpers.SanitizeIdentifier(spline.Origin.SourceNodeId ?? "junction");
                sb.Append(srcJunVar).Append(".IntoSpline(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(spline.Origin.SourcePortKey ?? string.Empty)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatNullableStringLiteral(spline.TemplateKey)).Append(')');
                break;
        }

        sb.Append(";\n");

        if (!string.IsNullOrEmpty(spline.Key))
        {
            sb.Append("    ").Append(factoryVar).Append(".Key = ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(spline.Key)).Append(";\n");
        }

        if (0 < spline.Curves.Count)
        {
            sb.Append("    ").Append(factoryVar).Append(".Curves");
            foreach (SplineCurveSegment seg in spline.Curves)
            {
                sb.Append("\n        .");
                switch (seg.Kind)
                {
                    case SplineCurveSegmentKind.Straight:
                        sb.Append("Straight(").Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                        break;
                    case SplineCurveSegmentKind.ByRadius:
                        sb.Append("ByRadius(").Append(RoslynCsxHelpers.FormatNumber(seg.Value)).Append(", ")
                            .Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                        break;
                    case SplineCurveSegmentKind.ByCurvature:
                        sb.Append("ByCurvature(").Append(RoslynCsxHelpers.FormatNumber(seg.Value)).Append(", ")
                            .Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                        break;
                }

                if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                {
                    sb.Append(", ").Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                }

                sb.Append(')');
            }

            sb.Append(";\n");
        }

        if (0 < spline.Gradients.Count)
        {
            sb.Append("    ").Append(factoryVar).Append(".Gradients");
            foreach (SplineGradientSegment seg in spline.Gradients)
            {
                sb.Append("\n        .");
                if (seg.Kind == SplineGradientSegmentKind.Constant)
                {
                    sb.Append("Constant(").Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                }
                else
                {
                    sb.Append(seg.Kind.ToString()).Append('(')
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Delta)).Append(", ")
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                }

                if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                {
                    sb.Append(", ").Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                }

                sb.Append(')');
            }

            sb.Append(";\n");
        }

        if (0 < spline.Cants.Count)
        {
            sb.Append("    ").Append(factoryVar).Append(".Cants");
            foreach (SplineCantSegment seg in spline.Cants)
            {
                sb.Append("\n        .");
                if (seg.Kind == SplineCantSegmentKind.Constant)
                {
                    sb.Append("Constant(").Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                }
                else if (seg.Kind == SplineCantSegmentKind.TransitionToHeight)
                {
                    sb.Append("TransitionToHeight(")
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Target)).Append(", ")
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Gauge)).Append(", ")
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                }
                else
                {
                    sb.Append(seg.Kind.ToString()).Append('(')
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Target)).Append(", ")
                        .Append(RoslynCsxHelpers.FormatNumber(seg.Length));
                }

                if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                {
                    sb.Append(", ").Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                }

                sb.Append(')');
            }

            sb.Append(";\n");
        }

        if (spline.ConnectBezier is not null)
        {
            SplineConnectBezierModel cb = spline.ConnectBezier;
            sb.Append("    ").Append(factoryVar).Append(".ConnectBezier(")
                .Append(FormatPortReference(cb.TargetPort, splineBuiltVars, junctionBuiltVars));
            if (!string.IsNullOrEmpty(cb.OutputAnchorId))
            {
                sb.Append(", ").Append(FormatOutAnchor(cb.OutputAnchorId, declaredAnchors));
            }

            if (cb.HandleScale != 0.5)
            {
                sb.Append(", ").Append(RoslynCsxHelpers.FormatNumber(cb.HandleScale));
            }

            sb.Append(");\n");
        }

        foreach (SplineSkipRule skip in spline.ExtrusionSkips)
        {
            EmitSkip(sb, factoryVar, "SkipExtrusion", skip);
        }

        foreach (SplineSkipRule skip in spline.PropSkips)
        {
            EmitSkip(sb, factoryVar, "SkipProp", skip);
        }

        foreach (CurveExtrusionPlacementRule ext in spline.Extrusions)
        {
            sb.Append(CurveRuleCsxCodec.FormatCurveExtrusion("    ", factoryVar, ext));
        }

        foreach (SplineInstancePropRule prop in spline.Props)
        {
            sb.Append("    ").Append(factoryVar).Append(".PutProp(");
            if (prop.Key is not null)
            {
                sb.Append(RoslynCsxHelpers.FormatStringLiteral(prop.Key)).Append(", ");
            }

            sb.Append(RoslynCsxHelpers.FormatStringArray(prop.ModelKeys)).Append(", ")
                .Append(RoslynCsxHelpers.FormatPoseArgs(prop.Pose)).Append(", ")
                .Append(DistanceExprCodec.Format(prop.From)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(prop.Span)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(prop.Interval)).Append(", ")
                .Append(prop.Count == int.MaxValue ? "int.MaxValue" : prop.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(");\n");
        }

        EmitPreBuildAndBuild(sb, spline.PreBuildUserCode, builtVar, factoryVar);
    }

    private static void EmitJunctionNode(
        StringBuilder sb,
        JunctionPlacementNode junction,
        string factoryVar,
        bool declareFactory,
        string builtVar,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars)
    {
        if (!string.IsNullOrWhiteSpace(junction.DisplayName))
        {
            sb.Append(RoslynCsxHelpers.FormatDisplayDirective("    ", junction.DisplayName));
        }

        sb.Append(declareFactory ? "    var " : "    ").Append(factoryVar).Append(" = ");
        switch (junction.Origin.Kind)
        {
            case JunctionOriginKind.ChunkPose:
                sb.Append("Chunks[").Append(junction.Origin.Chunk.X).Append(", ").Append(junction.Origin.Chunk.Z).Append("].PutJunction(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.TemplateKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatPoseArgs(junction.Origin.Pose)).Append(')');
                break;

            case JunctionOriginKind.FromSplineOutlet:
                string srcSplineVar = splineBuiltVars.TryGetValue(junction.Origin.SourceNodeId ?? string.Empty, out string? sv)
                    ? sv
                    : RoslynCsxHelpers.SanitizeIdentifier(junction.Origin.SourceNodeId ?? "spline");
                sb.Append(srcSplineVar).Append(".IntoJunction(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.TemplateKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.Origin.TargetPortKey ?? string.Empty)).Append(')');
                break;

            case JunctionOriginKind.FromJunctionPort:
                string srcJunVar = junctionBuiltVars.TryGetValue(junction.Origin.SourceNodeId ?? string.Empty, out string? jv)
                    ? jv
                    : RoslynCsxHelpers.SanitizeIdentifier(junction.Origin.SourceNodeId ?? "junction");
                sb.Append(srcJunVar).Append(".IntoJunction(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.Origin.SourcePortKey ?? string.Empty)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.TemplateKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(junction.Origin.TargetPortKey ?? string.Empty)).Append(')');
                break;
        }

        sb.Append(";\n");

        if (!string.IsNullOrEmpty(junction.Key))
        {
            sb.Append("    ").Append(factoryVar).Append(".Key = ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(junction.Key)).Append(";\n");
        }

        foreach (JunctionStaticPropPlacement prop in junction.Props)
        {
            sb.Append("    ").Append(factoryVar).Append(".PutProp(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(prop.ModelKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatPoseArgs(prop.Pose)).Append(");\n");
        }

        foreach (JunctionPathOverrideModel pathOverride in junction.PathOverrides)
        {
            string targetExpr = $"{factoryVar}.Paths[{RoslynCsxHelpers.FormatStringLiteral(pathOverride.PathKey)}]";
            if (pathOverride.TrafficDensityFactor.HasValue)
            {
                sb.Append("    ").Append(targetExpr).Append(".TrafficDensity(")
                    .Append(RoslynCsxHelpers.FormatNumber(pathOverride.TrafficDensityFactor.Value)).Append(");\n");
            }

            foreach (CurveExtrusionPlacementRule ext in pathOverride.Extrusions)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveExtrusion("    ", targetExpr, ext));
            }

            foreach (CurvePropPlacementRule prop in pathOverride.Props)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveProp("    ", targetExpr, prop));
            }
        }

        foreach (JunctionCurveOverrideModel curveOverride in junction.CurveOverrides)
        {
            string targetExpr = $"{factoryVar}.Curves[{RoslynCsxHelpers.FormatStringLiteral(curveOverride.CurveKey)}]";
            foreach (CurveExtrusionPlacementRule ext in curveOverride.Extrusions)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveExtrusion("    ", targetExpr, ext));
            }

            foreach (CurvePropPlacementRule prop in curveOverride.Props)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveProp("    ", targetExpr, prop));
            }
        }

        EmitPreBuildAndBuild(sb, junction.PreBuildUserCode, builtVar, factoryVar);
    }

    private static void EmitPreBuildAndBuild(StringBuilder sb, string? preBuildCode, string builtVar, string factoryVar)
    {
        if (preBuildCode is not null)
        {
            sb.Append("    // <user-prebuild>\n");
            RoslynCsxHelpers.AppendIndentedUserBlock(sb, preBuildCode);
            sb.Append("    // </user-prebuild>\n");
        }

        sb.Append("    var ").Append(builtVar).Append(" = ").Append(factoryVar).Append(".Build();\n");
    }

    private static void EmitSkip(StringBuilder sb, string factoryVar, string method, SplineSkipRule skip)
    {
        sb.Append("    ").Append(factoryVar).Append('.').Append(method).Append('(')
            .Append(RoslynCsxHelpers.FormatStringLiteral(skip.Key));
        if (skip.From.HasValue && skip.To.HasValue)
        {
            sb.Append(", ").Append(DistanceExprCodec.Format(skip.From.Value))
                .Append(", ").Append(DistanceExprCodec.Format(skip.To.Value));
        }

        sb.Append(");\n");
    }

    private static string FormatPortReference(
        PortReferenceModel portRef,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars)
    {
        if (portRef.Kind == PortTargetKind.SplineInlet || portRef.Kind == PortTargetKind.SplineOutlet)
        {
            string sVar = splineBuiltVars.TryGetValue(portRef.ElementId, out string? resolved)
                ? resolved
                : RoslynCsxHelpers.SanitizeIdentifier(portRef.ElementId);
            return portRef.Kind == PortTargetKind.SplineInlet ? $"{sVar}.Inlet" : $"{sVar}.Outlet";
        }

        string jVar = junctionBuiltVars.TryGetValue(portRef.ElementId, out string? jResolved)
            ? jResolved
            : RoslynCsxHelpers.SanitizeIdentifier(portRef.ElementId);
        return $"{jVar}.Junction.Ports[{RoslynCsxHelpers.FormatStringLiteral(portRef.PortKey ?? string.Empty)}]";
    }

    private static string? ResolvePreBuildForBuildStatement(
        IReadOnlyList<int> attachedIndices,
        IReadOnlyDictionary<int, CsxPreBuildBlock> preBuildByIndex,
        HashSet<int> boundIndices,
        string filePath,
        List<ParseDiagnostic> diagnostics)
    {
        if (attachedIndices.Count == 0)
        {
            return null;
        }

        for (int i = 0; i < attachedIndices.Count - 1; i++)
        {
            if (preBuildByIndex.TryGetValue(attachedIndices[i], out CsxPreBuildBlock? extraBlock))
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    extraBlock.MarkerStartLine,
                    1,
                    "1 つの .Build() に対して複数の '// <user-prebuild>' ブロックが連続しています。"));
            }
        }

        int lastIdx = attachedIndices[^1];
        boundIndices.Add(lastIdx);
        return preBuildByIndex.TryGetValue(lastIdx, out CsxPreBuildBlock? boundBlock) ? boundBlock.RawContent : null;
    }

    private static void ReportOrphanPreBuilds(
        IReadOnlyList<int> attachedIndices,
        IReadOnlyDictionary<int, CsxPreBuildBlock> preBuildByIndex,
        string filePath,
        List<ParseDiagnostic> diagnostics)
    {
        foreach (int idx in attachedIndices)
        {
            if (preBuildByIndex.TryGetValue(idx, out CsxPreBuildBlock? block))
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    block.MarkerStartLine,
                    1,
                    "'// <user-prebuild>' ブロックの直後には 'var ... = <factory>.Build();' が必要です。"));
            }
        }
    }

    private static bool TryParseBuildStatement(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, PendingFactoryNode> activeFactories,
        out PendingFactoryNode? factory)
    {
        factory = null;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma
            || ma.Name.Identifier.ValueText != "Build"
            || invocation.ArgumentList.Arguments.Count != 0
            || ma.Expression is not IdentifierNameSyntax ident)
        {
            return false;
        }

        return activeFactories.TryGetValue(ident.Identifier.ValueText, out factory);
    }

    private static bool TryParseFactoryStart(
        InvocationExpressionSyntax inv,
        string factoryVarName,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars,
        out PendingFactoryNode? factory)
    {
        factory = null;
        if (inv.Expression is not MemberAccessExpressionSyntax ma)
        {
            return false;
        }

        string method = ma.Name.Identifier.ValueText;
        SeparatedSyntaxList<ArgumentSyntax> args = inv.ArgumentList.Arguments;

        if (method == "BeginSpline" && TryParseChunksIndexer(ma.Expression, out ChunkIndexModel splineChunk))
        {
            if (!TryParseBeginSplineArgs(args, out string? templateKey, out PoseDegreesModel pose))
            {
                return false;
            }

            SplinePlacementNode node = new SplinePlacementNode
            {
                Origin = SplineOriginModel.FromChunk(splineChunk, pose),
                TemplateKey = templateKey,
            };
            factory = new PendingSplineFactory(factoryVarName, node);
            return true;
        }

        if (method == "IntoSpline" && ma.Expression is IdentifierNameSyntax srcSplineOrJunctionIdent)
        {
            string srcVar = srcSplineOrJunctionIdent.Identifier.ValueText;
            if (args.Count == 1
                && splineBuiltVars.TryGetValue(srcVar, out string? srcSplineId)
                && RoslynCsxHelpers.TryGetNullableString(args[0].Expression, out string? splineTpl1))
            {
                SplinePlacementNode node = new SplinePlacementNode
                {
                    Origin = SplineOriginModel.FromSpline(srcSplineId),
                    TemplateKey = splineTpl1,
                };
                factory = new PendingSplineFactory(factoryVarName, node);
                return true;
            }

            if (args.Count == 2
                && junctionBuiltVars.TryGetValue(srcVar, out string? srcJunId)
                && RoslynCsxHelpers.TryGetString(args[0].Expression, out string portKey)
                && RoslynCsxHelpers.TryGetNullableString(args[1].Expression, out string? splineTpl2))
            {
                SplinePlacementNode node = new SplinePlacementNode
                {
                    Origin = SplineOriginModel.FromJunction(srcJunId, portKey),
                    TemplateKey = splineTpl2,
                };
                factory = new PendingSplineFactory(factoryVarName, node);
                return true;
            }
        }

        if (method == "PutJunction" && TryParseChunksIndexer(ma.Expression, out ChunkIndexModel junChunk))
        {
            if ((args.Count != 4 && args.Count != 7)
                || !RoslynCsxHelpers.TryGetString(args[0].Expression, out string tplKey)
                || !TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
            {
                return false;
            }

            JunctionPlacementNode node = new JunctionPlacementNode
            {
                TemplateKey = tplKey,
                Origin = JunctionOriginModel.FromChunk(junChunk, pose),
            };
            factory = new PendingJunctionFactory(factoryVarName, node);
            return true;
        }

        if (method == "IntoJunction" && ma.Expression is IdentifierNameSyntax srcIdent)
        {
            string srcVar = srcIdent.Identifier.ValueText;
            if (args.Count == 2
                && splineBuiltVars.TryGetValue(srcVar, out string? srcSplineId)
                && RoslynCsxHelpers.TryGetString(args[0].Expression, out string tplKey1)
                && RoslynCsxHelpers.TryGetString(args[1].Expression, out string targetPortKey1))
            {
                JunctionPlacementNode node = new JunctionPlacementNode
                {
                    TemplateKey = tplKey1,
                    Origin = JunctionOriginModel.FromSpline(srcSplineId, targetPortKey1),
                };
                factory = new PendingJunctionFactory(factoryVarName, node);
                return true;
            }

            if (args.Count == 3
                && junctionBuiltVars.TryGetValue(srcVar, out string? srcJunId)
                && RoslynCsxHelpers.TryGetString(args[0].Expression, out string srcPortKey)
                && RoslynCsxHelpers.TryGetString(args[1].Expression, out string tplKey2)
                && RoslynCsxHelpers.TryGetString(args[2].Expression, out string targetPortKey2))
            {
                JunctionPlacementNode node = new JunctionPlacementNode
                {
                    TemplateKey = tplKey2,
                    Origin = JunctionOriginModel.FromJunction(srcJunId, srcPortKey, targetPortKey2),
                };
                factory = new PendingJunctionFactory(factoryVarName, node);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBeginSplineArgs(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out string? templateKey,
        out PoseDegreesModel pose)
    {
        templateKey = null;
        pose = PoseDegreesModel.Identity;

        if (args.Count == 3 || args.Count == 6)
        {
            return TryReadPose3Or6(args, 0, out pose);
        }

        if ((args.Count == 4 || args.Count == 7)
            && RoslynCsxHelpers.TryGetNullableString(args[0].Expression, out templateKey))
        {
            return TryReadPose3Or6(args, 1, out pose);
        }

        return false;
    }

    private static bool TryReadPose3Or6(SeparatedSyntaxList<ArgumentSyntax> args, int startIdx, out PoseDegreesModel pose)
    {
        pose = PoseDegreesModel.Identity;
        int rem = args.Count - startIdx;
        if (rem != 3 && rem != 6)
        {
            return false;
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[startIdx].Expression, out double x)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 1].Expression, out double y)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 2].Expression, out double z))
        {
            return false;
        }

        double rx = 0.0;
        double ry = 0.0;
        double rz = 0.0;
        if (rem == 6)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[startIdx + 3].Expression, out rx)
                || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 4].Expression, out ry)
                || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 5].Expression, out rz))
            {
                return false;
            }
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }

    private static bool TryParseChunksIndexer(ExpressionSyntax expr, out ChunkIndexModel chunk)
    {
        chunk = ChunkIndexModel.Zero;
        if (expr is not ElementAccessExpressionSyntax elemAccess
            || elemAccess.Expression is not IdentifierNameSyntax ident
            || ident.Identifier.ValueText != "Chunks"
            || elemAccess.ArgumentList.Arguments.Count != 2)
        {
            return false;
        }

        if (!RoslynCsxHelpers.TryGetInt(elemAccess.ArgumentList.Arguments[0].Expression, out int cx)
            || !RoslynCsxHelpers.TryGetInt(elemAccess.ArgumentList.Arguments[1].Expression, out int cz))
        {
            return false;
        }

        chunk = new ChunkIndexModel(cx, cz);
        return true;
    }

    private static void SetFactoryDisplayName(PendingFactoryNode factory, string? displayName, bool onlyIfUnset = false)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        if (factory is PendingSplineFactory ps)
        {
            if (!onlyIfUnset || string.IsNullOrWhiteSpace(ps.Node.DisplayName))
            {
                ps.Node.DisplayName = displayName;
            }
        }
        else if (factory is PendingJunctionFactory pj)
        {
            if (!onlyIfUnset || string.IsNullOrWhiteSpace(pj.Node.DisplayName))
            {
                pj.Node.DisplayName = displayName;
            }
        }
    }

    private static bool TryParseExpressionStatement(
        ExpressionSyntax expr,
        string? displayName,
        WorldPlacementDocument doc,
        IReadOnlyDictionary<string, PendingFactoryNode> activeFactories,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars,
        ref int propCounter)
    {
        if (expr is AssignmentExpressionSyntax assign
            && assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign.Left is MemberAccessExpressionSyntax leftMa
            && leftMa.Name.Identifier.ValueText == "Key"
            && leftMa.Expression is IdentifierNameSyntax targetVar
            && activeFactories.TryGetValue(targetVar.Identifier.ValueText, out PendingFactoryNode? factory)
            && RoslynCsxHelpers.TryGetNullableString(assign.Right, out string? assignedKey))
        {
            if (factory is PendingSplineFactory ps)
            {
                ps.Node.Key = assignedKey;
                return true;
            }

            if (factory is PendingJunctionFactory pj)
            {
                pj.Node.Key = assignedKey;
                return true;
            }
        }

        if (expr is not InvocationExpressionSyntax inv)
        {
            return false;
        }

        (ExpressionSyntax rootReceiver, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(inv);
        if (calls.Count == 0)
        {
            return false;
        }

        if (calls.Count == 1
            && rootReceiver is IdentifierNameSyntax rootGlobal
            && TryParseGlobalInvocation(rootGlobal.Identifier.ValueText, calls[0], doc, splineBuiltVars, junctionBuiltVars))
        {
            return true;
        }

        if (calls.Count == 1 && TryParseChunksIndexer(rootReceiver, out ChunkIndexModel chunk))
        {
            InvocationExpressionSyntax call = calls[0];
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;
            if (RoslynCsxHelpers.GetInvokedMethodName(call) == "PutProp"
                && (args.Count == 4 || args.Count == 7)
                && RoslynCsxHelpers.TryGetString(args[0].Expression, out string modelKey)
                && TryReadPose3Or6(args, 1, out PoseDegreesModel pose))
            {
                propCounter++;
                doc.Props.Add(new ChunkPropPlacement($"prop_{propCounter:D3}", modelKey, chunk, pose, displayName));
                return true;
            }

            return false;
        }

        if (rootReceiver is IdentifierNameSyntax factoryIdent
            && activeFactories.TryGetValue(factoryIdent.Identifier.ValueText, out PendingFactoryNode? fNode))
        {
            if (fNode is PendingSplineFactory psf && calls.Count == 1)
            {
                return ApplySplineFactorySingleCall(calls[0], psf.Node, splineBuiltVars, junctionBuiltVars);
            }

            if (fNode is PendingJunctionFactory pjf && calls.Count == 1)
            {
                return ApplyJunctionFactorySingleCall(calls[0], pjf.Node);
            }

            return false;
        }

        if (rootReceiver is MemberAccessExpressionSyntax timelineAccess
            && timelineAccess.Expression is IdentifierNameSyntax tlSplineIdent
            && activeFactories.TryGetValue(tlSplineIdent.Identifier.ValueText, out PendingFactoryNode? tlFactory)
            && tlFactory is PendingSplineFactory tlSpline)
        {
            string tlName = timelineAccess.Name.Identifier.ValueText;
            if (tlName == "Curves")
            {
                return ApplySplineCurveCalls(calls, tlSpline.Node);
            }

            if (tlName == "Gradients")
            {
                return ApplySplineGradientCalls(calls, tlSpline.Node);
            }

            if (tlName == "Cants")
            {
                return ApplySplineCantCalls(calls, tlSpline.Node);
            }

            return false;
        }

        if (rootReceiver is ElementAccessExpressionSyntax elementOverride
            && elementOverride.ArgumentList.Arguments.Count == 1
            && RoslynCsxHelpers.TryGetString(elementOverride.ArgumentList.Arguments[0].Expression, out string subKey)
            && elementOverride.Expression is MemberAccessExpressionSyntax collectionAccess
            && collectionAccess.Expression is IdentifierNameSyntax jFactoryIdent
            && activeFactories.TryGetValue(jFactoryIdent.Identifier.ValueText, out PendingFactoryNode? jNode)
            && jNode is PendingJunctionFactory pJunction)
        {
            string collName = collectionAccess.Name.Identifier.ValueText;
            if (collName == "Paths")
            {
                return ApplyJunctionPathOverrideCalls(subKey, calls, pJunction.Node);
            }

            if (collName == "Curves")
            {
                return ApplyJunctionCurveOverrideCalls(subKey, calls, pJunction.Node);
            }
        }

        return false;
    }

    private static bool TryParseGlobalInvocation(
        string receiverName,
        InvocationExpressionSyntax call,
        WorldPlacementDocument doc,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        switch (receiverName)
        {
            case "Camera":
                if (method == "Locate"
                    && args.Count == 8
                    && RoslynCsxHelpers.TryGetInt(args[0].Expression, out int camCx)
                    && RoslynCsxHelpers.TryGetInt(args[1].Expression, out int camCz)
                    && CurveRuleCsxCodec.TryReadPose6(args, 2, out PoseDegreesModel camPose))
                {
                    doc.Camera.Pose = new WorldPoseDegreesModel(new ChunkIndexModel(camCx, camCz), camPose);
                    return true;
                }
                return false;

            case "Avatar":
                if (method == "Load" && args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string avatarPath))
                {
                    doc.Avatar ??= new AvatarSettingsModel();
                    doc.Avatar.XmlPath = avatarPath;
                    return true;
                }

                if (method == "Locate"
                    && (args.Count == 5 || args.Count == 8)
                    && RoslynCsxHelpers.TryGetInt(args[0].Expression, out int avCx)
                    && RoslynCsxHelpers.TryGetInt(args[1].Expression, out int avCz)
                    && TryReadPose3Or6(args, 2, out PoseDegreesModel avPose))
                {
                    doc.Avatar ??= new AvatarSettingsModel();
                    doc.Avatar.InitialPose = new WorldPoseDegreesModel(new ChunkIndexModel(avCx, avCz), avPose);
                    return true;
                }
                return false;

            case "Environment":
                if (method == "SetDefault" && args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string envPath))
                {
                    doc.EnvironmentPath = envPath;
                    return true;
                }
                return false;

            case "DirectionalLight":
                if (method == "SetColor" && args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string colorHex))
                {
                    doc.DirectionalLight.ColorHex = colorHex;
                    return true;
                }

                if (method == "SetDirection"
                    && args.Count == 3
                    && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double dx)
                    && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double dy)
                    && RoslynCsxHelpers.TryGetDouble(args[2].Expression, out double dz))
                {
                    doc.DirectionalLight.DirectionX = dx;
                    doc.DirectionalLight.DirectionY = dy;
                    doc.DirectionalLight.DirectionZ = dz;
                    return true;
                }

                if (method == "SetIntensity" && args.Count == 1 && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double intensity))
                {
                    doc.DirectionalLight.Intensity = intensity;
                    return true;
                }
                return false;

            case "Background":
                if (method == "Add" && args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string bgKey))
                {
                    doc.BackgroundModels.Add(bgKey);
                    return true;
                }
                return false;

            case "Network":
                if (method == "Connect"
                    && args.Count == 2
                    && TryParsePortReference(args[0].Expression, splineBuiltVars, junctionBuiltVars, out PortReferenceModel? portA)
                    && TryParsePortReference(args[1].Expression, splineBuiltVars, junctionBuiltVars, out PortReferenceModel? portB))
                {
                    doc.Connections.Add(new ExplicitPortConnection(portA!, portB!));
                    return true;
                }
                return false;
        }

        return false;
    }

    private static bool TryParsePortReference(
        ExpressionSyntax expr,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars,
        out PortReferenceModel? portRef)
    {
        portRef = null;

        if (expr is MemberAccessExpressionSyntax ma && ma.Expression is IdentifierNameSyntax sIdent)
        {
            if (splineBuiltVars.TryGetValue(sIdent.Identifier.ValueText, out string? splineId))
            {
                string prop = ma.Name.Identifier.ValueText;
                if (prop == "Inlet")
                {
                    portRef = new PortReferenceModel(splineId, PortTargetKind.SplineInlet);
                    return true;
                }

                if (prop == "Outlet")
                {
                    portRef = new PortReferenceModel(splineId, PortTargetKind.SplineOutlet);
                    return true;
                }
            }
        }

        if (expr is ElementAccessExpressionSyntax elem
            && elem.ArgumentList.Arguments.Count == 1
            && RoslynCsxHelpers.TryGetString(elem.ArgumentList.Arguments[0].Expression, out string portKey)
            && elem.Expression is MemberAccessExpressionSyntax portsAccess
            && portsAccess.Name.Identifier.ValueText == "Ports"
            && portsAccess.Expression is MemberAccessExpressionSyntax junctionProp
            && junctionProp.Name.Identifier.ValueText == "Junction"
            && junctionProp.Expression is IdentifierNameSyntax jIdent
            && junctionBuiltVars.TryGetValue(jIdent.Identifier.ValueText, out string? junctionId))
        {
            portRef = new PortReferenceModel(junctionId, PortTargetKind.JunctionPort, portKey);
            return true;
        }

        return false;
    }

    private static bool ApplySplineFactorySingleCall(
        InvocationExpressionSyntax call,
        SplinePlacementNode node,
        IReadOnlyDictionary<string, string> splineBuiltVars,
        IReadOnlyDictionary<string, string> junctionBuiltVars)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "ConnectBezier")
        {
            if (args.Count < 1 || 3 < args.Count)
            {
                return false;
            }

            if (!TryParsePortReference(args[0].Expression, splineBuiltVars, junctionBuiltVars, out PortReferenceModel? targetPort))
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;

            if (args.Count == 2)
            {
                if (RoslynCsxHelpers.IsOutArgument(args[1], out anchorId))
                {
                }
                else if (!RoslynCsxHelpers.TryGetDouble(args[1].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 3)
            {
                if (!RoslynCsxHelpers.IsOutArgument(args[1], out anchorId)
                    || !RoslynCsxHelpers.TryGetDouble(args[2].Expression, out handleScale))
                {
                    return false;
                }
            }

            node.ConnectBezier = new SplineConnectBezierModel(targetPort!, handleScale, anchorId);
            return true;
        }

        if (method == "PutProp")
        {
            string? key = null;
            int bodyStartIdx = 0;
            if (2 <= args.Count
                && RoslynCsxHelpers.TryGetNullableString(args[0].Expression, out string? maybeKey)
                && RoslynCsxHelpers.TryGetStringList(args[1].Expression, out _))
            {
                key = maybeKey;
                bodyStartIdx = 1;
            }

            if (!CurveRuleCsxCodec.TryParsePropBody(
                args,
                bodyStartIdx,
                out List<string> models,
                out PoseDegreesModel pose,
                out AnchoredDistance from,
                out double span,
                out double interval,
                out int count))
            {
                return false;
            }

            SplineInstancePropRule rule = new SplineInstancePropRule
            {
                Key = key,
                Pose = pose,
                From = from,
                Span = span,
                Interval = interval,
                Count = count,
            };
            rule.ModelKeys.AddRange(models);
            node.Props.Add(rule);
            return true;
        }

        if (method == "SkipProp" && TryParseSkipRule(args, out SplineSkipRule? propSkip))
        {
            node.PropSkips.Add(propSkip!);
            return true;
        }

        if (method == "PutExtrusion" && CurveRuleCsxCodec.TryParseCurveExtrusion(args, out CurveExtrusionPlacementRule? extRule))
        {
            node.Extrusions.Add(extRule!);
            return true;
        }

        if (method == "SkipExtrusion" && TryParseSkipRule(args, out SplineSkipRule? extSkip))
        {
            node.ExtrusionSkips.Add(extSkip!);
            return true;
        }

        return false;
    }

    private static bool TryParseSkipRule(SeparatedSyntaxList<ArgumentSyntax> args, out SplineSkipRule? skip)
    {
        skip = null;
        if (args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string keyOnly))
        {
            skip = new SplineSkipRule(keyOnly, null, null);
            return true;
        }

        if (args.Count == 3
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string key)
            && RoslynCsxHelpers.TryParseAnchoredDistance(args[1], out AnchoredDistance from, out bool fromMax)
            && !fromMax
            && RoslynCsxHelpers.TryParseAnchoredDistance(args[2], out AnchoredDistance to, out _))
        {
            skip = new SplineSkipRule(key, from, to);
            return true;
        }

        return false;
    }

    private static bool ApplySplineCurveCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Straight" && (args.Count == 1 || args.Count == 2)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double len))
            {
                string? anchor = null;
                if (args.Count == 2 && !RoslynCsxHelpers.IsOutArgument(args[1], out anchor))
                {
                    return false;
                }

                node.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, len, anchor));
                continue;
            }

            if ((method == "ByRadius" || method == "ByCurvature") && (args.Count == 2 || args.Count == 3)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double val)
                && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double arcLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !RoslynCsxHelpers.IsOutArgument(args[2], out anchor))
                {
                    return false;
                }

                SplineCurveSegmentKind kind = method == "ByRadius"
                    ? SplineCurveSegmentKind.ByRadius
                    : SplineCurveSegmentKind.ByCurvature;
                node.Curves.Add(new SplineCurveSegment(kind, val, arcLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ApplySplineGradientCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Constant" && (args.Count == 1 || args.Count == 2)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double cLen))
            {
                string? anchor = null;
                if (args.Count == 2 && !RoslynCsxHelpers.IsOutArgument(args[1], out anchor))
                {
                    return false;
                }

                node.Gradients.Add(new SplineGradientSegment(SplineGradientSegmentKind.Constant, 0.0, cLen, anchor));
                continue;
            }

            SplineGradientSegmentKind? kind = method switch
            {
                "TransitionByRadian" => SplineGradientSegmentKind.TransitionByRadian,
                "TransitionByDegree" => SplineGradientSegmentKind.TransitionByDegree,
                "TransitionByPercent" => SplineGradientSegmentKind.TransitionByPercent,
                "TransitionByPermil" => SplineGradientSegmentKind.TransitionByPermil,
                _ => null,
            };

            if (kind.HasValue && (args.Count == 2 || args.Count == 3)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double delta)
                && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double tLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !RoslynCsxHelpers.IsOutArgument(args[2], out anchor))
                {
                    return false;
                }

                node.Gradients.Add(new SplineGradientSegment(kind.Value, delta, tLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ApplySplineCantCalls(IReadOnlyList<InvocationExpressionSyntax> calls, SplinePlacementNode node)
    {
        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "Constant" && (args.Count == 1 || args.Count == 2)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double cLen))
            {
                string? anchor = null;
                if (args.Count == 2 && !RoslynCsxHelpers.IsOutArgument(args[1], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.Constant, 0.0, 0.0, cLen, anchor));
                continue;
            }

            if (method == "TransitionToHeight" && (args.Count == 3 || args.Count == 4)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double height)
                && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double gauge)
                && RoslynCsxHelpers.TryGetDouble(args[2].Expression, out double hLen))
            {
                string? anchor = null;
                if (args.Count == 4 && !RoslynCsxHelpers.IsOutArgument(args[3], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.TransitionToHeight, height, gauge, hLen, anchor));
                continue;
            }

            SplineCantSegmentKind? kind = method switch
            {
                "TransitionToRadian" => SplineCantSegmentKind.TransitionToRadian,
                "TransitionToDegree" => SplineCantSegmentKind.TransitionToDegree,
                "TransitionToPercent" => SplineCantSegmentKind.TransitionToPercent,
                _ => null,
            };

            if (kind.HasValue && (args.Count == 2 || args.Count == 3)
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double target)
                && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double tLen))
            {
                string? anchor = null;
                if (args.Count == 3 && !RoslynCsxHelpers.IsOutArgument(args[2], out anchor))
                {
                    return false;
                }

                node.Cants.Add(new SplineCantSegment(kind.Value, target, 0.0, tLen, anchor));
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ApplyJunctionFactorySingleCall(InvocationExpressionSyntax call, JunctionPlacementNode node)
    {
        if (RoslynCsxHelpers.GetInvokedMethodName(call) == "PutProp"
            && (call.ArgumentList.Arguments.Count == 4 || call.ArgumentList.Arguments.Count == 7)
            && RoslynCsxHelpers.TryGetString(call.ArgumentList.Arguments[0].Expression, out string modelKey)
            && TryReadPose3Or6(call.ArgumentList.Arguments, 1, out PoseDegreesModel pose))
        {
            node.Props.Add(new JunctionStaticPropPlacement(modelKey, pose));
            return true;
        }

        return false;
    }

    private static bool ApplyJunctionPathOverrideCalls(
        string pathKey,
        IReadOnlyList<InvocationExpressionSyntax> calls,
        JunctionPlacementNode node)
    {
        JunctionPathOverrideModel? ov = node.PathOverrides.FirstOrDefault(p => string.Equals(p.PathKey, pathKey, StringComparison.Ordinal));
        if (ov is null)
        {
            ov = new JunctionPathOverrideModel { PathKey = pathKey };
            node.PathOverrides.Add(ov);
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "TrafficDensity" && args.Count == 1
                && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double factor))
            {
                ov.TrafficDensityFactor = factor;
                continue;
            }

            if (method == "PutProp" && CurveRuleCsxCodec.TryParseCurveProp(args, out CurvePropPlacementRule? prop))
            {
                ov.Props.Add(prop!);
                continue;
            }

            if (method == "PutExtrusion" && CurveRuleCsxCodec.TryParseCurveExtrusion(args, out CurveExtrusionPlacementRule? ext))
            {
                ov.Extrusions.Add(ext!);
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ApplyJunctionCurveOverrideCalls(
        string curveKey,
        IReadOnlyList<InvocationExpressionSyntax> calls,
        JunctionPlacementNode node)
    {
        JunctionCurveOverrideModel? ov = node.CurveOverrides.FirstOrDefault(c => string.Equals(c.CurveKey, curveKey, StringComparison.Ordinal));
        if (ov is null)
        {
            ov = new JunctionCurveOverrideModel { CurveKey = curveKey };
            node.CurveOverrides.Add(ov);
        }

        foreach (InvocationExpressionSyntax call in calls)
        {
            string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
            SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

            if (method == "PutProp" && CurveRuleCsxCodec.TryParseCurveProp(args, out CurvePropPlacementRule? prop))
            {
                ov.Props.Add(prop!);
                continue;
            }

            if (method == "PutExtrusion" && CurveRuleCsxCodec.TryParseCurveExtrusion(args, out CurveExtrusionPlacementRule? ext))
            {
                ov.Extrusions.Add(ext!);
                continue;
            }

            return false;
        }

        return true;
    }
}
