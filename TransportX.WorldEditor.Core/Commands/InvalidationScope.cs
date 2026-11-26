namespace TransportX.WorldEditor.Core.Commands;

/// <summary>
/// Specifies the minimal 3D preview rebuild scope required after executing or undoing an editor command.
/// </summary>
public enum InvalidationScope
{
    None = 0,
    ViewportOnly = 1,
    SingleElement = 2,
    TemplateDependents = 3,
    FullRebuild = 4,
}
