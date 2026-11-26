using System;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// Controls which scene and editor overlay layers are drawn in the 3D viewport.
/// </summary>
[Flags]
public enum ViewportOverlayFlags
{
    None = 0,
    Normal = 1 << 0,
    Network = 1 << 1,
    Colliders = 1 << 2,
    ChunkGrid = 1 << 3,
    Anchors = 1 << 4,
    Gizmos = 1 << 5,
    Traffic = 1 << 6,
    Default = Normal | Network | ChunkGrid | Anchors | Gizmos | Traffic,
}
