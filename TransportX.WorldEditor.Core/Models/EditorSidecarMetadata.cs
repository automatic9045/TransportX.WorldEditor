using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Models;

/// <summary>
/// Represents a saved camera pose bookmark stored in <c>__Editor.json</c>.
/// </summary>
public sealed record CameraBookmarkModel(
    string Name,
    int ChunkX,
    int ChunkZ,
    double X,
    double Y,
    double Z,
    double RotationX,
    double RotationY,
    double RotationZ);

/// <summary>
/// Represents editor-only 3D viewport layer visibility preferences.
/// </summary>
public sealed record VisibilityLayerSettings(
    bool Normal = true,
    bool Network = true,
    bool Colliders = false,
    bool ChunkGrid = true,
    bool Anchors = true,
    bool Gizmos = true,
    bool Traffic = true);

/// <summary>
/// Represents an optional reference image overlay on the XZ ground plane.
/// </summary>
public sealed record BackgroundMapReference(
    string Name,
    string ImagePath,
    int ChunkX,
    int ChunkZ,
    double X,
    double Y,
    double Z,
    double WidthMeters,
    double HeightMeters,
    double Opacity,
    bool Visible);

/// <summary>
/// Represents optional, deletable editor-only metadata persisted in <c>__Editor.json</c>.
/// </summary>
public sealed class EditorSidecarMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public CameraBookmarkModel? LastCameraPose { get; set; }
    public List<CameraBookmarkModel> CameraBookmarks { get; } = new List<CameraBookmarkModel>();
    public VisibilityLayerSettings VisibilityLayers { get; set; } = new VisibilityLayerSettings();
    public List<string> HiddenNodeIds { get; } = new List<string>();
    public List<BackgroundMapReference> BackgroundMaps { get; } = new List<BackgroundMapReference>();
    public Dictionary<string, string> AnchorLabels { get; } = new Dictionary<string, string>();
    public string? PlayerExecutablePath { get; set; }
}
