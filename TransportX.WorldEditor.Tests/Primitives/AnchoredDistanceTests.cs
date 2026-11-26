using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Primitives;
using Xunit;

namespace TransportX.WorldEditor.Tests.Primitives;

public sealed class AnchoredDistanceTests
{
    [Fact]
    public void TryResolve_ReturnsOffsetDirectlyForAbsoluteDistance()
    {
        Dictionary<string, double> anchors = new Dictionary<string, double>();
        AnchoredDistance distance = AnchoredDistance.Absolute(42.5);

        bool resolved = distance.TryResolve(anchors, out double value);

        Assert.False(distance.IsRelative);
        Assert.True(resolved);
        Assert.Equal(42.5, value, 6);
    }

    [Fact]
    public void TryResolve_AppliesPositiveAndNegativeOffsetsRelativeToAnchor()
    {
        Dictionary<string, double> anchors = new Dictionary<string, double>
        {
            ["s_mid"] = 35.0,
            ["s_end"] = 100.0,
        };

        AnchoredDistance forward = AnchoredDistance.RelativeTo("s_mid", 12.5);
        AnchoredDistance backward = AnchoredDistance.RelativeTo("s_end", -30.0);

        Assert.True(forward.IsRelative);
        Assert.True(forward.TryResolve(anchors, out double forwardS));
        Assert.Equal(47.5, forwardS, 6);

        Assert.True(backward.TryResolve(anchors, out double backwardS));
        Assert.Equal(70.0, backwardS, 6);
    }

    [Fact]
    public void TryResolve_ReturnsFalseWhenAnchorIsMissing()
    {
        Dictionary<string, double> anchors = new Dictionary<string, double>();
        AnchoredDistance missing = AnchoredDistance.RelativeTo("s_unknown", -4.0);

        bool resolved = missing.TryResolve(anchors, out double value);

        Assert.False(resolved);
        Assert.Equal(0.0, value);
    }
}
