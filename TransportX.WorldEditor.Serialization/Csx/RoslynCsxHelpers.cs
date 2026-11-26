using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Serialization.Diagnostics;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Shared Roslyn AST utilities and canonical `.csx` formatting helpers.
/// </summary>
internal static class RoslynCsxHelpers
{
    private static readonly CSharpParseOptions ScriptParseOptions = new CSharpParseOptions(
        LanguageVersion.Latest,
        DocumentationMode.None,
        SourceCodeKind.Script);

    public static bool TryParseEditorGeneratedBlock(
        string filePath,
        CsxSlicedFile sliced,
        List<ParseDiagnostic> diagnostics,
        out SyntaxList<StatementSyntax> statements,
        out SyntaxToken closingToken,
        out IReadOnlyDictionary<int, CsxPreBuildBlock> preBuildByIndex)
    {
        Dictionary<int, CsxPreBuildBlock> map = new Dictionary<int, CsxPreBuildBlock>();
        foreach (CsxPreBuildBlock block in sliced.PreBuildBlocks)
        {
            map[block.Index] = block;
        }

        preBuildByIndex = map;
        statements = default;
        closingToken = default;

        string wrappedCode = "{\n" + sliced.EditorGeneratedCode + "\n}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(wrappedCode, ScriptParseOptions, filePath);
        foreach (Diagnostic diag in tree.GetDiagnostics())
        {
            if (diag.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            {
                continue;
            }

            FileLinePositionSpan span = diag.Location.GetLineSpan();
            int actualLine = Math.Max(1, sliced.EditorGeneratedStartLine - 1 + span.StartLinePosition.Line);
            int actualCol = span.StartLinePosition.Character + 1;
            diagnostics.Add(new ParseDiagnostic(
                Diagnostics.DiagnosticSeverity.Error,
                filePath,
                actualLine,
                actualCol,
                $"C# スクリプト構文エラー: {diag.GetMessage(CultureInfo.InvariantCulture)}"));
        }

        CompilationUnitSyntax compilationUnit = tree.GetCompilationUnitRoot();
        if (compilationUnit.Members.Count != 1
            || compilationUnit.Members[0] is not GlobalStatementSyntax globalBlock
            || globalBlock.Statement is not BlockSyntax bodyBlock)
        {
            if (!HasError(diagnostics))
            {
                diagnostics.Add(new ParseDiagnostic(
                    Diagnostics.DiagnosticSeverity.Error,
                    filePath,
                    Math.Max(1, sliced.EditorGeneratedStartLine),
                    1,
                    "<editor-generated> ブロック内の波括弧バランスまたは構文が不正です。"));
            }

            return false;
        }

        statements = bodyBlock.Statements;
        closingToken = bodyBlock.CloseBraceToken;
        return !HasError(diagnostics);
    }

    public static bool HasError(IEnumerable<ParseDiagnostic> diagnostics)
    {
        foreach (ParseDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == Diagnostics.DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }

    public static int GetActualLine(CsxSlicedFile sliced, SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return Math.Max(1, sliced.EditorGeneratedStartLine - 1 + span.StartLinePosition.Line);
    }

    public static int GetColumn(SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return span.StartLinePosition.Character + 1;
    }

    public static List<int> ExtractPreBuildPlaceholderIndices(SyntaxTriviaList triviaList)
    {
        List<int> indices = new List<int>();
        foreach (SyntaxTrivia trivia in triviaList)
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            {
                continue;
            }

            string text = trivia.ToString().Trim();
            if (text.StartsWith(CsxMarkerSlicer.PlaceholderPrefix, StringComparison.Ordinal)
                && text.EndsWith("__", StringComparison.Ordinal))
            {
                string numberSpan = text.Substring(
                    CsxMarkerSlicer.PlaceholderPrefix.Length,
                    text.Length - CsxMarkerSlicer.PlaceholderPrefix.Length - 2);
                if (int.TryParse(numberSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    indices.Add(idx);
                }
            }
        }

        return indices;
    }

    public static string? ExtractDisplayName(SyntaxTriviaList triviaList)
    {
        string? result = null;
        foreach (SyntaxTrivia trivia in triviaList)
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            {
                continue;
            }

            string comment = trivia.ToString();
            if (!comment.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            string body = comment[2..].Trim();
            if (!body.StartsWith("<display", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                XElement elem = XElement.Parse(body);
                if (!string.Equals(elem.Name.LocalName, "display", StringComparison.Ordinal))
                {
                    continue;
                }

                string? name = (string?)elem.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result = name.Trim();
                }
            }
            catch (XmlException)
            {
                // Ignore malformed comment directives
            }
        }

        return result;
    }

    public static string? ExtractStatementDisplayName(StatementSyntax statement)
    {
        return ExtractDisplayName(statement.GetLeadingTrivia())
            ?? ExtractDisplayName(statement.GetTrailingTrivia());
    }

    public static string FormatDisplayDirective(string indent, string displayName)
    {
        XElement elem = new("display", new XAttribute("name", displayName.Trim()));
        return $"{indent}// {elem.ToString(SaveOptions.DisableFormatting)}\n";
    }

    public static (ExpressionSyntax RootReceiver, List<InvocationExpressionSyntax> CallsInOrder) UnwrapFluentChain(
        InvocationExpressionSyntax outerInvocation)
    {
        Stack<InvocationExpressionSyntax> stack = new Stack<InvocationExpressionSyntax>();
        ExpressionSyntax current = outerInvocation;

        while (current is InvocationExpressionSyntax inv)
        {
            stack.Push(inv);
            if (inv.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression is InvocationExpressionSyntax innerInv)
            {
                current = innerInv;
            }
            else if (inv.Expression is MemberAccessExpressionSyntax terminalMember)
            {
                current = terminalMember.Expression;
                break;
            }
            else
            {
                current = inv.Expression;
                break;
            }
        }

        return (current, new List<InvocationExpressionSyntax>(stack));
    }

    public static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            IdentifierNameSyntax ident => ident.Identifier.ValueText,
            _ => null,
        };
    }

    public static bool TryGetString(ExpressionSyntax expression, out string value)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetNullableString(ExpressionSyntax expression, out string? value)
    {
        if (expression is LiteralExpressionSyntax nullLiteral && nullLiteral.IsKind(SyntaxKind.NullLiteralExpression))
        {
            value = null;
            return true;
        }

        if (TryGetString(expression, out string nonNull))
        {
            value = nonNull;
            return true;
        }

        value = null;
        return false;
    }

    public static bool IsDoubleMaxValue(ExpressionSyntax expression)
    {
        return expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "MaxValue"
            && ma.Expression is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.DoubleKeyword);
    }

    public static bool IsIntMaxValue(ExpressionSyntax expression)
    {
        return expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "MaxValue"
            && ma.Expression is PredefinedTypeSyntax pt
            && pt.Keyword.IsKind(SyntaxKind.IntKeyword);
    }

    public static bool TryGetDouble(ExpressionSyntax expression, out double value)
    {
        if (IsDoubleMaxValue(expression))
        {
            value = double.MaxValue;
            return true;
        }

        if (expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            if (lit.Token.Value is double d)
            {
                value = d;
                return true;
            }

            if (lit.Token.Value is float f)
            {
                value = f;
                return true;
            }

            if (lit.Token.Value is int i)
            {
                value = i;
                return true;
            }

            if (lit.Token.Value is long l)
            {
                value = l;
                return true;
            }
        }

        if (expression is PrefixUnaryExpressionSyntax prefix && TryGetDouble(prefix.Operand, out double operandVal))
        {
            if (prefix.IsKind(SyntaxKind.UnaryMinusExpression))
            {
                value = -operandVal;
                return true;
            }

            if (prefix.IsKind(SyntaxKind.UnaryPlusExpression))
            {
                value = operandVal;
                return true;
            }
        }

        value = 0.0;
        return false;
    }

    public static bool TryGetInt(ExpressionSyntax expression, out int value)
    {
        if (IsIntMaxValue(expression))
        {
            value = int.MaxValue;
            return true;
        }

        if (expression is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.NumericLiteralExpression)
            && lit.Token.Value is int i)
        {
            value = i;
            return true;
        }

        if (expression is PrefixUnaryExpressionSyntax prefix && TryGetInt(prefix.Operand, out int inner))
        {
            if (prefix.IsKind(SyntaxKind.UnaryMinusExpression))
            {
                value = -inner;
                return true;
            }

            if (prefix.IsKind(SyntaxKind.UnaryPlusExpression))
            {
                value = inner;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetStringList(ExpressionSyntax expression, out List<string> values)
    {
        values = new List<string>();

        if (expression is CollectionExpressionSyntax collection)
        {
            foreach (CollectionElementSyntax element in collection.Elements)
            {
                if (element is ExpressionElementSyntax exprEl && TryGetString(exprEl.Expression, out string item))
                {
                    values.Add(item);
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray)
        {
            return TryReadInitializerStrings(implicitArray.Initializer, values);
        }

        if (expression is ArrayCreationExpressionSyntax array && array.Initializer is not null)
        {
            return TryReadInitializerStrings(array.Initializer, values);
        }

        return false;
    }

    private static bool TryReadInitializerStrings(InitializerExpressionSyntax initializer, IList<string> values)
    {
        foreach (ExpressionSyntax expr in initializer.Expressions)
        {
            if (!TryGetString(expr, out string item))
            {
                return false;
            }

            values.Add(item);
        }

        return true;
    }

    public static bool IsOutArgument(ArgumentSyntax argument, out string? anchorIdentifier)
    {
        anchorIdentifier = null;
        if (!argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
        {
            return false;
        }

        if (argument.Expression is DeclarationExpressionSyntax decl
            && decl.Designation is SingleVariableDesignationSyntax single)
        {
            anchorIdentifier = single.Identifier.ValueText;
            return true;
        }

        if (argument.Expression is DeclarationExpressionSyntax discardDecl
            && discardDecl.Designation is DiscardDesignationSyntax)
        {
            anchorIdentifier = null;
            return true;
        }

        if (argument.Expression is IdentifierNameSyntax ident)
        {
            anchorIdentifier = ident.Identifier.ValueText == "_" ? null : ident.Identifier.ValueText;
            return true;
        }

        return false;
    }

    public static bool IsCanonicalOutAnchorArgument(ArgumentSyntax argument, out string? anchorId)
    {
        anchorId = null;
        if (!IsOutArgument(argument, out string? rawIdentifier))
        {
            return false;
        }

        if (rawIdentifier is null)
        {
            return true;
        }

        if (DistanceExprCodec.TryParseAnchorVariableName(rawIdentifier, out string parsedAnchor))
        {
            anchorId = parsedAnchor;
            return true;
        }

        return false;
    }

    public static string FormatStringLiteral(string value)
    {
        return SymbolDisplay.FormatLiteral(value, quote: true);
    }

    public static string FormatNullableStringLiteral(string? value)
    {
        return value is null ? "null" : FormatStringLiteral(value);
    }

    public static string FormatStringArray(IReadOnlyList<string> values)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < values.Count; i++)
        {
            if (0 < i)
            {
                sb.Append(", ");
            }

            sb.Append(FormatStringLiteral(values[i]));
        }

        sb.Append(']');
        return sb.ToString();
    }

    public static string FormatNumber(double value)
    {
        return DistanceExprCodec.FormatNumber(value);
    }

    public static string FormatPoseArgs(PoseDegreesModel pose)
    {
        return string.Join(", ",
            FormatNumber(pose.X),
            FormatNumber(pose.Y),
            FormatNumber(pose.Z),
            FormatNumber(pose.RotationX),
            FormatNumber(pose.RotationY),
            FormatNumber(pose.RotationZ));
    }

    private static readonly HashSet<string> WindowsReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SanitizeIdentifier(string raw)
    {
        StringBuilder sb = new();
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    public static string SanitizeFileName(string? raw, string fallback = "Template")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string trimmed = raw.Trim();
        ReadOnlySpan<char> invalidChars = System.IO.Path.GetInvalidFileNameChars();
        StringBuilder sb = new(trimmed.Length);
        foreach (char c in trimmed)
        {
            if (char.IsControl(c)
                || c == '/'
                || c == '\\'
                || c == ':'
                || c == '*'
                || c == '?'
                || c == '"'
                || c == '<'
                || c == '>'
                || c == '|'
                || 0 <= invalidChars.IndexOf(c))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        string cleaned = sb.ToString().TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(cleaned) || cleaned == "." || cleaned == "..")
        {
            return fallback;
        }

        if (WindowsReservedFileNames.Contains(cleaned))
        {
            return $"{cleaned}_";
        }

        return cleaned;
    }

    public static string DedentOneLevel(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                lines[i] = string.Empty;
                continue;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal))
            {
                lines[i] = line[4..];
                continue;
            }

            if (line[0] == '\t')
            {
                lines[i] = line[1..];
                continue;
            }

            int spaces = 0;
            while (spaces < 4 && spaces < line.Length && line[spaces] == ' ')
            {
                spaces++;
            }

            lines[i] = 0 < spaces ? line[spaces..] : line;
        }

        return string.Join('\n', lines);
    }

    public static void AppendIndentedUserBlock(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append('\n');
            }
            else
            {
                sb.Append("    ").Append(line).Append('\n');
            }
        }
    }

    public static string WrapCanonicalCsxFile(
        IEnumerable<string> loadDirectives,
        string editorGeneratedBody,
        string? userCode)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string load in loadDirectives)
        {
            sb.Append("#load ").Append(FormatStringLiteral(load)).Append('\n');
        }

        sb.Append("{\n");
        sb.Append("    // <editor-generated version=\"1\">\n");
        if (!string.IsNullOrEmpty(editorGeneratedBody))
        {
            sb.Append(editorGeneratedBody);
            if (!editorGeneratedBody.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        sb.Append("    // </editor-generated>\n");

        if (userCode is not null)
        {
            sb.Append('\n');
            sb.Append("    // <user-code>\n");
            AppendIndentedUserBlock(sb, userCode);
            sb.Append("    // </user-code>\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    public static bool TryParseAnchoredDistance(
        ArgumentSyntax arg,
        out AnchoredDistance value,
        out bool isDoubleMaxValue)
    {
        return DistanceExprCodec.TryParse(arg.Expression, out value, out isDoubleMaxValue);
    }
}
