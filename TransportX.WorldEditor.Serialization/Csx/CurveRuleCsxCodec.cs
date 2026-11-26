using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Serialization.Csx;

/// <summary>
/// Shared AST parser and canonical code formatter for <c>PutProp</c>, <c>PutExtrusion</c>,
/// and <c>StraightTo</c>/<c>BezierTo</c> segments shared between <see cref="JunctionTemplateCsxSerializer"/>
/// and <see cref="WorldCsxSerializer"/>.
/// </summary>
internal static class CurveRuleCsxCodec
{
    public static bool TryParsePropBody(
        SeparatedSyntaxList<ArgumentSyntax> args,
        int startIdx,
        out List<string> modelKeys,
        out PoseDegreesModel pose,
        out AnchoredDistance from,
        out double span,
        out double interval,
        out int count)
    {
        modelKeys = new List<string>();
        pose = PoseDegreesModel.Identity;
        from = AnchoredDistance.Zero;
        span = 0.0;
        interval = 1.0;
        count = int.MaxValue;

        if (args.Count <= startIdx || !RoslynCsxHelpers.TryGetStringList(args[startIdx].Expression, out modelKeys))
        {
            return false;
        }

        int idx = startIdx + 1;
        int rem = args.Count - idx;
        bool hasRot;
        bool hasCount;

        if (rem == 6)
        {
            hasRot = false;
            hasCount = false;
        }
        else if (rem == 7)
        {
            hasRot = false;
            hasCount = true;
        }
        else if (rem == 9)
        {
            hasRot = true;
            hasCount = false;
        }
        else if (rem == 10)
        {
            hasRot = true;
            hasCount = true;
        }
        else
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
        if (hasRot)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out rx)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out ry)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out rz))
            {
                return false;
            }
        }

        if (!RoslynCsxHelpers.TryParseAnchoredDistance(args[idx++], out from, out bool isMax) || isMax)
        {
            return false;
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out span)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out interval))
        {
            return false;
        }

        if (hasCount && !RoslynCsxHelpers.TryGetInt(args[idx].Expression, out count))
        {
            return false;
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }

    public static bool TryParseCurveProp(SeparatedSyntaxList<ArgumentSyntax> args, out CurvePropPlacementRule? rule)
    {
        rule = null;
        if (!TryParsePropBody(
            args,
            0,
            out List<string> models,
            out PoseDegreesModel pose,
            out AnchoredDistance from,
            out double span,
            out double interval,
            out int count))
        {
            return false;
        }

        rule = new CurvePropPlacementRule
        {
            Pose = pose,
            From = from,
            Span = span,
            Interval = interval,
            Count = count,
        };
        rule.ModelKeys.AddRange(models);
        return true;
    }

    public static bool TryParseCurveExtrusion(
        SeparatedSyntaxList<ArgumentSyntax> args,
        out CurveExtrusionPlacementRule? rule)
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
        double maxDeviation;
        double maxAngleDelta;
        double vScale = 1.0;
        double vOffset = 0.0;
        AnchoredDistance from;
        AnchoredDistance? to = null;

        bool hasXY = rem == 6 || rem == 7 || rem == 8 || rem == 9;
        bool hasUV = rem == 8 || rem == 9;
        bool hasTo = rem == 5 || rem == 7 || rem == 9;

        if (rem < 4 || 9 < rem)
        {
            return false;
        }

        if (hasXY)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetX)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out offsetY))
            {
                return false;
            }
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out baseInterval)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxDeviation)
            || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out maxAngleDelta))
        {
            return false;
        }

        if (hasUV)
        {
            if (!RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out vScale)
                || !RoslynCsxHelpers.TryGetDouble(args[idx++].Expression, out vOffset))
            {
                return false;
            }
        }

        if (!RoslynCsxHelpers.TryParseAnchoredDistance(args[idx++], out from, out bool fromMax) || fromMax)
        {
            return false;
        }

        if (hasTo)
        {
            if (!RoslynCsxHelpers.TryParseAnchoredDistance(args[idx], out AnchoredDistance toVal, out bool toIsMax))
            {
                return false;
            }

            to = toIsMax ? null : toVal;
        }

        rule = new CurveExtrusionPlacementRule
        {
            Key = key,
            CrossSectionKey = csKey,
            OffsetX = offsetX,
            OffsetY = offsetY,
            BaseInterval = baseInterval,
            MaxDeviation = maxDeviation,
            MaxAngleDeltaDeg = maxAngleDelta,
            VIntervalScale = vScale,
            VOffset = vOffset,
            From = from,
            To = to,
        };
        return true;
    }

    public static string FormatCurveProp(string indent, string targetExpr, CurvePropPlacementRule prop)
    {
        return $"{indent}{targetExpr}.PutProp({RoslynCsxHelpers.FormatStringArray(prop.ModelKeys)}, "
            + $"{RoslynCsxHelpers.FormatPoseArgs(prop.Pose)}, "
            + $"{DistanceExprCodec.Format(prop.From)}, "
            + $"{RoslynCsxHelpers.FormatNumber(prop.Span)}, "
            + $"{RoslynCsxHelpers.FormatNumber(prop.Interval)}, "
            + $"{(prop.Count == int.MaxValue ? "int.MaxValue" : prop.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))});\n";
    }

    public static string FormatCurveExtrusion(string indent, string targetExpr, CurveExtrusionPlacementRule ext)
    {
        string keyPrefix = ext.Key is null ? string.Empty : $"{RoslynCsxHelpers.FormatStringLiteral(ext.Key)}, ";
        return $"{indent}{targetExpr}.PutExtrusion({keyPrefix}"
            + $"{RoslynCsxHelpers.FormatStringLiteral(ext.CrossSectionKey)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.OffsetX)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.OffsetY)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.BaseInterval)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.MaxDeviation)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.MaxAngleDeltaDeg)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.VIntervalScale)}, "
            + $"{RoslynCsxHelpers.FormatNumber(ext.VOffset)}, "
            + $"{DistanceExprCodec.Format(ext.From)}, "
            + $"{DistanceExprCodec.FormatNullableUpperBound(ext.To)});\n";
    }

    public static bool TryParseJunctionCurveSegment(
        InvocationExpressionSyntax call,
        bool allowToEnd,
        out JunctionCurveSegment? segment)
    {
        segment = null;
        string? method = RoslynCsxHelpers.GetInvokedMethodName(call);
        if (method is null)
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> args = call.ArgumentList.Arguments;

        if (method == "StraightTo")
        {
            if (args.Count != 6 && args.Count != 7)
            {
                return false;
            }

            if (!TryReadPose6(args, 0, out PoseDegreesModel pose))
            {
                return false;
            }

            string? anchorId = null;
            if (args.Count == 7 && !RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[6], out anchorId))
            {
                return false;
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, pose, 0.5, anchorId);
            return true;
        }

        if (method == "BezierTo")
        {
            if (args.Count < 6 || 8 < args.Count)
            {
                return false;
            }

            if (!TryReadPose6(args, 0, out PoseDegreesModel pose))
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;

            if (args.Count == 7)
            {
                if (RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[6], out anchorId))
                {
                }
                else if (!RoslynCsxHelpers.TryGetDouble(args[6].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 8)
            {
                if (!RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[6], out anchorId)
                    || !RoslynCsxHelpers.TryGetDouble(args[7].Expression, out handleScale))
                {
                    return false;
                }
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.BezierTo, pose, handleScale, anchorId);
            return true;
        }

        if (allowToEnd && method == "StraightToEnd")
        {
            string? anchorId = null;
            if (args.Count == 1 && !RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[0], out anchorId))
            {
                return false;
            }

            if (1 < args.Count)
            {
                return false;
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity, 0.5, anchorId);
            return true;
        }

        if (allowToEnd && method == "BezierToEnd")
        {
            if (2 < args.Count)
            {
                return false;
            }

            string? anchorId = null;
            double handleScale = 0.5;

            if (args.Count == 1)
            {
                if (RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[0], out anchorId))
                {
                }
                else if (!RoslynCsxHelpers.TryGetDouble(args[0].Expression, out handleScale))
                {
                    return false;
                }
            }
            else if (args.Count == 2)
            {
                if (!RoslynCsxHelpers.IsCanonicalOutAnchorArgument(args[0], out anchorId)
                    || !RoslynCsxHelpers.TryGetDouble(args[1].Expression, out handleScale))
                {
                    return false;
                }
            }

            segment = new JunctionCurveSegment(JunctionCurveSegmentKind.BezierToEnd, PoseDegreesModel.Identity, handleScale, anchorId);
            return true;
        }

        return false;
    }

    public static bool TryReadPose6(SeparatedSyntaxList<ArgumentSyntax> args, int startIdx, out PoseDegreesModel pose)
    {
        pose = PoseDegreesModel.Identity;
        if (args.Count < startIdx + 6)
        {
            return false;
        }

        if (!RoslynCsxHelpers.TryGetDouble(args[startIdx].Expression, out double x)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 1].Expression, out double y)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 2].Expression, out double z)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 3].Expression, out double rx)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 4].Expression, out double ry)
            || !RoslynCsxHelpers.TryGetDouble(args[startIdx + 5].Expression, out double rz))
        {
            return false;
        }

        pose = new PoseDegreesModel(x, y, z, rx, ry, rz);
        return true;
    }
}
