using System;
using System.Collections.Generic;
using TransportX.WorldEditor.Core.Commands;
using Xunit;

namespace TransportX.WorldEditor.Tests.Commands;

public sealed class CommandHistoryTests
{
    private sealed class CounterBox
    {
        public int Value { get; set; }
    }

    private sealed class AddValueCommand : IEditorCommand
    {
        private readonly CounterBox Target;
        private readonly int Delta;

        public string Description { get; }
        public InvalidationScope Invalidation => InvalidationScope.SingleElement;
        public IReadOnlyList<WorldFileTarget> AffectedFiles { get; } = Array.Empty<WorldFileTarget>();

        public AddValueCommand(CounterBox target, int delta, string description)
        {
            Target = target;
            Delta = delta;
            Description = description;
        }

        public void Execute()
        {
            Target.Value += Delta;
        }

        public void Undo()
        {
            Target.Value -= Delta;
        }
    }

    private sealed class SetValueMergeableCommand : IMergeableEditorCommand
    {
        private readonly CounterBox Target;
        private readonly string MergeKey;
        private readonly int OldValue;
        private readonly int NewValue;

        public string Description => "値変更";
        public InvalidationScope Invalidation => InvalidationScope.SingleElement;
        public IReadOnlyList<WorldFileTarget> AffectedFiles { get; } = Array.Empty<WorldFileTarget>();

        public SetValueMergeableCommand(CounterBox target, string mergeKey, int oldValue, int newValue)
        {
            Target = target;
            MergeKey = mergeKey;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public void Execute()
        {
            Target.Value = NewValue;
        }

        public void Undo()
        {
            Target.Value = OldValue;
        }

        public bool CanMergeWith(IEditorCommand next)
        {
            return next is SetValueMergeableCommand other && other.MergeKey == MergeKey;
        }

        public IEditorCommand MergeWith(IEditorCommand next)
        {
            SetValueMergeableCommand other = (SetValueMergeableCommand)next;
            return new SetValueMergeableCommand(Target, MergeKey, OldValue, other.NewValue);
        }
    }

    [Fact]
    public void ExecuteUndoRedo_MaintainsStacksAndClearsRedoOnNewExecute()
    {
        CounterBox box = new CounterBox();
        CommandHistory history = new CommandHistory();

        history.Execute(new AddValueCommand(box, 10, "Add 10"));
        history.Execute(new AddValueCommand(box, 5, "Add 5"));

        Assert.Equal(15, box.Value);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        Assert.True(history.Undo());
        Assert.Equal(10, box.Value);
        Assert.True(history.CanRedo);

        history.Execute(new AddValueCommand(box, 3, "Add 3"));
        Assert.Equal(13, box.Value);
        Assert.False(history.CanRedo);

        Assert.True(history.Undo());
        Assert.Equal(10, box.Value);
        Assert.True(history.Redo());
        Assert.Equal(13, box.Value);
    }

    [Fact]
    public void Execute_CoalescesMergeableCommands()
    {
        CounterBox box = new CounterBox { Value = 1 };
        CommandHistory history = new CommandHistory();

        history.Execute(new SetValueMergeableCommand(box, "drag-x", 1, 2));
        history.Execute(new SetValueMergeableCommand(box, "drag-x", 2, 7));

        Assert.Equal(7, box.Value);
        Assert.Equal(1, history.UndoCount);

        Assert.True(history.Undo());
        Assert.Equal(1, box.Value);
        Assert.False(history.CanUndo);
    }
}
