using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Serialization.Lists;
using TransportX.WorldEditor.Serialization.Storage;
using TransportX.WorldEditor.Serialization.Xml;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// List row display item for <c>CrossSections.txt</c>.
/// </summary>
public sealed class CrossSectionListRowViewModel : INotifyPropertyChanged
{
    public static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));
    public static readonly IBrush UnsavedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
    public static readonly IBrush MissingBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));

    private int IndexValue;
    private string KeyValue;
    private string RelativePathValue;
    private string StatusTextValue;
    private IBrush StatusBrushValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public string Key => KeyValue;
    public string RelativePath => RelativePathValue;
    public string StatusText => StatusTextValue;
    public IBrush StatusBrush => StatusBrushValue;

    public CrossSectionListRowViewModel(
        int index,
        string key,
        string relativePath,
        string statusText,
        IBrush statusBrush)
    {
        IndexValue = index;
        KeyValue = key;
        RelativePathValue = relativePath;
        StatusTextValue = statusText;
        StatusBrushValue = statusBrush;
    }

    public void Update(
        int index,
        string key,
        string relativePath,
        string statusText,
        IBrush statusBrush)
    {
        if (IndexValue != index)
        {
            IndexValue = index;
            OnPropertyChanged(nameof(Index));
        }

        if (!string.Equals(KeyValue, key, StringComparison.Ordinal))
        {
            KeyValue = key;
            OnPropertyChanged(nameof(Key));
        }

        if (!string.Equals(RelativePathValue, relativePath, StringComparison.Ordinal))
        {
            RelativePathValue = relativePath;
            OnPropertyChanged(nameof(RelativePath));
        }

        if (!string.Equals(StatusTextValue, statusText, StringComparison.Ordinal))
        {
            StatusTextValue = statusText;
            OnPropertyChanged(nameof(StatusText));
        }

        if (!ReferenceEquals(StatusBrushValue, statusBrush))
        {
            StatusBrushValue = statusBrush;
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Document ViewModel for editing <c>CrossSections.txt</c> cross-section lists with Undo/Redo,
/// TSV line preview, XML import/browse helpers, and quick navigation into <see cref="CrossSectionEditorDocumentViewModel"/>.
/// </summary>
public sealed class CrossSectionListEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly IFileDialogService DialogService;
    private readonly string DocumentKey;
    private readonly Action<string>? OpenCrossSectionCallback;
    private readonly Action<string>? CrossSectionRemovedCallback;
    private bool IsSyncing;
    private bool IsUpdatingList;
    private bool IsCommitting;

    public ObservableCollection<CrossSectionListRowViewModel> Entries { get; } = new();
    public BindableReactiveProperty<CrossSectionListRowViewModel?> SelectedRow { get; }
    public BindableReactiveProperty<bool> HasSelection { get; }
    public BindableReactiveProperty<bool> HasNoEntries { get; }
    public BindableReactiveProperty<string> EntryKey { get; }
    public BindableReactiveProperty<string> EntryPath { get; }
    public BindableReactiveProperty<string> ResolvedWorldPathText { get; }
    public BindableReactiveProperty<string> DocumentSummaryText { get; }
    public BindableReactiveProperty<bool> HasKeyWarning { get; }
    public BindableReactiveProperty<string> KeyWarningText { get; }
    public BindableReactiveProperty<string> LiveTsvPreview { get; }

    public ReactiveCommand<Unit> AddEntryCommand { get; }
    public ReactiveCommand<Unit> ImportXmlEntriesCommand { get; }
    public ReactiveCommand<Unit> DuplicateEntryCommand { get; }
    public ReactiveCommand<Unit> RemoveEntryCommand { get; }
    public ReactiveCommand<Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit> BrowseXmlPathCommand { get; }
    public ReactiveCommand<Unit> OpenCrossSectionEditorCommand { get; }

    public CrossSectionListEditorDocumentViewModel(
        WorldWorkspaceService workspace,
        IFileDialogService dialogService,
        string documentKey,
        Action<string>? openCrossSectionCallback = null,
        Action<string>? crossSectionRemovedCallback = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(documentKey);

        Workspace = workspace;
        DialogService = dialogService;
        DocumentKey = WorldProject.NormalizeRelativePath(documentKey);
        OpenCrossSectionCallback = openCrossSectionCallback;
        CrossSectionRemovedCallback = crossSectionRemovedCallback;
        Id = $"Doc_CrossSections_{DocumentKey}";
        Title = Path.GetFileName(DocumentKey);

        SelectedRow = new BindableReactiveProperty<CrossSectionListRowViewModel?>(null).AddTo(Disposables);
        HasSelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasNoEntries = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        EntryKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        EntryPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ResolvedWorldPathText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        DocumentSummaryText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        HasKeyWarning = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        KeyWarningText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        LiveTsvPreview = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedRow.Subscribe(_ =>
        {
            if (!IsUpdatingList)
            {
                SyncInspectorFromSelection();
            }
        }).AddTo(Disposables);

        EntryKey.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitEdit();
            }
        }).AddTo(Disposables);

        EntryPath.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                CommitEdit();
            }
        }).AddTo(Disposables);

        AddEntryCommand = new ReactiveCommand<Unit>(_ => AddNewEntry()).AddTo(Disposables);
        ImportXmlEntriesCommand = new ReactiveCommand<Unit>(u => { _ = ImportXmlEntriesAsync(); }).AddTo(Disposables);
        DuplicateEntryCommand = new ReactiveCommand<Unit>(_ => DuplicateSelectedEntry()).AddTo(Disposables);
        RemoveEntryCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedEntry()).AddTo(Disposables);
        MoveUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(-1)).AddTo(Disposables);
        MoveDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(1)).AddTo(Disposables);
        BrowseXmlPathCommand = new ReactiveCommand<Unit>(u => { _ = BrowseXmlFileAsync(); }).AddTo(Disposables);
        OpenCrossSectionEditorCommand = new ReactiveCommand<Unit>(_ => OpenSelectedCrossSection()).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            Path.GetFileName(DocumentKey),
            (_, target) => target.Kind == WorldFileKind.CrossSectionsTxt
                && string.Equals(WorldProject.NormalizeRelativePath(target.RelativePath), DocumentKey, StringComparison.OrdinalIgnoreCase));
    }

    public int ImportXmlFiles(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        if (project is null || doc is null || filePaths.Count == 0)
        {
            return 0;
        }

        List<(CrossSectionListEntry Entry, CrossSectionDocument Document)> imported = new();
        HashSet<string> plannedKeys = new(project.CrossSections.Keys, StringComparer.Ordinal);
        foreach (CrossSectionListEntry existing in doc.Entries)
        {
            plannedKeys.Add(existing.Key);
        }

        foreach (string rawPath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            if (!Workspace.TryCopyOrResolveExternalCrossSectionXml(
                rawPath,
                DocumentKey,
                null,
                out string entryRel,
                out string _,
                out CrossSectionDocument? parsedDoc,
                out string baseKey)
                || parsedDoc is null)
            {
                continue;
            }

            string finalKey = baseKey;
            int suffix = 2;
            while (!plannedKeys.Add(finalKey))
            {
                finalKey = $"{baseKey}_{suffix}";
                suffix++;
            }

            parsedDoc.Key = finalKey;
            imported.Add((new CrossSectionListEntry(finalKey, entryRel), parsedDoc));
        }

        if (imported.Count == 0)
        {
            return 0;
        }

        int insertBase = doc.Entries.Count;
        WorldFileTarget listTarget = new(WorldFileKind.CrossSectionsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            imported.Count == 1
                ? $"断面 '{imported[0].Entry.Key}' を XML から追加"
                : $"{imported.Count} 件の断面を XML から追加",
            InvalidationScope.FullRebuild,
            listTarget,
            () =>
            {
                for (int i = 0; i < imported.Count; i++)
                {
                    int idx = Math.Clamp(insertBase + i, 0, doc.Entries.Count);
                    doc.Entries.Insert(idx, imported[i].Entry);
                    project.CrossSections[imported[i].Entry.Key] = imported[i].Document;
                }
            },
            () =>
            {
                for (int i = imported.Count - 1; 0 <= i; i--)
                {
                    int idx = insertBase + i;
                    if (0 <= idx && idx < doc.Entries.Count)
                    {
                        doc.Entries.RemoveAt(idx);
                    }

                    string removedKey = imported[i].Entry.Key;
                    if (!WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, removedKey, DocumentKey))
                    {
                        project.CrossSections.Remove(removedKey);
                    }
                }
            }));

        SelectedRow.Value = Entries.LastOrDefault();
        return imported.Count;
    }

    public void OpenSelectedCrossSection()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        CrossSectionListRowViewModel? selected = SelectedRow.Value;
        if (project is null || doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        CrossSectionListEntry entry = doc.Entries[selected.Index];
        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            Workspace.LastStatusMessage.Value = "断面キーが空のため、断面エディタを開けません。";
            return;
        }

        if (Workspace.EnsureCrossSectionDocument(DocumentKey, entry.Key, entry.RelativePath))
        {
            OpenCrossSectionCallback?.Invoke(entry.Key);
        }
    }

    private CrossSectionListDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.CrossSectionLists.TryGetValue(DocumentKey, out CrossSectionListDocument? doc) ? doc : null;
    }

    private void AddNewEntry()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        if (project is null || doc is null)
        {
            return;
        }

        int n = doc.Entries.Count + 1;
        string key = $"CrossSection_{n:D3}";
        while (doc.Entries.Any(e => string.Equals(e.Key, key, StringComparison.Ordinal))
            || project.CrossSections.ContainsKey(key))
        {
            n++;
            key = $"CrossSection_{n:D3}";
        }

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);
        string xmlFullPath = Path.GetFullPath(Path.Combine(project.WorldDirectory, "CrossSections", $"{key}.xml"));
        string entryRel = Path.GetRelativePath(listDir, xmlFullPath).Replace('\\', '/');
        string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));

        CrossSectionListEntry entry = new(key, entryRel);
        CrossSectionDocument csDoc = Workspace.CreateDefaultCrossSectionDocument(key, worldRel);
        int insertIndex = doc.Entries.Count;

        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.CrossSectionsTxt, DocumentKey),
            new(WorldFileKind.CrossSectionXml, worldRel),
        };

        Workspace.Execute(new DelegateEditorCommand(
            $"断面 '{key}' を追加",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                int idx = Math.Clamp(insertIndex, 0, doc.Entries.Count);
                doc.Entries.Insert(idx, entry);
                project.CrossSections[key] = csDoc;
            },
            () =>
            {
                if (0 <= insertIndex && insertIndex < doc.Entries.Count)
                {
                    doc.Entries.RemoveAt(insertIndex);
                }

                if (!WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, key, DocumentKey))
                {
                    project.CrossSections.Remove(key);
                }
            }));

        SelectedRow.Value = Entries.LastOrDefault();
    }

    private void DuplicateSelectedEntry()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        CrossSectionListRowViewModel? selected = SelectedRow.Value;
        if (project is null || doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        CrossSectionListEntry sourceEntry = doc.Entries[selected.Index];
        string baseKey = string.IsNullOrWhiteSpace(sourceEntry.Key) ? "CrossSection" : sourceEntry.Key;
        string newKey = $"{baseKey}_Copy";
        int suffix = 2;
        while (doc.Entries.Any(e => string.Equals(e.Key, newKey, StringComparison.Ordinal))
            || project.CrossSections.ContainsKey(newKey))
        {
            newKey = $"{baseKey}_Copy{suffix}";
            suffix++;
        }

        string sourceEntryDir = Path.GetDirectoryName(sourceEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "CrossSections";
        string entryRel = string.IsNullOrEmpty(sourceEntryDir)
            ? $"{newKey}.xml"
            : WorldProject.NormalizeRelativePath(Path.Combine(sourceEntryDir, $"{newKey}.xml"));

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);
        string xmlFullPath = Path.GetFullPath(Path.Combine(listDir, entryRel));
        string worldRel = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFullPath));

        CrossSectionDocument clonedDoc;
        if (project.CrossSections.TryGetValue(sourceEntry.Key, out CrossSectionDocument? srcDoc))
        {
            string serialized = CrossSectionXmlSerializer.Generate(srcDoc);
            (CrossSectionDocument? reparsed, _) = CrossSectionXmlSerializer.Parse(newKey, worldRel, xmlFullPath, serialized);
            clonedDoc = reparsed ?? Workspace.CreateDefaultCrossSectionDocument(newKey, worldRel);
        }
        else
        {
            clonedDoc = Workspace.CreateDefaultCrossSectionDocument(newKey, worldRel);
        }

        CrossSectionListEntry newEntry = new(newKey, entryRel);
        int insertAt = selected.Index + 1;
        IReadOnlyList<WorldFileTarget> targets = new WorldFileTarget[]
        {
            new(WorldFileKind.CrossSectionsTxt, DocumentKey),
            new(WorldFileKind.CrossSectionXml, worldRel),
        };

        Workspace.Execute(new DelegateEditorCommand(
            $"断面 '{sourceEntry.Key}' を複製",
            InvalidationScope.FullRebuild,
            targets,
            () =>
            {
                int idx = Math.Clamp(insertAt, 0, doc.Entries.Count);
                doc.Entries.Insert(idx, newEntry);
                project.CrossSections[newKey] = clonedDoc;
            },
            () =>
            {
                if (0 <= insertAt && insertAt < doc.Entries.Count)
                {
                    doc.Entries.RemoveAt(insertAt);
                }

                if (!WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, newKey, DocumentKey))
                {
                    project.CrossSections.Remove(newKey);
                }
            }));

        if (0 <= insertAt && insertAt < Entries.Count)
        {
            SelectedRow.Value = Entries[insertAt];
        }
    }

    private void RemoveSelectedEntry()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        CrossSectionListRowViewModel? selected = SelectedRow.Value;
        if (project is null || doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        CrossSectionListEntry removed = doc.Entries[idx];
        CrossSectionDocument? snapshotDoc = null;
        bool removeDoc = !string.IsNullOrWhiteSpace(removed.Key)
            && project.CrossSections.TryGetValue(removed.Key, out snapshotDoc)
            && !WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, removed.Key, DocumentKey, idx);

        WorldFileTarget target = new(WorldFileKind.CrossSectionsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"断面 '{removed.Key}' を削除",
            InvalidationScope.FullRebuild,
            target,
            () =>
            {
                if (0 <= idx && idx < doc.Entries.Count)
                {
                    doc.Entries.RemoveAt(idx);
                }

                if (removeDoc)
                {
                    project.CrossSections.Remove(removed.Key);
                }
            },
            () =>
            {
                int restoreIdx = Math.Clamp(idx, 0, doc.Entries.Count);
                doc.Entries.Insert(restoreIdx, removed);
                if (removeDoc && snapshotDoc is not null)
                {
                    project.CrossSections[removed.Key] = snapshotDoc;
                }
            }));

        if (removeDoc)
        {
            CrossSectionRemovedCallback?.Invoke(removed.Key);
        }
    }

    private void MoveSelectedEntry(int delta)
    {
        CrossSectionListDocument? doc = GetDocument();
        CrossSectionListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null)
        {
            return;
        }

        int oldIndex = selected.Index;
        int newIndex = oldIndex + delta;
        if (oldIndex < 0 || doc.Entries.Count <= oldIndex || newIndex < 0 || doc.Entries.Count <= newIndex)
        {
            return;
        }

        CrossSectionListEntry moved = doc.Entries[oldIndex];
        WorldFileTarget target = new(WorldFileKind.CrossSectionsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"断面 '{moved.Key}' の定義順を変更",
            InvalidationScope.FullRebuild,
            target,
            () =>
            {
                doc.Entries.RemoveAt(oldIndex);
                doc.Entries.Insert(newIndex, moved);
            },
            () =>
            {
                doc.Entries.RemoveAt(newIndex);
                doc.Entries.Insert(oldIndex, moved);
            }));

        if (0 <= newIndex && newIndex < Entries.Count)
        {
            SelectedRow.Value = Entries[newIndex];
        }
    }

    private async Task ImportXmlEntriesAsync()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        if (project is null)
        {
            return;
        }

        string crossSectionsDir = Path.Combine(project.WorldDirectory, "CrossSections");
        string initialDir = Directory.Exists(crossSectionsDir)
            ? crossSectionsDir
            : WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);

        IReadOnlyList<string> pickedFiles = await DialogService.PickAssetFilesAsync(
            "断面 XML ファイルを選択（複数可）",
            initialDir,
            "CrossSection XML",
            new string[] { "*.xml" });

        if (0 < pickedFiles.Count)
        {
            ImportXmlFiles(pickedFiles);
        }
    }

    private async Task BrowseXmlFileAsync()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        if (project is null || doc is null || SelectedRow.Value is null)
        {
            return;
        }

        string crossSectionsDir = Path.Combine(project.WorldDirectory, "CrossSections");
        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);
        string initialDir = Directory.Exists(crossSectionsDir) ? crossSectionsDir : listDir;

        string? picked = await DialogService.PickAssetFileAsync(
            "断面 XML ファイルを選択",
            initialDir,
            "CrossSection XML",
            new string[] { "*.xml" });

        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        if (Workspace.TryCopyOrResolveExternalCrossSectionXml(
            picked,
            DocumentKey,
            EntryKey.Value,
            out string entryRel,
            out string _,
            out CrossSectionDocument? _,
            out string _))
        {
            EntryPath.Value = entryRel;
        }
    }

    private void CommitEdit()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        CrossSectionListRowViewModel? selected = SelectedRow.Value;
        if (project is null || doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        CrossSectionListEntry oldEntry = doc.Entries[idx];
        CrossSectionListEntry newEntry = new(EntryKey.Value.Trim(), EntryPath.Value.Trim());
        if (oldEntry == newEntry)
        {
            UpdateDerivedInspectorInfo(project, doc, idx, newEntry);
            return;
        }

        bool keyChanged = !string.Equals(oldEntry.Key, newEntry.Key, StringComparison.Ordinal);
        bool pathChanged = !string.Equals(oldEntry.RelativePath, newEntry.RelativePath, StringComparison.Ordinal);

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);
        string oldXmlFullPath = string.IsNullOrWhiteSpace(oldEntry.RelativePath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(listDir, oldEntry.RelativePath));
        string newXmlFullPath = string.IsNullOrWhiteSpace(newEntry.RelativePath)
            ? string.Empty
            : Path.GetFullPath(Path.Combine(listDir, newEntry.RelativePath));
        string newWorldRel = string.IsNullOrWhiteSpace(newEntry.RelativePath)
            ? string.Empty
            : WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, newXmlFullPath));

        CrossSectionDocument? oldDocSnapshot = null;
        bool oldKeyUniquelyOwned = !string.IsNullOrWhiteSpace(oldEntry.Key)
            && project.CrossSections.TryGetValue(oldEntry.Key, out oldDocSnapshot)
            && !WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, oldEntry.Key, DocumentKey, idx);

        CrossSectionDocument? loadedFromNewPath = null;
        if (pathChanged && !string.IsNullOrWhiteSpace(newEntry.Key) && !string.IsNullOrWhiteSpace(newXmlFullPath) && File.Exists(newXmlFullPath))
        {
            string xmlText = File.ReadAllText(newXmlFullPath);
            (CrossSectionDocument? parsed, _) = CrossSectionXmlSerializer.Parse(newEntry.Key, newWorldRel, newXmlFullPath, xmlText);
            loadedFromNewPath = parsed;
        }

        string oldDocRelPathSnapshot = oldDocSnapshot?.RelativePath ?? string.Empty;
        bool retargetUnsavedDocPath = pathChanged
            && loadedFromNewPath is null
            && oldDocSnapshot is not null
            && !string.IsNullOrEmpty(newWorldRel)
            && (string.IsNullOrEmpty(oldXmlFullPath) || !File.Exists(oldXmlFullPath));

        CrossSectionDocument? existingNewKeySnapshot = null;
        bool hasExistingNewKeyDoc = !string.IsNullOrWhiteSpace(newEntry.Key)
            && project.CrossSections.TryGetValue(newEntry.Key, out existingNewKeySnapshot);

        List<WorldFileTarget> targets =
        [
            new(WorldFileKind.CrossSectionsTxt, DocumentKey),
        ];
        if (retargetUnsavedDocPath && oldDocSnapshot is not null)
        {
            targets.Add(new(WorldFileKind.CrossSectionXml, newWorldRel));
        }

        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"断面 '{oldEntry.Key}' を編集",
                InvalidationScope.FullRebuild,
                targets,
                () =>
                {
                    doc.Entries[idx] = newEntry;

                    if (keyChanged && oldKeyUniquelyOwned && oldDocSnapshot is not null)
                    {
                        project.CrossSections.Remove(oldEntry.Key);
                        if (!string.IsNullOrWhiteSpace(newEntry.Key) && !project.CrossSections.ContainsKey(newEntry.Key))
                        {
                            oldDocSnapshot.Key = newEntry.Key;
                            project.CrossSections[newEntry.Key] = oldDocSnapshot;
                        }
                    }

                    if (loadedFromNewPath is not null && !string.IsNullOrWhiteSpace(newEntry.Key))
                    {
                        project.CrossSections[newEntry.Key] = loadedFromNewPath;
                    }
                    else if (retargetUnsavedDocPath && oldDocSnapshot is not null)
                    {
                        oldDocSnapshot.RelativePath = newWorldRel;
                    }
                },
                () =>
                {
                    doc.Entries[idx] = oldEntry;

                    if (loadedFromNewPath is not null && !string.IsNullOrWhiteSpace(newEntry.Key))
                    {
                        if (hasExistingNewKeyDoc && existingNewKeySnapshot is not null)
                        {
                            project.CrossSections[newEntry.Key] = existingNewKeySnapshot;
                        }
                        else if (!WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, newEntry.Key, DocumentKey, idx))
                        {
                            project.CrossSections.Remove(newEntry.Key);
                        }
                    }

                    if (keyChanged && oldKeyUniquelyOwned && oldDocSnapshot is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(newEntry.Key)
                            && ReferenceEquals(project.CrossSections.GetValueOrDefault(newEntry.Key), oldDocSnapshot))
                        {
                            project.CrossSections.Remove(newEntry.Key);
                        }

                        oldDocSnapshot.Key = oldEntry.Key;
                        project.CrossSections[oldEntry.Key] = oldDocSnapshot;
                    }

                    if (retargetUnsavedDocPath && oldDocSnapshot is not null)
                    {
                        oldDocSnapshot.RelativePath = oldDocRelPathSnapshot;
                    }
                }));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void SyncFromModel()
    {
        int prevIndex = SelectedRow.Value?.Index ?? -1;
        WorldProject? project = Workspace.CurrentProject.Value;
        CrossSectionListDocument? doc = GetDocument();
        string? listDir = project is not null && doc is not null
            ? WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey)
            : null;

        if (project is not null && doc is not null && listDir is not null && Entries.Count == doc.Entries.Count)
        {
            for (int i = 0; i < doc.Entries.Count; i++)
            {
                CrossSectionListEntry entry = doc.Entries[i];
                (string statusText, IBrush statusBrush) = EvaluateRowStatus(project, listDir, entry);
                Entries[i].Update(i, entry.Key, entry.RelativePath, statusText, statusBrush);
            }
        }
        else
        {
            IsUpdatingList = true;
            try
            {
                Entries.Clear();
                if (project is not null && doc is not null && listDir is not null)
                {
                    for (int i = 0; i < doc.Entries.Count; i++)
                    {
                        CrossSectionListEntry entry = doc.Entries[i];
                        (string statusText, IBrush statusBrush) = EvaluateRowStatus(project, listDir, entry);
                        Entries.Add(new CrossSectionListRowViewModel(i, entry.Key, entry.RelativePath, statusText, statusBrush));
                    }
                }

                SelectedRow.Value = 0 <= prevIndex && prevIndex < Entries.Count
                    ? Entries[prevIndex]
                    : Entries.FirstOrDefault();
            }
            finally
            {
                IsUpdatingList = false;
            }
        }

        HasNoEntries.Value = Entries.Count == 0;

        if (IsCommitting && project is not null && doc is not null && SelectedRow.Value is CrossSectionListRowViewModel activeRow
            && 0 <= activeRow.Index && activeRow.Index < doc.Entries.Count)
        {
            UpdateDerivedInspectorInfo(project, doc, activeRow.Index, doc.Entries[activeRow.Index]);
        }
        else if (!IsCommitting)
        {
            SyncInspectorFromSelection();
        }
    }

    private void SyncInspectorFromSelection()
    {
        IsSyncing = true;
        try
        {
            CrossSectionListRowViewModel? row = SelectedRow.Value;
            WorldProject? project = Workspace.CurrentProject.Value;
            CrossSectionListDocument? doc = GetDocument();
            if (row is null || project is null || doc is null || row.Index < 0 || doc.Entries.Count <= row.Index)
            {
                HasSelection.Value = false;
                EntryKey.Value = string.Empty;
                EntryPath.Value = string.Empty;
                ResolvedWorldPathText.Value = string.Empty;
                DocumentSummaryText.Value = string.Empty;
                HasKeyWarning.Value = false;
                KeyWarningText.Value = string.Empty;
                LiveTsvPreview.Value = string.Empty;
                return;
            }

            CrossSectionListEntry entry = doc.Entries[row.Index];
            HasSelection.Value = true;
            EntryKey.Value = entry.Key;
            EntryPath.Value = entry.RelativePath;
            UpdateDerivedInspectorInfo(project, doc, row.Index, entry);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void UpdateDerivedInspectorInfo(
        WorldProject project,
        CrossSectionListDocument doc,
        int index,
        CrossSectionListEntry entry)
    {
        LiveTsvPreview.Value = CrossSectionListSerializer.FormatEntryLine(entry);

        if (string.IsNullOrWhiteSpace(entry.Key))
        {
            HasKeyWarning.Value = true;
            KeyWarningText.Value = "断面キーが空です。スプラインテンプレート等から参照できる一意のキーを指定してください。";
        }
        else if (WorldWorkspaceService.IsCrossSectionKeyReferencedInOtherLists(project, entry.Key, DocumentKey, index))
        {
            HasKeyWarning.Value = true;
            KeyWarningText.Value = $"断面キー '{entry.Key}' は他のエントリでも定義されており重複しています。";
        }
        else
        {
            HasKeyWarning.Value = false;
            KeyWarningText.Value = string.Empty;
        }

        string listDir = WorldProjectStorage.GetListDirectory(project.WorldDirectory, DocumentKey);
        if (string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            ResolvedWorldPathText.Value = "(未指定)";
        }
        else
        {
            string xmlFull = Path.GetFullPath(Path.Combine(listDir, entry.RelativePath));
            ResolvedWorldPathText.Value = WorldProject.NormalizeRelativePath(Path.GetRelativePath(project.WorldDirectory, xmlFull));
        }

        if (!string.IsNullOrWhiteSpace(entry.Key)
            && project.CrossSections.TryGetValue(entry.Key, out CrossSectionDocument? csDoc))
        {
            DocumentSummaryText.Value = $"読込済 (頂点 {csDoc.Vertices.Count} / 面 {csDoc.Surfaces.Count} / マテリアル {csDoc.Materials.Count})";
        }
        else
        {
            DocumentSummaryText.Value = "未作成または未読込（「断面エディタで開く」で作成・読込できます）";
        }
    }

    private (string StatusText, IBrush StatusBrush) EvaluateRowStatus(
        WorldProject project,
        string listDir,
        CrossSectionListEntry entry)
    {
        bool fileExists = !string.IsNullOrWhiteSpace(entry.RelativePath)
            && File.Exists(Path.GetFullPath(Path.Combine(listDir, entry.RelativePath)));
        bool isLoaded = !string.IsNullOrWhiteSpace(entry.Key)
            && project.CrossSections.ContainsKey(entry.Key);

        if (fileExists)
        {
            return ("OK", CrossSectionListRowViewModel.OkBrush);
        }

        if (isLoaded)
        {
            return ("未保存", CrossSectionListRowViewModel.UnsavedBrush);
        }

        return ("未検出", CrossSectionListRowViewModel.MissingBrush);
    }
}
