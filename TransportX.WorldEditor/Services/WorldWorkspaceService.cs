using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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
using TransportX.WorldEditor.Rendering.Verification;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Serialization.Xml;

namespace TransportX.WorldEditor.Services;

/// <summary>
/// Application-wide keyboard shortcuts routed cleanly from both Avalonia UI controls and the
/// native Win32 3D viewport child window.
/// </summary>
public enum EditorGlobalShortcut
{
    Save,
    SaveAll,
    NewWorld,
    OpenWorld,
    Undo,
    Redo,
    Verify,
    LaunchPlayer,
}

/// <summary>
/// Central state &amp; persistence coordinator for the currently opened <see cref="WorldProject"/>,
/// integrating <see cref="CommandHistory"/>, <see cref="WorldValidator"/>, and <see cref="WorldProjectStorage"/>.
/// </summary>
public sealed class WorldWorkspaceService : IDisposable
{
    private readonly CompositeDisposable Disposables = new();
    private readonly WorldFileWatcherService FileWatcher = new();
    private readonly object BackgroundCompileLock = new();
    private CancellationTokenSource? BackgroundCompileCts;
    private long BackgroundCompileGeneration;
    private bool SuppressDirtyTracking;
    private bool SuppressSelectionSync;
    private bool SuppressSidecarSync;

    public CommandHistory CommandHistory { get; } = new();

    public BindableReactiveProperty<WorldProject?> CurrentProject { get; }
    public BindableReactiveProperty<int> ProjectRevision { get; }
    public BindableReactiveProperty<IReadOnlyList<ParseDiagnostic>> ParseDiagnostics { get; }
    public BindableReactiveProperty<IReadOnlyList<ValidationIssue>> ValidationIssues { get; }
    public BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>> RuntimeErrors { get; }
    public BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>> VerificationErrors { get; }
    public BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>> BackgroundScriptDiagnostics { get; }
    public BindableReactiveProperty<bool> IsBackgroundCompiling { get; }
    public BindableReactiveProperty<bool> IsVerificationStarting { get; }
    public BindableReactiveProperty<EditorRenderMode> ActiveRenderMode { get; }
    public BindableReactiveProperty<string> VerificationSummaryText { get; }
    public BindableReactiveProperty<string> LastStatusMessage { get; }

    public bool EnableBackgroundScriptCompilation { get; set; } = true;
    public int BackgroundCompileDebounceMs { get; set; } = 800;
    public Action<Action>? UiThreadDispatcher { get; set; }
    internal TimeSpan FileWatcherSaveSuppressionDuration { get; set; } = TimeSpan.FromMilliseconds(1500);

    public BindableReactiveProperty<string?> SelectedPropId { get; }
    public BindableReactiveProperty<string?> SelectedSplineId { get; }
    public BindableReactiveProperty<string?> SelectedJunctionId { get; }
    public BindableReactiveProperty<int> SelectedBackgroundModelIndex { get; }
    public BindableReactiveProperty<EditorToolMode> ActiveToolMode { get; }
    public BindableReactiveProperty<string> PlacementModelKey { get; }
    public BindableReactiveProperty<string> PlacementSplineTemplateKey { get; }
    public BindableReactiveProperty<string> PlacementJunctionTemplateKey { get; }
    public BindableReactiveProperty<PortReferenceModel?> PendingConnectSourcePort { get; }
    public BindableReactiveProperty<double> GridSnapMeters { get; }
    public BindableReactiveProperty<double> AngleSnapDegrees { get; }
    public BindableReactiveProperty<ViewportOverlayFlags> OverlayFlags { get; }
    public BindableReactiveProperty<string> CameraStatusText { get; }

    internal WorldProject? SavedCameraProject { get; private set; }
    internal TransportX.Cameras.CameraPose? SavedCameraPose { get; private set; }

    public Func<string, WorldPoseDegreesModel?>? ResolveSplineFocusPose { get; set; }
    public Func<string, WorldPoseDegreesModel?>? ResolveJunctionFocusPose { get; set; }
    public Func<CameraBookmarkModel?>? CaptureCurrentCameraPose { get; set; }

    public event Action<WorldPoseDegreesModel>? FocusCameraRequested;
    public event Action<int, int>? ChunkJumpRequested;
    public event Action<WorldProject>? StartVerificationRequested;
    public event Action? CancelVerificationStartRequested;
    public event Action? StopVerificationRequested;
    public event Action<CameraBookmarkModel>? ApplyCameraBookmarkRequested;
    public event Action? ActivateViewportRequested;
    public event Action<EditorGlobalShortcut>? GlobalShortcutRequested;

    public void RequestGlobalShortcut(EditorGlobalShortcut shortcut)
    {
        GlobalShortcutRequested?.Invoke(shortcut);
    }

    public WorldWorkspaceService()
    {
        CurrentProject = new BindableReactiveProperty<WorldProject?>(null).AddTo(Disposables);
        ProjectRevision = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        ParseDiagnostics = new BindableReactiveProperty<IReadOnlyList<ParseDiagnostic>>(Array.Empty<ParseDiagnostic>()).AddTo(Disposables);
        ValidationIssues = new BindableReactiveProperty<IReadOnlyList<ValidationIssue>>(Array.Empty<ValidationIssue>()).AddTo(Disposables);
        RuntimeErrors = new BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>>(Array.Empty<TransportX.Diagnostics.Error>()).AddTo(Disposables);
        VerificationErrors = new BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>>(Array.Empty<TransportX.Diagnostics.Error>()).AddTo(Disposables);
        BackgroundScriptDiagnostics = new BindableReactiveProperty<IReadOnlyList<TransportX.Diagnostics.Error>>(Array.Empty<TransportX.Diagnostics.Error>()).AddTo(Disposables);
        IsBackgroundCompiling = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsVerificationStarting = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ActiveRenderMode = new BindableReactiveProperty<EditorRenderMode>(EditorRenderMode.EditLivePreview).AddTo(Disposables);
        VerificationSummaryText = new BindableReactiveProperty<string>("編集モード").AddTo(Disposables);
        LastStatusMessage = new BindableReactiveProperty<string>("準備完了").AddTo(Disposables);

        SelectedPropId = new BindableReactiveProperty<string?>(null).AddTo(Disposables);
        SelectedSplineId = new BindableReactiveProperty<string?>(null).AddTo(Disposables);
        SelectedJunctionId = new BindableReactiveProperty<string?>(null).AddTo(Disposables);
        SelectedBackgroundModelIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        ActiveToolMode = new BindableReactiveProperty<EditorToolMode>(EditorToolMode.Select).AddTo(Disposables);
        PlacementModelKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PlacementSplineTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PlacementJunctionTemplateKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PendingConnectSourcePort = new BindableReactiveProperty<PortReferenceModel?>(null).AddTo(Disposables);
        GridSnapMeters = new BindableReactiveProperty<double>(0.5).AddTo(Disposables);
        AngleSnapDegrees = new BindableReactiveProperty<double>(15.0).AddTo(Disposables);
        OverlayFlags = new BindableReactiveProperty<ViewportOverlayFlags>(ViewportOverlayFlags.Default).AddTo(Disposables);
        CameraStatusText = new BindableReactiveProperty<string>("Camera Chunk(0,0) (125.0, 10.0, 125.0)").AddTo(Disposables);

        OverlayFlags.Skip(1).Subscribe(flags =>
        {
            if (SuppressSidecarSync || SuppressDirtyTracking)
            {
                return;
            }

            WorldProject? project = CurrentProject.Value;
            if (project is null)
            {
                return;
            }

            VisibilityLayerSettings updated = new(
                flags.HasFlag(ViewportOverlayFlags.Normal),
                flags.HasFlag(ViewportOverlayFlags.Network),
                flags.HasFlag(ViewportOverlayFlags.Colliders),
                flags.HasFlag(ViewportOverlayFlags.ChunkGrid),
                flags.HasFlag(ViewportOverlayFlags.Anchors),
                flags.HasFlag(ViewportOverlayFlags.Gizmos),
                flags.HasFlag(ViewportOverlayFlags.Traffic));
            if (project.Sidecar.VisibilityLayers != updated)
            {
                project.Sidecar.VisibilityLayers = updated;
                project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
                ProjectRevision.Value++;
            }
        }).AddTo(Disposables);

        SelectedPropId.Subscribe(id =>
        {
            if (id is not null && !SuppressSelectionSync)
            {
                SuppressSelectionSync = true;
                try
                {
                    SelectedSplineId.Value = null;
                    SelectedJunctionId.Value = null;
                    SelectedBackgroundModelIndex.Value = -1;
                    PendingConnectSourcePort.Value = null;
                }
                finally
                {
                    SuppressSelectionSync = false;
                }
            }
        }).AddTo(Disposables);

        SelectedSplineId.Subscribe(id =>
        {
            if (id is not null && !SuppressSelectionSync)
            {
                SuppressSelectionSync = true;
                try
                {
                    SelectedPropId.Value = null;
                    SelectedJunctionId.Value = null;
                    SelectedBackgroundModelIndex.Value = -1;
                }
                finally
                {
                    SuppressSelectionSync = false;
                }
            }
        }).AddTo(Disposables);

        SelectedJunctionId.Subscribe(id =>
        {
            if (id is not null && !SuppressSelectionSync)
            {
                SuppressSelectionSync = true;
                try
                {
                    SelectedPropId.Value = null;
                    SelectedSplineId.Value = null;
                    SelectedBackgroundModelIndex.Value = -1;
                }
                finally
                {
                    SuppressSelectionSync = false;
                }
            }
        }).AddTo(Disposables);

        SelectedBackgroundModelIndex.Subscribe(idx =>
        {
            if (0 <= idx && !SuppressSelectionSync)
            {
                SuppressSelectionSync = true;
                try
                {
                    SelectedPropId.Value = null;
                    SelectedSplineId.Value = null;
                    SelectedJunctionId.Value = null;
                    PendingConnectSourcePort.Value = null;
                }
                finally
                {
                    SuppressSelectionSync = false;
                }
            }
        }).AddTo(Disposables);

        ActiveToolMode.Subscribe(mode =>
        {
            if (mode != EditorToolMode.ConnectPort && PendingConnectSourcePort.Value is not null)
            {
                PendingConnectSourcePort.Value = null;
            }
        }).AddTo(Disposables);

        CommandHistory.HistoryChanged += OnCommandHistoryChanged;
        FileWatcher.FileExternallyChanged += OnExternalFileChanged;
    }

    public WorldProjectLoadResult OpenWorld(string worldInfoPath)
    {
        ArgumentNullException.ThrowIfNull(worldInfoPath);

        WorldProjectLoadResult result = WorldProjectStorage.Load(worldInfoPath);

        SuppressDirtyTracking = true;
        try
        {
            if (ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
            {
                StopVerificationRequested?.Invoke();
            }

            ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
            VerificationErrors.Value = Array.Empty<TransportX.Diagnostics.Error>();
            VerificationSummaryText.Value = "編集モード";
            CommandHistory.Clear();
            SelectedPropId.Value = null;
            SelectedSplineId.Value = null;
            SelectedJunctionId.Value = null;
            SelectedBackgroundModelIndex.Value = -1;
            PendingConnectSourcePort.Value = null;
            SavedCameraProject = null;
            SavedCameraPose = null;
            CurrentProject.Value = result.Project;
            PlacementSplineTemplateKey.Value = GetDefaultSplineTemplateKey(result.Project);
            PlacementJunctionTemplateKey.Value = GetDefaultJunctionTemplateKey(result.Project);
            ParseDiagnostics.Value = result.Diagnostics;
            BackgroundScriptDiagnostics.Value = Array.Empty<TransportX.Diagnostics.Error>();
            ApplySidecarToState(result.Project);
        }
        finally
        {
            SuppressDirtyTracking = false;
        }

        RefreshValidation();
        StartWatchingProject(result.Project);
        ScheduleBackgroundScriptCompile();
        ProjectRevision.Value++;

        int errCount = CountErrors(result.Diagnostics, ValidationIssues.Value);
        LastStatusMessage.Value = 0 < result.Project.ProtectedFiles.Count
            ? $"'{result.Project.WorldInfo.Title}' を開きました (上書き保護ファイル: {result.Project.ProtectedFiles.Count} 件, エラー: {errCount} 件)。"
            : $"'{result.Project.WorldInfo.Title}' を開きました (エラー: {errCount} 件)。";

        return result;
    }

    public WorldProject CreateNewWorld(
        string parentDirectory,
        string author,
        string worldName,
        string title,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);

        string worldDir = Path.Combine(parentDirectory, author, worldName);
        string worldInfoPath = Path.Combine(parentDirectory, $"{author}.{worldName}.xml");

        WorldProject project = new WorldProject
        {
            WorldInfoFilePath = worldInfoPath,
            WorldDirectory = worldDir,
        };

        project.WorldInfo.Title = string.IsNullOrWhiteSpace(title) ? worldName : title;
        project.WorldInfo.Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description;
        project.WorldInfo.Author = author;
        project.WorldInfo.Path = "$(bin)TransportX.Scripting.dll";
        project.WorldInfo.Args.Add(Path.Combine(author, worldName, "World.csx"));

        project.Manifest.Dependencies.Add("$(plugins)TransportX.Domains.RoadTraffic.dll");

        ModelListDocument modelList = new ModelListDocument { RelativePath = "Models.txt" };
        project.ModelLists["Models.txt"] = modelList;

        SoundListDocument soundList = new SoundListDocument { RelativePath = "Sounds.txt" };
        project.SoundLists["Sounds.txt"] = soundList;

        LaneLayoutDocument layout1 = CreateDefaultSymmetricLayout("Layout1", "LaneLayouts/Layout1.xml");
        project.LaneLayouts[layout1.Key] = layout1;

        CrossSectionListDocument crossSectionList = new() { RelativePath = "CrossSections.txt" };
        crossSectionList.Entries.Add(new CrossSectionListEntry("Road1L", "CrossSections/Road1L.xml"));
        project.CrossSectionLists["CrossSections.txt"] = crossSectionList;

        CrossSectionDocument road1L = CreateDefaultRoadCrossSection("Road1L", "CrossSections/Road1L.xml");
        project.CrossSections[road1L.Key] = road1L;

        EnvironmentProfileDocument env1 = new EnvironmentProfileDocument { RelativePath = "Environment1.xml" };
        project.Environments["Environment1.xml"] = env1;

        project.Init.ModelLists.Add(new AssetListLoadReference(null, "Models.txt"));
        project.Init.SoundLists.Add(new AssetListLoadReference(null, "Sounds.txt"));
        project.Init.CrossSectionLists.Add("CrossSections.txt");
        project.Init.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("Pedestrians", "歩行者", "#FFFF00"));
        project.Init.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("Buses", "バス", "#00FF00"));
        project.Init.LaneTraffic.Types.Add(new LaneTrafficTypeDefinition("NormalCars", "その他自動車", "#0000FF"));
        project.Init.LaneTraffic.Groups.Add(new LaneTrafficGroupDefinition("Cars", new List<string> { "Buses", "NormalCars" }, "#00FFFF"));
        project.Init.LaneLayouts.Add(new ResourceLoadReference("Layout1", "LaneLayouts/Layout1.xml"));

        SplineTemplateDocument spline1 = new SplineTemplateDocument
        {
            Key = "Spline1",
            RelativePath = "Templates/Splines/Spline1.csx",
            OutletLayoutKey = "Layout1",
        };
        spline1.Extrusions.Add(new SplineTemplateExtrusionRule
        {
            Key = "RoadSurface",
            CrossSectionKey = "Road1L",
        });
        project.SplineTemplates[spline1.Key] = spline1;

        project.Placement.EnvironmentPath = "Environment1.xml";
        SplinePlacementNode starterSpline = new SplinePlacementNode
        {
            Id = "spline_001",
            Key = "spline_001",
            TemplateKey = "Spline1",
            Origin = SplineOriginModel.FromChunk(ChunkIndexModel.Zero, PoseDegreesModel.Identity),
        };
        starterSpline.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 50.0, null));
        project.Placement.Splines.Add(starterSpline);

        WorldProjectStorage.Save(project, dirtyOnly: false);

        SuppressDirtyTracking = true;
        try
        {
            if (ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
            {
                StopVerificationRequested?.Invoke();
            }

            ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
            VerificationErrors.Value = Array.Empty<TransportX.Diagnostics.Error>();
            VerificationSummaryText.Value = "編集モード";
            CommandHistory.Clear();
            SelectedPropId.Value = null;
            SelectedSplineId.Value = null;
            SelectedJunctionId.Value = null;
            SelectedBackgroundModelIndex.Value = -1;
            PendingConnectSourcePort.Value = null;
            SavedCameraProject = null;
            SavedCameraPose = null;
            CurrentProject.Value = project;
            PlacementSplineTemplateKey.Value = GetDefaultSplineTemplateKey(project);
            PlacementJunctionTemplateKey.Value = GetDefaultJunctionTemplateKey(project);
            ParseDiagnostics.Value = Array.Empty<ParseDiagnostic>();
            BackgroundScriptDiagnostics.Value = Array.Empty<TransportX.Diagnostics.Error>();
            ApplySidecarToState(project);
        }
        finally
        {
            SuppressDirtyTracking = false;
        }

        RefreshValidation();
        StartWatchingProject(project);
        ScheduleBackgroundScriptCompile();
        ProjectRevision.Value++;
        LastStatusMessage.Value = $"新規ワールド '{project.WorldInfo.Title}' を作成しました。";
        return project;
    }

    internal void SaveCameraPose(WorldProject project, TransportX.Cameras.CameraPose pose)
    {
        ArgumentNullException.ThrowIfNull(project);
        SavedCameraProject = project;
        SavedCameraPose = pose;
    }

    public WorldProjectSaveResult Save(bool dirtyOnly)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return new WorldProjectSaveResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParseDiagnostic>());
        }

        CameraBookmarkModel? latestCam = CaptureCurrentCameraPose?.Invoke();
        if (latestCam is null && SavedCameraProject == project && SavedCameraPose is TransportX.Cameras.CameraPose storedCam)
        {
            latestCam = new CameraBookmarkModel(
                "Last",
                storedCam.Chunk.X,
                storedCam.Chunk.Z,
                storedCam.Position.X,
                storedCam.Position.Y,
                storedCam.Position.Z,
                double.RadiansToDegrees(storedCam.Angle.X),
                double.RadiansToDegrees(storedCam.Angle.Y),
                0.0);
        }

        if (latestCam is not null && !Equals(project.Sidecar.LastCameraPose, latestCam))
        {
            project.Sidecar.LastCameraPose = latestCam;
            project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
        }

        List<string> candidatePaths = CollectSaveCandidatePaths(project, dirtyOnly);
        FileWatcher.SuppressPaths(candidatePaths, FileWatcherSaveSuppressionDuration);
        WorldProjectSaveResult result = WorldProjectStorage.Save(project, dirtyOnly);
        FileWatcher.SuppressPaths(result.WrittenFiles, FileWatcherSaveSuppressionDuration);
        ScheduleBackgroundScriptCompile();
        ProjectRevision.Value++;

        LastStatusMessage.Value = 0 < result.SkippedProtectedFiles.Count
            ? $"{result.WrittenFiles.Count} 件を保存 / {result.SkippedProtectedFiles.Count} 件は上書き保護のためスキップしました。"
            : $"{result.WrittenFiles.Count} 件のファイルを保存しました。";

        return result;
    }

    private void ApplySidecarToState(WorldProject project)
    {
        SuppressSidecarSync = true;
        try
        {
            VisibilityLayerSettings vl = project.Sidecar.VisibilityLayers;
            ViewportOverlayFlags flags = ViewportOverlayFlags.None;
            if (vl.Normal)
            {
                flags |= ViewportOverlayFlags.Normal;
            }

            if (vl.Network)
            {
                flags |= ViewportOverlayFlags.Network;
            }

            if (vl.Colliders)
            {
                flags |= ViewportOverlayFlags.Colliders;
            }

            if (vl.ChunkGrid)
            {
                flags |= ViewportOverlayFlags.ChunkGrid;
            }

            if (vl.Anchors)
            {
                flags |= ViewportOverlayFlags.Anchors;
            }

            if (vl.Gizmos)
            {
                flags |= ViewportOverlayFlags.Gizmos;
            }

            if (vl.Traffic)
            {
                flags |= ViewportOverlayFlags.Traffic;
            }

            OverlayFlags.Value = flags;
        }
        finally
        {
            SuppressSidecarSync = false;
        }

        if (project.Sidecar.LastCameraPose is CameraBookmarkModel lastCam)
        {
            float pitchRad = float.DegreesToRadians((float)lastCam.RotationX);
            float yawRad = float.DegreesToRadians((float)lastCam.RotationY);
            SavedCameraProject = project;
            SavedCameraPose = new TransportX.Cameras.CameraPose(
                new TransportX.Spatial.ChunkIndex(lastCam.ChunkX, lastCam.ChunkZ),
                new System.Numerics.Vector3((float)lastCam.X, (float)lastCam.Y, (float)lastCam.Z),
                new System.Numerics.Vector2(pitchRad, yawRad));
        }
    }

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        CommandHistory.Execute(command);
    }

    public bool Undo()
    {
        return CommandHistory.Undo();
    }

    public bool Redo()
    {
        return CommandHistory.Redo();
    }

    public ChunkPropPlacement? FindProp(string? propId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrEmpty(propId))
        {
            return null;
        }

        for (int i = 0; i < project.Placement.Props.Count; i++)
        {
            if (string.Equals(project.Placement.Props[i].Id, propId, StringComparison.Ordinal))
            {
                return project.Placement.Props[i];
            }
        }

        return null;
    }

    public ChunkPropPlacement? AddPropPlacement(string modelKey, WorldPoseDegreesModel rawPose)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(modelKey))
        {
            return null;
        }

        WorldPoseDegreesModel normalized = rawPose.Normalized();
        string newId = GenerateNextPropId(project);
        ChunkPropPlacement created = new ChunkPropPlacement(newId, modelKey.Trim(), normalized.Chunk, normalized.LocalPose);
        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));

        Execute(new DelegateEditorCommand(
            $"プロップ '{newId}' ({created.ModelKey}) を配置",
            InvalidationScope.SingleElement,
            worldTarget,
            () =>
            {
                project.Placement.Props.Add(created);
                SelectedPropId.Value = created.Id;
            },
            () =>
            {
                RemovePropById(project, created.Id);
                if (string.Equals(SelectedPropId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedPropId.Value = null;
                }
            }));

        return created;
    }

    public bool RemovePropPlacement(string propId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(propId))
        {
            return false;
        }

        int index = -1;
        for (int i = 0; i < project.Placement.Props.Count; i++)
        {
            if (string.Equals(project.Placement.Props[i].Id, propId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        ChunkPropPlacement removed = project.Placement.Props[index];
        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));

        Execute(new DelegateEditorCommand(
            $"プロップ '{propId}' を削除",
            InvalidationScope.SingleElement,
            worldTarget,
            () =>
            {
                RemovePropById(project, removed.Id);
                if (string.Equals(SelectedPropId.Value, removed.Id, StringComparison.Ordinal))
                {
                    SelectedPropId.Value = null;
                }
            },
            () =>
            {
                int restoreIdx = Math.Clamp(index, 0, project.Placement.Props.Count);
                project.Placement.Props.Insert(restoreIdx, removed);
                SelectedPropId.Value = removed.Id;
            }));

        return true;
    }

    public bool SetPropPose(string propId, WorldPoseDegreesModel newPose, string? mergeKey = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(propId))
        {
            return false;
        }

        int index = FindPropIndex(project, propId);
        if (index < 0)
        {
            return false;
        }

        ChunkPropPlacement current = project.Placement.Props[index];
        WorldPoseDegreesModel oldPose = new WorldPoseDegreesModel(current.Chunk, current.Pose);
        WorldPoseDegreesModel normalized = newPose.Normalized();
        if (oldPose == normalized)
        {
            return false;
        }

        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
        string key = mergeKey ?? $"Prop.Pose.{propId}";

        Execute(new MergeableValueCommand<WorldPoseDegreesModel>(
            $"プロップ '{propId}' の座標/回転を変更",
            key,
            InvalidationScope.SingleElement,
            worldTarget,
            oldPose,
            normalized,
            applied =>
            {
                int curIdx = FindPropIndex(project, propId);
                if (0 <= curIdx)
                {
                    ChunkPropPlacement p = project.Placement.Props[curIdx];
                    project.Placement.Props[curIdx] = p with
                    {
                        Chunk = applied.Chunk,
                        Pose = applied.LocalPose,
                    };
                }
            }));

        return true;
    }

    public bool SetPropModelKey(string propId, string newModelKey)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(propId) || string.IsNullOrWhiteSpace(newModelKey))
        {
            return false;
        }

        int index = FindPropIndex(project, propId);
        if (index < 0)
        {
            return false;
        }

        ChunkPropPlacement oldProp = project.Placement.Props[index];
        string trimmed = newModelKey.Trim();
        if (string.Equals(oldProp.ModelKey, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
        Execute(new DelegateEditorCommand(
            $"プロップ '{propId}' のモデルを変更",
            InvalidationScope.SingleElement,
            worldTarget,
            () =>
            {
                int curIdx = FindPropIndex(project, propId);
                if (0 <= curIdx)
                {
                    project.Placement.Props[curIdx] = project.Placement.Props[curIdx] with { ModelKey = trimmed };
                }
            },
            () =>
            {
                int curIdx = FindPropIndex(project, propId);
                if (0 <= curIdx)
                {
                    project.Placement.Props[curIdx] = project.Placement.Props[curIdx] with { ModelKey = oldProp.ModelKey };
                }
            }));

        return true;
    }

    public void SelectProp(string? propId)
    {
        SelectedPropId.Value = propId;
    }

    public void SelectSpline(string? splineId)
    {
        SelectedSplineId.Value = splineId;
    }

    public void SelectJunction(string? junctionId)
    {
        SelectedJunctionId.Value = junctionId;
    }

    public void SelectBackgroundModel(int index)
    {
        WorldProject? project = CurrentProject.Value;
        SelectedBackgroundModelIndex.Value = project is not null && 0 <= index && index < project.Placement.BackgroundModels.Count
            ? index
            : -1;
    }

    public void ClearSelection()
    {
        SelectedPropId.Value = null;
        SelectedSplineId.Value = null;
        SelectedJunctionId.Value = null;
        SelectedBackgroundModelIndex.Value = -1;
        PendingConnectSourcePort.Value = null;
    }

    public bool AddBackgroundModel(string modelKey)
    {
        WorldProject? project = CurrentProject.Value;
        string trimmed = modelKey.Trim();
        if (project is null || string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、背景モデルを追加できません。";
            return false;
        }

        int insertIndex = project.Placement.BackgroundModels.Count;
        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"背景モデル '{trimmed}' を追加",
            InvalidationScope.FullRebuild,
            worldTarget,
            () => project.Placement.BackgroundModels.Add(trimmed),
            () =>
            {
                if (0 <= insertIndex && insertIndex < project.Placement.BackgroundModels.Count)
                {
                    project.Placement.BackgroundModels.RemoveAt(insertIndex);
                }
            }));

        SelectBackgroundModel(project.Placement.BackgroundModels.Count - 1);
        return true;
    }

    public bool RemoveBackgroundModelAt(int index)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || index < 0 || project.Placement.BackgroundModels.Count <= index)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、背景モデルを削除できません。";
            return false;
        }

        string removed = project.Placement.BackgroundModels[index];
        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"背景モデル '{removed}' を削除",
            InvalidationScope.FullRebuild,
            worldTarget,
            () => project.Placement.BackgroundModels.RemoveAt(index),
            () => project.Placement.BackgroundModels.Insert(index, removed)));

        int nextSel = 0 < project.Placement.BackgroundModels.Count
            ? int.Clamp(index, 0, project.Placement.BackgroundModels.Count - 1)
            : -1;
        SelectBackgroundModel(nextSel);
        return true;
    }

    public bool SetBackgroundModelKey(int index, string newModelKey)
    {
        WorldProject? project = CurrentProject.Value;
        string trimmed = newModelKey.Trim();
        if (project is null || string.IsNullOrEmpty(trimmed) || index < 0 || project.Placement.BackgroundModels.Count <= index)
        {
            return false;
        }

        string oldKey = project.Placement.BackgroundModels[index];
        if (string.Equals(oldKey, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、背景モデルを変更できません。";
            return false;
        }

        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"背景モデル #{index} を '{trimmed}' に変更",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                if (0 <= index && index < project.Placement.BackgroundModels.Count)
                {
                    project.Placement.BackgroundModels[index] = trimmed;
                }
            },
            () =>
            {
                if (0 <= index && index < project.Placement.BackgroundModels.Count)
                {
                    project.Placement.BackgroundModels[index] = oldKey;
                }
            }));

        return true;
    }

    public bool MoveBackgroundModel(int oldIndex, int newIndex)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null
            || oldIndex < 0
            || newIndex < 0
            || project.Placement.BackgroundModels.Count <= oldIndex
            || project.Placement.BackgroundModels.Count <= newIndex
            || oldIndex == newIndex)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、背景モデルの順序を変更できません。";
            return false;
        }

        WorldFileTarget worldTarget = new WorldFileTarget(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"背景モデル #{oldIndex} の順序を変更",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                string item = project.Placement.BackgroundModels[oldIndex];
                project.Placement.BackgroundModels.RemoveAt(oldIndex);
                project.Placement.BackgroundModels.Insert(newIndex, item);
            },
            () =>
            {
                string item = project.Placement.BackgroundModels[newIndex];
                project.Placement.BackgroundModels.RemoveAt(newIndex);
                project.Placement.BackgroundModels.Insert(oldIndex, item);
            }));

        SelectBackgroundModel(newIndex);
        return true;
    }

    public void RequestActivateViewport()
    {
        ActivateViewportRequested?.Invoke();
    }

    public void StartPlacementMode(EditorToolMode mode)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        if (mode == EditorToolMode.PlaceProp && string.IsNullOrWhiteSpace(PlacementModelKey.Value))
        {
            foreach (ModelListDocument list in project.ModelLists.Values)
            {
                string? firstModel = list.Entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Key))?.Key;
                if (!string.IsNullOrEmpty(firstModel))
                {
                    PlacementModelKey.Value = firstModel;
                    break;
                }
            }
        }
        else if (mode == EditorToolMode.PlaceSpline && string.IsNullOrWhiteSpace(PlacementSplineTemplateKey.Value))
        {
            string? firstSpline = project.SplineTemplates.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (!string.IsNullOrEmpty(firstSpline))
            {
                PlacementSplineTemplateKey.Value = firstSpline;
            }
        }
        else if (mode == EditorToolMode.PlaceJunction && string.IsNullOrWhiteSpace(PlacementJunctionTemplateKey.Value))
        {
            string? firstJunc = project.JunctionTemplates.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (!string.IsNullOrEmpty(firstJunc))
            {
                PlacementJunctionTemplateKey.Value = firstJunc;
            }
            else
            {
                LastStatusMessage.Value = "配置可能な JunctionTemplate がありません。アセットブラウザから先に作成してください。";
            }
        }

        ActiveToolMode.Value = mode;
        RequestActivateViewport();
    }

    public void RequestFocusSelectedOrDefault()
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        ChunkPropPlacement? selectedProp = FindProp(SelectedPropId.Value);
        if (selectedProp is not null)
        {
            FocusCameraRequested?.Invoke(new WorldPoseDegreesModel(selectedProp.Chunk, selectedProp.Pose));
            return;
        }

        SplinePlacementNode? selectedSpline = FindSpline(SelectedSplineId.Value);
        if (selectedSpline is not null)
        {
            WorldPoseDegreesModel? resolvedPose = ResolveSplineFocusPose?.Invoke(selectedSpline.Id);
            if (resolvedPose.HasValue)
            {
                FocusCameraRequested?.Invoke(resolvedPose.Value);
                return;
            }

            if (selectedSpline.Origin.Kind == SplineOriginKind.ChunkPose)
            {
                FocusCameraRequested?.Invoke(new WorldPoseDegreesModel(selectedSpline.Origin.Chunk, selectedSpline.Origin.Pose));
                return;
            }
        }

        JunctionPlacementNode? selectedJunction = FindJunction(SelectedJunctionId.Value);
        if (selectedJunction is not null)
        {
            WorldPoseDegreesModel? resolvedPose = ResolveJunctionFocusPose?.Invoke(selectedJunction.Id);
            if (resolvedPose.HasValue)
            {
                FocusCameraRequested?.Invoke(resolvedPose.Value);
                return;
            }

            if (selectedJunction.Origin.Kind == JunctionOriginKind.ChunkPose)
            {
                FocusCameraRequested?.Invoke(new WorldPoseDegreesModel(selectedJunction.Origin.Chunk, selectedJunction.Origin.Pose));
                return;
            }
        }

        FocusCameraRequested?.Invoke(project.Placement.Camera.Pose);
    }

    public SplinePlacementNode? FindSpline(string? splineId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrEmpty(splineId))
        {
            return null;
        }

        for (int i = 0; i < project.Placement.Splines.Count; i++)
        {
            if (string.Equals(project.Placement.Splines[i].Id, splineId, StringComparison.Ordinal))
            {
                return project.Placement.Splines[i];
            }
        }

        return null;
    }

    public SplinePlacementNode? AddSplineAtChunkPose(
        string? templateKey,
        WorldPoseDegreesModel rawPose,
        double initialLength = 50.0,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、スプラインを配置できません。";
            return null;
        }

        WorldPoseDegreesModel normalized = rawPose.Normalized();
        string newId = GenerateNextSplineId(project);
        string? resolvedTemplateKey = ResolvePlacementTemplateKey(templateKey);
        SplinePlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            Origin = SplineOriginModel.FromChunk(normalized.Chunk, normalized.LocalPose),
            TemplateKey = resolvedTemplateKey,
        };
        created.Curves.Add(new SplineCurveSegment(
            SplineCurveSegmentKind.Straight,
            0.0,
            double.Max(1.0, initialLength),
            null));

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"スプライン '{newId}' を配置",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Splines.Add(created);
                SelectedSplineId.Value = created.Id;
                LastStatusMessage.Value = $"スプライン '{created.Id}' を配置しました。";
            },
            () =>
            {
                RemoveSplineById(project, created.Id);
                if (string.Equals(SelectedSplineId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedSplineId.Value = null;
                }
            }));

        return created;
    }

    public SplinePlacementNode? AddChainedSplineFromOutlet(
        string sourceSplineId,
        string? templateKey = null,
        double initialLength = 50.0,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(sourceSplineId))
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、延長スプラインを追加できません。";
            return null;
        }

        int sourceIndex = FindSplineIndex(project, sourceSplineId);
        if (sourceIndex < 0)
        {
            return null;
        }

        SplinePlacementNode source = project.Placement.Splines[sourceIndex];
        if (IsSplineOutletOccupied(project, source.Id))
        {
            LastStatusMessage.Value = $"スプライン '{source.Id}' の Outlet は既に接続されているため、延長できません。";
            return null;
        }

        string newId = GenerateNextSplineId(project);
        string? resolvedTemplateKey = !string.IsNullOrWhiteSpace(templateKey)
            ? templateKey.Trim()
            : (!string.IsNullOrWhiteSpace(source.TemplateKey) ? source.TemplateKey : ResolvePlacementTemplateKey(null));

        SplinePlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            Origin = SplineOriginModel.FromSpline(source.Id),
            TemplateKey = resolvedTemplateKey,
        };
        created.Curves.Add(new SplineCurveSegment(
            SplineCurveSegmentKind.Straight,
            0.0,
            double.Max(1.0, initialLength),
            null));

        int insertIndex = sourceIndex + 1;
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"'{source.Id}' から延長スプライン '{newId}' (IntoSpline) を追加",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                int bounded = int.Clamp(insertIndex, 0, project.Placement.Splines.Count);
                project.Placement.Splines.Insert(bounded, created);
                SelectedSplineId.Value = created.Id;
                LastStatusMessage.Value = $"'{source.Id}' の先に '{created.Id}' を連結しました。";
            },
            () =>
            {
                RemoveSplineById(project, created.Id);
                if (string.Equals(SelectedSplineId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedSplineId.Value = null;
                }
            }));

        return created;
    }

    public bool RemoveSplinePlacement(string splineId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(splineId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、スプラインを削除できません。";
            return false;
        }

        int index = FindSplineIndex(project, splineId);
        if (index < 0)
        {
            return false;
        }

        SplinePlacementNode removed = project.Placement.Splines[index];
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"スプライン '{splineId}' を削除",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                RemoveSplineById(project, removed.Id);
                if (string.Equals(SelectedSplineId.Value, removed.Id, StringComparison.Ordinal))
                {
                    SelectedSplineId.Value = null;
                }

                if (PendingConnectSourcePort.Value is not null
                    && string.Equals(PendingConnectSourcePort.Value.ElementId, removed.Id, StringComparison.Ordinal))
                {
                    PendingConnectSourcePort.Value = null;
                }

                LastStatusMessage.Value = $"スプライン '{removed.Id}' を削除しました。";
            },
            () =>
            {
                int restoreIdx = int.Clamp(index, 0, project.Placement.Splines.Count);
                project.Placement.Splines.Insert(restoreIdx, removed);
                SelectedSplineId.Value = removed.Id;
            }));

        return true;
    }

    public bool SetSplineOriginPose(
        string splineId,
        ChunkIndexModel chunk,
        PoseDegreesModel pose,
        string? mergeKey = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(splineId))
        {
            return false;
        }

        int index = FindSplineIndex(project, splineId);
        if (index < 0)
        {
            return false;
        }

        SplinePlacementNode current = project.Placement.Splines[index];
        if (current.Origin.Kind != SplineOriginKind.ChunkPose)
        {
            return false;
        }

        WorldPoseDegreesModel oldPose = new(current.Origin.Chunk, current.Origin.Pose);
        WorldPoseDegreesModel rawPose = new(chunk, pose);
        WorldPoseDegreesModel normalized = rawPose.Normalized();
        if (oldPose == normalized)
        {
            return false;
        }

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
        string key = mergeKey ?? $"Spline.OriginPose.{splineId}";

        Execute(new MergeableValueCommand<WorldPoseDegreesModel>(
            $"スプライン '{splineId}' の始点座標/回転を変更",
            key,
            InvalidationScope.FullRebuild,
            worldTarget,
            oldPose,
            normalized,
            applied =>
            {
                int curIdx = FindSplineIndex(project, splineId);
                if (0 <= curIdx)
                {
                    SplinePlacementNode target = project.Placement.Splines[curIdx];
                    if (target.Origin.Kind == SplineOriginKind.ChunkPose)
                    {
                        target.Origin = SplineOriginModel.FromChunk(applied.Chunk, applied.LocalPose);
                    }
                }
            }));

        return true;
    }

    public bool SetSplineConnectBezier(string splineId, SplineConnectBezierModel? connectBezier)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(splineId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、ConnectBezier を変更できません。";
            return false;
        }

        int index = FindSplineIndex(project, splineId);
        if (index < 0)
        {
            return false;
        }

        SplinePlacementNode source = project.Placement.Splines[index];
        if (Equals(source.ConnectBezier, connectBezier))
        {
            return false;
        }

        if (connectBezier is not null)
        {
            IReadOnlyList<CompatiblePortCandidate> compatible = SplineAnchorUtilities.FindCompatibleConnectBezierTargets(project, source);
            bool foundCompatible = false;
            for (int i = 0; i < compatible.Count; i++)
            {
                if (compatible[i].PortRef == connectBezier.TargetPort)
                {
                    foundCompatible = true;
                    break;
                }
            }

            if (!foundCompatible)
            {
                LastStatusMessage.Value = $"接続先ポート '{connectBezier.TargetPort.ElementId}' は車線構成が非互換または循環参照になるため接続できません。";
                return false;
            }

            if (IsPortTargetedByAnotherConnectBezier(project, connectBezier.TargetPort, splineId))
            {
                LastStatusMessage.Value = "接続先ポートには既に別の ConnectBezier が接続されています。";
                return false;
            }
        }

        SplineConnectBezierModel? oldConnect = source.ConnectBezier;
        SplineConnectBezierModel? newConnect = connectBezier;
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        string desc = newConnect is null
            ? $"スプライン '{splineId}' の ConnectBezier を解除"
            : $"スプライン '{splineId}' を '{newConnect.TargetPort.ElementId}' へ ConnectBezier 接続";

        Execute(new DelegateEditorCommand(
            desc,
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                SplinePlacementNode? target = FindSpline(splineId);
                if (target is not null)
                {
                    target.ConnectBezier = newConnect;
                    LastStatusMessage.Value = desc;
                }
            },
            () =>
            {
                SplinePlacementNode? target = FindSpline(splineId);
                if (target is not null)
                {
                    target.ConnectBezier = oldConnect;
                }
            }));

        return true;
    }

    public bool ReplaceSplineSnapshot(
        string splineId,
        SplinePlacementNode updatedSnapshot,
        string description = "スプラインを編集",
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(updatedSnapshot);
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(splineId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、スプラインを編集できません。";
            return false;
        }

        SplinePlacementNode? current = FindSpline(splineId);
        if (current is null)
        {
            return false;
        }

        SplinePlacementNode before = SplineAnchorUtilities.DeepCloneSpline(current);
        SplinePlacementNode after = SplineAnchorUtilities.DeepCloneSpline(updatedSnapshot);
        after.Id = current.Id;

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        if (!string.IsNullOrEmpty(mergeKey))
        {
            Execute(new MergeableValueCommand<SplinePlacementNode>(
                description,
                mergeKey,
                InvalidationScope.FullRebuild,
                worldTarget,
                before,
                after,
                applied =>
                {
                    SplinePlacementNode? target = FindSpline(splineId);
                    if (target is not null)
                    {
                        SplineAnchorUtilities.CopySpline(applied, target);
                    }
                }));
        }
        else
        {
            Execute(new DelegateEditorCommand(
                description,
                InvalidationScope.FullRebuild,
                worldTarget,
                () =>
                {
                    SplinePlacementNode? target = FindSpline(splineId);
                    if (target is not null)
                    {
                        SplineAnchorUtilities.CopySpline(after, target);
                    }
                },
                () =>
                {
                    SplinePlacementNode? target = FindSpline(splineId);
                    if (target is not null)
                    {
                        SplineAnchorUtilities.CopySpline(before, target);
                    }
                }));
        }

        return true;
    }

    public JunctionPlacementNode? FindJunction(string? junctionId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrEmpty(junctionId))
        {
            return null;
        }

        for (int i = 0; i < project.Placement.Junctions.Count; i++)
        {
            if (string.Equals(project.Placement.Junctions[i].Id, junctionId, StringComparison.Ordinal))
            {
                return project.Placement.Junctions[i];
            }
        }

        return null;
    }

    public JunctionPlacementNode? AddJunctionAtChunkPose(
        string? templateKey,
        WorldPoseDegreesModel rawPose,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、ジャンクションを配置できません。";
            return null;
        }

        string? resolvedTemplateKey = ResolvePlacementJunctionTemplateKey(templateKey, project);
        if (string.IsNullOrEmpty(resolvedTemplateKey) || !project.JunctionTemplates.ContainsKey(resolvedTemplateKey))
        {
            LastStatusMessage.Value = "配置する JunctionTemplate が見つかりません (先にジャンクションテンプレートを作成してください)。";
            return null;
        }

        WorldPoseDegreesModel normalized = rawPose.Normalized();
        string newId = GenerateNextJunctionId(project);
        JunctionPlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            TemplateKey = resolvedTemplateKey,
            Origin = JunctionOriginModel.FromChunk(normalized.Chunk, normalized.LocalPose),
        };

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"ジャンクション '{newId}' を配置",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Junctions.Add(created);
                SelectedJunctionId.Value = created.Id;
                LastStatusMessage.Value = $"ジャンクション '{created.Id}' を配置しました。";
            },
            () =>
            {
                RemoveJunctionById(project, created.Id);
                if (string.Equals(SelectedJunctionId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedJunctionId.Value = null;
                }
            }));

        return created;
    }

    public JunctionPlacementNode? AddChainedJunctionFromSplineOutlet(
        string sourceSplineId,
        string templateKey,
        string targetPortKey,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(sourceSplineId) || string.IsNullOrWhiteSpace(templateKey) || string.IsNullOrWhiteSpace(targetPortKey))
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、IntoJunction を追加できません。";
            return null;
        }

        SplinePlacementNode? source = FindSpline(sourceSplineId);
        if (source is null)
        {
            return null;
        }

        if (source.ConnectBezier is not null)
        {
            LastStatusMessage.Value = $"スプライン '{source.Id}' は ConnectBezier を持つため IntoJunction で接続できません。";
            return null;
        }

        if (IsSplineOutletOccupied(project, source.Id))
        {
            LastStatusMessage.Value = $"スプライン '{source.Id}' の Outlet は既に接続されています。";
            return null;
        }

        IReadOnlyList<string> compatiblePorts = JunctionAnchorUtilities.FindCompatibleJunctionPortsForSplineOutlet(project, source, templateKey.Trim());
        if (!compatiblePorts.Contains(targetPortKey.Trim(), StringComparer.Ordinal))
        {
            LastStatusMessage.Value = $"テンプレート '{templateKey}' のポート '{targetPortKey}' はスプライン '{source.Id}' と車線構成が互換ではありません。";
            return null;
        }

        string newId = GenerateNextJunctionId(project);
        JunctionPlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            TemplateKey = templateKey.Trim(),
            Origin = JunctionOriginModel.FromSpline(source.Id, targetPortKey.Trim()),
        };

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"ジャンクション '{newId}' を '{source.Id}.Outlet' に接続追加",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Junctions.Add(created);
                SelectedJunctionId.Value = created.Id;
                LastStatusMessage.Value = $"ジャンクション '{created.Id}' を '{source.Id}' に接続配置しました。";
            },
            () =>
            {
                RemoveJunctionById(project, created.Id);
                if (string.Equals(SelectedJunctionId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedJunctionId.Value = null;
                }
            }));

        return created;
    }

    public JunctionPlacementNode? AddChainedJunctionFromJunctionPort(
        string sourceJunctionId,
        string sourcePortKey,
        string templateKey,
        string targetPortKey,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null
            || string.IsNullOrWhiteSpace(sourceJunctionId)
            || string.IsNullOrWhiteSpace(sourcePortKey)
            || string.IsNullOrWhiteSpace(templateKey)
            || string.IsNullOrWhiteSpace(targetPortKey))
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、IntoJunction を追加できません。";
            return null;
        }

        JunctionPlacementNode? sourceJunction = FindJunction(sourceJunctionId);
        if (sourceJunction is null)
        {
            return null;
        }

        IReadOnlyList<string> compatiblePorts = JunctionAnchorUtilities.FindCompatibleJunctionPortsForJunctionPort(
            project,
            sourceJunction,
            sourcePortKey.Trim(),
            templateKey.Trim());
        if (!compatiblePorts.Contains(targetPortKey.Trim(), StringComparer.Ordinal))
        {
            LastStatusMessage.Value = $"ポート '{sourceJunctionId}.{sourcePortKey}' から '{templateKey}.{targetPortKey}' への接続は非互換または使用中です。";
            return null;
        }

        string newId = GenerateNextJunctionId(project);
        JunctionPlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            TemplateKey = templateKey.Trim(),
            Origin = JunctionOriginModel.FromJunction(sourceJunction.Id, sourcePortKey.Trim(), targetPortKey.Trim()),
        };

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"ジャンクション '{newId}' を '{sourceJunction.Id}.{sourcePortKey}' に接続追加",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Junctions.Add(created);
                SelectedJunctionId.Value = created.Id;
                LastStatusMessage.Value = $"ジャンクション '{created.Id}' を接続配置しました。";
            },
            () =>
            {
                RemoveJunctionById(project, created.Id);
                if (string.Equals(SelectedJunctionId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedJunctionId.Value = null;
                }
            }));

        return created;
    }

    public SplinePlacementNode? AddChainedSplineFromJunctionPort(
        string sourceJunctionId,
        string sourcePortKey,
        string? templateKey = null,
        double initialLength = 50.0,
        string? key = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(sourceJunctionId) || string.IsNullOrWhiteSpace(sourcePortKey))
        {
            return null;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、延長スプラインを追加できません。";
            return null;
        }

        JunctionPlacementNode? sourceJunction = FindJunction(sourceJunctionId);
        if (sourceJunction is null)
        {
            return null;
        }

        if (JunctionAnchorUtilities.IsJunctionPortOccupied(project.Placement, sourceJunction.Id, sourcePortKey.Trim()))
        {
            LastStatusMessage.Value = $"ジャンクションポート '{sourceJunction.Id}.{sourcePortKey}' は既に接続されています。";
            return null;
        }

        IReadOnlyList<string> compatibleTemplates = JunctionAnchorUtilities.FindCompatibleSplineTemplatesForJunctionPort(
            project,
            sourceJunction,
            sourcePortKey.Trim());
        string? requestedTemplate = ResolvePlacementTemplateKey(templateKey);
        string? resolvedTemplateKey = !string.IsNullOrEmpty(requestedTemplate) && compatibleTemplates.Contains(requestedTemplate, StringComparer.Ordinal)
            ? requestedTemplate
            : compatibleTemplates.FirstOrDefault();

        if (string.IsNullOrEmpty(resolvedTemplateKey))
        {
            LastStatusMessage.Value = $"ジャンクションポート '{sourceJunction.Id}.{sourcePortKey}' のレイアウトに一致する SplineTemplate が見つかりません。";
            return null;
        }

        string newId = GenerateNextSplineId(project);
        SplinePlacementNode created = new()
        {
            Id = newId,
            Key = string.IsNullOrWhiteSpace(key) ? newId : key.Trim(),
            Origin = SplineOriginModel.FromJunction(sourceJunction.Id, sourcePortKey.Trim()),
            TemplateKey = resolvedTemplateKey,
        };
        created.Curves.Add(new SplineCurveSegment(
            SplineCurveSegmentKind.Straight,
            0.0,
            double.Max(1.0, initialLength),
            null));

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"スプライン '{newId}' を '{sourceJunction.Id}.{sourcePortKey}' から延長",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Splines.Add(created);
                SelectedSplineId.Value = created.Id;
                LastStatusMessage.Value = $"スプライン '{created.Id}' を '{sourceJunction.Id}.{sourcePortKey}' に接続しました。";
            },
            () =>
            {
                RemoveSplineById(project, created.Id);
                if (string.Equals(SelectedSplineId.Value, created.Id, StringComparison.Ordinal))
                {
                    SelectedSplineId.Value = null;
                }
            }));

        return created;
    }

    public bool MoveJunctionChunkPose(
        string junctionId,
        WorldPoseDegreesModel rawPose,
        string? mergeKey = null)
    {
        return SetJunctionOriginPose(junctionId, rawPose.Chunk, rawPose.LocalPose, mergeKey);
    }

    public bool SetJunctionOriginPose(
        string junctionId,
        ChunkIndexModel chunk,
        PoseDegreesModel pose,
        string? mergeKey = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(junctionId))
        {
            return false;
        }

        int index = FindJunctionIndex(project, junctionId);
        if (index < 0)
        {
            return false;
        }

        JunctionPlacementNode current = project.Placement.Junctions[index];
        if (current.Origin.Kind != JunctionOriginKind.ChunkPose)
        {
            return false;
        }

        WorldPoseDegreesModel oldPose = new(current.Origin.Chunk, current.Origin.Pose);
        WorldPoseDegreesModel normalized = new WorldPoseDegreesModel(chunk, pose).Normalized();
        if (oldPose == normalized)
        {
            return false;
        }

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
        string key = mergeKey ?? $"Junction.OriginPose.{junctionId}";

        Execute(new MergeableValueCommand<WorldPoseDegreesModel>(
            $"ジャンクション '{junctionId}' の座標/回転を変更",
            key,
            InvalidationScope.FullRebuild,
            worldTarget,
            oldPose,
            normalized,
            applied =>
            {
                int curIdx = FindJunctionIndex(project, junctionId);
                if (0 <= curIdx)
                {
                    JunctionPlacementNode target = project.Placement.Junctions[curIdx];
                    if (target.Origin.Kind == JunctionOriginKind.ChunkPose)
                    {
                        target.Origin = JunctionOriginModel.FromChunk(applied.Chunk, applied.LocalPose);
                    }
                }
            }));

        return true;
    }

    public bool MutateJunctionPlacement(
        string junctionId,
        string description,
        string? mergeKey,
        Action<JunctionPlacementNode> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(junctionId))
        {
            return false;
        }

        JunctionPlacementNode? current = FindJunction(junctionId);
        if (current is null)
        {
            return false;
        }

        JunctionPlacementNode draft = JunctionAnchorUtilities.DeepCloneJunctionPlacement(current);
        mutator(draft);
        return ReplaceJunctionSnapshot(junctionId, draft, description, mergeKey);
    }

    public bool ReplaceJunctionSnapshot(
        string junctionId,
        JunctionPlacementNode updatedSnapshot,
        string description = "ジャンクションを編集",
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(updatedSnapshot);
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(junctionId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、ジャンクションを編集できません。";
            return false;
        }

        JunctionPlacementNode? current = FindJunction(junctionId);
        if (current is null)
        {
            return false;
        }

        JunctionPlacementNode before = JunctionAnchorUtilities.DeepCloneJunctionPlacement(current);
        JunctionPlacementNode after = JunctionAnchorUtilities.DeepCloneJunctionPlacement(updatedSnapshot);
        after.Id = current.Id;

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        if (!string.IsNullOrEmpty(mergeKey))
        {
            Execute(new MergeableValueCommand<JunctionPlacementNode>(
                description,
                mergeKey,
                InvalidationScope.FullRebuild,
                worldTarget,
                before,
                after,
                applied =>
                {
                    JunctionPlacementNode? target = FindJunction(junctionId);
                    if (target is not null)
                    {
                        JunctionAnchorUtilities.CopyJunctionPlacement(applied, target);
                    }
                }));
        }
        else
        {
            Execute(new DelegateEditorCommand(
                description,
                InvalidationScope.FullRebuild,
                worldTarget,
                () =>
                {
                    JunctionPlacementNode? target = FindJunction(junctionId);
                    if (target is not null)
                    {
                        JunctionAnchorUtilities.CopyJunctionPlacement(after, target);
                    }
                },
                () =>
                {
                    JunctionPlacementNode? target = FindJunction(junctionId);
                    if (target is not null)
                    {
                        JunctionAnchorUtilities.CopyJunctionPlacement(before, target);
                    }
                }));
        }

        return true;
    }

    public bool RemoveJunctionPlacement(string junctionId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(junctionId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、ジャンクションを削除できません。";
            return false;
        }

        int index = FindJunctionIndex(project, junctionId);
        if (index < 0)
        {
            return false;
        }

        JunctionPlacementNode removed = project.Placement.Junctions[index];
        List<(int Index, ExplicitPortConnection Connection)> removedConns = new();
        for (int i = 0; i < project.Placement.Connections.Count; i++)
        {
            ExplicitPortConnection c = project.Placement.Connections[i];
            if (string.Equals(c.From.ElementId, junctionId, StringComparison.Ordinal)
                || string.Equals(c.To.ElementId, junctionId, StringComparison.Ordinal))
            {
                removedConns.Add((i, c));
            }
        }

        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"ジャンクション '{junctionId}' を削除",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                RemoveJunctionById(project, removed.Id);
                for (int i = removedConns.Count - 1; 0 <= i; i--)
                {
                    int ci = removedConns[i].Index;
                    if (0 <= ci && ci < project.Placement.Connections.Count)
                    {
                        project.Placement.Connections.RemoveAt(ci);
                    }
                }

                if (string.Equals(SelectedJunctionId.Value, removed.Id, StringComparison.Ordinal))
                {
                    SelectedJunctionId.Value = null;
                }

                if (PendingConnectSourcePort.Value is not null
                    && string.Equals(PendingConnectSourcePort.Value.ElementId, removed.Id, StringComparison.Ordinal))
                {
                    PendingConnectSourcePort.Value = null;
                }

                LastStatusMessage.Value = $"ジャンクション '{removed.Id}' を削除しました。";
            },
            () =>
            {
                int restoreIdx = int.Clamp(index, 0, project.Placement.Junctions.Count);
                project.Placement.Junctions.Insert(restoreIdx, removed);
                for (int i = 0; i < removedConns.Count; i++)
                {
                    int ci = int.Clamp(removedConns[i].Index, 0, project.Placement.Connections.Count);
                    project.Placement.Connections.Insert(ci, removedConns[i].Connection);
                }

                SelectedJunctionId.Value = removed.Id;
            }));

        return true;
    }

    public bool AddExplicitPortConnection(PortReferenceModel fromPort, PortReferenceModel toPort)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、ポート接続を追加できません。";
            return false;
        }

        IReadOnlyList<CompatiblePortCandidate> compatible = JunctionAnchorUtilities.FindCompatibleExplicitConnectTargets(project, fromPort);
        bool valid = false;
        for (int i = 0; i < compatible.Count; i++)
        {
            if (compatible[i].PortRef == toPort)
            {
                valid = true;
                break;
            }
        }

        if (!valid)
        {
            LastStatusMessage.Value = $"ポート '{fromPort.ElementId}.{fromPort.PortKey}' と '{toPort.ElementId}.{toPort.PortKey}' は接続できません。";
            return false;
        }

        ExplicitPortConnection conn = new(fromPort, toPort);
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"Network.Connect({fromPort.ElementId}, {toPort.ElementId})",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                project.Placement.Connections.Add(conn);
                LastStatusMessage.Value = $"ポート '{fromPort.ElementId}.{fromPort.PortKey}' と '{toPort.ElementId}.{toPort.PortKey}' を接続しました。";
            },
            () =>
            {
                if (0 < project.Placement.Connections.Count && project.Placement.Connections[^1] == conn)
                {
                    project.Placement.Connections.RemoveAt(project.Placement.Connections.Count - 1);
                }
                else
                {
                    project.Placement.Connections.Remove(conn);
                }
            }));

        return true;
    }

    public bool RemoveExplicitPortConnectionForPort(PortReferenceModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        int index = JunctionAnchorUtilities.FindExplicitConnectionIndexForPort(project.Placement, port);
        if (index < 0)
        {
            LastStatusMessage.Value = $"ポート '{port.ElementId}.{port.PortKey}' に解除できる直接接続 (Network.Connect) がありません。";
            return false;
        }

        return RemoveExplicitPortConnection(index);
    }

    public bool RemoveExplicitPortConnection(int index)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || index < 0 || project.Placement.Connections.Count <= index)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、接続を削除できません。";
            return false;
        }

        ExplicitPortConnection removed = project.Placement.Connections[index];
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"ポート接続 ({removed.From.ElementId} -> {removed.To.ElementId}) を解除",
            InvalidationScope.FullRebuild,
            worldTarget,
            () =>
            {
                if (0 <= index && index < project.Placement.Connections.Count)
                {
                    project.Placement.Connections.RemoveAt(index);
                }
            },
            () =>
            {
                int restoreIdx = int.Clamp(index, 0, project.Placement.Connections.Count);
                project.Placement.Connections.Insert(restoreIdx, removed);
            }));

        return true;
    }

    public void RequestJumpToChunk(int chunkX, int chunkZ)
    {
        ChunkJumpRequested?.Invoke(chunkX, chunkZ);
    }

    public void SetRuntimeErrors(IReadOnlyList<TransportX.Diagnostics.Error> errors)
    {
        RuntimeErrors.Value = errors;
    }

    public void SetVerificationErrors(IReadOnlyList<TransportX.Diagnostics.Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        VerificationErrors.Value = errors;

        int errorCount = 0;
        int warningCount = 0;
        foreach (TransportX.Diagnostics.Error err in errors)
        {
            if (err.Level == TransportX.Diagnostics.ErrorLevel.Error || err.Level == TransportX.Diagnostics.ErrorLevel.Fatal)
            {
                errorCount++;
            }
            else if (err.Level == TransportX.Diagnostics.ErrorLevel.Warning)
            {
                warningCount++;
            }
        }

        if (ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
        {
            if (0 < errorCount)
            {
                VerificationSummaryText.Value = $"スクリプト検証モード (エラー: {errorCount} 件, 警告: {warningCount} 件)";
            }
            else if (0 < warningCount)
            {
                VerificationSummaryText.Value = $"スクリプト検証モード (警告: {warningCount} 件)";
            }
            else
            {
                VerificationSummaryText.Value = "スクリプト検証モード (正常実行中 - エラー 0 件)";
            }
        }
    }

    public bool RequestStartScriptVerification()
    {
        if (IsVerificationStarting.Value)
        {
            return false;
        }

        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            LastStatusMessage.Value = "スクリプト検証を実行するにはワールドを開いてください。";
            return false;
        }

        WorldProjectSaveResult saveResult = Save(dirtyOnly: true);
        if (0 < saveResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error))
        {
            LastStatusMessage.Value = "保存中にエラーが発生したため、スクリプト検証を開始できませんでした。";
            return false;
        }

        VerificationErrors.Value = Array.Empty<TransportX.Diagnostics.Error>();
        ActiveRenderMode.Value = EditorRenderMode.ScriptVerification;
        if (StartVerificationRequested is not null)
        {
            IsVerificationStarting.Value = true;
            VerificationSummaryText.Value = "スクリプト検証モード (起動中...)";
            LastStatusMessage.Value = "スクリプト検証モードをバックグラウンドで準備中...";
            StartVerificationRequested.Invoke(project);
        }
        else
        {
            IsVerificationStarting.Value = false;
            VerificationSummaryText.Value = "スクリプト検証モード";
            LastStatusMessage.Value = "スクリプト検証モードを実行中...";
        }

        return true;
    }

    public void RequestCancelVerificationStart()
    {
        if (!IsVerificationStarting.Value)
        {
            return;
        }

        LastStatusMessage.Value = "スクリプト検証の起動をキャンセルしています...";
        CancelVerificationStartRequested?.Invoke();
    }

    public void RequestStopScriptVerification()
    {
        if (IsVerificationStarting.Value)
        {
            RequestCancelVerificationStart();
            return;
        }

        if (ActiveRenderMode.Value == EditorRenderMode.EditLivePreview
            && VerificationErrors.Value.Count == 0)
        {
            return;
        }

        StopVerificationRequested?.Invoke();
        ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
        VerificationErrors.Value = Array.Empty<TransportX.Diagnostics.Error>();
        VerificationSummaryText.Value = "編集モード";
        LastStatusMessage.Value = "編集モードに戻りました。";
    }

    /// <summary>
    /// Cancels an active verification startup, exits verification mode, or exits active placement/connect/transform tools.
    /// </summary>
    public bool CancelActiveInteraction()
    {
        if (IsVerificationStarting.Value)
        {
            RequestCancelVerificationStart();
            return true;
        }

        if (ActiveRenderMode.Value == EditorRenderMode.ScriptVerification)
        {
            RequestStopScriptVerification();
            return true;
        }

        bool hadActiveTool = PendingConnectSourcePort.Value is not null
            || ActiveToolMode.Value != EditorToolMode.Select;
        if (!hadActiveTool)
        {
            return false;
        }

        PendingConnectSourcePort.Value = null;
        ActiveToolMode.Value = EditorToolMode.Select;
        LastStatusMessage.Value = "選択モードに戻りました。";
        return true;
    }

    public bool AddCameraBookmark(string name, CameraBookmarkModel? explicitPose = null)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        CameraBookmarkModel? pose = explicitPose ?? CaptureCurrentCameraPose?.Invoke();
        if (pose is null && SavedCameraProject == project && SavedCameraPose is TransportX.Cameras.CameraPose stored)
        {
            pose = new CameraBookmarkModel(
                name,
                stored.Chunk.X,
                stored.Chunk.Z,
                stored.Position.X,
                stored.Position.Y,
                stored.Position.Z,
                double.RadiansToDegrees(stored.Angle.X),
                double.RadiansToDegrees(stored.Angle.Y),
                0.0);
        }

        if (pose is null)
        {
            pose = new CameraBookmarkModel(name, 0, 0, 125.0, 10.0, 125.0, 15.0, 0.0, 0.0);
        }

        string trimmedName = string.IsNullOrWhiteSpace(name)
            ? $"視点 {project.Sidecar.CameraBookmarks.Count + 1}"
            : name.Trim();
        CameraBookmarkModel entry = pose with { Name = trimmedName };
        project.Sidecar.CameraBookmarks.Add(entry);
        project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
        ProjectRevision.Value++;
        LastStatusMessage.Value = $"カメラブックマーク '{trimmedName}' を追加しました。";
        return true;
    }

    public bool RemoveCameraBookmark(int index)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || index < 0 || project.Sidecar.CameraBookmarks.Count <= index)
        {
            return false;
        }

        string removedName = project.Sidecar.CameraBookmarks[index].Name;
        project.Sidecar.CameraBookmarks.RemoveAt(index);
        project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
        ProjectRevision.Value++;
        LastStatusMessage.Value = $"カメラブックマーク '{removedName}' を削除しました。";
        return true;
    }

    public bool JumpToCameraBookmark(int index)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || index < 0 || project.Sidecar.CameraBookmarks.Count <= index)
        {
            return false;
        }

        CameraBookmarkModel bookmark = project.Sidecar.CameraBookmarks[index];
        ApplyCameraBookmarkRequested?.Invoke(bookmark);
        LastStatusMessage.Value = $"カメラをブックマーク '{bookmark.Name}' へ移動しました。";
        return true;
    }

    public bool SetPlayerExecutablePath(string? path)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string? normalized = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (string.Equals(project.Sidecar.PlayerExecutablePath, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        project.Sidecar.PlayerExecutablePath = normalized;
        project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
        ProjectRevision.Value++;
        return true;
    }

    public bool SetWorldUserCode(string? newUserCode)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、<user-code> を編集できません。";
            return false;
        }

        string? targetValue = string.IsNullOrEmpty(newUserCode) ? null : newUserCode;
        string? oldValue = project.Placement.UserCode;
        if (string.Equals(oldValue, targetValue, StringComparison.Ordinal))
        {
            return false;
        }

        WorldFileTarget fileTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new MergeableValueCommand<string?>(
            "World.csx の <user-code> を編集",
            "World.UserCode",
            InvalidationScope.None,
            fileTarget,
            oldValue,
            targetValue,
            val => project.Placement.UserCode = val));
        return true;
    }

    public string? GetElementDisplayName(string? elementId)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(elementId))
        {
            return null;
        }

        SplinePlacementNode? spline = FindSpline(elementId);
        if (spline is not null)
        {
            return string.IsNullOrWhiteSpace(spline.DisplayName) ? null : spline.DisplayName;
        }

        JunctionPlacementNode? junction = FindJunction(elementId);
        if (junction is not null)
        {
            return string.IsNullOrWhiteSpace(junction.DisplayName) ? null : junction.DisplayName;
        }

        ChunkPropPlacement? prop = FindProp(elementId);
        if (prop is not null)
        {
            return string.IsNullOrWhiteSpace(prop.DisplayName) ? null : prop.DisplayName;
        }

        return null;
    }

    public bool SetSplineDisplayName(string splineId, string? displayName)
    {
        SplinePlacementNode? spline = FindSpline(splineId);
        if (spline is null)
        {
            return false;
        }

        string? before = string.IsNullOrWhiteSpace(spline.DisplayName) ? null : spline.DisplayName;
        return ExecuteDisplayNameChange(
            splineId,
            "スプライン",
            before,
            displayName,
            val =>
            {
                SplinePlacementNode? target = FindSpline(splineId);
                if (target is not null)
                {
                    target.DisplayName = val;
                }
            });
    }

    public bool SetJunctionDisplayName(string junctionId, string? displayName)
    {
        JunctionPlacementNode? junction = FindJunction(junctionId);
        if (junction is null)
        {
            return false;
        }

        string? before = string.IsNullOrWhiteSpace(junction.DisplayName) ? null : junction.DisplayName;
        return ExecuteDisplayNameChange(
            junctionId,
            "ジャンクション",
            before,
            displayName,
            val =>
            {
                JunctionPlacementNode? target = FindJunction(junctionId);
                if (target is not null)
                {
                    target.DisplayName = val;
                }
            });
    }

    public bool SetPropDisplayName(string propId, string? displayName)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(propId))
        {
            return false;
        }

        int index = FindPropIndex(project, propId);
        if (index < 0)
        {
            return false;
        }

        ChunkPropPlacement current = project.Placement.Props[index];
        string? before = string.IsNullOrWhiteSpace(current.DisplayName) ? null : current.DisplayName;
        return ExecuteDisplayNameChange(
            propId,
            "プロップ",
            before,
            displayName,
            val =>
            {
                int curIdx = FindPropIndex(project, propId);
                if (0 <= curIdx)
                {
                    project.Placement.Props[curIdx] = project.Placement.Props[curIdx] with { DisplayName = val };
                }
            });
    }

    public bool RenameSplineKey(string splineId, string? newKey)
    {
        return RenameElementKeyCore(isSpline: true, splineId, newKey);
    }

    public bool RenameJunctionKey(string junctionId, string? newKey)
    {
        return RenameElementKeyCore(isSpline: false, junctionId, newKey);
    }

    private bool ExecuteDisplayNameChange(
        string elementId,
        string kindLabel,
        string? before,
        string? displayName,
        Action<string?> applyValue)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(elementId))
        {
            return false;
        }

        string? normalized = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (string.Equals(before, normalized, StringComparison.Ordinal))
        {
            return true;
        }

        WorldFileTarget worldTarget = new(
            WorldFileKind.WorldCsx,
            WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
        Execute(new MergeableValueCommand<string?>(
            $"{kindLabel} '{elementId}' の表示名を変更",
            $"World.DisplayName.{elementId}",
            InvalidationScope.None,
            worldTarget,
            before,
            normalized,
            applyValue));
        return true;
    }

    private bool RenameElementKeyCore(bool isSpline, string currentId, string? requestedKey)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(currentId))
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、Key を変更できません。";
            return false;
        }

        SplinePlacementNode? targetSpline = isSpline ? FindSpline(currentId) : null;
        JunctionPlacementNode? targetJunction = isSpline ? null : FindJunction(currentId);
        if (isSpline && targetSpline is null)
        {
            return false;
        }

        if (!isSpline && targetJunction is null)
        {
            return false;
        }

        if (!CSharpIdentifierUtilities.TryValidateNodeKey(requestedKey, out string reason))
        {
            LastStatusMessage.Value = reason;
            return false;
        }

        string newKey = requestedKey!;
        string? existingKey = isSpline ? targetSpline!.Key : targetJunction!.Key;
        if (string.Equals(currentId, newKey, StringComparison.Ordinal)
            && string.Equals(existingKey, newKey, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (SplinePlacementNode s in project.Placement.Splines)
        {
            if (ReferenceEquals(s, targetSpline))
            {
                continue;
            }

            if (string.Equals(s.Id, newKey, StringComparison.Ordinal)
                || string.Equals(s.Key, newKey, StringComparison.Ordinal))
            {
                LastStatusMessage.Value = $"Key '{newKey}' は他のスプラインで既に使用されています。";
                return false;
            }
        }

        foreach (JunctionPlacementNode j in project.Placement.Junctions)
        {
            if (ReferenceEquals(j, targetJunction))
            {
                continue;
            }

            if (string.Equals(j.Id, newKey, StringComparison.Ordinal)
                || string.Equals(j.Key, newKey, StringComparison.Ordinal))
            {
                LastStatusMessage.Value = $"Key '{newKey}' は他のジャンクションで既に使用されています。";
                return false;
            }
        }

        string kindLabel = isSpline ? "スプライン" : "ジャンクション";
        WorldFileTarget worldTarget = new(WorldFileKind.WorldCsx, normalizedWorld);
        Execute(new DelegateEditorCommand(
            $"{kindLabel} Key を '{currentId}' から '{newKey}' へ変更",
            InvalidationScope.FullRebuild,
            worldTarget,
            () => ApplyElementKeyRename(project, isSpline, currentId, newKey),
            () => ApplyElementKeyRename(project, isSpline, newKey, currentId)));

        LastStatusMessage.Value = $"{kindLabel} の Key を '{newKey}' に変更しました。";
        return true;
    }

    private void ApplyElementKeyRename(WorldProject project, bool isSpline, string fromId, string toId)
    {
        if (isSpline)
        {
            SplinePlacementNode? target = project.Placement.Splines
                .FirstOrDefault(s => string.Equals(s.Id, fromId, StringComparison.Ordinal));
            if (target is null)
            {
                return;
            }

            target.Id = toId;
            target.Key = toId;
        }
        else
        {
            JunctionPlacementNode? target = project.Placement.Junctions
                .FirstOrDefault(j => string.Equals(j.Id, fromId, StringComparison.Ordinal));
            if (target is null)
            {
                return;
            }

            target.Id = toId;
            target.Key = toId;
        }

        foreach (SplinePlacementNode s in project.Placement.Splines)
        {
            bool matchesOrigin = isSpline
                ? s.Origin.Kind == SplineOriginKind.FromSplineOutlet
                : s.Origin.Kind == SplineOriginKind.FromJunctionPort;
            if (matchesOrigin && string.Equals(s.Origin.SourceNodeId, fromId, StringComparison.Ordinal))
            {
                s.Origin = s.Origin with { SourceNodeId = toId };
            }

            if (s.ConnectBezier is not null)
            {
                PortReferenceModel updatedTarget = UpdatePortReferenceId(s.ConnectBezier.TargetPort, isSpline, fromId, toId);
                if (!ReferenceEquals(updatedTarget, s.ConnectBezier.TargetPort))
                {
                    s.ConnectBezier = s.ConnectBezier with { TargetPort = updatedTarget };
                }
            }
        }

        foreach (JunctionPlacementNode j in project.Placement.Junctions)
        {
            bool matchesOrigin = isSpline
                ? j.Origin.Kind == JunctionOriginKind.FromSplineOutlet
                : j.Origin.Kind == JunctionOriginKind.FromJunctionPort;
            if (matchesOrigin && string.Equals(j.Origin.SourceNodeId, fromId, StringComparison.Ordinal))
            {
                j.Origin = j.Origin with { SourceNodeId = toId };
            }
        }

        for (int i = 0; i < project.Placement.Connections.Count; i++)
        {
            ExplicitPortConnection conn = project.Placement.Connections[i];
            PortReferenceModel nextFrom = UpdatePortReferenceId(conn.From, isSpline, fromId, toId);
            PortReferenceModel nextTo = UpdatePortReferenceId(conn.To, isSpline, fromId, toId);
            if (!ReferenceEquals(nextFrom, conn.From) || !ReferenceEquals(nextTo, conn.To))
            {
                project.Placement.Connections[i] = new ExplicitPortConnection(nextFrom, nextTo);
            }
        }

        bool sidecarTouched = false;
        for (int i = 0; i < project.Sidecar.HiddenNodeIds.Count; i++)
        {
            if (string.Equals(project.Sidecar.HiddenNodeIds[i], fromId, StringComparison.Ordinal))
            {
                project.Sidecar.HiddenNodeIds[i] = toId;
                sidecarTouched = true;
            }
        }

        if (sidecarTouched)
        {
            project.MarkDirty(WorldFileKind.EditorJson, "__Editor.json");
        }

        if (isSpline && string.Equals(SelectedSplineId.Value, fromId, StringComparison.Ordinal))
        {
            SelectedSplineId.Value = toId;
        }

        if (!isSpline && string.Equals(SelectedJunctionId.Value, fromId, StringComparison.Ordinal))
        {
            SelectedJunctionId.Value = toId;
        }

        if (PendingConnectSourcePort.Value is not null)
        {
            PortReferenceModel updatedPending = UpdatePortReferenceId(PendingConnectSourcePort.Value, isSpline, fromId, toId);
            if (!ReferenceEquals(updatedPending, PendingConnectSourcePort.Value))
            {
                PendingConnectSourcePort.Value = updatedPending;
            }
        }
    }

    private static PortReferenceModel UpdatePortReferenceId(PortReferenceModel portRef, bool isSpline, string fromId, string toId)
    {
        bool matchesKind = isSpline
            ? portRef.Kind is PortTargetKind.SplineInlet or PortTargetKind.SplineOutlet
            : portRef.Kind == PortTargetKind.JunctionPort;
        return matchesKind && string.Equals(portRef.ElementId, fromId, StringComparison.Ordinal)
            ? portRef with { ElementId = toId }
            : portRef;
    }

    public bool ClearProtectedFlagAndMarkDirty(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string normalized = WorldProject.NormalizeRelativePath(relativePath);
        int removedCount = project.ProtectedFiles.RemoveWhere(
            p => string.Equals(WorldProject.NormalizeRelativePath(p), normalized, StringComparison.OrdinalIgnoreCase));
        if (removedCount == 0)
        {
            return false;
        }

        WorldFileKind kind = ResolveFileKind(project, normalized);
        project.MarkDirty(kind, normalized);

        List<ParseDiagnostic> remainingDiag = new();
        foreach (ParseDiagnostic pd in ParseDiagnostics.Value)
        {
            string pdRel = WorldProject.NormalizeRelativePath(pd.FilePath);
            if (!string.Equals(pdRel, normalized, StringComparison.OrdinalIgnoreCase)
                && !pd.FilePath.EndsWith(normalized.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                remainingDiag.Add(pd);
            }
        }

        ParseDiagnostics.Value = remainingDiag;
        RefreshValidation();
        ProjectRevision.Value++;
        LastStatusMessage.Value = $"'{normalized}' の上書き保護を解除し、再生成を許可しました (保存で上書きされます)。";
        return true;
    }

    private static WorldFileKind ResolveFileKind(WorldProject project, string normalizedRel)
    {
        if (string.Equals(WorldProject.NormalizeRelativePath(project.Placement.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
        {
            return WorldFileKind.WorldCsx;
        }

        if (string.Equals(WorldProject.NormalizeRelativePath(project.Init.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
        {
            return WorldFileKind.InitCsx;
        }

        if (project.SplineTemplates.Values.Any(t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.SplineTemplateCsx;
        }

        if (project.JunctionTemplates.Values.Any(t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.JunctionTemplateCsx;
        }

        if (project.CrossSections.Values.Any(c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.CrossSectionXml;
        }

        if (project.LaneLayouts.Values.Any(l => string.Equals(WorldProject.NormalizeRelativePath(l.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.LaneLayoutXml;
        }

        if (project.Environments.Values.Any(e => string.Equals(WorldProject.NormalizeRelativePath(e.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.EnvironmentXml;
        }

        if (project.ModelLists.Values.Any(m => string.Equals(WorldProject.NormalizeRelativePath(m.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.ModelsTxt;
        }

        if (project.SoundLists.Values.Any(s => string.Equals(WorldProject.NormalizeRelativePath(s.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.SoundsTxt;
        }

        if (project.CrossSectionLists.Values.Any(c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase)))
        {
            return WorldFileKind.CrossSectionsTxt;
        }

        return WorldFileKind.WorldCsx;
    }

    private static int FindPropIndex(WorldProject project, string propId)
    {
        for (int i = 0; i < project.Placement.Props.Count; i++)
        {
            if (string.Equals(project.Placement.Props[i].Id, propId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void RemovePropById(WorldProject project, string propId)
    {
        int idx = FindPropIndex(project, propId);
        if (0 <= idx)
        {
            project.Placement.Props.RemoveAt(idx);
        }
    }

    private static int FindSplineIndex(WorldProject project, string splineId)
    {
        for (int i = 0; i < project.Placement.Splines.Count; i++)
        {
            if (string.Equals(project.Placement.Splines[i].Id, splineId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void RemoveSplineById(WorldProject project, string splineId)
    {
        int idx = FindSplineIndex(project, splineId);
        if (0 <= idx)
        {
            project.Placement.Splines.RemoveAt(idx);
        }
    }

    private static int FindJunctionIndex(WorldProject project, string junctionId)
    {
        for (int i = 0; i < project.Placement.Junctions.Count; i++)
        {
            if (string.Equals(project.Placement.Junctions[i].Id, junctionId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void RemoveJunctionById(WorldProject project, string junctionId)
    {
        int idx = FindJunctionIndex(project, junctionId);
        if (0 <= idx)
        {
            project.Placement.Junctions.RemoveAt(idx);
        }
    }

    private static string GenerateNextJunctionId(WorldProject project)
    {
        return GenerateNextNodeIdentifier(project, "junction_");
    }

    private string? ResolvePlacementJunctionTemplateKey(string? explicitKey, WorldProject project)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(PlacementJunctionTemplateKey.Value))
        {
            return PlacementJunctionTemplateKey.Value.Trim();
        }

        string fallback = GetDefaultJunctionTemplateKey(project);
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    private static string GetDefaultJunctionTemplateKey(WorldProject project)
    {
        return project.JunctionTemplates.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string GenerateNextSplineId(WorldProject project)
    {
        return GenerateNextNodeIdentifier(project, "spline_");
    }

    private static string GenerateNextNodeIdentifier(WorldProject project, string prefix)
    {
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (SplinePlacementNode s in project.Placement.Splines)
        {
            if (!string.IsNullOrEmpty(s.Id))
            {
                used.Add(s.Id);
            }

            if (!string.IsNullOrEmpty(s.Key))
            {
                used.Add(s.Key);
            }
        }

        foreach (JunctionPlacementNode j in project.Placement.Junctions)
        {
            if (!string.IsNullOrEmpty(j.Id))
            {
                used.Add(j.Id);
            }

            if (!string.IsNullOrEmpty(j.Key))
            {
                used.Add(j.Key);
            }
        }

        int n = 1;
        while (true)
        {
            string candidate = $"{prefix}{n:000}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }

            n++;
        }
    }

    private string? ResolvePlacementTemplateKey(string? explicitKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(PlacementSplineTemplateKey.Value))
        {
            return PlacementSplineTemplateKey.Value.Trim();
        }

        return null;
    }

    private static string GetDefaultSplineTemplateKey(WorldProject project)
    {
        return project.SplineTemplates.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool IsSplineOutletOccupied(WorldProject project, string splineId)
    {
        SplinePlacementNode? source = null;
        for (int i = 0; i < project.Placement.Splines.Count; i++)
        {
            if (string.Equals(project.Placement.Splines[i].Id, splineId, StringComparison.Ordinal))
            {
                source = project.Placement.Splines[i];
                break;
            }
        }

        if (source?.ConnectBezier is not null)
        {
            return true;
        }

        for (int i = 0; i < project.Placement.Splines.Count; i++)
        {
            SplinePlacementNode other = project.Placement.Splines[i];
            if (other.Origin.Kind == SplineOriginKind.FromSplineOutlet
                && string.Equals(other.Origin.SourceNodeId, splineId, StringComparison.Ordinal))
            {
                return true;
            }

            if (other.ConnectBezier is not null
                && other.ConnectBezier.TargetPort.Kind == PortTargetKind.SplineOutlet
                && string.Equals(other.ConnectBezier.TargetPort.ElementId, splineId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < project.Placement.Junctions.Count; i++)
        {
            JunctionPlacementNode j = project.Placement.Junctions[i];
            if (j.Origin.Kind == JunctionOriginKind.FromSplineOutlet
                && string.Equals(j.Origin.SourceNodeId, splineId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPortTargetedByAnotherConnectBezier(
        WorldProject project,
        PortReferenceModel port,
        string excludingSplineId)
    {
        for (int i = 0; i < project.Placement.Splines.Count; i++)
        {
            SplinePlacementNode other = project.Placement.Splines[i];
            if (string.Equals(other.Id, excludingSplineId, StringComparison.Ordinal))
            {
                continue;
            }

            if (other.ConnectBezier is not null && other.ConnectBezier.TargetPort == port)
            {
                return true;
            }
        }

        return false;
    }

    private static string GenerateNextPropId(WorldProject project)
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChunkPropPlacement p in project.Placement.Props)
        {
            used.Add(p.Id);
        }

        int n = 1;
        while (true)
        {
            string candidate = $"prop_{n:000}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }

            n++;
        }
    }

    public bool AddNewLaneLayout(string key, string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key) || project.LaneLayouts.ContainsKey(key))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        LaneLayoutDocument doc = CreateDefaultSymmetricLayout(key, normalizedRel);
        ResourceLoadReference initRef = new ResourceLoadReference(key, normalizedRel);

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new WorldFileTarget(WorldFileKind.LaneLayoutXml, normalizedRel),
            new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath)),
        };

        Execute(new DelegateEditorCommand(
            $"LaneLayout '{key}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.LaneLayouts[key] = doc;
                project.Init.LaneLayouts.Add(initRef);
            },
            () =>
            {
                project.LaneLayouts.Remove(key);
                project.Init.LaneLayouts.Remove(initRef);
            }));

        return true;
    }

    public bool AddNewCrossSection(string key, string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key) || project.CrossSections.ContainsKey(key))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        CrossSectionListDocument? targetList = project.CrossSectionLists.Values.FirstOrDefault();
        bool createDefaultList = targetList is null;
        string normalizedInit = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (createDefaultList && project.IsProtected(normalizedInit))
        {
            LastStatusMessage.Value = "Init.csx が上書き保護されているため、CrossSections.txt を追加できません。";
            return false;
        }

        string listRel = targetList is not null
            ? WorldProject.NormalizeRelativePath(targetList.RelativePath)
            : "CrossSections.txt";
        CrossSectionListDocument listToUse = targetList ?? new CrossSectionListDocument { RelativePath = listRel };
        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, listRel);
        string xmlFullPath = Path.GetFullPath(Path.Combine(project.WorldDirectory, normalizedRel));
        string entryRel = Path.GetRelativePath(listDir, xmlFullPath).Replace('\\', '/');

        CrossSectionDocument doc = CreateDefaultRoadCrossSection(key, normalizedRel);
        CrossSectionListEntry listEntry = new(key, entryRel);

        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.CrossSectionXml, normalizedRel),
            new(WorldFileKind.CrossSectionsTxt, listRel),
        ];
        if (createDefaultList)
        {
            targets.Add(new WorldFileTarget(WorldFileKind.InitCsx, normalizedInit));
        }

        Execute(new DelegateEditorCommand(
            $"CrossSection '{key}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                if (createDefaultList)
                {
                    project.CrossSectionLists[listRel] = listToUse;
                    project.Init.CrossSectionLists.Add(listRel);
                }

                listToUse.Entries.Add(listEntry);
                project.CrossSections[key] = doc;
            },
            () =>
            {
                project.CrossSections.Remove(key);
                listToUse.Entries.Remove(listEntry);
                if (createDefaultList)
                {
                    project.CrossSectionLists.Remove(listRel);
                    project.Init.CrossSectionLists.Remove(listRel);
                }
            }));

        return true;
    }

    public CrossSectionDocument CreateDefaultCrossSectionDocument(string key, string worldRelativePath)
    {
        return CreateDefaultRoadCrossSection(key, WorldProject.NormalizeRelativePath(worldRelativePath));
    }

    public bool EnsureCrossSectionDocument(string listRelativePath, string key, string entryRelativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (project.CrossSections.ContainsKey(key))
        {
            return true;
        }

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, listRelativePath);
        string xmlFullPath = Path.GetFullPath(Path.Combine(listDir, entryRelativePath));
        string xmlWorldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));
        if (File.Exists(xmlFullPath))
        {
            string xml = File.ReadAllText(xmlFullPath, Encoding.UTF8);
            (CrossSectionDocument? loadedDoc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionXmlSerializer.Parse(key, xmlWorldRel, xmlFullPath, xml);
            if (loadedDoc is null)
            {
                ParseDiagnostics.Value = diag;
                LastStatusMessage.Value = $"CrossSection '{Path.GetFileName(xmlFullPath)}' の解析に失敗しました。";
                return false;
            }

            project.CrossSections[key] = loadedDoc;
            return true;
        }

        CrossSectionDocument newDoc = CreateDefaultRoadCrossSection(key, xmlWorldRel);
        WorldFileTarget target = new(WorldFileKind.CrossSectionXml, xmlWorldRel);
        Execute(new DelegateEditorCommand(
            $"CrossSection '{key}' の XML を新規作成",
            InvalidationScope.FullRebuild,
            target,
            () => project.CrossSections[key] = newDoc,
            () => project.CrossSections.Remove(key)));
        return true;
    }

    public bool TryCopyOrResolveExternalCrossSectionXml(
        string sourceFilePath,
        string listRelativePath,
        string? desiredKey,
        out string entryRelativePath,
        out string worldRelativePath,
        out CrossSectionDocument? parsedDoc,
        out string resolvedKey)
    {
        entryRelativePath = string.Empty;
        worldRelativePath = string.Empty;
        parsedDoc = null;
        resolvedKey = string.Empty;

        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                "CrossSections",
                _ => false,
                out string destFullPath,
                out string normalizedWorldRel))
        {
            return false;
        }

        string key = ResolveUniqueResourceKey(
            desiredKey,
            Path.GetFileNameWithoutExtension(destFullPath),
            k => project.CrossSections.ContainsKey(k));

        string xml = File.ReadAllText(destFullPath, Encoding.UTF8);
        (CrossSectionDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionXmlSerializer.Parse(key, normalizedWorldRel, destFullPath, xml);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"CrossSection '{Path.GetFileName(destFullPath)}' の解析に失敗しました。";
            return false;
        }

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, listRelativePath);
        entryRelativePath = Path.GetRelativePath(listDir, destFullPath).Replace('\\', '/');
        worldRelativePath = normalizedWorldRel;
        parsedDoc = doc;
        resolvedKey = key;
        return true;
    }

    public static bool IsCrossSectionKeyReferencedInOtherLists(
        WorldProject project,
        string key,
        string? excludeListRelPath,
        int excludeEntryIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? normExclude = excludeListRelPath is null ? null : WorldProject.NormalizeRelativePath(excludeListRelPath);

        foreach (KeyValuePair<string, CrossSectionListDocument> kv in project.CrossSectionLists)
        {
            bool isExcludedList = normExclude is not null
                && string.Equals(WorldProject.NormalizeRelativePath(kv.Key), normExclude, StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < kv.Value.Entries.Count; i++)
            {
                if (isExcludedList && i == excludeEntryIndex)
                {
                    continue;
                }

                if (string.Equals(kv.Value.Entries[i].Key, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool AddNewCrossSectionList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null || project.IsProtected(normalizedInit))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        if (project.CrossSectionLists.ContainsKey(normalizedRel))
        {
            return false;
        }

        CrossSectionListDocument doc = new() { RelativePath = normalizedRel };
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.CrossSectionsTxt, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"断面リスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.CrossSectionLists[normalizedRel] = doc;
                project.Init.CrossSectionLists.Add(normalizedRel);
            },
            () =>
            {
                project.CrossSectionLists.Remove(normalizedRel);
                project.Init.CrossSectionLists.Remove(normalizedRel);
            }));

        return true;
    }

    public bool AddNewModelList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || project.IsProtected(project.Init.RelativePath))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        if (project.ModelLists.ContainsKey(normalizedRel))
        {
            return false;
        }

        ModelListDocument doc = new ModelListDocument { RelativePath = normalizedRel };
        AssetListLoadReference initRef = new AssetListLoadReference(null, normalizedRel);
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new WorldFileTarget(WorldFileKind.ModelsTxt, normalizedRel),
            new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath)),
        };

        Execute(new DelegateEditorCommand(
            $"モデルリスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.ModelLists[normalizedRel] = doc;
                project.Init.ModelLists.Add(initRef);
            },
            () =>
            {
                project.ModelLists.Remove(normalizedRel);
                project.Init.ModelLists.Remove(initRef);
            }));

        return true;
    }

    public bool AddNewSoundList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || project.IsProtected(project.Init.RelativePath))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        if (project.SoundLists.ContainsKey(normalizedRel))
        {
            return false;
        }

        SoundListDocument doc = new SoundListDocument { RelativePath = normalizedRel };
        AssetListLoadReference initRef = new AssetListLoadReference(null, normalizedRel);
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new WorldFileTarget(WorldFileKind.SoundsTxt, normalizedRel),
            new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath)),
        };

        Execute(new DelegateEditorCommand(
            $"サウンドリスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.SoundLists[normalizedRel] = doc;
                project.Init.SoundLists.Add(initRef);
            },
            () =>
            {
                project.SoundLists.Remove(normalizedRel);
                project.Init.SoundLists.Remove(initRef);
            }));

        return true;
    }

    public bool RemoveModelList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        return project is not null
            && RemoveInitAssetList(project, project.ModelLists, project.Init.ModelLists, relativePath, WorldFileKind.ModelsTxt, "モデルリスト");
    }

    public bool RemoveSoundList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        return project is not null
            && RemoveInitAssetList(project, project.SoundLists, project.Init.SoundLists, relativePath, WorldFileKind.SoundsTxt, "サウンドリスト");
    }

    public bool RemoveCrossSectionList(string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string normalizedInit = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project.IsProtected(normalizedInit))
        {
            LastStatusMessage.Value = "Init.csx が上書き保護されているため、断面リストを削除できません。";
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        if (!project.CrossSectionLists.TryGetValue(normalizedRel, out CrossSectionListDocument? existingDoc))
        {
            return false;
        }

        int refIndex = -1;
        string? existingRef = null;
        for (int i = 0; i < project.Init.CrossSectionLists.Count; i++)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(project.Init.CrossSectionLists[i]), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                refIndex = i;
                existingRef = project.Init.CrossSectionLists[i];
                break;
            }
        }

        Dictionary<string, CrossSectionDocument> orphanedSections = new(StringComparer.Ordinal);
        foreach (CrossSectionListEntry entry in existingDoc.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key)
                && !IsCrossSectionKeyReferencedInOtherLists(project, entry.Key, normalizedRel)
                && project.CrossSections.TryGetValue(entry.Key, out CrossSectionDocument? csDoc))
            {
                orphanedSections[entry.Key] = csDoc;
            }
        }

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.CrossSectionsTxt, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"断面リスト '{normalizedRel}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.CrossSectionLists.Remove(normalizedRel);
                if (existingRef is not null)
                {
                    project.Init.CrossSectionLists.Remove(existingRef);
                }

                foreach (string key in orphanedSections.Keys)
                {
                    project.CrossSections.Remove(key);
                }
            },
            () =>
            {
                project.CrossSectionLists[normalizedRel] = existingDoc;
                if (existingRef is not null)
                {
                    int insertAt = Math.Clamp(refIndex, 0, project.Init.CrossSectionLists.Count);
                    project.Init.CrossSectionLists.Insert(insertAt, existingRef);
                }

                foreach (KeyValuePair<string, CrossSectionDocument> kv in orphanedSections)
                {
                    project.CrossSections[kv.Key] = kv.Value;
                }
            }));

        return true;
    }

    public bool RemoveLaneLayout(string key)
    {
        WorldProject? project = CurrentProject.Value;
        return project is not null
            && RemoveInitResource(project, project.LaneLayouts, project.Init.LaneLayouts, key, WorldFileKind.LaneLayoutXml, "LaneLayout");
    }

    public bool RemoveCrossSection(string key)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || !project.CrossSections.TryGetValue(key, out CrossSectionDocument? existingDoc))
        {
            return false;
        }

        List<(CrossSectionListDocument List, int Index, CrossSectionListEntry Entry)> removedEntries = new();
        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.CrossSectionXml, WorldProject.NormalizeRelativePath(existingDoc.RelativePath)),
        ];

        foreach (CrossSectionListDocument listDoc in project.CrossSectionLists.Values)
        {
            for (int i = listDoc.Entries.Count - 1; 0 <= i; i--)
            {
                if (string.Equals(listDoc.Entries[i].Key, key, StringComparison.Ordinal))
                {
                    removedEntries.Add((listDoc, i, listDoc.Entries[i]));
                    WorldFileTarget listTarget = new(WorldFileKind.CrossSectionsTxt, WorldProject.NormalizeRelativePath(listDoc.RelativePath));
                    if (!targets.Contains(listTarget))
                    {
                        targets.Add(listTarget);
                    }
                }
            }
        }

        Execute(new DelegateEditorCommand(
            $"CrossSection '{key}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.CrossSections.Remove(key);
                foreach ((CrossSectionListDocument listDoc, int idx, CrossSectionListEntry _) in removedEntries)
                {
                    if (0 <= idx && idx < listDoc.Entries.Count)
                    {
                        listDoc.Entries.RemoveAt(idx);
                    }
                }
            },
            () =>
            {
                project.CrossSections[key] = existingDoc;
                for (int i = removedEntries.Count - 1; 0 <= i; i--)
                {
                    (CrossSectionListDocument listDoc, int idx, CrossSectionListEntry entry) = removedEntries[i];
                    int insertAt = Math.Clamp(idx, 0, listDoc.Entries.Count);
                    listDoc.Entries.Insert(insertAt, entry);
                }
            }));

        return true;
    }

    public bool AddNewSplineTemplate(string key, string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string trimmedKey = key.Trim();
        if (project.SplineTemplates.ContainsKey(trimmedKey))
        {
            return false;
        }

        if (project.IsProtected(WorldProjectStorage.TemplatesLoadRelativePath))
        {
            LastStatusMessage.Value = "Templates/Load.csx が上書き保護されているため、SplineTemplate を追加できません。";
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        string defaultLayout = project.LaneLayouts.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
        string? defaultCrossSection = project.CrossSections.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        SplineTemplateDocument doc = new()
        {
            Key = trimmedKey,
            RelativePath = normalizedRel,
            OutletLayoutKey = defaultLayout,
        };
        if (!string.IsNullOrEmpty(defaultCrossSection))
        {
            doc.Extrusions.Add(new SplineTemplateExtrusionRule
            {
                Key = "MainSurface",
                CrossSectionKey = defaultCrossSection,
            });
        }

        string previousDefaultTemplate = PlacementSplineTemplateKey.Value;
        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.SplineTemplateCsx, normalizedRel),
            new(WorldFileKind.TemplatesLoadCsx, WorldProjectStorage.TemplatesLoadRelativePath),
        ];

        Execute(new DelegateEditorCommand(
            $"SplineTemplate '{trimmedKey}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.SplineTemplates[trimmedKey] = doc;
                if (string.IsNullOrEmpty(PlacementSplineTemplateKey.Value))
                {
                    PlacementSplineTemplateKey.Value = trimmedKey;
                }
            },
            () =>
            {
                project.SplineTemplates.Remove(trimmedKey);
                PlacementSplineTemplateKey.Value = previousDefaultTemplate;
            }));

        return true;
    }

    public bool CreateNewJunctionTemplate(string desiredKey, out string? registeredKey, out string? registeredRelativePath)
    {
        registeredKey = null;
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string uniqueKey = ResolveUniqueResourceKey(desiredKey, "Junction", k => project.JunctionTemplates.ContainsKey(k));
        string relPath = WorldProjectStorage.BuildJunctionTemplateRelativePath(uniqueKey);
        if (!AddNewJunctionTemplate(uniqueKey, relPath))
        {
            return false;
        }

        registeredKey = uniqueKey;
        registeredRelativePath = relPath;
        return true;
    }

    public bool AddNewJunctionTemplate(string key, string relativePath)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string trimmedKey = key.Trim();
        if (project.JunctionTemplates.ContainsKey(trimmedKey))
        {
            return false;
        }

        if (project.IsProtected(WorldProjectStorage.TemplatesLoadRelativePath))
        {
            LastStatusMessage.Value = "Templates/Load.csx が上書き保護されているため、JunctionTemplate を追加できません。";
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        string defaultLayout = project.LaneLayouts.Keys
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;

        JunctionTemplateDocument doc = new()
        {
            Key = trimmedKey,
            RelativePath = normalizedRel,
        };

        if (!string.IsNullOrEmpty(defaultLayout) && project.LaneLayouts.TryGetValue(defaultLayout, out LaneLayoutDocument? layoutDoc))
        {
            doc.Ports.Add(new JunctionPortDefinition("S", defaultLayout, new PoseDegreesModel(0.0, 0.0, 0.0, 0.0, 180.0, 0.0)));
            doc.Ports.Add(new JunctionPortDefinition("N", defaultLayout, new PoseDegreesModel(0.0, 0.0, 24.0, 0.0, 0.0, 0.0)));

            // Add default straight wires between compatible opposite-directed pin pairs
            int laneCount = layoutDoc.Lanes.Count;
            for (int i = 0; i < laneCount; i++)
            {
                int oppositePin = laneCount - 1 - i;
                LaneDefinition fromLane = layoutDoc.Lanes[i];
                LaneDefinition toLane = layoutDoc.Lanes[oppositePin];
                if ((fromLane.Directions == LaneFlowDirections.InOut && toLane.Directions == LaneFlowDirections.InOut)
                    || (fromLane.Directions == LaneFlowDirections.In && toLane.Directions == LaneFlowDirections.Out)
                    || (fromLane.Directions == LaneFlowDirections.Out && toLane.Directions == LaneFlowDirections.In))
                {
                    JunctionPathTemplateModel wire = new()
                    {
                        Key = $"SN_{i}",
                        FromPortKey = "S",
                        FromPinIndex = i,
                        ToPortKey = "N",
                        ToPinIndex = oppositePin,
                    };
                    wire.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity));
                    doc.Paths.Add(wire);
                }
            }
        }

        string previousDefaultTemplate = PlacementJunctionTemplateKey.Value;
        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.JunctionTemplateCsx, normalizedRel),
            new(WorldFileKind.TemplatesLoadCsx, WorldProjectStorage.TemplatesLoadRelativePath),
        ];

        Execute(new DelegateEditorCommand(
            $"JunctionTemplate '{trimmedKey}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.JunctionTemplates[trimmedKey] = doc;
                if (string.IsNullOrEmpty(PlacementJunctionTemplateKey.Value))
                {
                    PlacementJunctionTemplateKey.Value = trimmedKey;
                }
            },
            () =>
            {
                project.JunctionTemplates.Remove(trimmedKey);
                PlacementJunctionTemplateKey.Value = previousDefaultTemplate;
            }));

        return true;
    }

    public bool MutateJunctionTemplate(
        string key,
        string description,
        Action<JunctionTemplateDocument> mutator,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!project.JunctionTemplates.TryGetValue(key, out JunctionTemplateDocument? doc))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        if (project.IsProtected(normalizedRel))
        {
            LastStatusMessage.Value = $"テンプレート '{normalizedRel}' は上書き保護されているため編集できません。";
            return false;
        }

        JunctionTemplateDocument before = JunctionAnchorUtilities.DeepCloneJunctionTemplate(doc);
        JunctionTemplateDocument draft = JunctionAnchorUtilities.DeepCloneJunctionTemplate(doc);
        mutator(draft);
        draft.Key = doc.Key;
        draft.RelativePath = doc.RelativePath;

        WorldFileTarget target = new(WorldFileKind.JunctionTemplateCsx, normalizedRel);
        if (!string.IsNullOrEmpty(mergeKey))
        {
            Execute(new MergeableValueCommand<JunctionTemplateDocument>(
                description,
                mergeKey,
                InvalidationScope.FullRebuild,
                target,
                before,
                draft,
                applied =>
                {
                    if (project.JunctionTemplates.TryGetValue(key, out JunctionTemplateDocument? currentDoc))
                    {
                        JunctionAnchorUtilities.CopyJunctionTemplate(applied, currentDoc);
                    }
                }));
        }
        else
        {
            Execute(new DelegateEditorCommand(
                description,
                InvalidationScope.FullRebuild,
                target,
                () =>
                {
                    if (project.JunctionTemplates.TryGetValue(key, out JunctionTemplateDocument? currentDoc))
                    {
                        JunctionAnchorUtilities.CopyJunctionTemplate(draft, currentDoc);
                    }
                },
                () =>
                {
                    if (project.JunctionTemplates.TryGetValue(key, out JunctionTemplateDocument? currentDoc))
                    {
                        JunctionAnchorUtilities.CopyJunctionTemplate(before, currentDoc);
                    }
                }));
        }

        return true;
    }

    public bool MutateSplineTemplate(
        string key,
        string description,
        Action<SplineTemplateDocument> mutator,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        WorldProject? project = CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!project.SplineTemplates.TryGetValue(key, out SplineTemplateDocument? doc))
        {
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        if (project.IsProtected(normalizedRel))
        {
            LastStatusMessage.Value = $"テンプレート '{normalizedRel}' は上書き保護されているため編集できません。";
            return false;
        }

        SplineTemplateDocument before = SplineAnchorUtilities.DeepCloneSplineTemplate(doc);
        SplineTemplateDocument draft = SplineAnchorUtilities.DeepCloneSplineTemplate(doc);
        mutator(draft);
        draft.Key = doc.Key;
        draft.RelativePath = doc.RelativePath;

        WorldFileTarget target = new(WorldFileKind.SplineTemplateCsx, normalizedRel);
        if (!string.IsNullOrEmpty(mergeKey))
        {
            Execute(new MergeableValueCommand<SplineTemplateDocument>(
                description,
                mergeKey,
                InvalidationScope.FullRebuild,
                target,
                before,
                draft,
                applied =>
                {
                    if (project.SplineTemplates.TryGetValue(key, out SplineTemplateDocument? currentDoc))
                    {
                        SplineAnchorUtilities.CopySplineTemplate(applied, currentDoc);
                    }
                }));
        }
        else
        {
            Execute(new DelegateEditorCommand(
                description,
                InvalidationScope.FullRebuild,
                target,
                () =>
                {
                    if (project.SplineTemplates.TryGetValue(key, out SplineTemplateDocument? currentDoc))
                    {
                        SplineAnchorUtilities.CopySplineTemplate(draft, currentDoc);
                    }
                },
                () =>
                {
                    if (project.SplineTemplates.TryGetValue(key, out SplineTemplateDocument? currentDoc))
                    {
                        SplineAnchorUtilities.CopySplineTemplate(before, currentDoc);
                    }
                }));
        }

        return true;
    }

    public bool ImportExistingModelList(string sourceFilePath, out string? registeredRelativePath)
    {
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null || project.IsProtected(normalizedInit))
        {
            if (project is not null)
            {
                LastStatusMessage.Value = "Init.csx が上書き保護されているため、既存モデルリストを読み込めません。";
            }

            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                string.Empty,
                rel => project.ModelLists.ContainsKey(rel),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string text = File.ReadAllText(fullPath, Encoding.UTF8);
        (ModelListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = ModelListSerializer.Parse(null, normalizedRel, fullPath, text);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"モデルリスト '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        AssetListLoadReference initRef = new(null, normalizedRel);
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.ModelsTxt, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"既存モデルリスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.ModelLists[normalizedRel] = doc;
                project.Init.ModelLists.Add(initRef);
            },
            () =>
            {
                project.ModelLists.Remove(normalizedRel);
                project.Init.ModelLists.Remove(initRef);
            }));

        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingSoundList(string sourceFilePath, out string? registeredRelativePath)
    {
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null || project.IsProtected(normalizedInit))
        {
            if (project is not null)
            {
                LastStatusMessage.Value = "Init.csx が上書き保護されているため、既存サウンドリストを読み込めません。";
            }

            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                string.Empty,
                rel => project.SoundLists.ContainsKey(rel),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string text = File.ReadAllText(fullPath, Encoding.UTF8);
        (SoundListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = SoundListSerializer.Parse(null, normalizedRel, fullPath, text);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"サウンドリスト '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        AssetListLoadReference initRef = new(null, normalizedRel);
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.SoundsTxt, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"既存サウンドリスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.SoundLists[normalizedRel] = doc;
                project.Init.SoundLists.Add(initRef);
            },
            () =>
            {
                project.SoundLists.Remove(normalizedRel);
                project.Init.SoundLists.Remove(initRef);
            }));

        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingCrossSectionList(string sourceFilePath, out string? registeredRelativePath)
    {
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null || project.IsProtected(normalizedInit))
        {
            if (project is not null)
            {
                LastStatusMessage.Value = "Init.csx が上書き保護されているため、既存断面リストを読み込めません。";
            }

            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                string.Empty,
                rel => project.CrossSectionLists.ContainsKey(rel),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string text = File.ReadAllText(fullPath, Encoding.UTF8);
        (CrossSectionListDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionListSerializer.Parse(normalizedRel, fullPath, text);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"断面リスト '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, normalizedRel);
        Dictionary<string, CrossSectionDocument> loadedSections = new(StringComparer.Ordinal);
        foreach (CrossSectionListEntry entry in doc.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)
                || string.IsNullOrWhiteSpace(entry.RelativePath)
                || project.CrossSections.ContainsKey(entry.Key)
                || loadedSections.ContainsKey(entry.Key))
            {
                continue;
            }

            string xmlFullPath = Path.GetFullPath(Path.Combine(listDir, entry.RelativePath));
            if (!File.Exists(xmlFullPath))
            {
                continue;
            }

            string xmlWorldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));
            string xmlText = File.ReadAllText(xmlFullPath, Encoding.UTF8);
            (CrossSectionDocument? csDoc, IReadOnlyList<ParseDiagnostic> csDiag) = CrossSectionXmlSerializer.Parse(entry.Key, xmlWorldRel, xmlFullPath, xmlText);
            if (csDoc is not null)
            {
                loadedSections[entry.Key] = csDoc;
            }
        }

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.CrossSectionsTxt, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"既存断面リスト '{normalizedRel}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.CrossSectionLists[normalizedRel] = doc;
                project.Init.CrossSectionLists.Add(normalizedRel);
                foreach (KeyValuePair<string, CrossSectionDocument> kv in loadedSections)
                {
                    project.CrossSections[kv.Key] = kv.Value;
                }
            },
            () =>
            {
                project.CrossSectionLists.Remove(normalizedRel);
                project.Init.CrossSectionLists.Remove(normalizedRel);
                foreach (string key in loadedSections.Keys)
                {
                    project.CrossSections.Remove(key);
                }
            }));

        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingLaneLayout(string sourceFilePath, string? desiredKey, out string? registeredKey, out string? registeredRelativePath)
    {
        registeredKey = null;
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null || project.IsProtected(normalizedInit))
        {
            if (project is not null)
            {
                LastStatusMessage.Value = "Init.csx が上書き保護されているため、既存 LaneLayout を読み込めません。";
            }

            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                "LaneLayouts",
                rel => project.LaneLayouts.Values.Any(l => string.Equals(WorldProject.NormalizeRelativePath(l.RelativePath), rel, StringComparison.OrdinalIgnoreCase)),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string key = ResolveUniqueResourceKey(
            desiredKey,
            Path.GetFileNameWithoutExtension(fullPath),
            k => project.LaneLayouts.ContainsKey(k));

        string xml = File.ReadAllText(fullPath, Encoding.UTF8);
        (LaneLayoutDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = LaneLayoutXmlSerializer.Parse(key, normalizedRel, fullPath, xml);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"LaneLayout '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        ResourceLoadReference initRef = new(key, normalizedRel);
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.LaneLayoutXml, normalizedRel),
            new(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"既存 LaneLayout '{key}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.LaneLayouts[key] = doc;
                project.Init.LaneLayouts.Add(initRef);
            },
            () =>
            {
                project.LaneLayouts.Remove(key);
                project.Init.LaneLayouts.Remove(initRef);
            }));

        registeredKey = key;
        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingCrossSection(string sourceFilePath, string? desiredKey, out string? registeredKey, out string? registeredRelativePath)
    {
        registeredKey = null;
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        string normalizedInit = project is null ? string.Empty : WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project is null)
        {
            return false;
        }

        CrossSectionListDocument? targetList = project.CrossSectionLists.Values.FirstOrDefault();
        bool createDefaultList = targetList is null;
        if (createDefaultList && project.IsProtected(normalizedInit))
        {
            LastStatusMessage.Value = "Init.csx が上書き保護されているため、既存 CrossSection を読み込めません。";
            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                "CrossSections",
                rel => project.CrossSections.Values.Any(c => string.Equals(WorldProject.NormalizeRelativePath(c.RelativePath), rel, StringComparison.OrdinalIgnoreCase)),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string key = ResolveUniqueResourceKey(
            desiredKey,
            Path.GetFileNameWithoutExtension(fullPath),
            k => project.CrossSections.ContainsKey(k));

        string xml = File.ReadAllText(fullPath, Encoding.UTF8);
        (CrossSectionDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = CrossSectionXmlSerializer.Parse(key, normalizedRel, fullPath, xml);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"CrossSection '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        string listRel = targetList is not null
            ? WorldProject.NormalizeRelativePath(targetList.RelativePath)
            : "CrossSections.txt";
        CrossSectionListDocument listToUse = targetList ?? new CrossSectionListDocument { RelativePath = listRel };
        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, listRel);
        string entryRel = Path.GetRelativePath(listDir, fullPath).Replace('\\', '/');
        CrossSectionListEntry listEntry = new(key, entryRel);

        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.CrossSectionXml, normalizedRel),
            new(WorldFileKind.CrossSectionsTxt, listRel),
        ];
        if (createDefaultList)
        {
            targets.Add(new WorldFileTarget(WorldFileKind.InitCsx, normalizedInit));
        }

        Execute(new DelegateEditorCommand(
            $"既存 CrossSection '{key}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                if (createDefaultList)
                {
                    project.CrossSectionLists[listRel] = listToUse;
                    project.Init.CrossSectionLists.Add(listRel);
                }

                listToUse.Entries.Add(listEntry);
                project.CrossSections[key] = doc;
            },
            () =>
            {
                project.CrossSections.Remove(key);
                listToUse.Entries.Remove(listEntry);
                if (createDefaultList)
                {
                    project.CrossSectionLists.Remove(listRel);
                    project.Init.CrossSectionLists.Remove(listRel);
                }
            }));

        registeredKey = key;
        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingSplineTemplate(string sourceFilePath, string? desiredKey, out string? registeredKey, out string? registeredRelativePath)
    {
        registeredKey = null;
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        if (project.IsProtected(WorldProjectStorage.TemplatesLoadRelativePath))
        {
            LastStatusMessage.Value = "Templates/Load.csx が上書き保護されているため、既存 SplineTemplate を読み込めません。";
            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                "Templates/Splines",
                rel => project.SplineTemplates.Values.Any(t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), rel, StringComparison.OrdinalIgnoreCase)),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string scriptText = File.ReadAllText(fullPath, Encoding.UTF8);
        (SplineTemplateDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = SplineTemplateCsxSerializer.Parse(fullPath, scriptText, normalizedRel);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"SplineTemplate '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        string fallbackBase = !string.IsNullOrWhiteSpace(doc.Key) ? doc.Key : Path.GetFileNameWithoutExtension(fullPath);
        string uniqueKey = ResolveUniqueResourceKey(
            desiredKey,
            fallbackBase,
            k => project.SplineTemplates.ContainsKey(k));
        doc.Key = uniqueKey;

        string previousDefaultTemplate = PlacementSplineTemplateKey.Value;
        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.SplineTemplateCsx, normalizedRel),
            new(WorldFileKind.TemplatesLoadCsx, WorldProjectStorage.TemplatesLoadRelativePath),
        ];

        Execute(new DelegateEditorCommand(
            $"既存 SplineTemplate '{uniqueKey}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.SplineTemplates[uniqueKey] = doc;
                if (string.IsNullOrEmpty(PlacementSplineTemplateKey.Value))
                {
                    PlacementSplineTemplateKey.Value = uniqueKey;
                }
            },
            () =>
            {
                project.SplineTemplates.Remove(uniqueKey);
                PlacementSplineTemplateKey.Value = previousDefaultTemplate;
            }));

        registeredKey = uniqueKey;
        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingJunctionTemplate(string sourceFilePath, string? desiredKey, out string? registeredKey, out string? registeredRelativePath)
    {
        registeredKey = null;
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        if (project.IsProtected(WorldProjectStorage.TemplatesLoadRelativePath))
        {
            LastStatusMessage.Value = "Templates/Load.csx が上書き保護されているため、既存 JunctionTemplate を読み込めません。";
            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                "Templates/Junctions",
                rel => project.JunctionTemplates.Values.Any(t => string.Equals(WorldProject.NormalizeRelativePath(t.RelativePath), rel, StringComparison.OrdinalIgnoreCase)),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string scriptText = File.ReadAllText(fullPath, Encoding.UTF8);
        (JunctionTemplateDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = JunctionTemplateCsxSerializer.Parse(fullPath, scriptText, normalizedRel);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"JunctionTemplate '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        string fallbackBase = !string.IsNullOrWhiteSpace(doc.Key) ? doc.Key : Path.GetFileNameWithoutExtension(fullPath);
        string uniqueKey = ResolveUniqueResourceKey(
            desiredKey,
            fallbackBase,
            k => project.JunctionTemplates.ContainsKey(k));
        doc.Key = uniqueKey;

        string previousDefaultTemplate = PlacementJunctionTemplateKey.Value;
        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.JunctionTemplateCsx, normalizedRel),
            new(WorldFileKind.TemplatesLoadCsx, WorldProjectStorage.TemplatesLoadRelativePath),
        ];

        Execute(new DelegateEditorCommand(
            $"既存 JunctionTemplate '{uniqueKey}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.JunctionTemplates[uniqueKey] = doc;
                if (string.IsNullOrEmpty(PlacementJunctionTemplateKey.Value))
                {
                    PlacementJunctionTemplateKey.Value = uniqueKey;
                }
            },
            () =>
            {
                project.JunctionTemplates.Remove(uniqueKey);
                PlacementJunctionTemplateKey.Value = previousDefaultTemplate;
            }));

        registeredKey = uniqueKey;
        registeredRelativePath = normalizedRel;
        return true;
    }

    public bool ImportExistingEnvironment(string sourceFilePath, out string? registeredRelativePath)
    {
        registeredRelativePath = null;
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string normalizedWorld = WorldProject.NormalizeRelativePath(project.Placement.RelativePath);
        if (project.IsProtected(normalizedWorld))
        {
            LastStatusMessage.Value = "World.csx が上書き保護されているため、既存環境設定を読み込めません。";
            return false;
        }

        if (!TryResolveImportTarget(
                project,
                sourceFilePath,
                string.Empty,
                rel => project.Environments.ContainsKey(rel),
                out string fullPath,
                out string normalizedRel))
        {
            return false;
        }

        string xml = File.ReadAllText(fullPath, Encoding.UTF8);
        (EnvironmentProfileDocument? doc, IReadOnlyList<ParseDiagnostic> diag) = EnvironmentProfileXmlSerializer.Parse(normalizedRel, fullPath, xml);
        if (doc is null)
        {
            ParseDiagnostics.Value = diag;
            LastStatusMessage.Value = $"環境設定 '{Path.GetFileName(fullPath)}' の解析に失敗しました。";
            return false;
        }

        string? previousEnvPath = project.Placement.EnvironmentPath;
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.EnvironmentXml, normalizedRel),
            new(WorldFileKind.WorldCsx, normalizedWorld),
        };

        Execute(new DelegateEditorCommand(
            $"既存環境設定 '{normalizedRel}' を読込",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                project.Environments[normalizedRel] = doc;
                project.Placement.EnvironmentPath = normalizedRel;
            },
            () =>
            {
                project.Environments.Remove(normalizedRel);
                project.Placement.EnvironmentPath = previousEnvPath;
            }));

        registeredRelativePath = normalizedRel;
        return true;
    }

    private bool TryResolveImportTarget(
        WorldProject project,
        string sourceFilePath,
        string preferredSubfolder,
        Func<string, bool> isAlreadyLoaded,
        out string destinationFullPath,
        out string normalizedRelativePath)
    {
        destinationFullPath = string.Empty;
        normalizedRelativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            LastStatusMessage.Value = "選択されたファイルが見つかりません。";
            return false;
        }

        string fullSource = Path.GetFullPath(sourceFilePath);
        string worldDirFull = Path.GetFullPath(project.WorldDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (fullSource.StartsWith(worldDirFull, StringComparison.OrdinalIgnoreCase))
        {
            string rel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, fullSource));
            if (isAlreadyLoaded(rel))
            {
                LastStatusMessage.Value = $"ファイル '{rel}' は既にプロジェクトへ登録されています。";
                return false;
            }

            destinationFullPath = fullSource;
            normalizedRelativePath = rel;
            return true;
        }

        string destDir = string.IsNullOrEmpty(preferredSubfolder)
            ? project.WorldDirectory
            : Path.Combine(project.WorldDirectory, preferredSubfolder);
        Directory.CreateDirectory(destDir);

        string baseName = Path.GetFileNameWithoutExtension(fullSource);
        string ext = Path.GetExtension(fullSource);
        string candidatePath = Path.Combine(destDir, $"{baseName}{ext}");
        int suffix = 2;
        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(destDir, $"{baseName}_{suffix}{ext}");
            suffix++;
        }

        File.Copy(fullSource, candidatePath, overwrite: false);
        destinationFullPath = Path.GetFullPath(candidatePath);
        normalizedRelativePath = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, destinationFullPath));
        return true;
    }

    private static string ResolveUniqueResourceKey(string? desiredKey, string fallbackBase, Func<string, bool> exists)
    {
        string raw = string.IsNullOrWhiteSpace(desiredKey) ? fallbackBase : desiredKey.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "Resource";
        }

        string candidate = raw;
        int n = 2;
        while (exists(candidate))
        {
            candidate = $"{raw}_{n}";
            n++;
        }

        return candidate;
    }

    public bool RemoveSplineTemplate(string key)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        bool removed = RemoveWorldTemplate(project, project.SplineTemplates, t => t.RelativePath, key, WorldFileKind.SplineTemplateCsx, "SplineTemplate");
        if (removed && string.Equals(PlacementSplineTemplateKey.Value, key, StringComparison.Ordinal))
        {
            PlacementSplineTemplateKey.Value = GetDefaultSplineTemplateKey(project);
        }

        return removed;
    }

    public bool RemoveJunctionTemplate(string key)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        bool removed = RemoveWorldTemplate(project, project.JunctionTemplates, t => t.RelativePath, key, WorldFileKind.JunctionTemplateCsx, "JunctionTemplate");
        if (removed && string.Equals(PlacementJunctionTemplateKey.Value, key, StringComparison.Ordinal))
        {
            PlacementJunctionTemplateKey.Value = GetDefaultJunctionTemplateKey(project);
        }

        return removed;
    }

    private bool RemoveInitAssetList<TDoc>(
        WorldProject project,
        IDictionary<string, TDoc> store,
        IList<AssetListLoadReference> initRefs,
        string relativePath,
        WorldFileKind fileKind,
        string label)
        where TDoc : class
    {
        string normalizedInit = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project.IsProtected(normalizedInit))
        {
            LastStatusMessage.Value = $"Init.csx が上書き保護されているため、{label}を削除できません。";
            return false;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(relativePath);
        if (!store.TryGetValue(normalizedRel, out TDoc? existingDoc))
        {
            return false;
        }

        int refIndex = -1;
        AssetListLoadReference? existingRef = null;
        for (int i = 0; i < initRefs.Count; i++)
        {
            if (string.Equals(WorldProject.NormalizeRelativePath(initRefs[i].RelativePath), normalizedRel, StringComparison.OrdinalIgnoreCase))
            {
                refIndex = i;
                existingRef = initRefs[i];
                break;
            }
        }

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new WorldFileTarget(fileKind, normalizedRel),
            new WorldFileTarget(WorldFileKind.InitCsx, normalizedInit),
        };

        Execute(new DelegateEditorCommand(
            $"{label} '{normalizedRel}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                store.Remove(normalizedRel);
                if (existingRef is not null)
                {
                    initRefs.Remove(existingRef);
                }
            },
            () =>
            {
                store[normalizedRel] = existingDoc;
                if (existingRef is not null && !initRefs.Contains(existingRef))
                {
                    int insertAt = Math.Clamp(refIndex, 0, initRefs.Count);
                    initRefs.Insert(insertAt, existingRef);
                }
            }));

        return true;
    }

    private bool RemoveInitResource<TDoc>(
        WorldProject project,
        IDictionary<string, TDoc> store,
        IList<ResourceLoadReference> initRefs,
        string key,
        WorldFileKind fileKind,
        string label)
        where TDoc : class
    {
        string normalizedInit = WorldProject.NormalizeRelativePath(project.Init.RelativePath);
        if (project.IsProtected(normalizedInit))
        {
            LastStatusMessage.Value = $"Init.csx が上書き保護されているため、{label}を削除できません。";
            return false;
        }

        if (!store.TryGetValue(key, out TDoc? existingDoc))
        {
            return false;
        }

        int refIndex = -1;
        ResourceLoadReference? existingRef = null;
        for (int i = 0; i < initRefs.Count; i++)
        {
            if (string.Equals(initRefs[i].Key, key, StringComparison.Ordinal))
            {
                refIndex = i;
                existingRef = initRefs[i];
                break;
            }
        }

        string relPath = existingRef?.RelativePath ?? string.Empty;
        IReadOnlyList<WorldFileTarget> targets = string.IsNullOrEmpty(relPath)
            ? new WorldFileTarget[] { new WorldFileTarget(WorldFileKind.InitCsx, normalizedInit) }
            : new WorldFileTarget[]
            {
                new WorldFileTarget(fileKind, WorldProject.NormalizeRelativePath(relPath)),
                new WorldFileTarget(WorldFileKind.InitCsx, normalizedInit),
            };

        Execute(new DelegateEditorCommand(
            $"{label} '{key}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                store.Remove(key);
                if (existingRef is not null)
                {
                    initRefs.Remove(existingRef);
                }
            },
            () =>
            {
                store[key] = existingDoc;
                if (existingRef is not null && !initRefs.Contains(existingRef))
                {
                    int insertAt = Math.Clamp(refIndex, 0, initRefs.Count);
                    initRefs.Insert(insertAt, existingRef);
                }
            }));

        return true;
    }

    private bool RemoveWorldTemplate<TDoc>(
        WorldProject project,
        IDictionary<string, TDoc> store,
        Func<TDoc, string> getRelativePath,
        string key,
        WorldFileKind fileKind,
        string label)
        where TDoc : class
    {
        if (project.IsProtected(WorldProjectStorage.TemplatesLoadRelativePath))
        {
            LastStatusMessage.Value = $"Templates/Load.csx が上書き保護されているため、{label}を削除できません。";
            return false;
        }

        if (!store.TryGetValue(key, out TDoc? existingDoc))
        {
            return false;
        }

        string normalizedTpl = WorldProject.NormalizeRelativePath(getRelativePath(existingDoc));

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new WorldFileTarget(fileKind, normalizedTpl),
            new WorldFileTarget(WorldFileKind.TemplatesLoadCsx, WorldProjectStorage.TemplatesLoadRelativePath),
        };

        Execute(new DelegateEditorCommand(
            $"{label} '{key}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () => store.Remove(key),
            () => store[key] = existingDoc));

        return true;
    }

    internal WorldFileWatcherService FileWatcherService => FileWatcher;

    public ExternalFileReloadResult ReloadExternalFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            return new ExternalFileReloadResult(false, false, false, WorldFileKind.WorldCsx, string.Empty, null, Array.Empty<ParseDiagnostic>());
        }

        ExternalFileReloadResult result = WorldProjectStorage.ReloadExternalFile(project, fullPath);
        if (!result.Handled)
        {
            return result;
        }

        if (result.SkippedDirty)
        {
            LastStatusMessage.Value = result.Message
                ?? $"外部で '{result.RelativePath}' が変更されましたが、未保存の変更を保護するため自動反映をスキップしました。";
            return result;
        }

        if (result.Reloaded)
        {
            string normalizedFull = Path.GetFullPath(fullPath);
            List<ParseDiagnostic> updatedDiags = new();
            foreach (ParseDiagnostic existing in ParseDiagnostics.Value)
            {
                bool matchesFull = !string.IsNullOrWhiteSpace(existing.FilePath)
                    && string.Equals(Path.GetFullPath(existing.FilePath), normalizedFull, StringComparison.OrdinalIgnoreCase);
                if (!matchesFull)
                {
                    updatedDiags.Add(existing);
                }
            }

            updatedDiags.AddRange(result.Diagnostics);
            ParseDiagnostics.Value = updatedDiags;

            RefreshValidation();
            ScheduleBackgroundScriptCompile();
            ProjectRevision.Value++;
            LastStatusMessage.Value = $"外部での変更を自動反映しました: {result.RelativePath}";
        }
        else if (!string.IsNullOrEmpty(result.Message))
        {
            LastStatusMessage.Value = result.Message;
        }

        return result;
    }

    public void ScheduleBackgroundScriptCompile()
    {
        if (!EnableBackgroundScriptCompilation)
        {
            return;
        }

        WorldProject? project = CurrentProject.Value;
        CancellationTokenSource? prevCts;
        long gen;
        CancellationTokenSource nextCts = new();
        lock (BackgroundCompileLock)
        {
            prevCts = BackgroundCompileCts;
            BackgroundCompileCts = nextCts;
            BackgroundCompileGeneration++;
            gen = BackgroundCompileGeneration;
        }

        prevCts?.Cancel();
        prevCts?.Dispose();

        if (project is null)
        {
            IsBackgroundCompiling.Value = false;
            BackgroundScriptDiagnostics.Value = Array.Empty<TransportX.Diagnostics.Error>();
            nextCts.Dispose();
            return;
        }

        ScriptCompileSnapshot snapshot = BackgroundScriptCompiler.CaptureSnapshot(project);
        int debounceMs = BackgroundCompileDebounceMs;
        CancellationToken token = nextCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (0 < debounceMs)
                {
                    await Task.Delay(debounceMs, token);
                }

                token.ThrowIfCancellationRequested();
                DispatchToUi(() =>
                {
                    if (gen == Volatile.Read(ref BackgroundCompileGeneration))
                    {
                        IsBackgroundCompiling.Value = true;
                    }
                });

                IReadOnlyList<TransportX.Diagnostics.Error> errors = BackgroundScriptCompiler.Compile(snapshot, token);
                token.ThrowIfCancellationRequested();

                DispatchToUi(() =>
                {
                    if (gen != Volatile.Read(ref BackgroundCompileGeneration))
                    {
                        return;
                    }

                    IsBackgroundCompiling.Value = false;
                    BackgroundScriptDiagnostics.Value = errors;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DispatchToUi(() =>
                {
                    if (gen != Volatile.Read(ref BackgroundCompileGeneration))
                    {
                        return;
                    }

                    IsBackgroundCompiling.Value = false;
                    BackgroundScriptDiagnostics.Value = new TransportX.Diagnostics.Error[]
                    {
                        new(TransportX.Diagnostics.ErrorLevel.Error, $"スクリプト事前コンパイルエラー: {ex.Message}", snapshot.WorldCsxFullPath),
                    };
                });
            }
        }, CancellationToken.None);
    }

    public async Task<IReadOnlyList<TransportX.Diagnostics.Error>> RunBackgroundScriptCompileNowAsync(
        CancellationToken cancellationToken = default)
    {
        WorldProject? project = CurrentProject.Value;
        if (project is null)
        {
            BackgroundScriptDiagnostics.Value = Array.Empty<TransportX.Diagnostics.Error>();
            return Array.Empty<TransportX.Diagnostics.Error>();
        }

        CancellationTokenSource? prevCts;
        long gen;
        lock (BackgroundCompileLock)
        {
            prevCts = BackgroundCompileCts;
            BackgroundCompileCts = null;
            BackgroundCompileGeneration++;
            gen = BackgroundCompileGeneration;
        }

        prevCts?.Cancel();
        prevCts?.Dispose();

        ScriptCompileSnapshot snapshot = BackgroundScriptCompiler.CaptureSnapshot(project);
        IsBackgroundCompiling.Value = true;
        try
        {
            IReadOnlyList<TransportX.Diagnostics.Error> errors =
                await Task.Run(() => BackgroundScriptCompiler.Compile(snapshot, cancellationToken), cancellationToken);
            if (gen == Volatile.Read(ref BackgroundCompileGeneration))
            {
                BackgroundScriptDiagnostics.Value = errors;
            }

            return errors;
        }
        finally
        {
            if (gen == Volatile.Read(ref BackgroundCompileGeneration))
            {
                IsBackgroundCompiling.Value = false;
            }
        }
    }

    private void StartWatchingProject(WorldProject project)
    {
        FileWatcher.StartWatching(project.WorldDirectory, project.WorldInfoFilePath);
    }

    private void OnExternalFileChanged(string fullPath)
    {
        _ = Task.Run(async () =>
        {
            await WaitForExternalFileUnlockAsync(fullPath);
            DispatchToUi(() => ReloadExternalFile(fullPath));
        });
    }

    private static async Task WaitForExternalFileUnlockAsync(string fullPath)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            try
            {
                using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }

    private void DispatchToUi(Action action)
    {
        if (UiThreadDispatcher is not null)
        {
            UiThreadDispatcher(action);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            Dispatcher.UIThread.Post(action);
            return;
        }

        action();
    }

    private static List<string> CollectSaveCandidatePaths(WorldProject project, bool dirtyOnly)
    {
        List<string> paths = new();
        if (!string.IsNullOrWhiteSpace(project.WorldInfoFilePath))
        {
            paths.Add(Path.GetFullPath(project.WorldInfoFilePath));
        }

        if (!string.IsNullOrWhiteSpace(project.WorldDirectory))
        {
            string worldDir = project.WorldDirectory;
            paths.Add(Path.GetFullPath(Path.Combine(worldDir, "__Editor.csx")));
            paths.Add(Path.GetFullPath(Path.Combine(worldDir, "__Editor.json")));

            foreach (WorldFileTarget dirty in project.DirtyFiles)
            {
                if (!string.IsNullOrWhiteSpace(dirty.RelativePath))
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(dirty.RelativePath))));
                }
            }

            if (!dirtyOnly)
            {
                paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(project.Placement.RelativePath))));
                paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProjectStorage.TemplatesLoadRelativePath)));
                paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(Path.ChangeExtension(project.Placement.RelativePath, ".manifest.xml")))));
                paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(project.Init.RelativePath))));
                foreach (ModelListDocument m in project.ModelLists.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(m.RelativePath))));
                }

                foreach (SoundListDocument s in project.SoundLists.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(s.RelativePath))));
                }

                foreach (CrossSectionListDocument csList in project.CrossSectionLists.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(csList.RelativePath))));
                }

                foreach (LaneLayoutDocument l in project.LaneLayouts.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(l.RelativePath))));
                }

                foreach (CrossSectionDocument c in project.CrossSections.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(c.RelativePath))));
                }

                foreach (EnvironmentProfileDocument e in project.Environments.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProject.NormalizeRelativePath(e.RelativePath))));
                }

                foreach (SplineTemplateDocument st in project.SplineTemplates.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProjectStorage.GetSplineTemplateRelativePath(st))));
                }

                foreach (JunctionTemplateDocument jt in project.JunctionTemplates.Values)
                {
                    paths.Add(Path.GetFullPath(Path.Combine(worldDir, WorldProjectStorage.GetJunctionTemplateRelativePath(jt))));
                }
            }
        }

        return paths;
    }

    public void RefreshValidation()
    {
        WorldProject? project = CurrentProject.Value;
        ValidationIssues.Value = project is null
            ? Array.Empty<ValidationIssue>()
            : WorldValidator.Validate(project);
    }

    private void OnCommandHistoryChanged(object? sender, CommandExecutedEventArgs e)
    {
        if (SuppressDirtyTracking)
        {
            return;
        }

        WorldProject? project = CurrentProject.Value;
        if (project is not null && e.Command is not null)
        {
            foreach (WorldFileTarget target in e.Command.AffectedFiles)
            {
                project.MarkDirty(target.Kind, target.RelativePath);
            }
        }

        RefreshValidation();
        ScheduleBackgroundScriptCompile();
        ProjectRevision.Value++;
    }

    private static int CountErrors(
        IReadOnlyList<ParseDiagnostic> parseDiagnostics,
        IReadOnlyList<ValidationIssue> validationIssues)
    {
        int count = 0;
        foreach (ParseDiagnostic pd in parseDiagnostics)
        {
            if (pd.Severity == DiagnosticSeverity.Error)
            {
                count++;
            }
        }

        foreach (ValidationIssue vi in validationIssues)
        {
            if (vi.Severity == ValidationSeverity.Error)
            {
                count++;
            }
        }

        return count;
    }

    private static LaneLayoutDocument CreateDefaultSymmetricLayout(string key, string relativePath)
    {
        LaneLayoutDocument doc = new LaneLayoutDocument
        {
            Key = key,
            RelativePath = relativePath,
        };
        doc.Lanes.Add(new LaneDefinition("Pedestrians", LaneFlowDirections.InOut, -6.25f, 0.0f, 1.5f, 1.25f));
        doc.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.Out, -2.0f, -0.12f, 2.75f, 2.0f));
        doc.Lanes.Add(new LaneDefinition("Cars", LaneFlowDirections.In, 2.0f, -0.12f, 2.0f, 2.75f));
        doc.Lanes.Add(new LaneDefinition("Pedestrians", LaneFlowDirections.InOut, 6.25f, 0.0f, 1.25f, 1.5f));
        return doc;
    }

    private static CrossSectionDocument CreateDefaultRoadCrossSection(string key, string relativePath)
    {
        CrossSectionDocument doc = new CrossSectionDocument
        {
            Key = key,
            RelativePath = relativePath,
        };
        doc.Vertices.Add(new CrossSectionVertexModel { Key = "LeftEdge", X = -4.25f, Y = -0.12f, NormalX = 0f, NormalY = 1f, U = -3.54f });
        doc.Vertices.Add(new CrossSectionVertexModel { Key = "RightEdge", X = 4.25f, Y = -0.12f, NormalX = 0f, NormalY = 1f, U = 3.54f });
        doc.Materials.Add(new CrossSectionMaterialModel { Name = "Asphalt", Roughness = 0.7f });
        doc.ColliderMaterials.Add(new CrossSectionColliderMaterialModel { Name = "Default", FrictionCoefficient = 0.9f, MaxRecoveryVelocity = 5.0f });

        CrossSectionSurfaceModel surface = new CrossSectionSurfaceModel
        {
            VInterval = 1.0f,
            VOffset = 0.0f,
            MaterialName = "Asphalt",
            ColliderMaterialName = "Default",
        };
        surface.VertexKeys.Add("LeftEdge");
        surface.VertexKeys.Add("RightEdge");
        doc.Surfaces.Add(surface);
        return doc;
    }

    public void Dispose()
    {
        FileWatcher.FileExternallyChanged -= OnExternalFileChanged;
        FileWatcher.Dispose();

        CancellationTokenSource? cts;
        lock (BackgroundCompileLock)
        {
            cts = BackgroundCompileCts;
            BackgroundCompileCts = null;
        }

        cts?.Cancel();
        cts?.Dispose();

        CommandHistory.HistoryChanged -= OnCommandHistoryChanged;
        Disposables.Dispose();
    }
}
