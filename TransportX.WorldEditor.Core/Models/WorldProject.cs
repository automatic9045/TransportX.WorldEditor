using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Templates;

namespace TransportX.WorldEditor.Core.Models;

/// <summary>
/// Root aggregate representing an editable TransportX world folder and all associated resources.
/// </summary>
public sealed class WorldProject
{
    public string WorldInfoFilePath { get; set; } = string.Empty;
    public string WorldDirectory { get; set; } = string.Empty;

    public WorldInfoModel WorldInfo { get; set; } = new WorldInfoModel();
    public ScriptManifestModel Manifest { get; set; } = new ScriptManifestModel();
    public InitDocument Init { get; set; } = new InitDocument();
    public WorldPlacementDocument Placement { get; set; } = new WorldPlacementDocument();
    public EditorSidecarMetadata Sidecar { get; set; } = new EditorSidecarMetadata();

    public Dictionary<string, ModelListDocument> ModelLists { get; } = new Dictionary<string, ModelListDocument>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SoundListDocument> SoundLists { get; } = new Dictionary<string, SoundListDocument>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LaneLayoutDocument> LaneLayouts { get; } = new Dictionary<string, LaneLayoutDocument>(StringComparer.Ordinal);
    public Dictionary<string, CrossSectionDocument> CrossSections { get; } = new Dictionary<string, CrossSectionDocument>(StringComparer.Ordinal);
    public Dictionary<string, EnvironmentProfileDocument> Environments { get; } = new Dictionary<string, EnvironmentProfileDocument>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SplineTemplateDocument> SplineTemplates { get; } = new Dictionary<string, SplineTemplateDocument>(StringComparer.Ordinal);
    public Dictionary<string, JunctionTemplateDocument> JunctionTemplates { get; } = new Dictionary<string, JunctionTemplateDocument>(StringComparer.Ordinal);

    /// <summary>
    /// Normalized relative paths of files marked read-only because their <c>&lt;editor-generated&gt;</c>
    /// region failed canonical AST parsing.
    /// </summary>
    public HashSet<string> ProtectedFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tracks files with unsaved modifications for differential persistence.
    /// </summary>
    public HashSet<WorldFileTarget> DirtyFiles { get; } = new HashSet<WorldFileTarget>();

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.Replace('\\', '/').TrimStart('.', '/');
    }

    public bool IsProtected(string relativePath)
    {
        return ProtectedFiles.Contains(NormalizeRelativePath(relativePath));
    }

    public void MarkDirty(WorldFileKind kind, string relativePath)
    {
        DirtyFiles.Add(new WorldFileTarget(kind, NormalizeRelativePath(relativePath)));
    }
}
