using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Network;

namespace TransportX.WorldEditor.Core.Models.Init;

/// <summary>
/// Represents a <c>Models.LoadList(...)</c> or <c>Sounds.LoadList(...)</c> directive in <c>Init.csx</c>.
/// </summary>
public sealed record AssetListLoadReference(string? BundleKey, string RelativePath);

/// <summary>
/// Represents a keyed resource load entry such as <c>Network.LaneLayouts.Load(key, path)</c>.
/// </summary>
public sealed record ResourceLoadReference(string Key, string RelativePath);

/// <summary>
/// Represents the canonical editor-managed portion (plus trailing user code) of <c>Init.csx</c>.
/// </summary>
public sealed class InitDocument
{
    public string RelativePath { get; set; } = "Init.csx";
    public List<AssetListLoadReference> ModelLists { get; } = new();
    public List<AssetListLoadReference> SoundLists { get; } = new();
    public List<string> CrossSectionLists { get; } = new();
    public LaneTrafficConfigModel LaneTraffic { get; } = new();
    public List<ResourceLoadReference> LaneLayouts { get; } = new();
    public string? UserCode { get; set; }
}
