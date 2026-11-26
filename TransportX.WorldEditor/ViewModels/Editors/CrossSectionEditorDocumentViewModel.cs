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
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// Row wrapper for a <see cref="CrossSectionVertexModel"/>.
/// </summary>
public sealed class CrossSectionVertexRow : INotifyPropertyChanged
{
    private int IndexValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public CrossSectionVertexModel Model { get; }
    public string Summary => $"{Model.Key} ({Model.X:0.###}, {Model.Y:0.###}) N=({Model.NormalX:0.##}, {Model.NormalY:0.##}) U={Model.U:0.###}";

    public CrossSectionVertexRow(int index, CrossSectionVertexModel model)
    {
        IndexValue = index;
        Model = model;
    }

    public void UpdateIndex(int index)
    {
        IndexValue = index;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// Row wrapper for a <see cref="CrossSectionSurfaceModel"/>.
/// </summary>
public sealed class CrossSectionSurfaceRow : INotifyPropertyChanged
{
    private int IndexValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public CrossSectionSurfaceModel Model { get; }
    public string Summary => $"[{IndexValue}] {Model.MaterialName} ({Model.VertexKeys.Count}頂点 / Col:{Model.ColliderMaterialName ?? "None"})";

    public CrossSectionSurfaceRow(int index, CrossSectionSurfaceModel model)
    {
        IndexValue = index;
        Model = model;
    }

    public void UpdateIndex(int index)
    {
        IndexValue = index;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// Document ViewModel for the interactive 2D <c>CrossSection</c> editor with real-time single-ColliderMaterial validation.
/// </summary>
public sealed class CrossSectionEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly IFileDialogService DialogService;
    private bool IsSyncing;
    private bool IsUpdatingList;
    private bool IsCommitting;

    public string CrossSectionKey { get; }
    public IReadOnlyList<TextureSampleModeModel> SampleModes { get; } = Enum.GetValues<TextureSampleModeModel>();

    public BindableReactiveProperty<bool> HasColliderConflictError { get; }
    public BindableReactiveProperty<string> ValidationBannerText { get; }
    public BindableReactiveProperty<bool> HasValidationBanner { get; }

    public ObservableCollection<CrossSectionVertexRow> Vertices { get; } = new ObservableCollection<CrossSectionVertexRow>();
    public ObservableCollection<CrossSectionSurfaceRow> Surfaces { get; } = new ObservableCollection<CrossSectionSurfaceRow>();
    public ObservableCollection<CrossSectionMaterialModel> Materials { get; } = new ObservableCollection<CrossSectionMaterialModel>();
    public ObservableCollection<CrossSectionColliderMaterialModel> ColliderMaterials { get; } = new ObservableCollection<CrossSectionColliderMaterialModel>();

    public ObservableCollection<string> AvailableMaterialNames { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> AvailableColliderMaterialOptions { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> AvailableVertexKeys { get; } = new ObservableCollection<string>();

    // Selected Vertex state
    public BindableReactiveProperty<CrossSectionVertexRow?> SelectedVertex { get; }
    public BindableReactiveProperty<bool> HasSelectedVertex { get; }
    public BindableReactiveProperty<string> VertexKey { get; }
    public BindableReactiveProperty<decimal?> VertexX { get; }
    public BindableReactiveProperty<decimal?> VertexY { get; }
    public BindableReactiveProperty<decimal?> NormalX { get; }
    public BindableReactiveProperty<decimal?> NormalY { get; }
    public BindableReactiveProperty<decimal?> VertexU { get; }
    public BindableReactiveProperty<string> VertexColorHex { get; }

    // Selected Surface state
    public BindableReactiveProperty<CrossSectionSurfaceRow?> SelectedSurface { get; }
    public BindableReactiveProperty<bool> HasSelectedSurface { get; }
    public BindableReactiveProperty<decimal?> SurfaceVInterval { get; }
    public BindableReactiveProperty<decimal?> SurfaceVOffset { get; }
    public BindableReactiveProperty<string> SurfaceMaterialName { get; }
    public BindableReactiveProperty<string> SurfaceColliderOption { get; }
    public ObservableCollection<string> SelectedSurfaceVertexKeys { get; } = new ObservableCollection<string>();
    public BindableReactiveProperty<int> SelectedSurfaceVertexIndex { get; }
    public BindableReactiveProperty<string> VertexKeyToAdd { get; }

    // Selected Material state
    public BindableReactiveProperty<CrossSectionMaterialModel?> SelectedMaterial { get; }
    public BindableReactiveProperty<bool> HasSelectedMaterial { get; }
    public BindableReactiveProperty<string> MaterialName { get; }
    public BindableReactiveProperty<string> MaterialBaseColorHex { get; }
    public BindableReactiveProperty<double> MaterialMetallic { get; }
    public BindableReactiveProperty<decimal?> MaterialMetallicNumeric { get; }
    public BindableReactiveProperty<double> MaterialRoughness { get; }
    public BindableReactiveProperty<decimal?> MaterialRoughnessNumeric { get; }
    public BindableReactiveProperty<string> MaterialEmissiveHex { get; }
    public BindableReactiveProperty<decimal?> MaterialEmissiveStrength { get; }
    public BindableReactiveProperty<string> BaseColorTexPath { get; }
    public BindableReactiveProperty<string> NormalTexPath { get; }
    public BindableReactiveProperty<string> OcclusionTexPath { get; }
    public BindableReactiveProperty<string> RoughnessTexPath { get; }
    public BindableReactiveProperty<string> MetallicTexPath { get; }
    public BindableReactiveProperty<string> EmissiveTexPath { get; }
    public BindableReactiveProperty<TextureSampleModeModel> BaseColorTexMode { get; }
    public BindableReactiveProperty<TextureSampleModeModel> EmissiveTexMode { get; }

    // Selected ColliderMaterial state
    public BindableReactiveProperty<CrossSectionColliderMaterialModel?> SelectedColliderMaterial { get; }
    public BindableReactiveProperty<bool> HasSelectedColliderMaterial { get; }
    public BindableReactiveProperty<string> ColliderName { get; }
    public BindableReactiveProperty<decimal?> ColliderFriction { get; }
    public BindableReactiveProperty<decimal?> ColliderMaxRecovery { get; }
    public BindableReactiveProperty<decimal?> ColliderSpringFreq { get; }
    public BindableReactiveProperty<decimal?> ColliderSpringDamping { get; }

    public ReactiveCommand<Unit> AddVertexCommand { get; }
    public ReactiveCommand<Unit> RemoveVertexCommand { get; }
    public ReactiveCommand<Unit> MoveVertexUpCommand { get; }
    public ReactiveCommand<Unit> MoveVertexDownCommand { get; }
    public ReactiveCommand<Unit> NormalizeVertexNormalCommand { get; }

    public ReactiveCommand<Unit> AddSurfaceCommand { get; }
    public ReactiveCommand<Unit> RemoveSurfaceCommand { get; }
    public ReactiveCommand<Unit> MoveSurfaceUpCommand { get; }
    public ReactiveCommand<Unit> MoveSurfaceDownCommand { get; }
    public ReactiveCommand<Unit> AddVertexRefToSurfaceCommand { get; }
    public ReactiveCommand<Unit> RemoveVertexRefFromSurfaceCommand { get; }

    public ReactiveCommand<Unit> AddMaterialCommand { get; }
    public ReactiveCommand<Unit> RemoveMaterialCommand { get; }
    public ReactiveCommand<Unit> MoveMaterialUpCommand { get; }
    public ReactiveCommand<Unit> MoveMaterialDownCommand { get; }
    public ReactiveCommand<string> BrowseTextureCommand { get; }

    public ReactiveCommand<Unit> AddColliderMaterialCommand { get; }
    public ReactiveCommand<Unit> RemoveColliderMaterialCommand { get; }
    public ReactiveCommand<Unit> MoveColliderMaterialUpCommand { get; }
    public ReactiveCommand<Unit> MoveColliderMaterialDownCommand { get; }

    public CrossSectionEditorDocumentViewModel(
        WorldWorkspaceService workspace,
        IFileDialogService dialogService,
        string crossSectionKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(crossSectionKey);

        Workspace = workspace;
        DialogService = dialogService;
        CrossSectionKey = crossSectionKey;
        Id = $"Doc_CrossSection_{crossSectionKey}";
        Title = $"断面: {crossSectionKey}";

        HasColliderConflictError = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ValidationBannerText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        HasValidationBanner = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        SelectedVertex = new BindableReactiveProperty<CrossSectionVertexRow?>(null).AddTo(Disposables);
        HasSelectedVertex = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        VertexKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        VertexX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        VertexY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        NormalX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        NormalY = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        VertexU = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        VertexColorHex = new BindableReactiveProperty<string>("#FFFFFF").AddTo(Disposables);

        SelectedSurface = new BindableReactiveProperty<CrossSectionSurfaceRow?>(null).AddTo(Disposables);
        HasSelectedSurface = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SurfaceVInterval = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        SurfaceVOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SurfaceMaterialName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SurfaceColliderOption = new BindableReactiveProperty<string>("(None)").AddTo(Disposables);
        SelectedSurfaceVertexIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        VertexKeyToAdd = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedMaterial = new BindableReactiveProperty<CrossSectionMaterialModel?>(null).AddTo(Disposables);
        HasSelectedMaterial = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        MaterialName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        MaterialBaseColorHex = new BindableReactiveProperty<string>("#FFFFFF").AddTo(Disposables);
        MaterialMetallic = new BindableReactiveProperty<double>(0.0).AddTo(Disposables);
        MaterialMetallicNumeric = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        MaterialRoughness = new BindableReactiveProperty<double>(1.0).AddTo(Disposables);
        MaterialRoughnessNumeric = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        MaterialEmissiveHex = new BindableReactiveProperty<string>("#FFFFFF").AddTo(Disposables);
        MaterialEmissiveStrength = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        BaseColorTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NormalTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        OcclusionTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        RoughnessTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        MetallicTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        EmissiveTexPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        BaseColorTexMode = new BindableReactiveProperty<TextureSampleModeModel>(TextureSampleModeModel.Texture).AddTo(Disposables);
        EmissiveTexMode = new BindableReactiveProperty<TextureSampleModeModel>(TextureSampleModeModel.Texture).AddTo(Disposables);

        SelectedColliderMaterial = new BindableReactiveProperty<CrossSectionColliderMaterialModel?>(null).AddTo(Disposables);
        HasSelectedColliderMaterial = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ColliderName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ColliderFriction = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        ColliderMaxRecovery = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);
        ColliderSpringFreq = new BindableReactiveProperty<decimal?>(30m).AddTo(Disposables);
        ColliderSpringDamping = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);

        SelectedVertex.Subscribe(_ => { if (!IsUpdatingList) SyncVertexInspector(); }).AddTo(Disposables);
        SelectedSurface.Subscribe(_ => { if (!IsUpdatingList) SyncSurfaceInspector(); }).AddTo(Disposables);
        SelectedMaterial.Subscribe(_ => { if (!IsUpdatingList) SyncMaterialInspector(); }).AddTo(Disposables);
        SelectedColliderMaterial.Subscribe(_ => { if (!IsUpdatingList) SyncColliderInspector(); }).AddTo(Disposables);

        HookCommit(VertexKey, CommitVertexEdit);
        HookCommit(VertexX, CommitVertexEdit);
        HookCommit(VertexY, CommitVertexEdit);
        HookCommit(NormalX, CommitVertexEdit);
        HookCommit(NormalY, CommitVertexEdit);
        HookCommit(VertexU, CommitVertexEdit);
        HookCommit(VertexColorHex, CommitVertexEdit);

        HookCommit(SurfaceVInterval, CommitSurfaceEdit);
        HookCommit(SurfaceVOffset, CommitSurfaceEdit);
        HookCommit(SurfaceMaterialName, CommitSurfaceEdit);
        HookCommit(SurfaceColliderOption, CommitSurfaceEdit);

        HookCommit(MaterialName, CommitMaterialEdit);
        HookCommit(MaterialBaseColorHex, CommitMaterialEdit);
        HookNormalizedMaterialRange(MaterialMetallic, MaterialMetallicNumeric);
        HookNormalizedMaterialRange(MaterialRoughness, MaterialRoughnessNumeric);
        HookCommit(MaterialEmissiveHex, CommitMaterialEdit);
        HookCommit(MaterialEmissiveStrength, CommitMaterialEdit);
        HookCommit(BaseColorTexPath, CommitMaterialEdit);
        HookCommit(NormalTexPath, CommitMaterialEdit);
        HookCommit(OcclusionTexPath, CommitMaterialEdit);
        HookCommit(RoughnessTexPath, CommitMaterialEdit);
        HookCommit(MetallicTexPath, CommitMaterialEdit);
        HookCommit(EmissiveTexPath, CommitMaterialEdit);
        HookCommit(BaseColorTexMode, CommitMaterialEdit);
        HookCommit(EmissiveTexMode, CommitMaterialEdit);

        HookCommit(ColliderName, CommitColliderEdit);
        HookCommit(ColliderFriction, CommitColliderEdit);
        HookCommit(ColliderMaxRecovery, CommitColliderEdit);
        HookCommit(ColliderSpringFreq, CommitColliderEdit);
        HookCommit(ColliderSpringDamping, CommitColliderEdit);

        AddVertexCommand = new ReactiveCommand<Unit>(_ => AddVertex()).AddTo(Disposables);
        RemoveVertexCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedVertex()).AddTo(Disposables);
        MoveVertexUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedVertex(-1)).AddTo(Disposables);
        MoveVertexDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedVertex(1)).AddTo(Disposables);
        NormalizeVertexNormalCommand = new ReactiveCommand<Unit>(_ => NormalizeSelectedVertexNormal()).AddTo(Disposables);

        AddSurfaceCommand = new ReactiveCommand<Unit>(_ => AddSurface()).AddTo(Disposables);
        RemoveSurfaceCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedSurface()).AddTo(Disposables);
        MoveSurfaceUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedSurface(-1)).AddTo(Disposables);
        MoveSurfaceDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedSurface(1)).AddTo(Disposables);
        AddVertexRefToSurfaceCommand = new ReactiveCommand<Unit>(_ => AddVertexRefToSelectedSurface()).AddTo(Disposables);
        RemoveVertexRefFromSurfaceCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedVertexRefFromSurface()).AddTo(Disposables);

        AddMaterialCommand = new ReactiveCommand<Unit>(_ => AddMaterial()).AddTo(Disposables);
        RemoveMaterialCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedMaterial()).AddTo(Disposables);
        MoveMaterialUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedMaterial(-1)).AddTo(Disposables);
        MoveMaterialDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedMaterial(1)).AddTo(Disposables);
        BrowseTextureCommand = new ReactiveCommand<string>(slotName => { _ = BrowseTextureAsync(slotName); }).AddTo(Disposables);

        AddColliderMaterialCommand = new ReactiveCommand<Unit>(_ => AddColliderMaterial()).AddTo(Disposables);
        RemoveColliderMaterialCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedColliderMaterial()).AddTo(Disposables);
        MoveColliderMaterialUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedColliderMaterial(-1)).AddTo(Disposables);
        MoveColliderMaterialDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedColliderMaterial(1)).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            $"断面: {CrossSectionKey}",
            (_, target) =>
            {
                if (target.Kind != WorldFileKind.CrossSectionXml)
                {
                    return false;
                }

                CrossSectionDocument? doc = GetDocument();
                return doc is not null && string.Equals(
                    WorldProject.NormalizeRelativePath(target.RelativePath),
                    WorldProject.NormalizeRelativePath(doc.RelativePath),
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    private void HookCommit<T>(BindableReactiveProperty<T> prop, Action commitAction)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                commitAction();
            }
        }).AddTo(Disposables);
    }

    private bool MoveDocListItem<T>(IList<T> list, int oldIndex, int delta, string description)
    {
        CrossSectionDocument? doc = GetDocument();
        int newIndex = oldIndex + delta;
        if (doc is null || oldIndex < 0 || list.Count <= oldIndex || newIndex < 0 || list.Count <= newIndex)
        {
            return false;
        }

        T item = list[oldIndex];
        WorldFileTarget target = new(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            description,
            InvalidationScope.TemplateDependents,
            target,
            () =>
            {
                list.RemoveAt(oldIndex);
                list.Insert(newIndex, item);
            },
            () =>
            {
                list.RemoveAt(newIndex);
                list.Insert(oldIndex, item);
            }));

        return true;
    }

    private void MoveSelectedVertex(int delta)
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionVertexRow? row = SelectedVertex.Value;
        if (doc is null || row is null)
        {
            return;
        }

        int idx = doc.Vertices.IndexOf(row.Model);
        if (MoveDocListItem(doc.Vertices, idx, delta, $"頂点 '{row.Model.Key}' の順序を変更"))
        {
            SelectedVertex.Value = Vertices.FirstOrDefault(r => ReferenceEquals(r.Model, row.Model));
        }
    }

    private void MoveSelectedSurface(int delta)
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionSurfaceRow? row = SelectedSurface.Value;
        if (doc is null || row is null)
        {
            return;
        }

        int idx = doc.Surfaces.IndexOf(row.Model);
        if (MoveDocListItem(doc.Surfaces, idx, delta, $"Surface[{idx}] の順序を変更"))
        {
            SelectedSurface.Value = Surfaces.FirstOrDefault(r => ReferenceEquals(r.Model, row.Model));
        }
    }

    private void MoveSelectedMaterial(int delta)
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionMaterialModel? mat = SelectedMaterial.Value;
        if (doc is null || mat is null)
        {
            return;
        }

        int idx = doc.Materials.IndexOf(mat);
        if (MoveDocListItem(doc.Materials, idx, delta, $"Material '{mat.Name}' の順序を変更"))
        {
            SelectedMaterial.Value = mat;
        }
    }

    private void MoveSelectedColliderMaterial(int delta)
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionColliderMaterialModel? cm = SelectedColliderMaterial.Value;
        if (doc is null || cm is null)
        {
            return;
        }

        int idx = doc.ColliderMaterials.IndexOf(cm);
        if (MoveDocListItem(doc.ColliderMaterials, idx, delta, $"ColliderMaterial '{cm.Name}' の順序を変更"))
        {
            SelectedColliderMaterial.Value = cm;
        }
    }

    private void HookNormalizedMaterialRange(
        BindableReactiveProperty<double> sliderProp,
        BindableReactiveProperty<decimal?> numericProp)
    {
        sliderProp.Skip(1).Subscribe(v =>
        {
            if (IsSyncing)
            {
                return;
            }

            double clamped = double.Clamp(v, 0.0, 1.0);
            bool prevSync = IsSyncing;
            IsSyncing = true;
            try
            {
                numericProp.Value = (decimal)double.Round(clamped, 3);
            }
            finally
            {
                IsSyncing = prevSync;
            }

            CommitMaterialEdit();
        }).AddTo(Disposables);

        numericProp.Skip(1).Subscribe(v =>
        {
            if (IsSyncing || !v.HasValue)
            {
                return;
            }

            double clamped = double.Clamp((double)v.Value, 0.0, 1.0);
            bool prevSync = IsSyncing;
            IsSyncing = true;
            try
            {
                sliderProp.Value = clamped;
            }
            finally
            {
                IsSyncing = prevSync;
            }

            CommitMaterialEdit();
        }).AddTo(Disposables);
    }

    public CrossSectionDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.CrossSections.TryGetValue(CrossSectionKey, out CrossSectionDocument? doc) ? doc : null;
    }

    public void SelectVertexIndex(int index)
    {
        SelectedVertex.Value = 0 <= index && index < Vertices.Count ? Vertices[index] : null;
    }

    public void MoveVertexFromCanvas(int vertexIndex, float newX, float newY)
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null || vertexIndex < 0 || doc.Vertices.Count <= vertexIndex)
        {
            return;
        }

        CrossSectionVertexModel vertex = doc.Vertices[vertexIndex];
        float rx = MathF.Round(newX, 3);
        float ry = MathF.Round(newY, 3);
        float oldX = vertex.X;
        float oldY = vertex.Y;
        if (MathF.Abs(oldX - rx) < 1e-4f && MathF.Abs(oldY - ry) < 1e-4f)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new MergeableValueCommand<System.Numerics.Vector2>(
            $"頂点 '{vertex.Key}' を移動",
            $"CS.{CrossSectionKey}.VPos.{vertexIndex}",
            InvalidationScope.TemplateDependents,
            target,
            new System.Numerics.Vector2(oldX, oldY),
            new System.Numerics.Vector2(rx, ry),
            pos =>
            {
                vertex.X = pos.X;
                vertex.Y = pos.Y;
            }));
    }

    public void SetVertexNormalFromCanvas(int vertexIndex, float rawNx, float rawNy)
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null || vertexIndex < 0 || doc.Vertices.Count <= vertexIndex)
        {
            return;
        }

        float lenSq = rawNx * rawNx + rawNy * rawNy;
        if (lenSq < 1e-6f)
        {
            return;
        }

        float len = MathF.Sqrt(lenSq);
        float nx = MathF.Round(rawNx / len, 3);
        float ny = MathF.Round(rawNy / len, 3);

        CrossSectionVertexModel vertex = doc.Vertices[vertexIndex];
        float oldNx = vertex.NormalX;
        float oldNy = vertex.NormalY;

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new MergeableValueCommand<System.Numerics.Vector2>(
            $"頂点 '{vertex.Key}' の法線を調整",
            $"CS.{CrossSectionKey}.VNorm.{vertexIndex}",
            InvalidationScope.TemplateDependents,
            target,
            new System.Numerics.Vector2(oldNx, oldNy),
            new System.Numerics.Vector2(nx, ny),
            n =>
            {
                vertex.NormalX = n.X;
                vertex.NormalY = n.Y;
            }));
    }

    private void AddVertex()
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        int n = doc.Vertices.Count;
        string key = $"V{n}";
        while (doc.Vertices.Any(v => string.Equals(v.Key, key, StringComparison.Ordinal)))
        {
            n++;
            key = $"V{n}";
        }

        float nextX = doc.Vertices.Count == 0 ? 0f : doc.Vertices[^1].X + 1.0f;
        float nextY = doc.Vertices.Count == 0 ? 0f : doc.Vertices[^1].Y;
        CrossSectionVertexModel v = new CrossSectionVertexModel { Key = key, X = nextX, Y = nextY };
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));

        Workspace.Execute(new DelegateEditorCommand(
            $"頂点 '{key}' を追加",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Vertices.Add(v),
            () => doc.Vertices.Remove(v)));

        SelectedVertex.Value = Vertices.LastOrDefault();
    }

    private void RemoveSelectedVertex()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionVertexRow? row = SelectedVertex.Value;
        if (doc is null || row is null)
        {
            return;
        }

        int idx = doc.Vertices.IndexOf(row.Model);
        if (idx < 0)
        {
            return;
        }

        CrossSectionVertexModel removed = row.Model;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"頂点 '{removed.Key}' を削除",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Vertices.RemoveAt(idx),
            () => doc.Vertices.Insert(idx, removed)));
    }

    private void NormalizeSelectedVertexNormal()
    {
        CrossSectionVertexRow? row = SelectedVertex.Value;
        if (row is null)
        {
            return;
        }

        SetVertexNormalFromCanvas(row.Index, (float)(NormalX.Value ?? 0m), (float)(NormalY.Value ?? 1m));
    }

    private void CommitVertexEdit()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionVertexRow? row = SelectedVertex.Value;
        if (doc is null || row is null)
        {
            return;
        }

        CrossSectionVertexModel model = row.Model;
        string oldKey = model.Key;
        float oldX = model.X;
        float oldY = model.Y;
        float oldNx = model.NormalX;
        float oldNy = model.NormalY;
        float oldU = model.U;
        string oldColor = model.ColorHex;

        string newKey = VertexKey.Value.Trim();
        float newX = (float)(VertexX.Value ?? 0m);
        float newY = (float)(VertexY.Value ?? 0m);
        float newNx = (float)(NormalX.Value ?? 0m);
        float newNy = (float)(NormalY.Value ?? 1m);
        float newU = (float)(VertexU.Value ?? 0m);
        string newColor = string.IsNullOrWhiteSpace(VertexColorHex.Value) ? "#FFFFFF" : VertexColorHex.Value.Trim();

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"頂点 '{oldKey}' を更新",
                InvalidationScope.TemplateDependents,
                target,
                () =>
                {
                    model.Key = newKey;
                    model.X = newX;
                    model.Y = newY;
                    model.NormalX = newNx;
                    model.NormalY = newNy;
                    model.U = newU;
                    model.ColorHex = newColor;
                },
                () =>
                {
                    model.Key = oldKey;
                    model.X = oldX;
                    model.Y = oldY;
                    model.NormalX = oldNx;
                    model.NormalY = oldNy;
                    model.U = oldU;
                    model.ColorHex = oldColor;
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void AddSurface()
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        CrossSectionSurfaceModel s = new CrossSectionSurfaceModel
        {
            MaterialName = doc.Materials.FirstOrDefault()?.Name ?? "Default",
            ColliderMaterialName = doc.ColliderMaterials.FirstOrDefault()?.Name,
        };
        if (2 <= doc.Vertices.Count)
        {
            s.VertexKeys.Add(doc.Vertices[0].Key);
            s.VertexKeys.Add(doc.Vertices[1].Key);
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            "Surface を追加",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Surfaces.Add(s),
            () => doc.Surfaces.Remove(s)));

        SelectedSurface.Value = Surfaces.LastOrDefault();
    }

    private void RemoveSelectedSurface()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionSurfaceRow? row = SelectedSurface.Value;
        if (doc is null || row is null)
        {
            return;
        }

        int idx = doc.Surfaces.IndexOf(row.Model);
        if (idx < 0)
        {
            return;
        }

        CrossSectionSurfaceModel removed = row.Model;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"Surface[{idx}] を削除",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Surfaces.RemoveAt(idx),
            () => doc.Surfaces.Insert(idx, removed)));
    }

    private void AddVertexRefToSelectedSurface()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionSurfaceRow? row = SelectedSurface.Value;
        string vKey = VertexKeyToAdd.Value;
        if (doc is null || row is null || string.IsNullOrEmpty(vKey))
        {
            return;
        }

        CrossSectionSurfaceModel surface = row.Model;
        int insertIdx = surface.VertexKeys.Count;
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"Surface へ頂点参照 '{vKey}' を追加",
            InvalidationScope.TemplateDependents,
            target,
            () => surface.VertexKeys.Insert(insertIdx, vKey),
            () => surface.VertexKeys.RemoveAt(insertIdx)));
    }

    private void RemoveSelectedVertexRefFromSurface()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionSurfaceRow? row = SelectedSurface.Value;
        int idx = SelectedSurfaceVertexIndex.Value;
        if (doc is null || row is null || idx < 0 || row.Model.VertexKeys.Count <= idx)
        {
            return;
        }

        CrossSectionSurfaceModel surface = row.Model;
        string removedKey = surface.VertexKeys[idx];
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"Surface から頂点参照 '{removedKey}' を削除",
            InvalidationScope.TemplateDependents,
            target,
            () => surface.VertexKeys.RemoveAt(idx),
            () => surface.VertexKeys.Insert(idx, removedKey)));
    }

    private void CommitSurfaceEdit()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionSurfaceRow? row = SelectedSurface.Value;
        if (doc is null || row is null)
        {
            return;
        }

        CrossSectionSurfaceModel surface = row.Model;
        float oldVi = surface.VInterval;
        float oldVo = surface.VOffset;
        string oldMat = surface.MaterialName;
        string? oldCol = surface.ColliderMaterialName;

        float newVi = (float)(SurfaceVInterval.Value ?? 1m);
        float newVo = (float)(SurfaceVOffset.Value ?? 0m);
        string newMat = SurfaceMaterialName.Value;
        string? newCol = SurfaceColliderOption.Value == "(None)" || string.IsNullOrEmpty(SurfaceColliderOption.Value)
            ? null
            : SurfaceColliderOption.Value;

        if (float.Abs(oldVi - newVi) < 1e-6f
            && float.Abs(oldVo - newVo) < 1e-6f
            && string.Equals(oldMat, newMat, StringComparison.Ordinal)
            && string.Equals(oldCol, newCol, StringComparison.Ordinal))
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"Surface[{row.Index}] を更新",
                InvalidationScope.TemplateDependents,
                target,
                () =>
                {
                    surface.VInterval = newVi;
                    surface.VOffset = newVo;
                    surface.MaterialName = newMat;
                    surface.ColliderMaterialName = newCol;
                },
                () =>
                {
                    surface.VInterval = oldVi;
                    surface.VOffset = oldVo;
                    surface.MaterialName = oldMat;
                    surface.ColliderMaterialName = oldCol;
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void AddMaterial()
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        CrossSectionMaterialModel mat = new CrossSectionMaterialModel
        {
            Name = $"Material_{doc.Materials.Count + 1}",
        };
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"Material '{mat.Name}' を追加",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Materials.Add(mat),
            () => doc.Materials.Remove(mat)));

        SelectedMaterial.Value = mat;
    }

    private void RemoveSelectedMaterial()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionMaterialModel? mat = SelectedMaterial.Value;
        if (doc is null || mat is null)
        {
            return;
        }

        int idx = doc.Materials.IndexOf(mat);
        if (idx < 0)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"Material '{mat.Name}' を削除",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.Materials.RemoveAt(idx),
            () => doc.Materials.Insert(idx, mat)));
    }

    private void CommitMaterialEdit()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionMaterialModel? mat = SelectedMaterial.Value;
        if (doc is null || mat is null)
        {
            return;
        }

        string oldName = mat.Name;
        string oldBase = mat.BaseColorHex;
        float oldMetal = mat.Metallic;
        float oldRough = mat.Roughness;
        string oldEmissive = mat.EmissiveHex;
        float oldEmissiveStr = mat.EmissiveStrength;
        CrossSectionTextureSlotModel oldBc = mat.BaseColorTexture;
        CrossSectionTextureSlotModel oldNm = mat.NormalTexture;
        CrossSectionTextureSlotModel oldOc = mat.OcclusionTexture;
        CrossSectionTextureSlotModel oldRg = mat.RoughnessTexture;
        CrossSectionTextureSlotModel oldMt = mat.MetallicTexture;
        CrossSectionTextureSlotModel oldEm = mat.EmissiveTexture;

        string newName = MaterialName.Value.Trim();
        string newBase = string.IsNullOrWhiteSpace(MaterialBaseColorHex.Value) ? "#FFFFFF" : MaterialBaseColorHex.Value.Trim();
        float newMetal = (float)MaterialMetallic.Value;
        float newRough = (float)MaterialRoughness.Value;
        string newEmissive = string.IsNullOrWhiteSpace(MaterialEmissiveHex.Value) ? "#FFFFFF" : MaterialEmissiveHex.Value.Trim();
        float newEmissiveStr = (float)(MaterialEmissiveStrength.Value ?? 1m);

        CrossSectionTextureSlotModel newBc = MakeSlot(BaseColorTexPath.Value, BaseColorTexMode.Value);
        CrossSectionTextureSlotModel newNm = MakeSlot(NormalTexPath.Value, TextureSampleModeModel.Texture);
        CrossSectionTextureSlotModel newOc = MakeSlot(OcclusionTexPath.Value, TextureSampleModeModel.Texture);
        CrossSectionTextureSlotModel newRg = MakeSlot(RoughnessTexPath.Value, TextureSampleModeModel.Texture);
        CrossSectionTextureSlotModel newMt = MakeSlot(MetallicTexPath.Value, TextureSampleModeModel.Texture);
        CrossSectionTextureSlotModel newEm = MakeSlot(EmissiveTexPath.Value, EmissiveTexMode.Value);

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"Material '{oldName}' を更新",
                InvalidationScope.TemplateDependents,
                target,
                () =>
                {
                    mat.Name = newName;
                    mat.BaseColorHex = newBase;
                    mat.Metallic = newMetal;
                    mat.Roughness = newRough;
                    mat.EmissiveHex = newEmissive;
                    mat.EmissiveStrength = newEmissiveStr;
                    mat.BaseColorTexture = newBc;
                    mat.NormalTexture = newNm;
                    mat.OcclusionTexture = newOc;
                    mat.RoughnessTexture = newRg;
                    mat.MetallicTexture = newMt;
                    mat.EmissiveTexture = newEm;
                },
                () =>
                {
                    mat.Name = oldName;
                    mat.BaseColorHex = oldBase;
                    mat.Metallic = oldMetal;
                    mat.Roughness = oldRough;
                    mat.EmissiveHex = oldEmissive;
                    mat.EmissiveStrength = oldEmissiveStr;
                    mat.BaseColorTexture = oldBc;
                    mat.NormalTexture = oldNm;
                    mat.OcclusionTexture = oldOc;
                    mat.RoughnessTexture = oldRg;
                    mat.MetallicTexture = oldMt;
                    mat.EmissiveTexture = oldEm;
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private async Task BrowseTextureAsync(string slotName)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionDocument? doc = GetDocument();
        if (project is null || doc is null || SelectedMaterial.Value is null)
        {
            return;
        }

        string xmlDir = Path.GetDirectoryName(Path.Combine(project.WorldDirectory, doc.RelativePath)) ?? project.WorldDirectory;
        string? picked = await DialogService.PickAssetFileAsync(
            $"{slotName} テクスチャを選択",
            xmlDir,
            "Image",
            new string[] { "*.png", "*.jpg", "*.jpeg", "*.dds" });

        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        string rel = Path.GetRelativePath(xmlDir, picked).Replace('\\', '/');
        switch (slotName)
        {
            case "BaseColor": BaseColorTexPath.Value = rel; break;
            case "Normal": NormalTexPath.Value = rel; break;
            case "Occlusion": OcclusionTexPath.Value = rel; break;
            case "Roughness": RoughnessTexPath.Value = rel; break;
            case "Metallic": MetallicTexPath.Value = rel; break;
            case "Emissive": EmissiveTexPath.Value = rel; break;
        }
    }

    private void AddColliderMaterial()
    {
        CrossSectionDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        CrossSectionColliderMaterialModel cm = new CrossSectionColliderMaterialModel
        {
            Name = $"Collider_{doc.ColliderMaterials.Count + 1}",
        };
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"ColliderMaterial '{cm.Name}' を追加",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.ColliderMaterials.Add(cm),
            () => doc.ColliderMaterials.Remove(cm)));

        SelectedColliderMaterial.Value = cm;
    }

    private void RemoveSelectedColliderMaterial()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionColliderMaterialModel? cm = SelectedColliderMaterial.Value;
        if (doc is null || cm is null)
        {
            return;
        }

        int idx = doc.ColliderMaterials.IndexOf(cm);
        if (idx < 0)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"ColliderMaterial '{cm.Name}' を削除",
            InvalidationScope.TemplateDependents,
            target,
            () => doc.ColliderMaterials.RemoveAt(idx),
            () => doc.ColliderMaterials.Insert(idx, cm)));
    }

    private void CommitColliderEdit()
    {
        CrossSectionDocument? doc = GetDocument();
        CrossSectionColliderMaterialModel? cm = SelectedColliderMaterial.Value;
        if (doc is null || cm is null)
        {
            return;
        }

        string oldName = cm.Name;
        float oldF = cm.FrictionCoefficient;
        float oldMrv = cm.MaxRecoveryVelocity;
        float oldSf = cm.SpringFrequency;
        float oldSdr = cm.SpringDampingRatio;

        string newName = ColliderName.Value.Trim();
        float newF = (float)(ColliderFriction.Value ?? 1m);
        float newMrv = (float)(ColliderMaxRecovery.Value ?? 5m);
        float newSf = (float)(ColliderSpringFreq.Value ?? 30m);
        float newSdr = (float)(ColliderSpringDamping.Value ?? 1m);

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(doc.RelativePath));
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"ColliderMaterial '{oldName}' を更新",
                InvalidationScope.TemplateDependents,
                target,
                () =>
                {
                    cm.Name = newName;
                    cm.FrictionCoefficient = newF;
                    cm.MaxRecoveryVelocity = newMrv;
                    cm.SpringFrequency = newSf;
                    cm.SpringDampingRatio = newSdr;
                },
                () =>
                {
                    cm.Name = oldName;
                    cm.FrictionCoefficient = oldF;
                    cm.MaxRecoveryVelocity = oldMrv;
                    cm.SpringFrequency = oldSf;
                    cm.SpringDampingRatio = oldSdr;
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private static CrossSectionTextureSlotModel MakeSlot(string path, TextureSampleModeModel mode)
    {
        string? trimmed = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        return trimmed is null && mode == TextureSampleModeModel.Texture
            ? CrossSectionTextureSlotModel.Empty
            : new CrossSectionTextureSlotModel(trimmed, mode);
    }

    private static bool HaveSameVertexModels(ObservableCollection<CrossSectionVertexRow> rows, List<CrossSectionVertexModel> models)
    {
        if (rows.Count != models.Count)
        {
            return false;
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (!ReferenceEquals(rows[i].Model, models[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameSurfaceModels(ObservableCollection<CrossSectionSurfaceRow> rows, List<CrossSectionSurfaceModel> models)
    {
        if (rows.Count != models.Count)
        {
            return false;
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (!ReferenceEquals(rows[i].Model, models[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameReferenceItems<T>(ObservableCollection<T> current, List<T> target) where T : class
    {
        if (current.Count != target.Count)
        {
            return false;
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (!ReferenceEquals(current[i], target[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void SyncStringCollection(ObservableCollection<string> target, List<string> next)
    {
        if (target.SequenceEqual(next, StringComparer.Ordinal))
        {
            return;
        }

        target.Clear();
        foreach (string item in next)
        {
            target.Add(item);
        }
    }

    private void SyncFromModel()
    {
        CrossSectionVertexModel? prevVertex = SelectedVertex.Value?.Model;
        CrossSectionSurfaceModel? prevSurface = SelectedSurface.Value?.Model;
        CrossSectionMaterialModel? prevMaterial = SelectedMaterial.Value;
        CrossSectionColliderMaterialModel? prevCollider = SelectedColliderMaterial.Value;

        CrossSectionDocument? doc = GetDocument();

        List<string> nextVertexKeys = new();
        List<string> nextMaterialNames = new();
        List<string> nextColliderOptions = new() { "(None)" };
        if (doc is not null)
        {
            foreach (CrossSectionVertexModel v in doc.Vertices)
            {
                if (!string.IsNullOrEmpty(v.Key))
                {
                    nextVertexKeys.Add(v.Key);
                }
            }

            foreach (CrossSectionMaterialModel m in doc.Materials)
            {
                if (!string.IsNullOrEmpty(m.Name))
                {
                    nextMaterialNames.Add(m.Name);
                }
            }

            foreach (CrossSectionColliderMaterialModel cm in doc.ColliderMaterials)
            {
                if (!string.IsNullOrEmpty(cm.Name))
                {
                    nextColliderOptions.Add(cm.Name);
                }
            }
        }

        IsUpdatingList = true;
        IsSyncing = true;
        try
        {
            SyncStringCollection(AvailableVertexKeys, nextVertexKeys);
            SyncStringCollection(AvailableMaterialNames, nextMaterialNames);
            SyncStringCollection(AvailableColliderMaterialOptions, nextColliderOptions);

            if (doc is null)
            {
                Vertices.Clear();
                Surfaces.Clear();
                Materials.Clear();
                ColliderMaterials.Clear();
                SelectedVertex.Value = null;
                SelectedSurface.Value = null;
                SelectedMaterial.Value = null;
                SelectedColliderMaterial.Value = null;
            }
            else
            {
                if (HaveSameVertexModels(Vertices, doc.Vertices))
                {
                    for (int i = 0; i < Vertices.Count; i++)
                    {
                        Vertices[i].UpdateIndex(i);
                    }
                }
                else
                {
                    Vertices.Clear();
                    for (int i = 0; i < doc.Vertices.Count; i++)
                    {
                        Vertices.Add(new CrossSectionVertexRow(i, doc.Vertices[i]));
                    }
                }

                if (HaveSameSurfaceModels(Surfaces, doc.Surfaces))
                {
                    for (int i = 0; i < Surfaces.Count; i++)
                    {
                        Surfaces[i].UpdateIndex(i);
                    }
                }
                else
                {
                    Surfaces.Clear();
                    for (int i = 0; i < doc.Surfaces.Count; i++)
                    {
                        Surfaces.Add(new CrossSectionSurfaceRow(i, doc.Surfaces[i]));
                    }
                }

                if (!HaveSameReferenceItems(Materials, doc.Materials))
                {
                    Materials.Clear();
                    foreach (CrossSectionMaterialModel m in doc.Materials)
                    {
                        Materials.Add(m);
                    }
                }

                if (!HaveSameReferenceItems(ColliderMaterials, doc.ColliderMaterials))
                {
                    ColliderMaterials.Clear();
                    foreach (CrossSectionColliderMaterialModel cm in doc.ColliderMaterials)
                    {
                        ColliderMaterials.Add(cm);
                    }
                }

                if (string.IsNullOrEmpty(VertexKeyToAdd.Value) && 0 < AvailableVertexKeys.Count)
                {
                    VertexKeyToAdd.Value = AvailableVertexKeys[0];
                }

                SelectedVertex.Value = prevVertex is not null
                    ? Vertices.FirstOrDefault(r => ReferenceEquals(r.Model, prevVertex))
                    : Vertices.FirstOrDefault();
                SelectedSurface.Value = prevSurface is not null
                    ? Surfaces.FirstOrDefault(r => ReferenceEquals(r.Model, prevSurface))
                    : Surfaces.FirstOrDefault();
                SelectedMaterial.Value = prevMaterial is not null && Materials.Contains(prevMaterial)
                    ? prevMaterial
                    : Materials.FirstOrDefault();
                SelectedColliderMaterial.Value = prevCollider is not null && ColliderMaterials.Contains(prevCollider)
                    ? prevCollider
                    : ColliderMaterials.FirstOrDefault();
            }

            List<ValidationIssue> docIssues = doc is null
                ? new List<ValidationIssue>()
                : Workspace.ValidationIssues.Value
                    .Where(i => string.Equals(i.SourcePath, doc.RelativePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            HasColliderConflictError.Value = docIssues.Any(i => i.Code == "CROSS_SECTION_MIXED_COLLIDER_MATERIALS");
            HasValidationBanner.Value = 0 < docIssues.Count;
            ValidationBannerText.Value = string.Join("\n", docIssues.Select(i => $"[{i.Severity}] {i.Message}"));
        }
        finally
        {
            IsSyncing = false;
            IsUpdatingList = false;
        }

        if (!IsCommitting)
        {
            SyncVertexInspector();
            SyncSurfaceInspector();
            SyncMaterialInspector();
            SyncColliderInspector();
        }
    }

    private void SyncVertexInspector()
    {
        IsSyncing = true;
        try
        {
            CrossSectionVertexRow? row = SelectedVertex.Value;
            if (row is null)
            {
                HasSelectedVertex.Value = false;
                return;
            }

            HasSelectedVertex.Value = true;
            CrossSectionVertexModel v = row.Model;
            VertexKey.Value = v.Key;
            VertexX.Value = (decimal)v.X;
            VertexY.Value = (decimal)v.Y;
            NormalX.Value = (decimal)v.NormalX;
            NormalY.Value = (decimal)v.NormalY;
            VertexU.Value = (decimal)v.U;
            VertexColorHex.Value = v.ColorHex;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void SyncSurfaceInspector()
    {
        IsSyncing = true;
        try
        {
            CrossSectionSurfaceRow? row = SelectedSurface.Value;
            SelectedSurfaceVertexKeys.Clear();
            if (row is null)
            {
                HasSelectedSurface.Value = false;
                return;
            }

            HasSelectedSurface.Value = true;
            CrossSectionSurfaceModel s = row.Model;
            SurfaceVInterval.Value = (decimal)s.VInterval;
            SurfaceVOffset.Value = (decimal)s.VOffset;
            SurfaceMaterialName.Value = s.MaterialName;
            SurfaceColliderOption.Value = string.IsNullOrEmpty(s.ColliderMaterialName) ? "(None)" : s.ColliderMaterialName;
            foreach (string vk in s.VertexKeys)
            {
                SelectedSurfaceVertexKeys.Add(vk);
            }
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void SyncMaterialInspector()
    {
        IsSyncing = true;
        try
        {
            CrossSectionMaterialModel? m = SelectedMaterial.Value;
            if (m is null)
            {
                HasSelectedMaterial.Value = false;
                return;
            }

            HasSelectedMaterial.Value = true;
            MaterialName.Value = m.Name;
            MaterialBaseColorHex.Value = m.BaseColorHex;
            MaterialMetallic.Value = m.Metallic;
            MaterialMetallicNumeric.Value = (decimal)double.Round(m.Metallic, 3);
            MaterialRoughness.Value = m.Roughness;
            MaterialRoughnessNumeric.Value = (decimal)double.Round(m.Roughness, 3);
            MaterialEmissiveHex.Value = m.EmissiveHex;
            MaterialEmissiveStrength.Value = (decimal)m.EmissiveStrength;
            BaseColorTexPath.Value = m.BaseColorTexture.Path ?? string.Empty;
            NormalTexPath.Value = m.NormalTexture.Path ?? string.Empty;
            OcclusionTexPath.Value = m.OcclusionTexture.Path ?? string.Empty;
            RoughnessTexPath.Value = m.RoughnessTexture.Path ?? string.Empty;
            MetallicTexPath.Value = m.MetallicTexture.Path ?? string.Empty;
            EmissiveTexPath.Value = m.EmissiveTexture.Path ?? string.Empty;
            BaseColorTexMode.Value = m.BaseColorTexture.SampleMode;
            EmissiveTexMode.Value = m.EmissiveTexture.SampleMode;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void SyncColliderInspector()
    {
        IsSyncing = true;
        try
        {
            CrossSectionColliderMaterialModel? cm = SelectedColliderMaterial.Value;
            if (cm is null)
            {
                HasSelectedColliderMaterial.Value = false;
                return;
            }

            HasSelectedColliderMaterial.Value = true;
            ColliderName.Value = cm.Name;
            ColliderFriction.Value = (decimal)cm.FrictionCoefficient;
            ColliderMaxRecovery.Value = (decimal)(float.MaxValue * 0.5f < cm.MaxRecoveryVelocity ? 999999m : (decimal)cm.MaxRecoveryVelocity);
            ColliderSpringFreq.Value = (decimal)cm.SpringFrequency;
            ColliderSpringDamping.Value = (decimal)cm.SpringDampingRatio;
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
