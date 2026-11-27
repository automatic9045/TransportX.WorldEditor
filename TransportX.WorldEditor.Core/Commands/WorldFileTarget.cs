namespace TransportX.WorldEditor.Core.Commands;

/// <summary>
/// Classifies the TransportX or editor-sidecar file affected by an operation.
/// </summary>
public enum WorldFileKind
{
    WorldInfo,
    Manifest,
    InitCsx,
    TemplatesLoadCsx,
    WorldCsx,
    SplineTemplateCsx,
    JunctionTemplateCsx,
    ModelsTxt,
    SoundsTxt,
    CrossSectionsTxt,
    LaneLayoutXml,
    CrossSectionXml,
    EnvironmentXml,
    EditorCsx,
    EditorJson,
}

/// <summary>
/// Identifies a relative file target within a TransportX world project.
/// </summary>
public readonly record struct WorldFileTarget(WorldFileKind Kind, string RelativePath);
