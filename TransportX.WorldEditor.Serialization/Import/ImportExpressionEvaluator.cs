using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Serialization.Csx;

namespace TransportX.WorldEditor.Serialization.Import;

/// <summary>
/// Lexically scoped evaluator and identifier rewriter used when importing arbitrary TransportX C# scripts.
/// </summary>
internal sealed class ImportExpressionEvaluator
{
    private const double IntegerTolerance = 1e-9;

    private sealed class EvalScope
    {
        public Dictionary<string, double> Doubles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);
    }

    private readonly List<EvalScope> Scopes = new();

    public ImportExpressionEvaluator()
    {
        Scopes.Add(new EvalScope());
    }

    public void PushScope()
    {
        Scopes.Add(new EvalScope());
    }

    public void PopScope()
    {
        if (1 < Scopes.Count)
        {
            Scopes.RemoveAt(Scopes.Count - 1);
        }
    }

    public void SetDouble(string name, double value, bool preferExistingScope)
    {
        if (preferExistingScope)
        {
            for (int i = Scopes.Count - 1; 0 <= i; i--)
            {
                if (Scopes[i].Doubles.ContainsKey(name))
                {
                    Scopes[i].Doubles[name] = value;
                    return;
                }
            }
        }

        Scopes[^1].Doubles[name] = value;
    }

    public void SetString(string name, string value, bool preferExistingScope)
    {
        if (preferExistingScope)
        {
            for (int i = Scopes.Count - 1; 0 <= i; i--)
            {
                if (Scopes[i].Strings.ContainsKey(name))
                {
                    Scopes[i].Strings[name] = value;
                    return;
                }
            }
        }

        Scopes[^1].Strings[name] = value;
    }

    public bool TryGetDouble(string name, out double value)
    {
        for (int i = Scopes.Count - 1; 0 <= i; i--)
        {
            if (Scopes[i].Doubles.TryGetValue(name, out value))
            {
                return true;
            }
        }

        value = 0.0;
        return false;
    }

    public bool TryGetString(string name, out string value)
    {
        for (int i = Scopes.Count - 1; 0 <= i; i--)
        {
            if (Scopes[i].Strings.TryGetValue(name, out string? found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public IReadOnlyDictionary<string, double> SnapshotDoubles()
    {
        Dictionary<string, double> merged = new(StringComparer.Ordinal);
        foreach (EvalScope scope in Scopes)
        {
            foreach (KeyValuePair<string, double> kv in scope.Doubles)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }

    public bool TryEvalDouble(ExpressionSyntax expression, out double value)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (RoslynCsxHelpers.IsDoubleMaxValue(expression))
        {
            value = double.MaxValue;
            return true;
        }

        if (RoslynCsxHelpers.TryGetDouble(expression, out double literalVal))
        {
            value = literalVal;
            return true;
        }

        if (expression is ParenthesizedExpressionSyntax parens)
        {
            return TryEvalDouble(parens.Expression, out value);
        }

        if (expression is CastExpressionSyntax cast)
        {
            return TryEvalDouble(cast.Expression, out value);
        }

        if (expression is PrefixUnaryExpressionSyntax prefix && TryEvalDouble(prefix.Operand, out double unaryVal))
        {
            if (prefix.IsKind(SyntaxKind.UnaryMinusExpression))
            {
                value = -unaryVal;
                return true;
            }

            if (prefix.IsKind(SyntaxKind.UnaryPlusExpression))
            {
                value = unaryVal;
                return true;
            }
        }

        if (expression is IdentifierNameSyntax ident && TryGetDouble(ident.Identifier.ValueText, out double varVal))
        {
            value = varVal;
            return true;
        }

        if (expression is MemberAccessExpressionSyntax ma && TryEvalKnownMathConstant(ma, out double constVal))
        {
            value = constVal;
            return true;
        }

        if (expression is InvocationExpressionSyntax inv && TryEvalMathCall(inv, out double callVal))
        {
            value = callVal;
            return true;
        }

        if (expression is BinaryExpressionSyntax bin
            && TryEvalDouble(bin.Left, out double leftVal)
            && TryEvalDouble(bin.Right, out double rightVal))
        {
            if (bin.IsKind(SyntaxKind.AddExpression))
            {
                value = leftVal + rightVal;
                return double.IsFinite(value);
            }

            if (bin.IsKind(SyntaxKind.SubtractExpression))
            {
                value = leftVal - rightVal;
                return double.IsFinite(value);
            }

            if (bin.IsKind(SyntaxKind.MultiplyExpression))
            {
                value = leftVal * rightVal;
                return double.IsFinite(value);
            }

            if (bin.IsKind(SyntaxKind.DivideExpression) && rightVal != 0.0)
            {
                value = leftVal / rightVal;
                return double.IsFinite(value);
            }

            if (bin.IsKind(SyntaxKind.ModuloExpression) && rightVal != 0.0)
            {
                value = leftVal % rightVal;
                return double.IsFinite(value);
            }
        }

        value = 0.0;
        return false;
    }

    public bool TryEvalInt(ExpressionSyntax expression, out int value)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (RoslynCsxHelpers.IsIntMaxValue(expression))
        {
            value = int.MaxValue;
            return true;
        }

        if (RoslynCsxHelpers.TryGetInt(expression, out int direct))
        {
            value = direct;
            return true;
        }

        if (TryEvalDouble(expression, out double dbl)
            && double.IsFinite(dbl)
            && int.MinValue <= dbl
            && dbl <= int.MaxValue)
        {
            double rounded = double.Round(dbl);
            if (double.Abs(dbl - rounded) < IntegerTolerance)
            {
                value = (int)rounded;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public bool TryEvalString(ExpressionSyntax expression, out string value)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (RoslynCsxHelpers.TryGetString(expression, out string lit))
        {
            value = lit;
            return true;
        }

        if (expression is ParenthesizedExpressionSyntax parens)
        {
            return TryEvalString(parens.Expression, out value);
        }

        if (expression is IdentifierNameSyntax ident && TryGetString(ident.Identifier.ValueText, out string varVal))
        {
            value = varVal;
            return true;
        }

        if (expression is BinaryExpressionSyntax bin
            && bin.IsKind(SyntaxKind.AddExpression)
            && TryEvalString(bin.Left, out string leftStr)
            && TryEvalString(bin.Right, out string rightStr))
        {
            value = leftStr + rightStr;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public bool TryEvalNullableString(ExpressionSyntax expression, out string? value)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression is LiteralExpressionSyntax nullLiteral && nullLiteral.IsKind(SyntaxKind.NullLiteralExpression))
        {
            value = null;
            return true;
        }

        if (TryEvalString(expression, out string nonNull))
        {
            value = nonNull;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryEvalStringList(ExpressionSyntax expression, out List<string> values)
    {
        values = new List<string>();

        if (expression is CollectionExpressionSyntax collection)
        {
            foreach (CollectionElementSyntax element in collection.Elements)
            {
                if (element is ExpressionElementSyntax exprEl && TryEvalString(exprEl.Expression, out string item))
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

        InitializerExpressionSyntax? init = expression switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer,
            ArrayCreationExpressionSyntax array => array.Initializer,
            _ => null,
        };

        if (init is not null)
        {
            foreach (ExpressionSyntax itemExpr in init.Expressions)
            {
                if (!TryEvalString(itemExpr, out string item))
                {
                    return false;
                }

                values.Add(item);
            }

            return true;
        }

        return false;
    }

    public bool TryEvalAnchoredDistance(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string> anchorAliases,
        out AnchoredDistance result,
        out bool isDoubleMaxValue)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(anchorAliases);

        result = AnchoredDistance.Zero;
        isDoubleMaxValue = false;

        if (RoslynCsxHelpers.IsDoubleMaxValue(expression))
        {
            isDoubleMaxValue = true;
            result = AnchoredDistance.Absolute(double.MaxValue);
            return true;
        }

        if (TryEvalDouble(expression, out double numericValue))
        {
            result = AnchoredDistance.Absolute(numericValue);
            return true;
        }

        if (expression is ParenthesizedExpressionSyntax parens)
        {
            return TryEvalAnchoredDistance(parens.Expression, anchorAliases, out result, out isDoubleMaxValue);
        }

        if (expression is IdentifierNameSyntax ident
            && anchorAliases.TryGetValue(ident.Identifier.ValueText, out string? ssaAnchor))
        {
            result = AnchoredDistance.RelativeTo(ssaAnchor, 0.0);
            return true;
        }

        if (expression is BinaryExpressionSyntax bin)
        {
            if (bin.Left is IdentifierNameSyntax leftIdent
                && anchorAliases.TryGetValue(leftIdent.Identifier.ValueText, out string? leftSsa)
                && TryEvalDouble(bin.Right, out double rightDelta))
            {
                if (bin.IsKind(SyntaxKind.AddExpression))
                {
                    result = AnchoredDistance.RelativeTo(leftSsa, rightDelta);
                    return true;
                }

                if (bin.IsKind(SyntaxKind.SubtractExpression))
                {
                    result = AnchoredDistance.RelativeTo(leftSsa, -rightDelta);
                    return true;
                }
            }

            if (bin.Right is IdentifierNameSyntax rightIdent
                && anchorAliases.TryGetValue(rightIdent.Identifier.ValueText, out string? rightSsa)
                && bin.IsKind(SyntaxKind.AddExpression)
                && TryEvalDouble(bin.Left, out double leftDelta))
            {
                result = AnchoredDistance.RelativeTo(rightSsa, leftDelta);
                return true;
            }
        }

        return false;
    }

    public static TNode RewriteSyntaxForUserCode<TNode>(
        TNode node,
        IReadOnlyDictionary<string, string> identifierMap,
        IReadOnlyDictionary<string, double>? scalarSubstitutions = null)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(identifierMap);

        if (identifierMap.Count == 0 && (scalarSubstitutions is null || scalarSubstitutions.Count == 0))
        {
            return node;
        }

        UserCodeSyntaxRewriter rewriter = new(identifierMap, scalarSubstitutions);
        return (TNode)rewriter.Visit(node);
    }

    private static bool TryEvalKnownMathConstant(MemberAccessExpressionSyntax ma, out double value)
    {
        string typeText = ma.Expression.ToString();
        string memberName = ma.Name.Identifier.ValueText;

        if ((typeText == "double" || typeText == "Double" || typeText == "System.Double"
            || typeText == "float" || typeText == "Single" || typeText == "System.Single"
            || typeText == "Math" || typeText == "System.Math"
            || typeText == "MathF" || typeText == "System.MathF")
            && (memberName == "PI" || memberName == "Pi"))
        {
            value = double.Pi;
            return true;
        }

        if ((typeText == "double" || typeText == "Double" || typeText == "System.Double"
            || typeText == "float" || typeText == "Single" || typeText == "System.Single"
            || typeText == "Math" || typeText == "System.Math"
            || typeText == "MathF" || typeText == "System.MathF")
            && memberName == "Tau")
        {
            value = double.Tau;
            return true;
        }

        value = 0.0;
        return false;
    }

    private bool TryEvalMathCall(InvocationExpressionSyntax inv, out double value)
    {
        value = 0.0;
        if (inv.Expression is not MemberAccessExpressionSyntax ma || inv.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        string typeText = ma.Expression.ToString();
        if (typeText != "Math" && typeText != "System.Math"
            && typeText != "double" && typeText != "Double" && typeText != "System.Double"
            && typeText != "float" && typeText != "Single" && typeText != "System.Single")
        {
            return false;
        }

        if (!TryEvalDouble(inv.ArgumentList.Arguments[0].Expression, out double arg))
        {
            return false;
        }

        switch (ma.Name.Identifier.ValueText)
        {
            case "Sin":
                value = double.Sin(arg);
                return double.IsFinite(value);
            case "Cos":
                value = double.Cos(arg);
                return double.IsFinite(value);
            case "Tan":
                value = double.Tan(arg);
                return double.IsFinite(value);
            case "Sqrt" when 0.0 <= arg:
                value = double.Sqrt(arg);
                return double.IsFinite(value);
            case "Abs":
                value = double.Abs(arg);
                return double.IsFinite(value);
            case "Floor":
                value = double.Floor(arg);
                return double.IsFinite(value);
            case "Ceiling":
                value = double.Ceiling(arg);
                return double.IsFinite(value);
            case "Round":
                value = double.Round(arg);
                return double.IsFinite(value);
            default:
                return false;
        }
    }

    private sealed class UserCodeSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, string> IdentifierMap;
        private readonly IReadOnlyDictionary<string, double>? ScalarSubstitutions;

        public UserCodeSyntaxRewriter(
            IReadOnlyDictionary<string, string> identifierMap,
            IReadOnlyDictionary<string, double>? scalarSubstitutions)
        {
            IdentifierMap = identifierMap;
            ScalarSubstitutions = scalarSubstitutions;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax ma && ReferenceEquals(ma.Name, node))
            {
                return node;
            }

            if (node.Parent is NameEqualsSyntax || node.Parent is QualifiedNameSyntax)
            {
                return node;
            }

            string text = node.Identifier.ValueText;
            if (IdentifierMap.TryGetValue(text, out string? mapped))
            {
                return SyntaxFactory.IdentifierName(mapped).WithTriviaFrom(node);
            }

            if (ScalarSubstitutions is not null && ScalarSubstitutions.TryGetValue(text, out double scalar))
            {
                string formatted = DistanceExprCodec.FormatNumber(scalar);
                ExpressionSyntax parsed = SyntaxFactory.ParseExpression(formatted);
                return parsed.WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }
    }
}
