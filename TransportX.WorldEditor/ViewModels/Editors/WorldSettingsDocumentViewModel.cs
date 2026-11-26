using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Init;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// Document ViewModel for editing <c>WorldInfo</c> (<c>.xml</c>), <c>World.manifest.xml</c>, and <c>Init.csx</c>.
/// </summary>
public sealed class WorldSettingsDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly IFileDialogService? DialogService;
    private bool IsSyncing;
    private bool IsCommitting;

    public BindableReactiveProperty<string> WorldTitle { get; }
    public BindableReactiveProperty<string> Description { get; }
    public BindableReactiveProperty<string> Author { get; }
    public BindableReactiveProperty<string> RuntimePath { get; }
    public BindableReactiveProperty<string> Identifier { get; }
    public BindableReactiveProperty<string> AppPath { get; }
    public BindableReactiveProperty<string> MainScriptArg { get; }

    public ObservableCollection<string> ManifestDependencies { get; } = new ObservableCollection<string>();
    public BindableReactiveProperty<string?> SelectedDependency { get; }
    public BindableReactiveProperty<string> NewDependencyPath { get; }

    public ObservableCollection<LaneTrafficTypeDefinition> TrafficTypes { get; } = new ObservableCollection<LaneTrafficTypeDefinition>();
    public BindableReactiveProperty<LaneTrafficTypeDefinition?> SelectedTrafficType { get; }
    public BindableReactiveProperty<string> NewTrafficTypeKey { get; }
    public BindableReactiveProperty<string> NewTrafficTypeName { get; }
    public BindableReactiveProperty<string> NewTrafficTypeColor { get; }

    public ObservableCollection<LaneTrafficGroupRowViewModel> TrafficGroups { get; } = new ObservableCollection<LaneTrafficGroupRowViewModel>();
    public BindableReactiveProperty<LaneTrafficGroupRowViewModel?> SelectedTrafficGroup { get; }
    public BindableReactiveProperty<string> NewTrafficGroupKey { get; }
    public BindableReactiveProperty<string> NewTrafficGroupTypeKeys { get; }
    public BindableReactiveProperty<string> NewTrafficGroupColor { get; }

    public BindableReactiveProperty<bool> IsInitProtected { get; }
    public BindableReactiveProperty<string> InitUserCode { get; }
    public BindableReactiveProperty<bool> IsWorldProtected { get; }
    public BindableReactiveProperty<string> WorldUserCode { get; }
    public BindableReactiveProperty<string> PlayerExecutablePath { get; }

    public ReactiveCommand<Unit> AddDependencyCommand { get; }
    public ReactiveCommand<Unit> RemoveDependencyCommand { get; }
    public ReactiveCommand<Unit> AddTrafficTypeCommand { get; }
    public ReactiveCommand<Unit> RemoveTrafficTypeCommand { get; }
    public ReactiveCommand<Unit> AddTrafficGroupCommand { get; }
    public ReactiveCommand<Unit> RemoveTrafficGroupCommand { get; }
    public ReactiveCommand<Unit> OpenWorldCsxExternalCommand { get; }
    public ReactiveCommand<Unit> OpenInitCsxExternalCommand { get; }
    public ReactiveCommand<Unit> UnlockWorldProtectedCommand { get; }
    public ReactiveCommand<Unit> UnlockInitProtectedCommand { get; }
    public ReactiveCommand<Unit> BrowsePlayerExecutableCommand { get; }
    public ReactiveCommand<Unit> AutoDetectPlayerExecutableCommand { get; }

    public WorldSettingsDocumentViewModel(WorldWorkspaceService workspace)
        : this(workspace, null)
    {
    }

    public WorldSettingsDocumentViewModel(WorldWorkspaceService workspace, IFileDialogService? dialogService)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        DialogService = dialogService;
        Id = "Doc_WorldSettings";
        Title = "ワールド設定 / Init";

        WorldTitle = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        Description = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        Author = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        RuntimePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        Identifier = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        AppPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        MainScriptArg = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedDependency = new BindableReactiveProperty<string?>(null).AddTo(Disposables);
        NewDependencyPath = new BindableReactiveProperty<string>("$(plugins)TransportX.Domains.RoadTraffic.dll").AddTo(Disposables);

        SelectedTrafficType = new BindableReactiveProperty<LaneTrafficTypeDefinition?>(null).AddTo(Disposables);
        NewTrafficTypeKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewTrafficTypeName = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewTrafficTypeColor = new BindableReactiveProperty<string>("#FFFFFF").AddTo(Disposables);

        SelectedTrafficGroup = new BindableReactiveProperty<LaneTrafficGroupRowViewModel?>(null).AddTo(Disposables);
        NewTrafficGroupKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewTrafficGroupTypeKeys = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        NewTrafficGroupColor = new BindableReactiveProperty<string>("#00FFFF").AddTo(Disposables);

        IsInitProtected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        InitUserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsWorldProtected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        WorldUserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PlayerExecutablePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        BindWorldInfoString(WorldTitle, "WorldInfo.Title", "ワールドタイトルを変更",
            p => p.WorldInfo.Title, (p, v) => p.WorldInfo.Title = v);
        BindWorldInfoString(Description, "WorldInfo.Description", "ワールド説明を変更",
            p => p.WorldInfo.Description, (p, v) => p.WorldInfo.Description = v);
        BindWorldInfoString(Author, "WorldInfo.Author", "著者名を変更",
            p => p.WorldInfo.Author, (p, v) => p.WorldInfo.Author = v);
        BindWorldInfoString(RuntimePath, "WorldInfo.Path", "実行アセンブリパスを変更",
            p => p.WorldInfo.Path, (p, v) => p.WorldInfo.Path = v);
        BindWorldInfoString(Identifier, "WorldInfo.Identifier", "Identifier を変更",
            p => p.WorldInfo.Identifier ?? string.Empty,
            (p, v) => p.WorldInfo.Identifier = string.IsNullOrWhiteSpace(v) ? null : v);
        BindWorldInfoString(AppPath, "WorldInfo.AppPath", "AppPath を変更",
            p => p.WorldInfo.AppPath ?? string.Empty,
            (p, v) => p.WorldInfo.AppPath = string.IsNullOrWhiteSpace(v) ? null : v);
        BindWorldInfoString(MainScriptArg, "WorldInfo.Args0", "メインスクリプトパスを変更",
            p => 0 < p.WorldInfo.Args.Count ? p.WorldInfo.Args[0] : string.Empty,
            (p, v) =>
            {
                if (p.WorldInfo.Args.Count == 0)
                {
                    p.WorldInfo.Args.Add(v);
                }
                else
                {
                    p.WorldInfo.Args[0] = v;
                }
            });

        InitUserCode.Skip(1).Subscribe(newVal =>
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            if (IsSyncing || project is null || IsInitProtected.Value)
            {
                return;
            }

            string? targetValue = string.IsNullOrEmpty(newVal) ? null : newVal;
            string? oldValue = project.Init.UserCode;
            if (string.Equals(oldValue, targetValue, StringComparison.Ordinal))
            {
                return;
            }

            WorldFileTarget fileTarget = new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath));
            IsCommitting = true;
            try
            {
                Workspace.Execute(new MergeableValueCommand<string?>(
                    "Init.csx の <user-code> を編集",
                    "Init.UserCode",
                    InvalidationScope.FullRebuild,
                    fileTarget,
                    oldValue,
                    targetValue,
                    val => project.Init.UserCode = val));
            }
            finally
            {
                IsCommitting = false;
            }
        }).AddTo(Disposables);

        WorldUserCode.Skip(1).Subscribe(newVal =>
        {
            if (IsSyncing || IsWorldProtected.Value)
            {
                return;
            }

            IsCommitting = true;
            try
            {
                Workspace.SetWorldUserCode(newVal);
            }
            finally
            {
                IsCommitting = false;
            }
        }).AddTo(Disposables);

        PlayerExecutablePath.Skip(1).Subscribe(newVal =>
        {
            if (IsSyncing)
            {
                return;
            }

            IsCommitting = true;
            try
            {
                Workspace.SetPlayerExecutablePath(newVal);
            }
            finally
            {
                IsCommitting = false;
            }
        }).AddTo(Disposables);

        AddDependencyCommand = new ReactiveCommand<Unit>(_ => AddDependency()).AddTo(Disposables);
        RemoveDependencyCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedDependency()).AddTo(Disposables);
        AddTrafficTypeCommand = new ReactiveCommand<Unit>(_ => AddTrafficType()).AddTo(Disposables);
        RemoveTrafficTypeCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedTrafficType()).AddTo(Disposables);
        AddTrafficGroupCommand = new ReactiveCommand<Unit>(_ => AddTrafficGroup()).AddTo(Disposables);
        RemoveTrafficGroupCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedTrafficGroup()).AddTo(Disposables);
        OpenWorldCsxExternalCommand = new ReactiveCommand<Unit>(_ => OpenScriptExternal(isInit: false)).AddTo(Disposables);
        OpenInitCsxExternalCommand = new ReactiveCommand<Unit>(_ => OpenScriptExternal(isInit: true)).AddTo(Disposables);
        UnlockWorldProtectedCommand = new ReactiveCommand<Unit>(_ => UnlockProtected(isInit: false)).AddTo(Disposables);
        UnlockInitProtectedCommand = new ReactiveCommand<Unit>(_ => UnlockProtected(isInit: true)).AddTo(Disposables);
        BrowsePlayerExecutableCommand = new ReactiveCommand<Unit>(u => { _ = BrowsePlayerExecutableAsync(); }).AddTo(Disposables);
        AutoDetectPlayerExecutableCommand = new ReactiveCommand<Unit>(_ => AutoDetectPlayerExecutable()).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            "ワールド設定 / Init",
            (_, target) => target.Kind is WorldFileKind.WorldInfo or WorldFileKind.Manifest or WorldFileKind.InitCsx);
    }

    private void BindWorldInfoString(
        BindableReactiveProperty<string> property,
        string mergeKey,
        string description,
        Func<WorldProject, string> getter,
        Action<WorldProject, string> setter)
    {
        property.Skip(1).Subscribe(newVal =>
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            if (IsSyncing || project is null)
            {
                return;
            }

            string oldVal = getter(project);
            if (string.Equals(oldVal, newVal, StringComparison.Ordinal))
            {
                return;
            }

            WorldFileTarget target = new WorldFileTarget(
                WorldFileKind.WorldInfo,
                string.IsNullOrEmpty(project.WorldInfoFilePath) ? "WorldInfo.xml" : System.IO.Path.GetFileName(project.WorldInfoFilePath));

            IsCommitting = true;
            try
            {
                Workspace.Execute(new MergeableValueCommand<string>(
                    description,
                    mergeKey,
                    InvalidationScope.None,
                    target,
                    oldVal,
                    newVal,
                    val => setter(project, val)));
            }
            finally
            {
                IsCommitting = false;
            }
        }).AddTo(Disposables);
    }

    private void AddDependency()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        string path = NewDependencyPath.Value.Trim();
        if (project is null || string.IsNullOrEmpty(path) || project.Manifest.Dependencies.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        IReadOnlyList<WorldFileTarget> targets = GetManifestTargets(project);
        Workspace.Execute(new DelegateEditorCommand(
            $"依存 DLL '{path}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () => project.Manifest.Dependencies.Add(path),
            () => project.Manifest.Dependencies.Remove(path)));
    }

    private void RemoveSelectedDependency()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        string? selected = SelectedDependency.Value;
        if (project is null || selected is null)
        {
            return;
        }

        int index = project.Manifest.Dependencies.IndexOf(selected);
        if (index < 0)
        {
            return;
        }

        IReadOnlyList<WorldFileTarget> targets = GetManifestTargets(project);
        Workspace.Execute(new DelegateEditorCommand(
            $"依存 DLL '{selected}' を削除",
            InvalidationScope.FullRebuild,
            targets,
            () => project.Manifest.Dependencies.RemoveAt(index),
            () => project.Manifest.Dependencies.Insert(index, selected)));
    }

    private void AddTrafficType()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        string key = NewTrafficTypeKey.Value.Trim();
        if (project is null || IsInitProtected.Value || string.IsNullOrEmpty(key)
            || project.Init.LaneTraffic.Types.Any(t => string.Equals(t.Key, key, StringComparison.Ordinal)))
        {
            return;
        }

        LaneTrafficTypeDefinition def = new LaneTrafficTypeDefinition(
            key,
            string.IsNullOrWhiteSpace(NewTrafficTypeName.Value) ? key : NewTrafficTypeName.Value.Trim(),
            string.IsNullOrWhiteSpace(NewTrafficTypeColor.Value) ? "#FFFFFF" : NewTrafficTypeColor.Value.Trim());

        WorldFileTarget initTarget = new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"交通種別 '{key}' を追加",
            InvalidationScope.FullRebuild,
            initTarget,
            () => project.Init.LaneTraffic.Types.Add(def),
            () => project.Init.LaneTraffic.Types.Remove(def)));

        NewTrafficTypeKey.Value = string.Empty;
        NewTrafficTypeName.Value = string.Empty;
    }

    private void RemoveSelectedTrafficType()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        LaneTrafficTypeDefinition? selected = SelectedTrafficType.Value;
        if (project is null || IsInitProtected.Value || selected is null)
        {
            return;
        }

        int idx = project.Init.LaneTraffic.Types.IndexOf(selected);
        if (idx < 0)
        {
            return;
        }

        WorldFileTarget initTarget = new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"交通種別 '{selected.Key}' を削除",
            InvalidationScope.FullRebuild,
            initTarget,
            () => project.Init.LaneTraffic.Types.RemoveAt(idx),
            () => project.Init.LaneTraffic.Types.Insert(idx, selected)));
    }

    private void AddTrafficGroup()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        string key = NewTrafficGroupKey.Value.Trim();
        if (project is null || IsInitProtected.Value || string.IsNullOrEmpty(key)
            || project.Init.LaneTraffic.Groups.Any(g => string.Equals(g.Key, key, StringComparison.Ordinal)))
        {
            return;
        }

        List<string> typeKeys = NewTrafficGroupTypeKeys.Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        LaneTrafficGroupDefinition def = new LaneTrafficGroupDefinition(
            key,
            typeKeys,
            string.IsNullOrWhiteSpace(NewTrafficGroupColor.Value) ? "#00FFFF" : NewTrafficGroupColor.Value.Trim());

        WorldFileTarget initTarget = new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"交通グループ '{key}' を追加",
            InvalidationScope.FullRebuild,
            initTarget,
            () => project.Init.LaneTraffic.Groups.Add(def),
            () => project.Init.LaneTraffic.Groups.Remove(def)));

        NewTrafficGroupKey.Value = string.Empty;
        NewTrafficGroupTypeKeys.Value = string.Empty;
    }

    private void RemoveSelectedTrafficGroup()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        LaneTrafficGroupRowViewModel? selected = SelectedTrafficGroup.Value;
        if (project is null || IsInitProtected.Value || selected is null)
        {
            return;
        }

        int idx = project.Init.LaneTraffic.Groups.IndexOf(selected.Definition);
        if (idx < 0)
        {
            return;
        }

        LaneTrafficGroupDefinition def = selected.Definition;
        WorldFileTarget initTarget = new WorldFileTarget(WorldFileKind.InitCsx, WorldProject.NormalizeRelativePath(project.Init.RelativePath));
        Workspace.Execute(new DelegateEditorCommand(
            $"交通グループ '{def.Key}' を削除",
            InvalidationScope.FullRebuild,
            initTarget,
            () => project.Init.LaneTraffic.Groups.RemoveAt(idx),
            () => project.Init.LaneTraffic.Groups.Insert(idx, def)));
    }

    private static IReadOnlyList<WorldFileTarget> GetManifestTargets(WorldProject project)
    {
        string manifestRel = WorldProject.NormalizeRelativePath(
            System.IO.Path.ChangeExtension(project.Placement.RelativePath, ".manifest.xml"));
        return new WorldFileTarget[]
        {
            new WorldFileTarget(WorldFileKind.Manifest, manifestRel),
            new WorldFileTarget(WorldFileKind.EditorCsx, "__Editor.csx"),
        };
    }

    private void OpenScriptExternal(bool isInit)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string relPath = isInit ? project.Init.RelativePath : project.Placement.RelativePath;
        string fullPath = Path.Combine(project.WorldDirectory, WorldProject.NormalizeRelativePath(relPath).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            Workspace.Save(dirtyOnly: false);
        }

        if (!PlayerLauncherService.OpenInExternalEditor(fullPath, out string? errorMsg))
        {
            Workspace.LastStatusMessage.Value = errorMsg ?? "外部エディタで開けませんでした。";
        }
        else
        {
            Workspace.LastStatusMessage.Value = $"外部エディタで開きました: {fullPath}";
        }
    }

    private void UnlockProtected(bool isInit)
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string relPath = isInit ? project.Init.RelativePath : project.Placement.RelativePath;
        Workspace.ClearProtectedFlagAndMarkDirty(relPath);
    }

    private async Task BrowsePlayerExecutableAsync()
    {
        if (DialogService is null)
        {
            return;
        }

        WorldProject? project = Workspace.CurrentProject.Value;
        string initialDir = project?.WorldDirectory ?? AppContext.BaseDirectory;
        string? picked = await DialogService.PickAssetFileAsync(
            "TransportX.Player.exe を選択",
            initialDir,
            "Executable",
            new string[] { "*.exe" });
        if (!string.IsNullOrWhiteSpace(picked))
        {
            PlayerExecutablePath.Value = picked;
        }
    }

    private void AutoDetectPlayerExecutable()
    {
        string? detected = PlayerLauncherService.ResolvePlayerExecutablePath(null);
        if (string.IsNullOrEmpty(detected))
        {
            Workspace.LastStatusMessage.Value = "TransportX.Player.exe が見つかりませんでした。参照から直接指定してください。";
            return;
        }

        PlayerExecutablePath.Value = detected;
        Workspace.LastStatusMessage.Value = $"TransportX.Player.exe を検出しました: {detected}";
    }

    private void SyncFromModel()
    {
        IsSyncing = true;
        try
        {
            WorldProject? project = Workspace.CurrentProject.Value;
            if (project is null)
            {
                WorldTitle.Value = string.Empty;
                Description.Value = string.Empty;
                Author.Value = string.Empty;
                RuntimePath.Value = string.Empty;
                Identifier.Value = string.Empty;
                AppPath.Value = string.Empty;
                MainScriptArg.Value = string.Empty;
                ManifestDependencies.Clear();
                TrafficTypes.Clear();
                TrafficGroups.Clear();
                IsInitProtected.Value = false;
                InitUserCode.Value = string.Empty;
                IsWorldProtected.Value = false;
                WorldUserCode.Value = string.Empty;
                PlayerExecutablePath.Value = string.Empty;
                return;
            }

            if (!IsCommitting)
            {
                WorldTitle.Value = project.WorldInfo.Title;
                Description.Value = project.WorldInfo.Description;
                Author.Value = project.WorldInfo.Author;
                RuntimePath.Value = project.WorldInfo.Path;
                Identifier.Value = project.WorldInfo.Identifier ?? string.Empty;
                AppPath.Value = project.WorldInfo.AppPath ?? string.Empty;
                MainScriptArg.Value = 0 < project.WorldInfo.Args.Count ? project.WorldInfo.Args[0] : string.Empty;
            }

            ManifestDependencies.Clear();
            foreach (string dep in project.Manifest.Dependencies)
            {
                ManifestDependencies.Add(dep);
            }

            TrafficTypes.Clear();
            foreach (LaneTrafficTypeDefinition t in project.Init.LaneTraffic.Types)
            {
                TrafficTypes.Add(t);
            }

            TrafficGroups.Clear();
            foreach (LaneTrafficGroupDefinition g in project.Init.LaneTraffic.Groups)
            {
                TrafficGroups.Add(new LaneTrafficGroupRowViewModel(g));
            }

            IsInitProtected.Value = project.IsProtected(project.Init.RelativePath);
            IsWorldProtected.Value = project.IsProtected(project.Placement.RelativePath);
            if (!IsCommitting)
            {
                InitUserCode.Value = project.Init.UserCode ?? string.Empty;
                WorldUserCode.Value = project.Placement.UserCode ?? string.Empty;
                PlayerExecutablePath.Value = project.Sidecar.PlayerExecutablePath ?? string.Empty;
            }
        }
        finally
        {
            IsSyncing = false;
        }
    }
}

/// <summary>
/// Display wrapper for a <see cref="LaneTrafficGroupDefinition"/> row.
/// </summary>
public sealed class LaneTrafficGroupRowViewModel
{
    public LaneTrafficGroupDefinition Definition { get; }
    public string Key => Definition.Key;
    public string TypeKeysJoined => string.Join("|", Definition.TypeKeys);
    public string ColorHex => Definition.ColorHex;

    public LaneTrafficGroupRowViewModel(LaneTrafficGroupDefinition definition)
    {
        Definition = definition;
    }
}
