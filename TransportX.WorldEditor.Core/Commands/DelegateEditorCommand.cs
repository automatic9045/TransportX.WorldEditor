using System;
using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Commands;

/// <summary>
/// General-purpose undoable command backed by explicit execute and undo delegates.
/// </summary>
public sealed class DelegateEditorCommand : IEditorCommand
{
    private readonly Action ApplyAction;
    private readonly Action RevertAction;

    public string Description { get; }
    public InvalidationScope Invalidation { get; }
    public IReadOnlyList<WorldFileTarget> AffectedFiles { get; }

    public DelegateEditorCommand(
        string description,
        InvalidationScope invalidation,
        WorldFileTarget affectedFile,
        Action applyAction,
        Action revertAction)
        : this(description, invalidation, new WorldFileTarget[] { affectedFile }, applyAction, revertAction)
    {
    }

    public DelegateEditorCommand(
        string description,
        InvalidationScope invalidation,
        IReadOnlyList<WorldFileTarget> affectedFiles,
        Action applyAction,
        Action revertAction)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(affectedFiles);
        ArgumentNullException.ThrowIfNull(applyAction);
        ArgumentNullException.ThrowIfNull(revertAction);

        Description = description;
        Invalidation = invalidation;
        AffectedFiles = affectedFiles;
        ApplyAction = applyAction;
        RevertAction = revertAction;
    }

    public void Execute()
    {
        ApplyAction();
    }

    public void Undo()
    {
        RevertAction();
    }
}

/// <summary>
/// Undoable property-change command supporting consecutive-edit coalescing via <see cref="MergeKey"/>.
/// </summary>
public sealed class MergeableValueCommand<T> : IMergeableEditorCommand
{
    private readonly Action<T> Setter;
    private readonly T OldValue;
    private readonly T NewValue;

    public string Description { get; }
    public string MergeKey { get; }
    public InvalidationScope Invalidation { get; }
    public IReadOnlyList<WorldFileTarget> AffectedFiles { get; }

    public MergeableValueCommand(
        string description,
        string mergeKey,
        InvalidationScope invalidation,
        WorldFileTarget affectedFile,
        T oldValue,
        T newValue,
        Action<T> setter)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(mergeKey);
        ArgumentNullException.ThrowIfNull(setter);

        Description = description;
        MergeKey = mergeKey;
        Invalidation = invalidation;
        AffectedFiles = new WorldFileTarget[] { affectedFile };
        OldValue = oldValue;
        NewValue = newValue;
        Setter = setter;
    }

    public void Execute()
    {
        Setter(NewValue);
    }

    public void Undo()
    {
        Setter(OldValue);
    }

    public bool CanMergeWith(IEditorCommand next)
    {
        return next is MergeableValueCommand<T> other && string.Equals(MergeKey, other.MergeKey, StringComparison.Ordinal);
    }

    public IEditorCommand MergeWith(IEditorCommand next)
    {
        MergeableValueCommand<T> other = (MergeableValueCommand<T>)next;
        return new MergeableValueCommand<T>(
            other.Description,
            MergeKey,
            Invalidation | other.Invalidation,
            AffectedFiles[0],
            OldValue,
            other.NewValue,
            Setter);
    }
}
