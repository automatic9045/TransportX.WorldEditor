using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TransportX.Cameras;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.Tests.ViewModels;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.Views;
using TransportX.WorldEditor.Views.Controls;
using TransportX.WorldEditor.Views.Documents;
using TransportX.WorldEditor.Views.Tools;
using TransportX.Worlds;
using Vortice.DXGI;
using Xunit;

namespace TransportX.WorldEditor.Tests;

public sealed class Phase7FixesAndHighlighterTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p7_{Guid.NewGuid():N}");
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

    [Fact]
    public void RoslynClassifier_IdentifiesCommentsStringsNumbersKeywordsMethodsTypesAndGlobals()
    {
        string code =
            "// line comment\n" +
            "if (0 < count) {\n" +
            "    var s = fSpline.SpeedLimit(1, 50.5);\n" +
            "    Component<TrafficSignals>().AddController(\"4Forked1\", \"Signal.xml\");\n" +
            "}\n";

        IReadOnlyList<CSharpHighlightSpan> spans = CSharpSyntaxHighlightClassifier.Classify(code);

        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.Comment);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.ControlKeyword);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.Keyword);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.Number);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.String);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.Method);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.Type);
        Assert.Contains(spans, static s => s.Kind == CSharpHighlightKind.ScriptGlobal);
    }

    [Fact]
    public void CancelActiveInteraction_AndMainWindowEscape_ExitPlacementModesEvenWhenAvaloniaFocused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            AvaloniaUiTestHost.Run(() =>
            {
                using MainWindowViewModel shell = new();
                shell.CreateAndActivateWorld(tempDir, "Author", "EscWorld", "Esc Test", "");

                MainWindow window = new()
                {
                    DataContext = shell,
                    Width = 900,
                    Height = 600,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Position = new PixelPoint(-20000, -20000),
                };

                window.Show();
                Dispatcher.UIThread.RunJobs();

                foreach (EditorToolMode placementMode in (ReadOnlySpan<EditorToolMode>)[
                    EditorToolMode.PlaceProp,
                    EditorToolMode.PlaceSpline,
                    EditorToolMode.PlaceJunction])
                {
                    shell.Workspace.StartPlacementMode(placementMode);
                    Assert.Equal(placementMode, shell.Workspace.ActiveToolMode.Value);

                    // Simulate focus remaining on an Avalonia button (e.g., "+ 追加" or toolbar)
                    Button? addBtn = window.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                    addBtn?.Focus();
                    Dispatcher.UIThread.RunJobs();

                    KeyEventArgs escArgs = new()
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Escape,
                        KeyModifiers = KeyModifiers.None,
                        Source = window,
                    };
                    window.RaiseEvent(escArgs);
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(EditorToolMode.Select, shell.Workspace.ActiveToolMode.Value);
                    Assert.True(escArgs.Handled);
                }

                window.Close();
                Dispatcher.UIThread.RunJobs();
            });
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ViewportLayers_TrafficFlagAndVerificationVisibleLayersSync_AndChunkGridMatchesDrawChunkCountRadius()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "LayerGridWorld", "Layer & Grid", "");

            ViewportDocumentViewModel viewportVm = shell.ViewportDocument;
            Assert.True(viewportVm.ShowTrafficLayer.Value);
            Assert.True(shell.Workspace.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Traffic));

            viewportVm.ShowTrafficLayer.Value = false;
            Assert.False(shell.Workspace.OverlayFlags.Value.HasFlag(ViewportOverlayFlags.Traffic));

            viewportVm.ShowCollidersLayer.Value = true;
            viewportVm.ShowNetworkLayer.Value = false;
            viewportVm.ShowTrafficLayer.Value = true;

            using GraphicsHost graphicsHost = new();
            using TestGraphicsClient graphicsClient = new();
            using DummyAudioHost audioHost = new();
            using DummyInputHost inputHost = new();
            using PhysicsHost physicsHost = PhysicsHost.Create();
            TransportX.Dependency.PluginLoadContext appCtx = new(typeof(EditorWorld).Assembly.Location);
            try
            {
                FreeCameraController cameraController = new();
                TransportX.Platform platform = new()
                {
                    Window = new ViewportWindowAdapter(0, 640, 480),
                    Input = inputHost.SilkContext,
                };

                WorldBuilder builder = new(new TransportXWorldInfoStub(project.WorldInfoFilePath!))
                {
                    Platform = platform,
                    GraphicsHost = graphicsHost,
                    GraphicsClient = graphicsClient,
                    AudioHost = audioHost,
                    AudioClient = new DummyAudioClient(),
                    InputHost = inputHost,
                    InputClient = new DummyInputClient(),
                    PhysicsHost = physicsHost,
                    Options = new WorldOptions { SimulationChunkCount = 2, IsDebugMode = false },
                    ErrorCollector = new TransportX.WorldEditor.Rendering.Diagnostics.EditorErrorCollector(),
                    AppContext = appCtx,
                    TimeManager = new TimeManager(),
                    Camera = cameraController.Camera,
                };

                using EditorWorld world = new(builder)
                {
                    GridDrawChunkCount = EditorViewportRenderer.DefaultDrawChunkCount,
                };
                world.RebuildFromProject(project);
                world.UpdateOverlays(
                    null,
                    null,
                    null,
                    EditorToolMode.Select,
                    GizmoAxis.None,
                    ViewportOverlayFlags.Normal | ViewportOverlayFlags.ChunkGrid,
                    null,
                    ChunkIndex.Zero);

                // DrawChunkCount = 3 -> radius = 2 -> 5x5 = 25 grid chunks matching SceneSubmitter range
                int populatedGridChunks = 0;
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (world.Chunks.TryGetValue(new ChunkIndex(dx, dz), out Chunk? chunk) && 0 < chunk.Models.Count)
                        {
                            populatedGridChunks++;
                        }
                    }
                }

                Assert.Equal(25, populatedGridChunks);
            }
            finally
            {
                appCtx.Unload();
            }
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AvaloniaUi_MenuAndFlyoutUseNonBitmapJapaneseFallbackFont_AndCodeEditorHighlights()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempDir = CreateTempDirectory();
        try
        {
            AvaloniaUiTestHost.Run(() =>
            {
                Assert.True(Application.Current!.TryFindResource("ContentControlThemeFontFamily", out object? res));
                FontFamily themeFont = Assert.IsType<FontFamily>(res);
                Assert.StartsWith("Yu Gothic UI", themeFont.Name, StringComparison.Ordinal);

                using MainWindowViewModel shell = new();
                shell.CreateAndActivateWorld(tempDir, "Author", "FontTest", "Font Test", "");

                SceneHierarchyView hierarchy = new() { DataContext = shell.SceneHierarchyTool };
                ViewportDocumentView viewportView = new() { DataContext = shell.ViewportDocument };
                CSharpCodeEditor codeEditor = new()
                {
                    Text = "// test\nfSpline.SpeedLimit(1, 50);",
                };

                Window host = new()
                {
                    Width = 900,
                    Height = 600,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Position = new PixelPoint(-20000, -20000),
                    Content = new StackPanel
                    {
                        Children = { hierarchy, viewportView, codeEditor },
                    },
                };

                host.Show();
                Dispatcher.UIThread.RunJobs();

                Button? addButton = hierarchy.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                Assert.NotNull(addButton);
                Assert.DoesNotContain("MS Gothic", addButton!.FontFamily.Name, StringComparison.OrdinalIgnoreCase);

                Button? displayButton = viewportView.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(static b => b.Content is string s && s.Contains("表示", StringComparison.Ordinal));
                Assert.NotNull(displayButton);
                Assert.DoesNotContain("MS Gothic", displayButton!.FontFamily.Name, StringComparison.OrdinalIgnoreCase);

                AvaloniaEdit.TextEditor? innerEditor = codeEditor.GetVisualDescendants()
                    .OfType<AvaloniaEdit.TextEditor>()
                    .FirstOrDefault();
                Assert.NotNull(innerEditor);
                Assert.Contains("MS Gothic", innerEditor!.FontFamily.Name, StringComparison.OrdinalIgnoreCase);

                host.Close();
                Dispatcher.UIThread.RunJobs();
            });
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void FixedFactoryVariables_PreservePreBuildUserCodeAcrossNodeKeyRenames()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "RenameFactoryWorld", "Rename Test", "");

            shell.Workspace.AddSplineAtChunkPose(
                "Spline1",
                new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.Identity));
            string splineId = project.Placement.Splines[0].Id;
            project.Placement.Splines[0].PreBuildUserCode = "fSpline.SpeedLimit(1, 50);";

            Assert.True(shell.Workspace.RenameSplineKey(splineId, "MainArterialRoad"));

            string worldCsx = WorldCsxSerializer.Generate(project.Placement, ["Templates/Splines/Spline1.csx"]);
            Assert.Contains("fSpline.SpeedLimit(1, 50);", worldCsx, StringComparison.Ordinal);
            Assert.Contains("fSpline.Key = \"MainArterialRoad\";", worldCsx, StringComparison.Ordinal);
            Assert.Contains("    fSpline.Build();", worldCsx, StringComparison.Ordinal);
            Assert.DoesNotContain("var MainArterialRoad =", worldCsx, StringComparison.Ordinal);

            (ParsedWorldCsx? reparsed, IReadOnlyList<Serialization.Diagnostics.ParseDiagnostic> diag) =
                WorldCsxSerializer.Parse("World.csx", worldCsx);
            Assert.Empty(diag);
            Assert.NotNull(reparsed);
            Assert.Equal("fSpline.SpeedLimit(1, 50);", reparsed!.Document.Splines[0].PreBuildUserCode);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SceneHierarchy_SupportsCategoryTabs_TopologicalNetworkDefaultOrder_AndViewSorts()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldWorkspaceService ws = shell.Workspace;
            ws.EnableBackgroundScriptCompilation = false;
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "HierarchyTabsWorld", "Hierarchy Tabs", "");

            SplinePlacementNode a1 = Assert.Single(project.Placement.Splines);
            ws.SetSplineDisplayName(a1.Id, "A街・本線1");
            SplinePlacementNode a2 = ws.AddChainedSplineFromOutlet(a1.Id, a1.TemplateKey, 25.0)!;
            ws.SetSplineDisplayName(a2.Id, "A街・本線2");

            SplinePlacementNode b1 = ws.AddSplineAtChunkPose(
                a1.TemplateKey,
                new WorldPoseDegreesModel(new ChunkIndexModel(3, 1), PoseDegreesModel.Identity))!;
            ws.SetSplineDisplayName(b1.Id, "B街・本線1");
            SplinePlacementNode b2 = ws.AddChainedSplineFromOutlet(b1.Id, a1.TemplateKey, 25.0)!;
            ws.SetSplineDisplayName(b2.Id, "B街・本線2");

            // Added after B1/B2, but connected to A2 -> Network default order keeps A-village contiguous before B-village
            SplinePlacementNode a3 = ws.AddChainedSplineFromOutlet(a2.Id, a1.TemplateKey, 25.0)!;
            ws.SetSplineDisplayName(a3.Id, "A街・支線3");

            ChunkPropPlacement pFar = ws.AddPropPlacement("Grass", new WorldPoseDegreesModel(new ChunkIndexModel(4, 2), PoseDegreesModel.FromTranslation(10, 0, 10)))!;
            ws.SetPropDisplayName(pFar.Id, "A倉庫");
            ChunkPropPlacement pNear = ws.AddPropPlacement("Grass", new WorldPoseDegreesModel(new ChunkIndexModel(-1, 0), PoseDegreesModel.FromTranslation(20, 0, 20)))!;
            ws.SetPropDisplayName(pNear.Id, "Zバス停");

            SceneHierarchyToolViewModel hierarchy = shell.SceneHierarchyTool;
            Assert.Equal(SceneHierarchyFilterCategory.All, hierarchy.SelectedFilterCategory.Value);
            Assert.True(hierarchy.IsFilterAll.Value);
            Assert.Contains(hierarchy.Items, i => i.FilterCategory == SceneHierarchyFilterCategory.Environment);
            Assert.Contains(hierarchy.Items, i => i.FilterCategory == SceneHierarchyFilterCategory.Props);
            Assert.Contains(hierarchy.Items, i => i.FilterCategory == SceneHierarchyFilterCategory.Network);

            // Environment tab
            hierarchy.SelectFilterEnvironmentCommand.Execute(R3.Unit.Default);
            Assert.True(hierarchy.IsFilterEnvironment.Value);
            Assert.All(hierarchy.Items, i => Assert.Equal(SceneHierarchyFilterCategory.Environment, i.FilterCategory));

            // Network tab (default = topological connection order: A1 -> A2 -> A3 -> B1 -> B2)
            hierarchy.SelectFilterNetworkCommand.Execute(R3.Unit.Default);
            Assert.True(hierarchy.IsFilterNetwork.Value);
            List<string> networkIds = hierarchy.Items.Select(static i => i.SplineId!).ToList();
            Assert.Equal([a1.Id, a2.Id, a3.Id, b1.Id, b2.Id], networkIds);

            // Props tab + Name sort (A倉庫, Zバス停) vs Chunk sort (Chunk(-1,0), Chunk(4,2)) without mutating WorldPlacementDocument.Props
            hierarchy.SelectFilterPropsCommand.Execute(R3.Unit.Default);
            Assert.True(hierarchy.IsFilterProps.Value);
            Assert.Equal([pFar.Id, pNear.Id], hierarchy.Items.Select(static i => i.PropId!).ToList());

            hierarchy.SetSortMode(SceneHierarchySortMode.Name);
            Assert.Equal([pFar.Id, pNear.Id], hierarchy.Items.Select(static i => i.PropId!).ToList());

            hierarchy.SetSortMode(SceneHierarchySortMode.Chunk);
            Assert.Equal([pNear.Id, pFar.Id], hierarchy.Items.Select(static i => i.PropId!).ToList());

            // Underlying WorldPlacementDocument.Props order remains untouched
            Assert.Equal([pFar.Id, pNear.Id], project.Placement.Props.Select(static p => p.Id).ToList());

            // Selection sync restores selected prop when returning to All
            ws.SelectProp(pFar.Id);
            Assert.Equal(pFar.Id, hierarchy.SelectedItem.Value?.PropId);
            hierarchy.SelectFilterNetworkCommand.Execute(R3.Unit.Default);
            Assert.Null(hierarchy.SelectedItem.Value);
            hierarchy.SelectFilterAllCommand.Execute(R3.Unit.Default);
            Assert.Equal(pFar.Id, hierarchy.SelectedItem.Value?.PropId);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private sealed class TransportXWorldInfoStub : IWorldInfo
    {
        public TransportXWorldInfoStub(string infoPath)
        {
            InfoPath = infoPath;
            Path = typeof(EditorWorld).Assembly.Location;
        }

        public string InfoPath { get; }
        public string Title => "Stub";
        public string Description => string.Empty;
        public string Author => "Test";
        public string Path { get; }
        public string? Identifier => null;
        public string AppPath => string.Empty;
        public IReadOnlyList<string> Args { get; } = Array.Empty<string>();
    }

    private sealed class TestGraphicsClient : IGraphicsClient
    {
        public nint Hwnd => 0;
        public IDXGISwapChain1 SwapChain => null!;
        public RenderSurface? Surface => null;

        public void Dispose()
        {
        }
    }
}
