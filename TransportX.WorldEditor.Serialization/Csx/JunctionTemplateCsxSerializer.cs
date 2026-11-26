using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Serialization.Diagnostics;
using DiagnosticSeverity = TransportX.WorldEditor.Serialization.Diagnostics.DiagnosticSeverity;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Parses and generates single-file junction template scripts (<c>Templates/Junctions/&lt;Key&gt;.csx</c>).
/// </summary>
public static class JunctionTemplateCsxSerializer
{
    public static (JunctionTemplateDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string filePath,
        string sourceText,
        string relativePath = "")
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sourceText);

        CsxSlicedFile sliced = CsxMarkerSlicer.Slice(filePath, sourceText);
        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>(sliced.Diagnostics);

        foreach (CsxPreBuildBlock orphan in sliced.PreBuildBlocks)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                orphan.MarkerStartLine,
                1,
                "ジャンクションテンプレート内では '// <user-prebuild>' を使用できません。"));
        }

        if (!RoslynCsxHelpers.TryParseEditorGeneratedBlock(
            filePath,
            sliced,
            diagnostics,
            out SyntaxList<StatementSyntax> statements,
            out _,
            out _))
        {
            return (null, diagnostics);
        }

        JunctionTemplateDocument doc = new JunctionTemplateDocument
        {
            RelativePath = relativePath,
            UserCode = sliced.UserCode,
        };

        string? junctionVarName = null;
        Dictionary<string, JunctionPathTemplateModel> pathVars = new Dictionary<string, JunctionPathTemplateModel>(StringComparer.Ordinal);
        Dictionary<string, JunctionCurveTemplateModel> curveVars = new Dictionary<string, JunctionCurveTemplateModel>(StringComparer.Ordinal);

        foreach (StatementSyntax statement in statements)
        {
            if (statement is EmptyStatementSyntax)
            {
                continue;
            }

            if (junctionVarName is null
                && statement is LocalDeclarationStatementSyntax createDecl
                && createDecl.Declaration.Variables.Count == 1
                && createDecl.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax createInv
                && TryParseCreateJunction(createInv, out string templateKey))
            {
                junctionVarName = createDecl.Declaration.Variables[0].Identifier.ValueText;
                doc.Key = templateKey;
                continue;
            }

            if (junctionVarName is not null
                && statement is LocalDeclarationStatementSyntax localDecl
                && localDecl.Declaration.Variables.Count == 1
                && localDecl.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax initInv)
            {
                string varName = localDecl.Declaration.Variables[0].Identifier.ValueText;
                if (TryParseWireOrAddCurve(junctionVarName, varName, initInv, doc, pathVars, curveVars))
                {
                    continue;
                }
            }

            if (junctionVarName is not null
                && statement is ExpressionStatementSyntax exprStmt
                && exprStmt.Expression is InvocationExpressionSyntax stmtInv)
            {
                if (TryParseStatementInvocation(junctionVarName, stmtInv, doc, pathVars, curveVars))
                {
                    continue;
                }
            }

            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                RoslynCsxHelpers.GetActualLine(sliced, statement),
                RoslynCsxHelpers.GetColumn(statement),
                $"JunctionTemplate の <editor-generated> 内で不正または未知のステートメントです: {statement}"));
        }

        if (junctionVarName is null && !RoslynCsxHelpers.HasError(diagnostics))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                sliced.EditorGeneratedStartLine,
                1,
                "Network.Templates.CreateJunction(...) 宣言が見つかりません。"));
        }

        return RoslynCsxHelpers.HasError(diagnostics) ? (null, diagnostics) : (doc, diagnostics);
    }

    public static string Generate(JunctionTemplateDocument document, string editorCsxLoadPath = "../../__Editor.csx")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editorCsxLoadPath);

        StringBuilder sb = new StringBuilder();
        HashSet<string> usedVarNames = new HashSet<string>(StringComparer.Ordinal) { "tJunction" };
        HashSet<string> declaredAnchors = new HashSet<string>(StringComparer.Ordinal);

        sb.Append("    var tJunction = Network.Templates.CreateJunction(")
            .Append(RoslynCsxHelpers.FormatStringLiteral(document.Key))
            .Append(");\n");

        foreach (JunctionPortDefinition port in document.Ports)
        {
            sb.Append("    tJunction.AddPort(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(port.Key)).Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(port.LayoutKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatPoseArgs(port.Pose))
                .Append(");\n");
        }

        foreach (JunctionRelayDefinition relay in document.Relays)
        {
            sb.Append("    tJunction.AddRelay(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(relay.InputKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(relay.OutputKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(relay.LayoutKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatPoseArgs(relay.Pose))
                .Append(");\n");
        }

        foreach (JunctionPathTemplateModel path in document.Paths)
        {
            sb.Append('\n');
            string pathVar = AllocateUniqueVarName("tPath", path.Key, usedVarNames);

            sb.Append("    var ").Append(pathVar).Append(" = tJunction.Wire(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(path.Key)).Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(path.FromPortKey)).Append(", ")
                .Append(path.FromPinIndex).Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(path.ToPortKey)).Append(", ")
                .Append(path.ToPinIndex).Append(");\n");

            if (path.TrafficDensityFactor.HasValue)
            {
                sb.Append("    ").Append(pathVar).Append(".TrafficDensity(")
                    .Append(RoslynCsxHelpers.FormatNumber(path.TrafficDensityFactor.Value))
                    .Append(");\n");
            }

            AppendSegments(sb, pathVar, path.Segments, declaredAnchors);

            if (0 < path.WidthSegments.Count)
            {
                sb.Append("    ").Append(pathVar).Append(".Width");
                for (int i = 0; i < path.WidthSegments.Count; i++)
                {
                    JunctionWidthSegment ws = path.WidthSegments[i];
                    sb.Append('\n').Append("        .");
                    if (ws.Kind == JunctionWidthSegmentKind.Constant)
                    {
                        sb.Append("Constant(").Append(DistanceExprCodec.Format(ws.Length));
                    }
                    else
                    {
                        sb.Append("TransitionTo(")
                            .Append(RoslynCsxHelpers.FormatNumber(ws.LeftWidth)).Append(", ")
                            .Append(RoslynCsxHelpers.FormatNumber(ws.RightWidth)).Append(", ")
                            .Append(DistanceExprCodec.Format(ws.Length));
                    }

                    if (!string.IsNullOrEmpty(ws.OutputAnchorId))
                    {
                        sb.Append(", ").Append(FormatOutAnchor(ws.OutputAnchorId, declaredAnchors));
                    }

                    sb.Append(')');
                }

                sb.Append(";\n");
            }

            foreach (CurveExtrusionPlacementRule ext in path.Extrusions)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveExtrusion("    ", pathVar, ext));
            }

            foreach (CurvePropPlacementRule prop in path.Props)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveProp("    ", pathVar, prop));
            }
        }

        foreach (JunctionCurveTemplateModel curve in document.Curves)
        {
            sb.Append('\n');
            string curveVar = AllocateUniqueVarName("tCurve", curve.Key, usedVarNames);

            sb.Append("    var ").Append(curveVar).Append(" = tJunction.AddCurve(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(curve.Key));

            if (curve.Origin.Kind == JunctionCurveOriginKind.AbsolutePose)
            {
                sb.Append(", ").Append(RoslynCsxHelpers.FormatPoseArgs(curve.Origin.Pose));
            }
            else
            {
                sb.Append(", ")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(curve.Origin.PortKey ?? string.Empty))
                    .Append(", ")
                    .Append(curve.Origin.PinIndex);
                if (curve.Origin.HasPortPinOffset)
                {
                    sb.Append(", ").Append(RoslynCsxHelpers.FormatPoseArgs(curve.Origin.Pose));
                }
            }

            sb.Append(");\n");

            AppendSegments(sb, curveVar, curve.Segments, declaredAnchors);

            foreach (CurveExtrusionPlacementRule ext in curve.Extrusions)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveExtrusion("    ", curveVar, ext));
            }

            foreach (CurvePropPlacementRule prop in curve.Props)
            {
                sb.Append(CurveRuleCsxCodec.FormatCurveProp("    ", curveVar, prop));
            }
        }

        if (0 < document.Props.Count)
        {
            sb.Append('\n');
            foreach (JunctionStaticPropPlacement prop in document.Props)
            {
                sb.Append("    tJunction.PutProp(")
                    .Append(RoslynCsxHelpers.FormatStringLiteral(prop.ModelKey)).Append(", ")
                    .Append(RoslynCsxHelpers.FormatPoseArgs(prop.Pose))
                    .Append(");\n");
            }
        }

        return RoslynCsxHelpers.WrapCanonicalCsxFile([editorCsxLoadPath], sb.ToString(), document.UserCode);
    }

    internal static string AllocateUniqueVarName(string prefix, string key, HashSet<string> usedNames)
    {
        string baseName = string.IsNullOrEmpty(key)
            ? $"{prefix}_Anon"
            : $"{prefix}_{RoslynCsxHelpers.SanitizeIdentifier(key)}";
        string candidate = baseName;
        int suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string FormatOutAnchor(string anchorId, HashSet<string> declaredAnchors)
    {
        return declaredAnchors.Add(anchorId) ? $"out var {anchorId}" : $"out {anchorId}";
    }

    private static void AppendSegments(
        StringBuilder sb,
        string targetVar,
        IReadOnlyList<JunctionCurveSegment> segments,
        HashSet<string> declaredAnchors)
    {
        if (segments.Count == 0)
        {
            return;
        }

        sb.Append("    ").Append(targetVar);
        foreach (JunctionCurveSegment seg in segments)
        {
            sb.Append('\n').Append("        .");
            switch (seg.Kind)
            {
                case JunctionCurveSegmentKind.StraightTo:
                    sb.Append("StraightTo(").Append(RoslynCsxHelpers.FormatPoseArgs(seg.TargetPose));
                    if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                    {
                        sb.Append(", ").Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                    }

                    sb.Append(')');
                    break;

                case JunctionCurveSegmentKind.BezierTo:
                    sb.Append("BezierTo(").Append(RoslynCsxHelpers.FormatPoseArgs(seg.TargetPose));
                    if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                    {
                        sb.Append(", ").Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                    }

                    if (seg.HandleScale != 0.5)
                    {
                        sb.Append(", ").Append(RoslynCsxHelpers.FormatNumber(seg.HandleScale));
                    }

                    sb.Append(')');
                    break;

                case JunctionCurveSegmentKind.StraightToEnd:
                    sb.Append("StraightToEnd(");
                    if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                    {
                        sb.Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                    }

                    sb.Append(')');
                    break;

                case JunctionCurveSegmentKind.BezierToEnd:
                    sb.Append("BezierToEnd(");
                    bool needComma = false;
                    if (!string.IsNullOrEmpty(seg.OutputAnchorId))
                    {
                        sb.Append(FormatOutAnchor(seg.OutputAnchorId, declaredAnchors));
                        needComma = true;
                    }

                    if (seg.HandleScale != 0.5)
                    {
                        if (needComma)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(RoslynCsxHelpers.FormatNumber(seg.HandleScale));
                    }

                    sb.Append(')');
                    break;
            }
        }

        sb.Append(";\n");
    }

    private static bool TryParseCreateJunction(InvocationExpressionSyntax inv, out string key)
    {
        key = string.Empty;
        if (inv.Expression is not MemberAccessExpressionSyntax ma
            || ma.Name.Identifier.ValueText != "CreateJunction"
            || ma.Expression.ToString() != "Network.Templates")
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> args = inv.ArgumentList.Arguments;
        return args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out key);
    }

    private static bool TryParseWireOrAddCurve(
        string junctionVarName,
        string localVarName,
        InvocationExpressionSyntax initInv,
        JunctionTemplateDocument doc,
        Dictionary<string, JunctionPathTemplateModel> pathVars,
        Dictionary<string, JunctionCurveTemplateModel> curveVars)
    {
        if (pathVars.ContainsKey(localVarName) || curveVars.ContainsKey(localVarName))
        {
            return false;
        }

        (ExpressionSyntax root, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(initInv);
        if (root is not IdentifierNameSyntax rootIdent || rootIdent.Identifier.ValueText != junctionVarName || calls.Count == 0)
        {
            return false;
        }

        InvocationExpressionSyntax firstCall = calls[0];
        string? firstMethod = RoslynCsxHelpers.GetInvokedMethodName(firstCall);

        if (firstMethod == "Wire")
        {
            SeparatedSyntaxList<ArgumentSyntax> args = firstCall.ArgumentList.Arguments;
            if (args.Count != 5
                || !RoslynCsxHelpers.TryGetString(args[0].Expression, out string key)
                || !RoslynCsxHelpers.TryGetString(args[1].Expression, out string fromPort)
                || !RoslynCsxHelpers.TryGetInt(args[2].Expression, out int fromPin)
                || !RoslynCsxHelpers.TryGetString(args[3].Expression, out string toPort)
                || !RoslynCsxHelpers.TryGetInt(args[4].Expression, out int toPin))
            {
                return false;
            }

            JunctionPathTemplateModel path = new JunctionPathTemplateModel
            {
                Key = key,
                FromPortKey = fromPort,
                FromPinIndex = fromPin,
                ToPortKey = toPort,
                ToPinIndex = toPin,
            };

            for (int i = 1; i < calls.Count; i++)
            {
                if (!ApplyPathMethodCall(calls[i], path))
                {
                    return false;
                }
            }

            doc.Paths.Add(path);
            pathVars[localVarName] = path;
            return true;
        }

        if (firstMethod == "AddCurve")
        {
            if (!TryParseAddCurveOrigin(firstCall.ArgumentList.Arguments, out string curveKey, out JunctionCurveOriginModel? origin))
            {
                return false;
            }

            JunctionCurveTemplateModel curve = new JunctionCurveTemplateModel
            {
                Key = curveKey,
                Origin = origin!,
            };

            for (int i = 1; i < calls.Count; i++)
            {
                if (!ApplyCurveMethodCall(calls[i], curve))
                {
                    return false;
                }
            }

            doc.Curves.Add(curve);
            curveVars[localVarName] = curve;
            return true;
        }

        return false;
    }

    private static bool TryParseAddCurveOrigin(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out string key,
        out JunctionCurveOriginModel? origin)
    {
        key = string.Empty;
        origin = null;

        if (args.Count == 7 && RoslynCsxHelpers.TryGetString(args[0].Expression, out key)
            && CurveRuleCsxCodec.TryReadPose6(args, 1, out PoseDegreesModel absPose))
        {
            origin = JunctionCurveOriginModel.Absolute(absPose);
            return true;
        }

        if ((args.Count == 3 || args.Count == 9)
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out key)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string portKey)
            && RoslynCsxHelpers.TryGetInt(args[2].Expression, out int pinIndex))
        {
            if (args.Count == 3)
            {
                origin = JunctionCurveOriginModel.FromPortPin(portKey, pinIndex);
                return true;
            }

            if (CurveRuleCsxCodec.TryReadPose6(args, 3, out PoseDegreesModel relOffset))
            {
                origin = JunctionCurveOriginModel.FromPortPinWithOffset(portKey, pinIndex, relOffset);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseStatementInvocation(
        string junctionVarName,
        InvocationExpressionSyntax invocation,
        JunctionTemplateDocument doc,
        IReadOnlyDictionary<string, JunctionPathTemplateModel> pathVars,
        IReadOnlyDictionary<string, JunctionCurveTemplateModel> curveVars)
    {
        (ExpressionSyntax root, List<InvocationExpressionSyntax> calls) = RoslynCsxHelpers.UnwrapFluentChain(invocation);
        if (calls.Count == 0)
        {
            return false;
        }

        if (root is IdentifierNameSyntax targetIdent)
        {
            string targetName = targetIdent.Identifier.ValueText;

            if (targetName == junctionVarName && calls.Count == 1)
            {
                return TryApplyJunctionCall(calls[0], doc);
            }

            if (pathVars.TryGetValue(targetName, out JunctionPathTemplateModel? path))
            {
                foreach (InvocationExpressionSyntax call in calls)
                {
                    if (!ApplyPathMethodCall(call, path))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (curveVars.TryGetValue(targetName, out JunctionCurveTemplateModel? curve))
            {
                foreach (InvocationExpressionSyntax call in calls)
                {
                    if (!ApplyCurveMethodCall(call, curve))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        else if (root is MemberAccessExpressionSyntax propAccess
            && propAccess.Name.Identifier.ValueText == "Width"
            && propAccess.Expression is IdentifierNameSyntax pathIdent
            && pathVars.TryGetValue(pathIdent.Identifier.ValueText, out JunctionPathTemplateModel? widthPath))
        {
            foreach (InvocationExpressionSyntax call in calls)
            {
                if (!ApplyWidthCall(call, widthPath))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool TryApplyJunctionCall(InvocationExpressionSyntax call, JunctionTemplateDocument doc)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "AddPort" && args.Count == 8
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string portKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string portLayout)
            && CurveRuleCsxCodec.TryReadPose6(args, 2, out PoseDegreesModel portPose))
        {
            doc.Ports.Add(new JunctionPortDefinition(portKey, portLayout, portPose));
            return true;
        }

        if (method == "AddRelay" && args.Count == 9
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string inKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string outKey)
            && RoslynCsxHelpers.TryGetString(args[2].Expression, out string relayLayout)
            && CurveRuleCsxCodec.TryReadPose6(args, 3, out PoseDegreesModel relayPose))
        {
            doc.Relays.Add(new JunctionRelayDefinition(inKey, outKey, relayLayout, relayPose));
            return true;
        }

        if (method == "PutProp" && (args.Count == 4 || args.Count == 7)
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string modelKey)
            && RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double x)
            && RoslynCsxHelpers.TryGetDouble(args[2].Expression, out double y)
            && RoslynCsxHelpers.TryGetDouble(args[3].Expression, out double z))
        {
            double rx = 0.0;
            double ry = 0.0;
            double rz = 0.0;
            if (args.Count == 7
                && (!RoslynCsxHelpers.TryGetDouble(args[4].Expression, out rx)
                    || !RoslynCsxHelpers.TryGetDouble(args[5].Expression, out ry)
                    || !RoslynCsxHelpers.TryGetDouble(args[6].Expression, out rz)))
            {
                return false;
            }

            doc.Props.Add(new JunctionStaticPropPlacement(modelKey, new PoseDegreesModel(x, y, z, rx, ry, rz)));
            return true;
        }

        return false;
    }

    private static bool ApplyPathMethodCall(InvocationExpressionSyntax call, JunctionPathTemplateModel path)
    {
        if (CurveRuleCsxCodec.TryParseJunctionCurveSegment(call, allowToEnd: true, out JunctionCurveSegment? seg))
        {
            path.Segments.Add(seg!);
            return true;
        }

        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "TrafficDensity" && args.Count == 1
            && RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double factor))
        {
            path.TrafficDensityFactor = factor;
            return true;
        }

        if (method == "PutProp" && CurveRuleCsxCodec.TryParseCurveProp(args, out CurvePropPlacementRule? propRule))
        {
            path.Props.Add(propRule!);
            return true;
        }

        if (method == "PutExtrusion" && CurveRuleCsxCodec.TryParseCurveExtrusion(args, out CurveExtrusionPlacementRule? extRule))
        {
            path.Extrusions.Add(extRule!);
            return true;
        }

        return false;
    }

    private static bool ApplyCurveMethodCall(InvocationExpressionSyntax call, JunctionCurveTemplateModel curve)
    {
        if (CurveRuleCsxCodec.TryParseJunctionCurveSegment(call, allowToEnd: false, out JunctionCurveSegment? seg))
        {
            curve.Segments.Add(seg!);
            return true;
        }

        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "PutProp" && CurveRuleCsxCodec.TryParseCurveProp(args, out CurvePropPlacementRule? propRule))
        {
            curve.Props.Add(propRule!);
            return true;
        }

        if (method == "PutExtrusion" && CurveRuleCsxCodec.TryParseCurveExtrusion(args, out CurveExtrusionPlacementRule? extRule))
        {
            curve.Extrusions.Add(extRule!);
            return true;
        }

        return false;
    }

    private static bool ApplyWidthCall(InvocationExpressionSyntax call, JunctionPathTemplateModel path)
    {
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "Constant" && (args.Count == 1 || args.Count == 2))
        {
            if (!RoslynCsxHelpers.TryParseAnchoredDistance(args[0], out AnchoredDistance length, out bool isMax) || isMax)
            {
                return false;
            }

            string? anchor = null;
            if (args.Count == 2 && !RoslynCsxHelpers.IsOutArgument(args[1], out anchor))
            {
                return false;
            }

            path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.Constant, 0.0, 0.0, length, anchor));
            return true;
        }

        if (method == "TransitionTo" && (args.Count == 3 || args.Count == 4))
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[0].Expression, out double left)
                || !RoslynCsxHelpers.TryGetDouble(args[1].Expression, out double right)
                || !RoslynCsxHelpers.TryParseAnchoredDistance(args[2], out AnchoredDistance length, out bool isMax)
                || isMax)
            {
                return false;
            }

            string? anchor = null;
            if (args.Count == 4 && !RoslynCsxHelpers.IsOutArgument(args[3], out anchor))
            {
                return false;
            }

            path.WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.TransitionTo, left, right, length, anchor));
            return true;
        }

        return false;
    }
}
