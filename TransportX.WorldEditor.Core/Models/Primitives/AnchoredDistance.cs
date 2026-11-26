using System;
using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models.Primitives;

/// <summary>
/// Represents a longitudinal distance along a curve or path, specified either absolutely
/// or relative to an <c>out var s</c> anchor identifier.
/// </summary>
public readonly record struct AnchoredDistance(string? AnchorId, double Offset)
{
    public static AnchoredDistance Zero { get; } = new(null, 0.0);

    public bool IsRelative => !string.IsNullOrEmpty(AnchorId);

    public static AnchoredDistance Absolute(double s)
    {
        return new AnchoredDistance(null, s);
    }

    public static AnchoredDistance RelativeTo(string anchorId, double offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        return new AnchoredDistance(anchorId, offset);
    }

    /// <summary>
    /// Resolves this parametric distance to a concrete distance using the supplied anchor lookup table.
    /// </summary>
    public bool TryResolve(IReadOnlyDictionary<string, double> anchors, out double resolvedS)
    {
        ArgumentNullException.ThrowIfNull(anchors);

        if (string.IsNullOrEmpty(AnchorId))
        {
            resolvedS = Offset;
            return true;
        }

        if (anchors.TryGetValue(AnchorId, out double anchorValue))
        {
            resolvedS = anchorValue + Offset;
            return true;
        }

        resolvedS = 0.0;
        return false;
    }
}
