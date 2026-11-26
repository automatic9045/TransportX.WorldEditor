using System;
using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models.Network;

/// <summary>
/// Flow direction of a lane relative to its parent port (matches <c>TransportX.Network.FlowDirections</c>).
/// </summary>
public enum LaneFlowDirections
{
    In = 1,
    Out = 2,
    InOut = 3,
}

/// <summary>
/// Represents a single lane slot within a <c>LaneLayout</c> XML document.
/// </summary>
public sealed record LaneDefinition(
    string AllowedTrafficKey,
    LaneFlowDirections Directions,
    float X,
    float Y,
    float LeftWidth,
    float RightWidth)
{
    public static LaneFlowDirections ReverseDirection(LaneFlowDirections directions)
    {
        return directions switch
        {
            LaneFlowDirections.In => LaneFlowDirections.Out,
            LaneFlowDirections.Out => LaneFlowDirections.In,
            LaneFlowDirections.InOut => LaneFlowDirections.InOut,
            _ => throw new ArgumentOutOfRangeException(nameof(directions)),
        };
    }

    public LaneDefinition CreateOpposition()
    {
        return new LaneDefinition(
            AllowedTrafficKey,
            ReverseDirection(Directions),
            -X,
            Y,
            RightWidth,
            LeftWidth);
    }

    public bool IsOppositeOf(LaneDefinition other, float tolerance = 1e-4f)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(AllowedTrafficKey, other.AllowedTrafficKey, StringComparison.Ordinal)
            && Directions == ReverseDirection(other.Directions)
            && MathF.Abs(X + other.X) <= tolerance
            && MathF.Abs(Y - other.Y) <= tolerance
            && MathF.Abs(LeftWidth - other.RightWidth) <= tolerance
            && MathF.Abs(RightWidth - other.LeftWidth) <= tolerance;
    }
}

/// <summary>
/// Represents a <c>LaneLayout*.xml</c> resource loaded via <c>Network.LaneLayouts.Load</c>.
/// </summary>
public sealed class LaneLayoutDocument
{
    public string Key { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public List<LaneDefinition> Lanes { get; } = new List<LaneDefinition>();

    public LaneLayoutDocument CreateOpposition(string oppositionKey = "", string oppositionRelativePath = "")
    {
        LaneLayoutDocument result = new LaneLayoutDocument
        {
            Key = oppositionKey,
            RelativePath = oppositionRelativePath,
        };

        for (int i = Lanes.Count - 1; 0 <= i; i--)
        {
            result.Lanes.Add(Lanes[i].CreateOpposition());
        }

        return result;
    }

    /// <summary>
    /// Validates whether this layout can connect face-to-face with <paramref name="other"/>
    /// according to TransportX's reverse-mirrored port connection rules.
    /// </summary>
    public bool CanConnectTo(LaneLayoutDocument other, float tolerance = 1e-4f)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Lanes.Count != other.Lanes.Count)
        {
            return false;
        }

        for (int i = 0; i < Lanes.Count; i++)
        {
            LaneDefinition from = Lanes[i];
            LaneDefinition to = other.Lanes[Lanes.Count - 1 - i];
            if (!from.IsOppositeOf(to, tolerance))
            {
                return false;
            }
        }

        return true;
    }
}
