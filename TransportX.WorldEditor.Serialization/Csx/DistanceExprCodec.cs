using System;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Primitives;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Parses and formats canonical <see cref="AnchoredDistance"/> C# script expressions:
/// <c>Number | double.MaxValue | Ident | Ident + Number | Ident - Number</c>.
/// </summary>
public static class DistanceExprCodec
{
    public static string FormatNumber(double value)
    {
        double normalized = value == 0.0 ? 0.0 : value;
        return normalized.ToString("G15", CultureInfo.InvariantCulture);
    }

    public static string Format(AnchoredDistance distance)
    {
        if (string.IsNullOrEmpty(distance.AnchorId))
        {
            return FormatNumber(distance.Offset);
        }

        if (distance.Offset == 0.0)
        {
            return distance.AnchorId;
        }

        if (0.0 < distance.Offset)
        {
            return $"{distance.AnchorId} + {FormatNumber(distance.Offset)}";
        }

        return $"{distance.AnchorId} - {FormatNumber(-distance.Offset)}";
    }

    public static string FormatNullableUpperBound(AnchoredDistance? distance)
    {
        return distance.HasValue ? Format(distance.Value) : "double.MaxValue";
    }

    public static bool TryParse(
        ExpressionSyntax expression,
        out AnchoredDistance result,
        out bool isDoubleMaxValue)
    {
        ArgumentNullException.ThrowIfNull(expression);

        result = AnchoredDistance.Zero;
        isDoubleMaxValue = false;

        if (RoslynCsxHelpers.IsDoubleMaxValue(expression))
        {
            isDoubleMaxValue = true;
            result = AnchoredDistance.Absolute(double.MaxValue);
            return true;
        }

        if (RoslynCsxHelpers.TryGetDouble(expression, out double absoluteValue))
        {
            result = AnchoredDistance.Absolute(absoluteValue);
            return true;
        }

        if (expression is IdentifierNameSyntax ident)
        {
            result = AnchoredDistance.RelativeTo(ident.Identifier.ValueText, 0.0);
            return true;
        }

        if (expression is BinaryExpressionSyntax binary
            && binary.Left is IdentifierNameSyntax leftIdent
            && RoslynCsxHelpers.TryGetDouble(binary.Right, out double delta))
        {
            if (binary.IsKind(SyntaxKind.AddExpression))
            {
                result = AnchoredDistance.RelativeTo(leftIdent.Identifier.ValueText, delta);
                return true;
            }

            if (binary.IsKind(SyntaxKind.SubtractExpression))
            {
                result = AnchoredDistance.RelativeTo(leftIdent.Identifier.ValueText, -delta);
                return true;
            }
        }

        return false;
    }
}
