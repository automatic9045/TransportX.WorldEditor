using System.Collections.Generic;
using TransportX.WorldEditor.Core.Models.Network;

namespace TransportX.WorldEditor.Core.Models.Init;

/// <summary>
/// Represents a <c>Models.LoadList(...)</c> or <c>Sounds.LoadList(...)</c> directive in <c>Init.csx</c>.
/// </summary>
public sealed record AssetListLoadReference(string? BundleKey, string RelativePath);

/// <summary>
/// Represents a keyed resource load entry such as <c>CrossSections.Load(key, path)</c> or <c>Network.LaneLayouts.Load(key, path)</c>.
/// </summary>
public sealed record ResourceLoadReference(string Key, string RelativePath);

/// <summary>
/// Represents the canonical editor-managed portion (plus trailing user code) of <c>Init.csx</c>.
/// </summary>
public sealed class InitDocument
{
    public string RelativePath { get; set; } = "Init.csx";
    public List<AssetListLoadReference> ModelLists { get; } = new List<AssetListLoadReference>();
    public List<AssetListLoadReference> SoundLists { get; } = new List<AssetListLoadReference>();
    public List<ResourceLoadReference> CrossSections { get; } = new List<ResourceLoadReference>();
    public LaneTrafficConfigModel LaneTraffic { get; } = new LaneTrafficConfigModel();
    public List<ResourceLoadReference> LaneLayouts { get; } = new List<ResourceLoadReference>();
    public string? UserCode { get; set; }
}
