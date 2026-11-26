using System;
using System.Collections.Generic;

namespace TransportX.WorldEditor.Core.Commands;

/// <summary>
/// Provides metadata about a command execution, undo, or redo transition.
/// </summary>
public sealed class CommandExecutedEventArgs : EventArgs
{
    public IEditorCommand? Command { get; }
    public InvalidationScope Invalidation { get; }

    public CommandExecutedEventArgs(IEditorCommand? command, InvalidationScope invalidation)
    {
        Command = command;
        Invalidation = invalidation;
    }
}

/// <summary>
/// Manages undo/redo command stacks with optional command coalescing.
/// </summary>
public sealed class CommandHistory
{
    private readonly Stack<IEditorCommand> UndoStack = new Stack<IEditorCommand>();
    private readonly Stack<IEditorCommand> RedoStack = new Stack<IEditorCommand>();

    public bool CanUndo => 0 < UndoStack.Count;
    public bool CanRedo => 0 < RedoStack.Count;
    public int UndoCount => UndoStack.Count;
    public int RedoCount => RedoStack.Count;
    public string? UndoDescription => 0 < UndoStack.Count ? UndoStack.Peek().Description : null;
    public string? RedoDescription => 0 < RedoStack.Count ? RedoStack.Peek().Description : null;

    public event EventHandler<CommandExecutedEventArgs>? HistoryChanged;

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Execute();
        RedoStack.Clear();

        if (0 < UndoStack.Count)
        {
            IEditorCommand previous = UndoStack.Peek();
            if (previous is IMergeableEditorCommand mergeable && mergeable.CanMergeWith(command))
            {
                UndoStack.Pop();
                IEditorCommand merged = mergeable.MergeWith(command);
                UndoStack.Push(merged);
                HistoryChanged?.Invoke(this, new CommandExecutedEventArgs(merged, merged.Invalidation));
                return;
            }
        }

        UndoStack.Push(command);
        HistoryChanged?.Invoke(this, new CommandExecutedEventArgs(command, command.Invalidation));
    }

    public bool Undo()
    {
        if (UndoStack.Count == 0)
        {
            return false;
        }

        IEditorCommand command = UndoStack.Pop();
        command.Undo();
        RedoStack.Push(command);
        HistoryChanged?.Invoke(this, new CommandExecutedEventArgs(command, command.Invalidation));
        return true;
    }

    public bool Redo()
    {
        if (RedoStack.Count == 0)
        {
            return false;
        }

        IEditorCommand command = RedoStack.Pop();
        command.Execute();
        UndoStack.Push(command);
        HistoryChanged?.Invoke(this, new CommandExecutedEventArgs(command, command.Invalidation));
        return true;
    }

    public void Clear()
    {
        UndoStack.Clear();
        RedoStack.Clear();
        HistoryChanged?.Invoke(this, new CommandExecutedEventArgs(null, InvalidationScope.None));
    }
}
