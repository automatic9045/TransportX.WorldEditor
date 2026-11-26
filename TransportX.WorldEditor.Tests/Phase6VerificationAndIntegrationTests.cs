using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using R3;
using TransportX.Dependency;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Diagnostics;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Verification;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.ViewModels.Editors;
using TransportX.WorldEditor.Views;
using TransportX.WorldEditor.Views.Controls;
using TransportX.WorldEditor.Views.Documents;
using TransportX.WorldEditor.Views.Editors;
using TransportX.WorldEditor.Views.Tools;
using TransportX.Worlds;
using Vortice.DXGI;
using Xunit;

namespace TransportX.WorldEditor.Tests;

public sealed class Phase6VerificationAndIntegrationTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p6_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static (bool Started, IReadOnlyList<TransportX.Diagnostics.Error> Errors, bool HasFatalError, bool TickSucceeded) RunHeadlessScriptVerification(
        WorldProject project,
        int tickCount)
    {
        using GraphicsHost graphicsHost = new();
        using DummyGraphicsClient graphicsClient = new();
        using ViewportWindowAdapter windowAdapter = new(0, 640, 480);
        using DummyInputHost inputHost = new();
        FreeCameraController cameraController = new();

        TransportX.Platform platform = new()
        {
            Window = windowAdapter,
            Input = inputHost.SilkContext,
        };

        using ScriptVerificationRunner runner = new();
        bool started = runner.Start(
            project,
            platform,
            graphicsHost,
            graphicsClient,
            cameraController.Camera,
            isDebugMode: false);

        if (started)
        {
            for (int i = 0; i < tickCount; i++)
            {
                runner.Tick(TimeSpan.FromSeconds(1.0 / 60.0));
            }
        }

        IReadOnlyList<TransportX.Diagnostics.Error> errors = runner.ErrorCollector.Errors;
        bool hasFatal = runner.HasFatalError;
        bool running = runner.IsRunning;
        return (started, errors, hasFatal, running);
    }

    [Fact]
    public void ScriptVerificationRunner_ExecutesRealGeneratedWorldWithUserCode_TicksAndUnloadsCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            string markerFile = Path.Combine(tempDir, "usercode_executed.txt");
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Mode2RunWorld", "Mode 2 Run", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            // Remove default optional domain plugin dependency when RoadTraffic.dll is not built into test bin
            project.Manifest.Dependencies.Clear();

            string escapedMarker = markerFile.Replace("\\", "\\\\", StringComparison.Ordinal);
            Assert.True(ws.SetWorldUserCode($"System.IO.File.WriteAllText(\"{escapedMarker}\", \"executed_ok\");"));

            WorldProjectSaveResult saveResult = ws.Save(dirtyOnly: false);
            Assert.DoesNotContain(
                saveResult.Diagnostics,
                d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);

            (bool started, IReadOnlyList<TransportX.Diagnostics.Error> errors, bool hasFatal, bool isRunning) =
                RunHeadlessScriptVerification(project, tickCount: 5);

            Assert.True(started);
            Assert.False(hasFatal);
            Assert.True(isRunning);
            Assert.DoesNotContain(
                errors,
                e => e.Level == TransportX.Diagnostics.ErrorLevel.Error || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal);
            Assert.True(File.Exists(markerFile));
            Assert.Equal("executed_ok", File.ReadAllText(markerFile));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void ScriptVerificationRunner_CapturesCompileAndRuntimeScriptErrors_WithFileAndLineNumbers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Mode2ErrWorld", "Mode 2 Err", "Desc");
            WorldWorkspaceService ws = shell.Workspace;
            project.Manifest.Dependencies.Clear();

            // 1. Intentional C# compile error inside <user-code>
            Assert.True(ws.SetWorldUserCode("int brokenSyntax = ( ;"));
            ws.Save(dirtyOnly: false);

            (bool compileStarted, IReadOnlyList<TransportX.Diagnostics.Error> compileErrors, bool compileFatal, _) =
                RunHeadlessScriptVerification(project, tickCount: 1);

            Assert.False(compileStarted);
            Assert.True(compileFatal);
            Assert.NotEmpty(compileErrors);
            Assert.Contains(
                compileErrors,
                e => (e.Level == TransportX.Diagnostics.ErrorLevel.Error || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal)
                     && ((!string.IsNullOrEmpty(e.Location) && e.Location.Contains("World.csx", StringComparison.OrdinalIgnoreCase))
                         || e.Message.Contains("CS", StringComparison.Ordinal)
                         || e.Message.Contains("error", StringComparison.OrdinalIgnoreCase)));

            // 2. Intentional runtime exception inside <user-code>
            // Note: TransportX UserScript.RunAsync captures runtime script exceptions as ErrorLevel.Error (non-fatal)
            // so the world keeps running while reporting the ScriptError with file/line info to ErrorCollector.
            Assert.True(ws.SetWorldUserCode("throw new System.InvalidOperationException(\"boom_verify_runtime\");"));
            ws.Save(dirtyOnly: false);

            (bool runtimeStarted, IReadOnlyList<TransportX.Diagnostics.Error> runtimeErrors, bool runtimeFatal, _) =
                RunHeadlessScriptVerification(project, tickCount: 1);

            Assert.True(runtimeStarted);
            Assert.False(runtimeFatal);
            TransportX.Diagnostics.Error runtimeErr = Assert.Single(
                runtimeErrors,
                e => e.Message.Contains("boom_verify_runtime", StringComparison.Ordinal));
            Assert.Equal(TransportX.Diagnostics.ErrorLevel.Error, runtimeErr.Level);
            Assert.True(0 < runtimeErr.LineNumber || (!string.IsNullOrEmpty(runtimeErr.Location) && runtimeErr.Location.Contains("World.csx", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void SidecarMetadata_PersistsCameraBookmarksLastCameraPoseAndVisibilityLayers_AcrossSaveAndReload()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string worldInfoPath;
            using (WorldWorkspaceService ws1 = new())
            {
                WorldProject project1 = ws1.CreateNewWorld(tempDir, "Author", "SidecarWorld", "Sidecar", "Desc");
                worldInfoPath = project1.WorldInfoFilePath;

                ws1.CaptureCurrentCameraPose = () => new CameraBookmarkModel(
                    "Last",
                    2,
                    -1,
                    10.5,
                    22.0,
                    33.5,
                    12.0,
                    -45.0,
                    0.0);

                Assert.True(ws1.AddCameraBookmark(
                    "駅西口",
                    new CameraBookmarkModel("ignored", 1, 2, 111.0, 22.5, 222.0, 15.0, 90.0, 0.0)));

                ws1.OverlayFlags.Value = ViewportOverlayFlags.Normal | ViewportOverlayFlags.ChunkGrid;

                WorldProjectSaveResult saveResult = ws1.Save(dirtyOnly: false);
                Assert.DoesNotContain(
                    saveResult.Diagnostics,
                    d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
                Assert.True(File.Exists(Path.Combine(project1.WorldDirectory, "__Editor.json")));
            }

            using WorldWorkspaceService ws2 = new();
            WorldProjectLoadResult loadResult = ws2.OpenWorld(worldInfoPath);
            Assert.NotNull(loadResult.Project);

            WorldProject project2 = loadResult.Project!;
            CameraBookmarkModel bookmark = Assert.Single(project2.Sidecar.CameraBookmarks);
            Assert.Equal("駅西口", bookmark.Name);
            Assert.Equal(1, bookmark.ChunkX);
            Assert.Equal(2, bookmark.ChunkZ);
            Assert.Equal(111.0, bookmark.X);
            Assert.Equal(22.5, bookmark.Y);
            Assert.Equal(222.0, bookmark.Z);
            Assert.Equal(15.0, bookmark.RotationX);
            Assert.Equal(90.0, bookmark.RotationY);

            Assert.NotNull(project2.Sidecar.LastCameraPose);
            Assert.Equal(2, project2.Sidecar.LastCameraPose!.ChunkX);
            Assert.Equal(-1, project2.Sidecar.LastCameraPose.ChunkZ);
            Assert.Equal(10.5, project2.Sidecar.LastCameraPose.X);
            Assert.Equal(-45.0, project2.Sidecar.LastCameraPose.RotationY);

            Assert.True(project2.Sidecar.VisibilityLayers.Normal);
            Assert.False(project2.Sidecar.VisibilityLayers.Network);
            Assert.True(project2.Sidecar.VisibilityLayers.ChunkGrid);
            Assert.False(project2.Sidecar.VisibilityLayers.Anchors);
            Assert.False(project2.Sidecar.VisibilityLayers.Gizmos);

            Assert.True(ws2.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Normal));
            Assert.False(ws2.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Network));
            Assert.False(ws2.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Gizmos));

            CameraBookmarkModel? jumped = null;
            ws2.ApplyCameraBookmarkRequested += b => jumped = b;
            Assert.True(ws2.JumpToCameraBookmark(0));
            Assert.NotNull(jumped);
            Assert.Equal("駅西口", jumped!.Name);

            Assert.True(ws2.RemoveCameraBookmark(0));
            Assert.Empty(project2.Sidecar.CameraBookmarks);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void WorldSettings_EditsWorldUserCode_HandlesProtectedUnlock_AndPlayerLauncherResolvesPath()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "SettingsWorld", "Settings", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            WorldSettingsDocumentViewModel? vm = shell.OpenWorldSettings();
            Assert.NotNull(vm);

            // Edit World.csx <user-code> and persist
            vm!.WorldUserCode.Value = "// custom user code\nint a = 1;";
            Assert.Equal("// custom user code\nint a = 1;", project.Placement.UserCode);

            string dummyExe = Path.Combine(tempDir, "TransportX.Player.exe");
            File.WriteAllText(dummyExe, "dummy");
            vm.PlayerExecutablePath.Value = dummyExe;
            Assert.Equal(dummyExe, project.Sidecar.PlayerExecutablePath);
            Assert.Equal(Path.GetFullPath(dummyExe), PlayerLauncherService.ResolvePlayerExecutablePath(project));

            ws.Save(dirtyOnly: false);
            string worldCsxPath = Path.Combine(project.WorldDirectory, project.Placement.RelativePath);
            string writtenWorldCsx = File.ReadAllText(worldCsxPath);
            Assert.Contains("// custom user code", writtenWorldCsx);

            // Verify ProtectedReadOnly guard + unlock command
            project.ProtectedFiles.Add(WorldProject.NormalizeRelativePath(project.Placement.RelativePath));
            ws.ProjectRevision.Value++;
            Assert.True(vm.IsWorldProtected.Value);
            Assert.False(ws.SetWorldUserCode("blocked_while_protected"));

            vm.UnlockWorldProtectedCommand.Execute(Unit.Default);
            Assert.False(vm.IsWorldProtected.Value);
            Assert.False(project.IsProtected(project.Placement.RelativePath));
            Assert.True(ws.SetWorldUserCode("unlocked_and_editable"));

            // Confirm PlayerExecutablePath persists across reload
            string worldInfoPath = project.WorldInfoFilePath;
            ws.Save(dirtyOnly: false);
            using WorldWorkspaceService wsReloaded = new();
            WorldProjectLoadResult reloaded = wsReloaded.OpenWorld(worldInfoPath);
            Assert.NotNull(reloaded.Project);
            Assert.Equal(dummyExe, reloaded.Project!.Sidecar.PlayerExecutablePath);
            Assert.Equal(Path.GetFullPath(dummyExe), PlayerLauncherService.ResolvePlayerExecutablePath(reloaded.Project));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void DiagnosticsTool_UnifiesVerificationScriptErrors_AndNavigatesToElementOrAsset()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "DiagNavWorld", "Diag Nav", "Desc");
            WorldWorkspaceService ws = shell.Workspace;

            ChunkPropPlacement? prop = ws.AddPropPlacement(
                "SampleProp",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(100.0, 0.0, 100.0)));
            Assert.NotNull(prop);
            ws.ClearSelection();
            Assert.Null(ws.SelectedPropId.Value);

            // Close Doc_WorldSettings opened automatically by CreateAndActivateWorld to assert navigation re-opens it
            shell.CloseAssetDocument(new AssetBrowserEntry(
                AssetBrowserItemKind.WorldSettings,
                "WorldSettings",
                project.Placement.RelativePath,
                false));

            ws.ValidationIssues.Value =
            [
                new ValidationIssue(
                    ValidationSeverity.Warning,
                    "TEST_PROP",
                    $"Prop '{prop!.Id}' test warning.",
                    project.Placement.RelativePath),
            ];

            ws.SetVerificationErrors(
            [
                new TransportX.Diagnostics.Error(
                    TransportX.Diagnostics.ErrorLevel.Error,
                    "Mock script failure",
                    $"{project.Placement.RelativePath}(12,4)"),
            ]);

            Assert.Contains(shell.DiagnosticsTool.Items, item => item.ElementId == prop.Id);
            DiagnosticEntryViewModel scriptDiag = Assert.Single(
                shell.DiagnosticsTool.Items,
                item => item.Source.Contains("[検証]", StringComparison.Ordinal) && item.Line == 12);
            Assert.Equal(4, scriptDiag.Column);
            Assert.Contains("検証: 1", shell.DiagnosticsTool.HeaderSummary.Value);

            DiagnosticEntryViewModel propDiag = shell.DiagnosticsTool.Items.First(item => item.ElementId == prop.Id);
            shell.DiagnosticsTool.NavigateToEntry(propDiag);
            Assert.Equal(prop.Id, ws.SelectedPropId.Value);

            shell.DiagnosticsTool.NavigateToEntry(scriptDiag);
            Assert.Contains("行 12", ws.LastStatusMessage.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void AvaloniaUi_InstantiatesBindsMeasuresAndRendersPhase6ViewsCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Exception? capturedException = null;
        string tempDir = CreateTempDirectory();
        try
        {
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    using MainWindowViewModel shell = new();
                    WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Phase6UiWorld", "Phase 6 UI", "Desc");
                    shell.Workspace.AddCameraBookmark(
                        "UI視点",
                        new CameraBookmarkModel("UI視点", 0, 0, 125.0, 12.0, 125.0, 15.0, 0.0, 0.0));
                    shell.Workspace.ActiveRenderMode.Value = EditorRenderMode.ScriptVerification;
                    shell.Workspace.SetVerificationErrors(
                    [
                        new TransportX.Diagnostics.Error(
                            TransportX.Diagnostics.ErrorLevel.Warning,
                            "UIテスト警告",
                            $"{project.Placement.RelativePath}(5,1)"),
                    ]);

                    using WorldSettingsDocumentViewModel? settingsVm = shell.OpenWorldSettings();
                    Assert.NotNull(settingsVm);

                    ViewLocator locator = new();
                    Control? settingsView = locator.Build(settingsVm!);
                    Control? diagnosticsView = locator.Build(shell.DiagnosticsTool);
                    Control? viewportView = locator.Build(shell.ViewportDocument);

                    Assert.IsType<WorldSettingsView>(settingsView);
                    Assert.IsType<DiagnosticsView>(diagnosticsView);
                    Assert.IsType<ViewportDocumentView>(viewportView);

                    MeasureAndArrange(settingsView!, settingsVm!, 920, 720);
                    MeasureAndArrange(diagnosticsView!, shell.DiagnosticsTool, 640, 260);
                    MeasureAndArrange(viewportView!, shell.ViewportDocument, 980, 620);

                    MainWindow mainWindow = new()
                    {
                        DataContext = shell,
                    };
                    mainWindow.Measure(new Avalonia.Size(1280, 800));
                    mainWindow.Arrange(new Avalonia.Rect(0, 0, 1280, 800));
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
            });

            Assert.Null(capturedException);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void Win32HwndViewportHost_ConsecutiveClicksDoubleClicksAndMouseWheel_AcceptsEveryOperation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const uint wmLButtonDown = 0x0201;
        const uint wmLButtonUp = 0x0202;
        const uint wmLButtonDblClk = 0x0203;
        const uint wmRButtonDown = 0x0204;
        const uint wmRButtonUp = 0x0205;
        const uint wmRButtonDblClk = 0x0206;
        const uint wmMButtonDown = 0x0207;
        const uint wmMButtonUp = 0x0208;
        const uint wmMButtonDblClk = 0x0209;
        const uint wmMouseWheel = 0x020A;

        // CS_DBLCLKS (0x0008) must not be set on the viewport window class
        Assert.Equal(0u, Win32HwndViewportHost.NativeWindowClassStyleForTest & 0x0008u);

        Exception? capturedException = null;
        AvaloniaUiTestHost.Run(() =>
        {
            try
            {
                using Win32HwndViewportHost host = new();
                nint lParamPoint = unchecked((nint)((200 << 16) | 150));

                // Consecutive right-drags including rapid WM_RBUTTONDBLCLK must all enter active drag state
                for (int i = 0; i < 4; i++)
                {
                    uint downMsg = (i % 2 == 0) ? wmRButtonDown : wmRButtonDblClk;
                    host.DispatchNativeMessageForTest(0, downMsg, 0, lParamPoint);
                    Assert.True(host.IsRightButtonDownForTest);
                    host.DispatchNativeMessageForTest(0, wmRButtonUp, 0, lParamPoint);
                    Assert.False(host.IsRightButtonDownForTest);
                }

                // Consecutive middle-drags (mouse wheel press) including WM_MBUTTONDBLCLK must all enter active pan state
                for (int i = 0; i < 4; i++)
                {
                    uint downMsg = (i % 2 == 0) ? wmMButtonDown : wmMButtonDblClk;
                    host.DispatchNativeMessageForTest(0, downMsg, 0, lParamPoint);
                    Assert.True(host.IsMiddleButtonDownForTest);
                    host.DispatchNativeMessageForTest(0, wmMButtonUp, 0, lParamPoint);
                    Assert.False(host.IsMiddleButtonDownForTest);
                }

                // Consecutive left-clicks including WM_LBUTTONDBLCLK must all enter active press state
                for (int i = 0; i < 4; i++)
                {
                    uint downMsg = (i % 2 == 0) ? wmLButtonDown : wmLButtonDblClk;
                    host.DispatchNativeMessageForTest(0, downMsg, 0, lParamPoint);
                    Assert.True(host.IsLeftButtonDownForTest);
                    host.DispatchNativeMessageForTest(0, wmLButtonUp, 0, lParamPoint);
                    Assert.False(host.IsLeftButtonDownForTest);
                }

                // Wheel messages with +/-120 deltas must dispatch cleanly without throwing
                host.DispatchNativeMessageForTest(0, wmMouseWheel, unchecked((nint)(120 << 16)), lParamPoint);
                host.DispatchNativeMessageForTest(0, wmMouseWheel, unchecked((nint)(-120 << 16)), lParamPoint);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        Assert.Null(capturedException);
    }

    [Fact]
    public void WindowMenu_TogglesRestoresClosedToolPanes_AndResetsLayoutPreservingDocuments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        Exception? capturedException = null;
        try
        {
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    using MainWindowViewModel shell = new();
                    shell.CreateAndActivateWorld(tempDir, "Author", "WindowMenuWorld", "Window Menu", "Desc");
                    shell.OpenWorldSettings();

                    Assert.NotNull(shell.DockFactory.DocumentDock?.VisibleDockables);
                    Assert.Contains(shell.DockFactory.DocumentDock!.VisibleDockables!, d => d.Id == "Doc_WorldSettings");
                    Assert.True(shell.IsSceneHierarchyVisible.Value);
                    Assert.True(shell.IsAssetBrowserVisible.Value);
                    Assert.True(shell.IsInspectorVisible.Value);
                    Assert.True(shell.IsTimelineVisible.Value);
                    Assert.True(shell.IsDiagnosticsVisible.Value);

                    // Toggle/close several tool panes including collapsing LeftColumnDock
                    shell.ToggleSceneHierarchyCommand.Execute(Unit.Default);
                    shell.DockFactory.CloseDockable(shell.AssetBrowserTool);
                    shell.ToggleInspectorCommand.Execute(Unit.Default);
                    shell.ToggleTimelineCommand.Execute(Unit.Default);

                    Assert.False(shell.IsSceneHierarchyVisible.Value);
                    Assert.False(shell.IsAssetBrowserVisible.Value);
                    Assert.False(shell.IsInspectorVisible.Value);
                    Assert.False(shell.IsTimelineVisible.Value);
                    Assert.False(shell.DockFactory.IsDockableVisible("SceneHierarchy"));
                    Assert.False(shell.DockFactory.IsDockableVisible("AssetBrowser"));
                    Assert.False(shell.DockFactory.IsDockableVisible("Inspector"));
                    Assert.False(shell.DockFactory.IsDockableVisible("Timeline"));

                    // Restore each hidden/closed tool pane from the Window menu commands
                    shell.ToggleSceneHierarchyCommand.Execute(Unit.Default);
                    shell.ToggleAssetBrowserCommand.Execute(Unit.Default);
                    shell.ToggleInspectorCommand.Execute(Unit.Default);
                    shell.ToggleTimelineCommand.Execute(Unit.Default);

                    Assert.True(shell.IsSceneHierarchyVisible.Value);
                    Assert.True(shell.IsAssetBrowserVisible.Value);
                    Assert.True(shell.IsInspectorVisible.Value);
                    Assert.True(shell.IsTimelineVisible.Value);
                    Assert.True(shell.DockFactory.IsDockableVisible("SceneHierarchy"));
                    Assert.True(shell.DockFactory.IsDockableVisible("AssetBrowser"));
                    Assert.True(shell.DockFactory.IsDockableVisible("Inspector"));
                    Assert.True(shell.DockFactory.IsDockableVisible("Timeline"));

                    // Reset layout restores all tools and keeps open dynamic documents intact
                    shell.ToggleDiagnosticsCommand.Execute(Unit.Default);
                    Assert.False(shell.IsDiagnosticsVisible.Value);

                    shell.ResetWindowLayoutCommand.Execute(Unit.Default);
                    Assert.True(shell.IsSceneHierarchyVisible.Value);
                    Assert.True(shell.IsAssetBrowserVisible.Value);
                    Assert.True(shell.IsInspectorVisible.Value);
                    Assert.True(shell.IsTimelineVisible.Value);
                    Assert.True(shell.IsDiagnosticsVisible.Value);
                    Assert.Contains(shell.DockFactory.DocumentDock!.VisibleDockables!, d => d.Id == "Doc_WorldSettings");

                    MainWindow mainWindow = new()
                    {
                        DataContext = shell,
                    };
                    mainWindow.Measure(new Avalonia.Size(1280, 800));
                    mainWindow.Arrange(new Avalonia.Rect(0, 0, 1280, 800));
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
            });

            Assert.Null(capturedException);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }


    [Fact]
    public void Anchors_DefaultToEmpty_PreventSameNodeDuplicates_AndOmitVarOnCrossNodeReuse()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AnchorReuseWorld", "Anchor Reuse", "Desc");
            WorldWorkspaceService ws = shell.Workspace;
            project.Manifest.Dependencies.Clear();

            // 1. Default anchors for new splines and junction curves must be empty (null)
            SplinePlacementNode s1 = project.Placement.Splines[0];
            Assert.Null(s1.Curves[0].OutputAnchorId);

            SplinePlacementNode? s2 = ws.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(150.0, 0.0, 150.0)));
            Assert.NotNull(s2);
            Assert.Null(s2!.Curves[0].OutputAnchorId);

            SplinePlacementNode? s3 = ws.AddChainedSplineFromOutlet(s2.Id, "Spline1");
            Assert.NotNull(s3);
            Assert.Null(s3!.Curves[0].OutputAnchorId);

            Assert.True(ws.CreateNewJunctionTemplate("TestJunc", out string? juncKey, out _));
            AssetBrowserEntry? juncEntry = shell.AssetBrowserTool.JunctionTemplateItems.FirstOrDefault(e => e.Key == juncKey);
            Assert.NotNull(juncEntry);
            using JunctionTemplateEditorDocumentViewModel? juncVm =
                shell.OpenAsset(juncEntry!) as JunctionTemplateEditorDocumentViewModel;
            Assert.NotNull(juncVm);
            juncVm!.AddCurveCommand.Execute(Unit.Default);
            Assert.Null(project.JunctionTemplates[juncKey!].Curves[0].Segments[0].OutputAnchorId);

            // 2. Same-node duplicate anchor ID is rejected in TimelineToolViewModel
            ws.SelectSpline(s1.Id);
            shell.TimelineTool.AddCurveCommand.Execute(Unit.Default);
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 0);
            shell.TimelineTool.CurveAnchorId.Value = "s";
            Assert.Equal("s", s1.Curves[0].OutputAnchorId);

            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 1);
            shell.TimelineTool.CurveAnchorId.Value = "s";
            Assert.Null(s1.Curves[1].OutputAnchorId);
            Assert.Equal(string.Empty, shell.TimelineTool.CurveAnchorId.Value);

            // 3. Reusing "s" intentionally on a subsequent spline emits `out var s_s` once, then `out s_s`
            ws.SelectSpline(s2.Id);
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 0);
            shell.TimelineTool.CurveAnchorId.Value = "s";
            Assert.Equal("s", s2.Curves[0].OutputAnchorId);

            ws.Save(dirtyOnly: false);
            string worldCsxPath = Path.Combine(project.WorldDirectory, project.Placement.RelativePath);
            string worldCsxText = File.ReadAllText(worldCsxPath);
            Assert.Contains("out var s_s)", worldCsxText);
            Assert.Contains(", out s_s)", worldCsxText);
            Assert.DoesNotContain(", out s_s_end)", worldCsxText);

            (ParsedWorldCsx? parsedWorld, IReadOnlyList<Serialization.Diagnostics.ParseDiagnostic> worldDiags) =
                WorldCsxSerializer.Parse(worldCsxPath, worldCsxText, project.Placement.RelativePath);
            Assert.NotNull(parsedWorld);
            Assert.DoesNotContain(worldDiags, d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
            Assert.Equal("s", parsedWorld!.Document.Splines[0].Curves[0].OutputAnchorId);
            Assert.Equal("s", parsedWorld.Document.Splines[1].Curves[0].OutputAnchorId);

            if (OperatingSystem.IsWindows())
            {
                (bool started, IReadOnlyList<TransportX.Diagnostics.Error> errors, bool hasFatal, _) =
                    RunHeadlessScriptVerification(project, tickCount: 2);
                Assert.True(started);
                Assert.False(hasFatal);
                Assert.DoesNotContain(
                    errors,
                    e => e.Level == TransportX.Diagnostics.ErrorLevel.Error
                         || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal);
            }

            // 4. JunctionTemplateCsxSerializer also emits `out var s_s1` then `out s_s1` across paths/curves
            JunctionTemplateDocument jDoc = project.JunctionTemplates[juncKey!];
            jDoc.Paths.Add(new JunctionPathTemplateModel { Key = "P1", FromPortKey = "S", ToPortKey = "N" });
            jDoc.Paths[^1].Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity, 0.5, "s1"));
            jDoc.Curves[0].Segments[0] = new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, PoseDegreesModel.FromTranslation(0, 0, 20), 0.5, "s1");

            string jCsxText = JunctionTemplateCsxSerializer.Generate(jDoc);
            Assert.Contains("out var s_s1)", jCsxText);
            Assert.Contains(", out s_s1)", jCsxText);
            (JunctionTemplateDocument? parsedJunc, IReadOnlyList<Serialization.Diagnostics.ParseDiagnostic> jDiags) =
                JunctionTemplateCsxSerializer.Parse("TestJunc.csx", jCsxText, jDoc.RelativePath);
            Assert.NotNull(parsedJunc);
            Assert.DoesNotContain(jDiags, d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
            Assert.Equal("s1", parsedJunc!.Paths[^1].Segments[0].OutputAnchorId);
            Assert.Equal("s1", parsedJunc.Curves[0].Segments[0].OutputAnchorId);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Anchors_AllowKeywordsAndReservedNamesViaPrefixedVariables_WithoutCollision()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "PrefixedAnchors", "Prefixed Anchors", "");
            project.Manifest.Dependencies.Clear();

            SplinePlacementNode s1 = Assert.Single(project.Placement.Splines);
            Assert.True(ws.RenameSplineKey(s1.Id, "s_fSpline"));

            ws.SelectSpline(s1.Id);
            shell.TimelineTool.AddCurveCommand.Execute(Unit.Default);
            shell.TimelineTool.AddCurveCommand.Execute(Unit.Default);
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 0);
            shell.TimelineTool.CurveAnchorId.Value = "fSpline";
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 1);
            shell.TimelineTool.CurveAnchorId.Value = "class";
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Curves, 2);
            shell.TimelineTool.CurveAnchorId.Value = "1";

            // Invalid characters (space) are rejected and reverted in TimelineToolViewModel
            shell.TimelineTool.CurveAnchorId.Value = "a b";
            Assert.Equal("1", shell.TimelineTool.CurveAnchorId.Value);
            Assert.Equal("1", s1.Curves[2].OutputAnchorId);

            // Use "fSpline" anchor in an extrusion rule
            shell.TimelineTool.AddExtrusionCommand.Execute(Unit.Default);
            shell.TimelineTool.SelectTrackItem(SplineTimelineTrackKind.Extrusions, 0);
            shell.TimelineTool.FromModeIndex.Value = 1;
            shell.TimelineTool.FromAnchorId.Value = "fSpline";
            shell.TimelineTool.FromOffset.Value = -5m;

            // JunctionTemplate Anchor IDs also permit reserved names and reject invalid spaces
            string layoutKey = project.LaneLayouts.Keys.First();
            Assert.True(ws.CreateNewJunctionTemplate("AnchorJunc", out string? jKey, out _));
            JunctionTemplateDocument jDoc = project.JunctionTemplates[jKey!];
            jDoc.Ports.Clear();
            jDoc.Paths.Clear();
            jDoc.Ports.Add(new JunctionPortDefinition("S", layoutKey, PoseDegreesModel.Identity));
            jDoc.Ports.Add(new JunctionPortDefinition("N", layoutKey, new PoseDegreesModel(0, 0, 20, 0, 0, 0)));
            jDoc.Paths.Add(new JunctionPathTemplateModel { Key = "P1", FromPortKey = "S", ToPortKey = "N" });
            jDoc.Paths[0].Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightToEnd, PoseDegreesModel.Identity));
            jDoc.Paths[0].WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.Constant, 0, 0, AnchoredDistance.Absolute(5.0)));

            // Place a junction at Chunk(1, 0) so s1 has a compatible ConnectBezier target, then verify invalid Anchor ID rejection
            JunctionPlacementNode? jNode = ws.AddJunctionAtChunkPose(jKey!, new WorldPoseDegreesModel(new ChunkIndexModel(1, 0), PoseDegreesModel.FromTranslation(100, 0, 100)));
            Assert.NotNull(jNode);
            ws.SelectSpline(null);
            ws.SelectSpline(s1.Id);
            Assert.NotEmpty(shell.InspectorTool.CompatibleConnectTargets);
            shell.InspectorTool.SelectedConnectTarget.Value = shell.InspectorTool.CompatibleConnectTargets[0];
            shell.InspectorTool.ConnectOutputAnchorId.Value = "c s";
            shell.InspectorTool.ApplyConnectBezierCommand.Execute(Unit.Default);
            Assert.Equal(string.Empty, shell.InspectorTool.ConnectOutputAnchorId.Value);
            Assert.Null(s1.ConnectBezier);

            AssetBrowserEntry jEntry = shell.AssetBrowserTool.JunctionTemplateItems.Single(e => e.Key == jKey);
            using JunctionTemplateEditorDocumentViewModel? jVm = shell.OpenAsset(jEntry) as JunctionTemplateEditorDocumentViewModel;
            Assert.NotNull(jVm);
            jVm!.SelectedPath.Value = jVm.Paths[0];
            jVm.SelectedPathSegment.Value = jVm.PathSegments[0];
            jVm.SelectedPathWidth.Value = jVm.PathWidths[0];
            jVm.PathSegAnchorId.Value = "tJunction";
            Assert.Equal("tJunction", jDoc.Paths[0].Segments[0].OutputAnchorId);
            jVm.WidthAnchorId.Value = "class";
            Assert.Equal("class", jDoc.Paths[0].WidthSegments[0].OutputAnchorId);
            jVm.WidthAnchorId.Value = "bad id";
            Assert.Equal("class", jVm.WidthAnchorId.Value);

            ws.Save(dirtyOnly: false);
            string worldCsxPath = Path.Combine(project.WorldDirectory, project.Placement.RelativePath);
            string worldCsxText = File.ReadAllText(worldCsxPath);
            Assert.Contains("out var s_fSpline", worldCsxText);
            Assert.Contains("out var s_class", worldCsxText);
            Assert.Contains("out var s_1", worldCsxText);
            Assert.Contains("s_fSpline - 5", worldCsxText);
            Assert.Contains("fSpline.Key = \"s_fSpline\";", worldCsxText);
            Assert.Contains("var s_fSpline_2 = fSpline.Build();", worldCsxText);

            string jCsxText = File.ReadAllText(Path.Combine(project.WorldDirectory, jDoc.RelativePath));
            Assert.Contains("out var s_tJunction", jCsxText);
            Assert.Contains("out var s_class", jCsxText);

            (ParsedWorldCsx? parsedWorld, IReadOnlyList<Serialization.Diagnostics.ParseDiagnostic> worldDiags) =
                WorldCsxSerializer.Parse(worldCsxPath, worldCsxText, project.Placement.RelativePath);
            Assert.NotNull(parsedWorld);
            Assert.Empty(worldDiags);
            SplinePlacementNode reparsedSpline = Assert.Single(parsedWorld!.Document.Splines);
            Assert.Equal("s_fSpline", reparsedSpline.Key);
            Assert.Equal("fSpline", reparsedSpline.Curves[0].OutputAnchorId);
            Assert.Equal("class", reparsedSpline.Curves[1].OutputAnchorId);
            Assert.Equal("1", reparsedSpline.Curves[2].OutputAnchorId);
            Assert.Equal("fSpline", reparsedSpline.Extrusions[0].From.AnchorId);
            Assert.Equal(-5.0, reparsedSpline.Extrusions[0].From.Offset, 6);

            if (OperatingSystem.IsWindows())
            {
                (bool started, IReadOnlyList<TransportX.Diagnostics.Error> errors, bool hasFatal, _) =
                    RunHeadlessScriptVerification(project, tickCount: 2);
                Assert.True(started);
                Assert.False(hasFatal);
                Assert.DoesNotContain(
                    errors,
                    e => e.Level == TransportX.Diagnostics.ErrorLevel.Error
                         || e.Level == TransportX.Diagnostics.ErrorLevel.Fatal);
            }
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task BackgroundScriptCompiler_DetectsSyntaxErrorInUnsavedUserCode_WithoutExecutingScript()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;
            ws.UiThreadDispatcher = static a => a();

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "BgCompileWorld", "Bg Compile", "Desc");
            project.Manifest.Dependencies.Clear();

            string sideEffectSentinelPath = Path.Combine(tempDir, "should_never_execute.txt");
            project.Placement.UserCode = $"System.IO.File.WriteAllText(@\"{sideEffectSentinelPath}\", \"ran\");\nint brokenSyntax = ;";
            project.MarkDirty(WorldFileKind.WorldCsx, project.Placement.RelativePath);

            IReadOnlyList<TransportX.Diagnostics.Error> errors = await ws.RunBackgroundScriptCompileNowAsync();
            Assert.False(File.Exists(sideEffectSentinelPath));
            Assert.NotEmpty(errors);
            Assert.Contains(errors, static e => e.Level == TransportX.Diagnostics.ErrorLevel.Error);
            Assert.Contains(shell.DiagnosticsTool.Items, static item => item.Source.StartsWith("[スクリプト]", StringComparison.Ordinal));

            // Fixing the syntax error clears background script diagnostics while STILL never executing user code
            project.Placement.UserCode = $"System.IO.File.WriteAllText(@\"{sideEffectSentinelPath}\", \"ran\");\nint validValue = 42;\n_ = validValue;";
            IReadOnlyList<TransportX.Diagnostics.Error> cleanErrors = await ws.RunBackgroundScriptCompileNowAsync();
            Assert.False(File.Exists(sideEffectSentinelPath));
            Assert.DoesNotContain(cleanErrors, static e => e.Level == TransportX.Diagnostics.ErrorLevel.Error);
            Assert.DoesNotContain(shell.DiagnosticsTool.Items, static item => item.Source.StartsWith("[スクリプト]", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AsyncVerificationStarting_DisablesConflictingCommands_AndSupportsCancel()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            shell.CreateAndActivateWorld(tempDir, "Author", "AsyncVerifyWorld", "Async Verify", "Desc");
            ws.AddPropPlacement(
                "BusStop",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(10.0, 0.0, 10.0)));
            Assert.True(shell.CanUndo.Value);
            Assert.True(shell.CanRunWorkspaceActions.Value);
            Assert.True(shell.CanStartNewOrOpen.Value);

            bool startRequested = false;
            bool cancelRequested = false;
            ws.StartVerificationRequested += _ => startRequested = true;
            ws.CancelVerificationStartRequested += () =>
            {
                cancelRequested = true;
                ws.IsVerificationStarting.Value = false;
                ws.ActiveRenderMode.Value = EditorRenderMode.EditLivePreview;
            };

            Assert.True(shell.RunScriptVerification());
            Assert.True(startRequested);
            Assert.True(shell.IsVerificationStarting.Value);
            Assert.True(shell.ViewportDocument.IsVerificationStarting.Value);
            Assert.False(shell.CanRunWorkspaceActions.Value);
            Assert.False(shell.CanStartNewOrOpen.Value);
            Assert.False(shell.CanUndo.Value);
            Assert.False(shell.CanRedo.Value);

            shell.ViewportDocument.CancelVerificationStartCommand.Execute(Unit.Default);
            Assert.True(cancelRequested);
            Assert.False(shell.IsVerificationStarting.Value);
            Assert.True(shell.CanRunWorkspaceActions.Value);
            Assert.True(shell.CanStartNewOrOpen.Value);
            Assert.True(shell.CanUndo.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ViewportAndSceneHierarchy_SlimToolbarContextCombo_TransformGuard_AndCreationMenu()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "ToolbarWorld", "Toolbar World", "Desc");
            project.ModelLists["Models.txt"].Entries.Add(new ModelListEntry
            {
                Key = "BusStopSign",
                ModelPath = "Props/BusStopSign.x",
            });
            Assert.True(ws.CreateNewJunctionTemplate("TestJunction", out string? juncKey, out _));
            ws.ProjectRevision.Value++;

            // 1. SceneHierarchy creation commands enter interactive placement modes (except Background which is immediate)
            ViewportDocumentViewModel vp = shell.ViewportDocument;
            int activateCount = 0;
            ws.ActivateViewportRequested += () => activateCount++;

            shell.SceneHierarchyTool.AddNewPropCommand.Execute(Unit.Default);
            Assert.True(vp.IsPlacePropToolActive.Value);
            Assert.True(vp.IsAnyPlacementModeActive.Value);
            Assert.Equal("プロップ配置モード", vp.ActivePlacementTitle.Value);
            Assert.Equal(1, activateCount);

            shell.SceneHierarchyTool.AddNewSplineCommand.Execute(Unit.Default);
            Assert.True(vp.IsPlaceSplineToolActive.Value);
            Assert.True(vp.IsAnyPlacementModeActive.Value);
            Assert.Equal("スプライン配置モード", vp.ActivePlacementTitle.Value);

            shell.SceneHierarchyTool.AddNewJunctionCommand.Execute(Unit.Default);
            Assert.True(vp.IsPlaceJunctionToolActive.Value);
            Assert.True(vp.IsAnyPlacementModeActive.Value);
            Assert.Equal("ジャンクション配置モード", vp.ActivePlacementTitle.Value);

            vp.SelectToolCommand.Execute(Unit.Default);
            Assert.True(vp.IsSelectToolActive.Value);
            Assert.False(vp.IsAnyPlacementModeActive.Value);

            shell.SceneHierarchyTool.AddNewBackgroundCommand.Execute(Unit.Default);
            Assert.Single(project.Placement.BackgroundModels);

            // 2. Direct placement operations create scene elements cleanly for transform guard testing
            int initialSplines = project.Placement.Splines.Count;
            ChunkPropPlacement? placedProp = ws.AddPropPlacement(
                "BusStopSign",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(20.0, 0.0, 20.0)));
            Assert.NotNull(placedProp);
            string propId = placedProp!.Id;

            SplinePlacementNode? standaloneSpline = ws.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(40.0, 0.0, 40.0)));
            Assert.NotNull(standaloneSpline);
            Assert.Equal(initialSplines + 1, project.Placement.Splines.Count);
            SplinePlacementNode? chainedSpline = ws.AddChainedSplineFromOutlet(standaloneSpline!.Id, "Spline1");
            Assert.NotNull(chainedSpline);

            JunctionPlacementNode? standaloneJunction = ws.AddJunctionAtChunkPose(
                juncKey!,
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(80.0, 0.0, 80.0)));
            Assert.NotNull(standaloneJunction);

            // 3. Transform guard permits only Prop / ChunkPose Spline / ChunkPose Junction
            ws.SelectProp(null);
            ws.SelectSpline(null);
            ws.SelectJunction(null);
            ws.SelectBackgroundModel(-1);
            Assert.False(vp.CanTransformSelected.Value);
            vp.TranslateToolCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);

            ws.SelectProp(propId);
            Assert.True(vp.CanTransformSelected.Value);
            vp.TranslateToolCommand.Execute(Unit.Default);
            Assert.True(vp.IsTranslateToolActive.Value);
            Assert.Equal(EditorToolMode.Translate, ws.ActiveToolMode.Value);

            // Selecting a chained (port-bound) spline auto-resets Translate -> Select & disables buttons with reason
            ws.SelectSpline(chainedSpline!.Id);
            Assert.False(vp.CanTransformSelected.Value);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);
            Assert.True(vp.IsSelectToolActive.Value);
            Assert.Contains("連結済み", vp.TransformDisabledReason.Value);
            vp.RotateToolCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);

            // Selecting a standalone ChunkPose spline re-enables Translate / Rotate
            ws.SelectSpline(standaloneSpline.Id);
            Assert.True(vp.CanTransformSelected.Value);
            vp.RotateToolCommand.Execute(Unit.Default);
            Assert.True(vp.IsRotateToolActive.Value);

            // Selecting a background model disables transform and resets back to Select
            ws.SelectBackgroundModel(0);
            Assert.False(vp.CanTransformSelected.Value);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);

            // Selecting a standalone ChunkPose junction enables transform again
            ws.SelectJunction(standaloneJunction.Id);
            Assert.True(vp.CanTransformSelected.Value);

            if (OperatingSystem.IsWindows())
            {
                Exception? capturedUiException = null;
                AvaloniaUiTestHost.Run(() =>
                {
                    try
                    {
                        ViewportDocumentView vpView = new();
                        MeasureAndArrange(vpView, vp, 1024, 600);

                        SceneHierarchyView hierarchyView = new();
                        MeasureAndArrange(hierarchyView, shell.SceneHierarchyTool, 300, 500);

                        InspectorView inspectorView = new();
                        MeasureAndArrange(inspectorView, shell.InspectorTool, 360, 500);
                    }
                    catch (Exception ex)
                    {
                        capturedUiException = ex;
                    }
                });

                Assert.Null(capturedUiException);
            }
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExternalFileWatcher_ReloadsCleanModelsTxtAndJunctionCsx_AndProtectsDirtyFiles()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using WorldWorkspaceService ws = new();
            ws.EnableBackgroundScriptCompilation = false;
            ws.UiThreadDispatcher = static a => a();
            ws.FileWatcherService.DebounceDelay = TimeSpan.FromMilliseconds(30);
            ws.FileWatcherSaveSuppressionDuration = TimeSpan.FromMilliseconds(100);

            WorldProject project = ws.CreateNewWorld(tempDir, "Author", "LiveReloadWorld", "Live Reload", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("WatchedJunc", out string? juncKey, out string? juncRelPath));
            ws.Save(dirtyOnly: false);

            // Wait briefly so OS FileSystemWatcher flush & suppression settle before simulating external editor save
            await Task.Delay(250);

            string modelsFullPath = Path.GetFullPath(Path.Combine(project.WorldDirectory, "Models.txt"));
            int revBeforeModels = ws.ProjectRevision.Value;
            await File.WriteAllTextAsync(modelsFullPath, "ExternalSign\tProps/ExternalSign.x\tNONE\n");

            bool modelsReloaded = await WaitUntilAsync(
                () => revBeforeModels < ws.ProjectRevision.Value
                    && project.ModelLists["Models.txt"].Entries.Any(static e => string.Equals(e.Key, "ExternalSign", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(4));
            Assert.True(modelsReloaded);
            Assert.Contains(project.ModelLists["Models.txt"].Entries, static e => e.Key == "ExternalSign");

            // External edit to Junction template .csx adds a new port and reloads live
            string juncFullPath = Path.GetFullPath(Path.Combine(project.WorldDirectory, juncRelPath!));
            string juncText = File.ReadAllText(juncFullPath);
            Assert.DoesNotContain("\"E\"", juncText);

            JunctionTemplateDocument updatedTemplateDraft = JunctionAnchorUtilities.DeepCloneJunctionTemplate(project.JunctionTemplates[juncKey!]);
            updatedTemplateDraft.Ports.Add(new JunctionPortDefinition("E", "Layout1", new PoseDegreesModel(20, 0, 0, 0, 90, 0)));
            string modifiedJuncText = JunctionTemplateCsxSerializer.Generate(updatedTemplateDraft);

            int revBeforeJunc = ws.ProjectRevision.Value;
            await File.WriteAllTextAsync(juncFullPath, modifiedJuncText);

            bool juncReloaded = await WaitUntilAsync(
                () => revBeforeJunc < ws.ProjectRevision.Value
                    && project.JunctionTemplates[juncKey!].Ports.Any(static p => string.Equals(p.Key, "E", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(4));
            Assert.True(juncReloaded);
            Assert.Contains(project.JunctionTemplates[juncKey!].Ports, static p => p.Key == "E");

            // Unsaved (dirty) file must NOT be overwritten when externally modified
            project.ModelLists["Models.txt"].Entries.Add(new ModelListEntry
            {
                Key = "UnsavedLocalEdit",
                ModelPath = "Props/UnsavedLocalEdit.x",
            });
            project.MarkDirty(WorldFileKind.ModelsTxt, "Models.txt");

            await File.WriteAllTextAsync(modelsFullPath, "ExternalOverwriteAttempt\tProps/Attempt.x\tNONE\n");
            ExternalFileReloadResult reloadDirtyResult = ws.ReloadExternalFile(modelsFullPath);
            Assert.True(reloadDirtyResult.Handled);
            Assert.True(reloadDirtyResult.SkippedDirty);
            Assert.False(reloadDirtyResult.Reloaded);
            Assert.Contains(project.ModelLists["Models.txt"].Entries, static e => e.Key == "UnsavedLocalEdit");
            Assert.DoesNotContain(project.ModelLists["Models.txt"].Entries, static e => e.Key == "ExternalOverwriteAttempt");
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void JunctionPlanCanvas_HighlightsPathOnlyOnPathTab_AndCurveOnlyOnCurveTab()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "JunctionTabWorld", "Junction Tab", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("PlanTabJunc", out string? juncKey, out _));
            AssetBrowserEntry? juncEntry = shell.AssetBrowserTool.JunctionTemplateItems.FirstOrDefault(e => e.Key == juncKey);
            Assert.NotNull(juncEntry);

            using JunctionTemplateEditorDocumentViewModel? vm =
                shell.OpenAsset(juncEntry!) as JunctionTemplateEditorDocumentViewModel;
            Assert.NotNull(vm);

            vm!.AddPathCommand.Execute(Unit.Default);
            vm.AddCurveCommand.Execute(Unit.Default);
            Assert.NotNull(vm.SelectedPath.Value);
            Assert.NotNull(vm.SelectedCurve.Value);
            int selectedPathIdx = vm.SelectedPath.Value!.Index;
            int selectedCurveIdx = vm.SelectedCurve.Value!.Index;

            // Tab 0 (Ports), Tab 1 (Relays): neither Path nor Curve is highlighted
            vm.SelectedEditorTabIndex.Value = 0;
            Assert.False(JunctionPlanCanvas.ShouldHighlightPath(vm, selectedPathIdx));
            Assert.False(JunctionPlanCanvas.ShouldHighlightCurve(vm, selectedCurveIdx));

            vm.SelectedEditorTabIndex.Value = 1;
            Assert.False(JunctionPlanCanvas.ShouldHighlightPath(vm, selectedPathIdx));
            Assert.False(JunctionPlanCanvas.ShouldHighlightCurve(vm, selectedCurveIdx));

            // Tab 2 (Paths): only Path is highlighted
            vm.SelectedEditorTabIndex.Value = JunctionTemplateEditorDocumentViewModel.PathEditorTabIndex;
            Assert.True(JunctionPlanCanvas.ShouldHighlightPath(vm, selectedPathIdx));
            Assert.False(JunctionPlanCanvas.ShouldHighlightCurve(vm, selectedCurveIdx));

            // Tab 3 (Auxiliary Curves): only Curve is highlighted
            vm.SelectedEditorTabIndex.Value = JunctionTemplateEditorDocumentViewModel.CurveEditorTabIndex;
            Assert.False(JunctionPlanCanvas.ShouldHighlightPath(vm, selectedPathIdx));
            Assert.True(JunctionPlanCanvas.ShouldHighlightCurve(vm, selectedCurveIdx));

            // Tab 4 (Static Props), Tab 5 (Custom Code): neither is highlighted
            vm.SelectedEditorTabIndex.Value = 4;
            Assert.False(JunctionPlanCanvas.ShouldHighlightPath(vm, selectedPathIdx));
            Assert.False(JunctionPlanCanvas.ShouldHighlightCurve(vm, selectedCurveIdx));

            if (OperatingSystem.IsWindows())
            {
                Exception? capturedUiException = null;
                AvaloniaUiTestHost.Run(() =>
                {
                    try
                    {
                        JunctionTemplateEditorView view = new();
                        MeasureAndArrange(view, vm, 1100, 700);
                    }
                    catch (Exception ex)
                    {
                        capturedUiException = ex;
                    }
                });

                Assert.Null(capturedUiException);
            }
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void JunctionTemplatePreviewRenderer_OffscreenD3D11_RendersExtrusionsToRgbaAndSupports2D3DToggle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Junction3DWorld", "Junction 3D", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("Preview3DJunc", out string? juncKey, out _));
            JunctionTemplateDocument tpl = project.JunctionTemplates[juncKey!];
            if (0 < tpl.Paths.Count && 0 == tpl.Paths[0].Extrusions.Count)
            {
                tpl.Paths[0].Extrusions.Add(new CurveExtrusionPlacementRule
                {
                    Key = "RoadSurface",
                    CrossSectionKey = "Road1L",
                    From = AnchoredDistance.Absolute(0.0),
                });
            }

            using JunctionTemplatePreviewRenderer previewRenderer = new(320, 240);
            previewRenderer.SyncTemplate(project, juncKey!);
            previewRenderer.ResetOrbit();

            byte[] buffer = new byte[320 * 240 * 4];
            Assert.True(previewRenderer.RenderToRgbaBuffer(320, 240, buffer, 320 * 4));

            bool hasVisiblePixels = false;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
                {
                    hasVisiblePixels = true;
                    break;
                }
            }

            Assert.True(hasVisiblePixels);

            float beforeDistance = previewRenderer.CurrentOrbitDistance;
            previewRenderer.OrbitBy(25f, -5f);
            previewRenderer.ZoomBy(1f);
            Assert.True(previewRenderer.CurrentOrbitDistance < beforeDistance);
            Assert.True(previewRenderer.RenderToRgbaBuffer(320, 240, buffer, 320 * 4));

            AssetBrowserEntry? juncEntry = shell.AssetBrowserTool.JunctionTemplateItems.FirstOrDefault(e => e.Key == juncKey);
            Assert.NotNull(juncEntry);
            using JunctionTemplateEditorDocumentViewModel? vm =
                shell.OpenAsset(juncEntry!) as JunctionTemplateEditorDocumentViewModel;
            Assert.NotNull(vm);
            Assert.False(vm!.Is3DPreviewMode.Value);

            vm.SwitchTo3DPreviewCommand.Execute(Unit.Default);
            Assert.True(vm.Is3DPreviewMode.Value);

            Exception? capturedUiException = null;
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    JunctionTemplateEditorView view = new();
                    MeasureAndArrange(view, vm, 1100, 700);
                    vm.SwitchTo2DPreviewCommand.Execute(Unit.Default);
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                    Assert.False(vm.Is3DPreviewMode.Value);
                }
                catch (Exception ex)
                {
                    capturedUiException = ex;
                }
            });

            Assert.Null(capturedUiException);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void JunctionInspector_ExplicitPortConnection_UpdatesStatus_BlocksDuplicate_AndDisconnectsSelectedPort()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "ExplicitPortWorld", "Explicit Port", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("ConnJunc", out string? juncKey, out _));

            JunctionPlacementNode? j1 = ws.AddJunctionAtChunkPose(
                juncKey!,
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(80.0, 0.0, 125.0)));
            JunctionPlacementNode? j2 = ws.AddJunctionAtChunkPose(
                juncKey!,
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(160.0, 0.0, 125.0)));
            Assert.NotNull(j1);
            Assert.NotNull(j2);

            ws.SelectJunction(j1!.Id);
            InspectorToolViewModel inspector = shell.InspectorTool;
            inspector.SelectedJunctionPortKey.Value = "N";

            Assert.True(inspector.HasCompatibleExplicitConnectTargets.Value);
            Assert.False(inspector.HasActiveExplicitConnectionOnSelectedPort.Value);
            Assert.Contains("未接続", inspector.SelectedJunctionPortStatusText.Value);

            // Connect J1.N to a compatible J2 port
            CompatiblePortCandidate? j2Candidate = inspector.CompatibleExplicitConnectTargets
                .FirstOrDefault(c => string.Equals(c.PortRef.ElementId, j2!.Id, StringComparison.Ordinal));
            Assert.NotNull(j2Candidate);
            inspector.SelectedExplicitConnectTarget.Value = j2Candidate;
            inspector.AddExplicitPortConnectionCommand.Execute(Unit.Default);

            Assert.Single(project.Placement.Connections);
            Assert.True(inspector.HasActiveExplicitConnectionOnSelectedPort.Value);
            Assert.Contains("直接ポート接続済み", inspector.SelectedJunctionPortStatusText.Value);
            Assert.False(inspector.HasCompatibleExplicitConnectTargets.Value);
            Assert.False(inspector.IsSelectedJunctionPortFree.Value);
            Assert.True(inspector.IsJunctionExplicitConnectMode.Value);

            // Duplicate connect attempt must be blocked while occupied
            IReadOnlyList<CompatiblePortCandidate> occupiedTargets = JunctionAnchorUtilities.FindCompatibleExplicitConnectTargets(
                project,
                new PortReferenceModel(j1.Id, PortTargetKind.JunctionPort, "N"));
            Assert.Empty(occupiedTargets);

            // Disconnect directly from the Junction Inspector port section
            inspector.DisconnectSelectedJunctionPortCommand.Execute(Unit.Default);
            Assert.Empty(project.Placement.Connections);
            Assert.False(inspector.HasActiveExplicitConnectionOnSelectedPort.Value);
            Assert.True(inspector.HasCompatibleExplicitConnectTargets.Value);
            Assert.Contains("未接続", inspector.SelectedJunctionPortStatusText.Value);

            // Undo / Redo restores and disconnects cleanly
            Assert.True(ws.Undo());
            Assert.Single(project.Placement.Connections);
            Assert.True(inspector.HasActiveExplicitConnectionOnSelectedPort.Value);

            Assert.True(ws.Redo());
            Assert.Empty(project.Placement.Connections);
            Assert.False(inspector.HasActiveExplicitConnectionOnSelectedPort.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void JunctionTemplate_MoveUpDown_ReordersPathAndCurveExtrusionsAndProps()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "ReorderJuncWorld", "Reorder Junc", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("ReorderJunc", out string? juncKey, out _));

            AssetBrowserEntry? juncEntry = shell.AssetBrowserTool.JunctionTemplateItems.FirstOrDefault(e => e.Key == juncKey);
            Assert.NotNull(juncEntry);
            using JunctionTemplateEditorDocumentViewModel? vm =
                shell.OpenAsset(juncEntry!) as JunctionTemplateEditorDocumentViewModel;
            Assert.NotNull(vm);

            vm!.SelectedPath.Value = vm.Paths[0];
            JunctionTemplateDocument doc = project.JunctionTemplates[juncKey!];
            vm.AddPathExtrusionCommand.Execute(Unit.Default);
            vm.PathExtKey.Value = "ExtA";
            vm.AddPathExtrusionCommand.Execute(Unit.Default);
            vm.PathExtKey.Value = "ExtB";
            Assert.Equal(2, vm.PathExtrusions.Count);
            Assert.Equal("ExtB", doc.Paths[0].Extrusions[1].Key);

            vm.SelectedPathExtrusion.Value = vm.PathExtrusions[1];
            vm.MovePathExtrusionUpCommand.Execute(Unit.Default);
            Assert.Equal("ExtB", doc.Paths[0].Extrusions[0].Key);
            Assert.Equal("ExtA", doc.Paths[0].Extrusions[1].Key);

            vm.MovePathExtrusionDownCommand.Execute(Unit.Default);
            Assert.Equal("ExtA", doc.Paths[0].Extrusions[0].Key);
            Assert.Equal("ExtB", doc.Paths[0].Extrusions[1].Key);

            vm.AddPathPropCommand.Execute(Unit.Default);
            vm.PathPropModelKeysText.Value = "PropFirst";
            vm.AddPathPropCommand.Execute(Unit.Default);
            vm.PathPropModelKeysText.Value = "PropSecond";
            vm.SelectedPathProp.Value = vm.PathProps[1];
            vm.MovePathPropUpCommand.Execute(Unit.Default);
            Assert.Equal("PropSecond", doc.Paths[0].Props[0].ModelKeys[0]);
            Assert.Equal("PropFirst", doc.Paths[0].Props[1].ModelKeys[0]);

            vm.AddCurveCommand.Execute(Unit.Default);
            vm.SelectedCurve.Value = vm.Curves[0];
            vm.AddCurveExtrusionCommand.Execute(Unit.Default);
            vm.CurveExtKey.Value = "CurveExt1";
            vm.AddCurveExtrusionCommand.Execute(Unit.Default);
            vm.CurveExtKey.Value = "CurveExt2";
            vm.SelectedCurveExtrusion.Value = vm.CurveExtrusions[1];
            vm.MoveCurveExtrusionUpCommand.Execute(Unit.Default);
            Assert.Equal("CurveExt2", doc.Curves[0].Extrusions[0].Key);

            vm.AddCurvePropCommand.Execute(Unit.Default);
            vm.CurvePropModelKeysText.Value = "CurveProp1";
            vm.AddCurvePropCommand.Execute(Unit.Default);
            vm.CurvePropModelKeysText.Value = "CurveProp2";
            vm.SelectedCurveProp.Value = vm.CurveProps[1];
            vm.MoveCurvePropUpCommand.Execute(Unit.Default);
            Assert.Equal("CurveProp2", doc.Curves[0].Props[0].ModelKeys[0]);

            Assert.True(ws.Undo());
            Assert.Equal("CurveProp1", doc.Curves[0].Props[0].ModelKeys[0]);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SplineTemplatePreviewRenderer_OffscreenD3D11_RendersSampleExtrusionsAndSupportsCurveToggle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "Spline3DWorld", "Spline 3D", "Desc");
            project.Placement.BackgroundModels.Add("Sky");
            Assert.True(ws.AddNewSplineTemplate("PreviewSplineTpl", "Templates/Splines/PreviewSplineTpl.csx"));

            using SplineTemplatePreviewRenderer previewRenderer = new(320, 240);
            previewRenderer.SyncTemplate(project, "PreviewSplineTpl", useCurvedSample: false);
            previewRenderer.ResetOrbit();

            Assert.Empty(previewRenderer.World.BackgroundModels);
            Assert.InRange(previewRenderer.CurrentOrbitTarget.X, 120.0f, 130.0f);
            Assert.InRange(previewRenderer.CurrentOrbitTarget.Z, 120.0f, 130.0f);
            Assert.InRange(previewRenderer.CurrentOrbitDistance, 12.0f, 120.0f);

            byte[] buffer = new byte[320 * 240 * 4];
            Assert.True(previewRenderer.RenderToRgbaBuffer(320, 240, buffer, 320 * 4));
            bool hasVisibleStraightPixels = buffer.Any(static b => b != 0 && b != 255);
            Assert.True(hasVisibleStraightPixels);

            previewRenderer.SyncTemplate(project, "PreviewSplineTpl", useCurvedSample: true);
            previewRenderer.ResetOrbit();
            Assert.InRange(previewRenderer.CurrentOrbitDistance, 12.0f, 120.0f);
            Assert.True(previewRenderer.RenderToRgbaBuffer(320, 240, buffer, 320 * 4, elapsedSeconds: 0.08));

            AssetBrowserEntry? splineEntry = shell.AssetBrowserTool.SplineTemplateItems.FirstOrDefault(e => e.Key == "PreviewSplineTpl");
            Assert.NotNull(splineEntry);
            using SplineTemplateEditorDocumentViewModel? vm =
                shell.OpenAsset(splineEntry!) as SplineTemplateEditorDocumentViewModel;
            Assert.NotNull(vm);
            Assert.False(vm!.UseCurvedPreview.Value);

            vm.SetCurvedPreviewCommand.Execute(Unit.Default);
            Assert.True(vm.UseCurvedPreview.Value);
            vm.SetStraightPreviewCommand.Execute(Unit.Default);
            Assert.False(vm.UseCurvedPreview.Value);

            Exception? capturedUiException = null;
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    SplineTemplateEditorView view = new();
                    MeasureAndArrange(view, vm, 1100, 700);
                }
                catch (Exception ex)
                {
                    capturedUiException = ex;
                }
            });

            Assert.Null(capturedUiException);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Inspector_3DPortPickAnd3ChoiceJunctionMode_PrebindsSourceAndSwitchesModes()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "InspectorConnectWorld", "Connect", "Desc");
            Assert.True(ws.CreateNewJunctionTemplate("PickJunc", out string? juncKey, out _));

            SplinePlacementNode? sp = ws.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(20.0, 0.0, 20.0)));
            JunctionPlacementNode? jn = ws.AddJunctionAtChunkPose(
                juncKey!,
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(90.0, 0.0, 90.0)));
            Assert.NotNull(sp);
            Assert.NotNull(jn);

            InspectorToolViewModel inspector = shell.InspectorTool;

            // 1. Spline Inspector 1-click 3D connect toggle pre-binds SplineOutlet
            ws.SelectSpline(sp!.Id);
            inspector.ToggleSplineConnectPickIn3DCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.ConnectPort, ws.ActiveToolMode.Value);
            Assert.True(inspector.IsPickingSplineConnectTargetIn3D.Value);
            Assert.Equal(new PortReferenceModel(sp.Id, PortTargetKind.SplineOutlet, null), ws.PendingConnectSourcePort.Value);

            // Second toggle cancels cleanly
            inspector.ToggleSplineConnectPickIn3DCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);
            Assert.False(inspector.IsPickingSplineConnectTargetIn3D.Value);
            Assert.Null(ws.PendingConnectSourcePort.Value);

            // 2. Junction 3-choice mode & 1-click 3D pick on free port
            ws.SelectJunction(jn!.Id);
            inspector.SelectedJunctionPortKey.Value = "N";
            Assert.True(inspector.IsJunctionExtendSplineMode.Value);

            inspector.SelectJunctionChainJunctionModeCommand.Execute(Unit.Default);
            Assert.True(inspector.IsJunctionChainJunctionMode.Value);
            Assert.False(inspector.IsJunctionExtendSplineMode.Value);

            inspector.SelectJunctionExplicitConnectModeCommand.Execute(Unit.Default);
            Assert.True(inspector.IsJunctionExplicitConnectMode.Value);
            Assert.True(inspector.IsSelectedJunctionPortFree.Value);

            inspector.ToggleJunctionConnectPickIn3DCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.ConnectPort, ws.ActiveToolMode.Value);
            Assert.True(inspector.IsPickingJunctionConnectTargetIn3D.Value);
            Assert.Equal(new PortReferenceModel(jn.Id, PortTargetKind.JunctionPort, "N"), ws.PendingConnectSourcePort.Value);

            // Occupied port cannot initiate 3D pick
            ws.AddChainedSplineFromJunctionPort(jn.Id, "S", "Spline1");
            inspector.SelectedJunctionPortKey.Value = "S";
            Assert.False(inspector.IsSelectedJunctionPortFree.Value);
            ws.ActiveToolMode.Value = EditorToolMode.Select;
            inspector.ToggleJunctionConnectPickIn3DCommand.Execute(Unit.Default);
            Assert.Equal(EditorToolMode.Select, ws.ActiveToolMode.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task MainWindowViewModel_ExitWithUnsavedChanges_PromptsAndRespectsCancelSaveDiscard()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            // 1. Clean world requires no prompt
            shell.CreateAndActivateWorld(tempDir, "Author", "ExitPromptWorld", "Exit Prompt", "Desc");
            shell.Save(dirtyOnly: true);
            Assert.False(shell.ShouldConfirmExit());
            int promptCalls = 0;
            bool cleanClose = await shell.ResolveExitAsync(_ =>
            {
                promptCalls++;
                return Task.FromResult(UnsavedChangesExitChoice.Cancel);
            });
            Assert.True(cleanClose);
            Assert.Equal(0, promptCalls);

            // 2. Dirty world prompts and Cancel aborts exit leaving dirty files intact
            ws.AddPropPlacement(
                "BusStop",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(15.0, 0.0, 15.0)));
            Assert.True(shell.ShouldConfirmExit());
            IReadOnlyList<string> reportedDirty = Array.Empty<string>();
            bool cancelClose = await shell.ResolveExitAsync(files =>
            {
                promptCalls++;
                reportedDirty = files;
                return Task.FromResult(UnsavedChangesExitChoice.Cancel);
            });
            Assert.False(cancelClose);
            Assert.Equal(1, promptCalls);
            Assert.NotEmpty(reportedDirty);
            Assert.True(shell.ShouldConfirmExit());

            // 3. SaveAndExit saves all dirty files and permits closing
            bool saveAndClose = await shell.ResolveExitAsync(_ => Task.FromResult(UnsavedChangesExitChoice.SaveAndExit));
            Assert.True(saveAndClose);
            Assert.False(shell.ShouldConfirmExit());

            // 4. DiscardAndExit permits closing even when dirty files remain
            ws.AddPropPlacement(
                "BusStop2",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(25.0, 0.0, 25.0)));
            Assert.True(shell.ShouldConfirmExit());
            bool discardClose = await shell.ResolveExitAsync(_ => Task.FromResult(UnsavedChangesExitChoice.DiscardAndExit));
            Assert.True(discardClose);
            Assert.True(shell.ShouldConfirmExit());
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void EditorWorld_SplineInstanceExtrusion_RebuildsMeshWhenOffsetOrVScaleChangesWithoutRuleKeyChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "SplineExtSigWorld", "Spline Ext Sig", "Desc");
            SplinePlacementNode? spline = project.Placement.Splines.FirstOrDefault();
            Assert.NotNull(spline);

            spline!.Extrusions.Add(new CurveExtrusionPlacementRule
            {
                Key = "CustomExtKey",
                CrossSectionKey = project.CrossSections.Keys.First(),
                OffsetX = 0.0,
                OffsetY = 0.0,
                VIntervalScale = 1.0,
                VOffset = 0.0,
                From = AnchoredDistance.Absolute(0.0),
            });

            using GraphicsHost graphicsHost = new();
            using DummyGraphicsClient graphicsClient = new();
            using PhysicsHost physicsHost = PhysicsHost.Create();
            using ViewportWindowAdapter windowAdapter = new(0, 800, 600);
            using DummyAudioHost audioHost = new();
            using DummyInputHost inputHost = new();
            DummyAudioClient audioClient = new();
            DummyInputClient inputClient = new();
            FreeCameraController cam = new();
            EditorErrorCollector errorCollector = new();
            PluginLoadContext appContext = new(typeof(EditorWorld).Assembly.Location);

            TransportX.Platform platform = new()
            {
                Window = windowAdapter,
                Input = inputHost.SilkContext,
            };

            WorldBuilder builder = new(new PreviewTestWorldInfo
            {
                InfoPath = Path.Combine(tempDir, "PreviewTest.xml"),
                Path = typeof(EditorWorld).Assembly.Location,
            })
            {
                Platform = platform,
                GraphicsHost = graphicsHost,
                GraphicsClient = graphicsClient,
                AudioHost = audioHost,
                AudioClient = audioClient,
                InputHost = inputHost,
                InputClient = inputClient,
                PhysicsHost = physicsHost,
                Options = new WorldOptions
                {
                    SimulationChunkCount = 2,
                    IsDebugMode = false,
                },
                ErrorCollector = errorCollector,
                AppContext = appContext,
                TimeManager = new TimeManager(),
                Camera = cam.Camera,
            };

            using EditorWorld world = new(builder);
            world.RebuildFromProject(project);
            Assert.Empty(errorCollector.Errors);
            Assert.NotEmpty(world.Splines);
            SplineRenderEntry initialEntry = world.Splines.First();

            // Change OffsetX WITHOUT modifying Rule Key -> network signature must change and rebuild network
            spline.Extrusions[0].OffsetX = 4.5;
            world.SyncFromProjectIncremental(project);
            Assert.Empty(errorCollector.Errors);
            Assert.NotEmpty(world.Splines);
            SplineRenderEntry afterOffsetEntry = world.Splines.First();
            Assert.False(ReferenceEquals(initialEntry, afterOffsetEntry));

            // Change VIntervalScale & VOffset WITHOUT modifying Rule Key -> must rebuild again
            spline.Extrusions[0].VIntervalScale = 2.5;
            spline.Extrusions[0].VOffset = 0.5;
            world.SyncFromProjectIncremental(project);
            Assert.Empty(errorCollector.Errors);
            Assert.NotEmpty(world.Splines);
            Assert.False(ReferenceEquals(afterOffsetEntry, world.Splines.First()));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void GlobalShortcuts_RouteFromWin32HwndAndMainWindowTunnel_AndSaveFlushesChanges()
    {
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x53, ctrl: true, shift: false, out EditorGlobalShortcut scSave));
        Assert.Equal(EditorGlobalShortcut.Save, scSave);
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x53, ctrl: true, shift: true, out EditorGlobalShortcut scSaveAll));
        Assert.Equal(EditorGlobalShortcut.SaveAll, scSaveAll);
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x5A, ctrl: true, shift: false, out EditorGlobalShortcut scUndo));
        Assert.Equal(EditorGlobalShortcut.Undo, scUndo);
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x59, ctrl: true, shift: false, out EditorGlobalShortcut scRedo));
        Assert.Equal(EditorGlobalShortcut.Redo, scRedo);
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x74, ctrl: false, shift: false, out EditorGlobalShortcut scVerify));
        Assert.Equal(EditorGlobalShortcut.Verify, scVerify);
        Assert.True(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x74, ctrl: true, shift: false, out EditorGlobalShortcut scLaunch));
        Assert.Equal(EditorGlobalShortcut.LaunchPlayer, scLaunch);
        Assert.False(Win32HwndViewportHost.TryMapGlobalShortcutKey(0x53, ctrl: false, shift: false, out _));

        string tempDir = CreateTempDirectory();
        try
        {
            Exception? capturedEx = null;
            AvaloniaUiTestHost.Run(() =>
            {
                try
                {
                    using MainWindowViewModel shell = new();
                    WorldWorkspaceService ws = shell.Workspace;
                    ws.EnableBackgroundScriptCompilation = false;

                    shell.CreateAndActivateWorld(tempDir, "Author", "ShortcutWorld", "Shortcut World", "Desc");
                    shell.Save(dirtyOnly: true);
                    Assert.False(shell.IsDirty.Value);

                    ws.AddPropPlacement(
                        "BusStop",
                        new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(10.0, 0.0, 10.0)));
                    Assert.True(shell.IsDirty.Value);

                    // Simulate Win32 child HWND forwarding Ctrl+S via Workspace.RequestGlobalShortcut
                    ws.RequestGlobalShortcut(EditorGlobalShortcut.Save);
                    Assert.False(shell.IsDirty.Value);

                    // Undo & Redo shortcuts work cleanly too
                    ws.RequestGlobalShortcut(EditorGlobalShortcut.Undo);
                    Assert.Empty(ws.CurrentProject.Value!.Placement.Props);
                    ws.RequestGlobalShortcut(EditorGlobalShortcut.Redo);
                    Assert.Single(ws.CurrentProject.Value!.Placement.Props);

                    MainWindow win = new() { DataContext = shell };
                    win.Measure(new Avalonia.Size(1100, 700));
                    win.Arrange(new Avalonia.Rect(0, 0, 1100, 700));
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

                    Assert.True(shell.IsDirty.Value);
                    KeyEventArgs keyArgs = new()
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.S,
                        KeyModifiers = KeyModifiers.Control,
                        Source = win,
                    };
                    win.RaiseEvent(keyArgs);
                    Assert.True(keyArgs.Handled);
                    Assert.False(shell.IsDirty.Value);
                    win.DataContext = null;
                }
                catch (Exception ex)
                {
                    capturedEx = ex;
                }
            });

            Assert.Null(capturedEx);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SplineAndJunction_RequireCSharpIdentifierKeyWithUnicode_AndOptionalEditorDisplayName()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;

            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "KeyDisplayWorld", "Key & Display", "Desc");
            SplinePlacementNode starter = Assert.Single(project.Placement.Splines);
            Assert.Equal("spline_001", starter.Id);
            Assert.Equal("spline_001", starter.Key);

            // Add a JunctionTemplate compatible with Layout1
            string juncTplKey = "TestCross";
            JunctionTemplateDocument juncTpl = new()
            {
                Key = juncTplKey,
                RelativePath = "Templates/Junctions/TestCross.csx",
            };
            string layoutKey = project.LaneLayouts.Keys.First();
            juncTpl.Ports.Add(new JunctionPortDefinition("S", layoutKey, new PoseDegreesModel(0, 0, -10, 0, 180, 0)));
            juncTpl.Ports.Add(new JunctionPortDefinition("N", layoutKey, new PoseDegreesModel(0, 0, 10, 0, 0, 0)));
            project.JunctionTemplates[juncTplKey] = juncTpl;

            SplinePlacementNode? secondSpline = ws.AddChainedSplineFromOutlet("spline_001", starter.TemplateKey, 30.0);
            Assert.NotNull(secondSpline);
            Assert.Equal("spline_002", secondSpline!.Id);
            Assert.Equal("spline_002", secondSpline.Key);

            JunctionPlacementNode? junction = ws.AddChainedJunctionFromSplineOutlet("spline_002", juncTplKey, "S");
            Assert.NotNull(junction);
            Assert.Equal("junction_001", junction!.Id);
            Assert.Equal("junction_001", junction.Key);

            // Verify Inspector default titles (no DisplayName) & Hierarchy falls back to Key
            ws.SelectedSplineId.Value = "spline_002";
            Assert.Equal("スプライン (Spline)", shell.InspectorTool.SplineTitleText.Value);
            Assert.Equal("spline_002", shell.InspectorTool.SplineKey.Value);
            Assert.Equal(string.Empty, shell.InspectorTool.SplineDisplayName.Value);

            SceneItemEntry splineEntryDefault = Assert.Single(shell.SceneHierarchyTool.Items, i => i.SplineId == "spline_002");
            Assert.Equal("spline_002", splineEntryDefault.Label);
            Assert.Contains("Key:spline_002", splineEntryDefault.SubLabel);
            Assert.DoesNotContain("[spline_002]", splineEntryDefault.SubLabel);

            ws.SelectedJunctionId.Value = "junction_001";
            Assert.Equal("ジャンクション (Junction)", shell.InspectorTool.JunctionTitleText.Value);

            // Optional DisplayName (Japanese + spaces allowed, persisted in World.csx via // <display name="..." />)
            ws.SelectedSplineId.Value = "spline_002";
            shell.InspectorTool.SplineDisplayName.Value = "駅前通り (南行)";
            Assert.Equal("駅前通り (南行) (Spline)", shell.InspectorTool.SplineTitleText.Value);
            SceneItemEntry splineEntryNamed = Assert.Single(shell.SceneHierarchyTool.Items, i => i.SplineId == "spline_002");
            Assert.Equal("駅前通り (南行)", splineEntryNamed.Label);
            Assert.Contains("Key:spline_002", splineEntryNamed.SubLabel);

            ChunkPropPlacement? namedProp = ws.AddPropPlacement("Grass", new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(1, 0, 2)));
            Assert.NotNull(namedProp);
            ws.SelectedPropId.Value = namedProp!.Id;
            shell.InspectorTool.PropDisplayName.Value = "南口広場";
            Assert.Equal("南口広場 (Prop)", shell.InspectorTool.PropTitleText.Value);
            SceneItemEntry propEntryNamed = Assert.Single(shell.SceneHierarchyTool.Items, i => i.PropId == namedProp.Id);
            Assert.Equal("南口広場", propEntryNamed.Label);
            Assert.Equal("南口広場", ws.GetElementDisplayName(namedProp.Id));
            ws.SelectedSplineId.Value = "spline_002";

            // Invalid Key edits (space / keyword / duplicate) must be rejected & reverted cleanly
            shell.InspectorTool.SplineKey.Value = "road 1";
            Assert.Equal("spline_002", shell.InspectorTool.SplineKey.Value);
            Assert.Equal("spline_002", secondSpline.Key);

            shell.InspectorTool.SplineKey.Value = "class";
            Assert.Equal("spline_002", shell.InspectorTool.SplineKey.Value);

            shell.InspectorTool.SplineKey.Value = "spline_001";
            Assert.Equal("spline_002", shell.InspectorTool.SplineKey.Value);

            // Japanese Unicode C# identifier Key is accepted and updates references & sidecar atomically
            shell.InspectorTool.SplineKey.Value = "バイパス_1";
            Assert.Equal("バイパス_1", secondSpline.Id);
            Assert.Equal("バイパス_1", secondSpline.Key);
            Assert.Equal("バイパス_1", ws.SelectedSplineId.Value);
            Assert.Equal("バイパス_1", junction.Origin.SourceNodeId);
            Assert.Equal("駅前通り (南行)", ws.GetElementDisplayName("バイパス_1"));
            Assert.Null(ws.GetElementDisplayName("spline_002"));

            // Also rename Junction Key to a Unicode C# identifier + set DisplayName
            ws.SelectedJunctionId.Value = "junction_001";
            shell.InspectorTool.JunctionDisplayName.Value = "北口交差点";
            shell.InspectorTool.JunctionKey.Value = "交差点_北口";
            Assert.Equal("交差点_北口", junction.Id);
            Assert.Equal("交差点_北口", junction.Key);
            Assert.Equal("北口交差点 (Junction)", shell.InspectorTool.JunctionTitleText.Value);

            // Clear DisplayName -> falls back to Key in hierarchy and default Inspector title
            shell.InspectorTool.JunctionDisplayName.Value = string.Empty;
            Assert.Equal("ジャンクション (Junction)", shell.InspectorTool.JunctionTitleText.Value);
            SceneItemEntry junctionEntryFallback = Assert.Single(shell.SceneHierarchyTool.Items, i => i.JunctionId == "交差点_北口");
            Assert.Equal("交差点_北口", junctionEntryFallback.Label);

            // Save & inspect generated World.csx (must use C# variable names & .Key, and store DisplayName via // <display name="..." />)
            shell.Save(dirtyOnly: false);
            string worldCsxPath = Path.Combine(project.WorldDirectory, project.Placement.RelativePath);
            string worldCsxText = File.ReadAllText(worldCsxPath);
            Assert.Contains("var fSpline =", worldCsxText);
            Assert.Contains("fSpline.Key = \"バイパス_1\";", worldCsxText);
            Assert.Contains("var バイパス_1 = fSpline.Build();", worldCsxText);
            Assert.Contains("var fJunction =", worldCsxText);
            Assert.Contains("fJunction.Key = \"交差点_北口\";", worldCsxText);
            Assert.Contains("var 交差点_北口 = fJunction.Build();", worldCsxText);
            Assert.Contains("    // <display name=\"駅前通り (南行)\" />", worldCsxText);
            Assert.Contains("    // <display name=\"南口広場\" />", worldCsxText);
            Assert.DoesNotContain("北口交差点", worldCsxText);

            // Round-trip World.csx parse and WorldValidator must succeed with 0 errors
            (ParsedWorldCsx? reparsed, IReadOnlyList<ParseDiagnostic> parseDiag) = WorldCsxSerializer.Parse("World.csx", worldCsxText);
            Assert.Empty(parseDiag);
            Assert.NotNull(reparsed);
            Assert.Equal("駅前通り (南行)", Assert.Single(reparsed!.Document.Splines, s => s.Key == "バイパス_1").DisplayName);
            Assert.Equal("南口広場", Assert.Single(reparsed.Document.Props, p => p.DisplayName == "南口広場").DisplayName);
            IReadOnlyList<ValidationIssue> valIssues = WorldValidator.Validate(project);
            Assert.DoesNotContain(valIssues, static v => v.Severity == ValidationSeverity.Error);

            // Verify Undo reverts Junction DisplayName clear, Junction Key rename, and Spline Key rename in order
            ws.Undo(); // restore junction DisplayName
            Assert.Equal("北口交差点", ws.GetElementDisplayName("交差点_北口"));
            ws.Undo(); // revert junction Key -> junction_001
            Assert.Equal("junction_001", junction.Id);
            Assert.Equal("junction_001", junction.Key);
            ws.Undo(); // revert junction DisplayName
            ws.Undo(); // revert spline Key -> spline_002
            Assert.Equal("spline_002", secondSpline.Id);
            Assert.Equal("spline_002", secondSpline.Key);
            Assert.Equal("spline_002", junction.Origin.SourceNodeId);
            Assert.Equal("駅前通り (南行)", ws.GetElementDisplayName("spline_002"));

            ws.Redo();
            Assert.Equal("バイパス_1", secondSpline.Id);
            Assert.Equal("バイパス_1", junction.Origin.SourceNodeId);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }


    private static void MeasureAndArrange(Control control, object dataContext, double width, double height)
    {
        control.DataContext = dataContext;
        control.Measure(new Avalonia.Size(width, height));
        control.Arrange(new Avalonia.Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Assert.True(0.0 < control.Bounds.Width);
        Assert.True(0.0 < control.Bounds.Height);
    }

    private sealed class DummyGraphicsClient : IGraphicsClient
    {
        public nint Hwnd => 0;
        public IDXGISwapChain1 SwapChain => null!;
        public TransportX.Rendering.Backend.RenderSurface? Surface => null;

        public void Dispose()
        {
        }
    }

    private sealed class PreviewTestWorldInfo : IWorldInfo
    {
        public string InfoPath { get; init; } = string.Empty;
        public string Title { get; init; } = "Test";
        public string Description { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string? Identifier { get; init; }
        public string AppPath { get; init; } = string.Empty;
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    }
}
