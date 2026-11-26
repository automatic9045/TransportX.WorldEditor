using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using R3;
using TransportX.Cameras;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Verification;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.WorldEditor.Services;
using Vanara.PInvoke;
using Vortice.Mathematics;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Hosts a Win32 child <c>HWND</c> driven by <see cref="EditorViewportRenderer"/> (Direct3D 11)
/// inside Avalonia's docking layout, routing native pointer/keyboard input to the 3D camera,
/// raycast picker, and translation/rotation gizmos.
/// </summary>
public sealed class Win32HwndViewportHost : NativeControlHost, IViewportHost
{
    private const string WindowClassName = "TransportXWorldEditorViewportHwnd";
    private const uint WindowClassStyle = 0u;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_CANCELMODE = 0x001F;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_RBUTTONDBLCLK = 0x0206;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MBUTTONDBLCLK = 0x0209;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_CAPTURECHANGED = 0x0215;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_DELETE = 0x2E;
    private const int VK_A = 0x41;
    private const int VK_D = 0x44;
    private const int VK_E = 0x45;
    private const int VK_F = 0x46;
    private const int VK_N = 0x4E;
    private const int VK_O = 0x4F;
    private const int VK_Q = 0x51;
    private const int VK_S = 0x53;
    private const int VK_W = 0x57;
    private const int VK_Y = 0x59;
    private const int VK_Z = 0x5A;
    private const int VK_F5 = 0x74;

    private static User32.WindowProc? StaticWndProcDelegate;
    private static readonly ConcurrentDictionary<nint, WeakReference<Win32HwndViewportHost>> HwndMap =
        new ConcurrentDictionary<nint, WeakReference<Win32HwndViewportHost>>();
    private static bool WindowClassRegistered;
    private static readonly object ClassRegLock = new object();

    public static readonly StyledProperty<WorldWorkspaceService?> WorkspaceProperty =
        AvaloniaProperty.Register<Win32HwndViewportHost, WorldWorkspaceService?>(nameof(Workspace));

    private readonly Stopwatch FrameWatch = new Stopwatch();
    private DispatcherTimer? RenderTimer;
    private CompositeDisposable? WorkspaceSubscriptions;
    private WorldWorkspaceService? SubscribedWorkspace;
    private WorldProject? LastLoadedProject;
    private bool SuppressNextRevisionSync;
    private bool IsRenderingFrame;
    private bool IsStartingVerificationLocal;
    private CancellationTokenSource? VerificationStartCts;
    private Task? InFlightVerificationStartTask;
    private string LastPublishedStatusText = string.Empty;
    private int LastPublishedVerificationErrorCount;
    private TopLevel? HookedTopLevel;
    private Win32Properties.CustomWndProcHookCallback? TopLevelWndProcHook;

    private User32.SafeHWND? ChildHwndHandle;
    private nint ChildHwnd;
    private EditorViewportRenderer? Renderer;

    private bool IsLeftDown;
    private bool IsRightDown;
    private bool IsMiddleDown;
    private int LastMouseX;
    private int LastMouseY;

    // Active gizmo drag state
    private bool IsDraggingGizmo;
    private GizmoAxis ActiveGizmoAxis = GizmoAxis.None;
    private GizmoAxis HoveredGizmoAxis = GizmoAxis.None;
    private string? DraggingPropId;
    private string? DraggingSplineId;
    private string? DraggingJunctionId;
    private string DragStrokeId = string.Empty;
    private WorldPoseDegreesModel DragStartPose;
    private Vector3 DragGizmoOriginRelCamera;
    private float DragStartAxisParam;
    private float DragStartYawAngleDeg;
    private Vector3? PlacementCursorRelCamera;

    public WorldWorkspaceService? Workspace
    {
        get => GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    public int PixelWidth => Renderer?.PixelWidth ?? Math.Max(1, (int)Bounds.Width);
    public int PixelHeight => Renderer?.PixelHeight ?? Math.Max(1, (int)Bounds.Height);
    bool IViewportHost.IsInitialized => Renderer is not null;
    public ViewportOverlayFlags OverlayFlags
    {
        get => Workspace?.OverlayFlags.Value ?? ViewportOverlayFlags.Default;
        set
        {
            if (Workspace is not null)
            {
                Workspace.OverlayFlags.Value = value;
            }
        }
    }

    public event EventHandler? ViewportResized;

    static Win32HwndViewportHost()
    {
        WorkspaceProperty.Changed.AddClassHandler<Win32HwndViewportHost>((ctrl, _) => ctrl.BindWorkspace());
    }

    public void RequestRedraw()
    {
        RenderSingleTick();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (OperatingSystem.IsWindows() && change.Property == IsVisibleProperty && ChildHwnd != 0)
        {
            User32.ShowWindow(
                new HWND(ChildHwnd),
                IsEffectivelyVisible ? ShowWindowCommand.SW_SHOWNOACTIVATE : ShowWindowCommand.SW_HIDE);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        EnsureWindowClassRegistered();
        (int initW, int initH) = GetPhysicalPixelSize();

        Kernel32.SafeHINSTANCE hInstance = Kernel32.GetModuleHandle(null);
        ChildHwndHandle = User32.CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            User32.WindowStyles.WS_CHILD
            | User32.WindowStyles.WS_VISIBLE
            | User32.WindowStyles.WS_CLIPSIBLINGS
            | User32.WindowStyles.WS_CLIPCHILDREN,
            0,
            0,
            initW,
            initH,
            new HWND(parent.Handle),
            HMENU.NULL,
            hInstance,
            nint.Zero);

        ChildHwnd = ChildHwndHandle.IsInvalid ? 0 : ChildHwndHandle.DangerousGetHandle();
        if (ChildHwnd == 0)
        {
            ChildHwndHandle.Dispose();
            ChildHwndHandle = null;
            return base.CreateNativeControlCore(parent);
        }

        HwndMap[ChildHwnd] = new WeakReference<Win32HwndViewportHost>(this);

        try
        {
            Renderer = new EditorViewportRenderer(ChildHwnd, initW, initH);
            Renderer.ErrorCollector.Reported += OnRendererErrorReported;
        }
        catch (Exception ex)
        {
            if (Workspace is not null)
            {
                Workspace.LastStatusMessage.Value = $"Direct3D 11 初期化エラー: {ex.Message}";
            }
        }

        BindWorkspace();
        AttachTopLevelWndProcHook();
        StartRenderLoop();

        return new PlatformHandle(ChildHwnd, "HWND");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (OperatingSystem.IsWindows())
        {
            AttachTopLevelWndProcHook();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            DetachTopLevelWndProcHook();
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows())
        {
            DetachTopLevelWndProcHook();
        }

        StopRenderLoop();
        CancelInFlightVerificationStart();

        if (Renderer is not null && Workspace?.CurrentProject.Value is WorldProject activeProject)
        {
            Workspace.SaveCameraPose(activeProject, Renderer.CameraController.CurrentCameraPose);
        }

        UnbindWorkspace();

        if (Renderer is not null)
        {
            Renderer.ErrorCollector.Reported -= OnRendererErrorReported;
            Renderer.Dispose();
            Renderer = null;
        }

        if (ChildHwnd != 0)
        {
            HwndMap.TryRemove(ChildHwnd, out _);
            ChildHwnd = 0;
        }

        if (OperatingSystem.IsWindows() && ChildHwndHandle is not null)
        {
            ChildHwndHandle.Dispose();
            ChildHwndHandle = null;
        }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            DetachTopLevelWndProcHook();
        }

        StopRenderLoop();
        CancelInFlightVerificationStart();
        UnbindWorkspace();

        if (Renderer is not null)
        {
            Renderer.ErrorCollector.Reported -= OnRendererErrorReported;
            Renderer.Dispose();
            Renderer = null;
        }

        if (ChildHwnd != 0)
        {
            HwndMap.TryRemove(ChildHwnd, out _);
            ChildHwnd = 0;
        }

        if (OperatingSystem.IsWindows() && ChildHwndHandle is not null)
        {
            ChildHwndHandle.Dispose();
            ChildHwndHandle = null;
        }
    }

    private void OnRendererErrorReported(object? sender, TransportX.Diagnostics.ErrorEventArgs e)
    {
        if (Renderer is not null && Workspace is not null)
        {
            IReadOnlyList<TransportX.Diagnostics.Error> snapshot = Renderer.ErrorCollector.Errors;
            Dispatcher.UIThread.Post(() => Workspace.SetRuntimeErrors(snapshot));
        }
    }

    private void BindWorkspace()
    {
        UnbindWorkspace();

        WorldWorkspaceService? ws = Workspace;
        if (ws is null)
        {
            return;
        }

        SubscribedWorkspace = ws;
        WorkspaceSubscriptions = new CompositeDisposable();
        LastLoadedProject = null;

        ws.ProjectRevision.Subscribe(_ => OnWorkspaceProjectRevisionChanged()).AddTo(WorkspaceSubscriptions);
        ws.FocusCameraRequested += OnFocusCameraRequested;
        ws.ChunkJumpRequested += OnChunkJumpRequested;
        ws.StartVerificationRequested += OnStartVerificationRequested;
        ws.CancelVerificationStartRequested += OnCancelVerificationStartRequested;
        ws.StopVerificationRequested += OnStopVerificationRequested;
        ws.ApplyCameraBookmarkRequested += OnApplyCameraBookmarkRequested;
        ws.ResolveSplineFocusPose = ResolveSplineFocusWorldPose;
        ws.ResolveJunctionFocusPose = ResolveJunctionFocusWorldPose;
        ws.CaptureCurrentCameraPose = CaptureCurrentCameraBookmark;

        OnWorkspaceProjectRevisionChanged();
    }

    private void UnbindWorkspace()
    {
        if (SubscribedWorkspace is not null)
        {
            SubscribedWorkspace.FocusCameraRequested -= OnFocusCameraRequested;
            SubscribedWorkspace.ChunkJumpRequested -= OnChunkJumpRequested;
            SubscribedWorkspace.StartVerificationRequested -= OnStartVerificationRequested;
            SubscribedWorkspace.CancelVerificationStartRequested -= OnCancelVerificationStartRequested;
            SubscribedWorkspace.StopVerificationRequested -= OnStopVerificationRequested;
            SubscribedWorkspace.ApplyCameraBookmarkRequested -= OnApplyCameraBookmarkRequested;
            if (SubscribedWorkspace.ResolveSplineFocusPose?.Target == this)
            {
                SubscribedWorkspace.ResolveSplineFocusPose = null;
            }

            if (SubscribedWorkspace.ResolveJunctionFocusPose?.Target == this)
            {
                SubscribedWorkspace.ResolveJunctionFocusPose = null;
            }

            if (SubscribedWorkspace.CaptureCurrentCameraPose?.Target == this)
            {
                SubscribedWorkspace.CaptureCurrentCameraPose = null;
            }

            SubscribedWorkspace = null;
        }

        WorkspaceSubscriptions?.Dispose();
        WorkspaceSubscriptions = null;
        LastLoadedProject = null;
    }

    private WorldPoseDegreesModel? ResolveJunctionFocusWorldPose(string junctionId)
    {
        if (Renderer is not null)
        {
            foreach (JunctionRenderEntry entry in Renderer.World.Junctions)
            {
                if (string.Equals(entry.Node.Id, junctionId, StringComparison.Ordinal))
                {
                    return new WorldPoseDegreesModel(
                        new ChunkIndexModel(entry.PrimaryChunk.X, entry.PrimaryChunk.Z),
                        PoseDegreesModel.FromTranslation(entry.LocalOrigin.X, entry.LocalOrigin.Y, entry.LocalOrigin.Z)).Normalized();
                }
            }
        }

        JunctionPlacementNode? junction = Workspace?.FindJunction(junctionId);
        if (junction is not null && junction.Origin.Kind == JunctionOriginKind.ChunkPose)
        {
            return new WorldPoseDegreesModel(junction.Origin.Chunk, junction.Origin.Pose).Normalized();
        }

        return null;
    }

    private WorldPoseDegreesModel? ResolveSplineFocusWorldPose(string splineId)
    {
        if (Renderer is not null)
        {
            foreach (SplineRenderEntry entry in Renderer.World.Splines)
            {
                if (string.Equals(entry.Node.Id, splineId, StringComparison.Ordinal)
                    && 0 < entry.PrimaryChunkCenterlinePoints.Count)
                {
                    Vector3 mid = entry.PrimaryChunkCenterlinePoints[entry.PrimaryChunkCenterlinePoints.Count / 2];
                    return new WorldPoseDegreesModel(
                        new ChunkIndexModel(entry.PrimaryChunk.X, entry.PrimaryChunk.Z),
                        PoseDegreesModel.FromTranslation(mid.X, mid.Y, mid.Z)).Normalized();
                }
            }
        }

        SplinePlacementNode? spline = Workspace?.FindSpline(splineId);
        if (spline is not null && spline.Origin.Kind == SplineOriginKind.ChunkPose)
        {
            return new WorldPoseDegreesModel(spline.Origin.Chunk, spline.Origin.Pose).Normalized();
        }

        return null;
    }

    private void OnFocusCameraRequested(WorldPoseDegreesModel target)
    {
        Renderer?.CameraController.FocusOn(target);
    }

    private void OnChunkJumpRequested(int chunkX, int chunkZ)
    {
        Renderer?.CameraController.JumpToChunk(chunkX, chunkZ);
    }

    private CameraBookmarkModel? CaptureCurrentCameraBookmark()
    {
        if (Renderer is null)
        {
            return null;
        }

        TransportX.Cameras.CameraPose pose = Renderer.CameraController.CurrentCameraPose;
        return new CameraBookmarkModel(
            string.Empty,
            pose.Chunk.X,
            pose.Chunk.Z,
            pose.Position.X,
            pose.Position.Y,
            pose.Position.Z,
            double.RadiansToDegrees(pose.Angle.X),
            double.RadiansToDegrees(pose.Angle.Y),
            0.0);
    }

    private void OnStartVerificationRequested(WorldProject project)
    {
        if (Renderer is null || Workspace is null)
        {
            if (Workspace is not null)
            {
                Workspace.IsVerificationStarting.Value = false;
                Workspace.ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
                Workspace.VerificationSummaryText.Value = "編集モード";
            }

            return;
        }

        _ = StartVerificationAsync(project);
    }

    private void OnCancelVerificationStartRequested()
    {
        VerificationStartCts?.Cancel();
        if (Workspace is not null && IsStartingVerificationLocal)
        {
            IsStartingVerificationLocal = false;
            Workspace.IsVerificationStarting.Value = false;
            Workspace.ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
            Workspace.VerificationSummaryText.Value = "編集モード";
            Workspace.LastStatusMessage.Value = "スクリプト検証の起動をキャンセルしました。";
        }
    }

    private void CancelInFlightVerificationStart()
    {
        VerificationStartCts?.Cancel();
        VerificationStartCts?.Dispose();
        VerificationStartCts = null;
        IsStartingVerificationLocal = false;
    }

    private async Task StartVerificationAsync(WorldProject project)
    {
        EditorViewportRenderer? renderer = Renderer;
        WorldWorkspaceService? ws = Workspace;
        if (renderer is null || ws is null)
        {
            return;
        }

        CancellationTokenSource? prevCts = VerificationStartCts;
        prevCts?.Cancel();
        prevCts?.Dispose();

        CancellationTokenSource cts = new();
        VerificationStartCts = cts;
        CancellationToken ct = cts.Token;

        IsStartingVerificationLocal = true;
        ws.IsVerificationStarting.Value = true;

        try
        {
            Task? priorTask = InFlightVerificationStartTask;
            if (priorTask is not null)
            {
                try
                {
                    await priorTask;
                }
                catch
                {
                }
            }

            if (ct.IsCancellationRequested || Renderer is null || ChildHwnd == 0)
            {
                return;
            }

            renderer.StopScriptVerification();

            Task<(bool Started, ScriptVerificationRunner Runner, IReadOnlyList<TransportX.Diagnostics.Error> Errors)> workTask =
                Task.Run(() => renderer.PrepareScriptVerificationOnBackground(project));
            InFlightVerificationStartTask = workTask;

            (bool started, ScriptVerificationRunner runner, IReadOnlyList<TransportX.Diagnostics.Error> errors) = await workTask;

            if (ct.IsCancellationRequested || Renderer is null || !ReferenceEquals(Renderer, renderer) || ChildHwnd == 0)
            {
                runner.Dispose();
                if (ReferenceEquals(Workspace, ws))
                {
                    ws.IsVerificationStarting.Value = false;
                    ws.ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
                    ws.VerificationSummaryText.Value = "編集モード";
                    ws.LastStatusMessage.Value = "スクリプト検証の起動をキャンセルしました。";
                }

                return;
            }

            renderer.CommitScriptVerification(runner, started, ws.OverlayFlags.Value);
            LastPublishedVerificationErrorCount = errors.Count;
            IsStartingVerificationLocal = false;
            ws.IsVerificationStarting.Value = false;
            ws.ActiveRenderMode.Value = EditorRenderMode.ScriptVerification;
            ws.SetVerificationErrors(errors);

            bool hasAnyErrors = !started || errors.Any(e =>
                e.Level == TransportX.Diagnostics.ErrorLevel.Error
                || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal);
            ws.LastStatusMessage.Value = hasAnyErrors
                ? $"スクリプト検証で {errors.Count} 件のエラーが検出されました。"
                : "スクリプト検証モードを実行中 (Esc で編集モードへ復帰)";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Workspace, ws))
            {
                IsStartingVerificationLocal = false;
                ws.IsVerificationStarting.Value = false;
                ws.ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
                ws.VerificationSummaryText.Value = "編集モード";
                ws.LastStatusMessage.Value = $"スクリプト検証の起動に失敗しました: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(VerificationStartCts, cts))
            {
                VerificationStartCts = null;
            }

            cts.Dispose();
            IsStartingVerificationLocal = false;
            if (ReferenceEquals(Workspace, ws) && ws.IsVerificationStarting.Value)
            {
                ws.IsVerificationStarting.Value = false;
            }
        }
    }

    private void OnStopVerificationRequested()
    {
        if (IsStartingVerificationLocal)
        {
            OnCancelVerificationStartRequested();
            return;
        }

        if (Renderer is null)
        {
            return;
        }

        Renderer.StopScriptVerification();
        LastPublishedVerificationErrorCount = 0;

        if (Workspace is not null)
        {
            Workspace.SetVerificationErrors(Array.Empty<TransportX.Diagnostics.Error>());
            if (Workspace.CurrentProject.Value is WorldProject activeProject)
            {
                Renderer.SyncFromProjectIncremental(activeProject);
                LastLoadedProject = activeProject;
            }
        }
    }

    private void OnApplyCameraBookmarkRequested(CameraBookmarkModel bookmark)
    {
        if (Renderer is null)
        {
            return;
        }

        WorldPoseDegreesModel pose = new(
            new ChunkIndexModel(bookmark.ChunkX, bookmark.ChunkZ),
            new PoseDegreesModel(
                bookmark.X,
                bookmark.Y,
                bookmark.Z,
                bookmark.RotationX,
                bookmark.RotationY,
                bookmark.RotationZ));
        Renderer.CameraController.Locate(pose);
    }

    private void OnWorkspaceProjectRevisionChanged()
    {
        if (Renderer is null || Workspace is null)
        {
            return;
        }

        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            LastLoadedProject = null;
            return;
        }

        EnsureDefaultPlacementModelKey(Workspace, project);

        if (SuppressNextRevisionSync || Renderer.ActiveMode == EditorRenderMode.ScriptVerification)
        {
            return;
        }

        bool isNewProject = !ReferenceEquals(LastLoadedProject, project);
        LastLoadedProject = project;

        if (isNewProject)
        {
            bool hasSavedPose = ReferenceEquals(Workspace.SavedCameraProject, project)
                && Workspace.SavedCameraPose.HasValue;
            Renderer.RebuildFromProject(project, relocateCamera: !hasSavedPose);
            if (hasSavedPose)
            {
                Renderer.CameraController.Locate(Workspace.SavedCameraPose!.Value);
            }
        }
        else
        {
            Renderer.SyncFromProjectIncremental(project);
        }

        Workspace.SetRuntimeErrors(Renderer.ErrorCollector.Errors);
    }

    private static void EnsureDefaultPlacementModelKey(WorldWorkspaceService ws, WorldProject project)
    {
        if (!string.IsNullOrWhiteSpace(ws.PlacementModelKey.Value))
        {
            return;
        }

        foreach (ModelListDocument list in project.ModelLists.Values)
        {
            foreach (ModelListEntry entry in list.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    ws.PlacementModelKey.Value = entry.Key;
                    return;
                }
            }
        }
    }

    private void StartRenderLoop()
    {
        StopRenderLoop();
        FrameWatch.Restart();
        RenderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => RenderSingleTick());
        RenderTimer.Start();
    }

    private void StopRenderLoop()
    {
        if (RenderTimer is not null)
        {
            RenderTimer.Stop();
            RenderTimer = null;
        }

        FrameWatch.Stop();
    }

    private void RenderSingleTick()
    {
        if (Renderer is null || IsStartingVerificationLocal)
        {
            return;
        }

        if (!IsVisible || !IsEffectivelyVisible)
        {
            if (OperatingSystem.IsWindows() && ChildHwnd != 0)
            {
                User32.ShowWindow(new HWND(ChildHwnd), ShowWindowCommand.SW_HIDE);
            }

            return;
        }

        if (IsRenderingFrame)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && ChildHwnd != 0)
        {
            User32.ShowWindow(new HWND(ChildHwnd), ShowWindowCommand.SW_SHOWNOACTIVATE);
        }

        IsRenderingFrame = true;
        try
        {
            TimeSpan elapsed = FrameWatch.Elapsed;
            FrameWatch.Restart();
            if (TimeSpan.FromSeconds(0.25) < elapsed)
            {
                elapsed = TimeSpan.FromSeconds(1.0 / 60.0);
            }

            (int physW, int physH) = GetPhysicalPixelSize();
            if (physW != Renderer.PixelWidth || physH != Renderer.PixelHeight)
            {
                Renderer.Resize(physW, physH);
                ViewportResized?.Invoke(this, EventArgs.Empty);
            }

            if (IsRightDown)
            {
                bool w = IsVirtualKeyDown(VK_W);
                bool s = IsVirtualKeyDown(VK_S);
                bool a = IsVirtualKeyDown(VK_A);
                bool d = IsVirtualKeyDown(VK_D);
                bool e = IsVirtualKeyDown(VK_E);
                bool q = IsVirtualKeyDown(VK_Q);
                bool shift = IsVirtualKeyDown(VK_SHIFT);
                bool ctrl = IsVirtualKeyDown(VK_CONTROL);

                Renderer.CameraController.MoveKeyboard(
                    w,
                    s,
                    a,
                    d,
                    e,
                    q,
                    shift,
                    ctrl,
                    (float)elapsed.TotalSeconds);
            }

            WorldWorkspaceService? ws = Workspace;
            bool isVerification = Renderer.ActiveMode == EditorRenderMode.ScriptVerification;
            string? selectedPropId = ws?.SelectedPropId.Value;
            string? selectedSplineId = ws?.SelectedSplineId.Value;
            string? selectedJunctionId = ws?.SelectedJunctionId.Value;
            PortReferenceModel? pendingConnectPort = ws?.PendingConnectSourcePort.Value;
            GizmoAxis highlightAxis = IsDraggingGizmo ? ActiveGizmoAxis : HoveredGizmoAxis;

            if (!isVerification)
            {
                EditorToolMode toolMode = ws?.ActiveToolMode.Value ?? EditorToolMode.Select;
                if (toolMode != EditorToolMode.PlaceProp
                    && toolMode != EditorToolMode.PlaceSpline
                    && toolMode != EditorToolMode.PlaceJunction)
                {
                    PlacementCursorRelCamera = null;
                }

                ViewportOverlayFlags flags = ws?.OverlayFlags.Value ?? ViewportOverlayFlags.Default;
                ChunkIndex camChunk = Renderer.CameraController.WorldPose.Chunk;

                Renderer.World.UpdateOverlays(
                    selectedPropId,
                    selectedSplineId,
                    pendingConnectPort,
                    toolMode,
                    highlightAxis,
                    flags,
                    PlacementCursorRelCamera,
                    camChunk,
                    selectedJunctionId);
            }
            else
            {
                Renderer.ApplyVerificationOverlayFlags(ws?.OverlayFlags.Value ?? ViewportOverlayFlags.Default);
            }

            Renderer.RenderFrame(elapsed);

            if (ws is not null)
            {
                if (isVerification)
                {
                    IReadOnlyList<TransportX.Diagnostics.Error> verifyErrors = Renderer.VerificationErrors;
                    if (verifyErrors.Count != LastPublishedVerificationErrorCount)
                    {
                        LastPublishedVerificationErrorCount = verifyErrors.Count;
                        ws.SetVerificationErrors(verifyErrors);
                    }
                }

                WorldPose camPose = Renderer.CameraController.WorldPose;
                Vector3 p = camPose.Pose.Position;
                string status;
                if (isVerification)
                {
                    status =
                        $"[スクリプト検証モード] Camera: Chunk({camPose.Chunk.X}, {camPose.Chunk.Z}) Local({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) | Esc: 編集へ戻る";
                }
                else
                {
                    string selText = !string.IsNullOrEmpty(selectedPropId)
                        ? selectedPropId
                        : (!string.IsNullOrEmpty(selectedSplineId)
                            ? selectedSplineId
                            : (!string.IsNullOrEmpty(selectedJunctionId) ? selectedJunctionId : "なし"));
                    string axisText = highlightAxis == GizmoAxis.None ? string.Empty : $" [{highlightAxis}]";
                    string pendingPortLabel = string.IsNullOrEmpty(pendingConnectPort?.PortKey) ? "Outlet" : pendingConnectPort.PortKey;
                    string connectText = pendingConnectPort is null
                        ? string.Empty
                        : $" | 接続元:{pendingConnectPort.ElementId}.{pendingPortLabel}";
                    status =
                        $"Camera: Chunk({camPose.Chunk.X}, {camPose.Chunk.Z}) Local({p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}) | 選択: {selText}{axisText}{connectText}";
                }

                if (!string.Equals(status, LastPublishedStatusText, StringComparison.Ordinal))
                {
                    LastPublishedStatusText = status;
                    ws.CameraStatusText.Value = status;
                }
            }
        }
        finally
        {
            IsRenderingFrame = false;
        }
    }

    private (int Width, int Height) GetPhysicalPixelSize()
    {
        if (OperatingSystem.IsWindows() && ChildHwnd != 0 && User32.GetClientRect(new HWND(ChildHwnd), out RECT clientRect))
        {
            int cw = clientRect.right - clientRect.left;
            int ch = clientRect.bottom - clientRect.top;
            if (0 < cw && 0 < ch)
            {
                return (cw, ch);
            }
        }

        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        return (w, h);
    }

    private static bool IsVirtualKeyDown(int vKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        short state = User32.GetAsyncKeyState(vKey);
        return (state & 0x8000) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowClassRegistered()
    {
        lock (ClassRegLock)
        {
            if (WindowClassRegistered)
            {
                return;
            }

            StaticWndProcDelegate ??= StaticWndProc;
            Kernel32.SafeHINSTANCE hInstance = Kernel32.GetModuleHandle(null);
            User32.WNDCLASSEX wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEX>(),
                // Never set CS_DBLCLKS: rapid consecutive right/middle drags or clicks must always
                // dispatch WM_*BUTTONDOWN rather than converting every second press into WM_*BUTTONDBLCLK.
                style = (User32.WindowClassStyles)WindowClassStyle,
                lpfnWndProc = StaticWndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = HICON.NULL,
                hCursor = WindowClass.StdArrowCursor,
                hbrBackground = HBRUSH.NULL,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = HICON.NULL,
            };

            User32.RegisterClassEx(in wc);
            WindowClassRegistered = true;
        }
    }

    [SupportedOSPlatform("windows")]
    private void AttachTopLevelWndProcHook()
    {
        if (ChildHwnd == 0)
        {
            return;
        }

        TopLevel? nextTopLevel = TopLevel.GetTopLevel(this);
        if (ReferenceEquals(nextTopLevel, HookedTopLevel))
        {
            return;
        }

        DetachTopLevelWndProcHook();
        if (nextTopLevel is not null)
        {
            TopLevelWndProcHook = OnTopLevelWndProc;
            Win32Properties.AddWndProcHookCallback(nextTopLevel, TopLevelWndProcHook);
            HookedTopLevel = nextTopLevel;
        }
    }

    [SupportedOSPlatform("windows")]
    private void DetachTopLevelWndProcHook()
    {
        if (HookedTopLevel is not null && TopLevelWndProcHook is not null)
        {
            Win32Properties.RemoveWndProcHookCallback(HookedTopLevel, TopLevelWndProcHook);
        }

        HookedTopLevel = null;
        TopLevelWndProcHook = null;
    }

    [SupportedOSPlatform("windows")]
    private nint OnTopLevelWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEWHEEL
            && ChildHwnd != 0
            && IsEffectivelyVisible
            && User32.GetFocus().DangerousGetHandle() != ChildHwnd)
        {
            (int screenX, int screenY) = UnpackPoint(lParam);
            if (User32.WindowFromPoint(new POINT(screenX, screenY)).DangerousGetHandle() == ChildHwnd)
            {
                HandleMouseWheel(wParam);
                handled = true;
                return 0;
            }
        }

        return 0;
    }

    internal static uint NativeWindowClassStyleForTest => WindowClassStyle;
    internal bool IsRightButtonDownForTest => IsRightDown;
    internal bool IsMiddleButtonDownForTest => IsMiddleDown;
    internal bool IsLeftButtonDownForTest => IsLeftDown;

    internal nint DispatchNativeMessageForTest(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        return InstanceWndProc(hWnd, msg, wParam, lParam);
    }

    [SupportedOSPlatform("windows")]
    private static nint StaticWndProc(HWND hWnd, uint msg, nint wParam, nint lParam)
    {
        nint rawHandle = hWnd.DangerousGetHandle();
        if (HwndMap.TryGetValue(rawHandle, out WeakReference<Win32HwndViewportHost>? weakRef)
            && weakRef.TryGetTarget(out Win32HwndViewportHost? host))
        {
            return host.InstanceWndProc(rawHandle, msg, wParam, lParam);
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [SupportedOSPlatform("windows")]
    private void EnsureFocusAndCapture(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }

        HWND target = new(hWnd);
        if (User32.GetFocus().DangerousGetHandle() != hWnd)
        {
            User32.SetFocus(target);
        }

        if (User32.GetCapture().DangerousGetHandle() != hWnd)
        {
            User32.SetCapture(target);
        }
    }

    private void HandleMouseWheel(nint wParam)
    {
        short wheelRaw = HighWordSigned(wParam);
        float wheelSteps = wheelRaw / 120f;
        Renderer?.CameraController.ZoomWheel(wheelSteps);
    }

    [SupportedOSPlatform("windows")]
    private nint InstanceWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;

            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
            {
                if (hWnd != 0 && User32.GetFocus().DangerousGetHandle() != hWnd)
                {
                    User32.SetFocus(new HWND(hWnd));
                }

                if (Renderer?.ActiveMode == EditorRenderMode.ScriptVerification)
                {
                    return 0;
                }

                if (hWnd != 0 && User32.GetCapture().DangerousGetHandle() != hWnd)
                {
                    User32.SetCapture(new HWND(hWnd));
                }

                (int x, int y) = UnpackPoint(lParam);
                LastMouseX = x;
                LastMouseY = y;
                IsLeftDown = true;
                OnLeftMouseDown(x, y);
                return 0;
            }

            case WM_LBUTTONUP:
            {
                IsLeftDown = false;
                IsDraggingGizmo = false;
                ActiveGizmoAxis = GizmoAxis.None;
                DraggingPropId = null;
                DraggingSplineId = null;
                DraggingJunctionId = null;
                UpdateCaptureState();
                return 0;
            }

            case WM_RBUTTONDOWN:
            case WM_RBUTTONDBLCLK:
            {
                EnsureFocusAndCapture(hWnd);
                (int x, int y) = UnpackPoint(lParam);
                LastMouseX = x;
                LastMouseY = y;
                IsRightDown = true;
                return 0;
            }

            case WM_RBUTTONUP:
            {
                IsRightDown = false;
                UpdateCaptureState();
                return 0;
            }

            case WM_MBUTTONDOWN:
            case WM_MBUTTONDBLCLK:
            {
                EnsureFocusAndCapture(hWnd);
                (int x, int y) = UnpackPoint(lParam);
                LastMouseX = x;
                LastMouseY = y;
                IsMiddleDown = true;
                return 0;
            }

            case WM_MBUTTONUP:
            {
                IsMiddleDown = false;
                UpdateCaptureState();
                return 0;
            }

            case WM_MOUSEMOVE:
            {
                (int x, int y) = UnpackPoint(lParam);
                int dx = x - LastMouseX;
                int dy = y - LastMouseY;
                LastMouseX = x;
                LastMouseY = y;
                OnMouseMove(x, y, dx, dy);
                return 0;
            }

            case WM_MOUSEWHEEL:
            {
                HandleMouseWheel(wParam);
                return 0;
            }

            case WM_KEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFFFF);
                bool ctrl = IsVirtualKeyDown(VK_CONTROL);
                bool shift = IsVirtualKeyDown(VK_SHIFT);
                bool alt = IsVirtualKeyDown(VK_MENU);

                if (!alt && TryMapGlobalShortcutKey(vk, ctrl, shift, out EditorGlobalShortcut shortcut))
                {
                    Workspace?.RequestGlobalShortcut(shortcut);
                    return 0;
                }

                if (vk == VK_ESCAPE && Workspace is not null)
                {
                    Workspace.CancelActiveInteraction();
                    return 0;
                }

                if (Renderer?.ActiveMode == EditorRenderMode.ScriptVerification)
                {
                    break;
                }

                if (vk == VK_F && !IsRightDown)
                {
                    Workspace?.RequestFocusSelectedOrDefault();
                    return 0;
                }

                if (vk == VK_DELETE && Workspace is not null)
                {
                    if (Workspace.SelectedPropId.Value is not null)
                    {
                        Workspace.RemovePropPlacement(Workspace.SelectedPropId.Value);
                        return 0;
                    }

                    if (Workspace.SelectedSplineId.Value is not null)
                    {
                        Workspace.RemoveSplinePlacement(Workspace.SelectedSplineId.Value);
                        return 0;
                    }

                    if (Workspace.SelectedJunctionId.Value is not null)
                    {
                        Workspace.RemoveJunctionPlacement(Workspace.SelectedJunctionId.Value);
                        return 0;
                    }
                }

                break;
            }

            case WM_KILLFOCUS:
            case WM_CANCELMODE:
                ResetPointerDragState();
                return 0;

            case WM_CAPTURECHANGED:
                if (lParam != hWnd)
                {
                    ResetPointerDragState();
                }

                return 0;

            case WM_DESTROY:
                ResetPointerDragState();
                HwndMap.TryRemove(hWnd, out _);
                break;
        }

        return User32.DefWindowProc(new HWND(hWnd), msg, wParam, lParam);
    }

    internal static bool TryMapGlobalShortcutKey(
        int virtualKey,
        bool ctrl,
        bool shift,
        out EditorGlobalShortcut shortcut)
    {
        if (ctrl && virtualKey == VK_S)
        {
            shortcut = shift ? EditorGlobalShortcut.SaveAll : EditorGlobalShortcut.Save;
            return true;
        }

        if (ctrl && !shift && virtualKey == VK_N)
        {
            shortcut = EditorGlobalShortcut.NewWorld;
            return true;
        }

        if (ctrl && !shift && virtualKey == VK_O)
        {
            shortcut = EditorGlobalShortcut.OpenWorld;
            return true;
        }

        if (ctrl && !shift && virtualKey == VK_Z)
        {
            shortcut = EditorGlobalShortcut.Undo;
            return true;
        }

        if (ctrl && !shift && virtualKey == VK_Y)
        {
            shortcut = EditorGlobalShortcut.Redo;
            return true;
        }

        if (virtualKey == VK_F5 && !shift)
        {
            shortcut = ctrl ? EditorGlobalShortcut.LaunchPlayer : EditorGlobalShortcut.Verify;
            return true;
        }

        shortcut = default;
        return false;
    }

    private void ResetPointerDragState()
    {
        IsLeftDown = false;
        IsRightDown = false;
        IsMiddleDown = false;
        IsDraggingGizmo = false;
        ActiveGizmoAxis = GizmoAxis.None;
        DraggingPropId = null;
        DraggingSplineId = null;
        DraggingJunctionId = null;
    }

    [SupportedOSPlatform("windows")]
    private void UpdateCaptureState()
    {
        if (!IsLeftDown && !IsRightDown && !IsMiddleDown)
        {
            User32.ReleaseCapture();
        }
    }

    private void OnLeftMouseDown(int pixelX, int pixelY)
    {
        if (Renderer is null || Workspace is null)
        {
            return;
        }

        ViewContext viewCtx = Renderer.CreateViewContext();
        ViewportRay ray = ViewportPickingAndGizmoService.CreateRay(
            pixelX,
            pixelY,
            Renderer.PixelWidth,
            Renderer.PixelHeight,
            viewCtx);

        EditorToolMode tool = Workspace.ActiveToolMode.Value;

        if (tool == EditorToolMode.PlaceProp)
        {
            string modelKey = Workspace.PlacementModelKey.Value;
            if (string.IsNullOrWhiteSpace(modelKey))
            {
                Workspace.LastStatusMessage.Value = "配置するモデルキーが選択されていません (先に Models.txt にモデルを追加してください)。";
                return;
            }

            WorldPoseDegreesModel placedPose = ViewportPickingAndGizmoService.SnapGroundPlacement(
                ray,
                Workspace.GridSnapMeters.Value,
                0f);
            Workspace.AddPropPlacement(modelKey, placedPose);
            return;
        }

        if (tool == EditorToolMode.PlaceSpline)
        {
            List<PortPickCandidate> freeSources = CollectPortCandidates(onlyFreeConnectSources: true, excludeElementId: null);
            PortPickHit? portHit = ViewportPickingAndGizmoService.PickNearestPort(ray, freeSources);
            if (portHit.HasValue)
            {
                if (portHit.Value.PortRef.Kind == PortTargetKind.JunctionPort
                    && !string.IsNullOrEmpty(portHit.Value.PortRef.PortKey))
                {
                    Workspace.AddChainedSplineFromJunctionPort(
                        portHit.Value.PortRef.ElementId,
                        portHit.Value.PortRef.PortKey,
                        Workspace.PlacementSplineTemplateKey.Value);
                    return;
                }

                Workspace.AddChainedSplineFromOutlet(
                    portHit.Value.PortRef.ElementId,
                    Workspace.PlacementSplineTemplateKey.Value);
                return;
            }

            WorldPoseDegreesModel placedPose = ViewportPickingAndGizmoService.SnapGroundPlacement(
                ray,
                Workspace.GridSnapMeters.Value,
                0f);
            Workspace.AddSplineAtChunkPose(Workspace.PlacementSplineTemplateKey.Value, placedPose);
            return;
        }

        if (tool == EditorToolMode.PlaceJunction)
        {
            string junctionTplKey = Workspace.PlacementJunctionTemplateKey.Value;
            if (string.IsNullOrWhiteSpace(junctionTplKey))
            {
                Workspace.LastStatusMessage.Value = "配置する JunctionTemplate が選択されていません (先にジャンクションテンプレートを作成してください)。";
                return;
            }

            List<PortPickCandidate> freeSources = CollectPortCandidates(onlyFreeConnectSources: true, excludeElementId: null);
            PortPickHit? portHit = ViewportPickingAndGizmoService.PickNearestPort(ray, freeSources);
            if (portHit.HasValue && Workspace.CurrentProject.Value is WorldProject project)
            {
                if (portHit.Value.PortRef.Kind == PortTargetKind.SplineOutlet)
                {
                    SplinePlacementNode? sourceSpline = Workspace.FindSpline(portHit.Value.PortRef.ElementId);
                    if (sourceSpline is not null)
                    {
                        IReadOnlyList<string> compatible = JunctionAnchorUtilities.FindCompatibleJunctionPortsForSplineOutlet(
                            project,
                            sourceSpline,
                            junctionTplKey);
                        if (0 < compatible.Count)
                        {
                            Workspace.AddChainedJunctionFromSplineOutlet(sourceSpline.Id, junctionTplKey, compatible[0]);
                            return;
                        }

                        Workspace.LastStatusMessage.Value = $"選択中の '{junctionTplKey}' には '{sourceSpline.Id}.Outlet' と互換なポートがありません。";
                        return;
                    }
                }
                else if (portHit.Value.PortRef.Kind == PortTargetKind.JunctionPort
                    && !string.IsNullOrEmpty(portHit.Value.PortRef.PortKey))
                {
                    JunctionPlacementNode? sourceJunction = Workspace.FindJunction(portHit.Value.PortRef.ElementId);
                    if (sourceJunction is not null)
                    {
                        IReadOnlyList<string> compatible = JunctionAnchorUtilities.FindCompatibleJunctionPortsForJunctionPort(
                            project,
                            sourceJunction,
                            portHit.Value.PortRef.PortKey,
                            junctionTplKey);
                        if (0 < compatible.Count)
                        {
                            Workspace.AddChainedJunctionFromJunctionPort(
                                sourceJunction.Id,
                                portHit.Value.PortRef.PortKey,
                                junctionTplKey,
                                compatible[0]);
                            return;
                        }

                        Workspace.LastStatusMessage.Value = $"選択中の '{junctionTplKey}' には '{sourceJunction.Id}.{portHit.Value.PortRef.PortKey}' と互換なポートがありません。";
                        return;
                    }
                }
            }

            WorldPoseDegreesModel placedPose = ViewportPickingAndGizmoService.SnapGroundPlacement(
                ray,
                Workspace.GridSnapMeters.Value,
                0f);
            Workspace.AddJunctionAtChunkPose(junctionTplKey, placedPose);
            return;
        }

        if (tool == EditorToolMode.ConnectPort)
        {
            HandleConnectPortClick(ray);
            return;
        }

        // Check gizmo handles first when a prop or ChunkPose spline/junction is already selected and tool is Translate or Rotate
        string? currentSelectedPropId = Workspace.SelectedPropId.Value;
        ChunkPropPlacement? selectedProp = Workspace.FindProp(currentSelectedPropId);
        if (selectedProp is not null && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
        {
            Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                ray.OriginChunk,
                selectedProp.Chunk,
                new Vector3((float)selectedProp.Pose.X, (float)selectedProp.Pose.Y, (float)selectedProp.Pose.Z));

            GizmoAxis hitAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
            if (hitAxis != GizmoAxis.None)
            {
                BeginPropGizmoDrag(selectedProp, hitAxis, ray, gizmoCenter);
                return;
            }
        }

        SplinePlacementNode? selectedSpline = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
        if (selectedSpline is not null
            && selectedSpline.Origin.Kind == SplineOriginKind.ChunkPose
            && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
        {
            Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                ray.OriginChunk,
                selectedSpline.Origin.Chunk,
                new Vector3((float)selectedSpline.Origin.Pose.X, (float)selectedSpline.Origin.Pose.Y, (float)selectedSpline.Origin.Pose.Z));

            GizmoAxis hitAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
            if (hitAxis != GizmoAxis.None)
            {
                BeginSplineGizmoDrag(selectedSpline, hitAxis, ray, gizmoCenter);
                return;
            }
        }

        JunctionPlacementNode? selectedJunction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
        if (selectedJunction is not null
            && selectedJunction.Origin.Kind == JunctionOriginKind.ChunkPose
            && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
        {
            Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                ray.OriginChunk,
                selectedJunction.Origin.Chunk,
                new Vector3((float)selectedJunction.Origin.Pose.X, (float)selectedJunction.Origin.Pose.Y, (float)selectedJunction.Origin.Pose.Z));

            GizmoAxis hitAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
            if (hitAxis != GizmoAxis.None)
            {
                BeginJunctionGizmoDrag(selectedJunction, hitAxis, ray, gizmoCenter);
                return;
            }
        }

        // Otherwise raycast-pick props, splines, and junctions in the scene
        List<PropPickCandidate> propCandidates = new();
        foreach (PropRenderEntry entry in Renderer.World.Props)
        {
            propCandidates.Add(new PropPickCandidate(
                entry.Placement.Id,
                new WorldPoseDegreesModel(entry.Placement.Chunk, entry.Placement.Pose),
                entry.LocalBounds));
        }

        List<SplinePickCandidate> splineCandidates = new();
        foreach (SplineRenderEntry entry in Renderer.World.Splines)
        {
            splineCandidates.Add(new SplinePickCandidate(
                entry.Node.Id,
                entry.PrimaryChunk,
                entry.PrimaryChunkCenterlinePoints));
        }

        List<JunctionPickCandidate> junctionCandidates = new();
        foreach (JunctionRenderEntry entry in Renderer.World.Junctions)
        {
            List<IReadOnlyList<Vector3>> combinedPolylines = new(
                entry.PrimaryChunkPathPolylines.Count + entry.PrimaryChunkCurvePolylines.Count);
            foreach (IReadOnlyList<Vector3> pl in entry.PrimaryChunkPathPolylines)
            {
                combinedPolylines.Add(pl);
            }

            foreach (IReadOnlyList<Vector3> cl in entry.PrimaryChunkCurvePolylines)
            {
                combinedPolylines.Add(cl);
            }

            WorldPoseDegreesModel jPose = new(
                new ChunkIndexModel(entry.PrimaryChunk.X, entry.PrimaryChunk.Z),
                ToPoseDegrees(entry.BuiltJunction.WorldPose.Pose));
            junctionCandidates.Add(new JunctionPickCandidate(
                entry.Node.Id,
                entry.PrimaryChunk,
                jPose,
                entry.LocalBounds,
                combinedPolylines));
        }

        PropPickHit? propHit = ViewportPickingAndGizmoService.PickNearestProp(ray, propCandidates);
        SplinePickHit? splineHit = ViewportPickingAndGizmoService.PickNearestSpline(ray, splineCandidates);
        JunctionPickHit? junctionHit = ViewportPickingAndGizmoService.PickNearestJunction(ray, junctionCandidates);

        float networkBestDist = float.MaxValue;
        string? bestSplineId = null;
        string? bestJunctionId = null;
        if (splineHit.HasValue)
        {
            networkBestDist = splineHit.Value.Distance;
            bestSplineId = splineHit.Value.SplineId;
        }

        if (junctionHit.HasValue && junctionHit.Value.Distance < networkBestDist)
        {
            networkBestDist = junctionHit.Value.Distance;
            bestSplineId = null;
            bestJunctionId = junctionHit.Value.JunctionId;
        }

        if (propHit.HasValue && (networkBestDist == float.MaxValue || propHit.Value.Distance <= networkBestDist + 0.5f))
        {
            Workspace.SelectProp(propHit.Value.PropId);
        }
        else if (bestSplineId is not null)
        {
            Workspace.SelectSpline(bestSplineId);
        }
        else if (bestJunctionId is not null)
        {
            Workspace.SelectJunction(bestJunctionId);
        }
        else
        {
            Workspace.ClearSelection();
        }
    }

    private static PoseDegreesModel ToPoseDegrees(in Pose pose)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, pose.Orientation);
        double yawRad = double.Atan2(forward.X, forward.Z);
        double pitchRad = -double.Asin(double.Clamp(forward.Y, -1.0, 1.0));
        Quaternion baseRot = Quaternion.CreateFromYawPitchRoll((float)yawRad, (float)pitchRad, 0f);
        Vector3 baseRight = Vector3.Transform(Vector3.UnitX, baseRot);
        Vector3 baseUp = Vector3.Transform(Vector3.UnitY, baseRot);
        Vector3 actualRight = Vector3.Transform(Vector3.UnitX, pose.Orientation);
        double rollRad = double.Atan2(Vector3.Dot(actualRight, baseUp), Vector3.Dot(actualRight, baseRight));
        return new PoseDegreesModel(
            pose.Position.X,
            pose.Position.Y,
            pose.Position.Z,
            double.RadiansToDegrees(pitchRad),
            double.RadiansToDegrees(yawRad),
            double.RadiansToDegrees(rollRad));
    }

    private List<PortPickCandidate> CollectPortCandidates(bool onlyFreeConnectSources, string? excludeElementId)
    {
        List<PortPickCandidate> list = new();
        if (Renderer is null)
        {
            return list;
        }

        foreach (SplineRenderEntry entry in Renderer.World.Splines)
        {
            foreach (SplinePortWorldLocation port in entry.Ports)
            {
                if (!string.IsNullOrEmpty(excludeElementId)
                    && string.Equals(port.PortRef.ElementId, excludeElementId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (onlyFreeConnectSources)
                {
                    if (port.PortRef.Kind != PortTargetKind.SplineOutlet || port.IsConnected)
                    {
                        continue;
                    }
                }
                else if (port.IsConnected)
                {
                    continue;
                }

                list.Add(new PortPickCandidate(port.PortRef, port.Chunk, port.LocalPosition, port.IsConnected));
            }
        }

        foreach (JunctionRenderEntry entry in Renderer.World.Junctions)
        {
            foreach (SplinePortWorldLocation port in entry.Ports)
            {
                if (!string.IsNullOrEmpty(excludeElementId)
                    && string.Equals(port.PortRef.ElementId, excludeElementId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (port.IsConnected)
                {
                    continue;
                }

                list.Add(new PortPickCandidate(port.PortRef, port.Chunk, port.LocalPosition, port.IsConnected));
            }
        }

        return list;
    }

    private void HandleConnectPortClick(ViewportRay ray)
    {
        if (Workspace is null)
        {
            return;
        }

        PortReferenceModel? pendingSource = Workspace.PendingConnectSourcePort.Value;
        if (pendingSource is null)
        {
            List<PortPickCandidate> freeSources = CollectPortCandidates(onlyFreeConnectSources: true, excludeElementId: null);
            PortPickHit? sourceHit = ViewportPickingAndGizmoService.PickNearestPort(ray, freeSources);
            if (!sourceHit.HasValue)
            {
                Workspace.LastStatusMessage.Value = "接続元にする自由 Outlet またはジャンクションポートをクリックしてください。";
                return;
            }

            Workspace.PendingConnectSourcePort.Value = sourceHit.Value.PortRef;
            if (sourceHit.Value.PortRef.Kind == PortTargetKind.JunctionPort)
            {
                Workspace.SelectJunction(sourceHit.Value.PortRef.ElementId);
            }
            else
            {
                Workspace.SelectSpline(sourceHit.Value.PortRef.ElementId);
            }

            string portLabel = string.IsNullOrEmpty(sourceHit.Value.PortRef.PortKey)
                ? "Outlet"
                : sourceHit.Value.PortRef.PortKey;
            Workspace.LastStatusMessage.Value = $"接続元 '{sourceHit.Value.PortRef.ElementId}.{portLabel}' を選択しました。接続先ポートをクリックしてください (Escで解除)。";
            return;
        }

        List<PortPickCandidate> targetCandidates = CollectPortCandidates(onlyFreeConnectSources: false, excludeElementId: pendingSource.ElementId);
        PortPickHit? targetHit = ViewportPickingAndGizmoService.PickNearestPort(ray, targetCandidates);
        Workspace.PendingConnectSourcePort.Value = null;

        if (!targetHit.HasValue)
        {
            Workspace.LastStatusMessage.Value = "ターゲットポートがヒットしなかったため、ポート接続を解除しました。";
            return;
        }

        if (pendingSource.Kind == PortTargetKind.SplineOutlet)
        {
            Workspace.SetSplineConnectBezier(
                pendingSource.ElementId,
                new SplineConnectBezierModel(targetHit.Value.PortRef, 0.5));
        }
        else
        {
            Workspace.AddExplicitPortConnection(pendingSource, targetHit.Value.PortRef);
        }

        Workspace.ActiveToolMode.Value = EditorToolMode.Select;
    }

    private void BeginPropGizmoDrag(
        ChunkPropPlacement prop,
        GizmoAxis axis,
        ViewportRay ray,
        Vector3 gizmoCenterRelCamera)
    {
        IsDraggingGizmo = true;
        ActiveGizmoAxis = axis;
        HoveredGizmoAxis = axis;
        DraggingPropId = prop.Id;
        DraggingSplineId = null;
        DraggingJunctionId = null;
        DragStrokeId = Guid.NewGuid().ToString("N");
        DragStartPose = new WorldPoseDegreesModel(prop.Chunk, prop.Pose);
        DragGizmoOriginRelCamera = gizmoCenterRelCamera;

        if (axis == GizmoAxis.X || axis == GizmoAxis.Y || axis == GizmoAxis.Z)
        {
            Vector3 axisDir = GetAxisUnitVector(axis);
            ViewportPickingAndGizmoService.TryComputeAxisLineParameter(
                ray,
                gizmoCenterRelCamera,
                axisDir,
                out DragStartAxisParam);
        }
        else if (axis == GizmoAxis.Yaw)
        {
            ViewportPickingAndGizmoService.TryComputeYawAngleDegrees(
                ray,
                gizmoCenterRelCamera,
                out DragStartYawAngleDeg);
        }
    }

    private void BeginSplineGizmoDrag(
        SplinePlacementNode spline,
        GizmoAxis axis,
        ViewportRay ray,
        Vector3 gizmoCenterRelCamera)
    {
        IsDraggingGizmo = true;
        ActiveGizmoAxis = axis;
        HoveredGizmoAxis = axis;
        DraggingPropId = null;
        DraggingSplineId = spline.Id;
        DraggingJunctionId = null;
        DragStrokeId = Guid.NewGuid().ToString("N");
        DragStartPose = new WorldPoseDegreesModel(spline.Origin.Chunk, spline.Origin.Pose);
        DragGizmoOriginRelCamera = gizmoCenterRelCamera;

        if (axis == GizmoAxis.X || axis == GizmoAxis.Y || axis == GizmoAxis.Z)
        {
            Vector3 axisDir = GetAxisUnitVector(axis);
            ViewportPickingAndGizmoService.TryComputeAxisLineParameter(
                ray,
                gizmoCenterRelCamera,
                axisDir,
                out DragStartAxisParam);
        }
        else if (axis == GizmoAxis.Yaw)
        {
            ViewportPickingAndGizmoService.TryComputeYawAngleDegrees(
                ray,
                gizmoCenterRelCamera,
                out DragStartYawAngleDeg);
        }
    }

    private void BeginJunctionGizmoDrag(
        JunctionPlacementNode junction,
        GizmoAxis axis,
        ViewportRay ray,
        Vector3 gizmoCenterRelCamera)
    {
        IsDraggingGizmo = true;
        ActiveGizmoAxis = axis;
        HoveredGizmoAxis = axis;
        DraggingPropId = null;
        DraggingSplineId = null;
        DraggingJunctionId = junction.Id;
        DragStrokeId = Guid.NewGuid().ToString("N");
        DragStartPose = new WorldPoseDegreesModel(junction.Origin.Chunk, junction.Origin.Pose);
        DragGizmoOriginRelCamera = gizmoCenterRelCamera;

        if (axis == GizmoAxis.X || axis == GizmoAxis.Y || axis == GizmoAxis.Z)
        {
            Vector3 axisDir = GetAxisUnitVector(axis);
            ViewportPickingAndGizmoService.TryComputeAxisLineParameter(
                ray,
                gizmoCenterRelCamera,
                axisDir,
                out DragStartAxisParam);
        }
        else if (axis == GizmoAxis.Yaw)
        {
            ViewportPickingAndGizmoService.TryComputeYawAngleDegrees(
                ray,
                gizmoCenterRelCamera,
                out DragStartYawAngleDeg);
        }
    }

    private void OnMouseMove(int pixelX, int pixelY, int deltaX, int deltaY)
    {
        if (Renderer is null)
        {
            return;
        }

        SizeI clientSize = new SizeI(Renderer.PixelWidth, Renderer.PixelHeight);

        if (IsRightDown)
        {
            Renderer.CameraController.RotateRightDrag(new Vector2(deltaX, deltaY), clientSize);
            return;
        }

        if (IsMiddleDown)
        {
            Renderer.CameraController.PanMiddleDrag(new Vector2(deltaX, deltaY), clientSize);
            return;
        }

        if (Renderer.ActiveMode == EditorRenderMode.ScriptVerification)
        {
            return;
        }

        if (Workspace is null)
        {
            return;
        }

        ViewContext viewCtx = Renderer.CreateViewContext();
        ViewportRay ray = ViewportPickingAndGizmoService.CreateRay(
            pixelX,
            pixelY,
            Renderer.PixelWidth,
            Renderer.PixelHeight,
            viewCtx);

        if (IsLeftDown && IsDraggingGizmo && (DraggingPropId is not null || DraggingSplineId is not null || DraggingJunctionId is not null))
        {
            UpdateActiveGizmoDrag(ray);
            return;
        }

        EditorToolMode tool = Workspace.ActiveToolMode.Value;
        if (tool == EditorToolMode.PlaceProp || tool == EditorToolMode.PlaceSpline || tool == EditorToolMode.PlaceJunction)
        {
            WorldPoseDegreesModel groundPose = ViewportPickingAndGizmoService.SnapGroundPlacement(
                ray,
                Workspace.GridSnapMeters.Value,
                0f);
            PlacementCursorRelCamera = WorldCoordinateHelper.ToChunkRelativePosition(
                ray.OriginChunk,
                groundPose.Chunk,
                new Vector3((float)groundPose.LocalPose.X, (float)groundPose.LocalPose.Y, (float)groundPose.LocalPose.Z));
            HoveredGizmoAxis = GizmoAxis.None;
            return;
        }

        PlacementCursorRelCamera = null;

        ChunkPropPlacement? selectedProp = Workspace.FindProp(Workspace.SelectedPropId.Value);
        if (selectedProp is not null && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
        {
            Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                ray.OriginChunk,
                selectedProp.Chunk,
                new Vector3((float)selectedProp.Pose.X, (float)selectedProp.Pose.Y, (float)selectedProp.Pose.Z));
            HoveredGizmoAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
        }
        else
        {
            SplinePlacementNode? selectedSpline = Workspace.FindSpline(Workspace.SelectedSplineId.Value);
            if (selectedSpline is not null
                && selectedSpline.Origin.Kind == SplineOriginKind.ChunkPose
                && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
            {
                Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                    ray.OriginChunk,
                    selectedSpline.Origin.Chunk,
                    new Vector3((float)selectedSpline.Origin.Pose.X, (float)selectedSpline.Origin.Pose.Y, (float)selectedSpline.Origin.Pose.Z));
                HoveredGizmoAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
            }
            else
            {
                JunctionPlacementNode? selectedJunction = Workspace.FindJunction(Workspace.SelectedJunctionId.Value);
                if (selectedJunction is not null
                    && selectedJunction.Origin.Kind == JunctionOriginKind.ChunkPose
                    && (tool == EditorToolMode.Translate || tool == EditorToolMode.Rotate))
                {
                    Vector3 gizmoCenter = WorldCoordinateHelper.ToChunkRelativePosition(
                        ray.OriginChunk,
                        selectedJunction.Origin.Chunk,
                        new Vector3((float)selectedJunction.Origin.Pose.X, (float)selectedJunction.Origin.Pose.Y, (float)selectedJunction.Origin.Pose.Z));
                    HoveredGizmoAxis = ViewportPickingAndGizmoService.HitTestGizmo(ray, gizmoCenter, tool);
                }
                else
                {
                    HoveredGizmoAxis = GizmoAxis.None;
                }
            }
        }
    }

    private void UpdateActiveGizmoDrag(ViewportRay ray)
    {
        if (Renderer is null || Workspace is null || (DraggingPropId is null && DraggingSplineId is null && DraggingJunctionId is null))
        {
            return;
        }

        WorldPoseDegreesModel updatedPose = DragStartPose;

        if (ActiveGizmoAxis == GizmoAxis.X || ActiveGizmoAxis == GizmoAxis.Y || ActiveGizmoAxis == GizmoAxis.Z)
        {
            Vector3 axisDir = GetAxisUnitVector(ActiveGizmoAxis);
            if (!ViewportPickingAndGizmoService.TryComputeAxisLineParameter(
                    ray,
                    DragGizmoOriginRelCamera,
                    axisDir,
                    out float currentParam))
            {
                return;
            }

            float deltaMeters = currentParam - DragStartAxisParam;
            updatedPose = ViewportPickingAndGizmoService.ApplyAxisTranslation(
                DragStartPose,
                ActiveGizmoAxis,
                deltaMeters,
                Workspace.GridSnapMeters.Value);
        }
        else if (ActiveGizmoAxis == GizmoAxis.Yaw)
        {
            if (!ViewportPickingAndGizmoService.TryComputeYawAngleDegrees(
                    ray,
                    DragGizmoOriginRelCamera,
                    out float currentAngleDeg))
            {
                return;
            }

            float deltaDeg = currentAngleDeg - DragStartYawAngleDeg;
            updatedPose = ViewportPickingAndGizmoService.ApplyYawRotation(
                DragStartPose,
                deltaDeg,
                Workspace.AngleSnapDegrees.Value);
        }

        if (DraggingPropId is not null)
        {
            string mergeKey = $"GizmoDrag.{DraggingPropId}.{DragStrokeId}";
            SuppressNextRevisionSync = true;
            try
            {
                if (Workspace.SetPropPose(DraggingPropId, updatedPose, mergeKey))
                {
                    ChunkPropPlacement? updatedProp = Workspace.FindProp(DraggingPropId);
                    if (updatedProp is not null)
                    {
                        Renderer.World.UpdatePropPose(updatedProp);
                    }
                }
            }
            finally
            {
                SuppressNextRevisionSync = false;
            }
        }
        else if (DraggingSplineId is not null)
        {
            string mergeKey = $"GizmoDrag.Spline.{DraggingSplineId}.{DragStrokeId}";
            Workspace.SetSplineOriginPose(DraggingSplineId, updatedPose.Chunk, updatedPose.LocalPose, mergeKey);
        }
        else if (DraggingJunctionId is not null)
        {
            string mergeKey = $"GizmoDrag.Junction.{DraggingJunctionId}.{DragStrokeId}";
            Workspace.MoveJunctionChunkPose(DraggingJunctionId, updatedPose, mergeKey);
        }
    }

    private static Vector3 GetAxisUnitVector(GizmoAxis axis)
    {
        return axis switch
        {
            GizmoAxis.X => Vector3.UnitX,
            GizmoAxis.Y => Vector3.UnitY,
            GizmoAxis.Z => Vector3.UnitZ,
            _ => Vector3.UnitX,
        };
    }

    private static (int X, int Y) UnpackPoint(nint lParam)
    {
        uint low32 = unchecked((uint)lParam.ToInt64());
        int x = unchecked((short)(low32 & 0xFFFFu));
        int y = unchecked((short)(low32 / 65536u));
        return (x, y);
    }

    private static short HighWordSigned(nint value)
    {
        uint low32 = unchecked((uint)value.ToInt64());
        return unchecked((short)(low32 / 65536u));
    }
}
