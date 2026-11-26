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
/// Parses and generates single-file spline template scripts (<c>Templates/Splines/&lt;Key&gt;.csx</c>).
/// </summary>
public static class SplineTemplateCsxSerializer
{
    public static (SplineTemplateDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
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
                "スプラインテンプレート内では '// <user-prebuild>' を使用できません。"));
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

        SplineTemplateDocument doc = new SplineTemplateDocument
        {
            RelativePath = relativePath,
            UserCode = sliced.UserCode,
        };

        string? splineVarName = null;

        foreach (StatementSyntax statement in statements)
        {
            if (statement is EmptyStatementSyntax)
            {
                continue;
            }

            if (splineVarName is null
                && statement is LocalDeclarationStatementSyntax decl
                && decl.Declaration.Variables.Count == 1
                && decl.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax createInv
                && TryParseCreateSpline(createInv, out string templateKey, out string outletLayoutKey))
            {
                splineVarName = decl.Declaration.Variables[0].Identifier.ValueText;
                doc.Key = templateKey;
                doc.OutletLayoutKey = outletLayoutKey;
                continue;
            }

            if (splineVarName is not null
                && statement is ExpressionStatementSyntax exprStmt
                && exprStmt.Expression is InvocationExpressionSyntax inv
                && inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Expression is IdentifierNameSyntax targetIdent
                && targetIdent.Identifier.ValueText == splineVarName)
            {
                string method = ma.Name.Identifier.ValueText;
                SeparatedSyntaxList<ArgumentSyntax> args = inv.ArgumentList.Arguments;

                if (method == "PutProp" && TryParseSplineTemplateProp(args, out SplineTemplatePropRule? propRule))
                {
                    doc.Props.Add(propRule!);
                    continue;
                }

                if (method == "PutExtrusion" && TryParseSplineTemplateExtrusion(args, out SplineTemplateExtrusionRule? extRule))
                {
                    doc.Extrusions.Add(extRule!);
                    continue;
                }
            }

            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                RoslynCsxHelpers.GetActualLine(sliced, statement),
                RoslynCsxHelpers.GetColumn(statement),
                $"SplineTemplate の <editor-generated> 内で不正または未知のステートメントです: {statement}"));
        }

        if (splineVarName is null && !RoslynCsxHelpers.HasError(diagnostics))
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                filePath,
                sliced.EditorGeneratedStartLine,
                1,
                "Network.Templates.CreateSpline(...) 宣言が見つかりません。"));
        }

        return RoslynCsxHelpers.HasError(diagnostics) ? (null, diagnostics) : (doc, diagnostics);
    }

    public static string Generate(SplineTemplateDocument document, string editorCsxLoadPath = "../../__Editor.csx")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editorCsxLoadPath);

        StringBuilder sb = new StringBuilder();
        sb.Append("    var tSpline = Network.Templates.CreateSpline(")
            .Append(RoslynCsxHelpers.FormatStringLiteral(document.Key))
            .Append(", ")
            .Append(RoslynCsxHelpers.FormatStringLiteral(document.OutletLayoutKey))
            .Append(");\n");

        foreach (SplineTemplateExtrusionRule extrusion in document.Extrusions)
        {
            sb.Append("    tSpline.PutExtrusion(");
            if (extrusion.Key is not null)
            {
                sb.Append(RoslynCsxHelpers.FormatStringLiteral(extrusion.Key)).Append(", ");
            }

            sb.Append(RoslynCsxHelpers.FormatStringLiteral(extrusion.CrossSectionKey)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.OffsetX)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.OffsetY)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.BaseInterval)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.MaxDeviation)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.MaxAngleDeltaDeg)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.VIntervalScale)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.VOffset)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(extrusion.From))
                .Append(");\n");
        }

        foreach (SplineTemplatePropRule prop in document.Props)
        {
            sb.Append("    tSpline.PutProp(");
            if (prop.Key is not null)
            {
                sb.Append(RoslynCsxHelpers.FormatStringLiteral(prop.Key)).Append(", ");
            }

            sb.Append(RoslynCsxHelpers.FormatStringArray(prop.ModelKeys)).Append(", ")
                .Append(RoslynCsxHelpers.FormatPoseArgs(prop.Pose)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(prop.From)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(prop.Span)).Append(", ")
                .Append(RoslynCsxHelpers.FormatNumber(prop.Interval))
                .Append(");\n");
        }

        return RoslynCsxHelpers.WrapCanonicalCsxFile([editorCsxLoadPath], sb.ToString(), document.UserCode);
    }

    private static bool TryParseCreateSpline(InvocationExpressionSyntax invocation, out string key, out string layoutKey)
    {
        key = string.Empty;
        layoutKey = string.Empty;

        if (invocation.Expression is not MemberAccessExpressionSyntax ma
            || ma.Name.Identifier.ValueText != "CreateSpline"
            || ma.Expression.ToString() != "Network.Templates")
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
        if (args.Count != 2)
        {
            return false;
        }

        return RoslynCsxHelpers.TryGetString(args[0].Expression, out key)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out layoutKey);
    }

    private static bool TryParseSplineTemplateProp(SeparatedSyntaxList<ArgumentSyntax> args, out SplineTemplatePropRule? rule)
    {
        rule = null;
        if (args.Count < 7)
        {
            return false;
        }

        string? key = null;
        int idx = 0;

        if (RoslynCsxHelpers.TryGetNullableString(args[0].Expression, out string? maybeKey)
            && RoslynCsxHelpers.TryGetStringList(args[1].Expression, out _))
        {
            key = maybeKey;
            idx = 1;
        }

        if (!RoslynCsxHelpers.TryGetStringList(args[idx].Expression, out List<string> models))
        {
            return false;
        }

        idx++;
        int rem = args.Count - idx;
        if (rem != 6 && rem != 9)
        {
            return false;
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out double x)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out double y)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out double z))
        {
            return false;
        }

        double rx = 0.0;
        double ry = 0.0;
        double rz = 0.0;
        if (rem == 9)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out rx)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out ry)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out rz))
            {
                return false;
            }
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out double from)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out double span)
            || !RoslynCsxHelpers.TryGetDouble(args[idx].Expression, out double interval))
        {
            return false;
        }

        rule = new SplineTemplatePropRule
        {
            Key = key,
            Pose = new PoseDegreesModel(x, y, z, rx, ry, rz),
            From = from,
            Span = span,
            Interval = interval,
        };
        rule.ModelKeys.AddRange(models);
        return true;
    }

    private static bool TryParseSplineTemplateExtrusion(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out SplineTemplateExtrusionRule? rule)
    {
        rule = null;
        if (args.Count < 5)
        {
            return false;
        }

        string? key = null;
        int idx = 0;

        if (RoslynCsxHelpers.TryGetNullableString(args[0].Expression, out string? maybeKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out _))
        {
            key = maybeKey;
            idx = 1;
        }

        if (!RoslynCsxHelpers.TryGetString(args[idx].Expression, out string csKey))
        {
            return false;
        }

        idx++;
        int rem = args.Count - idx;
        double offsetX = 0.0;
        double offsetY = 0.0;
        double baseInterval;
        double maxDev;
        double maxAngle;
        double vScale = 1.0;
        double vOffset = 0.0;
        double from;

        if (rem == 4)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out baseInterval)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxDev)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxAngle)
                || !RoslynCsxHelpers.TryGetDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else if (rem == 6)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetX)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetY)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out baseInterval)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxDev)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxAngle)
                || !RoslynCsxHelpers.TryGetDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else if (rem == 8)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetX)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetY)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out baseInterval)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxDev)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxAngle)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out vScale)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out vOffset)
                || !RoslynCsxHelpers.TryGetDouble(args[idx].Expression, out from))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        rule = new SplineTemplateExtrusionRule
        {
            Key = key,
            CrossSectionKey = csKey,
            OffsetX = offsetX,
            OffsetY = offsetY,
            BaseInterval = baseInterval,
            MaxDeviation = maxDev,
            MaxAngleDeltaDeg = maxAngle,
            VIntervalScale = vScale,
            VOffset = vOffset,
            From = from,
        };
        return true;
    }
}
