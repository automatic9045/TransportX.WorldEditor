using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Commands;

/// <summary>
/// Represents an undoable and redoable editing operation on the world model.
/// </summary>
public interface IEditorCommand
{
    string Description { get; }
    InvalidationScope Invalidation { get; }
    IReadOnlyList<WorldFileTarget> AffectedFiles { get; }

    void Execute();
    void Undo();
}

/// <summary>
/// Represents an undoable command that can coalesce consecutive edits (such as gizmo drags).
/// </summary>
public interface IMergeableEditorCommand : IEditorCommand
{
    bool CanMergeWith(IEditorCommand next);
    IEditorCommand MergeWith(IEditorCommand next);
}
