using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Semantic/syntactic token categories used to syntax-highlight user C# scripts.
/// </summary>
public enum CSharpHighlightKind
{
    Comment,
    String,
    Number,
    ControlKeyword,
    Keyword,
    Type,
    Method,
    ScriptGlobal,
}

/// <summary>
/// Character span in a script buffer tagged with a <see cref="CSharpHighlightKind"/>.
/// </summary>
public readonly record struct CSharpHighlightSpan(int Start, int Length, CSharpHighlightKind Kind);

/// <summary>
/// Roslyn AST-based C# script classifier used for light-weight, zero-execution syntax coloring
/// of <c>// &lt;user-code&gt;</c> blocks.
/// </summary>
public static class CSharpSyntaxHighlightClassifier
{
    private static readonly CSharpParseOptions ScriptParseOptions = new(
        LanguageVersion.Latest,
        DocumentationMode.None,
        SourceCodeKind.Script);

    private static readonly HashSet<SyntaxKind> ControlKeywordKinds = new()
    {
        SyntaxKind.IfKeyword,
        SyntaxKind.ElseKeyword,
        SyntaxKind.ForKeyword,
        SyntaxKind.ForEachKeyword,
        SyntaxKind.WhileKeyword,
        SyntaxKind.DoKeyword,
        SyntaxKind.SwitchKeyword,
        SyntaxKind.CaseKeyword,
        SyntaxKind.DefaultKeyword,
        SyntaxKind.BreakKeyword,
        SyntaxKind.ContinueKeyword,
        SyntaxKind.ReturnKeyword,
        SyntaxKind.ThrowKeyword,
        SyntaxKind.TryKeyword,
        SyntaxKind.CatchKeyword,
        SyntaxKind.FinallyKeyword,
        SyntaxKind.YieldKeyword,
        SyntaxKind.UsingKeyword,
    };

    private static readonly HashSet<string> KnownScriptGlobals = new(StringComparer.Ordinal)
    {
        "Chunks",
        "Network",
        "Component",
        "Models",
        "Sounds",
        "CrossSections",
        "Camera",
        "Avatar",
        "Environment",
        "DirectionalLight",
        "Background",
        "Triggers",
        "Debug",
        "tSpline",
        "tJunction",
        "fSpline",
        "fJunction",
        "spline",
        "junction",
    };

    /// <summary>
    /// Classifies <paramref name="code"/> using Roslyn's script parser and returns sorted,
    /// non-overlapping highlight spans.
    /// </summary>
    public static IReadOnlyList<CSharpHighlightSpan> Classify(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Array.Empty<CSharpHighlightSpan>();
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(code, ScriptParseOptions);
        SyntaxNode root = tree.GetRoot();
        List<CSharpHighlightSpan> spans = new List<CSharpHighlightSpan>();

        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                TextSpan ts = trivia.Span;
                if (0 < ts.Length)
                {
                    spans.Add(new CSharpHighlightSpan(ts.Start, ts.Length, CSharpHighlightKind.Comment));
                }
            }
        }

        foreach (SyntaxToken token in root.DescendantTokens(descendIntoTrivia: false))
        {
            TextSpan ts = token.Span;
            if (ts.Length <= 0)
            {
                continue;
            }

            CSharpHighlightKind? kind = ClassifyToken(token);
            if (kind.HasValue)
            {
                spans.Add(new CSharpHighlightSpan(ts.Start, ts.Length, kind.Value));
            }
        }

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length.CompareTo(b.Length));
        return spans;
    }

    private static CSharpHighlightKind? ClassifyToken(SyntaxToken token)
    {
        SyntaxKind kind = token.Kind();

        if (kind is SyntaxKind.StringLiteralToken
            or SyntaxKind.CharacterLiteralToken
            or SyntaxKind.InterpolatedStringStartToken
            or SyntaxKind.InterpolatedStringEndToken
            or SyntaxKind.InterpolatedVerbatimStringStartToken
            or SyntaxKind.InterpolatedStringTextToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken
            or SyntaxKind.Utf8StringLiteralToken)
        {
            return CSharpHighlightKind.String;
        }

        if (kind == SyntaxKind.NumericLiteralToken)
        {
            return CSharpHighlightKind.Number;
        }

        if (ControlKeywordKinds.Contains(kind))
        {
            return CSharpHighlightKind.ControlKeyword;
        }

        if (token.IsKeyword() || token.IsContextualKeyword())
        {
            return CSharpHighlightKind.Keyword;
        }

        if (kind != SyntaxKind.IdentifierToken)
        {
            return null;
        }

        SyntaxNode? parent = token.Parent;
        if (parent is IdentifierNameSyntax ident && ident.IsVar)
        {
            return CSharpHighlightKind.Keyword;
        }
        if (parent is TypeSyntax && IsTypeContext(parent))
        {
            if (parent.Parent is InvocationExpressionSyntax invExpr && ReferenceEquals(invExpr.Expression, parent))
            {
                return CSharpHighlightKind.Method;
            }

            if (parent.Parent is MemberAccessExpressionSyntax maParent
                && ReferenceEquals(maParent.Name, parent)
                && maParent.Parent is InvocationExpressionSyntax)
            {
                return CSharpHighlightKind.Method;
            }

            if (parent is SimpleNameSyntax simpleName && IsInsideTypePosition(simpleName))
            {
                return CSharpHighlightKind.Type;
            }
        }

        if (parent is MemberBindingExpressionSyntax mb && mb.Parent is InvocationExpressionSyntax)
        {
            return CSharpHighlightKind.Method;
        }

        if (KnownScriptGlobals.Contains(token.ValueText)
            || token.ValueText.StartsWith("f_", StringComparison.Ordinal)
            || token.ValueText.StartsWith("tPath", StringComparison.Ordinal)
            || token.ValueText.StartsWith("tCurve", StringComparison.Ordinal))
        {
            return CSharpHighlightKind.ScriptGlobal;
        }

        return null;
    }

    private static bool IsTypeContext(SyntaxNode node)
    {
        return node is IdentifierNameSyntax or GenericNameSyntax or PredefinedTypeSyntax;
    }

    private static bool IsInsideTypePosition(SimpleNameSyntax name)
    {
        SyntaxNode? parent = name.Parent;
        if (parent is GenericNameSyntax)
        {
            return true;
        }

        if (name is GenericNameSyntax)
        {
            return parent is TypeArgumentListSyntax or ObjectCreationExpressionSyntax or VariableDeclarationSyntax or ParameterSyntax or CastExpressionSyntax or MemberAccessExpressionSyntax;
        }

        return parent switch
        {
            TypeArgumentListSyntax => true,
            ObjectCreationExpressionSyntax => true,
            CastExpressionSyntax => true,
            TypeOfExpressionSyntax => true,
            DefaultExpressionSyntax => true,
            ArrayTypeSyntax => true,
            NullableTypeSyntax => true,
            VariableDeclarationSyntax varDecl => ReferenceEquals(varDecl.Type, name),
            ParameterSyntax param => ReferenceEquals(param.Type, name),
            ForEachStatementSyntax fe => ReferenceEquals(fe.Type, name),
            DeclarationPatternSyntax dp => ReferenceEquals(dp.Type, name),
            TypePatternSyntax tp => ReferenceEquals(tp.Type, name),
            BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AsExpression) || bin.IsKind(SyntaxKind.IsExpression) => ReferenceEquals(bin.Right, name),
            _ => false,
        };
    }
}
