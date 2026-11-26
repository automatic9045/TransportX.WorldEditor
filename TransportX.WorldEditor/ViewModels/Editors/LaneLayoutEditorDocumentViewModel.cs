using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// Display row representing a single <see cref="LaneDefinition"/> inside a <c>LaneLayout</c>.
/// </summary>
public sealed class LaneRowViewModel : INotifyPropertyChanged
{
    private int IndexValue;
    private LaneDefinition DefinitionValue;
    private string ColorHexValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public LaneDefinition Definition => DefinitionValue;
    public string ColorHex => ColorHexValue;
    public string AllowedTrafficKey => DefinitionValue.AllowedTrafficKey;
    public LaneFlowDirections Directions => DefinitionValue.Directions;
    public float X => DefinitionValue.X;
    public float Y => DefinitionValue.Y;
    public float LeftWidth => DefinitionValue.LeftWidth;
    public float RightWidth => DefinitionValue.RightWidth;
    public string Summary => $"#{IndexValue} {AllowedTrafficKey} ({Directions}) X={X:0.##} Y={Y:0.##}";

    public LaneRowViewModel(int index, LaneDefinition definition, string colorHex)
    {
        IndexValue = index;
        DefinitionValue = definition;
        ColorHexValue = colorHex;
    }

    public void Update(int index, LaneDefinition definition, string colorHex)
    {
        bool changed = IndexValue != index
            || DefinitionValue != definition
            || !string.Equals(ColorHexValue, colorHex, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        IndexValue = index;
        DefinitionValue = definition;
        ColorHexValue = colorHex;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// Document ViewModel for the interactive 2D <c>LaneLayout</c> editor with real-time mirror preview.
/// </summary>
public sealed class LaneLayoutEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private bool IsSyncing;
    private bool IsUpdatingList;
    private bool IsCommitting;

    public string LayoutKey { get; }
    public IReadOnlyList<LaneFlowDirections> FlowDirectionsValues { get; } = Enum.GetValues<LaneFlowDirections>();

    public ObservableCollection<LaneRowViewModel> Lanes { get; } = new ObservableCollection<LaneRowViewModel>();
    public ObservableCollection<LaneRowViewModel> MirrorLanes { get; } = new ObservableCollection<LaneRowViewModel>();
    public ObservableCollection<string> AvailableTrafficKeys { get; } = new ObservableCollection<string>();

    public BindableReactiveProperty<LaneRowViewModel?> SelectedLane { get; }
    public BindableReactiveProperty<bool> HasSelection { get; }
    public BindableReactiveProperty<bool> ShowOppositionMirror { get; }
    public BindableReactiveProperty<bool> IsSelfConnectable { get; }

    public BindableReactiveProperty<string> AllowedTrafficKey { get; }
    public BindableReactiveProperty<LaneFlowDirections> Directions { get; }
    public BindableReactiveProperty<decimal?> LaneX { get; }
    public BindableReactiveProperty<decimal?> LaneY { get; }
    public BindableReactiveProperty<decimal?> LeftWidth { get; }
    public BindableReactiveProperty<decimal?> RightWidth { get; }

    public ReactiveCommand<Unit> AddLaneCommand { get; }
    public ReactiveCommand<Unit> RemoveLaneCommand { get; }
    public ReactiveCommand<Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit> MoveDownCommand { get; }

    public LaneLayoutEditorDocumentViewModel(WorldWorkspaceService workspace, string layoutKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layoutKey);

        Workspace = workspace;
        LayoutKey = layoutKey;
        Id = $"Doc_LaneLayout_{layoutKey}";
        Title = $"Layout: {layoutKey}";

        SelectedLane = new BindableReactiveProperty<LaneRowViewModel?>(null).AddTo(Disposables);
        HasSelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ShowOppositionMirror = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsSelfConnectable = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        AllowedTrafficKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        Directions = new BindableReactiveProperty<LaneFlowDirections>(LaneFlowDirections.InOut).AddTo(Disposables);
        LaneX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        LaneY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        LeftWidth = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        RightWidth = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);

        SelectedLane.Subscribe(_ =>
        {
            if (!IsUpdatingList)
            {
                SyncInspectorFromSelection();
            }
        }).AddTo(Disposables);
        AllowedTrafficKey.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);
        Directions.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);
        LaneX.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);
        LaneY.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);
        LeftWidth.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);
        RightWidth.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitInspectorToModel(); }).AddTo(Disposables);

        AddLaneCommand = new ReactiveCommand<Unit>(_ => AddLane()).AddTo(Disposables);
        RemoveLaneCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedLane()).AddTo(Disposables);
        MoveUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedLane(-1)).AddTo(Disposables);
        MoveDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedLane(1)).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            $"Layout: {LayoutKey}",
            (_, target) =>
            {
                if (target.Kind != WorldFileKind.LaneLayoutXml)
                {
                    return false;
                }

                LaneLayoutDocument? doc = GetDocument();
                return doc is not null && string.Equals(
                    WorldProject.NormalizeRelativePath(target.RelativePath),
                    WorldProject.NormalizeRelativePath(doc.RelativePath),
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    public LaneLayoutDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.LaneLayouts.TryGetValue(LayoutKey, out LaneLayoutDocument? doc) ? doc : null;
    }

    public void SelectLaneIndex(int index)
    {
        if (0 <= index && index < Lanes.Count)
        {
            SelectedLane.Value = Lanes[index];
        }
        else
        {
            SelectedLane.Value = null;
        }
    }

    public void MoveLaneFromCanvas(int index, float newX, float newY)
    {
        LaneLayoutDocument? doc = GetDocument();
        if (doc is null || index < 0 || doc.Lanes.Count <= index)
        {
            return;
        }

        LaneDefinition oldLane = doc.Lanes[index];
        float roundedX = MathF.Round(newX, 2);
        float roundedY = MathF.Round(newY, 2);
        if (MathF.Abs(oldLane.X - roundedX) < 1e-4f && MathF.Abs(oldLane.Y - roundedY) < 1e-4f)
        {
            return;
        }

        LaneDefinition updated = oldLane with { X = roundedX, Y = roundedY };
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(doc.RelativePath));

        Workspace.Execute(new MergeableValueCommand<LaneDefinition>(
            $"レーン #{index} を移動",
            $"LaneLayout.{LayoutKey}.Drag.{index}",
            InvalidationScope.FullRebuild,
            target,
            oldLane,
            updated,
            val =>
            {
                if (index < doc.Lanes.Count)
                {
                    doc.Lanes[index] = val;
                }
            }));
    }

    private void AddLane()
    {
        LaneLayoutDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        string defaultTraffic = AvailableTrafficKeys.FirstOrDefault() ?? "Cars";
        float nextX = doc.Lanes.Count == 0 ? 0f : doc.Lanes[^1].X + 4.0f;
        LaneDefinition added = new LaneDefinition(defaultTraffic, LaneFlowDirections.InOut, nextX, 0f, 1.5f, 1.5f);
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(doc.RelativePath));

        Workspace.Execute(new DelegateEditorCommand(
            $"Layout '{LayoutKey}' にレーンを追加",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Lanes.Add(added),
            () => doc.Lanes.Remove(added)));

        SelectedLane.Value = Lanes.LastOrDefault();
    }

    private void RemoveSelectedLane()
    {
        LaneLayoutDocument? doc = GetDocument();
        LaneRowViewModel? selected = SelectedLane.Value;
        if (doc is null || selected is null || selected.Index < 0 || doc.Lanes.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        LaneDefinition removed = doc.Lanes[idx];
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(doc.RelativePath));

        Workspace.Execute(new DelegateEditorCommand(
            $"レーン #{idx} を削除",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Lanes.RemoveAt(idx),
            () => doc.Lanes.Insert(idx, removed)));
    }

    private void MoveSelectedLane(int delta)
    {
        LaneLayoutDocument? doc = GetDocument();
        LaneRowViewModel? selected = SelectedLane.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        int oldIndex = selected.Index;
        int newIndex = oldIndex + delta;
        if (newIndex < 0 || doc.Lanes.Count <= newIndex)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"レーン #{oldIndex} の順序を変更",
            InvalidationScope.FullRebuild,
            target,
            () =>
            {
                LaneDefinition item = doc.Lanes[oldIndex];
                doc.Lanes.RemoveAt(oldIndex);
                doc.Lanes.Insert(newIndex, item);
            },
            () =>
            {
                LaneDefinition item = doc.Lanes[newIndex];
                doc.Lanes.RemoveAt(newIndex);
                doc.Lanes.Insert(oldIndex, item);
            }));

        SelectLaneIndex(newIndex);
    }

    private void CommitInspectorToModel()
    {
        LaneLayoutDocument? doc = GetDocument();
        LaneRowViewModel? selected = SelectedLane.Value;
        if (doc is null || selected is null || selected.Index < 0 || doc.Lanes.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        LaneDefinition oldDef = doc.Lanes[idx];
        LaneDefinition newDef = new LaneDefinition(
            AllowedTrafficKey.Value,
            Directions.Value,
            (float)(LaneX.Value ?? 0m),
            (float)(LaneY.Value ?? 0m),
            float.Max(0.05f, (float)(LeftWidth.Value ?? 1m)),
            float.Max(0.05f, (float)(RightWidth.Value ?? 1m)));

        if (oldDef == newDef)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.LaneLayoutXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        IsCommitting = true;
        try
        {
            Workspace.Execute(new MergeableValueCommand<LaneDefinition>(
                $"レーン #{idx} を更新",
                $"LaneLayout.{LayoutKey}.Edit.{idx}",
                InvalidationScope.FullRebuild,
                target,
                oldDef,
                newDef,
                val =>
                {
                    if (idx < doc.Lanes.Count)
                    {
                        doc.Lanes[idx] = val;
                    }
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void SyncFromModel()
    {
        int prevIndex = SelectedLane.Value?.Index ?? -1;
        WorldProject? project = Workspace.CurrentProject.Value;
        LaneLayoutDocument? doc = GetDocument();

        List<string> newTrafficKeys = new();
        Dictionary<string, string> colorMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (project is not null)
        {
            foreach (LaneTrafficTypeDefinition t in project.Init.LaneTraffic.Types)
            {
                newTrafficKeys.Add(t.Key);
                colorMap[t.Key] = t.ColorHex;
            }

            foreach (LaneTrafficGroupDefinition g in project.Init.LaneTraffic.Groups)
            {
                if (!newTrafficKeys.Contains(g.Key))
                {
                    newTrafficKeys.Add(g.Key);
                }

                colorMap[g.Key] = g.ColorHex;
            }
        }

        if (doc is not null)
        {
            for (int i = 0; i < doc.Lanes.Count; i++)
            {
                LaneDefinition lane = doc.Lanes[i];
                if (!string.IsNullOrEmpty(lane.AllowedTrafficKey) && !newTrafficKeys.Contains(lane.AllowedTrafficKey))
                {
                    newTrafficKeys.Add(lane.AllowedTrafficKey);
                }
            }
        }

        IsUpdatingList = true;
        IsSyncing = true;
        try
        {
            if (!AvailableTrafficKeys.SequenceEqual(newTrafficKeys, StringComparer.Ordinal))
            {
                AvailableTrafficKeys.Clear();
                foreach (string key in newTrafficKeys)
                {
                    AvailableTrafficKeys.Add(key);
                }
            }

            if (doc is not null)
            {
                if (Lanes.Count == doc.Lanes.Count)
                {
                    for (int i = 0; i < doc.Lanes.Count; i++)
                    {
                        LaneDefinition lane = doc.Lanes[i];
                        string color = colorMap.TryGetValue(lane.AllowedTrafficKey, out string? hex) ? hex : "#4DA6FF";
                        Lanes[i].Update(i, lane, color);
                    }
                }
                else
                {
                    Lanes.Clear();
                    for (int i = 0; i < doc.Lanes.Count; i++)
                    {
                        LaneDefinition lane = doc.Lanes[i];
                        string color = colorMap.TryGetValue(lane.AllowedTrafficKey, out string? hex) ? hex : "#4DA6FF";
                        Lanes.Add(new LaneRowViewModel(i, lane, color));
                    }
                }

                LaneLayoutDocument mirror = doc.CreateOpposition();
                if (MirrorLanes.Count == mirror.Lanes.Count)
                {
                    for (int i = 0; i < mirror.Lanes.Count; i++)
                    {
                        LaneDefinition ml = mirror.Lanes[i];
                        string color = colorMap.TryGetValue(ml.AllowedTrafficKey, out string? hex) ? hex : "#4DA6FF";
                        MirrorLanes[i].Update(i, ml, color);
                    }
                }
                else
                {
                    MirrorLanes.Clear();
                    for (int i = 0; i < mirror.Lanes.Count; i++)
                    {
                        LaneDefinition ml = mirror.Lanes[i];
                        string color = colorMap.TryGetValue(ml.AllowedTrafficKey, out string? hex) ? hex : "#4DA6FF";
                        MirrorLanes.Add(new LaneRowViewModel(i, ml, color));
                    }
                }

                IsSelfConnectable.Value = doc.CanConnectTo(doc);
            }
            else
            {
                Lanes.Clear();
                MirrorLanes.Clear();
                IsSelfConnectable.Value = false;
            }

            SelectedLane.Value = 0 <= prevIndex && prevIndex < Lanes.Count
                ? Lanes[prevIndex]
                : Lanes.FirstOrDefault();
        }
        finally
        {
            IsSyncing = false;
            IsUpdatingList = false;
        }

        if (!IsCommitting)
        {
            SyncInspectorFromSelection();
        }
    }

    private void SyncInspectorFromSelection()
    {
        IsSyncing = true;
        try
        {
            LaneRowViewModel? row = SelectedLane.Value;
            if (row is null)
            {
                HasSelection.Value = false;
                return;
            }

            HasSelection.Value = true;
            AllowedTrafficKey.Value = row.AllowedTrafficKey;
            Directions.Value = row.Directions;
            LaneX.Value = (decimal)row.X;
            LaneY.Value = (decimal)row.Y;
            LeftWidth.Value = (decimal)row.LeftWidth;
            RightWidth.Value = (decimal)row.RightWidth;
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
