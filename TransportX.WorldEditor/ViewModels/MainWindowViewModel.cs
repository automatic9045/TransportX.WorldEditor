using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.Serialization.Diagnostics;
using TransportX.WorldEditor.Serialization.Import;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.ViewModels;

/// <summary>
/// No-op fallback <see cref="IFileDialogService"/> used by the XAML designer or headless tests without pickers.
/// </summary>
public sealed class NullFileDialogService : IFileDialogService
{
    public Task<string?> PickOpenWorldInfoAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickImportWorldSourceAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickOpenFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickAssetFileAsync(
        string title,
        string initialDirectory,
        string filterName,
        IReadOnlyList<string> extensions) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickAssetFilesAsync(
        string title,
        string initialDirectory,
        string filterName,
        IReadOnlyList<string> extensions) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

/// <summary>
/// Optional input payload for creating a new world folder and initial <c>WorldInfo</c>.
/// </summary>
public sealed record NewWorldPromptResult(
    string Author,
    string WorldKey,
    string Title,
    string Description);

/// <summary>
/// Result of prompting the user when closing the editor with unsaved changes.
/// </summary>
public enum UnsavedChangesExitChoice
{
    Cancel,
    SaveAndExit,
    DiscardAndExit,
}

/// <summary>
/// Top-level ViewModel coordinating the docking workspace shell, world lifecycle commands,
/// Undo/Redo state, and dynamically opened document tabs.
/// </summary>
public sealed class MainWindowViewModel : IDisposable
{
    private readonly IFileDialogService DialogService;
    private readonly Func<Task<NewWorldPromptResult?>>? NewWorldPrompt;
    private readonly Func<IReadOnlyList<string>, Task<UnsavedChangesExitChoice>>? UnsavedChangesPrompt;
    private readonly Func<Task<WorldImportOptions?>>? ImportWorldPrompt;
    private readonly CompositeDisposable Disposables = new CompositeDisposable();

    public WorldWorkspaceService Workspace { get; }

    public ViewportDocumentViewModel ViewportDocument { get; }
    public SceneHierarchyToolViewModel SceneHierarchyTool { get; }
    public AssetBrowserToolViewModel AssetBrowserTool { get; }
    public InspectorToolViewModel InspectorTool { get; }
    public DiagnosticsToolViewModel DiagnosticsTool { get; }
    public TimelineToolViewModel TimelineTool { get; }

    public EditorDockFactory DockFactory { get; }
    public BindableReactiveProperty<IRootDock> RootDock { get; }

    public BindableReactiveProperty<string> WindowTitle { get; }
    public BindableReactiveProperty<string> StatusText { get; }
    public BindableReactiveProperty<string> UndoMenuHeader { get; }
    public BindableReactiveProperty<string> RedoMenuHeader { get; }
    public BindableReactiveProperty<bool> CanUndo { get; }
    public BindableReactiveProperty<bool> CanRedo { get; }
    public BindableReactiveProperty<bool> HasOpenProject { get; }
    public BindableReactiveProperty<bool> IsDirty { get; }
    public BindableReactiveProperty<bool> IsVerificationMode { get; }
    public BindableReactiveProperty<bool> IsVerificationStarting { get; }
    public BindableReactiveProperty<bool> IsBackgroundCompiling { get; }
    public BindableReactiveProperty<bool> CanRunWorkspaceActions { get; }
    public BindableReactiveProperty<bool> CanStartNewOrOpen { get; }
    public BindableReactiveProperty<bool> IsSceneHierarchyVisible { get; }
    public BindableReactiveProperty<bool> IsAssetBrowserVisible { get; }
    public BindableReactiveProperty<bool> IsInspectorVisible { get; }
    public BindableReactiveProperty<bool> IsTimelineVisible { get; }
    public BindableReactiveProperty<bool> IsDiagnosticsVisible { get; }

    public ReactiveCommand<Unit> NewWorldCommand { get; }
    public ReactiveCommand<Unit> OpenWorldCommand { get; }
    public ReactiveCommand<Unit> ImportWorldCommand { get; }
    public ReactiveCommand<Unit> SaveCommand { get; }
    public ReactiveCommand<Unit> SaveAllCommand { get; }
    public ReactiveCommand<Unit> OpenWorldSettingsCommand { get; }
    public ReactiveCommand<Unit> OpenModelListCommand { get; }
    public ReactiveCommand<Unit> OpenSoundListCommand { get; }
    public ReactiveCommand<Unit> OpenCrossSectionListCommand { get; }
    public ReactiveCommand<Unit> OpenEnvironmentCommand { get; }
    public ReactiveCommand<Unit> UndoCommand { get; }
    public ReactiveCommand<Unit> RedoCommand { get; }
    public ReactiveCommand<Unit> SwitchToEditModeCommand { get; }
    public ReactiveCommand<Unit> RunScriptVerificationCommand { get; }
    public ReactiveCommand<Unit> LaunchPlayerCommand { get; }
    public ReactiveCommand<Unit> OpenWorldCsxExternalCommand { get; }
    public ReactiveCommand<Unit> ToggleSceneHierarchyCommand { get; }
    public ReactiveCommand<Unit> ToggleAssetBrowserCommand { get; }
    public ReactiveCommand<Unit> ToggleInspectorCommand { get; }
    public ReactiveCommand<Unit> ToggleTimelineCommand { get; }
    public ReactiveCommand<Unit> ToggleDiagnosticsCommand { get; }
    public ReactiveCommand<Unit> ResetWindowLayoutCommand { get; }
    public ReactiveCommand<Unit> ExitCommand { get; }

    public event Action? CloseRequested;

    public MainWindowViewModel()
        : this(new NullFileDialogService(), null, null, null)
    {
    }

    public MainWindowViewModel(
        IFileDialogService dialogService,
        Func<Task<NewWorldPromptResult?>>? newWorldPrompt)
        : this(dialogService, newWorldPrompt, null, null)
    {
    }

    public MainWindowViewModel(
        IFileDialogService dialogService,
        Func<Task<NewWorldPromptResult?>>? newWorldPrompt,
        Func<IReadOnlyList<string>, Task<UnsavedChangesExitChoice>>? unsavedChangesPrompt,
        Func<Task<WorldImportOptions?>>? importWorldPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        DialogService = dialogService;
        NewWorldPrompt = newWorldPrompt;
        UnsavedChangesPrompt = unsavedChangesPrompt;
        ImportWorldPrompt = importWorldPrompt;

        Workspace = new WorldWorkspaceService().AddTo(Disposables);
        ViewportDocument = new ViewportDocumentViewModel(Workspace).AddTo(Disposables);
        SceneHierarchyTool = new SceneHierarchyToolViewModel(Workspace).AddTo(Disposables);
        AssetBrowserTool = new AssetBrowserToolViewModel(
            Workspace,
            entry => _ = OpenAsset(entry),
            entry => CloseAssetDocument(entry),
            DialogService).AddTo(Disposables);
        InspectorTool = new InspectorToolViewModel(Workspace).AddTo(Disposables);
        DiagnosticsTool = new DiagnosticsToolViewModel(Workspace).AddTo(Disposables);
        TimelineTool = new TimelineToolViewModel(Workspace).AddTo(Disposables);

        DockFactory = new EditorDockFactory(
            ViewportDocument,
            SceneHierarchyTool,
            AssetBrowserTool,
            InspectorTool,
            DiagnosticsTool,
            TimelineTool);

        RootDock = new BindableReactiveProperty<IRootDock>(DockFactory.CreateLayout()).AddTo(Disposables);

        WindowTitle = new BindableReactiveProperty<string>("TransportX WorldEditor").AddTo(Disposables);
        StatusText = new BindableReactiveProperty<string>("準備完了").AddTo(Disposables);
        UndoMenuHeader = new BindableReactiveProperty<string>("元に戻す (_U)").AddTo(Disposables);
        RedoMenuHeader = new BindableReactiveProperty<string>("やり直す (_R)").AddTo(Disposables);
        CanUndo = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanRedo = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasOpenProject = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsDirty = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsVerificationMode = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsVerificationStarting = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsBackgroundCompiling = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanRunWorkspaceActions = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanStartNewOrOpen = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsSceneHierarchyVisible = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsAssetBrowserVisible = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsInspectorVisible = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsTimelineVisible = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        IsDiagnosticsVisible = new BindableReactiveProperty<bool>(true).AddTo(Disposables);

        NewWorldCommand = new ReactiveCommand<Unit>(u => { _ = NewWorldAsync(); }).AddTo(Disposables);
        OpenWorldCommand = new ReactiveCommand<Unit>(u => { _ = OpenWorldAsync(); }).AddTo(Disposables);
        ImportWorldCommand = new ReactiveCommand<Unit>(u => { _ = ImportWorldAsync(); }).AddTo(Disposables);
        SaveCommand = new ReactiveCommand<Unit>(u => Save(dirtyOnly: true)).AddTo(Disposables);
        SaveAllCommand = new ReactiveCommand<Unit>(u => Save(dirtyOnly: false)).AddTo(Disposables);
        OpenWorldSettingsCommand = new ReactiveCommand<Unit>(u => { _ = OpenWorldSettings(); }).AddTo(Disposables);
        OpenModelListCommand = new ReactiveCommand<Unit>(u => { _ = OpenDefaultModelList(); }).AddTo(Disposables);
        OpenSoundListCommand = new ReactiveCommand<Unit>(u => { _ = OpenDefaultSoundList(); }).AddTo(Disposables);
        OpenCrossSectionListCommand = new ReactiveCommand<Unit>(u => { _ = OpenDefaultCrossSectionList(); }).AddTo(Disposables);
        OpenEnvironmentCommand = new ReactiveCommand<Unit>(u => { _ = OpenDefaultEnvironment(); }).AddTo(Disposables);
        UndoCommand = new ReactiveCommand<Unit>(_ => Workspace.Undo()).AddTo(Disposables);
        RedoCommand = new ReactiveCommand<Unit>(_ => Workspace.Redo()).AddTo(Disposables);
        SwitchToEditModeCommand = new ReactiveCommand<Unit>(_ => Workspace.RequestStopScriptVerification()).AddTo(Disposables);
        RunScriptVerificationCommand = new ReactiveCommand<Unit>(_ => RunScriptVerification()).AddTo(Disposables);
        LaunchPlayerCommand = new ReactiveCommand<Unit>(_ => LaunchPlayer()).AddTo(Disposables);
        OpenWorldCsxExternalCommand = new ReactiveCommand<Unit>(_ => OpenWorldCsxExternal()).AddTo(Disposables);
        ToggleSceneHierarchyCommand = new ReactiveCommand<Unit>(_ => ToggleToolPane(SceneHierarchyTool, "LeftTopToolDock")).AddTo(Disposables);
        ToggleAssetBrowserCommand = new ReactiveCommand<Unit>(_ => ToggleToolPane(AssetBrowserTool, "LeftBottomToolDock")).AddTo(Disposables);
        ToggleInspectorCommand = new ReactiveCommand<Unit>(_ => ToggleToolPane(InspectorTool, "RightToolDock")).AddTo(Disposables);
        ToggleTimelineCommand = new ReactiveCommand<Unit>(_ => ToggleToolPane(TimelineTool, "BottomToolDock")).AddTo(Disposables);
        ToggleDiagnosticsCommand = new ReactiveCommand<Unit>(_ => ToggleToolPane(DiagnosticsTool, "BottomToolDock")).AddTo(Disposables);
        ResetWindowLayoutCommand = new ReactiveCommand<Unit>(_ => ResetWindowLayout()).AddTo(Disposables);
        ExitCommand = new ReactiveCommand<Unit>(_ => CloseRequested?.Invoke()).AddTo(Disposables);

        Workspace.LastStatusMessage.Subscribe(msg => StatusText.Value = msg).AddTo(Disposables);
        Workspace.ProjectRevision.Subscribe(_ => RefreshHeaderAndUndoState()).AddTo(Disposables);
        Workspace.ActiveRenderMode.Subscribe(mode =>
        {
            IsVerificationMode.Value = mode == EditorRenderMode.ScriptVerification;
        }).AddTo(Disposables);
        Workspace.IsVerificationStarting.Subscribe(starting =>
        {
            IsVerificationStarting.Value = starting;
            RefreshHeaderAndUndoState();
        }).AddTo(Disposables);
        Workspace.IsBackgroundCompiling.Subscribe(compiling =>
        {
            IsBackgroundCompiling.Value = compiling;
        }).AddTo(Disposables);
        Workspace.CommandHistory.HistoryChanged += OnCommandHistoryChanged;
        Workspace.ActivateViewportRequested += DockFactory.ActivateViewport;
        Workspace.GlobalShortcutRequested += ExecuteGlobalShortcut;
        DiagnosticsTool.NavigationRequested += OnDiagnosticNavigationRequested;
        DockFactory.LayoutChanged += RefreshToolVisibilityStates;

        RefreshHeaderAndUndoState();
        RefreshToolVisibilityStates();

        RefreshHeaderAndUndoState();
    }

    public async Task NewWorldAsync()
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return;
        }

        if (NewWorldPrompt is null)
        {
            Workspace.LastStatusMessage.Value = "新規ワールドダイアログが構成されていません。";
            return;
        }

        NewWorldPromptResult? input = await NewWorldPrompt();
        if (input is null)
        {
            return;
        }

        string? parentDir = await DialogService.PickOpenFolderAsync("ワールド保存先の親フォルダを選択 (例: Worlds)");
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            return;
        }

        CreateAndActivateWorld(parentDir, input.Author, input.WorldKey, input.Title, input.Description);
    }

    public WorldProject CreateAndActivateWorld(
        string parentDirectory,
        string author,
        string worldKey,
        string title,
        string description)
    {
        DockFactory.CloseDynamicDocuments();
        WorldProject project = Workspace.CreateNewWorld(parentDirectory, author, worldKey, title, description);
        OpenWorldSettings();
        return project;
    }

    public async Task OpenWorldAsync()
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return;
        }

        string? path = await DialogService.PickOpenWorldInfoAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        OpenWorldFromPath(path);
    }

    public WorldProjectLoadResult OpenWorldFromPath(string worldInfoPath)
    {
        DockFactory.CloseDynamicDocuments();
        return Workspace.OpenWorld(worldInfoPath);
    }

    public async Task ImportWorldAsync()
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return;
        }

        if (ImportWorldPrompt is null)
        {
            Workspace.LastStatusMessage.Value = "インポートダイアログが構成されていません。";
            return;
        }

        WorldImportOptions? options = await ImportWorldPrompt();
        if (options is null)
        {
            return;
        }

        ImportAndOpenWorld(options);
    }

    public WorldImportResult ImportAndOpenWorld(WorldImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        WorldImportResult importResult = WorldProjectImporter.Import(options);
        if (!importResult.Succeeded)
        {
            Workspace.ParseDiagnostics.Value = importResult.Diagnostics;
            ParseDiagnostic? firstErr = importResult.Diagnostics.FirstOrDefault(
                static d => d.Severity == DiagnosticSeverity.Error);
            Workspace.LastStatusMessage.Value = firstErr is not null
                ? $"インポートに失敗しました: {firstErr.Message}"
                : "インポートに失敗しました。";
            return importResult;
        }

        WorldProjectLoadResult loadResult = OpenWorldFromPath(importResult.DestinationWorldInfoPath);
        if (0 < importResult.Diagnostics.Count)
        {
            List<ParseDiagnostic> combinedDiag = new(importResult.Diagnostics.Count + loadResult.Diagnostics.Count);
            combinedDiag.AddRange(importResult.Diagnostics);
            combinedDiag.AddRange(loadResult.Diagnostics);
            Workspace.ParseDiagnostics.Value = combinedDiag;
        }

        Workspace.LastStatusMessage.Value =
            $"'{loadResult.Project.WorldInfo.Title}' をインポートして開きました " +
            $"(テンプレート: Sp={importResult.ImportedSplineTemplateCount}/Jn={importResult.ImportedJunctionTemplateCount}, " +
            $"要素: Sp={importResult.ImportedSplineCount}/Jn={importResult.ImportedJunctionCount}/Prop={importResult.ImportedPropCount}, " +
            $"ユーザーコード={importResult.PreservedUserCodeStatementCount}件, 保護={loadResult.Project.ProtectedFiles.Count}件)。";
        return importResult;
    }

    public void Save(bool dirtyOnly)
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return;
        }

        Workspace.Save(dirtyOnly);
        RefreshHeaderAndUndoState();
    }

    public bool ShouldConfirmExit()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && 0 < project.DirtyFiles.Count;
    }

    public IReadOnlyList<string> GetDirtyFilePaths()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null || 0 == project.DirtyFiles.Count)
        {
            return Array.Empty<string>();
        }

        return project.DirtyFiles
            .Select(static f => f.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<bool> ResolveExitAsync(
        Func<IReadOnlyList<string>, Task<UnsavedChangesExitChoice>>? promptOverride = null)
    {
        if (!ShouldConfirmExit())
        {
            return true;
        }

        Func<IReadOnlyList<string>, Task<UnsavedChangesExitChoice>>? prompt = promptOverride ?? UnsavedChangesPrompt;
        if (prompt is null)
        {
            return false;
        }

        UnsavedChangesExitChoice choice = await prompt(GetDirtyFilePaths());
        switch (choice)
        {
            case UnsavedChangesExitChoice.DiscardAndExit:
                return true;

            case UnsavedChangesExitChoice.SaveAndExit:
                if (Workspace.IsVerificationStarting.Value)
                {
                    Workspace.LastStatusMessage.Value = "スクリプト検証の起動中は保存・終了できません。";
                    return false;
                }

                Save(dirtyOnly: true);
                return !ShouldConfirmExit();

            default:
                return false;
        }
    }

    public void ExecuteGlobalShortcut(EditorGlobalShortcut shortcut)
    {
        switch (shortcut)
        {
            case EditorGlobalShortcut.Save:
                if (CanRunWorkspaceActions.Value)
                {
                    Save(dirtyOnly: true);
                }

                break;

            case EditorGlobalShortcut.SaveAll:
                if (CanRunWorkspaceActions.Value)
                {
                    Save(dirtyOnly: false);
                }

                break;

            case EditorGlobalShortcut.NewWorld:
                if (CanStartNewOrOpen.Value)
                {
                    _ = NewWorldAsync();
                }

                break;

            case EditorGlobalShortcut.OpenWorld:
                if (CanStartNewOrOpen.Value)
                {
                    _ = OpenWorldAsync();
                }

                break;

            case EditorGlobalShortcut.Undo:
                if (CanUndo.Value)
                {
                    Workspace.Undo();
                }

                break;

            case EditorGlobalShortcut.Redo:
                if (CanRedo.Value)
                {
                    Workspace.Redo();
                }

                break;

            case EditorGlobalShortcut.Verify:
                if (CanRunWorkspaceActions.Value)
                {
                    RunScriptVerification();
                }

                break;

            case EditorGlobalShortcut.LaunchPlayer:
                if (CanRunWorkspaceActions.Value)
                {
                    LaunchPlayer();
                }

                break;
        }
    }

    public WorldSettingsDocumentViewModel? OpenWorldSettings()
    {
        if (Workspace.CurrentProject.Value is null)
        {
            return null;
        }

        return DockFactory.OpenOrActivateDocument(
            "Doc_WorldSettings",
            () => new WorldSettingsDocumentViewModel(Workspace, DialogService));
    }

    public ModelListEditorDocumentViewModel? OpenDefaultModelList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        ModelListDocument? doc = project.ModelLists.Values.FirstOrDefault();
        if (doc is null)
        {
            if (Workspace.AddNewModelList("Models.txt"))
            {
                doc = project.ModelLists.Values.FirstOrDefault();
            }
            else
            {
                Workspace.LastStatusMessage.Value = "このワールドにはモデルリストがなく、Init.csx は上書き保護されています。";
                return null;
            }
        }

        if (doc is null)
        {
            return null;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        return DockFactory.OpenOrActivateDocument(
            $"Doc_Models_{normalizedRel}",
            () => new ModelListEditorDocumentViewModel(Workspace, DialogService, normalizedRel));
    }

    public SoundListEditorDocumentViewModel? OpenDefaultSoundList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        SoundListDocument? doc = project.SoundLists.Values.FirstOrDefault();
        if (doc is null)
        {
            if (Workspace.AddNewSoundList("Sounds.txt"))
            {
                doc = project.SoundLists.Values.FirstOrDefault();
            }
            else
            {
                Workspace.LastStatusMessage.Value = "このワールドにはサウンドリスト (Sounds.LoadList) が定義されておらず、Init.csx は上書き保護されています。";
                return null;
            }
        }

        if (doc is null)
        {
            return null;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        return DockFactory.OpenOrActivateDocument(
            $"Doc_Sounds_{normalizedRel}",
            () => new SoundListEditorDocumentViewModel(Workspace, DialogService, normalizedRel));
    }

    public CrossSectionListEditorDocumentViewModel? OpenDefaultCrossSectionList()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        CrossSectionListDocument? doc = project.CrossSectionLists.Values.FirstOrDefault();
        if (doc is null)
        {
            if (Workspace.AddNewCrossSectionList("CrossSections.txt"))
            {
                doc = project.CrossSectionLists.Values.FirstOrDefault();
            }
            else
            {
                Workspace.LastStatusMessage.Value = "このワールドには断面リスト (CrossSections.LoadList) が定義されておらず、Init.csx は上書き保護されています。";
                return null;
            }
        }

        if (doc is null)
        {
            return null;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        return DockFactory.OpenOrActivateDocument(
            $"Doc_CrossSections_{normalizedRel}",
            () => CreateCrossSectionListEditor(normalizedRel));
    }

    public CrossSectionEditorDocumentViewModel? OpenCrossSectionEditor(string crossSectionKey)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null || string.IsNullOrWhiteSpace(crossSectionKey) || !project.CrossSections.ContainsKey(crossSectionKey))
        {
            return null;
        }

        return DockFactory.OpenOrActivateDocument(
            $"Doc_CrossSection_{crossSectionKey}",
            () => new CrossSectionEditorDocumentViewModel(Workspace, DialogService, crossSectionKey));
    }

    private CrossSectionListEditorDocumentViewModel CreateCrossSectionListEditor(string normalizedRel)
    {
        return new CrossSectionListEditorDocumentViewModel(
            Workspace,
            DialogService,
            normalizedRel,
            key => _ = OpenCrossSectionEditor(key),
            key => DockFactory.CloseDocumentById($"Doc_CrossSection_{key}"));
    }

    public EnvironmentEditorDocumentViewModel? OpenDefaultEnvironment()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return null;
        }

        EnvironmentProfileDocument? doc = project.Environments.Values.FirstOrDefault();
        if (doc is null)
        {
            Workspace.LastStatusMessage.Value = "このワールドに環境設定 (Environment*.xml) が見つかりません。";
            return null;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
        return DockFactory.OpenOrActivateDocument(
            $"Doc_Env_{normalizedRel}",
            () => new EnvironmentEditorDocumentViewModel(Workspace, normalizedRel));
    }

    public EditorDocumentBase? OpenAsset(AssetBrowserEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Workspace.CurrentProject.Value is null)
        {
            return null;
        }

        string normalizedRel = WorldProject.NormalizeRelativePath(entry.RelativePath);
        switch (entry.Kind)
        {
            case AssetBrowserItemKind.WorldSettings:
                return OpenWorldSettings();

            case AssetBrowserItemKind.ModelList:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_Models_{normalizedRel}",
                    () => new ModelListEditorDocumentViewModel(Workspace, DialogService, normalizedRel));

            case AssetBrowserItemKind.SoundList:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_Sounds_{normalizedRel}",
                    () => new SoundListEditorDocumentViewModel(Workspace, DialogService, normalizedRel));

            case AssetBrowserItemKind.CrossSectionList:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_CrossSections_{normalizedRel}",
                    () => CreateCrossSectionListEditor(normalizedRel));

            case AssetBrowserItemKind.Environment:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_Env_{normalizedRel}",
                    () => new EnvironmentEditorDocumentViewModel(Workspace, normalizedRel));

            case AssetBrowserItemKind.LaneLayout:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_LaneLayout_{entry.Key}",
                    () => new LaneLayoutEditorDocumentViewModel(Workspace, entry.Key));

            case AssetBrowserItemKind.CrossSection:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_CrossSection_{entry.Key}",
                    () => new CrossSectionEditorDocumentViewModel(Workspace, DialogService, entry.Key));

            case AssetBrowserItemKind.SplineTemplate:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_SplineTemplate_{entry.Key}",
                    () => new SplineTemplateEditorDocumentViewModel(Workspace, entry.Key));

            case AssetBrowserItemKind.JunctionTemplate:
                return DockFactory.OpenOrActivateDocument(
                    $"Doc_JunctionTemplate_{entry.Key}",
                    () => new JunctionTemplateEditorDocumentViewModel(Workspace, entry.Key));

            default:
                Workspace.LastStatusMessage.Value = $"'{entry.Key}' ({entry.Kind}) の詳細GUI編集は次フェーズで接続予定です。";
                return null;
        }
    }

    public void CloseAssetDocument(AssetBrowserEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string normalizedRel = WorldProject.NormalizeRelativePath(entry.RelativePath);
        string? docId = entry.Kind switch
        {
            AssetBrowserItemKind.WorldSettings => "Doc_WorldSettings",
            AssetBrowserItemKind.ModelList => $"Doc_Models_{normalizedRel}",
            AssetBrowserItemKind.SoundList => $"Doc_Sounds_{normalizedRel}",
            AssetBrowserItemKind.CrossSectionList => $"Doc_CrossSections_{normalizedRel}",
            AssetBrowserItemKind.Environment => $"Doc_Env_{normalizedRel}",
            AssetBrowserItemKind.LaneLayout => $"Doc_LaneLayout_{entry.Key}",
            AssetBrowserItemKind.CrossSection => $"Doc_CrossSection_{entry.Key}",
            AssetBrowserItemKind.SplineTemplate => $"Doc_SplineTemplate_{entry.Key}",
            AssetBrowserItemKind.JunctionTemplate => $"Doc_JunctionTemplate_{entry.Key}",
            _ => null,
        };

        if (docId is not null)
        {
            DockFactory.CloseDocumentById(docId);
        }

        if (entry.Kind == AssetBrowserItemKind.CrossSectionList)
        {
            CloseOrphanedCrossSectionEditors();
        }
    }

    private void CloseOrphanedCrossSectionEditors()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null || DockFactory.DocumentDock?.VisibleDockables is null)
        {
            return;
        }

        List<CrossSectionEditorDocumentViewModel> openCrossSections = DockFactory.DocumentDock.VisibleDockables
            .OfType<CrossSectionEditorDocumentViewModel>()
            .ToList();
        foreach (CrossSectionEditorDocumentViewModel csDoc in openCrossSections)
        {
            if (!project.CrossSections.ContainsKey(csDoc.CrossSectionKey) && !string.IsNullOrEmpty(csDoc.Id))
            {
                DockFactory.CloseDocumentById(csDoc.Id);
            }
        }
    }

    private void OnCommandHistoryChanged(object? sender, Core.Commands.CommandExecutedEventArgs e)
    {
        RefreshHeaderAndUndoState();
    }

    private void RefreshHeaderAndUndoState()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        bool startingVerification = Workspace.IsVerificationStarting.Value;
        HasOpenProject.Value = project is not null;
        CanStartNewOrOpen.Value = !startingVerification;
        CanRunWorkspaceActions.Value = project is not null && !startingVerification;
        bool dirty = project is not null && 0 < project.DirtyFiles.Count;
        IsDirty.Value = dirty;

        if (project is null)
        {
            WindowTitle.Value = "TransportX WorldEditor";
        }
        else
        {
            string star = dirty ? "*" : string.Empty;
            string name = string.IsNullOrWhiteSpace(project.WorldInfo.Title)
                ? Path.GetFileName(project.WorldInfoFilePath)
                : project.WorldInfo.Title;
            WindowTitle.Value = $"{name}{star} - TransportX WorldEditor";
        }

        CanUndo.Value = !startingVerification && Workspace.CommandHistory.CanUndo;
        CanRedo.Value = !startingVerification && Workspace.CommandHistory.CanRedo;

        string? undoDesc = Workspace.CommandHistory.UndoDescription;
        UndoMenuHeader.Value = string.IsNullOrEmpty(undoDesc)
            ? "元に戻す (_U)"
            : $"元に戻す: {undoDesc} (_U)";

        string? redoDesc = Workspace.CommandHistory.RedoDescription;
        RedoMenuHeader.Value = string.IsNullOrEmpty(redoDesc)
            ? "やり直す (_R)"
            : $"やり直す: {redoDesc} (_R)";
    }

    public bool RunScriptVerification()
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return false;
        }

        bool ok = Workspace.RequestStartScriptVerification();
        RefreshHeaderAndUndoState();
        return ok;
    }

    public bool LaunchPlayer()
    {
        if (Workspace.IsVerificationStarting.Value)
        {
            return false;
        }

        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            Workspace.LastStatusMessage.Value = "TransportX.Player で試走するにはワールドを開いてください。";
            return false;
        }

        if (0 < project.DirtyFiles.Count)
        {
            Workspace.Save(dirtyOnly: true);
            RefreshHeaderAndUndoState();
        }

        string? exePath = PlayerLauncherService.ResolvePlayerExecutablePath(project);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Workspace.LastStatusMessage.Value = "TransportX.Player.exe が見つかりません。ワールド設定タブで実行ファイルのパスを指定してください。";
            return false;
        }

        if (!PlayerLauncherService.LaunchPlayer(exePath, out string? errorMessage))
        {
            Workspace.LastStatusMessage.Value = $"TransportX.Player.exe 起動エラー: {errorMessage}";
            return false;
        }

        Workspace.LastStatusMessage.Value = $"TransportX.Player.exe を起動しました: {exePath}";
        return true;
    }

    public bool OpenWorldCsxExternal()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return false;
        }

        string worldPath = Path.GetFullPath(Path.Combine(project.WorldDirectory, project.Placement.RelativePath));
        if (!File.Exists(worldPath))
        {
            Workspace.Save(dirtyOnly: false);
            RefreshHeaderAndUndoState();
        }

        if (!PlayerLauncherService.OpenInExternalEditor(worldPath, out string? errorMessage))
        {
            Workspace.LastStatusMessage.Value = errorMessage ?? "World.csx を外部エディタで開けませんでした。";
            return false;
        }

        Workspace.LastStatusMessage.Value = $"外部エディタで開きました: {project.Placement.RelativePath}";
        return true;
    }

    private void OnDiagnosticNavigationRequested(DiagnosticEntryViewModel entry)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.ElementId))
        {
            if (Workspace.FindProp(entry.ElementId) is not null)
            {
                Workspace.SelectProp(entry.ElementId);
                Workspace.SelectSpline(null);
                Workspace.SelectJunction(null);
                Workspace.RequestFocusSelectedOrDefault();
                Workspace.LastStatusMessage.Value = $"プロップ '{entry.ElementId}' へ移動しました。";
                return;
            }

            if (Workspace.FindSpline(entry.ElementId) is not null)
            {
                Workspace.SelectSpline(entry.ElementId);
                Workspace.SelectProp(null);
                Workspace.SelectJunction(null);
                Workspace.RequestFocusSelectedOrDefault();
                Workspace.LastStatusMessage.Value = $"スプライン '{entry.ElementId}' へ移動しました。";
                return;
            }

            if (Workspace.FindJunction(entry.ElementId) is not null)
            {
                Workspace.SelectJunction(entry.ElementId);
                Workspace.SelectProp(null);
                Workspace.SelectSpline(null);
                Workspace.RequestFocusSelectedOrDefault();
                Workspace.LastStatusMessage.Value = $"ジャンクション '{entry.ElementId}' へ移動しました。";
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.FilePath))
        {
            string raw = entry.FilePath;
            string rel = Path.IsPathRooted(raw) && raw.StartsWith(project.WorldDirectory, StringComparison.OrdinalIgnoreCase)
                ? WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, raw))
                : WorldProject.NormalizeRelativePath(raw);
            string fileName = Path.GetFileName(rel);

            if (string.Equals(rel, WorldProject.NormalizeRelativePath(project.Placement.RelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(project.Placement.RelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(rel, WorldProject.NormalizeRelativePath(project.Init.RelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(project.Init.RelativePath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, Path.GetFileName(project.WorldInfoFilePath), StringComparison.OrdinalIgnoreCase))
            {
                OpenWorldSettings();
                string lineSuffix = 0 < entry.Line ? $" (行 {entry.Line})" : string.Empty;
                Workspace.LastStatusMessage.Value = $"ワールド設定タブを開きました: {fileName}{lineSuffix}";
                return;
            }

            AssetBrowserEntry? matched = AssetBrowserTool.Items.FirstOrDefault(item =>
                string.Equals(WorldProject.NormalizeRelativePath(item.RelativePath), rel, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(fileName) && string.Equals(Path.GetFileName(item.RelativePath), fileName, StringComparison.OrdinalIgnoreCase)));
            if (matched is not null)
            {
                OpenAsset(matched);
                string lineSuffix = 0 < entry.Line ? $" (行 {entry.Line})" : string.Empty;
                Workspace.LastStatusMessage.Value = $"アセット '{matched.Key}' を開きました: {fileName}{lineSuffix}";
                return;
            }
        }
    }

    private void ToggleToolPane(IDockable tool, string defaultToolDockId)
    {
        DockFactory.ToggleToolPane(tool, defaultToolDockId);
        RefreshToolVisibilityStates();
    }

    public void ResetWindowLayout()
    {
        RootDock.Value = DockFactory.ResetLayoutPreservingDocuments();
        RefreshToolVisibilityStates();
        Workspace.LastStatusMessage.Value = "ウィンドウレイアウトを初期状態に戻しました。";
    }

    private void RefreshToolVisibilityStates()
    {
        IsSceneHierarchyVisible.Value = DockFactory.IsDockableVisible("SceneHierarchy");
        IsAssetBrowserVisible.Value = DockFactory.IsDockableVisible("AssetBrowser");
        IsInspectorVisible.Value = DockFactory.IsDockableVisible("Inspector");
        IsTimelineVisible.Value = DockFactory.IsDockableVisible("Timeline");
        IsDiagnosticsVisible.Value = DockFactory.IsDockableVisible("Diagnostics");
    }

    public void Dispose()
    {
        DockFactory.LayoutChanged -= RefreshToolVisibilityStates;
        DiagnosticsTool.NavigationRequested -= OnDiagnosticNavigationRequested;
        Workspace.GlobalShortcutRequested -= ExecuteGlobalShortcut;
        Workspace.ActivateViewportRequested -= DockFactory.ActivateViewport;
        Workspace.CommandHistory.HistoryChanged -= OnCommandHistoryChanged;
        DockFactory.CloseDynamicDocuments();
        Disposables.Dispose();
    }
}
