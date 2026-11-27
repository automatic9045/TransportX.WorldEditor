using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Serialization.Csx;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.ViewModels;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.ViewModels.Editors;
using TransportX.WorldEditor.Views.Controls;
using TransportX.WorldEditor.Views.Editors;
using TransportX.WorldEditor.Views.Tools;
using Xunit;

namespace TransportX.WorldEditor.Tests.ViewModels;

public sealed class Phase2ViewModelsTests
{
    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"txwe_p2_{Guid.NewGuid():N}");
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
    public void CreateNewWorld_EditWorldSettingsJapanese_UndoRedoAndSave_RoundTripsCleanly()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "TestAuthor", "YotsubaBus", "Initial Title", "Desc");

            Assert.True(shell.HasOpenProject.Value);
            Assert.DoesNotContain(shell.Workspace.ValidationIssues.Value, i => i.Severity == ValidationSeverity.Error);

            WorldSettingsDocumentViewModel? settings = shell.OpenWorldSettings();
            Assert.NotNull(settings);

            // Japanese UI / MS-IME input simulation
            settings!.WorldTitle.Value = "四葉台バス路線";
            Assert.Equal("四葉台バス路線", project.WorldInfo.Title);
            Assert.True(shell.IsDirty.Value);
            Assert.Contains("*", shell.WindowTitle.Value, StringComparison.Ordinal);

            // Manifest add with Undo/Redo
            settings.NewDependencyPath.Value = "$(plugins)CustomExt.dll";
            settings.AddDependencyCommand.Execute(Unit.Default);
            Assert.Contains("$(plugins)CustomExt.dll", project.Manifest.Dependencies);

            // Init TrafficType addition
            settings.NewTrafficTypeKey.Value = "ExpressBuses";
            settings.NewTrafficTypeName.Value = "急行バス";
            settings.NewTrafficTypeColor.Value = "#0088FF";
            settings.AddTrafficTypeCommand.Execute(Unit.Default);
            Assert.Contains(project.Init.LaneTraffic.Types, t => t.Key == "ExpressBuses");

            // Undo & Redo checks
            Assert.True(shell.CanUndo.Value);
            shell.UndoCommand.Execute(Unit.Default); // undo AddTrafficType
            Assert.DoesNotContain(project.Init.LaneTraffic.Types, t => t.Key == "ExpressBuses");
            Assert.True(shell.CanRedo.Value);
            shell.RedoCommand.Execute(Unit.Default); // redo AddTrafficType
            Assert.Contains(project.Init.LaneTraffic.Types, t => t.Key == "ExpressBuses");

            // Save clears dirty files and persists Japanese title
            shell.SaveCommand.Execute(Unit.Default);
            Assert.False(shell.IsDirty.Value);
            Assert.Empty(project.DirtyFiles);

            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.Equal("四葉台バス路線", reloaded.Project.WorldInfo.Title);
            Assert.Contains("$(plugins)CustomExt.dll", reloaded.Project.Manifest.Dependencies);
            Assert.Contains(reloaded.Project.Init.LaneTraffic.Types, t => t.Key == "ExpressBuses" && t.DisplayName == "急行バス");
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ModelAndSoundListEditors_CrudAndValidationGuards_WorkWithUndoRedo()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AssetsTest", "Assets Test", "");

            AssetBrowserEntry? modelsAsset = shell.AssetBrowserTool.Items.FirstOrDefault(i => i.Kind == AssetBrowserItemKind.ModelList);
            AssetBrowserEntry? soundsAsset = shell.AssetBrowserTool.Items.FirstOrDefault(i => i.Kind == AssetBrowserItemKind.SoundList);
            Assert.NotNull(modelsAsset);
            Assert.NotNull(soundsAsset);

            // Open ModelList tab & reuse same instance on second open
            EditorDocumentBase? doc1 = shell.OpenAsset(modelsAsset!);
            EditorDocumentBase? doc2 = shell.OpenAsset(modelsAsset!);
            Assert.Same(doc1, doc2);

            ModelListEditorDocumentViewModel modelsVm = Assert.IsType<ModelListEditorDocumentViewModel>(doc1);
            modelsVm.AddEntryCommand.Execute(Unit.Default);
            Assert.Single(modelsVm.Entries);
            Assert.NotNull(modelsVm.SelectedRow.Value);

            modelsVm.EntryKey.Value = "BusStopSign";
            modelsVm.SelectedColliderKind.Value = ModelColliderKind.ConvexHull;
            Assert.Contains("$ConvexHull", modelsVm.LiveTsvPreview.Value, StringComparison.Ordinal);

            // NONE model with ConvexHull triggers error banner
            modelsVm.IsNoneModel.Value = true;
            Assert.True(modelsVm.HasNoneModelError.Value);

            modelsVm.IsNoneModel.Value = false;
            modelsVm.ModelPath.Value = "Models/BusStop.glb";
            modelsVm.SelectedColliderKind.Value = ModelColliderKind.ClosedModel;
            modelsVm.CollisionModelPath.Value = "Models/BusStop_COL.glb";
            Assert.True(modelsVm.HasUppercaseCollisionPathWarning.Value);

            // Undo reverts uppercase path / NONE change cleanly
            shell.UndoCommand.Execute(Unit.Default);
            Assert.Single(modelsVm.Entries);

            // SoundList CRUD
            EditorDocumentBase? sdoc = shell.OpenAsset(soundsAsset!);
            SoundListEditorDocumentViewModel soundsVm = Assert.IsType<SoundListEditorDocumentViewModel>(sdoc);
            soundsVm.AddCommand.Execute(Unit.Default);
            Assert.Single(soundsVm.Entries);
            soundsVm.EntryKey.Value = "EngineIdle";
            soundsVm.EntryPath.Value = "Sounds/engine.wav";
            Assert.Single(project.SoundLists.Values.First().Entries);
            Assert.Equal("EngineIdle", project.SoundLists.Values.First().Entries[0].Key);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal("Sounds/Sound_001.wav", project.SoundLists.Values.First().Entries[0].RelativePath);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void EnvironmentAndLaneLayoutEditors_MirrorPreviewAndUndoWork()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "RoadNet", "RoadNet", "");

            AssetBrowserEntry? envAsset = shell.AssetBrowserTool.Items.FirstOrDefault(i => i.Kind == AssetBrowserItemKind.Environment);
            Assert.NotNull(envAsset);
            EnvironmentEditorDocumentViewModel envVm = Assert.IsType<EnvironmentEditorDocumentViewModel>(shell.OpenAsset(envAsset!));

            envVm.IblIntensity.Value = 1.75;
            envVm.BloomIntensity.Value = 0.65;
            EnvironmentProfileDocument envDoc = project.Environments.Values.First();
            Assert.Equal(1.75f, envDoc.Ibl.Intensity, 3);
            Assert.Equal(0.65f, envDoc.Bloom.Intensity, 3);

            // Create new LaneLayout via AssetBrowser
            shell.AssetBrowserTool.NewResourceKey.Value = "BusPriority4";
            shell.AssetBrowserTool.CreateLaneLayoutCommand.Execute(Unit.Default);
            Assert.True(project.LaneLayouts.ContainsKey("BusPriority4"));

            AssetBrowserEntry? layoutEntry = shell.AssetBrowserTool.Items.FirstOrDefault(
                i => i.Kind == AssetBrowserItemKind.LaneLayout && i.Key == "BusPriority4");
            Assert.NotNull(layoutEntry);

            LaneLayoutEditorDocumentViewModel layoutVm = Assert.IsType<LaneLayoutEditorDocumentViewModel>(shell.OpenAsset(layoutEntry!));
            Assert.Equal(4, layoutVm.Lanes.Count);
            Assert.Equal(4, layoutVm.MirrorLanes.Count);

            Assert.True(layoutVm.IsSelfConnectable.Value);
            float originalX = layoutVm.Lanes[1].X;
            layoutVm.SelectLaneIndex(1);
            layoutVm.MoveLaneFromCanvas(1, -2.5f, -0.15f);

            // CreateOpposition() reverses index order (lane #1 -> mirror #2) and negates X
            Assert.Equal(-2.5f, layoutVm.Lanes[1].X, 2);
            Assert.Equal(2.5f, layoutVm.MirrorLanes[2].X, 2);
            Assert.False(layoutVm.IsSelfConnectable.Value);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(originalX, layoutVm.Lanes[1].X, 2);
            Assert.True(layoutVm.IsSelfConnectable.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CrossSectionEditor_VertexNormalDragAndMixedColliderGuard_WorkWithDiagnostics()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "CrossSec", "CrossSec", "");

            shell.AssetBrowserTool.NewResourceKey.Value = "StandardRoad";
            shell.AssetBrowserTool.CreateCrossSectionCommand.Execute(Unit.Default);
            Assert.True(project.CrossSections.ContainsKey("StandardRoad"));

            CrossSectionEditorDocumentViewModel? csVm = shell.OpenCrossSectionEditor("StandardRoad");
            Assert.NotNull(csVm);
            Assert.False(csVm!.HasColliderConflictError.Value);

            // Drag vertex & normal on canvas
            csVm.SelectVertexIndex(0);
            csVm.MoveVertexFromCanvas(0, -4.5f, -0.25f);
            csVm.SetVertexNormalFromCanvas(0, 1.0f, 1.0f);
            CrossSectionDocument csDoc = project.CrossSections["StandardRoad"];
            Assert.Equal(-4.5f, csDoc.Vertices[0].X, 2);
            float normalLen = MathF.Sqrt(csDoc.Vertices[0].NormalX * csDoc.Vertices[0].NormalX + csDoc.Vertices[0].NormalY * csDoc.Vertices[0].NormalY);
            Assert.InRange(normalLen, 0.99f, 1.01f);

            // Add 2nd ColliderMaterial and second Surface using it -> live error banner + DiagnosticsPanel updates
            csVm.AddColliderMaterialCommand.Execute(Unit.Default);
            string secondColName = csVm.ColliderMaterials[1].Name;
            csVm.AddSurfaceCommand.Execute(Unit.Default);
            csVm.SelectedSurface.Value = csVm.Surfaces[1];
            csVm.SurfaceColliderOption.Value = secondColName;

            Assert.True(csVm.HasColliderConflictError.Value);
            Assert.Contains(
                shell.DiagnosticsTool.Items,
                d => d.Message.Contains("CROSS_SECTION_MIXED_COLLIDER_MATERIALS", StringComparison.Ordinal));

            // Undo clears the conflicting collider material assignment
            shell.UndoCommand.Execute(Unit.Default);
            Assert.False(csVm.HasColliderConflictError.Value);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void OpenDefaultModelAndSoundListCommands_AndBasicSampleFallback_LoadAndActivateTabsCleanly()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            shell.CreateAndActivateWorld(tempDir, "Author", "QuickNav", "QuickNav", "");

            shell.OpenModelListCommand.Execute(Unit.Default);
            ModelListEditorDocumentViewModel modelsTab = Assert.IsType<ModelListEditorDocumentViewModel>(
                shell.DockFactory.DocumentDock!.ActiveDockable);
            Assert.Contains("Models.txt", modelsTab.Title, StringComparison.Ordinal);

            shell.OpenSoundListCommand.Execute(Unit.Default);
            SoundListEditorDocumentViewModel soundsTab = Assert.IsType<SoundListEditorDocumentViewModel>(
                shell.DockFactory.DocumentDock!.ActiveDockable);
            Assert.Contains("Sounds.txt", soundsTab.Title, StringComparison.Ordinal);

            // Re-activating existing ModelList switches ActiveDockable back cleanly
            shell.OpenModelListCommand.Execute(Unit.Default);
            Assert.Same(modelsTab, shell.DockFactory.DocumentDock!.ActiveDockable);

            // Empty-state watermark flags follow actual entry count rather than SelectedRow
            Assert.True(modelsTab.HasNoEntries.Value);
            modelsTab.AddEntryCommand.Execute(Unit.Default);
            Assert.False(modelsTab.HasNoEntries.Value);
            modelsTab.RemoveEntryCommand.Execute(Unit.Default);
            Assert.True(modelsTab.HasNoEntries.Value);

            Assert.True(soundsTab.HasNoEntries.Value);
            soundsTab.AddCommand.Execute(Unit.Default);
            Assert.False(soundsTab.HasNoEntries.Value);
            soundsTab.RemoveCommand.Execute(Unit.Default);
            Assert.True(soundsTab.HasNoEntries.Value);

            // Opening BasicSample (hand-written scripts without <editor-generated> markers)
            string? repoRoot = FindRepoRoot();
            if (repoRoot is not null)
            {
                string basicSampleXml = Path.Combine(
                    repoRoot,
                    "external",
                    "TransportX",
                    "_out",
                    "Data",
                    "Automatic9045.BasicSample.xml");

                if (File.Exists(basicSampleXml))
                {
                    WorldProjectLoadResult sampleLoad = shell.OpenWorldFromPath(basicSampleXml);
                    WorldProject sample = sampleLoad.Project;

                    Assert.True(sample.IsProtected("World.csx"));
                    Assert.True(sample.IsProtected("Init.csx"));
                    Assert.NotEmpty(sample.ModelLists);
                    Assert.NotEmpty(sample.LaneLayouts);
                    Assert.NotEmpty(sample.Environments);
                    Assert.NotEmpty(sample.Placement.Props);
                    Assert.Contains(shell.AssetBrowserTool.Items, i => i.Kind == AssetBrowserItemKind.ModelList);

                    ModelListEditorDocumentViewModel? sampleModels = shell.OpenDefaultModelList();
                    Assert.NotNull(sampleModels);
                    Assert.NotEmpty(sampleModels!.Entries);
                    Assert.False(sampleModels.HasNoEntries.Value);
                    Assert.Same(sampleModels, shell.DockFactory.DocumentDock!.ActiveDockable);
                }
            }
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void ViewLocator_DoesNotImplementRecyclingTemplate_AndMatchesAllDockViewModels()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            shell.CreateAndActivateWorld(tempDir, "Author", "LocatorWorld", "Locator", "");

            ViewLocator locator = new ViewLocator();
            Assert.IsNotAssignableFrom<Avalonia.Controls.Templates.IRecyclingDataTemplate>(locator);

            Assert.True(locator.Match(shell.ViewportDocument));
            Assert.True(locator.Match(shell.SceneHierarchyTool));
            Assert.True(locator.Match(shell.AssetBrowserTool));
            Assert.True(locator.Match(shell.InspectorTool));
            Assert.True(locator.Match(shell.DiagnosticsTool));
            Assert.True(locator.Match(new TimelineToolViewModel()));

            ModelListEditorDocumentViewModel? models = shell.OpenDefaultModelList();
            SoundListEditorDocumentViewModel? sounds = shell.OpenDefaultSoundList();
            CrossSectionListEditorDocumentViewModel? csList = shell.OpenDefaultCrossSectionList();
            EnvironmentEditorDocumentViewModel? env = shell.OpenDefaultEnvironment();
            WorldSettingsDocumentViewModel? settings = shell.OpenWorldSettings();

            Assert.NotNull(models);
            Assert.NotNull(sounds);
            Assert.NotNull(csList);
            Assert.NotNull(env);
            Assert.NotNull(settings);

            Assert.True(locator.Match(models));
            Assert.True(locator.Match(sounds));
            Assert.True(locator.Match(csList));
            Assert.True(locator.Match(env));
            Assert.True(locator.Match(settings));
            Assert.False(locator.Match("unexpected"));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AssetBrowser_SeparatesTabs_CreatesMultipleLists_AndDeletesWithUndoAndTabClose()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new MainWindowViewModel();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AssetTabsWorld", "AssetTabs", "");
            AssetBrowserToolViewModel browser = shell.AssetBrowserTool;

            Assert.Equal(2, browser.BasicItems.Count); // WorldSettings + Environment
            Assert.Single(browser.ModelListItems);
            Assert.Single(browser.SoundListItems);
            Assert.Single(browser.CrossSectionListItems);
            Assert.Single(browser.LaneLayoutItems);

            // Creating an extra ModelList via Tab 1 (モデル)
            browser.SelectedTabIndex.Value = 1;
            Assert.True(browser.CanCreateForActiveTab.Value);
            browser.NewResourceKey.Value = "Models_Sub";
            browser.CreateActiveResourceCommand.Execute(Unit.Default);

            Assert.Equal(2, project.ModelLists.Count);
            Assert.Equal(2, browser.ModelListItems.Count);
            ModelListEditorDocumentViewModel subDoc = Assert.IsType<ModelListEditorDocumentViewModel>(
                shell.DockFactory.DocumentDock!.ActiveDockable);
            Assert.Equal("Doc_Models_Models_Sub.txt", subDoc.Id);

            // WorldSettings cannot be deleted
            AssetBrowserEntry wsEntry = Assert.Single(browser.BasicItems, i => i.Kind == AssetBrowserItemKind.WorldSettings);
            browser.SelectedItem.Value = wsEntry;
            Assert.False(browser.CanDeleteSelected.Value);

            // Deleting Models_Sub.txt closes its open tab and supports Undo
            AssetBrowserEntry subEntry = Assert.Single(browser.ModelListItems, i => i.RelativePath == "Models_Sub.txt");
            browser.SelectedItem.Value = subEntry;
            Assert.True(browser.CanDeleteSelected.Value);
            browser.DeleteSelectedCommand.Execute(Unit.Default);

            Assert.Single(project.ModelLists);
            Assert.Single(browser.ModelListItems);
            Assert.DoesNotContain(
                shell.DockFactory.DocumentDock!.VisibleDockables!,
                d => string.Equals(d.Id, "Doc_Models_Models_Sub.txt", StringComparison.Ordinal));

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(2, project.ModelLists.Count);
            Assert.Equal(2, browser.ModelListItems.Count);

            // Deleting LaneLayout works with Undo
            AssetBrowserEntry layoutEntry = Assert.Single(browser.LaneLayoutItems);
            browser.SelectedItem.Value = layoutEntry;
            Assert.True(browser.CanDeleteSelected.Value);
            browser.DeleteSelectedCommand.Execute(Unit.Default);
            Assert.Empty(project.LaneLayouts);
            Assert.Empty(browser.LaneLayoutItems);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Single(project.LaneLayouts);
            Assert.Single(browser.LaneLayoutItems);

            // Create a 2nd LaneLayout, edit and delete both, then SaveAll -> DirtyFiles and '*' must clear
            browser.SelectedTabIndex.Value = 3;
            browser.NewResourceKey.Value = "Layout2";
            browser.CreateActiveResourceCommand.Execute(Unit.Default);
            shell.SaveAllCommand.Execute(Unit.Default);
            Assert.Empty(project.DirtyFiles);
            Assert.Empty(shell.InspectorTool.DirtyFilesList);
            Assert.False(shell.IsDirty.Value);

            foreach (AssetBrowserEntry entry in browser.LaneLayoutItems.ToList())
            {
                browser.SelectedItem.Value = entry;
                browser.DeleteSelectedCommand.Execute(Unit.Default);
            }

            Assert.True(shell.IsDirty.Value);
            Assert.NotEmpty(shell.InspectorTool.DirtyFilesList);
            Assert.Contains("*", shell.WindowTitle.Value, StringComparison.Ordinal);

            shell.SaveAllCommand.Execute(Unit.Default);
            Assert.Empty(project.DirtyFiles);
            Assert.Empty(shell.InspectorTool.DirtyFilesList);
            Assert.False(shell.IsDirty.Value);
            Assert.DoesNotContain("*", shell.WindowTitle.Value, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Editors_TypingKeystrokesPreservesSelectionAndSpaces_AndSyncsMetallicRoughnessNumeric()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "EditFocusWorld", "EditFocus", "");

            ModelListEditorDocumentViewModel? models = shell.OpenDefaultModelList();
            Assert.NotNull(models);
            models!.AddEntryCommand.Execute(Unit.Default);
            ModelListRowViewModel? initialRow = models.SelectedRow.Value;
            Assert.NotNull(initialRow);

            List<bool> modelHasSelectionLog = new();
            using IDisposable subModel = models.HasSelection.Subscribe(v => modelHasSelectionLog.Add(v));

            models.EntryKey.Value = "Bus";
            models.EntryKey.Value = "Bus ";
            models.EntryKey.Value = "Bus Pole";

            Assert.DoesNotContain(false, modelHasSelectionLog);
            Assert.Same(initialRow, models.SelectedRow.Value);
            Assert.Equal("Bus Pole", models.SelectedRow.Value!.Key);
            Assert.Equal("Bus Pole", project.ModelLists["Models.txt"].Entries[0].Key);

            models.EntryKey.Value = "Bus Pole ";
            Assert.Equal("Bus Pole ", models.EntryKey.Value);

            SoundListEditorDocumentViewModel? sounds = shell.OpenDefaultSoundList();
            Assert.NotNull(sounds);
            sounds!.AddCommand.Execute(Unit.Default);
            SoundListRowViewModel? soundRow = sounds.SelectedRow.Value;
            Assert.NotNull(soundRow);

            List<bool> soundHasSelectionLog = new();
            using IDisposable subSound = sounds.HasSelection.Subscribe(v => soundHasSelectionLog.Add(v));
            sounds.EntryKey.Value = "Horn ";
            sounds.EntryKey.Value = "Horn A";
            Assert.DoesNotContain(false, soundHasSelectionLog);
            Assert.Same(soundRow, sounds.SelectedRow.Value);
            Assert.Equal("Horn A", project.SoundLists["Sounds.txt"].Entries[0].Key);

            CrossSectionEditorDocumentViewModel? csVm = shell.OpenCrossSectionEditor("Road1L");
            Assert.NotNull(csVm);
            CrossSectionVertexRow? vertexRow = csVm!.SelectedVertex.Value;
            Assert.NotNull(vertexRow);

            List<bool> csVertexSelLog = new();
            using IDisposable subVertex = csVm.HasSelectedVertex.Subscribe(v => csVertexSelLog.Add(v));
            csVm.VertexKey.Value = "v ";
            csVm.VertexKey.Value = "v curb";
            Assert.DoesNotContain(false, csVertexSelLog);
            Assert.Same(vertexRow, csVm.SelectedVertex.Value);
            Assert.Equal("v curb", project.CrossSections["Road1L"].Vertices[0].Key);

            // Metallic & Roughness numeric inputs stay bidirectionally synced with sliders + Undo/Redo
            CrossSectionMaterialModel? mat = csVm.SelectedMaterial.Value;
            Assert.NotNull(mat);
            float initialRoughness = mat!.Roughness;
            csVm.MaterialMetallicNumeric.Value = 0.65m;
            Assert.Equal(0.65, csVm.MaterialMetallic.Value, 3);
            Assert.Equal(0.65f, mat.Metallic, 3);

            csVm.MaterialRoughness.Value = 0.25;
            Assert.Equal(0.25m, csVm.MaterialRoughnessNumeric.Value);
            Assert.Equal(0.25f, mat.Roughness, 3);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal((decimal)double.Round(initialRoughness, 3), csVm.MaterialRoughnessNumeric.Value);
            Assert.Equal(initialRoughness, csVm.MaterialRoughness.Value, 3);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AvaloniaUi_TextBoxFocusPreserved_TabItemAndNumericUpDownCompact_UserCodeUsesSyntaxColoredEditor()
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
                WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "AvaloniaUiWorld", "UI", "");

                ModelListEditorDocumentViewModel? modelsVm = shell.OpenDefaultModelList();
                Assert.NotNull(modelsVm);
                modelsVm!.AddEntryCommand.Execute(Unit.Default);

                ModelListEditorView modelView = new()
                {
                    DataContext = modelsVm,
                };

                Window host = new()
                {
                    Width = 960,
                    Height = 640,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Position = new PixelPoint(-20000, -20000),
                    Content = modelView,
                };

                host.Show();
                Dispatcher.UIThread.RunJobs();

                TextBox? entryKeyBox = modelView.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(tb => tb.Text == modelsVm.EntryKey.Value);
                Assert.NotNull(entryKeyBox);

                entryKeyBox!.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused, "Expected EntryKey TextBox to be focused initially.");

                // Simulate incremental keystroke typing inside live TextBox
                entryKeyBox.Text = "A";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused, "Expected EntryKey TextBox to stay focused after 'A'.");

                entryKeyBox.Text = "AB ";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused, "Expected EntryKey TextBox to stay focused after 'AB '.");
                Assert.Equal("AB ", entryKeyBox.Text);

                entryKeyBox.Text = "AB 1";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused, "Expected EntryKey TextBox to stay focused after 'AB 1'.");
                Assert.Equal("AB 1", project.ModelLists["Models.txt"].Entries[0].Key);

                // Verify CrossSectionEditorView TabItem FontSize & compact NumericUpDown spinner RepeatButton width
                CrossSectionEditorDocumentViewModel? csVm = shell.OpenCrossSectionEditor("Road1L");
                Assert.NotNull(csVm);
                CrossSectionEditorView csView = new()
                {
                    DataContext = csVm,
                };
                host.Content = csView;
                Dispatcher.UIThread.RunJobs();

                TabItem? surfaceTab = csView.GetVisualDescendants().OfType<TabItem>().FirstOrDefault();
                Assert.NotNull(surfaceTab);
                Assert.True(surfaceTab!.FontSize <= 14.0, $"Expected TabItem FontSize <= 14, actual={surfaceTab.FontSize}");

                RepeatButton? spinnerBtn = csView.GetVisualDescendants()
                    .OfType<NumericUpDown>()
                    .SelectMany(static n => n.GetVisualDescendants().OfType<RepeatButton>())
                    .FirstOrDefault(static b => 0 < b.Bounds.Width);
                Assert.NotNull(spinnerBtn);
                Assert.True(
                    spinnerBtn!.Bounds.Width <= 18.0,
                    $"Expected RepeatButton width <= 18, actual={spinnerBtn.Bounds.Width}");

                if (spinnerBtn.Content is Control spinnerGlyph)
                {
                    Assert.True(
                        spinnerGlyph.Bounds.Width <= 9.0 && spinnerGlyph.Bounds.Height <= 9.0,
                        $"Expected compact spinner icon <= 9x9, actual={spinnerGlyph.Bounds.Width}x{spinnerGlyph.Bounds.Height}");
                }

                // Verify syntax-highlighted CSharpCodeEditor on all user-code views
                WorldSettingsDocumentViewModel? wsVm = shell.OpenWorldSettings();
                WorldSettingsView wsView = new() { DataContext = wsVm };
                host.Content = wsView;
                Dispatcher.UIThread.RunJobs();
                CSharpCodeEditor? initCodeEditor = wsView.GetVisualDescendants()
                    .OfType<CSharpCodeEditor>()
                    .LastOrDefault();
                Assert.NotNull(initCodeEditor);

                AssetBrowserEntry stEntry = Assert.Single(shell.AssetBrowserTool.SplineTemplateItems);
                SplineTemplateEditorDocumentViewModel stVm = Assert.IsType<SplineTemplateEditorDocumentViewModel>(
                    shell.OpenAsset(stEntry));
                SplineTemplateEditorView stView = new() { DataContext = stVm };
                host.Content = stView;
                Dispatcher.UIThread.RunJobs();
                List<TabItem> stTabs = stView.GetVisualDescendants().OfType<TabItem>().ToList();
                Assert.Equal(3, stTabs.Count);
                stTabs[2].IsSelected = true;
                Dispatcher.UIThread.RunJobs();
                CSharpCodeEditor? tplCodeEditor = stView.GetVisualDescendants()
                    .OfType<CSharpCodeEditor>()
                    .FirstOrDefault();
                Assert.NotNull(tplCodeEditor);

                // Verify InspectorView rotation labels, deferred >= 250m normalization, and MS Gothic PreBuild box
                shell.Workspace.AddPropPlacement(
                    "DefaultProp",
                    new WorldPoseDegreesModel(ChunkIndexModel.Zero, PoseDegreesModel.FromTranslation(10.0, 0.0, 125.0)));

                InspectorView inspView = new() { DataContext = shell.InspectorTool };
                host.Content = inspView;
                Dispatcher.UIThread.RunJobs();

                List<TextBlock> rotLabels = inspView.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Where(static tb => tb.Text is not null && tb.Text.Contains("RotationX", StringComparison.Ordinal))
                    .ToList();
                Assert.NotEmpty(rotLabels);
                Assert.DoesNotContain(
                    inspView.GetVisualDescendants().OfType<TextBlock>(),
                    static tb => tb.Text is not null && tb.Text.Contains("Pitch", StringComparison.Ordinal));

                NumericUpDown? propLocalXNum = inspView.GetVisualDescendants()
                    .OfType<NumericUpDown>()
                    .FirstOrDefault(static n => !n.ShowButtonSpinner && n.Value == 10m);
                Assert.NotNull(propLocalXNum);
                TextBox? propLocalXTextBox = propLocalXNum!.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                Assert.NotNull(propLocalXTextBox);

                propLocalXTextBox!.Focus();
                Dispatcher.UIThread.RunJobs();

                // Typing "2", "26", "265" must NOT roll chunks repeatedly mid-edit
                propLocalXTextBox.Text = "2";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, project.Placement.Props[0].Chunk.X);

                propLocalXTextBox.Text = "26";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, project.Placement.Props[0].Chunk.X);

                propLocalXTextBox.Text = "265";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, project.Placement.Props[0].Chunk.X);

                // Committing on LostFocus / Enter normalizes ONCE (ChunkX=0 -> ChunkX=1, LocalX=15)
                NumericUpDownCommitBehavior.CommitSource(propLocalXNum);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, project.Placement.Props[0].Chunk.X);
                Assert.Equal(15.0, project.Placement.Props[0].Pose.X, 3);

                CSharpCodeEditor? inspCodeEditor = inspView.GetVisualDescendants()
                    .OfType<CSharpCodeEditor>()
                    .FirstOrDefault();
                Assert.NotNull(inspCodeEditor);

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
    public void BackgroundModels_PlaceEditReorderDelete_SyncsSceneHierarchyInspectorAndWorldCsx()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "BgWorld", "Background Test", "");

            project.ModelLists["Models.txt"].Entries.Add(new ModelListEntry
            {
                Key = "SkyBackdrop",
                ModelPath = "Models/Sky.glb",
                Collider = ModelColliderSpec.NonCollision(),
            });
            project.ModelLists["Models.txt"].Entries.Add(new ModelListEntry
            {
                Key = "MountainsBg",
                ModelPath = "Models/Mountains.glb",
                Collider = ModelColliderSpec.NonCollision(),
            });
            shell.Workspace.ProjectRevision.Value++;

            shell.Workspace.PlacementModelKey.Value = "SkyBackdrop";
            shell.ViewportDocument.PlaceBackgroundCommand.Execute(Unit.Default);
            Assert.Single(project.Placement.BackgroundModels);
            Assert.Equal("SkyBackdrop", project.Placement.BackgroundModels[0]);
            Assert.Equal(0, shell.Workspace.SelectedBackgroundModelIndex.Value);
            Assert.True(shell.InspectorTool.HasSelectedBackground.Value);
            Assert.True(shell.InspectorTool.HasAnySelection.Value);

            shell.Workspace.PlacementModelKey.Value = "MountainsBg";
            shell.InspectorTool.AddNewBackgroundCommand.Execute(Unit.Default);
            Assert.Equal(2, project.Placement.BackgroundModels.Count);
            Assert.Equal(1, shell.Workspace.SelectedBackgroundModelIndex.Value);

            List<SceneItemEntry> bgSceneEntries = shell.SceneHierarchyTool.Items
                .Where(static i => i.Category == "背景")
                .ToList();
            Assert.Equal(2, bgSceneEntries.Count);

            // Reorder #1 -> #0
            shell.InspectorTool.MoveBackgroundUpCommand.Execute(Unit.Default);
            Assert.Equal(new[] { "MountainsBg", "SkyBackdrop" }, project.Placement.BackgroundModels);
            Assert.Equal(0, shell.Workspace.SelectedBackgroundModelIndex.Value);

            // Edit key at #0
            shell.InspectorTool.SelectedBackgroundModelKey.Value = "SkyBackdrop";
            Assert.Equal(new[] { "SkyBackdrop", "SkyBackdrop" }, project.Placement.BackgroundModels);

            // Save and reload World.csx -> Background.Add(...) persists
            shell.SaveAllCommand.Execute(Unit.Default);
            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.Equal(new[] { "SkyBackdrop", "SkyBackdrop" }, reloaded.Project.Placement.BackgroundModels);

            // Delete #0 and Undo
            shell.InspectorTool.DeleteSelectedBackgroundCommand.Execute(Unit.Default);
            Assert.Single(project.Placement.BackgroundModels);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(2, project.Placement.BackgroundModels.Count);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void Editors_MoveUpMoveDownReordering_WorksWithUndoRedo()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "ReorderWorld", "Reorder", "");

            ModelListEditorDocumentViewModel? models = shell.OpenDefaultModelList();
            Assert.NotNull(models);
            models!.AddEntryCommand.Execute(Unit.Default);
            models.AddEntryCommand.Execute(Unit.Default);
            string m1 = project.ModelLists["Models.txt"].Entries[0].Key;
            string m2 = project.ModelLists["Models.txt"].Entries[1].Key;

            models.SelectedRow.Value = models.Entries[1];
            models.MoveUpCommand.Execute(Unit.Default);
            Assert.Equal(m2, project.ModelLists["Models.txt"].Entries[0].Key);
            Assert.Equal(m1, project.ModelLists["Models.txt"].Entries[1].Key);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(m1, project.ModelLists["Models.txt"].Entries[0].Key);
            Assert.Equal(m2, project.ModelLists["Models.txt"].Entries[1].Key);

            SoundListEditorDocumentViewModel? sounds = shell.OpenDefaultSoundList();
            Assert.NotNull(sounds);
            sounds!.AddCommand.Execute(Unit.Default);
            sounds.AddCommand.Execute(Unit.Default);
            string s1 = project.SoundLists["Sounds.txt"].Entries[0].Key;
            string s2 = project.SoundLists["Sounds.txt"].Entries[1].Key;

            sounds.SelectedRow.Value = sounds.Entries[1];
            sounds.MoveUpCommand.Execute(Unit.Default);
            Assert.Equal(s2, project.SoundLists["Sounds.txt"].Entries[0].Key);
            Assert.Equal(s1, project.SoundLists["Sounds.txt"].Entries[1].Key);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(s1, project.SoundLists["Sounds.txt"].Entries[0].Key);
            Assert.Equal(s2, project.SoundLists["Sounds.txt"].Entries[1].Key);

            CrossSectionEditorDocumentViewModel? csVm = shell.OpenCrossSectionEditor("Road1L");
            Assert.NotNull(csVm);
            CrossSectionDocument csDoc = project.CrossSections["Road1L"];
            string v0 = csDoc.Vertices[0].Key;
            string v1 = csDoc.Vertices[1].Key;

            csVm.SelectVertexIndex(1);
            csVm.MoveVertexUpCommand.Execute(Unit.Default);
            Assert.Equal(v1, csDoc.Vertices[0].Key);
            Assert.Equal(v0, csDoc.Vertices[1].Key);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(v0, csDoc.Vertices[0].Key);
            Assert.Equal(v1, csDoc.Vertices[1].Key);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AssetBrowser_ImportsExistingFilesOutsideAndInsideWorld_ForAllSupportedAssetKinds()
    {
        string worldTempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string extModels = Path.Combine(externalDir, "Models_Custom.txt");
            File.WriteAllText(extModels, "BusStop_A\tModels/BusStopA.glb\t$NonCollision\n");

            string extSounds = Path.Combine(externalDir, "Sounds_Custom.txt");
            File.WriteAllText(extSounds, "DoorBell\tSounds/Bell.wav\n");

            string extCrossSectionsList = Path.Combine(externalDir, "CrossSections_Custom.txt");
            File.WriteAllText(extCrossSectionsList, "CustomListRoad\tCrossSections/CustomListRoad.xml\n");

            string extLayout = Path.Combine(externalDir, "CustomArterial.xml");
            File.WriteAllText(extLayout, """
                <?xml version="1.0" encoding="utf-8"?>
                <LaneLayout>
                  <Lane AllowedTrafficKey="AllTraffic" Directions="InOut" X="0" Y="0" LeftWidth="2.5" RightWidth="2.5" />
                </LaneLayout>
                """);

            string extCross = Path.Combine(externalDir, "CurbSidewalk.xml");
            File.WriteAllText(extCross, """
                <?xml version="1.0" encoding="utf-8"?>
                <CrossSection>
                  <Materials>
                    <Material Name="Asphalt">
                      <BaseColor>#404040</BaseColor>
                      <Metallic>0</Metallic>
                      <Roughness>0.8</Roughness>
                    </Material>
                  </Materials>
                  <Vertices>
                    <Vertex Key="L" X="-2" Y="0" U="0" />
                    <Vertex Key="R" X="2" Y="0" U="1" />
                  </Vertices>
                  <Surfaces>
                    <Surface MaterialName="Asphalt">
                      <Vertex Key="L" />
                      <Vertex Key="R" />
                    </Surface>
                  </Surfaces>
                </CrossSection>
                """);

            string extSpline = Path.Combine(externalDir, "RoadTemplateExt.csx");
            SplineTemplateDocument extSplineDoc = new()
            {
                Key = "RoadTemplateExt",
                RelativePath = "Templates/Splines/RoadTemplateExt.csx",
                OutletLayoutKey = "Layout1",
                UserCode = "// custom imported code",
            };
            extSplineDoc.Extrusions.Add(new SplineTemplateExtrusionRule
            {
                Key = "Surface",
                CrossSectionKey = "CurbSidewalk",
                OffsetX = 0,
                OffsetY = 0,
                BaseInterval = 5,
                MaxDeviation = 0.01,
                MaxAngleDeltaDeg = 2,
                VIntervalScale = 1,
                VOffset = 0,
                From = 0,
            });
            File.WriteAllText(extSpline, SplineTemplateCsxSerializer.Generate(extSplineDoc));

            string extEnv = Path.Combine(externalDir, "Environment_Night.xml");
            File.WriteAllText(extEnv, """
                <?xml version="1.0" encoding="utf-8"?>
                <EnvironmentProfile DiffuseTexturePath="SkyNight.dds">
                  <IBL>
                    <Intensity>0.8</Intensity>
                    <Saturation>0.25</Saturation>
                  </IBL>
                  <Exposure>
                    <Key>0.12</Key>
                    <Min>0.01</Min>
                    <Max>5</Max>
                    <DarkAdaptationSpeed>0.5</DarkAdaptationSpeed>
                    <LightAdaptationSpeed>1.5</LightAdaptationSpeed>
                  </Exposure>
                  <ToneMap>
                    <Contrast>1.5</Contrast>
                    <Shoulder>0.977</Shoulder>
                    <MaxLuminance>12</MaxLuminance>
                    <MidtoneScale>1.5</MidtoneScale>
                  </ToneMap>
                  <Bloom>
                    <Threshold>2</Threshold>
                    <Intensity>0.2</Intensity>
                    <Scatter>1</Scatter>
                    <SoftKnee>0.5</SoftKnee>
                    <Tint>#ffffff</Tint>
                  </Bloom>
                </EnvironmentProfile>
                """);

            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(worldTempDir, "Author", "ImportWorld", "Import Test", "");
            AssetBrowserToolViewModel browser = shell.AssetBrowserTool;

            Assert.True(browser.ImportExistingFromFile(1, extModels));
            Assert.True(project.ModelLists.ContainsKey("Models_Custom.txt"));
            Assert.True(File.Exists(Path.Combine(project.WorldDirectory, "Models_Custom.txt")));

            // Re-importing the already registered internal file must not duplicate
            Assert.False(browser.ImportExistingFromFile(1, Path.Combine(project.WorldDirectory, "Models_Custom.txt")));

            Assert.True(browser.ImportExistingFromFile(2, extSounds));
            Assert.True(project.SoundLists.ContainsKey("Sounds_Custom.txt"));

            Assert.True(browser.ImportExistingFromFile(3, extLayout));
            Assert.True(project.LaneLayouts.ContainsKey("CustomArterial"));

            Assert.True(browser.ImportExistingFromFile(4, extCrossSectionsList));
            Assert.True(project.CrossSectionLists.ContainsKey("CrossSections_Custom.txt"));
            Assert.False(browser.ImportExistingFromFile(4, Path.Combine(project.WorldDirectory, "CrossSections_Custom.txt")));

            CrossSectionListEditorDocumentViewModel? csListDoc = shell.OpenDefaultCrossSectionList();
            Assert.NotNull(csListDoc);
            Assert.Equal(1, csListDoc!.ImportXmlFiles(new string[] { extCross }));
            Assert.True(project.CrossSections.ContainsKey("CurbSidewalk"));

            Assert.True(browser.ImportExistingFromFile(5, extSpline));
            Assert.True(project.SplineTemplates.ContainsKey("RoadTemplateExt"));

            Assert.True(browser.ImportExistingFromFile(0, extEnv));
            Assert.Equal("Environment_Night.xml", project.Placement.EnvironmentPath);

            // Undo & Redo works on imported assets
            shell.UndoCommand.Execute(Unit.Default);
            Assert.NotEqual("Environment_Night.xml", project.Placement.EnvironmentPath);
            shell.RedoCommand.Execute(Unit.Default);
            Assert.Equal("Environment_Night.xml", project.Placement.EnvironmentPath);

            // Multi-file import via ImportXmlFiles derives distinct keys
            string extCrossA = Path.Combine(externalDir, "BatchCurbA.xml");
            string extCrossB = Path.Combine(externalDir, "BatchCurbB.xml");
            string csXmlTemplate = File.ReadAllText(extCross);
            File.WriteAllText(extCrossA, csXmlTemplate);
            File.WriteAllText(extCrossB, csXmlTemplate);
            int batchImported = csListDoc.ImportXmlFiles(new string[] { extCrossA, extCrossB });
            Assert.Equal(2, batchImported);
            Assert.True(project.CrossSections.ContainsKey("BatchCurbA"));
            Assert.True(project.CrossSections.ContainsKey("BatchCurbB"));

            // Save & Reload round-trips imported assets
            shell.SaveAllCommand.Execute(Unit.Default);
            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.True(reloaded.Project.ModelLists.ContainsKey("Models_Custom.txt"));
            Assert.True(reloaded.Project.SoundLists.ContainsKey("Sounds_Custom.txt"));
            Assert.True(reloaded.Project.CrossSectionLists.ContainsKey("CrossSections_Custom.txt"));
            Assert.True(reloaded.Project.LaneLayouts.ContainsKey("CustomArterial"));
            Assert.True(reloaded.Project.CrossSections.ContainsKey("CurbSidewalk"));
            Assert.True(reloaded.Project.CrossSections.ContainsKey("BatchCurbA"));
            Assert.True(reloaded.Project.CrossSections.ContainsKey("BatchCurbB"));
            Assert.True(reloaded.Project.SplineTemplates.ContainsKey("RoadTemplateExt"));
            Assert.Equal("Environment_Night.xml", reloaded.Project.Placement.EnvironmentPath);
        }
        finally
        {
            DeleteTempDirectory(worldTempDir);
            DeleteTempDirectory(externalDir);
        }
    }

    [Fact]
    public void DocumentTabs_ShowDirtyAsterisk_AndClearAfterSave()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "DirtyTabTest", "Dirty Tabs", "Desc");

            ViewportDocumentViewModel viewportDoc = shell.ViewportDocument;
            WorldSettingsDocumentViewModel? settingsDoc = shell.OpenWorldSettings();
            ModelListEditorDocumentViewModel? modelsDoc = shell.OpenDefaultModelList();
            SoundListEditorDocumentViewModel? soundsDoc = shell.OpenDefaultSoundList();
            CrossSectionListEditorDocumentViewModel? csListDoc = shell.OpenDefaultCrossSectionList();
            EnvironmentEditorDocumentViewModel? envDoc = shell.OpenDefaultEnvironment();

            AssetBrowserEntry? laneEntry = shell.AssetBrowserTool.LaneLayoutItems.FirstOrDefault();
            AssetBrowserEntry? tplEntry = shell.AssetBrowserTool.SplineTemplateItems.FirstOrDefault();
            Assert.NotNull(settingsDoc);
            Assert.NotNull(modelsDoc);
            Assert.NotNull(soundsDoc);
            Assert.NotNull(csListDoc);
            Assert.NotNull(envDoc);
            Assert.NotNull(laneEntry);
            Assert.NotNull(tplEntry);

            LaneLayoutEditorDocumentViewModel? laneDoc = shell.OpenAsset(laneEntry) as LaneLayoutEditorDocumentViewModel;
            CrossSectionEditorDocumentViewModel? csDoc = shell.OpenCrossSectionEditor("Road1L");
            SplineTemplateEditorDocumentViewModel? tplDoc = shell.OpenAsset(tplEntry) as SplineTemplateEditorDocumentViewModel;
            Assert.NotNull(laneDoc);
            Assert.NotNull(csDoc);
            Assert.NotNull(tplDoc);

            Assert.DoesNotContain("*", viewportDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", settingsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", modelsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", soundsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", csListDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", envDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", laneDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", csDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", tplDoc.Title, StringComparison.Ordinal);

            settingsDoc.WorldTitle.Value = "Dirty World Title";
            modelsDoc.AddEntryCommand.Execute(Unit.Default);
            soundsDoc.AddCommand.Execute(Unit.Default);
            csListDoc.AddEntryCommand.Execute(Unit.Default);
            envDoc.ExposureKey.Value = 0.25m;
            laneDoc.AddLaneCommand.Execute(Unit.Default);
            csDoc.AddVertexCommand.Execute(Unit.Default);
            tplDoc.AddExtrusionCommand.Execute(Unit.Default);
            shell.Workspace.AddBackgroundModel("dummy_bg");

            Assert.EndsWith(" *", viewportDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", settingsDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", modelsDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", soundsDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", csListDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", envDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", laneDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", csDoc.Title, StringComparison.Ordinal);
            Assert.EndsWith(" *", tplDoc.Title, StringComparison.Ordinal);

            shell.SaveAllCommand.Execute(Unit.Default);

            Assert.DoesNotContain("*", viewportDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", settingsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", modelsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", soundsDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", csListDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", envDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", laneDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", csDoc.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("*", tplDoc.Title, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void CrossSectionListEditor_CrudDuplicateMoveOpenAndUndoRedo_SyncProjectAndTsv()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            using MainWindowViewModel shell = new();
            WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "CsListWorld", "CsListWorld", "");

            AssetBrowserEntry csListAsset = Assert.Single(shell.AssetBrowserTool.CrossSectionListItems);
            CrossSectionListEditorDocumentViewModel csListVm = Assert.IsType<CrossSectionListEditorDocumentViewModel>(
                shell.OpenAsset(csListAsset));
            Assert.Single(csListVm.Entries);
            Assert.Equal("Road1L\tCrossSections/Road1L.xml", csListVm.LiveTsvPreview.Value);

            // Add new CrossSection entry + verify default CrossSectionDocument creation & Undo/Redo
            csListVm.AddEntryCommand.Execute(Unit.Default);
            Assert.Equal(2, csListVm.Entries.Count);
            string addedKey = csListVm.SelectedRow.Value!.Key;
            Assert.True(project.CrossSections.ContainsKey(addedKey));
            Assert.Equal($"{addedKey}\tCrossSections/{addedKey}.xml", csListVm.LiveTsvPreview.Value);

            // Open 2D CrossSectionEditor directly from list
            csListVm.OpenCrossSectionEditorCommand.Execute(Unit.Default);
            CrossSectionEditorDocumentViewModel openedCsVm = Assert.IsType<CrossSectionEditorDocumentViewModel>(
                shell.DockFactory.DocumentDock!.ActiveDockable);
            Assert.Equal(addedKey, openedCsVm.CrossSectionKey);

            // Rename key & check re-keying in project.CrossSections + LiveTsvPreview
            csListVm.EntryKey.Value = "CustomCurb";
            Assert.True(project.CrossSections.ContainsKey("CustomCurb"));
            Assert.False(project.CrossSections.ContainsKey(addedKey));
            Assert.Equal($"CustomCurb\tCrossSections/{addedKey}.xml", csListVm.LiveTsvPreview.Value);

            // Duplicate entry
            csListVm.DuplicateEntryCommand.Execute(Unit.Default);
            Assert.Equal(3, csListVm.Entries.Count);
            Assert.Equal("CustomCurb_Copy", csListVm.Entries[2].Key);
            Assert.True(project.CrossSections.ContainsKey("CustomCurb_Copy"));

            // MoveUp + Undo
            csListVm.SelectedRow.Value = csListVm.Entries[2];
            csListVm.MoveUpCommand.Execute(Unit.Default);
            Assert.Equal("CustomCurb_Copy", project.CrossSectionLists["CrossSections.txt"].Entries[1].Key);
            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal("CustomCurb_Copy", project.CrossSectionLists["CrossSections.txt"].Entries[2].Key);

            // Create an extra list via Tab 4 & delete with Undo
            shell.AssetBrowserTool.SelectedTabIndex.Value = 4;
            shell.AssetBrowserTool.NewResourceKey.Value = "CrossSections_Extra";
            shell.AssetBrowserTool.CreateActiveResourceCommand.Execute(Unit.Default);
            Assert.Equal(2, project.CrossSectionLists.Count);
            Assert.Equal(2, shell.AssetBrowserTool.CrossSectionListItems.Count);

            AssetBrowserEntry extraEntry = Assert.Single(
                shell.AssetBrowserTool.CrossSectionListItems,
                i => i.RelativePath == "CrossSections_Extra.txt");
            shell.AssetBrowserTool.SelectedItem.Value = extraEntry;
            Assert.True(shell.AssetBrowserTool.CanDeleteSelected.Value);
            shell.AssetBrowserTool.DeleteSelectedCommand.Execute(Unit.Default);
            Assert.Single(project.CrossSectionLists);

            shell.UndoCommand.Execute(Unit.Default);
            Assert.Equal(2, project.CrossSectionLists.Count);

            // Save & Reload
            shell.SaveAllCommand.Execute(Unit.Default);
            WorldProjectLoadResult reloaded = WorldProjectStorage.Load(project.WorldInfoFilePath);
            Assert.DoesNotContain(reloaded.Diagnostics, static d => d.Severity == Serialization.Diagnostics.DiagnosticSeverity.Error);
            Assert.True(reloaded.Project.CrossSections.ContainsKey("CustomCurb"));
            Assert.True(reloaded.Project.CrossSections.ContainsKey("CustomCurb_Copy"));
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void AvaloniaUi_CrossSectionListEditorView_PreservesFocusAndMeasuresCleanly()
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
                WorldProject project = shell.CreateAndActivateWorld(tempDir, "Author", "CsUiWorld", "CsUi", "");

                CrossSectionListEditorDocumentViewModel? csListVm = shell.OpenDefaultCrossSectionList();
                Assert.NotNull(csListVm);
                CrossSectionListRowViewModel? initialRow = csListVm!.SelectedRow.Value;
                Assert.NotNull(initialRow);

                CrossSectionListEditorView view = new()
                {
                    DataContext = csListVm,
                };

                Window host = new()
                {
                    Width = 960,
                    Height = 640,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Position = new PixelPoint(-20000, -20000),
                    Content = view,
                };

                host.Show();
                Dispatcher.UIThread.RunJobs();

                view.Measure(new Size(960, 640));
                view.Arrange(new Rect(0, 0, 960, 640));
                Dispatcher.UIThread.RunJobs();

                TextBox? entryKeyBox = view.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(tb => tb.Text == csListVm.EntryKey.Value);
                Assert.NotNull(entryKeyBox);

                entryKeyBox!.Focus();
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused);

                entryKeyBox.Text = "A";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused);

                entryKeyBox.Text = "A ";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused);
                Assert.Equal("A ", entryKeyBox.Text);

                entryKeyBox.Text = "A Road";
                Dispatcher.UIThread.RunJobs();
                Assert.True(entryKeyBox.IsFocused);
                Assert.Same(initialRow, csListVm.SelectedRow.Value);
                Assert.Equal("A Road", project.CrossSectionLists["CrossSections.txt"].Entries[0].Key);
                Assert.Equal("A Road\tCrossSections/Road1L.xml", csListVm.LiveTsvPreview.Value);

                host.Close();
            });
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "external", "TransportX")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
