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
    public const string AnchorVariablePrefix = "s_";

    public static string FormatAnchorVariableName(string anchorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        return $"{AnchorVariablePrefix}{anchorId}";
    }

    public static bool TryParseAnchorVariableName(string varName, out string anchorId)
    {
        if (!string.IsNullOrEmpty(varName)
            && varName.StartsWith(AnchorVariablePrefix, StringComparison.Ordinal)
            && AnchorVariablePrefix.Length < varName.Length)
        {
            anchorId = varName[AnchorVariablePrefix.Length..];
            return true;
        }

        anchorId = string.Empty;
        return false;
    }

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

        string varName = FormatAnchorVariableName(distance.AnchorId);
        if (distance.Offset == 0.0)
        {
            return varName;
        }

        if (0.0 < distance.Offset)
        {
            return $"{varName} + {FormatNumber(distance.Offset)}";
        }

        return $"{varName} - {FormatNumber(-distance.Offset)}";
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

        if (expression is IdentifierNameSyntax ident
            && TryParseAnchorVariableName(ident.Identifier.ValueText, out string anchorId))
        {
            result = AnchoredDistance.RelativeTo(anchorId, 0.0);
            return true;
        }

        if (expression is BinaryExpressionSyntax binary
            && binary.Left is IdentifierNameSyntax leftIdent
            && TryParseAnchorVariableName(leftIdent.Identifier.ValueText, out string leftAnchorId)
            && RoslynCsxHelpers.TryGetDouble(binary.Right, out double delta))
        {
            if (binary.IsKind(SyntaxKind.AddExpression))
            {
                result = AnchoredDistance.RelativeTo(leftAnchorId, delta);
                return true;
            }

            if (binary.IsKind(SyntaxKind.SubtractExpression))
            {
                result = AnchoredDistance.RelativeTo(leftAnchorId, -delta);
                return true;
            }
        }

        return false;
    }
}
