using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Serialization.Diagnostics;
using DiagnosticSeverity = TransportX.WorldEditor.Serialization.Diagnostics.DiagnosticSeverity;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Parses and generates the canonical <c>Init.csx</c> resource/traffic configuration script.
/// </summary>
public static class InitCsxSerializer
{
    public static (InitDocument? Document, IReadOnlyList<ParseDiagnostic> Diagnostics) Parse(
        string filePath,
        string sourceText,
        string relativePath = "Init.csx")
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(sourceText);

        CsxSlicedFile sliced = CsxMarkerSlicer.Slice(filePath, sourceText);
        List<ParseDiagnostic> diagnostics = new List<ParseDiagnostic>(sliced.Diagnostics);
        if (0 < sliced.PreBuildBlocks.Count)
        {
            foreach (CsxPreBuildBlock orphan in sliced.PreBuildBlocks)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    orphan.MarkerStartLine,
                    1,
                    "Init.csx 内では '// <user-prebuild>' を使用できません。"));
            }
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

        InitDocument doc = new InitDocument
        {
            RelativePath = relativePath,
            UserCode = sliced.UserCode,
        };

        foreach (StatementSyntax statement in statements)
        {
            if (statement is EmptyStatementSyntax)
            {
                continue;
            }

            if (statement is not ExpressionStatementSyntax exprStmt
                || exprStmt.Expression is not InvocationExpressionSyntax invocation)
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    RoslynCsxHelpers.GetActualLine(sliced, statement),
                    RoslynCsxHelpers.GetColumn(statement),
                    "Init.csx の <editor-generated> 内に正規形以外のステートメントが含まれています。"));
                continue;
            }

            if (!TryParseInitInvocation(invocation, doc))
            {
                diagnostics.Add(new ParseDiagnostic(
                    DiagnosticSeverity.Error,
                    filePath,
                    RoslynCsxHelpers.GetActualLine(sliced, invocation),
                    RoslynCsxHelpers.GetColumn(invocation),
                    $"Init.csx の <editor-generated> 内で許可されていない呼び出しです: {invocation}"));
            }
        }

        return RoslynCsxHelpers.HasError(diagnostics) ? (null, diagnostics) : (doc, diagnostics);
    }

    /// <summary>
    /// Best-effort read-only extraction for hand-written <c>Init.csx</c> files that lack
    /// <c>&lt;editor-generated&gt;</c> markers, so asset lists, cross-sections, and lane layouts
    /// remain inspectable and editable in the GUI while keeping <c>Init.csx</c> protected.
    /// </summary>
    public static void PopulateFallback(string sourceText, string relativePath, InitDocument doc)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(doc);

        doc.RelativePath = relativePath;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceText);
        SyntaxNode root = tree.GetRoot();

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            TryParseInitInvocation(invocation, doc);
        }
    }

    public static string Generate(InitDocument document, string editorCsxLoadPath = "__Editor.csx")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editorCsxLoadPath);

        StringBuilder sb = new StringBuilder();
        foreach (AssetListLoadReference list in document.ModelLists)
        {
            sb.Append("    Models.LoadList(");
            if (!string.IsNullOrEmpty(list.BundleKey))
            {
                sb.Append(RoslynCsxHelpers.FormatStringLiteral(list.BundleKey)).Append(", ");
            }

            sb.Append(RoslynCsxHelpers.FormatStringLiteral(list.RelativePath)).Append(");\n");
        }

        foreach (AssetListLoadReference list in document.SoundLists)
        {
            sb.Append("    Sounds.LoadList(");
            if (!string.IsNullOrEmpty(list.BundleKey))
            {
                sb.Append(RoslynCsxHelpers.FormatStringLiteral(list.BundleKey)).Append(", ");
            }

            sb.Append(RoslynCsxHelpers.FormatStringLiteral(list.RelativePath)).Append(");\n");
        }

        if (0 < document.CrossSections.Count && 0 < sb.Length)
        {
            sb.Append('\n');
        }

        foreach (ResourceLoadReference cs in document.CrossSections)
        {
            sb.Append("    CrossSections.Load(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(cs.Key))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(cs.RelativePath))
                .Append(");\n");
        }

        if ((0 < document.LaneTraffic.Types.Count || 0 < document.LaneTraffic.Groups.Count) && 0 < sb.Length)
        {
            sb.Append('\n');
        }

        foreach (LaneTrafficTypeDefinition typeDef in document.LaneTraffic.Types)
        {
            sb.Append("    Network.LaneTraffic.AddType(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(typeDef.Key))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(typeDef.DisplayName))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(typeDef.ColorHex))
                .Append(");\n");
        }

        foreach (LaneTrafficGroupDefinition groupDef in document.LaneTraffic.Groups)
        {
            string joined = string.Join("|", groupDef.TypeKeys);
            sb.Append("    Network.LaneTraffic.AddGroup(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(groupDef.Key))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(joined))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(groupDef.ColorHex))
                .Append(");\n");
        }

        if (0 < document.LaneLayouts.Count && 0 < sb.Length)
        {
            sb.Append('\n');
        }

        foreach (ResourceLoadReference layout in document.LaneLayouts)
        {
            sb.Append("    Network.LaneLayouts.Load(")
                .Append(RoslynCsxHelpers.FormatStringLiteral(layout.Key))
                .Append(", ")
                .Append(RoslynCsxHelpers.FormatStringLiteral(layout.RelativePath))
                .Append(");\n");
        }

        return RoslynCsxHelpers.WrapCanonicalCsxFile([editorCsxLoadPath], sb.ToString(), document.UserCode);
    }

    private static bool TryParseInitInvocation(InvocationExpressionSyntax invocation, InitDocument doc)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        string receiverPath = memberAccess.Expression.ToString();
        string methodName = memberAccess.Name.Identifier.ValueText;
        Microsoft.CodeAnalysis.SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;

        if ((receiverPath == "Models" || receiverPath == "Sounds") && methodName == "LoadList")
        {
            string? bundleKey = null;
            string? path = null;
            if (args.Count == 1 && RoslynCsxHelpers.TryGetString(args[0].Expression, out string p1))
            {
                path = p1;
            }
            else if (args.Count == 2
                && RoslynCsxHelpers.TryGetString(args[0].Expression, out string k)
                && RoslynCsxHelpers.TryGetString(args[1].Expression, out string p2))
            {
                bundleKey = k;
                path = p2;
            }
            else
            {
                return false;
            }

            if (receiverPath == "Models")
            {
                doc.ModelLists.Add(new AssetListLoadReference(bundleKey, path));
            }
            else
            {
                doc.SoundLists.Add(new AssetListLoadReference(bundleKey, path));
            }

            return true;
        }

        if (receiverPath == "CrossSections" && methodName == "Load" && args.Count == 2
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string csKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string csPath))
        {
            doc.CrossSections.Add(new ResourceLoadReference(csKey, csPath));
            return true;
        }

        if (receiverPath == "Network.LaneLayouts" && methodName == "Load" && args.Count == 2
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string llKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string llPath))
        {
            doc.LaneLayouts.Add(new ResourceLoadReference(llKey, llPath));
            return true;
        }

        if (receiverPath == "Network.LaneTraffic" && methodName == "AddType" && args.Count == 3
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string tKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string tName)
            && RoslynCsxHelpers.TryGetString(args[2].Expression, out string tColor))
        {
            doc.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition(tKey, tName, tColor));
            return true;
        }

        if (receiverPath == "Network.LaneTraffic" && methodName == "AddGroup" && args.Count == 3
            && RoslynCsxHelpers.TryGetString(args[0].Expression, out string gKey)
            && RoslynCsxHelpers.TryGetString(args[1].Expression, out string gTypes)
            && RoslynCsxHelpers.TryGetString(args[2].Expression, out string gColor))
        {
            List<string> typeKeys = gTypes
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            doc.LaneTraffic.Groups.Add(new LaneTrafficGroupDefinition(gKey, typeKeys, gColor));
            return true;
        }

        return false;
    }
}
