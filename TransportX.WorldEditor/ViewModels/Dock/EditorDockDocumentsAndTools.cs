using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dock.Model.Mvvm.Controls;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Services;

namespace TransportX.WorldEditor.ViewModels.Dock;

/// <summary>
/// Document ViewModel representing the primary 3D Direct3D 11 world viewport with tool, snap,
/// layer, and camera controls.
/// </summary>
public sealed class ViewportDocumentViewModel : EditorDocumentBase
{
    private bool IsSyncingFlags;

    public WorldWorkspaceService Workspace { get; }

    public ObservableCollection<string> AvailableModelKeys { get; } = new();
    public ObservableCollection<string> AvailableSplineTemplateKeys { get; } = new();
    public ObservableCollection<string> AvailableJunctionTemplateKeys { get; } = new();
    public ObservableCollection<CameraBookmarkModel> CameraBookmarks { get; } = new();

    public IReadOnlyList<double> GridSnapOptions { get; } = [0.0, 0.1, 0.5, 1.0, 5.0];
    public IReadOnlyList<double> AngleSnapOptions { get; } = [0.0, 5.0, 15.0, 45.0, 90.0];

    public BindableReactiveProperty<bool> ShowNormalLayer { get; }
    public BindableReactiveProperty<bool> ShowCollidersLayer { get; }
    public BindableReactiveProperty<bool> ShowNetworkLayer { get; }
    public BindableReactiveProperty<bool> ShowChunkGridLayer { get; }
    public BindableReactiveProperty<bool> ShowGizmosLayer { get; }
    public BindableReactiveProperty<bool> ShowAnchorsLayer { get; }
    public BindableReactiveProperty<bool> ShowTrafficLayer { get; }
    public BindableReactiveProperty<string> ActiveToolHint { get; }

    public BindableReactiveProperty<decimal?> JumpChunkX { get; }
    public BindableReactiveProperty<decimal?> JumpChunkZ { get; }
    public BindableReactiveProperty<int> SelectedBookmarkIndex { get; }
    public BindableReactiveProperty<string> NewBookmarkName { get; }
    public BindableReactiveProperty<bool> IsVerificationMode { get; }
    public BindableReactiveProperty<bool> IsVerificationStarting { get; }
    public BindableReactiveProperty<string> VerificationBannerText { get; }

    public BindableReactiveProperty<bool> IsSelectToolActive { get; }
    public BindableReactiveProperty<bool> IsTranslateToolActive { get; }
    public BindableReactiveProperty<bool> IsRotateToolActive { get; }
    public BindableReactiveProperty<bool> IsPlacePropToolActive { get; }
    public BindableReactiveProperty<bool> IsPlaceSplineToolActive { get; }
    public BindableReactiveProperty<bool> IsPlaceJunctionToolActive { get; }
    public BindableReactiveProperty<bool> IsAnyPlacementModeActive { get; }
    public BindableReactiveProperty<string> ActivePlacementTitle { get; }
    public BindableReactiveProperty<bool> IsConnectPortToolActive { get; }
    public BindableReactiveProperty<bool> CanTransformSelected { get; }
    public BindableReactiveProperty<string> TransformDisabledReason { get; }

    public ReactiveCommand<Unit> SelectToolCommand { get; }
    public ReactiveCommand<Unit> TranslateToolCommand { get; }
    public ReactiveCommand<Unit> RotateToolCommand { get; }
    public ReactiveCommand<Unit> PlacePropToolCommand { get; }
    public ReactiveCommand<Unit> PlaceSplineToolCommand { get; }
    public ReactiveCommand<Unit> PlaceJunctionToolCommand { get; }
    public ReactiveCommand<Unit> ConnectPortToolCommand { get; }
    public ReactiveCommand<Unit> PlacePropAtCameraCommand { get; }
    public ReactiveCommand<Unit> PlaceBackgroundCommand { get; }
    public ReactiveCommand<Unit> PlaceSplineAtCameraCommand { get; }
    public ReactiveCommand<Unit> PlaceJunctionAtCameraCommand { get; }
    public ReactiveCommand<Unit> FocusSelectedCommand { get; }
    public ReactiveCommand<Unit> JumpToChunkCommand { get; }
    public ReactiveCommand<Unit> AddBookmarkCommand { get; }
    public ReactiveCommand<Unit> JumpToBookmarkCommand { get; }
    public ReactiveCommand<Unit> RemoveBookmarkCommand { get; }
    public ReactiveCommand<Unit> StartVerificationCommand { get; }
    public ReactiveCommand<Unit> CancelVerificationStartCommand { get; }
    public ReactiveCommand<Unit> StopVerificationCommand { get; }

    public ViewportDocumentViewModel(WorldWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;

        ShowNormalLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ShowCollidersLayer = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ShowNetworkLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ShowChunkGridLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ShowGizmosLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ShowAnchorsLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ShowTrafficLayer = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        ActiveToolHint = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        JumpChunkX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JumpChunkZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SelectedBookmarkIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        NewBookmarkName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsVerificationMode = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsVerificationStarting = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        VerificationBannerText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        IsSelectToolActive = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsTranslateToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsRotateToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPlacePropToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPlaceSplineToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPlaceJunctionToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsAnyPlacementModeActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ActivePlacementTitle = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsConnectPortToolActive = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanTransformSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        TransformDisabledReason = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectToolCommand = new ReactiveCommand<Unit>(u => Workspace.ActiveToolMode.Value = EditorToolMode.Select).AddTo(Disposables);
        TranslateToolCommand = new ReactiveCommand<Unit>(u => ActivateTransformTool(EditorToolMode.Translate)).AddTo(Disposables);
        RotateToolCommand = new ReactiveCommand<Unit>(u => ActivateTransformTool(EditorToolMode.Rotate)).AddTo(Disposables);
        PlacePropToolCommand = new ReactiveCommand<Unit>(u => Workspace.StartPlacementMode(EditorToolMode.PlaceProp)).AddTo(Disposables);
        PlaceSplineToolCommand = new ReactiveCommand<Unit>(u => Workspace.StartPlacementMode(EditorToolMode.PlaceSpline)).AddTo(Disposables);
        PlaceJunctionToolCommand = new ReactiveCommand<Unit>(u => Workspace.StartPlacementMode(EditorToolMode.PlaceJunction)).AddTo(Disposables);
        ConnectPortToolCommand = new ReactiveCommand<Unit>(u => Workspace.ActiveToolMode.Value = EditorToolMode.ConnectPort).AddTo(Disposables);

        PlacePropAtCameraCommand = new ReactiveCommand<Unit>(u => PlacePropNearDefaultLocation()).AddTo(Disposables);
        PlaceBackgroundCommand = new ReactiveCommand<Unit>(u => PlaceBackgroundFromToolbar()).AddTo(Disposables);
        PlaceSplineAtCameraCommand = new ReactiveCommand<Unit>(u => PlaceSplineNearDefaultLocation()).AddTo(Disposables);
        PlaceJunctionAtCameraCommand = new ReactiveCommand<Unit>(u => PlaceJunctionNearDefaultLocation()).AddTo(Disposables);
        FocusSelectedCommand = new ReactiveCommand<Unit>(u => Workspace.RequestFocusSelectedOrDefault()).AddTo(Disposables);
        JumpToChunkCommand = new ReactiveCommand<Unit>(u =>
        {
            int cx = (int)(JumpChunkX.Value ?? 0m);
            int cz = (int)(JumpChunkZ.Value ?? 0m);
            Workspace.RequestJumpToChunk(cx, cz);
        }).AddTo(Disposables);
        AddBookmarkCommand = new ReactiveCommand<Unit>(u =>
        {
            if (Workspace.AddCameraBookmark(NewBookmarkName.Value))
            {
                NewBookmarkName.Value = string.Empty;
                if (0 < CameraBookmarks.Count)
                {
                    SelectedBookmarkIndex.Value = CameraBookmarks.Count - 1;
                }
            }
        }).AddTo(Disposables);
        JumpToBookmarkCommand = new ReactiveCommand<Unit>(u =>
        {
            Workspace.JumpToCameraBookmark(SelectedBookmarkIndex.Value);
        }).AddTo(Disposables);
        RemoveBookmarkCommand = new ReactiveCommand<Unit>(u =>
        {
            Workspace.RemoveCameraBookmark(SelectedBookmarkIndex.Value);
        }).AddTo(Disposables);
        StartVerificationCommand = new ReactiveCommand<Unit>(u =>
        {
            Workspace.RequestStartScriptVerification();
        }).AddTo(Disposables);
        CancelVerificationStartCommand = new ReactiveCommand<Unit>(u =>
        {
            Workspace.RequestCancelVerificationStart();
        }).AddTo(Disposables);
        StopVerificationCommand = new ReactiveCommand<Unit>(u =>
        {
            Workspace.RequestStopScriptVerification();
        }).AddTo(Disposables);

        Title = "3D ワールドビュー";
        BindDirtyTitle(
            Workspace,
            "3D ワールドビュー",
            (_, target) => target.Kind is WorldFileKind.WorldCsx or WorldFileKind.EditorCsx or WorldFileKind.EditorJson);

        ShowNormalLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowCollidersLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowNetworkLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowChunkGridLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowGizmosLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowAnchorsLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);
        ShowTrafficLayer.Skip(1).Subscribe(_ => PushOverlayFlags()).AddTo(Disposables);

        Workspace.OverlayFlags.Subscribe(_ => PullOverlayFlags()).AddTo(Disposables);
        Workspace.ActiveToolMode.Subscribe(_ =>
        {
            SyncToolModeStates();
            UpdateActiveToolHint();
        }).AddTo(Disposables);
        Workspace.PendingConnectSourcePort.Subscribe(_ => UpdateActiveToolHint()).AddTo(Disposables);
        Workspace.SelectedPropId.Subscribe(_ => UpdateTransformAvailability()).AddTo(Disposables);
        Workspace.SelectedSplineId.Subscribe(_ => UpdateTransformAvailability()).AddTo(Disposables);
        Workspace.SelectedJunctionId.Subscribe(_ => UpdateTransformAvailability()).AddTo(Disposables);
        Workspace.SelectedBackgroundModelIndex.Subscribe(_ => UpdateTransformAvailability()).AddTo(Disposables);
        Workspace.ActiveRenderMode.Subscribe(mode =>
        {
            IsVerificationMode.Value = mode == EditorRenderMode.ScriptVerification;
            UpdateTransformAvailability();
            UpdateActiveToolHint();
        }).AddTo(Disposables);
        Workspace.IsVerificationStarting.Subscribe(starting =>
        {
            IsVerificationStarting.Value = starting;
        }).AddTo(Disposables);
        Workspace.VerificationSummaryText.Subscribe(summary =>
        {
            VerificationBannerText.Value = summary;
        }).AddTo(Disposables);
        Workspace.ProjectRevision.Subscribe(_ =>
        {
            RebuildCatalogKeys();
            UpdateTransformAvailability();
        }).AddTo(Disposables);
        RebuildCatalogKeys();
        SyncToolModeStates();
        UpdateTransformAvailability();
        UpdateActiveToolHint();
    }

    private void ActivateTransformTool(EditorToolMode mode)
    {
        UpdateTransformAvailability();
        if (!CanTransformSelected.Value)
        {
            Workspace.LastStatusMessage.Value = TransformDisabledReason.Value;
            SyncToolModeStates();
            return;
        }

        Workspace.ActiveToolMode.Value = mode;
    }

    private void SyncToolModeStates()
    {
        EditorToolMode mode = Workspace.ActiveToolMode.Value;
        IsSelectToolActive.Value = mode == EditorToolMode.Select;
        IsTranslateToolActive.Value = mode == EditorToolMode.Translate;
        IsRotateToolActive.Value = mode == EditorToolMode.Rotate;
        IsPlacePropToolActive.Value = mode == EditorToolMode.PlaceProp;
        IsPlaceSplineToolActive.Value = mode == EditorToolMode.PlaceSpline;
        IsPlaceJunctionToolActive.Value = mode == EditorToolMode.PlaceJunction;
        IsAnyPlacementModeActive.Value = mode == EditorToolMode.PlaceProp
            || mode == EditorToolMode.PlaceSpline
            || mode == EditorToolMode.PlaceJunction;
        ActivePlacementTitle.Value = mode switch
        {
            EditorToolMode.PlaceProp => "プロップ配置モード",
            EditorToolMode.PlaceSpline => "スプライン配置モード",
            EditorToolMode.PlaceJunction => "ジャンクション配置モード",
            _ => string.Empty,
        };
        IsConnectPortToolActive.Value = mode == EditorToolMode.ConnectPort;
    }

    private void UpdateTransformAvailability()
    {
        (bool canTransform, string reason, _) = EvaluateTransformAvailability();
        CanTransformSelected.Value = canTransform;
        TransformDisabledReason.Value = reason;

        if (!canTransform
            && (Workspace.ActiveToolMode.Value == EditorToolMode.Translate
                || Workspace.ActiveToolMode.Value == EditorToolMode.Rotate))
        {
            Workspace.ActiveToolMode.Value = EditorToolMode.Select;
            Workspace.LastStatusMessage.Value = reason;
        }

        UpdateActiveToolHint();
    }

    private (bool CanTransform, string Reason, bool IsConnectedSelected) EvaluateTransformAvailability()
    {
        if (Workspace.ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
        {
            return (false, "スクリプト検証モード中は移動・回転ツールを使用できません。", false);
        }

        string? propId = Workspace.SelectedPropId.Value;
        if (!string.IsNullOrEmpty(propId) && Workspace.FindProp(propId) is not null)
        {
            return (true, "選択中のプロップを 3D ギズモで移動・回転します。", false);
        }

        string? splineId = Workspace.SelectedSplineId.Value;
        if (!string.IsNullOrEmpty(splineId) && Workspace.FindSpline(splineId) is SplinePlacementNode spline)
        {
            if (spline.Origin.Kind == SplineOriginKind.ChunkPose)
            {
                return (true, "選択中の独立スプライン起点 (ChunkPose) を 3D ギズモで移動・回転します。", false);
            }

            return (false, "連結済みスプラインは接続元ポートに拘束されているため移動・回転できません。", true);
        }

        string? junctionId = Workspace.SelectedJunctionId.Value;
        if (!string.IsNullOrEmpty(junctionId) && Workspace.FindJunction(junctionId) is JunctionPlacementNode junction)
        {
            if (junction.Origin.Kind == JunctionOriginKind.ChunkPose)
            {
                return (true, "選択中の独立ジャンクション (ChunkPose) を 3D ギズモで移動・回転します。", false);
            }

            return (false, "連結済みジャンクションは接続元ポートに拘束されているため移動・回転できません。", true);
        }

        if (0 <= Workspace.SelectedBackgroundModelIndex.Value)
        {
            return (false, "背景モデルは移動・回転できません。", false);
        }

        return (false, "移動・回転する要素 (プロップ、または独立配置のスプライン / ジャンクション) を選択してください。", false);
    }

    private void UpdateActiveToolHint()
    {
        if (Workspace.ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
        {
            ActiveToolHint.Value = "スクリプト検証モード: 右ドラッグ+WASD/QE: 視点移動 | 中ドラッグ: パン | ホイール: ズーム | Esc: 編集モードへ戻る";
            return;
        }

        EditorToolMode mode = Workspace.ActiveToolMode.Value;
        PortReferenceModel? pending = Workspace.PendingConnectSourcePort.Value;
        (_, _, bool isConnectedSelected) = EvaluateTransformAvailability();
        ActiveToolHint.Value = mode switch
        {
            EditorToolMode.Translate =>
                "モード: 移動 (3D ギズモの軸矢印をドラッグして位置を変更 / Escで選択モードへ戻る)",
            EditorToolMode.Rotate =>
                "モード: 回転 (3D ギズモの Y軸リングをドラッグして回転を変更 / Escで選択モードへ戻る)",
            EditorToolMode.PlaceProp =>
                "モード: プロップ配置 (地面左クリックで配置 / Escで選択モードへ戻る)",
            EditorToolMode.PlaceSpline =>
                "モード: スプライン配置 (地面クリックで新規配置 / 自由Outlet・ジャンクションポートクリックで連結 / Escで解除)",
            EditorToolMode.PlaceJunction =>
                "モード: ジャンクション配置 (地面クリックで新規配置 / 自由Outlet・互換ポートクリックで連結 / Escで解除)",
            EditorToolMode.ConnectPort => pending is null
                ? "モード: ポート接続 (1回目: 接続元の自由 Outlet / ジャンクションポートをクリック / Escで解除)"
                : $"モード: ポート接続 (2回目: '{pending.ElementId}' と互換性のある接続先ポートをクリック / Escで解除)",
            _ => isConnectedSelected
                ? "右ドラッグ+WASD/QE: 視点移動 | 連結済み要素は接続元に追従するため移動・回転不可"
                : "右ドラッグ+WASD/QE: 視点移動 | 中ドラッグ: パン | ホイール: ズーム | 左クリック: 選択/ギズモ | Del: 削除",
        };
    }

    private void PullOverlayFlags()
    {
        if (IsSyncingFlags)
        {
            return;
        }

        IsSyncingFlags = true;
        try
        {
            ViewportOverlayFlags flags = Workspace.OverlayFlags.Value;
            ShowNormalLayer.Value = flags.HasFlag(ViewportOverlayFlags.Normal);
            ShowCollidersLayer.Value = flags.HasFlag(ViewportOverlayFlags.Colliders);
            ShowNetworkLayer.Value = flags.HasFlag(ViewportOverlayFlags.Network);
            ShowChunkGridLayer.Value = flags.HasFlag(ViewportOverlayFlags.ChunkGrid);
            ShowGizmosLayer.Value = flags.HasFlag(ViewportOverlayFlags.Gizmos);
            ShowAnchorsLayer.Value = flags.HasFlag(ViewportOverlayFlags.Anchors);
            ShowTrafficLayer.Value = flags.HasFlag(ViewportOverlayFlags.Traffic);
        }
        finally
        {
            IsSyncingFlags = false;
        }
    }

    private void PushOverlayFlags()
    {
        if (IsSyncingFlags)
        {
            return;
        }

        IsSyncingFlags = true;
        try
        {
            ViewportOverlayFlags flags = ViewportOverlayFlags.None;
            if (ShowNormalLayer.Value)
            {
                flags |= ViewportOverlayFlags.Normal;
            }

            if (ShowCollidersLayer.Value)
            {
                flags |= ViewportOverlayFlags.Colliders;
            }

            if (ShowNetworkLayer.Value)
            {
                flags |= ViewportOverlayFlags.Network;
            }

            if (ShowChunkGridLayer.Value)
            {
                flags |= ViewportOverlayFlags.ChunkGrid;
            }

            if (ShowGizmosLayer.Value)
            {
                flags |= ViewportOverlayFlags.Gizmos;
            }

            if (ShowAnchorsLayer.Value)
            {
                flags |= ViewportOverlayFlags.Anchors;
            }

            if (ShowTrafficLayer.Value)
            {
                flags |= ViewportOverlayFlags.Traffic;
            }

            Workspace.OverlayFlags.Value = flags;
        }
        finally
        {
            IsSyncingFlags = false;
        }
    }

    private static void SyncStringCollection(ObservableCollection<string> target, IList<string> next)
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

    private void RebuildCatalogKeys()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            AvailableModelKeys.Clear();
            AvailableSplineTemplateKeys.Clear();
            AvailableJunctionTemplateKeys.Clear();
            CameraBookmarks.Clear();
            SelectedBookmarkIndex.Value = -1;
            return;
        }

        SceneElementCreationHelper.CollectCatalogKeys(
            project,
            out List<string> nextModelKeys,
            out List<string> nextTemplateKeys,
            out List<string> nextJunctionKeys);

        SyncStringCollection(AvailableModelKeys, nextModelKeys);
        SyncStringCollection(AvailableSplineTemplateKeys, nextTemplateKeys);
        SyncStringCollection(AvailableJunctionTemplateKeys, nextJunctionKeys);

        if (string.IsNullOrWhiteSpace(Workspace.PlacementModelKey.Value) && 0 < AvailableModelKeys.Count)
        {
            Workspace.PlacementModelKey.Value = AvailableModelKeys[0];
        }

        if (string.IsNullOrWhiteSpace(Workspace.PlacementSplineTemplateKey.Value) && 0 < AvailableSplineTemplateKeys.Count)
        {
            Workspace.PlacementSplineTemplateKey.Value = AvailableSplineTemplateKeys[0];
        }

        if (string.IsNullOrWhiteSpace(Workspace.PlacementJunctionTemplateKey.Value) && 0 < AvailableJunctionTemplateKeys.Count)
        {
            Workspace.PlacementJunctionTemplateKey.Value = AvailableJunctionTemplateKeys[0];
        }

        SyncCameraBookmarks(project.Sidecar.CameraBookmarks);
    }

    private void SyncCameraBookmarks(IReadOnlyList<CameraBookmarkModel> next)
    {
        if (!CameraBookmarks.SequenceEqual(next))
        {
            int previousIndex = SelectedBookmarkIndex.Value;
            CameraBookmarks.Clear();
            foreach (CameraBookmarkModel bookmark in next)
            {
                CameraBookmarks.Add(bookmark);
            }

            if (CameraBookmarks.Count == 0)
            {
                SelectedBookmarkIndex.Value = -1;
            }
            else if (previousIndex < 0)
            {
                SelectedBookmarkIndex.Value = 0;
            }
            else if (CameraBookmarks.Count <= previousIndex)
            {
                SelectedBookmarkIndex.Value = CameraBookmarks.Count - 1;
            }
            else
            {
                SelectedBookmarkIndex.Value = previousIndex;
            }
        }
    }

    private void PlaceSplineNearDefaultLocation()
    {
        SceneElementCreationHelper.PlaceDefaultSpline(
            Workspace,
            (int)(JumpChunkX.Value ?? 0m),
            (int)(JumpChunkZ.Value ?? 0m),
            AvailableSplineTemplateKeys);
    }

    private void PlaceJunctionNearDefaultLocation()
    {
        SceneElementCreationHelper.PlaceDefaultJunction(
            Workspace,
            (int)(JumpChunkX.Value ?? 0m),
            (int)(JumpChunkZ.Value ?? 0m),
            AvailableJunctionTemplateKeys,
            requireExplicitTemplate: true);
    }

    private void PlacePropNearDefaultLocation()
    {
        SceneElementCreationHelper.PlaceDefaultProp(
            Workspace,
            (int)(JumpChunkX.Value ?? 0m),
            (int)(JumpChunkZ.Value ?? 0m),
            AvailableModelKeys,
            allowPlaceholderFallback: false);
    }

    private void PlaceBackgroundFromToolbar()
    {
        SceneElementCreationHelper.PlaceDefaultBackground(
            Workspace,
            AvailableModelKeys,
            allowPlaceholderFallback: false);
    }
}

internal static class SceneElementCreationHelper
{
    public static void CollectCatalogKeys(
        WorldProject project,
        out List<string> modelKeys,
        out List<string> splineKeys,
        out List<string> junctionKeys)
    {
        modelKeys = new List<string>();
        foreach (ModelListDocument list in project.ModelLists.Values)
        {
            foreach (ModelListEntry entry in list.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && !modelKeys.Contains(entry.Key))
                {
                    modelKeys.Add(entry.Key);
                }
            }
        }

        splineKeys = project.SplineTemplates.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        junctionKeys = project.JunctionTemplates.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void PlaceDefaultProp(
        WorldWorkspaceService workspace,
        int chunkX,
        int chunkZ,
        IReadOnlyList<string> availableModelKeys,
        bool allowPlaceholderFallback)
    {
        WorldProject? project = workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string key = workspace.PlacementModelKey.Value;
        if (string.IsNullOrWhiteSpace(key) && 0 < availableModelKeys.Count)
        {
            key = availableModelKeys[0];
            workspace.PlacementModelKey.Value = key;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            if (!allowPlaceholderFallback)
            {
                workspace.LastStatusMessage.Value = "配置できるモデルキーがありません (先に Models.txt へモデルを登録してください)。";
                return;
            }

            key = "DefaultProp";
        }

        WorldPoseDegreesModel pose = new(
            new ChunkIndexModel(chunkX, chunkZ),
            PoseDegreesModel.FromTranslation(125.0, 0.0, 125.0));
        workspace.AddPropPlacement(key, pose);
    }

    public static void PlaceDefaultBackground(
        WorldWorkspaceService workspace,
        IReadOnlyList<string> availableModelKeys,
        bool allowPlaceholderFallback)
    {
        WorldProject? project = workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string key = workspace.PlacementModelKey.Value;
        if (string.IsNullOrWhiteSpace(key) && 0 < availableModelKeys.Count)
        {
            key = availableModelKeys.FirstOrDefault(static k => string.Equals(k, "Background", StringComparison.OrdinalIgnoreCase))
                ?? availableModelKeys[0];
            workspace.PlacementModelKey.Value = key;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            if (!allowPlaceholderFallback)
            {
                workspace.LastStatusMessage.Value = "背景に配置できるモデルキーがありません (先に Models.txt へモデルを登録してください)。";
                return;
            }

            key = "Background";
        }

        workspace.AddBackgroundModel(key);
    }

    public static void PlaceDefaultSpline(
        WorldWorkspaceService workspace,
        int chunkX,
        int chunkZ,
        IReadOnlyList<string> availableSplineKeys)
    {
        WorldProject? project = workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string templateKey = workspace.PlacementSplineTemplateKey.Value;
        if (string.IsNullOrWhiteSpace(templateKey) && 0 < availableSplineKeys.Count)
        {
            templateKey = availableSplineKeys[0];
            workspace.PlacementSplineTemplateKey.Value = templateKey;
        }

        WorldPoseDegreesModel pose = new(
            new ChunkIndexModel(chunkX, chunkZ),
            PoseDegreesModel.FromTranslation(125.0, 0.0, 125.0));
        workspace.AddSplineAtChunkPose(templateKey, pose);
    }

    public static void PlaceDefaultJunction(
        WorldWorkspaceService workspace,
        int chunkX,
        int chunkZ,
        IReadOnlyList<string> availableJunctionKeys,
        bool requireExplicitTemplate)
    {
        WorldProject? project = workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string templateKey = workspace.PlacementJunctionTemplateKey.Value;
        if (string.IsNullOrWhiteSpace(templateKey) && 0 < availableJunctionKeys.Count)
        {
            templateKey = availableJunctionKeys[0];
            workspace.PlacementJunctionTemplateKey.Value = templateKey;
        }

        if (requireExplicitTemplate && string.IsNullOrWhiteSpace(templateKey))
        {
            workspace.LastStatusMessage.Value = "配置できる JunctionTemplate がありません (先にジャンクションテンプレートを作成してください)。";
            return;
        }

        WorldPoseDegreesModel pose = new(
            new ChunkIndexModel(chunkX, chunkZ),
            PoseDegreesModel.FromTranslation(125.0, 0.0, 125.0));
        workspace.AddJunctionAtChunkPose(templateKey, pose);
    }
}

/// <summary>
/// Filter category tab in <see cref="SceneHierarchyToolViewModel"/>.
/// </summary>
public enum SceneHierarchyFilterCategory
{
    All = 0,
    Environment = 1,
    Props = 2,
    Network = 3,
}

/// <summary>
/// View-only sort mode in <see cref="SceneHierarchyToolViewModel"/> (does not mutate CSX declaration order).
/// </summary>
public enum SceneHierarchySortMode
{
    Default = 0,
    Name = 1,
    Chunk = 2,
}

/// <summary>
/// Tree/list entry inside <see cref="SceneHierarchyToolViewModel"/>.
/// </summary>
public sealed record SceneItemEntry(
    string Category,
    string Label,
    string SubLabel,
    string? PropId = null,
    string? SplineId = null,
    int BackgroundIndex = -1,
    string? JunctionId = null,
    SceneHierarchyFilterCategory FilterCategory = SceneHierarchyFilterCategory.Environment,
    bool HasChunkCoordinate = false,
    int ChunkX = 0,
    int ChunkZ = 0,
    int DefaultOrder = 0);

/// <summary>
/// Tool ViewModel displaying world scene elements with category tabs, view sorting, text filtering,
/// and two-way Prop/Spline/Junction selection sync.
/// </summary>
public sealed class SceneHierarchyToolViewModel : Tool, IDisposable
{
    private readonly WorldWorkspaceService Workspace;
    private readonly CompositeDisposable Disposables = new();
    private readonly List<SceneItemEntry> AllEntries = new();
    private bool IsSyncingSelection;

    public BindableReactiveProperty<string> FilterText { get; }
    public BindableReactiveProperty<SceneHierarchyFilterCategory> SelectedFilterCategory { get; }
    public BindableReactiveProperty<bool> IsFilterAll { get; }
    public BindableReactiveProperty<bool> IsFilterEnvironment { get; }
    public BindableReactiveProperty<bool> IsFilterProps { get; }
    public BindableReactiveProperty<bool> IsFilterNetwork { get; }
    public BindableReactiveProperty<int> SelectedSortModeIndex { get; }
    public IReadOnlyList<string> SortModeLabels { get; } = ["既定 (CSX順)", "名前順", "チャンク順"];
    public BindableReactiveProperty<SceneItemEntry?> SelectedItem { get; }
    public BindableReactiveProperty<bool> HasOpenProject { get; }
    public ObservableCollection<SceneItemEntry> Items { get; } = new();

    public ReactiveCommand<Unit> SelectFilterAllCommand { get; }
    public ReactiveCommand<Unit> SelectFilterEnvironmentCommand { get; }
    public ReactiveCommand<Unit> SelectFilterPropsCommand { get; }
    public ReactiveCommand<Unit> SelectFilterNetworkCommand { get; }
    public ReactiveCommand<Unit> AddNewPropCommand { get; }
    public ReactiveCommand<Unit> AddNewBackgroundCommand { get; }
    public ReactiveCommand<Unit> AddNewSplineCommand { get; }
    public ReactiveCommand<Unit> AddNewJunctionCommand { get; }

    public SceneHierarchyToolViewModel(WorldWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;

        FilterText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedFilterCategory = new BindableReactiveProperty<SceneHierarchyFilterCategory>(SceneHierarchyFilterCategory.All).AddTo(Disposables);
        IsFilterAll = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsFilterEnvironment = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsFilterProps = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsFilterNetwork = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedSortModeIndex = new BindableReactiveProperty<int>((int)SceneHierarchySortMode.Default).AddTo(Disposables);
        SelectedItem = new BindableReactiveProperty<SceneItemEntry?>(null).AddTo(Disposables);
        HasOpenProject = new BindableReactiveProperty<bool>(workspace.CurrentProject.Value is not null).AddTo(Disposables);

        SelectFilterAllCommand = new ReactiveCommand<Unit>(_ => SetFilterCategory(SceneHierarchyFilterCategory.All)).AddTo(Disposables);
        SelectFilterEnvironmentCommand = new ReactiveCommand<Unit>(_ => SetFilterCategory(SceneHierarchyFilterCategory.Environment)).AddTo(Disposables);
        SelectFilterPropsCommand = new ReactiveCommand<Unit>(_ => SetFilterCategory(SceneHierarchyFilterCategory.Props)).AddTo(Disposables);
        SelectFilterNetworkCommand = new ReactiveCommand<Unit>(_ => SetFilterCategory(SceneHierarchyFilterCategory.Network)).AddTo(Disposables);
        AddNewPropCommand = new ReactiveCommand<Unit>(_ => Workspace.StartPlacementMode(EditorToolMode.PlaceProp)).AddTo(Disposables);
        AddNewBackgroundCommand = new ReactiveCommand<Unit>(_ => CreateBackground()).AddTo(Disposables);
        AddNewSplineCommand = new ReactiveCommand<Unit>(_ => Workspace.StartPlacementMode(EditorToolMode.PlaceSpline)).AddTo(Disposables);
        AddNewJunctionCommand = new ReactiveCommand<Unit>(_ => Workspace.StartPlacementMode(EditorToolMode.PlaceJunction)).AddTo(Disposables);

        FilterText.Subscribe(_ => ApplyFilter()).AddTo(Disposables);
        SelectedFilterCategory.Skip(1).Subscribe(cat =>
        {
            SyncFilterToggleStates(cat);
            ApplyFilter();
        }).AddTo(Disposables);
        SelectedSortModeIndex.Skip(1).Subscribe(_ => ApplyFilter()).AddTo(Disposables);
        SelectedItem.Skip(1).Subscribe(entry => OnSelectedItemChanged(entry)).AddTo(Disposables);
        Workspace.SelectedPropId.Subscribe(_ => SyncSelectionFromWorkspace()).AddTo(Disposables);
        Workspace.SelectedSplineId.Subscribe(_ => SyncSelectionFromWorkspace()).AddTo(Disposables);
        Workspace.SelectedJunctionId.Subscribe(_ => SyncSelectionFromWorkspace()).AddTo(Disposables);
        Workspace.SelectedBackgroundModelIndex.Subscribe(_ => SyncSelectionFromWorkspace()).AddTo(Disposables);
        Workspace.ProjectRevision.Subscribe(_ =>
        {
            HasOpenProject.Value = Workspace.CurrentProject.Value is not null;
            Rebuild();
        }).AddTo(Disposables);
        Rebuild();
    }

    public void SetFilterCategory(SceneHierarchyFilterCategory category)
    {
        if (SelectedFilterCategory.Value != category)
        {
            SelectedFilterCategory.Value = category;
        }
        else
        {
            SyncFilterToggleStates(category);
        }
    }

    public void SetSortMode(SceneHierarchySortMode sortMode)
    {
        SelectedSortModeIndex.Value = (int)sortMode;
    }

    private void SyncFilterToggleStates(SceneHierarchyFilterCategory category)
    {
        IsFilterAll.Value = category == SceneHierarchyFilterCategory.All;
        IsFilterEnvironment.Value = category == SceneHierarchyFilterCategory.Environment;
        IsFilterProps.Value = category == SceneHierarchyFilterCategory.Props;
        IsFilterNetwork.Value = category == SceneHierarchyFilterCategory.Network;
    }

    private void CreateBackground()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        SceneElementCreationHelper.CollectCatalogKeys(project, out List<string> modelKeys, out _, out _);
        SceneElementCreationHelper.PlaceDefaultBackground(Workspace, modelKeys, allowPlaceholderFallback: true);
    }

    public void FocusSelected()
    {
        SceneItemEntry? entry = SelectedItem.Value;
        if (entry is not null)
        {
            if (!string.IsNullOrEmpty(entry.PropId))
            {
                Workspace.SelectProp(entry.PropId);
            }
            else if (!string.IsNullOrEmpty(entry.SplineId))
            {
                Workspace.SelectSpline(entry.SplineId);
            }
            else if (!string.IsNullOrEmpty(entry.JunctionId))
            {
                Workspace.SelectJunction(entry.JunctionId);
            }
        }

        Workspace.RequestFocusSelectedOrDefault();
    }

    private void OnSelectedItemChanged(SceneItemEntry? entry)
    {
        if (IsSyncingSelection)
        {
            return;
        }

        IsSyncingSelection = true;
        try
        {
            if (entry is not null)
            {
                if (!string.IsNullOrEmpty(entry.PropId))
                {
                    Workspace.SelectProp(entry.PropId);
                }
                else if (!string.IsNullOrEmpty(entry.SplineId))
                {
                    Workspace.SelectSpline(entry.SplineId);
                }
                else if (!string.IsNullOrEmpty(entry.JunctionId))
                {
                    Workspace.SelectJunction(entry.JunctionId);
                }
                else if (0 <= entry.BackgroundIndex)
                {
                    Workspace.SelectBackgroundModel(entry.BackgroundIndex);
                }
            }
        }
        finally
        {
            IsSyncingSelection = false;
        }
    }

    private void SyncSelectionFromWorkspace()
    {
        if (IsSyncingSelection)
        {
            return;
        }

        IsSyncingSelection = true;
        try
        {
            string? propId = Workspace.SelectedPropId.Value;
            if (!string.IsNullOrEmpty(propId))
            {
                SelectedItem.Value = Items.FirstOrDefault(i => string.Equals(i.PropId, propId, StringComparison.Ordinal));
                return;
            }

            string? splineId = Workspace.SelectedSplineId.Value;
            if (!string.IsNullOrEmpty(splineId))
            {
                SelectedItem.Value = Items.FirstOrDefault(i => string.Equals(i.SplineId, splineId, StringComparison.Ordinal));
                return;
            }

            string? junctionId = Workspace.SelectedJunctionId.Value;
            if (!string.IsNullOrEmpty(junctionId))
            {
                SelectedItem.Value = Items.FirstOrDefault(i => string.Equals(i.JunctionId, junctionId, StringComparison.Ordinal));
                return;
            }

            int bgIndex = Workspace.SelectedBackgroundModelIndex.Value;
            if (0 <= bgIndex)
            {
                SelectedItem.Value = Items.FirstOrDefault(i => i.BackgroundIndex == bgIndex);
                return;
            }

            SelectedItem.Value = null;
        }
        finally
        {
            IsSyncingSelection = false;
        }
    }

    private void Rebuild()
    {
        AllEntries.Clear();
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            ApplyFilter();
            return;
        }

        WorldPlacementDocument p = project.Placement;
        int defaultOrder = 0;
        AllEntries.Add(new SceneItemEntry(
            "Camera",
            "初期カメラ",
            $"Chunk({p.Camera.Pose.Chunk.X},{p.Camera.Pose.Chunk.Z}) ({p.Camera.Pose.LocalPose.X:0.#}, {p.Camera.Pose.LocalPose.Y:0.#}, {p.Camera.Pose.LocalPose.Z:0.#})",
            FilterCategory: SceneHierarchyFilterCategory.Environment,
            DefaultOrder: defaultOrder++));

        if (p.Avatar is not null)
        {
            AllEntries.Add(new SceneItemEntry(
                "Avatar",
                "アバター",
                p.Avatar.XmlPath,
                FilterCategory: SceneHierarchyFilterCategory.Environment,
                DefaultOrder: defaultOrder++));
        }

        if (!string.IsNullOrEmpty(p.EnvironmentPath))
        {
            AllEntries.Add(new SceneItemEntry(
                "Env",
                "環境プロファイル",
                p.EnvironmentPath,
                FilterCategory: SceneHierarchyFilterCategory.Environment,
                DefaultOrder: defaultOrder++));
        }

        for (int i = 0; i < p.BackgroundModels.Count; i++)
        {
            string bgKey = p.BackgroundModels[i];
            AllEntries.Add(new SceneItemEntry(
                "背景",
                bgKey,
                $"Background.Add(\"{bgKey}\") [#{i}]",
                BackgroundIndex: i,
                FilterCategory: SceneHierarchyFilterCategory.Environment,
                DefaultOrder: defaultOrder++));
        }

        foreach (ChunkPropPlacement prop in p.Props)
        {
            string? propDisplayName = string.IsNullOrWhiteSpace(prop.DisplayName) ? null : prop.DisplayName;
            string propLabel = propDisplayName ?? prop.ModelKey;
            string propModelPrefix = propDisplayName is null ? string.Empty : $"{prop.ModelKey} | ";
            AllEntries.Add(new SceneItemEntry(
                "Prop",
                propLabel,
                $"{propModelPrefix}Chunk({prop.Chunk.X},{prop.Chunk.Z}) ({prop.Pose.X:0.#}, {prop.Pose.Y:0.#}, {prop.Pose.Z:0.#}) [{prop.Id}]",
                PropId: prop.Id,
                FilterCategory: SceneHierarchyFilterCategory.Props,
                HasChunkCoordinate: true,
                ChunkX: prop.Chunk.X,
                ChunkZ: prop.Chunk.Z,
                DefaultOrder: defaultOrder++));
        }

        Dictionary<string, SplinePlacementNode> splineById = new(StringComparer.Ordinal);
        foreach (SplinePlacementNode spline in p.Splines)
        {
            splineById[spline.Id] = spline;
        }

        Dictionary<string, JunctionPlacementNode> junctionById = new(StringComparer.Ordinal);
        foreach (JunctionPlacementNode junction in p.Junctions)
        {
            junctionById[junction.Id] = junction;
        }

        NetworkTopologicalSortResult sort = NetworkTopologicalSorter.Sort(p);
        List<NetworkNodeRef> orderedNetwork = new(sort.OrderedNodes);
        HashSet<string> emittedIds = new(StringComparer.Ordinal);
        foreach (NetworkNodeRef nodeRef in orderedNetwork)
        {
            emittedIds.Add(nodeRef.Id);
        }

        foreach (SplinePlacementNode s in p.Splines)
        {
            if (emittedIds.Add(s.Id))
            {
                orderedNetwork.Add(new NetworkNodeRef(false, s.Id));
            }
        }

        foreach (JunctionPlacementNode j in p.Junctions)
        {
            if (emittedIds.Add(j.Id))
            {
                orderedNetwork.Add(new NetworkNodeRef(true, j.Id));
            }
        }

        foreach (NetworkNodeRef nodeRef in orderedNetwork)
        {
            if (!nodeRef.IsJunction && splineById.TryGetValue(nodeRef.Id, out SplinePlacementNode? spline))
            {
                double len = SplineAnchorUtilities.ComputeCurveTotalLength(spline);
                string originText = spline.Origin.Kind switch
                {
                    SplineOriginKind.ChunkPose => $"Chunk({spline.Origin.Chunk.X},{spline.Origin.Chunk.Z})",
                    SplineOriginKind.FromSplineOutlet => $"IntoSpline({spline.Origin.SourceNodeId})",
                    SplineOriginKind.FromJunctionPort => $"FromJunction({spline.Origin.SourceNodeId}.{spline.Origin.SourcePortKey})",
                    _ => spline.Origin.Kind.ToString(),
                };
                string connText = spline.ConnectBezier is null
                    ? string.Empty
                    : $" -> Bezier({spline.ConnectBezier.TargetPort.ElementId})";
                string splineKey = string.IsNullOrEmpty(spline.Key) ? spline.Id : spline.Key;
                string? splineDisplayName = Workspace.GetElementDisplayName(spline.Id);
                (bool hasChunk, ChunkIndexModel chunk) = TryResolveSplineChunk(spline, splineById, junctionById);
                AllEntries.Add(new SceneItemEntry(
                    "Spline",
                    string.IsNullOrEmpty(splineDisplayName) ? splineKey : splineDisplayName,
                    $"Key:{splineKey} | Tpl:{spline.TemplateKey ?? "(なし)"} | {originText} | L={len:0.#}m{connText}",
                    SplineId: spline.Id,
                    FilterCategory: SceneHierarchyFilterCategory.Network,
                    HasChunkCoordinate: hasChunk,
                    ChunkX: chunk.X,
                    ChunkZ: chunk.Z,
                    DefaultOrder: defaultOrder++));
            }
            else if (nodeRef.IsJunction && junctionById.TryGetValue(nodeRef.Id, out JunctionPlacementNode? junction))
            {
                string originText = junction.Origin.Kind switch
                {
                    JunctionOriginKind.ChunkPose => $"Chunk({junction.Origin.Chunk.X},{junction.Origin.Chunk.Z})",
                    JunctionOriginKind.FromSplineOutlet => $"IntoJunction({junction.Origin.SourceNodeId}->{junction.Origin.TargetPortKey})",
                    JunctionOriginKind.FromJunctionPort => $"FromJunction({junction.Origin.SourceNodeId}.{junction.Origin.SourcePortKey}->{junction.Origin.TargetPortKey})",
                    _ => junction.Origin.Kind.ToString(),
                };
                string junctionKey = string.IsNullOrEmpty(junction.Key) ? junction.Id : junction.Key;
                string? junctionDisplayName = Workspace.GetElementDisplayName(junction.Id);
                (bool hasChunk, ChunkIndexModel chunk) = TryResolveJunctionChunk(junction, splineById, junctionById);
                AllEntries.Add(new SceneItemEntry(
                    "Junction",
                    string.IsNullOrEmpty(junctionDisplayName) ? junctionKey : junctionDisplayName,
                    $"Key:{junctionKey} | Tpl:{junction.TemplateKey} | {originText}",
                    JunctionId: junction.Id,
                    FilterCategory: SceneHierarchyFilterCategory.Network,
                    HasChunkCoordinate: hasChunk,
                    ChunkX: chunk.X,
                    ChunkZ: chunk.Z,
                    DefaultOrder: defaultOrder++));
            }
        }

        ApplyFilter();
    }

    private static (bool HasChunk, ChunkIndexModel Chunk) TryResolveSplineChunk(
        SplinePlacementNode spline,
        IReadOnlyDictionary<string, SplinePlacementNode> splineById,
        IReadOnlyDictionary<string, JunctionPlacementNode> junctionById)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        return TryResolveNodeChunk(isJunction: false, spline.Id, splineById, junctionById, visited);
    }

    private static (bool HasChunk, ChunkIndexModel Chunk) TryResolveJunctionChunk(
        JunctionPlacementNode junction,
        IReadOnlyDictionary<string, SplinePlacementNode> splineById,
        IReadOnlyDictionary<string, JunctionPlacementNode> junctionById)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        return TryResolveNodeChunk(isJunction: true, junction.Id, splineById, junctionById, visited);
    }

    private static (bool HasChunk, ChunkIndexModel Chunk) TryResolveNodeChunk(
        bool isJunction,
        string nodeId,
        IReadOnlyDictionary<string, SplinePlacementNode> splineById,
        IReadOnlyDictionary<string, JunctionPlacementNode> junctionById,
        HashSet<string> visited)
    {
        string visitKey = $"{(isJunction ? 'J' : 'S')}:{nodeId}";
        if (string.IsNullOrEmpty(nodeId) || !visited.Add(visitKey))
        {
            return (false, ChunkIndexModel.Zero);
        }

        if (!isJunction && splineById.TryGetValue(nodeId, out SplinePlacementNode? spline))
        {
            if (spline.Origin.Kind == SplineOriginKind.ChunkPose)
            {
                return (true, spline.Origin.Chunk);
            }

            if (!string.IsNullOrEmpty(spline.Origin.SourceNodeId))
            {
                return TryResolveNodeChunk(
                    spline.Origin.Kind == SplineOriginKind.FromJunctionPort,
                    spline.Origin.SourceNodeId,
                    splineById,
                    junctionById,
                    visited);
            }

            return (false, ChunkIndexModel.Zero);
        }

        if (isJunction && junctionById.TryGetValue(nodeId, out JunctionPlacementNode? junction))
        {
            if (junction.Origin.Kind == JunctionOriginKind.ChunkPose)
            {
                return (true, junction.Origin.Chunk);
            }

            if (!string.IsNullOrEmpty(junction.Origin.SourceNodeId))
            {
                return TryResolveNodeChunk(
                    junction.Origin.Kind == JunctionOriginKind.FromJunctionPort,
                    junction.Origin.SourceNodeId,
                    splineById,
                    junctionById,
                    visited);
            }
        }

        return (false, ChunkIndexModel.Zero);
    }

    private void ApplyFilter()
    {
        string filter = FilterText.Value.Trim();
        SceneHierarchyFilterCategory categoryFilter = SelectedFilterCategory.Value;
        SceneHierarchySortMode sortMode = Enum.IsDefined(typeof(SceneHierarchySortMode), SelectedSortModeIndex.Value)
            ? (SceneHierarchySortMode)SelectedSortModeIndex.Value
            : SceneHierarchySortMode.Default;

        List<SceneItemEntry> filtered = new(AllEntries.Count);
        foreach (SceneItemEntry entry in AllEntries)
        {
            if (categoryFilter != SceneHierarchyFilterCategory.All && entry.FilterCategory != categoryFilter)
            {
                continue;
            }

            if (string.IsNullOrEmpty(filter)
                || entry.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || entry.SubLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(entry);
            }
        }

        if (sortMode == SceneHierarchySortMode.Name)
        {
            filtered.Sort(static (a, b) =>
            {
                int cmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.Label, b.Label);
                return cmp != 0 ? cmp : a.DefaultOrder.CompareTo(b.DefaultOrder);
            });
        }
        else if (sortMode == SceneHierarchySortMode.Chunk)
        {
            filtered.Sort(static (a, b) =>
            {
                int hasChunkCmp = (a.HasChunkCoordinate ? 1 : 0).CompareTo(b.HasChunkCoordinate ? 1 : 0);
                if (hasChunkCmp != 0)
                {
                    return hasChunkCmp;
                }

                if (a.HasChunkCoordinate)
                {
                    int xCmp = a.ChunkX.CompareTo(b.ChunkX);
                    if (xCmp != 0)
                    {
                        return xCmp;
                    }

                    int zCmp = a.ChunkZ.CompareTo(b.ChunkZ);
                    if (zCmp != 0)
                    {
                        return zCmp;
                    }
                }

                return a.DefaultOrder.CompareTo(b.DefaultOrder);
            });
        }

        Items.Clear();
        foreach (SceneItemEntry entry in filtered)
        {
            Items.Add(entry);
        }

        SyncSelectionFromWorkspace();
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}

/// <summary>
/// Categorized world asset item displayed in <see cref="AssetBrowserToolViewModel"/>.
/// </summary>
public enum AssetBrowserItemKind
{
    WorldSettings,
    ModelList,
    SoundList,
    CrossSectionList,
    Environment,
    LaneLayout,
    CrossSection,
    SplineTemplate,
    JunctionTemplate,
}

public sealed record AssetBrowserEntry(
    AssetBrowserItemKind Kind,
    string Key,
    string RelativePath,
    bool IsProtected)
{
    public string DisplayKey => IsProtected ? $"[保護] {Key}" : Key;
    public string DisplayLabel => IsProtected ? $"[保護] {Key} ({RelativePath})" : $"{Key} ({RelativePath})";
}

/// <summary>
/// Tool ViewModel listing editable resources, layouts, cross-sections, and templates
/// with commands to open tabs or create new LaneLayouts / CrossSections.
/// </summary>
public sealed class AssetBrowserToolViewModel : Tool, IDisposable
{
    private readonly WorldWorkspaceService Workspace;
    private readonly Action<AssetBrowserEntry> OpenCallback;
    private readonly Action<AssetBrowserEntry>? DeletedCallback;
    private readonly IFileDialogService DialogService;
    private readonly CompositeDisposable Disposables = new CompositeDisposable();

    public ObservableCollection<AssetBrowserEntry> Items { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> ActiveTabItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> BasicItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> ModelListItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> SoundListItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> CrossSectionListItems { get; } = new();
    public ObservableCollection<AssetBrowserEntry> LaneLayoutItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> SplineTemplateItems { get; } = new ObservableCollection<AssetBrowserEntry>();
    public ObservableCollection<AssetBrowserEntry> JunctionTemplateItems { get; } = new ObservableCollection<AssetBrowserEntry>();

    public BindableReactiveProperty<AssetBrowserEntry?> SelectedItem { get; }
    public BindableReactiveProperty<string> NewResourceKey { get; }
    public BindableReactiveProperty<int> SelectedTabIndex { get; }
    public BindableReactiveProperty<bool> CanCreateForActiveTab { get; }
    public BindableReactiveProperty<bool> CanImportForActiveTab { get; }
    public BindableReactiveProperty<string> CreateButtonLabel { get; }
    public BindableReactiveProperty<string> NewKeyWatermark { get; }
    public BindableReactiveProperty<bool> CanDeleteSelected { get; }

    public ReactiveCommand<Unit> OpenSelectedCommand { get; }
    public ReactiveCommand<Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit> CreateActiveResourceCommand { get; }
    public ReactiveCommand<Unit> ImportExistingResourceCommand { get; }
    public ReactiveCommand<Unit> CreateModelListCommand { get; }
    public ReactiveCommand<Unit> CreateSoundListCommand { get; }
    public ReactiveCommand<Unit> CreateCrossSectionListCommand { get; }
    public ReactiveCommand<Unit> CreateLaneLayoutCommand { get; }
    public ReactiveCommand<Unit> CreateCrossSectionCommand { get; }

    public AssetBrowserToolViewModel(
        WorldWorkspaceService workspace,
        Action<AssetBrowserEntry> openCallback,
        Action<AssetBrowserEntry>? deletedCallback = null,
        IFileDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(openCallback);

        Workspace = workspace;
        OpenCallback = openCallback;
        DeletedCallback = deletedCallback;
        DialogService = dialogService ?? new NullFileDialogService();

        SelectedItem = new BindableReactiveProperty<AssetBrowserEntry?>(null).AddTo(Disposables);
        NewResourceKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        CanCreateForActiveTab = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanImportForActiveTab = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        CreateButtonLabel = new BindableReactiveProperty<string>("+ 追加").AddTo(Disposables);
        NewKeyWatermark = new BindableReactiveProperty<string>("新規キー / ファイル名...").AddTo(Disposables);
        CanDeleteSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        OpenSelectedCommand = new ReactiveCommand<Unit>(_ =>
        {
            if (SelectedItem.Value is not null)
            {
                OpenCallback(SelectedItem.Value);
            }
        }).AddTo(Disposables);

        DeleteSelectedCommand = new ReactiveCommand<Unit>(_ =>
        {
            if (SelectedItem.Value is not null)
            {
                DeleteEntry(SelectedItem.Value);
            }
        }).AddTo(Disposables);

        CreateActiveResourceCommand = new ReactiveCommand<Unit>(_ => CreateForActiveTab()).AddTo(Disposables);
        ImportExistingResourceCommand = new ReactiveCommand<Unit>(u => { _ = ImportForActiveTabAsync(); }).AddTo(Disposables);
        CreateModelListCommand = new ReactiveCommand<Unit>(_ => CreateNewModelList()).AddTo(Disposables);
        CreateSoundListCommand = new ReactiveCommand<Unit>(_ => CreateNewSoundList()).AddTo(Disposables);
        CreateCrossSectionListCommand = new ReactiveCommand<Unit>(_ => CreateNewCrossSectionList()).AddTo(Disposables);
        CreateLaneLayoutCommand = new ReactiveCommand<Unit>(_ => CreateNewLaneLayout()).AddTo(Disposables);
        CreateCrossSectionCommand = new ReactiveCommand<Unit>(_ => CreateNewCrossSection()).AddTo(Disposables);

        SelectedItem.Subscribe(entry => CanDeleteSelected.Value = entry is not null && CanDeleteEntry(entry)).AddTo(Disposables);
        SelectedTabIndex.Subscribe(_ =>
        {
            SelectedItem.Value = null;
            UpdateActiveTabCreateUi();
            SyncActiveTabItems();
        }).AddTo(Disposables);
        Workspace.ProjectRevision.Subscribe(_ => Rebuild()).AddTo(Disposables);

        UpdateActiveTabCreateUi();
        Rebuild();
    }

    public void OpenEntry(AssetBrowserEntry entry)
    {
        OpenCallback(entry);
    }

    public bool CanDeleteEntry(AssetBrowserEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind switch
        {
            AssetBrowserItemKind.ModelList => true,
            AssetBrowserItemKind.SoundList => true,
            AssetBrowserItemKind.CrossSectionList => true,
            AssetBrowserItemKind.LaneLayout => true,
            AssetBrowserItemKind.CrossSection => true,
            AssetBrowserItemKind.SplineTemplate => true,
            AssetBrowserItemKind.JunctionTemplate => true,
            _ => false,
        };
    }

    public bool DeleteEntry(AssetBrowserEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!CanDeleteEntry(entry))
        {
            return false;
        }

        bool removed = entry.Kind switch
        {
            AssetBrowserItemKind.ModelList => Workspace.RemoveModelList(entry.RelativePath),
            AssetBrowserItemKind.SoundList => Workspace.RemoveSoundList(entry.RelativePath),
            AssetBrowserItemKind.CrossSectionList => Workspace.RemoveCrossSectionList(entry.RelativePath),
            AssetBrowserItemKind.LaneLayout => Workspace.RemoveLaneLayout(entry.Key),
            AssetBrowserItemKind.CrossSection => Workspace.RemoveCrossSection(entry.Key),
            AssetBrowserItemKind.SplineTemplate => Workspace.RemoveSplineTemplate(entry.Key),
            AssetBrowserItemKind.JunctionTemplate => Workspace.RemoveJunctionTemplate(entry.Key),
            _ => false,
        };

        if (removed)
        {
            if (SelectedItem.Value == entry)
            {
                SelectedItem.Value = null;
            }

            DeletedCallback?.Invoke(entry);
        }

        return removed;
    }

    private void UpdateActiveTabCreateUi()
    {
        switch (SelectedTabIndex.Value)
        {
            case 0:
                CanCreateForActiveTab.Value = false;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ 追加";
                NewKeyWatermark.Value = "既存の環境プロファイル (xml) を読込可能";
                break;

            case 1:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ モデルリスト";
                NewKeyWatermark.Value = "新規ファイル名 (例: Models_Sub.txt)";
                break;

            case 2:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ サウンドリスト";
                NewKeyWatermark.Value = "新規ファイル名 (例: Sounds_Sub.txt)";
                break;

            case 3:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ 進路レイアウト";
                NewKeyWatermark.Value = "新規/読込キー (例: Arterial4Lane)";
                break;

            case 4:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ 断面リスト";
                NewKeyWatermark.Value = "新規ファイル名 (例: CrossSections_Sub.txt)";
                break;

            case 5:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ スプラインテンプレート";
                NewKeyWatermark.Value = "新規/読込キー (例: SplineRoad1)";
                break;

            case 6:
                CanCreateForActiveTab.Value = true;
                CanImportForActiveTab.Value = true;
                CreateButtonLabel.Value = "+ ジャンクション";
                NewKeyWatermark.Value = "新規/読込キー (例: TIntersection1)";
                break;

            default:
                CanCreateForActiveTab.Value = false;
                CanImportForActiveTab.Value = false;
                CreateButtonLabel.Value = "+ 追加";
                NewKeyWatermark.Value = "新規キー / ファイル名...";
                break;
        }
    }

    private async Task ImportForActiveTabAsync()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string initialDir = SelectedTabIndex.Value switch
        {
            5 => Directory.Exists(Path.Combine(project.WorldDirectory, "Templates", "Splines"))
                ? Path.Combine(project.WorldDirectory, "Templates", "Splines")
                : project.WorldDirectory,
            6 => Directory.Exists(Path.Combine(project.WorldDirectory, "Templates", "Junctions"))
                ? Path.Combine(project.WorldDirectory, "Templates", "Junctions")
                : project.WorldDirectory,
            _ => project.WorldDirectory,
        };

        (string title, string filterName, string[] extensions) = SelectedTabIndex.Value switch
        {
            0 => ("既存の環境設定 XML を選択", "Environment XML", new[] { "*.xml" }),
            1 => ("既存のモデルリスト (.txt) を選択", "Model List", new[] { "*.txt" }),
            2 => ("既存のサウンドリスト (.txt) を選択", "Sound List", new[] { "*.txt" }),
            3 => ("既存の進路レイアウト XML を選択", "LaneLayout XML", new[] { "*.xml" }),
            4 => ("既存の断面リスト (.txt) を選択", "CrossSection List", new[] { "*.txt" }),
            5 => ("既存のスプラインテンプレート CSX を選択", "SplineTemplate CSX", new[] { "*.csx" }),
            6 => ("既存のジャンクションテンプレート CSX を選択", "JunctionTemplate CSX", new[] { "*.csx" }),
            _ => (string.Empty, string.Empty, Array.Empty<string>()),
        };

        if (extensions.Length == 0)
        {
            return;
        }

        IReadOnlyList<string> pickedFiles = await DialogService.PickAssetFilesAsync(title, initialDir, filterName, extensions);
        if (0 < pickedFiles.Count)
        {
            ImportExistingFromFiles(SelectedTabIndex.Value, pickedFiles);
        }
    }

    public bool ImportExistingFromFile(int tabIndex, string filePath)
    {
        return ImportExistingFromFiles(tabIndex, new string[] { filePath }) == 1;
    }

    public int ImportExistingFromFiles(int tabIndex, IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
        {
            return 0;
        }

        string desiredKey = filePaths.Count == 1 ? NewResourceKey.Value.Trim() : string.Empty;
        int importedCount = 0;
        AssetBrowserEntry? lastImportedEntry = null;

        foreach (string rawPath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            AssetBrowserEntry? entry = ImportSingleExistingFile(tabIndex, rawPath, desiredKey);
            if (entry is not null)
            {
                importedCount++;
                lastImportedEntry = entry;
            }
        }

        if (0 < importedCount)
        {
            NewResourceKey.Value = string.Empty;
            if (lastImportedEntry is not null)
            {
                OpenCallback(lastImportedEntry);
            }

            if (1 < importedCount)
            {
                Workspace.LastStatusMessage.Value = $"{importedCount} 件の既存ファイルを追加しました。";
            }
        }

        return importedCount;
    }

    private AssetBrowserEntry? ImportSingleExistingFile(int tabIndex, string filePath, string desiredKey)
    {
        switch (tabIndex)
        {
            case 0:
                return Workspace.ImportExistingEnvironment(filePath, out string? envRel) && envRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.Environment, Path.GetFileNameWithoutExtension(envRel), envRel, false)
                    : null;

            case 1:
                return Workspace.ImportExistingModelList(filePath, out string? modelRel) && modelRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.ModelList, Path.GetFileName(modelRel), modelRel, false)
                    : null;

            case 2:
                return Workspace.ImportExistingSoundList(filePath, out string? soundRel) && soundRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.SoundList, Path.GetFileName(soundRel), soundRel, false)
                    : null;

            case 3:
                return Workspace.ImportExistingLaneLayout(filePath, desiredKey, out string? layoutKey, out string? layoutRel)
                    && layoutKey is not null && layoutRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.LaneLayout, layoutKey, layoutRel, false)
                    : null;

            case 4:
                return Workspace.ImportExistingCrossSectionList(filePath, out string? csListRel) && csListRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.CrossSectionList, Path.GetFileName(csListRel), csListRel, false)
                    : null;

            case 5:
                return Workspace.ImportExistingSplineTemplate(filePath, desiredKey, out string? tplKey, out string? tplRel)
                    && tplKey is not null && tplRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.SplineTemplate, tplKey, tplRel, false)
                    : null;

            case 6:
                return Workspace.ImportExistingJunctionTemplate(filePath, desiredKey, out string? jtKey, out string? jtRel)
                    && jtKey is not null && jtRel is not null
                    ? new AssetBrowserEntry(AssetBrowserItemKind.JunctionTemplate, jtKey, jtRel, false)
                    : null;

            default:
                return null;
        }
    }

    private void CreateForActiveTab()
    {
        switch (SelectedTabIndex.Value)
        {
            case 1:
                CreateNewModelList();
                break;
            case 2:
                CreateNewSoundList();
                break;
            case 3:
                CreateNewLaneLayout();
                break;
            case 4:
                CreateNewCrossSectionList();
                break;
            case 5:
                CreateNewSplineTemplate();
                break;
            case 6:
                CreateNewJunctionTemplate();
                break;
        }
    }

    private void CreateNewModelList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string relPath = BuildUniqueListPath(NewResourceKey.Value, "Models", key => project.ModelLists.ContainsKey(key));
        if (Workspace.AddNewModelList(relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.ModelList, Path.GetFileName(relPath), relPath, false));
        }
    }

    private void CreateNewSoundList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string relPath = BuildUniqueListPath(NewResourceKey.Value, "Sounds", key => project.SoundLists.ContainsKey(key));
        if (Workspace.AddNewSoundList(relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.SoundList, Path.GetFileName(relPath), relPath, false));
        }
    }

    private void CreateNewCrossSectionList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string relPath = BuildUniqueListPath(NewResourceKey.Value, "CrossSections", key => project.CrossSectionLists.ContainsKey(key));
        if (Workspace.AddNewCrossSectionList(relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.CrossSectionList, Path.GetFileName(relPath), relPath, false));
        }
    }

    private static string BuildUniqueListPath(string rawInput, string baseName, Func<string, bool> exists)
    {
        string trimmed = rawInput.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            string withExt = Path.HasExtension(trimmed) ? trimmed : $"{trimmed}.txt";
            return WorldProject.NormalizeRelativePath(withExt);
        }

        string candidate = $"{baseName}.txt";
        int n = 2;
        while (exists(candidate))
        {
            candidate = $"{baseName}{n}.txt";
            n++;
        }

        return candidate;
    }

    private void CreateNewLaneLayout()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string key = NewResourceKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            int n = project.LaneLayouts.Count + 1;
            key = $"Layout{n}";
            while (project.LaneLayouts.ContainsKey(key))
            {
                n++;
                key = $"Layout{n}";
            }
        }

        string relPath = $"LaneLayouts/{key}.xml";
        if (Workspace.AddNewLaneLayout(key, relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.LaneLayout, key, relPath, false));
        }
    }

    private void CreateNewCrossSection()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string key = NewResourceKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            int n = project.CrossSections.Count + 1;
            key = $"RoadSection{n}";
            while (project.CrossSections.ContainsKey(key))
            {
                n++;
                key = $"RoadSection{n}";
            }
        }

        string relPath = $"CrossSections/{key}.xml";
        if (Workspace.AddNewCrossSection(key, relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.CrossSection, key, relPath, false));
        }
    }

    private void CreateNewSplineTemplate()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string key = NewResourceKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            int n = project.SplineTemplates.Count + 1;
            key = $"Spline{n}";
            while (project.SplineTemplates.ContainsKey(key))
            {
                n++;
                key = $"Spline{n}";
            }
        }

        string relPath = Serialization.Storage.WorldProjectStorage.BuildSplineTemplateRelativePath(key);
        if (Workspace.AddNewSplineTemplate(key, relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.SplineTemplate, key, relPath, false));
        }
    }

    private void CreateNewJunctionTemplate()
    {
        if (Workspace.CreateNewJunctionTemplate(NewResourceKey.Value, out string? key, out string? relPath)
            && !string.IsNullOrEmpty(key)
            && !string.IsNullOrEmpty(relPath))
        {
            NewResourceKey.Value = string.Empty;
            OpenCallback(new AssetBrowserEntry(AssetBrowserItemKind.JunctionTemplate, key, relPath, false));
        }
    }

    private void Rebuild()
    {
        Items.Clear();
        BasicItems.Clear();
        ModelListItems.Clear();
        SoundListItems.Clear();
        CrossSectionListItems.Clear();
        LaneLayoutItems.Clear();
        SplineTemplateItems.Clear();
        JunctionTemplateItems.Clear();

        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            SelectedItem.Value = null;
            return;
        }

        string worldInfoName = string.IsNullOrEmpty(project.WorldInfoFilePath)
            ? "WorldInfo.xml"
            : Path.GetFileName(project.WorldInfoFilePath);

        AddCategorizedEntry(new AssetBrowserEntry(
            AssetBrowserItemKind.WorldSettings,
            "ワールド設定 / Init",
            $"{worldInfoName} + {project.Init.RelativePath}",
            project.IsProtected(project.Init.RelativePath)));

        foreach (ModelListDocument m in project.ModelLists.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.ModelList, m.BundleKey ?? Path.GetFileName(m.RelativePath), m.RelativePath, false));
        }

        foreach (SoundListDocument s in project.SoundLists.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.SoundList, s.BundleKey ?? Path.GetFileName(s.RelativePath), s.RelativePath, false));
        }

        foreach (CrossSectionListDocument csList in project.CrossSectionLists.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.CrossSectionList, Path.GetFileName(csList.RelativePath), csList.RelativePath, false));
        }

        foreach (EnvironmentProfileDocument env in project.Environments.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.Environment, Path.GetFileNameWithoutExtension(env.RelativePath), env.RelativePath, false));
        }

        foreach (LaneLayoutDocument layout in project.LaneLayouts.Values.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.LaneLayout, layout.Key, layout.RelativePath, false));
        }

        foreach (SplineTemplateDocument st in project.SplineTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.SplineTemplate, st.Key, st.RelativePath, project.IsProtected(st.RelativePath)));
        }

        foreach (JunctionTemplateDocument jt in project.JunctionTemplates.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            AddCategorizedEntry(new AssetBrowserEntry(AssetBrowserItemKind.JunctionTemplate, jt.Key, jt.RelativePath, project.IsProtected(jt.RelativePath)));
        }

        if (SelectedItem.Value is not null)
        {
            CanDeleteSelected.Value = CanDeleteEntry(SelectedItem.Value);
        }

        SyncActiveTabItems();
    }

    private void SyncActiveTabItems()
    {
        ObservableCollection<AssetBrowserEntry> source = SelectedTabIndex.Value switch
        {
            0 => BasicItems,
            1 => ModelListItems,
            2 => SoundListItems,
            3 => LaneLayoutItems,
            4 => CrossSectionListItems,
            5 => SplineTemplateItems,
            6 => JunctionTemplateItems,
            _ => BasicItems,
        };

        ActiveTabItems.Clear();
        foreach (AssetBrowserEntry entry in source)
        {
            ActiveTabItems.Add(entry);
        }
    }

    private void AddCategorizedEntry(AssetBrowserEntry entry)
    {
        Items.Add(entry);
        switch (entry.Kind)
        {
            case AssetBrowserItemKind.WorldSettings:
            case AssetBrowserItemKind.Environment:
                BasicItems.Add(entry);
                break;
            case AssetBrowserItemKind.ModelList:
                ModelListItems.Add(entry);
                break;
            case AssetBrowserItemKind.SoundList:
                SoundListItems.Add(entry);
                break;
            case AssetBrowserItemKind.CrossSectionList:
                CrossSectionListItems.Add(entry);
                break;
            case AssetBrowserItemKind.LaneLayout:
                LaneLayoutItems.Add(entry);
                break;
            case AssetBrowserItemKind.SplineTemplate:
                SplineTemplateItems.Add(entry);
                break;
            case AssetBrowserItemKind.JunctionTemplate:
                JunctionTemplateItems.Add(entry);
                break;
        }
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}

/// <summary>
/// Row item displayed in inspector list boxes for junction static props, curve extrusion overrides, and explicit port connections.
/// </summary>
public sealed record InspectorListEntry(
    int Index,
    string DisplayLabel);

/// <summary>
/// Tool ViewModel displaying context-sensitive selected 3D Prop, BackgroundModel, Spline, and Junction properties
/// alongside explicit network connections and workspace dirty/protected file overview.
/// </summary>
public sealed class InspectorToolViewModel : Tool, IDisposable
{
    private readonly WorldWorkspaceService Workspace;
    private readonly CompositeDisposable Disposables = new();
    private bool IsSyncingProp;
    private bool IsSyncingSpline;
    private bool IsSyncingBackground;
    private bool IsSyncingJunction;
    private bool IsCommittingProp;
    private bool IsCommittingSpline;
    private bool IsCommittingJunction;

    public BindableReactiveProperty<bool> HasAnySelection { get; }
    public BindableReactiveProperty<bool> HasSelectedProp { get; }
    public BindableReactiveProperty<string> PropTitleText { get; }
    public BindableReactiveProperty<string> PropDisplayName { get; }
    public BindableReactiveProperty<string> SelectedPropIdText { get; }
    public ObservableCollection<string> AvailableModelKeys { get; } = new();
    public BindableReactiveProperty<string> SelectedPropModelKey { get; }

    public BindableReactiveProperty<decimal?> PropChunkX { get; }
    public BindableReactiveProperty<decimal?> PropChunkZ { get; }
    public BindableReactiveProperty<decimal?> PropLocalX { get; }
    public BindableReactiveProperty<decimal?> PropLocalY { get; }
    public BindableReactiveProperty<decimal?> PropLocalZ { get; }
    public BindableReactiveProperty<decimal?> PropRotX { get; }
    public BindableReactiveProperty<decimal?> PropRotY { get; }
    public BindableReactiveProperty<decimal?> PropRotZ { get; }

    // Selected BackgroundModel Inspector
    public BindableReactiveProperty<bool> HasSelectedBackground { get; }
    public BindableReactiveProperty<string> SelectedBackgroundIndexText { get; }
    public BindableReactiveProperty<string> SelectedBackgroundModelKey { get; }

    // Selected Spline Inspector
    public BindableReactiveProperty<bool> HasSelectedSpline { get; }
    public BindableReactiveProperty<string> SplineTitleText { get; }
    public BindableReactiveProperty<string> SplineDisplayName { get; }
    public BindableReactiveProperty<string> SplineOriginSummaryText { get; }
    public BindableReactiveProperty<bool> IsSplineChunkPoseOrigin { get; }
    public BindableReactiveProperty<string> SplineKey { get; }
    public ObservableCollection<string> AvailableSplineTemplateKeys { get; } = new();
    public BindableReactiveProperty<string> SplineTemplateKey { get; }
    public BindableReactiveProperty<decimal?> SplineChunkX { get; }
    public BindableReactiveProperty<decimal?> SplineChunkZ { get; }
    public BindableReactiveProperty<decimal?> SplineLocalX { get; }
    public BindableReactiveProperty<decimal?> SplineLocalY { get; }
    public BindableReactiveProperty<decimal?> SplineLocalZ { get; }
    public BindableReactiveProperty<decimal?> SplineRotX { get; }
    public BindableReactiveProperty<decimal?> SplineRotY { get; }
    public BindableReactiveProperty<decimal?> SplineRotZ { get; }
    public BindableReactiveProperty<string> SplinePreBuildUserCode { get; }

    // Spline Outlet -> IntoJunction chaining
    public ObservableCollection<string> AvailableJunctionTemplateKeys { get; } = new();
    public BindableReactiveProperty<string> SelectedChainJunctionTemplateKey { get; }
    public ObservableCollection<string> CompatibleChainJunctionPorts { get; } = new();
    public BindableReactiveProperty<string> SelectedChainJunctionPortKey { get; }
    public BindableReactiveProperty<bool> CanExtendJunctionFromSplineOutlet { get; }

    // ConnectBezier Controls
    public ObservableCollection<CompatiblePortCandidate> CompatibleConnectTargets { get; } = new();
    public BindableReactiveProperty<bool> HasCompatibleConnectTargets { get; }
    public BindableReactiveProperty<CompatiblePortCandidate?> SelectedConnectTarget { get; }
    public BindableReactiveProperty<decimal?> ConnectHandleScale { get; }
    public BindableReactiveProperty<string> ConnectOutputAnchorId { get; }
    public BindableReactiveProperty<string> ConnectStatusText { get; }
    public BindableReactiveProperty<bool> HasActiveConnectBezier { get; }

    // Selected Junction Inspector
    public BindableReactiveProperty<bool> HasSelectedJunction { get; }
    public BindableReactiveProperty<string> JunctionTitleText { get; }
    public BindableReactiveProperty<string> JunctionDisplayName { get; }
    public BindableReactiveProperty<string> JunctionKey { get; }
    public BindableReactiveProperty<string> JunctionTemplateKey { get; }
    public BindableReactiveProperty<string> JunctionOriginSummaryText { get; }
    public BindableReactiveProperty<bool> IsJunctionChunkPoseOrigin { get; }
    public BindableReactiveProperty<decimal?> JunctionChunkX { get; }
    public BindableReactiveProperty<decimal?> JunctionChunkZ { get; }
    public BindableReactiveProperty<decimal?> JunctionLocalX { get; }
    public BindableReactiveProperty<decimal?> JunctionLocalY { get; }
    public BindableReactiveProperty<decimal?> JunctionLocalZ { get; }
    public BindableReactiveProperty<decimal?> JunctionRotX { get; }
    public BindableReactiveProperty<decimal?> JunctionRotY { get; }
    public BindableReactiveProperty<decimal?> JunctionRotZ { get; }
    public BindableReactiveProperty<string> JunctionPreBuildUserCode { get; }

    // Junction Port Chaining & Explicit Network.Connect
    public ObservableCollection<string> AvailableJunctionPortKeys { get; } = new();
    public BindableReactiveProperty<string> SelectedJunctionPortKey { get; }
    public ObservableCollection<string> CompatibleSplineTemplatesForJunctionPort { get; } = new();
    public BindableReactiveProperty<string> SelectedChainSplineTemplateKey { get; }
    public BindableReactiveProperty<bool> CanExtendSplineFromJunctionPort { get; }
    public BindableReactiveProperty<string> SelectedJunctionChainTargetTemplateKey { get; }
    public ObservableCollection<string> CompatibleJunctionPortsForJunctionChain { get; } = new();
    public BindableReactiveProperty<string> SelectedJunctionChainTargetPortKey { get; }
    public BindableReactiveProperty<bool> CanExtendJunctionFromJunctionPort { get; }
    public ObservableCollection<CompatiblePortCandidate> CompatibleExplicitConnectTargets { get; } = new();
    public BindableReactiveProperty<CompatiblePortCandidate?> SelectedExplicitConnectTarget { get; }
    public BindableReactiveProperty<bool> HasCompatibleExplicitConnectTargets { get; }
    public BindableReactiveProperty<string> SelectedJunctionPortStatusText { get; }
    public BindableReactiveProperty<bool> HasActiveExplicitConnectionOnSelectedPort { get; }
    public BindableReactiveProperty<bool> IsSelectedJunctionPortFree { get; }
    public BindableReactiveProperty<bool> IsJunctionExtendSplineMode { get; }
    public BindableReactiveProperty<bool> IsJunctionChainJunctionMode { get; }
    public BindableReactiveProperty<bool> IsJunctionExplicitConnectMode { get; }
    public BindableReactiveProperty<bool> IsPickingSplineConnectTargetIn3D { get; }
    public BindableReactiveProperty<bool> IsPickingJunctionConnectTargetIn3D { get; }

    // PathOverrides (TrafficDensityFactor)
    public ObservableCollection<string> JunctionPathKeys { get; } = new();
    public BindableReactiveProperty<string> SelectedJunctionPathKey { get; }
    public BindableReactiveProperty<decimal?> JunctionPathTrafficDensity { get; }
    public BindableReactiveProperty<string> JunctionPathOverrideStatusText { get; }

    // CurveOverrides (AddCurve PutExtrusion)
    public ObservableCollection<string> JunctionCurveKeys { get; } = new();
    public BindableReactiveProperty<string> SelectedJunctionCurveKey { get; }
    public ObservableCollection<InspectorListEntry> JunctionCurveExtrusions { get; } = new();
    public BindableReactiveProperty<int> SelectedJunctionCurveExtrusionIndex { get; }
    public ObservableCollection<string> AvailableCrossSectionKeys { get; } = new();
    public BindableReactiveProperty<string> NewCurveExtrusionSectionKey { get; }
    public BindableReactiveProperty<decimal?> NewCurveExtrusionFrom { get; }
    public BindableReactiveProperty<decimal?> NewCurveExtrusionTo { get; }

    // Static Instance Props
    public ObservableCollection<InspectorListEntry> JunctionStaticProps { get; } = new();
    public BindableReactiveProperty<int> SelectedJunctionStaticPropIndex { get; }
    public BindableReactiveProperty<string> NewJunctionStaticPropModelKey { get; }
    public BindableReactiveProperty<decimal?> NewJunctionStaticPropX { get; }
    public BindableReactiveProperty<decimal?> NewJunctionStaticPropY { get; }
    public BindableReactiveProperty<decimal?> NewJunctionStaticPropZ { get; }

    // Explicit Port Connections (Network.Connect)
    public ObservableCollection<InspectorListEntry> ExplicitConnections { get; } = new();
    public BindableReactiveProperty<int> SelectedExplicitConnectionIndex { get; }
    public BindableReactiveProperty<bool> HasExplicitConnections { get; }

    public ReactiveCommand<Unit> FocusSelectedPropCommand { get; }
    public ReactiveCommand<Unit> DeleteSelectedPropCommand { get; }
    public ReactiveCommand<Unit> AddNewPropCommand { get; }
    public ReactiveCommand<Unit> AddNewBackgroundCommand { get; }
    public ReactiveCommand<Unit> DeleteSelectedBackgroundCommand { get; }
    public ReactiveCommand<Unit> MoveBackgroundUpCommand { get; }
    public ReactiveCommand<Unit> MoveBackgroundDownCommand { get; }

    public ReactiveCommand<Unit> AddNewSplineCommand { get; }
    public ReactiveCommand<Unit> FocusSelectedSplineCommand { get; }
    public ReactiveCommand<Unit> DeleteSelectedSplineCommand { get; }
    public ReactiveCommand<Unit> ExtendSplineFromOutletCommand { get; }
    public ReactiveCommand<Unit> ExtendJunctionFromSplineOutletCommand { get; }
    public ReactiveCommand<Unit> ClearSplineTemplateCommand { get; }
    public ReactiveCommand<Unit> ApplyConnectBezierCommand { get; }
    public ReactiveCommand<Unit> ClearConnectBezierCommand { get; }
    public ReactiveCommand<Unit> ToggleSplineConnectPickIn3DCommand { get; }

    public ReactiveCommand<Unit> AddNewJunctionCommand { get; }
    public ReactiveCommand<Unit> FocusSelectedJunctionCommand { get; }
    public ReactiveCommand<Unit> DeleteSelectedJunctionCommand { get; }
    public ReactiveCommand<Unit> SelectJunctionExtendSplineModeCommand { get; }
    public ReactiveCommand<Unit> SelectJunctionChainJunctionModeCommand { get; }
    public ReactiveCommand<Unit> SelectJunctionExplicitConnectModeCommand { get; }
    public ReactiveCommand<Unit> ExtendSplineFromJunctionPortCommand { get; }
    public ReactiveCommand<Unit> ExtendJunctionFromJunctionPortCommand { get; }
    public ReactiveCommand<Unit> AddExplicitPortConnectionCommand { get; }
    public ReactiveCommand<Unit> DisconnectSelectedJunctionPortCommand { get; }
    public ReactiveCommand<Unit> ToggleJunctionConnectPickIn3DCommand { get; }
    public ReactiveCommand<Unit> RemoveExplicitPortConnectionCommand { get; }
    public ReactiveCommand<Unit> ApplyJunctionPathOverrideCommand { get; }
    public ReactiveCommand<Unit> RemoveJunctionPathOverrideCommand { get; }
    public ReactiveCommand<Unit> AddJunctionCurveExtrusionCommand { get; }
    public ReactiveCommand<Unit> RemoveJunctionCurveExtrusionCommand { get; }
    public ReactiveCommand<Unit> AddJunctionStaticPropCommand { get; }
    public ReactiveCommand<Unit> RemoveJunctionStaticPropCommand { get; }

    public BindableReactiveProperty<string> SummaryText { get; }
    public ObservableCollection<string> DirtyFilesList { get; } = new();
    public ObservableCollection<string> ProtectedFilesList { get; } = new();

    public InspectorToolViewModel(WorldWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;

        HasAnySelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasSelectedProp = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PropTitleText = new BindableReactiveProperty<string>("3D プロップ").AddTo(Disposables);
        PropDisplayName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedPropIdText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedPropModelKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        PropChunkX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropChunkZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropLocalX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropLocalY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropLocalZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        HasSelectedBackground = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedBackgroundIndexText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedBackgroundModelKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        HasSelectedSpline = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SplineTitleText = new BindableReactiveProperty<string>("スプライン (Spline)").AddTo(Disposables);
        SplineDisplayName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SplineOriginSummaryText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsSplineChunkPoseOrigin = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SplineKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SplineTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SplineChunkX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineChunkZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineLocalX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineLocalY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineLocalZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplineRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        SplinePreBuildUserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedChainJunctionTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedChainJunctionPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CanExtendJunctionFromSplineOutlet = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        HasCompatibleConnectTargets = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedConnectTarget = new BindableReactiveProperty<CompatiblePortCandidate?>(null).AddTo(Disposables);
        ConnectHandleScale = new BindableReactiveProperty<decimal?>(0.5m).AddTo(Disposables);
        ConnectOutputAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ConnectStatusText = new BindableReactiveProperty<string>("未接続").AddTo(Disposables);
        HasActiveConnectBezier = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        HasSelectedJunction = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        JunctionTitleText = new BindableReactiveProperty<string>("ジャンクション (Junction)").AddTo(Disposables);
        JunctionDisplayName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        JunctionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        JunctionTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        JunctionOriginSummaryText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsJunctionChunkPoseOrigin = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        JunctionChunkX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionChunkZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionLocalX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionLocalY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionLocalZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        JunctionPreBuildUserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedJunctionPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedChainSplineTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CanExtendSplineFromJunctionPort = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedJunctionChainTargetTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedJunctionChainTargetPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CanExtendJunctionFromJunctionPort = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedExplicitConnectTarget = new BindableReactiveProperty<CompatiblePortCandidate?>(null).AddTo(Disposables);
        HasCompatibleExplicitConnectTargets = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        SelectedJunctionPortStatusText = new BindableReactiveProperty<string>("状態: 未選択").AddTo(Disposables);
        HasActiveExplicitConnectionOnSelectedPort = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsSelectedJunctionPortFree = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsJunctionExtendSplineMode = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsJunctionChainJunctionMode = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsJunctionExplicitConnectMode = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPickingSplineConnectTargetIn3D = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPickingJunctionConnectTargetIn3D = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        SelectedJunctionPathKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        JunctionPathTrafficDensity = new BindableReactiveProperty<decimal?>(1.0m).AddTo(Disposables);
        JunctionPathOverrideStatusText = new BindableReactiveProperty<string>("未設定 (テンプレート既定値)").AddTo(Disposables);

        SelectedJunctionCurveKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        SelectedJunctionCurveExtrusionIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        NewCurveExtrusionSectionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewCurveExtrusionFrom = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        NewCurveExtrusionTo = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);

        SelectedJunctionStaticPropIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        NewJunctionStaticPropModelKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewJunctionStaticPropX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        NewJunctionStaticPropY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        NewJunctionStaticPropZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedExplicitConnectionIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        HasExplicitConnections = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        SummaryText = new BindableReactiveProperty<string>("ワールド未読込").AddTo(Disposables);

        FocusSelectedPropCommand = new ReactiveCommand<Unit>(u => Workspace.RequestFocusSelectedOrDefault()).AddTo(Disposables);
        DeleteSelectedPropCommand = new ReactiveCommand<Unit>(u =>
        {
            string? id = Workspace.SelectedPropId.Value;
            if (!string.IsNullOrEmpty(id))
            {
                Workspace.RemovePropPlacement(id);
            }
        }).AddTo(Disposables);

        AddNewPropCommand = new ReactiveCommand<Unit>(u => AddNewDefaultProp()).AddTo(Disposables);
        AddNewBackgroundCommand = new ReactiveCommand<Unit>(u => AddNewDefaultBackground()).AddTo(Disposables);
        DeleteSelectedBackgroundCommand = new ReactiveCommand<Unit>(u =>
        {
            int bgIdx = Workspace.SelectedBackgroundModelIndex.Value;
            if (0 <= bgIdx)
            {
                Workspace.RemoveBackgroundModelAt(bgIdx);
            }
        }).AddTo(Disposables);
        MoveBackgroundUpCommand = new ReactiveCommand<Unit>(u =>
        {
            int bgIdx = Workspace.SelectedBackgroundModelIndex.Value;
            if (0 < bgIdx)
            {
                Workspace.MoveBackgroundModel(bgIdx, bgIdx - 1);
            }
        }).AddTo(Disposables);
        MoveBackgroundDownCommand = new ReactiveCommand<Unit>(u =>
        {
            int bgIdx = Workspace.SelectedBackgroundModelIndex.Value;
            if (0 <= bgIdx)
            {
                Workspace.MoveBackgroundModel(bgIdx, bgIdx + 1);
            }
        }).AddTo(Disposables);

        AddNewSplineCommand = new ReactiveCommand<Unit>(u => AddNewDefaultSpline()).AddTo(Disposables);
        FocusSelectedSplineCommand = new ReactiveCommand<Unit>(u => Workspace.RequestFocusSelectedOrDefault()).AddTo(Disposables);
        DeleteSelectedSplineCommand = new ReactiveCommand<Unit>(u =>
        {
            string? id = Workspace.SelectedSplineId.Value;
            if (!string.IsNullOrEmpty(id))
            {
                Workspace.RemoveSplinePlacement(id);
            }
        }).AddTo(Disposables);
        ExtendSplineFromOutletCommand = new ReactiveCommand<Unit>(u =>
        {
            string? id = Workspace.SelectedSplineId.Value;
            if (!string.IsNullOrEmpty(id))
            {
                Workspace.AddChainedSplineFromOutlet(id);
            }
        }).AddTo(Disposables);
        ExtendJunctionFromSplineOutletCommand = new ReactiveCommand<Unit>(u => ExtendJunctionFromSelectedSplineOutlet()).AddTo(Disposables);
        ClearSplineTemplateCommand = new ReactiveCommand<Unit>(u => ClearSelectedSplineTemplate()).AddTo(Disposables);
        ApplyConnectBezierCommand = new ReactiveCommand<Unit>(u => ApplySelectedConnectBezier()).AddTo(Disposables);
        ClearConnectBezierCommand = new ReactiveCommand<Unit>(u => ClearSelectedConnectBezier()).AddTo(Disposables);
        ToggleSplineConnectPickIn3DCommand = new ReactiveCommand<Unit>(u => ToggleSplineConnectPickIn3D()).AddTo(Disposables);

        AddNewJunctionCommand = new ReactiveCommand<Unit>(u => AddNewDefaultJunction()).AddTo(Disposables);
        FocusSelectedJunctionCommand = new ReactiveCommand<Unit>(u => Workspace.RequestFocusSelectedOrDefault()).AddTo(Disposables);
        DeleteSelectedJunctionCommand = new ReactiveCommand<Unit>(u =>
        {
            string? id = Workspace.SelectedJunctionId.Value;
            if (!string.IsNullOrEmpty(id))
            {
                Workspace.RemoveJunctionPlacement(id);
            }
        }).AddTo(Disposables);
        SelectJunctionExtendSplineModeCommand = new ReactiveCommand<Unit>(u => SetJunctionConnectionMode(0)).AddTo(Disposables);
        SelectJunctionChainJunctionModeCommand = new ReactiveCommand<Unit>(u => SetJunctionConnectionMode(1)).AddTo(Disposables);
        SelectJunctionExplicitConnectModeCommand = new ReactiveCommand<Unit>(u => SetJunctionConnectionMode(2)).AddTo(Disposables);
        ExtendSplineFromJunctionPortCommand = new ReactiveCommand<Unit>(u => ExtendSplineFromSelectedJunctionPort()).AddTo(Disposables);
        ExtendJunctionFromJunctionPortCommand = new ReactiveCommand<Unit>(u => ExtendJunctionFromSelectedJunctionPort()).AddTo(Disposables);
        AddExplicitPortConnectionCommand = new ReactiveCommand<Unit>(u => AddExplicitPortConnectionFromSelectedJunction()).AddTo(Disposables);
        DisconnectSelectedJunctionPortCommand = new ReactiveCommand<Unit>(u => DisconnectExplicitConnectionOnSelectedJunctionPort()).AddTo(Disposables);
        ToggleJunctionConnectPickIn3DCommand = new ReactiveCommand<Unit>(u => ToggleJunctionConnectPickIn3D()).AddTo(Disposables);
        RemoveExplicitPortConnectionCommand = new ReactiveCommand<Unit>(u =>
        {
            int idx = SelectedExplicitConnectionIndex.Value;
            if (0 <= idx)
            {
                Workspace.RemoveExplicitPortConnection(idx);
            }
        }).AddTo(Disposables);
        ApplyJunctionPathOverrideCommand = new ReactiveCommand<Unit>(u => ApplySelectedJunctionPathOverride()).AddTo(Disposables);
        RemoveJunctionPathOverrideCommand = new ReactiveCommand<Unit>(u => RemoveSelectedJunctionPathOverride()).AddTo(Disposables);
        AddJunctionCurveExtrusionCommand = new ReactiveCommand<Unit>(u => AddSelectedJunctionCurveExtrusion()).AddTo(Disposables);
        RemoveJunctionCurveExtrusionCommand = new ReactiveCommand<Unit>(u => RemoveSelectedJunctionCurveExtrusion()).AddTo(Disposables);
        AddJunctionStaticPropCommand = new ReactiveCommand<Unit>(u => AddSelectedJunctionStaticProp()).AddTo(Disposables);
        RemoveJunctionStaticPropCommand = new ReactiveCommand<Unit>(u => RemoveSelectedJunctionStaticProp()).AddTo(Disposables);

        PropDisplayName.Skip(1).Subscribe(_ => { if (!IsSyncingProp) CommitPropDisplayName(); }).AddTo(Disposables);
        SelectedPropModelKey.Skip(1).Subscribe(_ => CommitSelectedPropModelKey()).AddTo(Disposables);
        SelectedBackgroundModelKey.Skip(1).Subscribe(_ => CommitSelectedBackgroundModelKey()).AddTo(Disposables);
        HookPoseCommit(PropChunkX);
        HookPoseCommit(PropChunkZ);
        HookPoseCommit(PropLocalX);
        HookPoseCommit(PropLocalY);
        HookPoseCommit(PropLocalZ);
        HookPoseCommit(PropRotX);
        HookPoseCommit(PropRotY);
        HookPoseCommit(PropRotZ);

        SplineDisplayName.Skip(1).Subscribe(_ => { if (!IsSyncingSpline) CommitSplineDisplayName(); }).AddTo(Disposables);
        SplineKey.Skip(1).Subscribe(_ => { if (!IsSyncingSpline) CommitSplineMetadata(); }).AddTo(Disposables);
        SplineTemplateKey.Skip(1).Subscribe(_ => { if (!IsSyncingSpline) CommitSplineMetadata(); }).AddTo(Disposables);
        SplinePreBuildUserCode.Skip(1).Subscribe(_ => { if (!IsSyncingSpline) CommitSplineMetadata($"Inspector.SplineCode"); }).AddTo(Disposables);
        SelectedChainJunctionTemplateKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingSpline)
            {
                RefreshSelectedSplineJunctionChainCandidates();
            }
        }).AddTo(Disposables);
        HookSplinePoseCommit(SplineChunkX);
        HookSplinePoseCommit(SplineChunkZ);
        HookSplinePoseCommit(SplineLocalX);
        HookSplinePoseCommit(SplineLocalY);
        HookSplinePoseCommit(SplineLocalZ);
        HookSplinePoseCommit(SplineRotX);
        HookSplinePoseCommit(SplineRotY);
        HookSplinePoseCommit(SplineRotZ);

        JunctionDisplayName.Skip(1).Subscribe(_ => { if (!IsSyncingJunction) CommitJunctionDisplayName(); }).AddTo(Disposables);
        JunctionKey.Skip(1).Subscribe(_ => { if (!IsSyncingJunction) CommitJunctionMetadata(); }).AddTo(Disposables);
        JunctionTemplateKey.Skip(1).Subscribe(_ => { if (!IsSyncingJunction) CommitJunctionMetadata(); }).AddTo(Disposables);
        JunctionPreBuildUserCode.Skip(1).Subscribe(_ => { if (!IsSyncingJunction) CommitJunctionMetadata("Inspector.JunctionCode"); }).AddTo(Disposables);
        HookJunctionPoseCommit(JunctionChunkX);
        HookJunctionPoseCommit(JunctionChunkZ);
        HookJunctionPoseCommit(JunctionLocalX);
        HookJunctionPoseCommit(JunctionLocalY);
        HookJunctionPoseCommit(JunctionLocalZ);
        HookJunctionPoseCommit(JunctionRotX);
        HookJunctionPoseCommit(JunctionRotY);
        HookJunctionPoseCommit(JunctionRotZ);

        SelectedJunctionPortKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingJunction)
            {
                RefreshSelectedJunctionPortTargets();
                SyncPortPickToggleStates();
            }
        }).AddTo(Disposables);
        SelectedJunctionChainTargetTemplateKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingJunction)
            {
                RefreshSelectedJunctionToJunctionTargets();
            }
        }).AddTo(Disposables);
        SelectedJunctionPathKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingJunction)
            {
                SyncSelectedJunctionPathOverride();
            }
        }).AddTo(Disposables);
        SelectedJunctionCurveKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingJunction)
            {
                SyncSelectedJunctionCurveOverride();
            }
        }).AddTo(Disposables);

        Workspace.SelectedPropId.Subscribe(_ => SyncSelectedPropFromModel()).AddTo(Disposables);
        Workspace.SelectedSplineId.Subscribe(_ =>
        {
            SyncSelectedSplineFromModel();
            SyncPortPickToggleStates();
        }).AddTo(Disposables);
        Workspace.SelectedJunctionId.Subscribe(_ =>
        {
            SyncSelectedJunctionFromModel();
            SyncPortPickToggleStates();
        }).AddTo(Disposables);
        Workspace.SelectedBackgroundModelIndex.Subscribe(_ => SyncSelectedBackgroundFromModel()).AddTo(Disposables);
        Workspace.ActiveToolMode.Subscribe(_ => SyncPortPickToggleStates()).AddTo(Disposables);
        Workspace.PendingConnectSourcePort.Subscribe(_ => SyncPortPickToggleStates()).AddTo(Disposables);
        Workspace.ProjectRevision.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Rebuild();
    }

    private void HookPoseCommit(BindableReactiveProperty<decimal?> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingProp)
            {
                CommitSelectedPropPose();
            }
        }).AddTo(Disposables);
    }

    private void CommitSelectedPropModelKey()
    {
        if (IsSyncingProp)
        {
            return;
        }

        string? propId = Workspace.SelectedPropId.Value;
        string modelKey = SelectedPropModelKey.Value;
        if (!string.IsNullOrEmpty(propId) && !string.IsNullOrWhiteSpace(modelKey))
        {
            Workspace.SetPropModelKey(propId, modelKey);
        }
    }

    private void CommitSelectedPropPose()
    {
        if (IsSyncingProp)
        {
            return;
        }

        string? propId = Workspace.SelectedPropId.Value;
        if (string.IsNullOrEmpty(propId))
        {
            return;
        }

        ChunkIndexModel chunk = new ChunkIndexModel(
            (int)(PropChunkX.Value ?? 0m),
            (int)(PropChunkZ.Value ?? 0m));

        PoseDegreesModel localPose = new PoseDegreesModel(
            (double)(PropLocalX.Value ?? 0m),
            (double)(PropLocalY.Value ?? 0m),
            (double)(PropLocalZ.Value ?? 0m),
            (double)(PropRotX.Value ?? 0m),
            (double)(PropRotY.Value ?? 0m),
            (double)(PropRotZ.Value ?? 0m));

        WorldPoseDegreesModel normalized = new WorldPoseDegreesModel(chunk, localPose).Normalized();
        Workspace.SetPropPose(propId, normalized, $"Inspector.PropPose.{propId}");
    }

    private void AddNewDefaultProp()
    {
        SceneElementCreationHelper.PlaceDefaultProp(Workspace, 0, 0, AvailableModelKeys, allowPlaceholderFallback: true);
    }

    private void AddNewDefaultBackground()
    {
        SceneElementCreationHelper.PlaceDefaultBackground(Workspace, AvailableModelKeys, allowPlaceholderFallback: true);
    }

    private void CommitSelectedBackgroundModelKey()
    {
        if (IsSyncingBackground)
        {
            return;
        }

        int bgIdx = Workspace.SelectedBackgroundModelIndex.Value;
        string modelKey = SelectedBackgroundModelKey.Value;
        if (0 <= bgIdx && !string.IsNullOrWhiteSpace(modelKey))
        {
            Workspace.SetBackgroundModelKey(bgIdx, modelKey);
        }
    }

    private static void SyncStringCollection(ObservableCollection<string> target, IList<string> next)
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

    private void Rebuild()
    {
        IsSyncingProp = true;
        IsSyncingSpline = true;
        IsSyncingBackground = true;
        IsSyncingJunction = true;
        try
        {
            DirtyFilesList.Clear();
            ProtectedFilesList.Clear();
            ExplicitConnections.Clear();
            HasExplicitConnections.Value = false;

            WorldProject? project = Workspace.CurrentProject.Value;
            if (project is null)
            {
                AvailableModelKeys.Clear();
                AvailableSplineTemplateKeys.Clear();
                AvailableJunctionTemplateKeys.Clear();
                AvailableCrossSectionKeys.Clear();
                SummaryText.Value = "ワールド未読込 (ファイル -> 新規ワールド / ワールドを開く)";
                SyncSelectedPropFromModel();
                SyncSelectedSplineFromModel();
                SyncSelectedJunctionFromModel();
                SyncSelectedBackgroundFromModel();
                return;
            }

            List<string> nextModelKeys = new();
            foreach (ModelListDocument list in project.ModelLists.Values)
            {
                foreach (ModelListEntry entry in list.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && !nextModelKeys.Contains(entry.Key))
                    {
                        nextModelKeys.Add(entry.Key);
                    }
                }
            }

            ChunkPropPlacement? activeProp = Workspace.FindProp(Workspace.SelectedPropId.Value);
            if (activeProp is not null && !string.IsNullOrWhiteSpace(activeProp.ModelKey) && !nextModelKeys.Contains(activeProp.ModelKey))
            {
                nextModelKeys.Add(activeProp.ModelKey);
            }

            int activeBgIdx = Workspace.SelectedBackgroundModelIndex.Value;
            if (0 <= activeBgIdx && activeBgIdx < project.Placement.BackgroundModels.Count)
            {
                string bgKey = project.Placement.BackgroundModels[activeBgIdx];
                if (!string.IsNullOrWhiteSpace(bgKey) && !nextModelKeys.Contains(bgKey))
                {
                    nextModelKeys.Add(bgKey);
                }
            }

            List<string> nextTemplateKeys = new();
            foreach (string key in project.SplineTemplates.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (!nextTemplateKeys.Contains(key))
                {
                    nextTemplateKeys.Add(key);
                }
            }

            SplinePlacementNode? activeSpline = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
            if (activeSpline is not null && !string.IsNullOrWhiteSpace(activeSpline.TemplateKey) && !nextTemplateKeys.Contains(activeSpline.TemplateKey))
            {
                nextTemplateKeys.Add(activeSpline.TemplateKey);
            }

            List<string> nextJunctionTemplateKeys = new();
            foreach (string key in project.JunctionTemplates.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (!nextJunctionTemplateKeys.Contains(key))
                {
                    nextJunctionTemplateKeys.Add(key);
                }
            }

            JunctionPlacementNode? activeJunction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            if (activeJunction is not null
                && !string.IsNullOrWhiteSpace(activeJunction.TemplateKey)
                && !nextJunctionTemplateKeys.Contains(activeJunction.TemplateKey))
            {
                nextJunctionTemplateKeys.Add(activeJunction.TemplateKey);
            }

            List<string> nextCrossSections = project.CrossSections.Keys
                .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SyncStringCollection(AvailableModelKeys, nextModelKeys);
            SyncStringCollection(AvailableSplineTemplateKeys, nextTemplateKeys);
            SyncStringCollection(AvailableJunctionTemplateKeys, nextJunctionTemplateKeys);
            SyncStringCollection(AvailableCrossSectionKeys, nextCrossSections);

            if (string.IsNullOrWhiteSpace(NewCurveExtrusionSectionKey.Value) && 0 < AvailableCrossSectionKeys.Count)
            {
                NewCurveExtrusionSectionKey.Value = AvailableCrossSectionKeys[0];
            }

            if (string.IsNullOrWhiteSpace(NewJunctionStaticPropModelKey.Value) && 0 < AvailableModelKeys.Count)
            {
                NewJunctionStaticPropModelKey.Value = AvailableModelKeys[0];
            }

            for (int i = 0; i < project.Placement.Connections.Count; i++)
            {
                ExplicitPortConnection conn = project.Placement.Connections[i];
                string fromPort = string.IsNullOrEmpty(conn.From.PortKey) ? conn.From.Kind.ToString() : conn.From.PortKey;
                string toPort = string.IsNullOrEmpty(conn.To.PortKey) ? conn.To.Kind.ToString() : conn.To.PortKey;
                ExplicitConnections.Add(new InspectorListEntry(
                    i,
                    $"[{i}] {conn.From.ElementId}.{fromPort} <-> {conn.To.ElementId}.{toPort}"));
            }

            HasExplicitConnections.Value = 0 < ExplicitConnections.Count;
            if (SelectedExplicitConnectionIndex.Value < 0 || ExplicitConnections.Count <= SelectedExplicitConnectionIndex.Value)
            {
                SelectedExplicitConnectionIndex.Value = 0 < ExplicitConnections.Count ? 0 : -1;
            }

            SummaryText.Value = $"タイトル: {project.WorldInfo.Title}\n著者: {project.WorldInfo.Author}\n保存先: {project.WorldDirectory}";
            foreach (Core.Commands.WorldFileTarget dirty in project.DirtyFiles)
            {
                DirtyFilesList.Add($"* [{dirty.Kind}] {dirty.RelativePath}");
            }

            foreach (string prot in project.ProtectedFiles)
            {
                ProtectedFilesList.Add($"🔒 {prot}");
            }

            SyncSelectedPropFromModel();
            SyncSelectedSplineFromModel();
            SyncSelectedJunctionFromModel();
            SyncSelectedBackgroundFromModel();
        }
        finally
        {
            IsSyncingProp = false;
            IsSyncingSpline = false;
            IsSyncingBackground = false;
            IsSyncingJunction = false;
        }
    }

    private void UpdateHasAnySelection()
    {
        HasAnySelection.Value = HasSelectedProp.Value
            || HasSelectedSpline.Value
            || HasSelectedJunction.Value
            || HasSelectedBackground.Value;
    }

    private void SyncSelectedBackgroundFromModel()
    {
        bool prevSync = IsSyncingBackground;
        IsSyncingBackground = true;
        try
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            int bgIdx = Workspace.SelectedBackgroundModelIndex.Value;
            if (project is null || bgIdx < 0 || project.Placement.BackgroundModels.Count <= bgIdx)
            {
                HasSelectedBackground.Value = false;
                SelectedBackgroundIndexText.Value = string.Empty;
                UpdateHasAnySelection();
                return;
            }

            string key = project.Placement.BackgroundModels[bgIdx];
            if (!string.IsNullOrWhiteSpace(key) && !AvailableModelKeys.Contains(key))
            {
                AvailableModelKeys.Add(key);
            }

            HasSelectedBackground.Value = true;
            UpdateHasAnySelection();
            SelectedBackgroundIndexText.Value = $"#{bgIdx}";
            SelectedBackgroundModelKey.Value = string.Empty;
            SelectedBackgroundModelKey.Value = key;
        }
        finally
        {
            IsSyncingBackground = prevSync;
        }
    }

    private void SyncSelectedPropFromModel()
    {
        bool prevSync = IsSyncingProp;
        IsSyncingProp = true;
        try
        {
            ChunkPropPlacement? prop = Workspace.FindProp(Workspace.SelectedPropId.Value);
            if (prop is null)
            {
                HasSelectedProp.Value = false;
                PropTitleText.Value = "3D プロップ";
                PropDisplayName.Value = string.Empty;
                SelectedPropIdText.Value = string.Empty;
                UpdateHasAnySelection();
                return;
            }

            if (!string.IsNullOrWhiteSpace(prop.ModelKey) && !AvailableModelKeys.Contains(prop.ModelKey))
            {
                AvailableModelKeys.Add(prop.ModelKey);
            }

            HasSelectedProp.Value = true;
            UpdateHasAnySelection();
            string? propDisplayName = string.IsNullOrWhiteSpace(prop.DisplayName) ? null : prop.DisplayName;
            PropTitleText.Value = string.IsNullOrEmpty(propDisplayName)
                ? "3D プロップ"
                : $"{propDisplayName} (Prop)";
            if (!IsCommittingProp)
            {
                PropDisplayName.Value = propDisplayName ?? string.Empty;
            }

            SelectedPropIdText.Value = prop.Id;
            SelectedPropModelKey.Value = string.Empty;
            SelectedPropModelKey.Value = prop.ModelKey;
            PropChunkX.Value = prop.Chunk.X;
            PropChunkZ.Value = prop.Chunk.Z;
            PropLocalX.Value = (decimal)double.Round(prop.Pose.X, 3);
            PropLocalY.Value = (decimal)double.Round(prop.Pose.Y, 3);
            PropLocalZ.Value = (decimal)double.Round(prop.Pose.Z, 3);
            PropRotX.Value = (decimal)double.Round(prop.Pose.RotationX, 3);
            PropRotY.Value = (decimal)double.Round(prop.Pose.RotationY, 3);
            PropRotZ.Value = (decimal)double.Round(prop.Pose.RotationZ, 3);
        }
        finally
        {
            IsSyncingProp = prevSync;
        }
    }

    private void HookSplinePoseCommit(BindableReactiveProperty<decimal?> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingSpline)
            {
                CommitSelectedSplinePose();
            }
        }).AddTo(Disposables);
    }

    private void HookJunctionPoseCommit(BindableReactiveProperty<decimal?> prop)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncingJunction)
            {
                CommitSelectedJunctionPose();
            }
        }).AddTo(Disposables);
    }

    private void SyncSelectedSplineFromModel()
    {
        bool prevSync = IsSyncingSpline;
        IsSyncingSpline = true;
        try
        {
            CompatibleConnectTargets.Clear();
            CompatibleChainJunctionPorts.Clear();
            WorldProject? project = Workspace.CurrentProject.Value;
            SplinePlacementNode? spline = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
            if (project is null || spline is null)
            {
                HasSelectedSpline.Value = false;
                SplineTitleText.Value = "スプライン (Spline)";
                SplineDisplayName.Value = string.Empty;
                SplineKey.Value = string.Empty;
                SplineOriginSummaryText.Value = string.Empty;
                IsSplineChunkPoseOrigin.Value = false;
                HasCompatibleConnectTargets.Value = false;
                HasActiveConnectBezier.Value = false;
                ConnectStatusText.Value = "未接続";
                SelectedConnectTarget.Value = null;
                CanExtendJunctionFromSplineOutlet.Value = false;
                SelectedChainJunctionPortKey.Value = string.Empty;
                UpdateHasAnySelection();
                return;
            }

            if (!string.IsNullOrWhiteSpace(spline.TemplateKey) && !AvailableSplineTemplateKeys.Contains(spline.TemplateKey))
            {
                AvailableSplineTemplateKeys.Add(spline.TemplateKey);
            }

            HasSelectedSpline.Value = true;
            UpdateHasAnySelection();
            string? splineDisplayName = Workspace.GetElementDisplayName(spline.Id);
            SplineTitleText.Value = string.IsNullOrEmpty(splineDisplayName)
                ? "スプライン (Spline)"
                : $"{splineDisplayName} (Spline)";
            if (!IsCommittingSpline)
            {
                SplineDisplayName.Value = splineDisplayName ?? string.Empty;
                SplineKey.Value = string.IsNullOrEmpty(spline.Key) ? spline.Id : spline.Key;
                SplineTemplateKey.Value = spline.TemplateKey ?? string.Empty;
                SplinePreBuildUserCode.Value = spline.PreBuildUserCode ?? string.Empty;
            }

            double totalLength = SplineAnchorUtilities.ComputeCurveTotalLength(spline);
            SplineOriginSummaryText.Value = spline.Origin.Kind switch
            {
                SplineOriginKind.ChunkPose => $"ChunkPose (全長: {totalLength:0.##}m)",
                SplineOriginKind.FromSplineOutlet => $"IntoSpline({spline.Origin.SourceNodeId}) (全長: {totalLength:0.##}m)",
                SplineOriginKind.FromJunctionPort => $"FromJunction({spline.Origin.SourceNodeId}.{spline.Origin.SourcePortKey}) (全長: {totalLength:0.##}m)",
                _ => $"{spline.Origin.Kind} ({totalLength:0.##}m)",
            };

            IsSplineChunkPoseOrigin.Value = spline.Origin.Kind == SplineOriginKind.ChunkPose;
            SplineChunkX.Value = spline.Origin.Chunk.X;
            SplineChunkZ.Value = spline.Origin.Chunk.Z;
            SplineLocalX.Value = (decimal)double.Round(spline.Origin.Pose.X, 3);
            SplineLocalY.Value = (decimal)double.Round(spline.Origin.Pose.Y, 3);
            SplineLocalZ.Value = (decimal)double.Round(spline.Origin.Pose.Z, 3);
            SplineRotX.Value = (decimal)double.Round(spline.Origin.Pose.RotationX, 3);
            SplineRotY.Value = (decimal)double.Round(spline.Origin.Pose.RotationY, 3);
            SplineRotZ.Value = (decimal)double.Round(spline.Origin.Pose.RotationZ, 3);

            if (string.IsNullOrWhiteSpace(SelectedChainJunctionTemplateKey.Value)
                || !AvailableJunctionTemplateKeys.Contains(SelectedChainJunctionTemplateKey.Value))
            {
                SelectedChainJunctionTemplateKey.Value = 0 < AvailableJunctionTemplateKeys.Count
                    ? AvailableJunctionTemplateKeys[0]
                    : string.Empty;
            }

            RefreshSelectedSplineJunctionChainCandidates();

            IReadOnlyList<CompatiblePortCandidate> candidates = SplineAnchorUtilities.FindCompatibleConnectBezierTargets(project, spline);
            foreach (CompatiblePortCandidate candidate in candidates)
            {
                CompatibleConnectTargets.Add(candidate);
            }

            HasCompatibleConnectTargets.Value = 0 < CompatibleConnectTargets.Count;
            if (spline.ConnectBezier is not null)
            {
                HasActiveConnectBezier.Value = true;
                PortReferenceModel target = spline.ConnectBezier.TargetPort;
                string portSuffix = string.IsNullOrEmpty(target.PortKey) ? target.Kind.ToString() : target.PortKey;
                ConnectStatusText.Value = $"接続先: {target.ElementId}.{portSuffix} (Scale={spline.ConnectBezier.HandleScale:0.##})";
                ConnectHandleScale.Value = (decimal)spline.ConnectBezier.HandleScale;
                ConnectOutputAnchorId.Value = spline.ConnectBezier.OutputAnchorId ?? string.Empty;
                SelectedConnectTarget.Value = CompatibleConnectTargets.FirstOrDefault(c => c.PortRef == target);
            }
            else
            {
                HasActiveConnectBezier.Value = false;
                ConnectStatusText.Value = "未接続";
                ConnectHandleScale.Value = 0.5m;
                ConnectOutputAnchorId.Value = string.Empty;
                SelectedConnectTarget.Value = 0 < CompatibleConnectTargets.Count ? CompatibleConnectTargets[0] : null;
            }
        }
        finally
        {
            IsSyncingSpline = prevSync;
        }
    }

    private void RefreshSelectedSplineJunctionChainCandidates()
    {
        bool prevSync = IsSyncingSpline;
        IsSyncingSpline = true;
        try
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            SplinePlacementNode? spline = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
            string jtKey = SelectedChainJunctionTemplateKey.Value;
            if (project is null || spline is null || string.IsNullOrWhiteSpace(jtKey))
            {
                CompatibleChainJunctionPorts.Clear();
                SelectedChainJunctionPortKey.Value = string.Empty;
                CanExtendJunctionFromSplineOutlet.Value = false;
                return;
            }

            IReadOnlyList<string> compatiblePorts = JunctionAnchorUtilities.FindCompatibleJunctionPortsForSplineOutlet(
                project,
                spline,
                jtKey);
            SyncStringCollection(CompatibleChainJunctionPorts, compatiblePorts.ToList());
            if (!CompatibleChainJunctionPorts.Contains(SelectedChainJunctionPortKey.Value))
            {
                SelectedChainJunctionPortKey.Value = 0 < CompatibleChainJunctionPorts.Count
                    ? CompatibleChainJunctionPorts[0]
                    : string.Empty;
            }

            bool outgoingOccupied = IsSplineOutletOccupiedInProject(project, spline.Id);
            CanExtendJunctionFromSplineOutlet.Value = spline.ConnectBezier is null
                && !outgoingOccupied
                && !string.IsNullOrEmpty(SelectedChainJunctionPortKey.Value);
        }
        finally
        {
            IsSyncingSpline = prevSync;
        }
    }

    private static bool IsSplineOutletOccupiedInProject(WorldProject project, string splineId)
    {
        foreach (SplinePlacementNode s in project.Placement.Splines)
        {
            if (s.Origin.Kind == SplineOriginKind.FromSplineOutlet
                && string.Equals(s.Origin.SourceNodeId, splineId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (JunctionPlacementNode j in project.Placement.Junctions)
        {
            if (j.Origin.Kind == JunctionOriginKind.FromSplineOutlet
                && string.Equals(j.Origin.SourceNodeId, splineId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (ExplicitPortConnection conn in project.Placement.Connections)
        {
            if ((conn.From.Kind == PortTargetKind.SplineOutlet && string.Equals(conn.From.ElementId, splineId, StringComparison.Ordinal))
                || (conn.To.Kind == PortTargetKind.SplineOutlet && string.Equals(conn.To.ElementId, splineId, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private void ExtendJunctionFromSelectedSplineOutlet()
    {
        string? splineId = Workspace.SelectedSplineId.Value;
        string tplKey = SelectedChainJunctionTemplateKey.Value;
        string portKey = SelectedChainJunctionPortKey.Value;
        if (string.IsNullOrEmpty(splineId) || string.IsNullOrWhiteSpace(tplKey) || string.IsNullOrWhiteSpace(portKey))
        {
            return;
        }

        Workspace.AddChainedJunctionFromSplineOutlet(splineId, tplKey, portKey);
    }

    private void SyncSelectedJunctionFromModel()
    {
        bool prevSync = IsSyncingJunction;
        IsSyncingJunction = true;
        try
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            JunctionPlacementNode? junction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            if (project is null || junction is null)
            {
                HasSelectedJunction.Value = false;
                JunctionTitleText.Value = "ジャンクション (Junction)";
                JunctionDisplayName.Value = string.Empty;
                JunctionKey.Value = string.Empty;
                JunctionOriginSummaryText.Value = string.Empty;
                IsJunctionChunkPoseOrigin.Value = false;
                AvailableJunctionPortKeys.Clear();
                CompatibleSplineTemplatesForJunctionPort.Clear();
                CompatibleJunctionPortsForJunctionChain.Clear();
                CompatibleExplicitConnectTargets.Clear();
                JunctionPathKeys.Clear();
                JunctionCurveKeys.Clear();
                JunctionCurveExtrusions.Clear();
                JunctionStaticProps.Clear();
                CanExtendSplineFromJunctionPort.Value = false;
                CanExtendJunctionFromJunctionPort.Value = false;
                HasCompatibleExplicitConnectTargets.Value = false;
                SelectedJunctionPortStatusText.Value = "状態: 未選択";
                HasActiveExplicitConnectionOnSelectedPort.Value = false;
                UpdateHasAnySelection();
                return;
            }

            if (!string.IsNullOrWhiteSpace(junction.TemplateKey) && !AvailableJunctionTemplateKeys.Contains(junction.TemplateKey))
            {
                AvailableJunctionTemplateKeys.Add(junction.TemplateKey);
            }

            HasSelectedJunction.Value = true;
            UpdateHasAnySelection();
            string? junctionDisplayName = Workspace.GetElementDisplayName(junction.Id);
            JunctionTitleText.Value = string.IsNullOrEmpty(junctionDisplayName)
                ? "ジャンクション (Junction)"
                : $"{junctionDisplayName} (Junction)";
            if (!IsCommittingJunction)
            {
                JunctionDisplayName.Value = junctionDisplayName ?? string.Empty;
                JunctionKey.Value = string.IsNullOrEmpty(junction.Key) ? junction.Id : junction.Key;
                JunctionTemplateKey.Value = junction.TemplateKey;
                JunctionPreBuildUserCode.Value = junction.PreBuildUserCode ?? string.Empty;
            }

            JunctionOriginSummaryText.Value = junction.Origin.Kind switch
            {
                JunctionOriginKind.ChunkPose => $"ChunkPose ({junction.Origin.Chunk.X}, {junction.Origin.Chunk.Z})",
                JunctionOriginKind.FromSplineOutlet => $"IntoJunction({junction.Origin.SourceNodeId}.Outlet -> {junction.Origin.TargetPortKey})",
                JunctionOriginKind.FromJunctionPort => $"FromJunction({junction.Origin.SourceNodeId}.{junction.Origin.SourcePortKey} -> {junction.Origin.TargetPortKey})",
                _ => junction.Origin.Kind.ToString(),
            };

            IsJunctionChunkPoseOrigin.Value = junction.Origin.Kind == JunctionOriginKind.ChunkPose;
            JunctionChunkX.Value = junction.Origin.Chunk.X;
            JunctionChunkZ.Value = junction.Origin.Chunk.Z;
            JunctionLocalX.Value = (decimal)double.Round(junction.Origin.Pose.X, 3);
            JunctionLocalY.Value = (decimal)double.Round(junction.Origin.Pose.Y, 3);
            JunctionLocalZ.Value = (decimal)double.Round(junction.Origin.Pose.Z, 3);
            JunctionRotX.Value = (decimal)double.Round(junction.Origin.Pose.RotationX, 3);
            JunctionRotY.Value = (decimal)double.Round(junction.Origin.Pose.RotationY, 3);
            JunctionRotZ.Value = (decimal)double.Round(junction.Origin.Pose.RotationZ, 3);

            project.JunctionTemplates.TryGetValue(junction.TemplateKey, out JunctionTemplateDocument? templateDoc);

            List<string> portKeys = templateDoc is not null
                ? templateDoc.Ports.Select(p => p.Key).ToList()
                : new List<string>();
            SyncStringCollection(AvailableJunctionPortKeys, portKeys);
            if (!AvailableJunctionPortKeys.Contains(SelectedJunctionPortKey.Value))
            {
                SelectedJunctionPortKey.Value = 0 < AvailableJunctionPortKeys.Count ? AvailableJunctionPortKeys[0] : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(SelectedJunctionChainTargetTemplateKey.Value)
                || !AvailableJunctionTemplateKeys.Contains(SelectedJunctionChainTargetTemplateKey.Value))
            {
                SelectedJunctionChainTargetTemplateKey.Value = 0 < AvailableJunctionTemplateKeys.Count
                    ? AvailableJunctionTemplateKeys[0]
                    : string.Empty;
            }

            RefreshSelectedJunctionPortTargets();

            List<string> pathKeys = templateDoc is not null
                ? templateDoc.Paths.Select(p => p.Key).ToList()
                : new List<string>();
            SyncStringCollection(JunctionPathKeys, pathKeys);
            if (!JunctionPathKeys.Contains(SelectedJunctionPathKey.Value))
            {
                SelectedJunctionPathKey.Value = 0 < JunctionPathKeys.Count ? JunctionPathKeys[0] : string.Empty;
            }

            SyncSelectedJunctionPathOverride();

            List<string> curveKeys = templateDoc is not null
                ? templateDoc.Curves.Select(c => c.Key).ToList()
                : new List<string>();
            SyncStringCollection(JunctionCurveKeys, curveKeys);
            if (!JunctionCurveKeys.Contains(SelectedJunctionCurveKey.Value))
            {
                SelectedJunctionCurveKey.Value = 0 < JunctionCurveKeys.Count ? JunctionCurveKeys[0] : string.Empty;
            }

            SyncSelectedJunctionCurveOverride();

            JunctionStaticProps.Clear();
            for (int i = 0; i < junction.Props.Count; i++)
            {
                JunctionStaticPropPlacement sp = junction.Props[i];
                JunctionStaticProps.Add(new InspectorListEntry(
                    i,
                    $"[{i}] {sp.ModelKey} @ ({sp.Pose.X:0.##}, {sp.Pose.Y:0.##}, {sp.Pose.Z:0.##})"));
            }

            if (SelectedJunctionStaticPropIndex.Value < 0 || JunctionStaticProps.Count <= SelectedJunctionStaticPropIndex.Value)
            {
                SelectedJunctionStaticPropIndex.Value = 0 < JunctionStaticProps.Count ? 0 : -1;
            }
        }
        finally
        {
            IsSyncingJunction = prevSync;
        }
    }

    private void RefreshSelectedJunctionPortTargets()
    {
        bool prevSync = IsSyncingJunction;
        IsSyncingJunction = true;
        try
        {
            CompatibleExplicitConnectTargets.Clear();
            WorldProject? project = Workspace.CurrentProject.Value;
            JunctionPlacementNode? junction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            string portKey = SelectedJunctionPortKey.Value;
            if (project is null || junction is null || string.IsNullOrWhiteSpace(portKey))
            {
                CompatibleSplineTemplatesForJunctionPort.Clear();
                SelectedChainSplineTemplateKey.Value = string.Empty;
                CanExtendSplineFromJunctionPort.Value = false;
                CompatibleJunctionPortsForJunctionChain.Clear();
                SelectedJunctionChainTargetPortKey.Value = string.Empty;
                CanExtendJunctionFromJunctionPort.Value = false;
                SelectedExplicitConnectTarget.Value = null;
                HasCompatibleExplicitConnectTargets.Value = false;
                SelectedJunctionPortStatusText.Value = "状態: ポート未選択";
                HasActiveExplicitConnectionOnSelectedPort.Value = false;
                IsSelectedJunctionPortFree.Value = false;
                return;
            }

            PortReferenceModel sourceRef = new(junction.Id, PortTargetKind.JunctionPort, portKey);
            int explicitConnIdx = JunctionAnchorUtilities.FindExplicitConnectionIndexForPort(project.Placement, sourceRef);
            if (0 <= explicitConnIdx)
            {
                ExplicitPortConnection conn = project.Placement.Connections[explicitConnIdx];
                PortReferenceModel other = conn.From == sourceRef ? conn.To : conn.From;
                string otherPortLabel = string.IsNullOrEmpty(other.PortKey) ? other.Kind.ToString() : other.PortKey;
                SelectedJunctionPortStatusText.Value = $"状態: 直接ポート接続済み -> {other.ElementId}.{otherPortLabel}";
                HasActiveExplicitConnectionOnSelectedPort.Value = true;
                IsSelectedJunctionPortFree.Value = false;
                SetJunctionConnectionMode(2);
            }
            else if (JunctionAnchorUtilities.IsJunctionPortOccupied(project.Placement, junction.Id, portKey))
            {
                SelectedJunctionPortStatusText.Value = "状態: 他ノードとの延長・連結で使用中";
                HasActiveExplicitConnectionOnSelectedPort.Value = false;
                IsSelectedJunctionPortFree.Value = false;
            }
            else
            {
                SelectedJunctionPortStatusText.Value = "状態: 未接続 (3つの接続・延長方法が利用可能)";
                HasActiveExplicitConnectionOnSelectedPort.Value = false;
                IsSelectedJunctionPortFree.Value = true;
            }

            IReadOnlyList<string> splineTemplates = JunctionAnchorUtilities.FindCompatibleSplineTemplatesForJunctionPort(
                project,
                junction,
                portKey);
            SyncStringCollection(CompatibleSplineTemplatesForJunctionPort, splineTemplates.ToList());
            if (!CompatibleSplineTemplatesForJunctionPort.Contains(SelectedChainSplineTemplateKey.Value))
            {
                SelectedChainSplineTemplateKey.Value = 0 < CompatibleSplineTemplatesForJunctionPort.Count
                    ? CompatibleSplineTemplatesForJunctionPort[0]
                    : string.Empty;
            }

            CanExtendSplineFromJunctionPort.Value = !string.IsNullOrEmpty(SelectedChainSplineTemplateKey.Value);

            RefreshSelectedJunctionToJunctionTargets();

            IReadOnlyList<CompatiblePortCandidate> explicitTargets = JunctionAnchorUtilities.FindCompatibleExplicitConnectTargets(
                project,
                sourceRef);
            foreach (CompatiblePortCandidate cand in explicitTargets)
            {
                CompatibleExplicitConnectTargets.Add(cand);
            }

            HasCompatibleExplicitConnectTargets.Value = 0 < CompatibleExplicitConnectTargets.Count;
            SelectedExplicitConnectTarget.Value = 0 < CompatibleExplicitConnectTargets.Count
                ? CompatibleExplicitConnectTargets[0]
                : null;
        }
        finally
        {
            IsSyncingJunction = prevSync;
        }
    }

    private void RefreshSelectedJunctionToJunctionTargets()
    {
        bool prevSync = IsSyncingJunction;
        IsSyncingJunction = true;
        try
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            JunctionPlacementNode? junction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            string srcPort = SelectedJunctionPortKey.Value;
            string dstTpl = SelectedJunctionChainTargetTemplateKey.Value;
            if (project is null || junction is null || string.IsNullOrWhiteSpace(srcPort) || string.IsNullOrWhiteSpace(dstTpl))
            {
                CompatibleJunctionPortsForJunctionChain.Clear();
                SelectedJunctionChainTargetPortKey.Value = string.Empty;
                CanExtendJunctionFromJunctionPort.Value = false;
                return;
            }

            IReadOnlyList<string> targetPorts = JunctionAnchorUtilities.FindCompatibleJunctionPortsForJunctionPort(
                project,
                junction,
                srcPort,
                dstTpl);
            SyncStringCollection(CompatibleJunctionPortsForJunctionChain, targetPorts.ToList());
            if (!CompatibleJunctionPortsForJunctionChain.Contains(SelectedJunctionChainTargetPortKey.Value))
            {
                SelectedJunctionChainTargetPortKey.Value = 0 < CompatibleJunctionPortsForJunctionChain.Count
                    ? CompatibleJunctionPortsForJunctionChain[0]
                    : string.Empty;
            }

            CanExtendJunctionFromJunctionPort.Value = !string.IsNullOrEmpty(SelectedJunctionChainTargetPortKey.Value);
        }
        finally
        {
            IsSyncingJunction = prevSync;
        }
    }

    private void SyncSelectedJunctionPathOverride()
    {
        bool prevSync = IsSyncingJunction;
        IsSyncingJunction = true;
        try
        {
            JunctionPlacementNode? junction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            string pathKey = SelectedJunctionPathKey.Value;
            if (junction is null || string.IsNullOrWhiteSpace(pathKey))
            {
                JunctionPathTrafficDensity.Value = 1.0m;
                JunctionPathOverrideStatusText.Value = "パス未選択";
                return;
            }

            JunctionPathOverrideModel? ov = junction.PathOverrides.FirstOrDefault(
                p => string.Equals(p.PathKey, pathKey, StringComparison.Ordinal));
            if (ov is not null && ov.TrafficDensityFactor.HasValue)
            {
                JunctionPathTrafficDensity.Value = (decimal)double.Round(ov.TrafficDensityFactor.Value, 3);
                JunctionPathOverrideStatusText.Value = $"上書き中: TrafficDensityFactor = {ov.TrafficDensityFactor.Value:0.###}";
            }
            else
            {
                JunctionPathTrafficDensity.Value = 1.0m;
                JunctionPathOverrideStatusText.Value = "未上書き (テンプレート既定値)";
            }
        }
        finally
        {
            IsSyncingJunction = prevSync;
        }
    }

    private void SyncSelectedJunctionCurveOverride()
    {
        bool prevSync = IsSyncingJunction;
        IsSyncingJunction = true;
        try
        {
            JunctionCurveExtrusions.Clear();
            JunctionPlacementNode? junction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            string curveKey = SelectedJunctionCurveKey.Value;
            if (junction is null || string.IsNullOrWhiteSpace(curveKey))
            {
                SelectedJunctionCurveExtrusionIndex.Value = -1;
                return;
            }

            JunctionCurveOverrideModel? ov = junction.CurveOverrides.FirstOrDefault(
                c => string.Equals(c.CurveKey, curveKey, StringComparison.Ordinal));
            if (ov is not null)
            {
                for (int i = 0; i < ov.Extrusions.Count; i++)
                {
                    CurveExtrusionPlacementRule ext = ov.Extrusions[i];
                    string toStr = ext.To.HasValue ? $"{ext.To.Value.Offset:0.##}m" : "終端";
                    JunctionCurveExtrusions.Add(new InspectorListEntry(
                        i,
                        $"[{i}] {ext.CrossSectionKey} ({ext.From.Offset:0.##}m .. {toStr})"));
                }
            }

            SelectedJunctionCurveExtrusionIndex.Value = 0 < JunctionCurveExtrusions.Count ? 0 : -1;
        }
        finally
        {
            IsSyncingJunction = prevSync;
        }
    }

    private void CommitJunctionDisplayName()
    {
        JunctionPlacementNode? current = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
        if (current is null)
        {
            return;
        }

        IsCommittingJunction = true;
        try
        {
            Workspace.SetJunctionDisplayName(current.Id, JunctionDisplayName.Value);
        }
        finally
        {
            IsCommittingJunction = false;
        }
    }

    private void CommitJunctionMetadata(string? mergePrefix = null)
    {
        JunctionPlacementNode? current = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
        if (current is null)
        {
            return;
        }

        string currentKey = string.IsNullOrEmpty(current.Key) ? current.Id : current.Key;
        string nextKey = JunctionKey.Value;
        if (!string.Equals(nextKey, currentKey, StringComparison.Ordinal))
        {
            IsCommittingJunction = true;
            try
            {
                if (!Workspace.RenameJunctionKey(current.Id, nextKey))
                {
                    bool prevSync = IsSyncingJunction;
                    IsSyncingJunction = true;
                    try
                    {
                        JunctionKey.Value = currentKey;
                    }
                    finally
                    {
                        IsSyncingJunction = prevSync;
                    }

                    return;
                }
            }
            finally
            {
                IsCommittingJunction = false;
            }

            current = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
            if (current is null)
            {
                return;
            }
        }

        string nextTemplateKey = string.IsNullOrWhiteSpace(JunctionTemplateKey.Value)
            ? current.TemplateKey
            : JunctionTemplateKey.Value.Trim();
        string? nextPreBuild = string.IsNullOrWhiteSpace(JunctionPreBuildUserCode.Value)
            ? null
            : JunctionPreBuildUserCode.Value;
        if (string.Equals(current.TemplateKey, nextTemplateKey, StringComparison.Ordinal)
            && string.Equals(current.PreBuildUserCode, nextPreBuild, StringComparison.Ordinal))
        {
            return;
        }

        JunctionPlacementNode draft = JunctionAnchorUtilities.DeepCloneJunctionPlacement(current);
        draft.TemplateKey = nextTemplateKey;
        draft.PreBuildUserCode = nextPreBuild;

        string? mergeKey = string.IsNullOrEmpty(mergePrefix) ? null : $"{mergePrefix}.{current.Id}";
        IsCommittingJunction = true;
        try
        {
            Workspace.ReplaceJunctionSnapshot(current.Id, draft, $"ジャンクション '{current.Id}' の属性を変更", mergeKey);
        }
        finally
        {
            IsCommittingJunction = false;
        }
    }

    private void CommitSelectedJunctionPose()
    {
        JunctionPlacementNode? current = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
        if (current is null || current.Origin.Kind != JunctionOriginKind.ChunkPose)
        {
            return;
        }

        ChunkIndexModel chunk = new((int)(JunctionChunkX.Value ?? 0m), (int)(JunctionChunkZ.Value ?? 0m));
        PoseDegreesModel localPose = new(
            (double)(JunctionLocalX.Value ?? 0m),
            (double)(JunctionLocalY.Value ?? 0m),
            (double)(JunctionLocalZ.Value ?? 0m),
            (double)(JunctionRotX.Value ?? 0m),
            (double)(JunctionRotY.Value ?? 0m),
            (double)(JunctionRotZ.Value ?? 0m));

        Workspace.SetJunctionOriginPose(current.Id, chunk, localPose, $"Inspector.JunctionPose.{current.Id}");
    }

    private void AddNewDefaultJunction()
    {
        SceneElementCreationHelper.PlaceDefaultJunction(
            Workspace,
            0,
            0,
            AvailableJunctionTemplateKeys,
            requireExplicitTemplate: false);
    }

    private void ExtendSplineFromSelectedJunctionPort()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string portKey = SelectedJunctionPortKey.Value;
        string tplKey = SelectedChainSplineTemplateKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(portKey) || string.IsNullOrWhiteSpace(tplKey))
        {
            return;
        }

        Workspace.AddChainedSplineFromJunctionPort(junctionId, portKey, tplKey);
    }

    private void ExtendJunctionFromSelectedJunctionPort()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string srcPort = SelectedJunctionPortKey.Value;
        string dstTpl = SelectedJunctionChainTargetTemplateKey.Value;
        string dstPort = SelectedJunctionChainTargetPortKey.Value;
        if (string.IsNullOrEmpty(junctionId)
            || string.IsNullOrWhiteSpace(srcPort)
            || string.IsNullOrWhiteSpace(dstTpl)
            || string.IsNullOrWhiteSpace(dstPort))
        {
            return;
        }

        Workspace.AddChainedJunctionFromJunctionPort(junctionId, srcPort, dstTpl, dstPort);
    }

    private void AddExplicitPortConnectionFromSelectedJunction()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string portKey = SelectedJunctionPortKey.Value;
        CompatiblePortCandidate? target = SelectedExplicitConnectTarget.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(portKey) || !target.HasValue)
        {
            return;
        }

        Workspace.AddExplicitPortConnection(
            new PortReferenceModel(junctionId, PortTargetKind.JunctionPort, portKey),
            target.Value.PortRef);
    }

    private void DisconnectExplicitConnectionOnSelectedJunctionPort()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string portKey = SelectedJunctionPortKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(portKey))
        {
            return;
        }

        Workspace.RemoveExplicitPortConnectionForPort(new PortReferenceModel(junctionId, PortTargetKind.JunctionPort, portKey));
    }

    private void SetJunctionConnectionMode(int mode)
    {
        IsJunctionExtendSplineMode.Value = mode == 0;
        IsJunctionChainJunctionMode.Value = mode == 1;
        IsJunctionExplicitConnectMode.Value = mode == 2;
    }

    private void SyncPortPickToggleStates()
    {
        bool isConnect = Workspace.ActiveToolMode.Value == EditorToolMode.ConnectPort;
        PortReferenceModel? pending = Workspace.PendingConnectSourcePort.Value;

        string? selectedSpline = Workspace.SelectedSplineId.Value;
        IsPickingSplineConnectTargetIn3D.Value = isConnect
            && pending is not null
            && pending.Kind == PortTargetKind.SplineOutlet
            && !string.IsNullOrEmpty(selectedSpline)
            && string.Equals(pending.ElementId, selectedSpline, StringComparison.Ordinal);

        string? selectedJunction = Workspace.SelectedJunctionId.Value;
        string selectedPort = SelectedJunctionPortKey.Value;
        IsPickingJunctionConnectTargetIn3D.Value = isConnect
            && pending is not null
            && pending.Kind == PortTargetKind.JunctionPort
            && !string.IsNullOrEmpty(selectedJunction)
            && !string.IsNullOrEmpty(selectedPort)
            && string.Equals(pending.ElementId, selectedJunction, StringComparison.Ordinal)
            && string.Equals(pending.PortKey, selectedPort, StringComparison.Ordinal);
    }

    private void ToggleSplineConnectPickIn3D()
    {
        string? splineId = Workspace.SelectedSplineId.Value;
        if (string.IsNullOrEmpty(splineId))
        {
            return;
        }

        PortReferenceModel sourceRef = new(splineId, PortTargetKind.SplineOutlet, null);
        if (IsPickingSplineConnectTargetIn3D.Value)
        {
            Workspace.PendingConnectSourcePort.Value = null;
            Workspace.ActiveToolMode.Value = EditorToolMode.Select;
            return;
        }

        Workspace.PendingConnectSourcePort.Value = sourceRef;
        Workspace.ActiveToolMode.Value = EditorToolMode.ConnectPort;
        Workspace.LastStatusMessage.Value = $"接続先ポートを 3D ビューでクリックしてください ({splineId}.Outlet / Escで解除)。";
        Workspace.RequestActivateViewport();
    }

    private void ToggleJunctionConnectPickIn3D()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string portKey = SelectedJunctionPortKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(portKey))
        {
            return;
        }

        if (IsPickingJunctionConnectTargetIn3D.Value)
        {
            Workspace.PendingConnectSourcePort.Value = null;
            Workspace.ActiveToolMode.Value = EditorToolMode.Select;
            return;
        }

        if (!IsSelectedJunctionPortFree.Value)
        {
            Workspace.LastStatusMessage.Value = $"ポート '{junctionId}.{portKey}' はすでに接続されています。先に接続を解除してください。";
            return;
        }

        PortReferenceModel sourceRef = new(junctionId, PortTargetKind.JunctionPort, portKey);
        Workspace.PendingConnectSourcePort.Value = sourceRef;
        Workspace.ActiveToolMode.Value = EditorToolMode.ConnectPort;
        Workspace.LastStatusMessage.Value = $"接続先ポートを 3D ビューでクリックしてください ({junctionId}.{portKey} / Escで解除)。";
        Workspace.RequestActivateViewport();
    }

    private void ApplySelectedJunctionPathOverride()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string pathKey = SelectedJunctionPathKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(pathKey))
        {
            return;
        }

        double density = double.Clamp((double)(JunctionPathTrafficDensity.Value ?? 1.0m), 0.0, 100.0);
        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' パス '{pathKey}' の TrafficDensityFactor を設定",
            null,
            draft =>
            {
                JunctionPathOverrideModel? existing = draft.PathOverrides.FirstOrDefault(
                    p => string.Equals(p.PathKey, pathKey, StringComparison.Ordinal));
                if (existing is null)
                {
                    existing = new JunctionPathOverrideModel { PathKey = pathKey };
                    draft.PathOverrides.Add(existing);
                }

                existing.TrafficDensityFactor = density;
            });
    }

    private void RemoveSelectedJunctionPathOverride()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string pathKey = SelectedJunctionPathKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(pathKey))
        {
            return;
        }

        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' パス '{pathKey}' の上書きを解除",
            null,
            draft =>
            {
                for (int i = draft.PathOverrides.Count - 1; 0 <= i; i--)
                {
                    if (string.Equals(draft.PathOverrides[i].PathKey, pathKey, StringComparison.Ordinal))
                    {
                        draft.PathOverrides.RemoveAt(i);
                    }
                }
            });
    }

    private void AddSelectedJunctionCurveExtrusion()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string curveKey = SelectedJunctionCurveKey.Value;
        string sectionKey = NewCurveExtrusionSectionKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(curveKey) || string.IsNullOrWhiteSpace(sectionKey))
        {
            return;
        }

        double fromDist = double.Max(0.0, (double)(NewCurveExtrusionFrom.Value ?? 0m));
        double toDist = double.Max(fromDist, (double)(NewCurveExtrusionTo.Value ?? 5m));
        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' 補助曲線 '{curveKey}' に断面押し出しを追加",
            null,
            draft =>
            {
                JunctionCurveOverrideModel? existing = draft.CurveOverrides.FirstOrDefault(
                    c => string.Equals(c.CurveKey, curveKey, StringComparison.Ordinal));
                if (existing is null)
                {
                    existing = new JunctionCurveOverrideModel { CurveKey = curveKey };
                    draft.CurveOverrides.Add(existing);
                }

                existing.Extrusions.Add(new CurveExtrusionPlacementRule
                {
                    CrossSectionKey = sectionKey,
                    From = AnchoredDistance.Absolute(fromDist),
                    To = AnchoredDistance.Absolute(toDist),
                });
            });
    }

    private void RemoveSelectedJunctionCurveExtrusion()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string curveKey = SelectedJunctionCurveKey.Value;
        int idx = SelectedJunctionCurveExtrusionIndex.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(curveKey) || idx < 0)
        {
            return;
        }

        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' 補助曲線 '{curveKey}' の断面押し出しを削除",
            null,
            draft =>
            {
                JunctionCurveOverrideModel? existing = draft.CurveOverrides.FirstOrDefault(
                    c => string.Equals(c.CurveKey, curveKey, StringComparison.Ordinal));
                if (existing is not null && 0 <= idx && idx < existing.Extrusions.Count)
                {
                    existing.Extrusions.RemoveAt(idx);
                    if (existing.Extrusions.Count == 0 && existing.Props.Count == 0)
                    {
                        draft.CurveOverrides.Remove(existing);
                    }
                }
            });
    }

    private void AddSelectedJunctionStaticProp()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        string modelKey = NewJunctionStaticPropModelKey.Value;
        if (string.IsNullOrEmpty(junctionId) || string.IsNullOrWhiteSpace(modelKey))
        {
            return;
        }

        PoseDegreesModel pose = PoseDegreesModel.FromTranslation(
            (double)(NewJunctionStaticPropX.Value ?? 0m),
            (double)(NewJunctionStaticPropY.Value ?? 0m),
            (double)(NewJunctionStaticPropZ.Value ?? 0m));

        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' に静的プロップ '{modelKey}' を追加",
            null,
            draft => draft.Props.Add(new JunctionStaticPropPlacement(modelKey.Trim(), pose)));
    }

    private void RemoveSelectedJunctionStaticProp()
    {
        string? junctionId = Workspace.SelectedJunctionId.Value;
        int idx = SelectedJunctionStaticPropIndex.Value;
        if (string.IsNullOrEmpty(junctionId) || idx < 0)
        {
            return;
        }

        Workspace.MutateJunctionPlacement(
            junctionId,
            $"ジャンクション '{junctionId}' の静的プロップを削除",
            null,
            draft =>
            {
                if (0 <= idx && idx < draft.Props.Count)
                {
                    draft.Props.RemoveAt(idx);
                }
            });
    }

    private void CommitPropDisplayName()
    {
        ChunkPropPlacement? current = Workspace.FindProp(Workspace.SelectedPropId.Value);
        if (current is null)
        {
            return;
        }

        IsCommittingProp = true;
        try
        {
            Workspace.SetPropDisplayName(current.Id, PropDisplayName.Value);
        }
        finally
        {
            IsCommittingProp = false;
        }
    }

    private void CommitSplineDisplayName()
    {
        SplinePlacementNode? current = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
        if (current is null)
        {
            return;
        }

        IsCommittingSpline = true;
        try
        {
            Workspace.SetSplineDisplayName(current.Id, SplineDisplayName.Value);
        }
        finally
        {
            IsCommittingSpline = false;
        }
    }

    private void CommitSplineMetadata(string? mergePrefix = null)
    {
        SplinePlacementNode? current = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
        if (current is null)
        {
            return;
        }

        string currentKey = string.IsNullOrEmpty(current.Key) ? current.Id : current.Key;
        string nextKey = SplineKey.Value;
        if (!string.Equals(nextKey, currentKey, StringComparison.Ordinal))
        {
            IsCommittingSpline = true;
            try
            {
                if (!Workspace.RenameSplineKey(current.Id, nextKey))
                {
                    bool prevSync = IsSyncingSpline;
                    IsSyncingSpline = true;
                    try
                    {
                        SplineKey.Value = currentKey;
                    }
                    finally
                    {
                        IsSyncingSpline = prevSync;
                    }

                    return;
                }
            }
            finally
            {
                IsCommittingSpline = false;
            }

            current = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
            if (current is null)
            {
                return;
            }
        }

        string? nextTemplateKey = string.IsNullOrWhiteSpace(SplineTemplateKey.Value)
            ? null
            : SplineTemplateKey.Value.Trim();
        string? nextPreBuild = string.IsNullOrWhiteSpace(SplinePreBuildUserCode.Value)
            ? null
            : SplinePreBuildUserCode.Value;
        if (string.Equals(current.TemplateKey, nextTemplateKey, StringComparison.Ordinal)
            && string.Equals(current.PreBuildUserCode, nextPreBuild, StringComparison.Ordinal))
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        draft.TemplateKey = nextTemplateKey;
        draft.PreBuildUserCode = nextPreBuild;

        string? mergeKey = string.IsNullOrEmpty(mergePrefix) ? null : $"{mergePrefix}.{current.Id}";
        IsCommittingSpline = true;
        try
        {
            Workspace.ReplaceSplineSnapshot(current.Id, draft, $"スプライン '{current.Id}' の属性を変更", mergeKey);
        }
        finally
        {
            IsCommittingSpline = false;
        }
    }

    private void CommitSelectedSplinePose()
    {
        SplinePlacementNode? current = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
        if (current is null || current.Origin.Kind != SplineOriginKind.ChunkPose)
        {
            return;
        }

        ChunkIndexModel chunk = new((int)(SplineChunkX.Value ?? 0m), (int)(SplineChunkZ.Value ?? 0m));
        PoseDegreesModel localPose = new(
            (double)(SplineLocalX.Value ?? 0m),
            (double)(SplineLocalY.Value ?? 0m),
            (double)(SplineLocalZ.Value ?? 0m),
            (double)(SplineRotX.Value ?? 0m),
            (double)(SplineRotY.Value ?? 0m),
            (double)(SplineRotZ.Value ?? 0m));

        Workspace.SetSplineOriginPose(current.Id, chunk, localPose, $"Inspector.SplinePose.{current.Id}");
    }

    private void AddNewDefaultSpline()
    {
        SceneElementCreationHelper.PlaceDefaultSpline(Workspace, 0, 0, AvailableSplineTemplateKeys);
    }

    private void ClearSelectedSplineTemplate()
    {
        SplinePlacementNode? current = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
        if (current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        draft.TemplateKey = null;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, $"スプライン '{current.Id}' のテンプレートを解除");
    }

    private void ApplySelectedConnectBezier()
    {
        string? splineId = Workspace.SelectedSplineId.Value;
        CompatiblePortCandidate? candidate = SelectedConnectTarget.Value;
        if (string.IsNullOrEmpty(splineId) || !candidate.HasValue)
        {
            return;
        }

        double scale = double.Clamp((double)(ConnectHandleScale.Value ?? 0.5m), 0.05, 5.0);
        string? anchorId = string.IsNullOrWhiteSpace(ConnectOutputAnchorId.Value)
            ? null
            : ConnectOutputAnchorId.Value.Trim();
        if (!string.IsNullOrEmpty(anchorId))
        {
            SplinePlacementNode? currentSpline = Workspace.FindSpline(splineId);
            string? rejectionMessage = null;
            if (!CSharpIdentifierUtilities.TryValidateAnchorId(anchorId, out string formatError))
            {
                rejectionMessage = formatError;
            }
            else if (currentSpline is not null && SplineDefinesAnchorInSegments(currentSpline, anchorId))
            {
                rejectionMessage = $"同一スプライン内でアンカー '{anchorId}' が重複しています。別の名前を指定してください。";
            }

            if (rejectionMessage is not null)
            {
                ConnectOutputAnchorId.Value = currentSpline?.ConnectBezier?.OutputAnchorId ?? string.Empty;
                Workspace.LastStatusMessage.Value = rejectionMessage;
                return;
            }
        }

        Workspace.SetSplineConnectBezier(
            splineId,
            new SplineConnectBezierModel(candidate.Value.PortRef, scale, anchorId));
    }

    private static bool SplineDefinesAnchorInSegments(SplinePlacementNode node, string candidate)
    {
        for (int i = 0; i < node.Curves.Count; i++)
        {
            if (string.Equals(node.Curves[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Gradients.Count; i++)
        {
            if (string.Equals(node.Gradients[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Cants.Count; i++)
        {
            if (string.Equals(node.Cants[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ClearSelectedConnectBezier()
    {
        string? splineId = Workspace.SelectedSplineId.Value;
        if (!string.IsNullOrEmpty(splineId))
        {
            Workspace.SetSplineConnectBezier(splineId, null);
        }
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}

/// <summary>
/// Row item in <see cref="DiagnosticsToolViewModel"/> unifying parse diagnostics, validation issues, and runtime errors.
/// </summary>
public sealed record DiagnosticEntryViewModel(
    string SeverityLabel,
    string Source,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? ElementId = null);

/// <summary>
/// Tool ViewModel displaying parse diagnostics, tampering protection warnings, static validation issues,
/// Mode 2 script verification errors, and live D3D11/model runtime errors.
/// </summary>
public sealed class DiagnosticsToolViewModel : Tool, IDisposable
{
    private readonly WorldWorkspaceService Workspace;
    private readonly CompositeDisposable Disposables = new CompositeDisposable();

    public ObservableCollection<DiagnosticEntryViewModel> Items { get; } = new ObservableCollection<DiagnosticEntryViewModel>();
    public BindableReactiveProperty<string> HeaderSummary { get; }
    public BindableReactiveProperty<DiagnosticEntryViewModel?> SelectedEntry { get; }

    public ReactiveCommand<Unit> NavigateSelectedCommand { get; }
    public ReactiveCommand<Unit> OpenSelectedExternalCommand { get; }

    public event Action<DiagnosticEntryViewModel>? NavigationRequested;

    public DiagnosticsToolViewModel(WorldWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        HeaderSummary = new BindableReactiveProperty<string>("診断: エラー 0 件").AddTo(Disposables);
        SelectedEntry = new BindableReactiveProperty<DiagnosticEntryViewModel?>(null).AddTo(Disposables);

        NavigateSelectedCommand = new ReactiveCommand<Unit>(_ =>
        {
            if (SelectedEntry.Value is DiagnosticEntryViewModel entry)
            {
                NavigateToEntry(entry);
            }
        }).AddTo(Disposables);
        OpenSelectedExternalCommand = new ReactiveCommand<Unit>(_ => OpenSelectedExternal()).AddTo(Disposables);

        Workspace.ParseDiagnostics.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Workspace.ValidationIssues.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Workspace.RuntimeErrors.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Workspace.VerificationErrors.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Workspace.BackgroundScriptDiagnostics.Subscribe(_ => Rebuild()).AddTo(Disposables);
        Rebuild();
    }

    public void NavigateToEntry(DiagnosticEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        NavigationRequested?.Invoke(entry);
    }

    public void OpenSelectedExternal()
    {
        DiagnosticEntryViewModel? entry = SelectedEntry.Value;
        if (entry is null)
        {
            Workspace.LastStatusMessage.Value = "診断項目を選択してください。";
            return;
        }

        string? rawPath = entry.FilePath;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            TryParseLocation(entry.Source, out rawPath, out _, out _);
        }

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            Workspace.LastStatusMessage.Value = "外部エディタで開けるソースファイルパスがありません。";
            return;
        }

        string resolvedPath = rawPath;
        WorldProject? project = Workspace.CurrentProject.Value;
        if (!Path.IsPathRooted(resolvedPath) && project is not null && !string.IsNullOrWhiteSpace(project.WorldDirectory))
        {
            string combined = Path.GetFullPath(Path.Combine(project.WorldDirectory, resolvedPath));
            if (File.Exists(combined))
            {
                resolvedPath = combined;
            }
            else if (string.Equals(Path.GetFileName(resolvedPath), Path.GetFileName(project.WorldInfoFilePath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(project.WorldInfoFilePath))
            {
                resolvedPath = project.WorldInfoFilePath;
            }
            else
            {
                resolvedPath = combined;
            }
        }

        if (!PlayerLauncherService.OpenInExternalEditor(resolvedPath, out string? errorMessage)
            && !string.IsNullOrEmpty(errorMessage))
        {
            Workspace.LastStatusMessage.Value = errorMessage;
        }
    }

    private void Rebuild()
    {
        Items.Clear();
        int errorCount = 0;
        int verificationCount = Workspace.VerificationErrors.Value.Count;
        WorldProject? project = Workspace.CurrentProject.Value;

        foreach (ParseDiagnostic pd in Workspace.ParseDiagnostics.Value)
        {
            if (pd.Severity == DiagnosticSeverity.Error)
            {
                errorCount++;
            }

            Items.Add(new DiagnosticEntryViewModel(
                pd.Severity.ToString(),
                $"{pd.FilePath}({pd.Line},{pd.Column})",
                pd.Message,
                pd.FilePath,
                pd.Line,
                pd.Column,
                TryExtractElementId(project, pd.FilePath, pd.Message)));
        }

        foreach (ValidationIssue vi in Workspace.ValidationIssues.Value)
        {
            if (vi.Severity == ValidationSeverity.Error)
            {
                errorCount++;
            }

            TryParseLocation(vi.SourcePath, out string? parsedPath, out int line, out int col);
            Items.Add(new DiagnosticEntryViewModel(
                vi.Severity.ToString(),
                vi.SourcePath ?? vi.Code,
                $"[{vi.Code}] {vi.Message}",
                parsedPath ?? vi.SourcePath,
                line,
                col,
                TryExtractElementId(project, vi.SourcePath, vi.Message)));
        }

        foreach (TransportX.Diagnostics.Error re in Workspace.RuntimeErrors.Value)
        {
            if (re.Level == TransportX.Diagnostics.ErrorLevel.Error || re.Level == TransportX.Diagnostics.ErrorLevel.Fatal)
            {
                errorCount++;
            }

            TryParseLocation(re.Location, out string? parsedPath, out int line, out int col);
            if (line <= 0 && 0 < re.LineNumber)
            {
                line = re.LineNumber;
                col = re.LinePosition;
            }

            string sourceLabel = re.Location ?? "Viewport/Runtime";
            if (0 < re.LineNumber && !string.IsNullOrWhiteSpace(re.Location))
            {
                sourceLabel = $"{re.Location}({re.LineNumber},{re.LinePosition})";
            }

            Items.Add(new DiagnosticEntryViewModel(
                re.Level.ToString(),
                sourceLabel,
                re.Message,
                parsedPath,
                line,
                col,
                TryExtractElementId(project, re.Location, re.Message)));
        }

        foreach (TransportX.Diagnostics.Error ve in Workspace.VerificationErrors.Value)
        {
            if (ve.Level == TransportX.Diagnostics.ErrorLevel.Error || ve.Level == TransportX.Diagnostics.ErrorLevel.Fatal)
            {
                errorCount++;
            }

            TryParseLocation(ve.Location, out string? parsedPath, out int line, out int col);
            if (line <= 0 && 0 < ve.LineNumber)
            {
                line = ve.LineNumber;
                col = ve.LinePosition;
            }

            string baseLoc = string.IsNullOrWhiteSpace(ve.Location)
                ? string.Empty
                : (0 < ve.LineNumber ? $"{ve.Location}({ve.LineNumber},{ve.LinePosition})" : ve.Location);
            string sourceLabel = string.IsNullOrWhiteSpace(baseLoc) ? "[検証]" : $"[検証] {baseLoc}";
            Items.Add(new DiagnosticEntryViewModel(
                ve.Level.ToString(),
                sourceLabel,
                ve.Message,
                parsedPath,
                line,
                col,
                TryExtractElementId(project, ve.Location, ve.Message)));
        }

        int scriptDiagCount = 0;
        if (verificationCount == 0)
        {
            foreach (TransportX.Diagnostics.Error se in Workspace.BackgroundScriptDiagnostics.Value)
            {
                scriptDiagCount++;
                if (se.Level == TransportX.Diagnostics.ErrorLevel.Error || se.Level == TransportX.Diagnostics.ErrorLevel.Fatal)
                {
                    errorCount++;
                }

                TryParseLocation(se.Location, out string? parsedPath, out int line, out int col);
                if (line <= 0 && 0 < se.LineNumber)
                {
                    line = se.LineNumber;
                    col = se.LinePosition;
                }

                string baseLoc = string.IsNullOrWhiteSpace(se.Location)
                    ? string.Empty
                    : (0 < se.LineNumber ? $"{se.Location}({se.LineNumber},{se.LinePosition})" : se.Location);
                string sourceLabel = string.IsNullOrWhiteSpace(baseLoc) ? "[スクリプト]" : $"[スクリプト] {baseLoc}";
                string codePrefix = string.IsNullOrWhiteSpace(se.Code) ? string.Empty : $"[{se.Code}] ";
                Items.Add(new DiagnosticEntryViewModel(
                    se.Level.ToString(),
                    sourceLabel,
                    $"{codePrefix}{se.Message}",
                    parsedPath,
                    line,
                    col,
                    TryExtractElementId(project, se.Location, se.Message)));
            }
        }

        Title = 0 < Items.Count ? $"診断 / エラー ({Items.Count})" : "診断 / エラー";
        if (0 < verificationCount)
        {
            HeaderSummary.Value = $"合計 {Items.Count} 件 (エラー: {errorCount} 件 / 検証: {verificationCount} 件)";
        }
        else if (0 < scriptDiagCount)
        {
            HeaderSummary.Value = $"合計 {Items.Count} 件 (エラー: {errorCount} 件 / スクリプト: {scriptDiagCount} 件)";
        }
        else
        {
            HeaderSummary.Value = $"合計 {Items.Count} 件 (エラー: {errorCount} 件)";
        }
    }

    private static bool TryParseLocation(string? location, out string? filePath, out int line, out int column)
    {
        filePath = null;
        line = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        string trimmed = location.Trim();
        if (trimmed.StartsWith("[検証]", StringComparison.Ordinal))
        {
            trimmed = trimmed["[検証]".Length..].Trim();
        }
        else if (trimmed.StartsWith("[スクリプト]", StringComparison.Ordinal))
        {
            trimmed = trimmed["[スクリプト]".Length..].Trim();
        }

        int openParen = trimmed.LastIndexOf('(');
        if (0 < openParen && openParen < trimmed.Length - 2 && trimmed.EndsWith(')'))
        {
            string coords = trimmed[(openParen + 1)..^1];
            int commaIdx = coords.IndexOf(',');
            if (0 < commaIdx)
            {
                string lineStr = coords[..commaIdx].Trim();
                string colStr = coords[(commaIdx + 1)..].Trim();
                if (int.TryParse(lineStr, out int parsedLine) && int.TryParse(colStr, out int parsedCol))
                {
                    filePath = trimmed[..openParen].Trim();
                    line = parsedLine;
                    column = parsedCol;
                    return !string.IsNullOrEmpty(filePath);
                }
            }
            else if (int.TryParse(coords.Trim(), out int singleLine))
            {
                filePath = trimmed[..openParen].Trim();
                line = singleLine;
                return !string.IsNullOrEmpty(filePath);
            }
        }

        int colonIdx = trimmed.LastIndexOf(':');
        if (1 < colonIdx && colonIdx < trimmed.Length - 1 && int.TryParse(trimmed[(colonIdx + 1)..], out int colonLine))
        {
            filePath = trimmed[..colonIdx].Trim();
            line = colonLine;
            return !string.IsNullOrEmpty(filePath);
        }

        if (!string.Equals(trimmed, "Viewport/Runtime", StringComparison.Ordinal)
            && (trimmed.Contains('.') || trimmed.Contains('/') || trimmed.Contains('\\')))
        {
            filePath = trimmed;
            return true;
        }

        return false;
    }

    private static string? TryExtractElementId(WorldProject? project, string? source, string? message)
    {
        if (project is null)
        {
            return null;
        }

        foreach (ChunkPropPlacement prop in project.Placement.Props)
        {
            if (ContainsWholeIdentifier(message, prop.Id) || ContainsWholeIdentifier(source, prop.Id))
            {
                return prop.Id;
            }
        }

        foreach (SplinePlacementNode spline in project.Placement.Splines)
        {
            if (ContainsWholeIdentifier(message, spline.Id) || ContainsWholeIdentifier(source, spline.Id))
            {
                return spline.Id;
            }
        }

        foreach (JunctionPlacementNode junction in project.Placement.Junctions)
        {
            if (ContainsWholeIdentifier(message, junction.Id) || ContainsWholeIdentifier(source, junction.Id))
            {
                return junction.Id;
            }
        }

        return null;
    }

    private static bool ContainsWholeIdentifier(string? text, string identifier)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(identifier, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool leftBoundary = idx == 0 || (!char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_');
            int endIdx = idx + identifier.Length;
            bool rightBoundary = endIdx == text.Length || (!char.IsLetterOrDigit(text[endIdx]) && text[endIdx] != '_');
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = endIdx;
        }

        return false;
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}
