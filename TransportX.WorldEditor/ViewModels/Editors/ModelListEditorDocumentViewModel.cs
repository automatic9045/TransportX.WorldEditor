using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// List row display representation of a <see cref="ModelListEntry"/>.
/// </summary>
public sealed class ModelListRowViewModel : INotifyPropertyChanged
{
    private string KeyValue;
    private string ModelPathDisplayValue;
    private ModelColliderKind ColliderKindValue;
    private string CommandSummaryValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelListEntry Entry { get; }

    public string Key => KeyValue;
    public string ModelPathDisplay => ModelPathDisplayValue;
    public ModelColliderKind ColliderKind => ColliderKindValue;
    public string CommandSummary => CommandSummaryValue;

    public ModelListRowViewModel(
        ModelListEntry entry,
        string key,
        string modelPathDisplay,
        ModelColliderKind colliderKind,
        string commandSummary)
    {
        Entry = entry;
        KeyValue = key;
        ModelPathDisplayValue = modelPathDisplay;
        ColliderKindValue = colliderKind;
        CommandSummaryValue = commandSummary;
    }

    public void Update(string key, string modelPathDisplay, ModelColliderKind colliderKind, string commandSummary)
    {
        if (!string.Equals(KeyValue, key, StringComparison.Ordinal))
        {
            KeyValue = key;
            OnPropertyChanged(nameof(Key));
        }

        if (!string.Equals(ModelPathDisplayValue, modelPathDisplay, StringComparison.Ordinal))
        {
            ModelPathDisplayValue = modelPathDisplay;
            OnPropertyChanged(nameof(ModelPathDisplay));
        }

        if (ColliderKindValue != colliderKind)
        {
            ColliderKindValue = colliderKind;
            OnPropertyChanged(nameof(ColliderKind));
        }

        if (!string.Equals(CommandSummaryValue, commandSummary, StringComparison.Ordinal))
        {
            CommandSummaryValue = commandSummary;
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Document ViewModel for editing <c>Models.txt</c> model/collider records with Undo/Redo
/// and TransportX specification validation (<c>$ConvexHull</c> no-parens, <c>NONE</c> guards).
/// </summary>
public sealed class ModelListEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly IFileDialogService DialogService;
    private readonly string DocumentKey;
    private bool IsSyncing;
    private bool IsUpdatingList;
    private bool IsCommitting;

    public IReadOnlyList<ModelColliderKind> ColliderKinds { get; } = Enum.GetValues<ModelColliderKind>();

    public ObservableCollection<ModelListRowViewModel> Entries { get; } = new ObservableCollection<ModelListRowViewModel>();
    public BindableReactiveProperty<ModelListRowViewModel?> SelectedRow { get; }

    public BindableReactiveProperty<bool> HasSelection { get; }
    public BindableReactiveProperty<bool> HasNoEntries { get; }
    public BindableReactiveProperty<string> EntryKey { get; }
    public BindableReactiveProperty<bool> IsNoneModel { get; }
    public BindableReactiveProperty<string> ModelPath { get; }
    public BindableReactiveProperty<bool> DisableLeftHanded { get; }
    public BindableReactiveProperty<ModelColliderKind> SelectedColliderKind { get; }

    public BindableReactiveProperty<bool> ShowCollisionModelPath { get; }
    public BindableReactiveProperty<string> CollisionModelPath { get; }
    public BindableReactiveProperty<bool> HasUppercaseCollisionPathWarning { get; }
    public BindableReactiveProperty<bool> HasNoneModelError { get; }

    public BindableReactiveProperty<bool> ShowPrimitiveDimensions { get; }
    public BindableReactiveProperty<bool> ShowSizeY { get; }
    public BindableReactiveProperty<bool> ShowSizeZ { get; }
    public BindableReactiveProperty<decimal?> SizeX { get; }
    public BindableReactiveProperty<decimal?> SizeY { get; }
    public BindableReactiveProperty<decimal?> SizeZ { get; }

    public BindableReactiveProperty<bool> HasTranslationOffset { get; }
    public BindableReactiveProperty<bool> HasRotationOffset { get; }
    public BindableReactiveProperty<decimal?> OffsetX { get; }
    public BindableReactiveProperty<decimal?> OffsetY { get; }
    public BindableReactiveProperty<decimal?> OffsetZ { get; }
    public BindableReactiveProperty<decimal?> RotX { get; }
    public BindableReactiveProperty<decimal?> RotY { get; }
    public BindableReactiveProperty<decimal?> RotZ { get; }

    public BindableReactiveProperty<bool> CanHaveMaterial { get; }
    public BindableReactiveProperty<bool> HasCustomMaterial { get; }
    public BindableReactiveProperty<decimal?> FrictionCoefficient { get; }
    public BindableReactiveProperty<decimal?> MaxRecoveryVelocity { get; }
    public BindableReactiveProperty<decimal?> SpringFrequency { get; }
    public BindableReactiveProperty<decimal?> SpringDampingRatio { get; }

    public BindableReactiveProperty<string> LiveTsvPreview { get; }

    public ReactiveCommand<Unit> AddEntryCommand { get; }
    public ReactiveCommand<Unit> DuplicateEntryCommand { get; }
    public ReactiveCommand<Unit> RemoveEntryCommand { get; }
    public ReactiveCommand<Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit> BrowseModelPathCommand { get; }
    public ReactiveCommand<Unit> BrowseCollisionPathCommand { get; }

    public ModelListEditorDocumentViewModel(
        WorldWorkspaceService workspace,
        IFileDialogService dialogService,
        string documentKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(documentKey);

        Workspace = workspace;
        DialogService = dialogService;
        DocumentKey = WorldProject.NormalizeRelativePath(documentKey);
        Id = $"Doc_Models_{DocumentKey}";
        Title = Path.GetFileName(DocumentKey);

        SelectedRow = new BindableReactiveProperty<ModelListRowViewModel?>(null).AddTo(Disposables);
        HasSelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasNoEntries = new BindableReactiveProperty<bool>(true).AddTo(Disposables);

        EntryKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsNoneModel = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ModelPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        DisableLeftHanded = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedColliderKind = new BindableReactiveProperty<ModelColliderKind>(ModelColliderKind.NonCollision).AddTo(Disposables);

        ShowCollisionModelPath = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CollisionModelPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        HasUppercaseCollisionPathWarning = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasNoneModelError = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        ShowPrimitiveDimensions = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ShowSizeY = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ShowSizeZ = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SizeX = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        SizeY = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        SizeZ = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);

        HasTranslationOffset = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasRotationOffset = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        OffsetX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        OffsetY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        OffsetZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        CanHaveMaterial = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasCustomMaterial = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        FrictionCoefficient = new BindableReactiveProperty<decimal?>(1.0m).AddTo(Disposables);
        MaxRecoveryVelocity = new BindableReactiveProperty<decimal?>(10.0m).AddTo(Disposables);
        SpringFrequency = new BindableReactiveProperty<decimal?>(30.0m).AddTo(Disposables);
        SpringDampingRatio = new BindableReactiveProperty<decimal?>(1.0m).AddTo(Disposables);

        LiveTsvPreview = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedRow.Subscribe(_ =>
        {
            if (!IsUpdatingList)
            {
                SyncInspectorFromSelection();
            }
        }).AddTo(Disposables);

        HookFieldCommit(EntryKey);
        HookFieldCommit(IsNoneModel);
        HookFieldCommit(ModelPath);
        HookFieldCommit(DisableLeftHanded);
        HookFieldCommit(SelectedColliderKind);
        HookFieldCommit(CollisionModelPath);
        HookFieldCommit(SizeX);
        HookFieldCommit(SizeY);
        HookFieldCommit(SizeZ);
        HookFieldCommit(HasTranslationOffset);
        HookFieldCommit(HasRotationOffset);
        HookFieldCommit(OffsetX);
        HookFieldCommit(OffsetY);
        HookFieldCommit(OffsetZ);
        HookFieldCommit(RotX);
        HookFieldCommit(RotY);
        HookFieldCommit(RotZ);
        HookFieldCommit(HasCustomMaterial);
        HookFieldCommit(FrictionCoefficient);
        HookFieldCommit(MaxRecoveryVelocity);
        HookFieldCommit(SpringFrequency);
        HookFieldCommit(SpringDampingRatio);

        AddEntryCommand = new ReactiveCommand<Unit>(_ => AddNewEntry()).AddTo(Disposables);
        DuplicateEntryCommand = new ReactiveCommand<Unit>(_ => DuplicateSelectedEntry()).AddTo(Disposables);
        RemoveEntryCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedEntry()).AddTo(Disposables);
        MoveUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(-1)).AddTo(Disposables);
        MoveDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(1)).AddTo(Disposables);
        BrowseModelPathCommand = new ReactiveCommand<Unit>(u => { _ = BrowseModelFileAsync(isCollision: false); }).AddTo(Disposables);
        BrowseCollisionPathCommand = new ReactiveCommand<Unit>(u => { _ = BrowseModelFileAsync(isCollision: true); }).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncListFromModel()).AddTo(Disposables);
        SyncListFromModel();
        BindDirtyTitle(
            Workspace,
            Path.GetFileName(DocumentKey),
            (_, target) => target.Kind == WorldFileKind.ModelsTxt
                && string.Equals(WorldProject.NormalizeRelativePath(target.RelativePath), DocumentKey, StringComparison.OrdinalIgnoreCase));
    }

    private void HookFieldCommit<T>(BindableReactiveProperty<T> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitInspectorToModel();
            }
        }).AddTo(Disposables);
    }

    private ModelListDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        return project.ModelLists.TryGetValue(DocumentKey, out ModelListDocument? doc) ? doc : null;
    }

    private void AddNewEntry()
    {
        ModelListDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        int n = doc.Entries.Count + 1;
        string key = $"Model_{n:D3}";
        while (doc.Entries.Any(e => string.Equals(e.Key, key, StringComparison.Ordinal)))
        {
            n++;
            key = $"Model_{n:D3}";
        }

        ModelListEntry entry = new ModelListEntry
        {
            Key = key,
            ModelPath = $"Models/{key}.glb",
            Collider = ModelColliderSpec.NonCollision(),
        };

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.ModelsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"モデル '{key}' を追加",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Entries.Add(entry),
            () => doc.Entries.Remove(entry)));

        SelectedRow.Value = Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, entry));
    }

    private void DuplicateSelectedEntry()
    {
        ModelListDocument? doc = GetDocument();
        ModelListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        ModelListEntry copy = new ModelListEntry
        {
            Key = $"{selected.Entry.Key}_Copy",
            ModelPath = selected.Entry.ModelPath,
            Collider = selected.Entry.Collider,
        };

        int insertAt = doc.Entries.IndexOf(selected.Entry) + 1;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.ModelsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"モデル '{selected.Key}' を複製",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Entries.Insert(insertAt, copy),
            () => doc.Entries.Remove(copy)));

        SelectedRow.Value = Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, copy));
    }

    private void RemoveSelectedEntry()
    {
        ModelListDocument? doc = GetDocument();
        ModelListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        int idx = doc.Entries.IndexOf(selected.Entry);
        if (idx < 0)
        {
            return;
        }

        ModelListEntry removed = selected.Entry;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.ModelsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"モデル '{removed.Key}' を削除",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Entries.RemoveAt(idx),
            () => doc.Entries.Insert(idx, removed)));
    }

    private void MoveSelectedEntry(int delta)
    {
        ModelListDocument? doc = GetDocument();
        ModelListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        int oldIndex = doc.Entries.IndexOf(selected.Entry);
        int newIndex = oldIndex + delta;
        if (oldIndex < 0 || newIndex < 0 || doc.Entries.Count <= newIndex)
        {
            return;
        }

        ModelListEntry moved = selected.Entry;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.ModelsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"モデル '{moved.Key}' の定義順を変更",
            InvalidationScope.FullRebuild,
            target,
            () =>
            {
                doc.Entries.RemoveAt(oldIndex);
                doc.Entries.Insert(newIndex, moved);
            },
            () =>
            {
                doc.Entries.RemoveAt(newIndex);
                doc.Entries.Insert(oldIndex, moved);
            }));

        SelectedRow.Value = Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, moved));
    }

    private async Task BrowseModelFileAsync(bool isCollision)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        ModelListDocument? doc = GetDocument();
        if (project is null || doc is null || SelectedRow.Value is null)
        {
            return;
        }

        string listFullDir = Path.GetDirectoryName(Path.Combine(project.WorldDirectory, doc.RelativePath)) ?? project.WorldDirectory;
        string? picked = await DialogService.PickAssetFileAsync(
            isCollision ? "衝突判定モデルファイルを選択" : "描画モデルファイルを選択",
            listFullDir,
            "3D Model",
            new string[] { "*.glb", "*.gltf", "*.obj", "*.fbx" });

        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        string relPath = Path.GetRelativePath(listFullDir, picked).Replace('\\', '/');
        if (isCollision)
        {
            CollisionModelPath.Value = relPath;
        }
        else
        {
            IsNoneModel.Value = false;
            ModelPath.Value = relPath;
        }
    }

    private void CommitInspectorToModel()
    {
        ModelListDocument? doc = GetDocument();
        ModelListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        ModelListEntry entry = selected.Entry;
        string oldKey = entry.Key;
        string? oldPath = entry.ModelPath;
        ModelColliderSpec oldCollider = entry.Collider;

        string newKey = EntryKey.Value.Trim();
        string? newPath = IsNoneModel.Value ? null : (string.IsNullOrWhiteSpace(ModelPath.Value) ? null : ModelPath.Value.Trim());
        ModelColliderSpec newCollider = BuildColliderFromInspector();

        if (string.Equals(oldKey, newKey, StringComparison.Ordinal)
            && string.Equals(oldPath, newPath, StringComparison.Ordinal)
            && oldCollider == newCollider)
        {
            UpdateDerivedFlags(newPath, newCollider);
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.ModelsTxt, DocumentKey);
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"モデル '{oldKey}' を更新",
                InvalidationScope.FullRebuild,
                target,
                () =>
                {
                    entry.Key = newKey;
                    entry.ModelPath = newPath;
                    entry.Collider = newCollider;
                },
                () =>
                {
                    entry.Key = oldKey;
                    entry.ModelPath = oldPath;
                    entry.Collider = oldCollider;
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private ModelColliderSpec BuildColliderFromInspector()
    {
        bool makeLh = !DisableLeftHanded.Value;
        ModelColliderKind kind = SelectedColliderKind.Value;
        string? colPath = string.IsNullOrWhiteSpace(CollisionModelPath.Value) ? null : CollisionModelPath.Value.Trim();

        ColliderMaterialParams? mat = null;
        if (kind != ModelColliderKind.NonCollision && HasCustomMaterial.Value)
        {
            mat = new ColliderMaterialParams(
                (float)(FrictionCoefficient.Value ?? 1.0m),
                (float)(MaxRecoveryVelocity.Value ?? 10.0m),
                (float)(SpringFrequency.Value ?? 30.0m),
                (float)(SpringDampingRatio.Value ?? 1.0m));
        }

        bool useRot = HasRotationOffset.Value;
        bool useTrans = HasTranslationOffset.Value || useRot;
        PoseDegreesModel? offset = useTrans
            ? new PoseDegreesModel(
                (double)(OffsetX.Value ?? 0m),
                (double)(OffsetY.Value ?? 0m),
                (double)(OffsetZ.Value ?? 0m),
                useRot ? (double)(RotX.Value ?? 0m) : 0.0,
                useRot ? (double)(RotY.Value ?? 0m) : 0.0,
                useRot ? (double)(RotZ.Value ?? 0m) : 0.0)
            : null;

        float sx = (float)(SizeX.Value ?? 1.0m);
        float sy = (float)(SizeY.Value ?? 1.0m);
        float sz = (float)(SizeZ.Value ?? 1.0m);

        return kind switch
        {
            ModelColliderKind.NonCollision => ModelColliderSpec.NonCollision(makeLh),
            ModelColliderKind.BoundingBox => ModelColliderSpec.BoundingBox(mat, makeLh),
            ModelColliderKind.ConvexHull => ModelColliderSpec.ConvexHull(mat, makeLh),
            ModelColliderKind.ClosedModel => ModelColliderSpec.MeshCollider(false, colPath, mat, makeLh),
            ModelColliderKind.OpenModel => ModelColliderSpec.MeshCollider(true, colPath, mat, makeLh),
            ModelColliderKind.Box => ModelColliderSpec.BoxCollider(sx, sy, sz, offset, useRot, mat, makeLh),
            ModelColliderKind.Cylinder => ModelColliderSpec.CylinderCollider(sx, sy, offset, useRot, mat, makeLh),
            ModelColliderKind.Sphere => ModelColliderSpec.SphereCollider(sx, offset, useRot, mat, makeLh),
            _ => ModelColliderSpec.NonCollision(makeLh),
        };
    }

    private void SyncListFromModel()
    {
        ModelListEntry? previouslySelectedEntry = SelectedRow.Value?.Entry;
        ModelListDocument? doc = GetDocument();

        bool canUpdateInPlace = doc is not null && Entries.Count == doc.Entries.Count;
        if (canUpdateInPlace && doc is not null)
        {
            for (int i = 0; i < doc.Entries.Count; i++)
            {
                if (!ReferenceEquals(Entries[i].Entry, doc.Entries[i]))
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        if (canUpdateInPlace && doc is not null)
        {
            for (int i = 0; i < doc.Entries.Count; i++)
            {
                ModelListEntry entry = doc.Entries[i];
                string preview = FormatEntrySummary(entry);
                Entries[i].Update(entry.Key, entry.ModelPath ?? "NONE", entry.Collider.Kind, preview);
            }
        }
        else
        {
            IsUpdatingList = true;
            try
            {
                Entries.Clear();
                if (doc is not null)
                {
                    foreach (ModelListEntry entry in doc.Entries)
                    {
                        string preview = FormatEntrySummary(entry);
                        Entries.Add(new ModelListRowViewModel(
                            entry,
                            entry.Key,
                            entry.ModelPath ?? "NONE",
                            entry.Collider.Kind,
                            preview));
                    }
                }

                HasNoEntries.Value = Entries.Count == 0;

                ModelListRowViewModel? match = previouslySelectedEntry is not null
                    ? Entries.FirstOrDefault(r => ReferenceEquals(r.Entry, previouslySelectedEntry))
                    : Entries.FirstOrDefault();
                SelectedRow.Value = match;
            }
            finally
            {
                IsUpdatingList = false;
            }
        }

        HasNoEntries.Value = Entries.Count == 0;

        if (IsCommitting && SelectedRow.Value is ModelListRowViewModel activeRow)
        {
            UpdateDerivedFlags(activeRow.Entry.ModelPath, activeRow.Entry.Collider);
            LiveTsvPreview.Value = FormatEntryLine(activeRow.Entry);
        }
        else
        {
            SyncInspectorFromSelection();
        }
    }

    private void SyncInspectorFromSelection()
    {
        IsSyncing = true;
        try
        {
            ModelListRowViewModel? row = SelectedRow.Value;
            if (row is null)
            {
                HasSelection.Value = false;
                LiveTsvPreview.Value = string.Empty;
                return;
            }

            HasSelection.Value = true;
            ModelListEntry entry = row.Entry;
            ModelColliderSpec col = entry.Collider;

            EntryKey.Value = entry.Key;
            IsNoneModel.Value = entry.ModelPath is null;
            ModelPath.Value = entry.ModelPath ?? string.Empty;
            DisableLeftHanded.Value = !col.MakeLeftHanded;
            SelectedColliderKind.Value = col.Kind;

            CollisionModelPath.Value = col.CollisionModelPath ?? string.Empty;
            SizeX.Value = (decimal)(col.SizeX == 0f ? 1f : col.SizeX);
            SizeY.Value = (decimal)(col.SizeY == 0f ? 1f : col.SizeY);
            SizeZ.Value = (decimal)(col.SizeZ == 0f ? 1f : col.SizeZ);

            HasTranslationOffset.Value = col.HasTranslationOffset;
            HasRotationOffset.Value = col.HasRotationOffset;
            OffsetX.Value = (decimal)col.OffsetPose.X;
            OffsetY.Value = (decimal)col.OffsetPose.Y;
            OffsetZ.Value = (decimal)col.OffsetPose.Z;
            RotX.Value = (decimal)col.OffsetPose.RotationX;
            RotY.Value = (decimal)col.OffsetPose.RotationY;
            RotZ.Value = (decimal)col.OffsetPose.RotationZ;

            HasCustomMaterial.Value = col.Material is not null;
            ColliderMaterialParams mat = col.Material ?? new ColliderMaterialParams(1.0f, 10.0f, 30.0f, 1.0f);
            FrictionCoefficient.Value = (decimal)mat.FrictionCoefficient;
            MaxRecoveryVelocity.Value = (decimal)(float.MaxValue * 0.5f < mat.MaxRecoveryVelocity ? 999999m : (decimal)mat.MaxRecoveryVelocity);
            SpringFrequency.Value = (decimal)mat.SpringFrequency;
            SpringDampingRatio.Value = (decimal)mat.SpringDampingRatio;

            UpdateDerivedFlags(entry.ModelPath, col);
            LiveTsvPreview.Value = FormatEntryLine(entry);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void UpdateDerivedFlags(string? modelPath, ModelColliderSpec col)
    {
        ModelColliderKind kind = col.Kind;
        ShowCollisionModelPath.Value = kind == ModelColliderKind.ClosedModel || kind == ModelColliderKind.OpenModel;
        ShowPrimitiveDimensions.Value = kind == ModelColliderKind.Box || kind == ModelColliderKind.Cylinder || kind == ModelColliderKind.Sphere;
        ShowSizeY.Value = kind == ModelColliderKind.Box || kind == ModelColliderKind.Cylinder;
        ShowSizeZ.Value = kind == ModelColliderKind.Box;
        CanHaveMaterial.Value = kind != ModelColliderKind.NonCollision;

        HasUppercaseCollisionPathWarning.Value = !string.IsNullOrEmpty(col.CollisionModelPath)
            && col.CollisionModelPath.Any(char.IsUpper);

        bool requiresModel = kind == ModelColliderKind.BoundingBox
            || kind == ModelColliderKind.ConvexHull
            || kind == ModelColliderKind.ClosedModel
            || kind == ModelColliderKind.OpenModel;
        HasNoneModelError.Value = requiresModel && string.IsNullOrWhiteSpace(modelPath);
    }

    private static string FormatEntrySummary(ModelListEntry entry)
    {
        string line = FormatEntryLine(entry);
        int tabIdx = line.LastIndexOf('\t');
        return 0 <= tabIdx ? line[(tabIdx + 1)..] : line;
    }

    private static string FormatEntryLine(ModelListEntry entry)
    {
        ModelListDocument temp = new ModelListDocument();
        temp.Entries.Add(entry);
        return ModelListSerializer.Generate(temp).TrimEnd('\r', '\n');
    }
}
