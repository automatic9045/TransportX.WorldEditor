using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models.Network;

/// <summary>
/// Represents a <c>Network.LaneTraffic.AddType(...)</c> definition.
/// </summary>
public sealed record LaneTrafficTypeDefinition(
    string Key,
    string DisplayName,
    string ColorHex);

/// <summary>
/// Represents a <c>Network.LaneTraffic.AddGroup(...)</c> union definition.
/// </summary>
public sealed record LaneTrafficGroupDefinition(
    string Key,
    List<string> TypeKeys,
    string ColorHex);

/// <summary>
/// Represents all lane traffic types and groups registered in <c>Init.csx</c>.
/// </summary>
public sealed class LaneTrafficConfigModel
{
    public List<LaneTrafficTypeDefinition> Types { get; } = new List<LaneTrafficTypeDefinition>();
    public List<LaneTrafficGroupDefinition> Groups { get; } = new List<LaneTrafficGroupDefinition>();
}
