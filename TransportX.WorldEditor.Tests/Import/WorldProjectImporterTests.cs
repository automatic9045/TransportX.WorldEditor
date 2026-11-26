using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TransportX.Cameras;
using TransportX.Diagnostics;
using TransportX.Dependency;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Verification;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Import;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.Views.Dialogs;
using Vortice.DXGI;
using Xunit;

namespace TransportX.WorldEditor.Tests.Import;

public sealed class WorldProjectImporterTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_imp_{Guid.NewGuid():N}");
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

    private static string FindBasicSampleWorldInfoPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "external", "TransportX", "_out", "Data", "Automatic9045.BasicSample.xml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("Automatic9045.BasicSample.xml could not be located from test base directory.");
    }

    [Fact]
    public void ImportBasicSample_ReconstructsCanonicalProject_OpensWithZeroProtectedFiles_AndPassesScriptVerification()
    {
        string sourceWorldInfo = FindBasicSampleWorldInfoPath();
        string tempDestRoot = CreateTempDirectory();
        try
        {
            WorldImportOptions options = new(
                sourceWorldInfo,
                tempDestRoot,
                "ImportedTest",
                "BasicSampleImported",
                "インポート版 BasicSample",
                "Import テスト");

            using MainWindowViewModel shell = new();
            shell.Workspace.EnableBackgroundScriptCompilation = false;

            WorldImportResult result = shell.ImportAndOpenWorld(options);

            Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(static d => d.Message)));
            Assert.DoesNotContain(result.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal(2, result.ImportedSplineTemplateCount);
            Assert.Equal(6, result.ImportedJunctionTemplateCount);
            Assert.Equal(19, result.ImportedSplineCount);
            Assert.Equal(10, result.ImportedJunctionCount);
            Assert.Equal(6, result.ImportedPropCount);
            Assert.True(0 < result.PreservedUserCodeStatementCount);
            Assert.True(0 < result.CopiedAssetFileCount);

            string destWorldDir = result.DestinationWorldDirectory;
            Assert.True(File.Exists(result.DestinationWorldInfoPath));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "World.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "World.manifest.xml")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Init.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "__Editor.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "__Editor.json")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Templates", "Load.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Templates", "Splines", "Spline1.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Templates", "Splines", "Spline2.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Templates", "Junctions", "3Forked1.csx")));
            Assert.False(File.Exists(Path.Combine(destWorldDir, "Templates", "Junctions", "_3Forked1.csx")));
            Assert.True(File.Exists(Path.Combine(destWorldDir, "Templates", "Junctions", "DeadEnd1.csx")));
            Assert.False(File.Exists(Path.Combine(destWorldDir, "Templates.csx")));

            string templatesLoadText = File.ReadAllText(Path.Combine(destWorldDir, "Templates", "Load.csx"));
            Assert.Contains("#load \"../__Editor.csx\"", templatesLoadText);
            Assert.Contains("#load \"Splines/Spline1.csx\"", templatesLoadText);
            Assert.Contains("#load \"Junctions/3Forked1.csx\"", templatesLoadText);
            Assert.Contains("#load \"Junctions/DeadEnd1.csx\"", templatesLoadText);

            string threeForked1Text = File.ReadAllText(Path.Combine(destWorldDir, "Templates", "Junctions", "3Forked1.csx"));
            Assert.Contains("// <editor-generated version=\"1\">", threeForked1Text);
            Assert.Contains("SignalController(\"4Forked1\")", threeForked1Text);
            Assert.Contains("PutSignalProp(", threeForked1Text);
            Assert.Contains("tPath_SE_R_Out.Deflection(1).Yield((\"NS_S\", 7, 17), (\"NE_L\", 7, s_s1_2))", threeForked1Text);
            Assert.Contains("out var s_s1_2", threeForked1Text);
            Assert.DoesNotContain("for (double x", threeForked1Text);

            string deadEndText = File.ReadAllText(Path.Combine(destWorldDir, "Templates", "Junctions", "DeadEnd1.csx"));
            Assert.Contains("tPath_Anon.Width.Constant(s_s2 - s_s1);", deadEndText);

            string worldCsxText = File.ReadAllText(Path.Combine(destWorldDir, "World.csx"));
            Assert.Contains("// <editor-generated version=\"1\">", worldCsxText);
            Assert.Contains("#load \"Templates/Load.csx\"", worldCsxText);
            Assert.DoesNotContain("#load \"Templates/Splines/", worldCsxText);
            Assert.DoesNotContain("#load \"Templates/Junctions/", worldCsxText);
            Assert.Contains("fSpline.SpeedLimit(1, 50)", worldCsxText);
            Assert.Contains("var fSpline =", worldCsxText);
            Assert.Contains("Avatar_LV290N_Konan.xml", worldCsxText);

            WorldProject? openedProject = shell.Workspace.CurrentProject.Value;
            Assert.NotNull(openedProject);
            Assert.Empty(openedProject!.ProtectedFiles);
            Assert.True(shell.HasOpenProject.Value);
            Assert.DoesNotContain(shell.Workspace.ParseDiagnostics.Value, static d => d.Severity == DiagnosticSeverity.Error);

            IReadOnlyList<ValidationIssue> valIssues = WorldValidator.Validate(openedProject);
            Assert.DoesNotContain(valIssues, static v => v.Severity == ValidationSeverity.Error);

            string? avatarXmlRel = openedProject.Placement.Avatar?.XmlPath;
            Assert.False(string.IsNullOrWhiteSpace(avatarXmlRel));
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(openedProject.WorldDirectory, avatarXmlRel!))));

            if (OperatingSystem.IsWindows())
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
                    openedProject,
                    platform,
                    graphicsHost,
                    graphicsClient,
                    cameraController.Camera,
                    isDebugMode: false);

                Assert.True(started, string.Join("\n", runner.ErrorCollector.Errors.Select(static e => e.Message)));
                Assert.False(runner.HasFatalError);

                for (int i = 0; i < 5; i++)
                {
                    runner.Tick(TimeSpan.FromSeconds(1.0 / 60.0));
                }

                Assert.False(runner.HasFatalError);
                Assert.DoesNotContain(
                    runner.ErrorCollector.Errors,
                    static e => e.Level == ErrorLevel.Fatal || e.Level == ErrorLevel.Error);
            }
        }
        finally
        {
            DeleteTempDirectory(tempDestRoot);
        }
    }

    [Fact]
    public void Import_PreservesPureCustomExtraLoadScript_AndRejectsSourceDirectoryOverlap()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string sourceWorldDir = Path.Combine(tempRoot, "SrcAuthor", "HandWritten");
            string scriptsDir = Path.Combine(sourceWorldDir, "Scripts");
            Directory.CreateDirectory(scriptsDir);

            string sourceXml = Path.Combine(tempRoot, "SrcAuthor.HandWritten.xml");
            File.WriteAllText(sourceXml,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<WorldInfo>\n" +
                "  <Title>Hand Written</Title>\n" +
                "  <Description>Custom hook test</Description>\n" +
                "  <Author>SrcAuthor</Author>\n" +
                "  <Path>$(bin)TransportX.Scripting.dll</Path>\n" +
                "  <Args><Arg>SrcAuthor\\HandWritten\\World.csx</Arg></Args>\n" +
                "</WorldInfo>\n");

            File.WriteAllText(Path.Combine(scriptsDir, "CustomHook.csx"),
                "// Pure custom helper without editor-managed commands\n" +
                "int customCounter = 42;\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "Init.csx"),
                "Models.LoadList(\"Models.txt\");\n" +
                "Network.LaneLayouts.Load(\"L1\", \"Layout.xml\");\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "Models.txt"), "Dummy\tNONE\t$NonCollision\n");
            File.WriteAllText(Path.Combine(sourceWorldDir, "Layout.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LaneLayout><Lanes /></LaneLayout>\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "World.csx"),
                "#load \"Init.csx\"\n" +
                "#load \"Scripts/CustomHook.csx\"\n" +
                "Camera.Locate(0, 0, 10, 5, 20, 0, 0, 0);\n" +
                "var f = Chunks[0, 0].BeginSpline(0, 0, 0);\n" +
                "f.Curves.Straight(25);\n" +
                "var sp = f.Build();\n");

            // 1. Overlapping destination inside source must be rejected cleanly
            WorldImportResult overlapFail = WorldProjectImporter.Import(new WorldImportOptions(
                sourceXml,
                Path.Combine(sourceWorldDir, "Sub"),
                "SrcAuthor",
                "SubOut",
                "Bad",
                "Bad"));
            Assert.False(overlapFail.Succeeded);
            Assert.Contains(overlapFail.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);

            // 2. Clean import into sibling folder succeeds and keeps CustomHook.csx in ExtraLoadScripts
            string destRoot = Path.Combine(tempRoot, "Dest");
            WorldImportResult ok = WorldProjectImporter.Import(new WorldImportOptions(
                sourceXml,
                destRoot,
                "DestAuthor",
                "HandWrittenImported",
                "Hand Written Imported",
                "OK"));

            Assert.True(ok.Succeeded);
            string importedHook = Path.Combine(ok.DestinationWorldDirectory, "Scripts", "CustomHook.csx");
            Assert.True(File.Exists(importedHook));

            string generatedWorldCsx = File.ReadAllText(Path.Combine(ok.DestinationWorldDirectory, "World.csx"));
            Assert.Contains("#load \"Scripts/CustomHook.csx\"", generatedWorldCsx);

            WorldProjectLoadResult loaded = WorldProjectStorage.Load(ok.DestinationWorldInfoPath);
            Assert.Empty(loaded.Project.ProtectedFiles);
            Assert.Contains("Scripts/CustomHook.csx", loaded.Project.Placement.ExtraLoadScripts);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void ImportWorld_ResolvesResourcePathsRelativeToNestedLoadedScripts()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string srcRoot = Path.Combine(tempRoot, "Source");
            string srcWorldDir = Path.Combine(srcRoot, "NestedAuthor", "NestedWorld");
            Directory.CreateDirectory(Path.Combine(srcWorldDir, "LaneLayouts", "33_42"));
            Directory.CreateDirectory(Path.Combine(srcWorldDir, "Models", "Buildings"));
            Directory.CreateDirectory(Path.Combine(srcWorldDir, "CrossSections", "Sub"));

            string srcXml = Path.Combine(srcRoot, "NestedAuthor.NestedWorld.xml");
            File.WriteAllText(srcXml,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<WorldInfo>\n" +
                "  <Title>Nested</Title>\n" +
                "  <Author>NestedAuthor</Author>\n" +
                "  <Path>$(bin)TransportX.Scripting.dll</Path>\n" +
                "  <Args><Arg>NestedAuthor\\NestedWorld\\World.csx</Arg></Args>\n" +
                "</WorldInfo>\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "Models.txt"), "RootProp\tNONE\t$NonCollision\n");
            File.WriteAllText(Path.Combine(srcWorldDir, "Models", "Buildings", "Models.txt"), "Bldg1\tNONE\t$NonCollision\n");
            File.WriteAllText(Path.Combine(srcWorldDir, "LaneLayouts", "33_42", "L_1.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<LaneLayout><Lanes /></LaneLayout>\n");
            File.WriteAllText(Path.Combine(srcWorldDir, "CrossSections", "Sub", "Road.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<CrossSection><Vertices /><Materials /><ColliderMaterials /><Surfaces /></CrossSection>\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "Init.csx"),
                "Models.LoadList(\"Models.txt\");\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "LaneLayouts", "Load.csx"),
                "Network.LaneLayouts.Load(\"33_42_L_1\", @\"33_42\\L_1.xml\");\n" +
                "Network.LaneLayouts.AddOpposition(\"33_42_L_1_Opposition\", \"33_42_L_1\");\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "Models", "Buildings", "Load.csx"),
                "Models.LoadList(\"Buildings\", \"Models.txt\");\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "CrossSections", "Load.csx"),
                "CrossSections.Load(\"SubRoad\", @\"Sub\\Road.xml\");\n");

            File.WriteAllText(Path.Combine(srcWorldDir, "World.csx"),
                "#load \"Init.csx\"\n" +
                "#load \"LaneLayouts\\Load.csx\"\n" +
                "#load \"Models\\Buildings\\Load.csx\"\n" +
                "#load \"CrossSections\\Load.csx\"\n" +
                "Camera.Locate(0, 0, 10, 5, 20, 0, 0, 0);\n");

            string destRoot = Path.Combine(tempRoot, "Dest");
            WorldImportResult result = WorldProjectImporter.Import(new WorldImportOptions(
                srcXml,
                destRoot,
                "OutAuthor",
                "NestedImported",
                "Nested Imported",
                "OK"));

            Assert.True(result.Succeeded, string.Join(" | ", result.Diagnostics.Select(static d => d.Message)));
            Assert.DoesNotContain(result.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
            Assert.NotNull(result.Project);

            Assert.Contains(result.Project!.Init.LaneLayouts, l => l.Key == "33_42_L_1" && l.RelativePath == "LaneLayouts/33_42/L_1.xml");
            Assert.Contains(result.Project.Init.ModelLists, m => m.BundleKey is null && m.RelativePath == "Models.txt");
            Assert.Contains(result.Project.Init.ModelLists, m => m.BundleKey == "Buildings" && m.RelativePath == "Models/Buildings/Models.txt");
            Assert.Contains(result.Project.Init.CrossSections, c => c.Key == "SubRoad" && c.RelativePath == "CrossSections/Sub/Road.xml");
            Assert.True(result.Project.LaneLayouts.ContainsKey("33_42_L_1"));
            Assert.True(result.Project.ModelLists.ContainsKey("Models/Buildings/Models.txt"));
            Assert.True(result.Project.CrossSections.ContainsKey("SubRoad"));
            Assert.Contains("AddOpposition(\"33_42_L_1_Opposition\", \"33_42_L_1\")", result.Project.Init.UserCode ?? string.Empty);

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(result.DestinationWorldInfoPath);
            Assert.DoesNotContain(reloaded.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
            Assert.True(reloaded.Project.LaneLayouts.ContainsKey("33_42_L_1"));
            Assert.True(reloaded.Project.ModelLists.ContainsKey("Models/Buildings/Models.txt"));
            Assert.True(reloaded.Project.CrossSections.ContainsKey("SubRoad"));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void ImportWorld_PreviewAndDialogSmokeTest()
    {
        string sourceXml = FindBasicSampleWorldInfoPath();
        WorldImportSourcePreview? preview = WorldProjectImporter.ReadSourcePreview(sourceXml);
        Assert.NotNull(preview);
        Assert.Equal("automatic9045", preview!.SuggestedAuthor);
        Assert.Equal("BasicSample_Imported", preview.SuggestedWorldKey);
        Assert.Equal("サンプルワールド", preview.SuggestedTitle);

        Exception? captured = null;
        AvaloniaUiTestHost.Run(() =>
        {
            try
            {
                ImportWorldDialogWindow dialog = new();
                dialog.Measure(new Size(640, 480));
                dialog.Arrange(new Rect(0, 0, 640, 480));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                Assert.True(0.0 < dialog.Bounds.Width);
                Assert.True(0.0 < dialog.Bounds.Height);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        Assert.Null(captured);
    }

    [Fact]
    public void BuildTemplateRelativePath_PreservesLeadingDigits_AndSanitizesInvalidChars()
    {
        Assert.Equal("Templates/Junctions/3Forked1.csx", WorldProjectStorage.BuildJunctionTemplateRelativePath("3Forked1"));
        Assert.Equal("Templates/Splines/2LaneRoad.csx", WorldProjectStorage.BuildSplineTemplateRelativePath("2LaneRoad"));
        Assert.Equal("Templates/Splines/3_Road_A_.csx", WorldProjectStorage.BuildSplineTemplateRelativePath("3:Road/A?"));
        Assert.Equal("Templates/Junctions/CON_.csx", WorldProjectStorage.BuildJunctionTemplateRelativePath("CON"));
        Assert.Equal("Templates/Junctions/Junction.csx", WorldProjectStorage.BuildJunctionTemplateRelativePath("..."));
    }

    [Fact]
    public void Import_PreservesDisplayDirectives_AndResolvesOptionANetworkIndexers()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            string sourceWorldDir = Path.Combine(tempRoot, "SrcAuthor", "OptionAWorld");
            Directory.CreateDirectory(sourceWorldDir);

            string sourceXml = Path.Combine(tempRoot, "SrcAuthor.OptionAWorld.xml");
            File.WriteAllText(sourceXml,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<WorldInfo>\n" +
                "  <Title>OptionA</Title>\n" +
                "  <Description>Display and indexer test</Description>\n" +
                "  <Author>SrcAuthor</Author>\n" +
                "  <Path>$(bin)TransportX.Scripting.dll</Path>\n" +
                "  <Args><Arg>SrcAuthor\\OptionAWorld\\World.csx</Arg></Args>\n" +
                "</WorldInfo>\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "Init.csx"),
                "Models.LoadList(\"Models.txt\");\n" +
                "Network.LaneLayouts.Load(\"L1\", \"Layout.xml\");\n");
            File.WriteAllText(Path.Combine(sourceWorldDir, "Models.txt"), "Dummy\tNONE\t$NonCollision\n");
            File.WriteAllText(Path.Combine(sourceWorldDir, "Layout.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<LaneLayout><Lanes><Lane Key=\"L0\" X=\"0\" Y=\"0\" FlowDirection=\"Out\" Width=\"3\" AllowedTraffic=\"All\" /></Lanes></LaneLayout>\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "Templates.csx"),
                "var tSpline = Network.Templates.CreateSpline(\"Spline1\", \"L1\");\n" +
                "var tJunction = Network.Templates.CreateJunction(\"Junc1\");\n" +
                "tJunction.AddPort(\"S\", \"L1\", 0, 0, 0, 0, 180, 0);\n" +
                "tJunction.AddPort(\"N\", \"L1\", 0, 0, 10, 0, 0, 0);\n");

            File.WriteAllText(Path.Combine(sourceWorldDir, "World.csx"),
                "#load \"Init.csx\"\n" +
                "#load \"Templates.csx\"\n" +
                "Camera.Locate(0, 0, 0, 5, 10, 0, 0, 0);\n" +
                "// <display name=\"駅前バス停\" />\n" +
                "Chunks[0, 0].PutProp(\"Dummy\", 1, 0, 2);\n" +
                "// <display name=\"1号線-本線\" />\n" +
                "var fSpline = Chunks[0, 0].BeginSpline(\"Spline1\", 0, 0, 0);\n" +
                "fSpline.Key = \"1号線-本線\";\n" +
                "fSpline.Curves.Straight(30);\n" +
                "fSpline.Build();\n" +
                "var fJunction = Network.Splines[\"1号線-本線\"].IntoJunction(\"Junc1\", \"S\");\n" +
                "fJunction.Key = \"交差点-N\";\n" +
                "// <display name=\"北口交差点\" />\n" +
                "fJunction.Build();\n" +
                "// <display name=\"連絡線\" />\n" +
                "fSpline = Network.Junctions[\"交差点-N\"].IntoSpline(\"N\", \"Spline1\");\n" +
                "fSpline.Key = \"2号線\";\n" +
                "fSpline.Curves.Straight(20);\n" +
                "fSpline.Build();\n" +
                "Network.Connect(Network.Splines[\"2号線\"].Outlet, Network.Junctions[\"交差点-N\"].Junction.Ports[\"N\"]);\n");

            string destRoot = Path.Combine(tempRoot, "Dest");
            WorldImportResult result = WorldProjectImporter.Import(new WorldImportOptions(
                sourceXml,
                destRoot,
                "DestAuthor",
                "OptionAWorld_Imported",
                "Option A Imported",
                "Test"));

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Project);
            WorldProject imported = result.Project!;

            Assert.Equal("駅前バス停", Assert.Single(imported.Placement.Props).DisplayName);
            Core.Models.Placement.SplinePlacementNode line1 = Assert.Single(imported.Placement.Splines, s => s.Key == "1号線-本線");
            Assert.Equal("1号線-本線", line1.DisplayName);
            Core.Models.Placement.JunctionPlacementNode junc = Assert.Single(imported.Placement.Junctions, j => j.Key == "交差点-N");
            Assert.Equal("北口交差点", junc.DisplayName);
            Assert.Equal("1号線-本線", junc.Origin.SourceNodeId);
            Core.Models.Placement.SplinePlacementNode line2 = Assert.Single(imported.Placement.Splines, s => s.Key == "2号線");
            Assert.Equal("連絡線", line2.DisplayName);
            Assert.Equal("交差点-N", line2.Origin.SourceNodeId);
            Assert.Single(imported.Placement.Connections);

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(result.DestinationWorldInfoPath);
            Assert.DoesNotContain(reloaded.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal("駅前バス停", Assert.Single(reloaded.Project.Placement.Props).DisplayName);
            Assert.Equal("1号線-本線", Assert.Single(reloaded.Project.Placement.Splines, s => s.Key == "1号線-本線").DisplayName);
            Assert.Equal("北口交差点", Assert.Single(reloaded.Project.Placement.Junctions, j => j.Key == "交差点-N").DisplayName);
            Assert.Equal("連絡線", Assert.Single(reloaded.Project.Placement.Splines, s => s.Key == "2号線").DisplayName);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
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
}
