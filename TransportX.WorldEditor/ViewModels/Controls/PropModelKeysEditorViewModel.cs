using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using R3;
using TransportX.WorldEditor.Core.Validation;

namespace TransportX.WorldEditor.ViewModels.Controls;

/// <summary>
/// Option item in the per-slot model dropdown (<c>""</c> represents an empty prop slot).
/// </summary>
public sealed record PropModelKeyChoice(string ModelKey, string DisplayName);

/// <summary>
/// Single slot row inside <see cref="PropModelKeysEditorViewModel"/>.
/// </summary>
public sealed class PropModelSlotRow : IDisposable
{
    private readonly CompositeDisposable Disposables = new();

    public int Index { get; }
    public string ModelKey { get; }
    public string IndexText => $"#{Index + 1}";
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }

    public ObservableCollection<PropModelKeyChoice> Options { get; }
    public BindableReactiveProperty<PropModelKeyChoice?> SelectedChoice { get; }
    public ReactiveCommand<Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit> RemoveCommand { get; }

    public PropModelSlotRow(
        int index,
        int totalCount,
        string modelKey,
        IReadOnlyList<PropModelKeyChoice> options,
        Action<int, string> onSelectKey,
        Action<int, int> onMove,
        Action<int> onRemove)
    {
        Index = index;
        ModelKey = modelKey;
        CanMoveUp = 0 < index;
        CanMoveDown = index + 1 < totalCount;

        Options = new ObservableCollection<PropModelKeyChoice>(options);
        PropModelKeyChoice? initial = options.FirstOrDefault(o => string.Equals(o.ModelKey, modelKey, StringComparison.Ordinal));
        SelectedChoice = new BindableReactiveProperty<PropModelKeyChoice?>(initial).AddTo(Disposables);

        bool initialized = false;
        SelectedChoice.Skip(1).Subscribe(choice =>
        {
            if (initialized && choice is not null)
            {
                onSelectKey(Index, choice.ModelKey);
            }
        }).AddTo(Disposables);
        initialized = true;

        MoveUpCommand = new ReactiveCommand<Unit>(_ => onMove(Index, -1)).AddTo(Disposables);
        MoveDownCommand = new ReactiveCommand<Unit>(_ => onMove(Index, 1)).AddTo(Disposables);
        RemoveCommand = new ReactiveCommand<Unit>(_ => onRemove(Index)).AddTo(Disposables);
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}

/// <summary>
/// Reusable ViewModel backing the structured slot list editor for <c>PutProp</c> model sequences.
/// Synchronizes two-way with a backing comma-separated <see cref="ModelKeysText"/> property.
/// </summary>
public sealed class PropModelKeysEditorViewModel : IDisposable
{
    public static readonly PropModelKeyChoice EmptyChoice = new(string.Empty, "(空)");

    private readonly CompositeDisposable Disposables = new();
    private readonly List<string> CurrentKeys = new();
    private readonly INotifyCollectionChanged? ObservableAvailableModels;
    private bool IsUpdatingText;
    private bool IsDisposed;

    public ObservableCollection<string> AvailableModelKeys { get; }
    public BindableReactiveProperty<string> ModelKeysText { get; }
    public BindableReactiveProperty<string> ModelKeyToAdd { get; }
    public ObservableCollection<PropModelSlotRow> Slots { get; } = new();
    public BindableReactiveProperty<bool> HasSlots { get; }

    public ReactiveCommand<Unit> AppendSelectedModelCommand { get; }
    public ReactiveCommand<Unit> AppendEmptyModelCommand { get; }

    public PropModelKeysEditorViewModel(
        ObservableCollection<string> availableModelKeys,
        BindableReactiveProperty<string> modelKeysText,
        BindableReactiveProperty<string> modelKeyToAdd)
    {
        ArgumentNullException.ThrowIfNull(availableModelKeys);
        ArgumentNullException.ThrowIfNull(modelKeysText);
        ArgumentNullException.ThrowIfNull(modelKeyToAdd);

        AvailableModelKeys = availableModelKeys;
        ModelKeysText = modelKeysText;
        ModelKeyToAdd = modelKeyToAdd;
        HasSlots = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        AppendSelectedModelCommand = new ReactiveCommand<Unit>(_ => AppendSelectedModel()).AddTo(Disposables);
        AppendEmptyModelCommand = new ReactiveCommand<Unit>(_ => AppendEmptyModel()).AddTo(Disposables);

        ObservableAvailableModels = availableModelKeys;
        ObservableAvailableModels.CollectionChanged += OnAvailableModelsCollectionChanged;

        ModelKeysText.Subscribe(_ =>
        {
            if (!IsUpdatingText)
            {
                SyncFromText();
            }
        }).AddTo(Disposables);

        SyncFromText();
    }

    private void OnAvailableModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsDisposed)
        {
            RebuildSlotRows();
        }
    }

    private void SyncFromText()
    {
        List<string> parsed = PropModelKeysTextHelper.Parse(ModelKeysText.Value);
        if (parsed.SequenceEqual(CurrentKeys, StringComparer.Ordinal))
        {
            return;
        }

        CurrentKeys.Clear();
        CurrentKeys.AddRange(parsed);
        RebuildSlotRows();
    }

    private void AppendSelectedModel()
    {
        string key = (ModelKeyToAdd.Value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (0 < AvailableModelKeys.Count)
            {
                key = AvailableModelKeys[0];
            }
            else
            {
                return;
            }
        }

        List<string> next = [.. CurrentKeys, key];
        ApplyNextKeys(next);
    }

    private void AppendEmptyModel()
    {
        List<string> next = [.. CurrentKeys, string.Empty];
        ApplyNextKeys(next);
    }

    private void ReplaceSlot(int index, string newModelKey)
    {
        if (index < 0 || CurrentKeys.Count <= index || string.Equals(CurrentKeys[index], newModelKey, StringComparison.Ordinal))
        {
            return;
        }

        List<string> next = [.. CurrentKeys];
        next[index] = newModelKey;
        ApplyNextKeys(next);
    }

    private void MoveSlot(int index, int delta)
    {
        int target = index + delta;
        if (index < 0 || CurrentKeys.Count <= index || target < 0 || CurrentKeys.Count <= target)
        {
            return;
        }

        List<string> next = [.. CurrentKeys];
        (next[index], next[target]) = (next[target], next[index]);
        ApplyNextKeys(next);
    }

    private void RemoveSlot(int index)
    {
        if (index < 0 || CurrentKeys.Count <= index)
        {
            return;
        }

        List<string> next = [.. CurrentKeys];
        next.RemoveAt(index);
        ApplyNextKeys(next);
    }

    private void ApplyNextKeys(List<string> next)
    {
        CurrentKeys.Clear();
        CurrentKeys.AddRange(next);
        RebuildSlotRows();

        IsUpdatingText = true;
        try
        {
            ModelKeysText.Value = PropModelKeysTextHelper.Format(CurrentKeys);
        }
        finally
        {
            IsUpdatingText = false;
        }
    }

    private void RebuildSlotRows()
    {
        for (int i = 0; i < Slots.Count; i++)
        {
            Slots[i].Dispose();
        }

        Slots.Clear();

        for (int i = 0; i < CurrentKeys.Count; i++)
        {
            string currentKey = CurrentKeys[i];
            List<PropModelKeyChoice> options = BuildChoicesForSlot(currentKey);
            Slots.Add(new PropModelSlotRow(
                i,
                CurrentKeys.Count,
                currentKey,
                options,
                ReplaceSlot,
                MoveSlot,
                RemoveSlot));
        }

        HasSlots.Value = 0 < CurrentKeys.Count;
    }

    private List<PropModelKeyChoice> BuildChoicesForSlot(string currentKey)
    {
        List<PropModelKeyChoice> choices = [EmptyChoice];
        foreach (string mk in AvailableModelKeys)
        {
            if (!string.IsNullOrEmpty(mk))
            {
                choices.Add(new PropModelKeyChoice(mk, mk));
            }
        }

        if (!string.IsNullOrEmpty(currentKey)
            && !choices.Any(c => string.Equals(c.ModelKey, currentKey, StringComparison.Ordinal)))
        {
            choices.Add(new PropModelKeyChoice(currentKey, currentKey));
        }

        return choices;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (ObservableAvailableModels is not null)
        {
            ObservableAvailableModels.CollectionChanged -= OnAvailableModelsCollectionChanged;
        }

        for (int i = 0; i < Slots.Count; i++)
        {
            Slots[i].Dispose();
        }

        Slots.Clear();
        Disposables.Dispose();
    }
}
