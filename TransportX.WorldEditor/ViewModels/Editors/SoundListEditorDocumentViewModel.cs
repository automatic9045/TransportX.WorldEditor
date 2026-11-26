using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

/// <summary>
/// List row display item for <c>Sounds.txt</c>.
/// </summary>
public sealed class SoundListRowViewModel : INotifyPropertyChanged
{
    private int IndexValue;
    private string KeyValue;
    private string RelativePathValue;
    private bool FileExistsValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => IndexValue;
    public string Key => KeyValue;
    public string RelativePath => RelativePathValue;
    public bool FileExists => FileExistsValue;

    public SoundListRowViewModel(int index, string key, string relativePath, bool fileExists)
    {
        IndexValue = index;
        KeyValue = key;
        RelativePathValue = relativePath;
        FileExistsValue = fileExists;
    }

    public void Update(int index, string key, string relativePath, bool fileExists)
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

        if (FileExistsValue != fileExists)
        {
            FileExistsValue = fileExists;
            OnPropertyChanged(nameof(FileExists));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Document ViewModel for editing <c>Sounds.txt</c> sound asset lists with file existence check and Undo/Redo.
/// </summary>
public sealed class SoundListEditorDocumentViewModel : EditorDocumentBase
{
    private readonly WorldWorkspaceService Workspace;
    private readonly IFileDialogService DialogService;
    private readonly string DocumentKey;
    private bool IsSyncing;
    private bool IsUpdatingList;
    private bool IsCommitting;

    public ObservableCollection<SoundListRowViewModel> Entries { get; } = new ObservableCollection<SoundListRowViewModel>();
    public BindableReactiveProperty<SoundListRowViewModel?> SelectedRow { get; }
    public BindableReactiveProperty<bool> HasSelection { get; }
    public BindableReactiveProperty<bool> HasNoEntries { get; }
    public BindableReactiveProperty<string> EntryKey { get; }
    public BindableReactiveProperty<string> EntryPath { get; }

    public ReactiveCommand<Unit> AddCommand { get; }
    public ReactiveCommand<Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit> BrowseFileCommand { get; }

    public SoundListEditorDocumentViewModel(
        WorldWorkspaceService workspace,
        IFileDialogService dialogService,
        string documentKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(documentKey);

        Workspace = workspace;
        DialogService = dialogService;
        DocumentKey = WorldProject.NormalizeRelativePath(documentKey);
        Id = $"Doc_Sounds_{DocumentKey}";
        Title = Path.GetFileName(DocumentKey);

        SelectedRow = new BindableReactiveProperty<SoundListRowViewModel?>(null).AddTo(Disposables);
        HasSelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasNoEntries = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        EntryKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        EntryPath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedRow.Subscribe(_ =>
        {
            if (!IsUpdatingList)
            {
                SyncInspectorFromSelection();
            }
        }).AddTo(Disposables);
        EntryKey.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitEdit(); }).AddTo(Disposables);
        EntryPath.Skip(1).Subscribe(_ => { if (!IsSyncing) CommitEdit(); }).AddTo(Disposables);

        AddCommand = new ReactiveCommand<Unit>(_ => AddEntry()).AddTo(Disposables);
        RemoveCommand = new ReactiveCommand<Unit>(_ => RemoveEntry()).AddTo(Disposables);
        MoveUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(-1)).AddTo(Disposables);
        MoveDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedEntry(1)).AddTo(Disposables);
        BrowseFileCommand = new ReactiveCommand<Unit>(u => { _ = BrowseSoundFileAsync(); }).AddTo(Disposables);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            Path.GetFileName(DocumentKey),
            (_, target) => target.Kind == WorldFileKind.SoundsTxt
                && string.Equals(WorldProject.NormalizeRelativePath(target.RelativePath), DocumentKey, StringComparison.OrdinalIgnoreCase));
    }

    private SoundListDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.SoundLists.TryGetValue(DocumentKey, out SoundListDocument? doc) ? doc : null;
    }

    private void AddEntry()
    {
        SoundListDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        int n = doc.Entries.Count + 1;
        SoundListEntry entry = new SoundListEntry($"Sound_{n:D3}", $"Sounds/Sound_{n:D3}.wav");
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.SoundsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"サウンド '{entry.Key}' を追加",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Entries.Add(entry),
            () => doc.Entries.Remove(entry)));

        SelectedRow.Value = Entries.LastOrDefault();
    }

    private void RemoveEntry()
    {
        SoundListDocument? doc = GetDocument();
        SoundListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        SoundListEntry removed = doc.Entries[idx];
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.SoundsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"サウンド '{removed.Key}' を削除",
            InvalidationScope.FullRebuild,
            target,
            () => doc.Entries.RemoveAt(idx),
            () => doc.Entries.Insert(idx, removed)));
    }

    private void MoveSelectedEntry(int delta)
    {
        SoundListDocument? doc = GetDocument();
        SoundListRowViewModel? selected = SelectedRow.Value;
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

        SoundListEntry moved = doc.Entries[oldIndex];
        WorldFileTarget target = new WorldFileTarget(WorldFileKind.SoundsTxt, DocumentKey);
        Workspace.Execute(new DelegateEditorCommand(
            $"サウンド '{moved.Key}' の定義順を変更",
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

    private void CommitEdit()
    {
        SoundListDocument? doc = GetDocument();
        SoundListRowViewModel? selected = SelectedRow.Value;
        if (doc is null || selected is null || selected.Index < 0 || doc.Entries.Count <= selected.Index)
        {
            return;
        }

        int idx = selected.Index;
        SoundListEntry oldEntry = doc.Entries[idx];
        SoundListEntry newEntry = new SoundListEntry(EntryKey.Value.Trim(), EntryPath.Value.Trim());
        if (oldEntry == newEntry)
        {
            return;
        }

        WorldFileTarget target = new WorldFileTarget(WorldFileKind.SoundsTxt, DocumentKey);
        IsCommitting = true;
        try
        {
            Workspace.Execute(new DelegateEditorCommand(
                $"サウンド '{oldEntry.Key}' を編集",
                InvalidationScope.FullRebuild,
                target,
                () => doc.Entries[idx] = newEntry,
                () => doc.Entries[idx] = oldEntry));
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private async Task BrowseSoundFileAsync()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        SoundListDocument? doc = GetDocument();
        if (project is null || doc is null || SelectedRow.Value is null)
        {
            return;
        }

        string listDir = Path.GetDirectoryName(Path.Combine(project.WorldDirectory, doc.RelativePath)) ?? project.WorldDirectory;
        string? picked = await DialogService.PickAssetFileAsync(
            "サウンドファイルを選択",
            listDir,
            "Audio",
            new string[] { "*.wav", "*.ogg", "*.mp3" });

        if (!string.IsNullOrEmpty(picked))
        {
            EntryPath.Value = Path.GetRelativePath(listDir, picked).Replace('\\', '/');
        }
    }

    private void SyncFromModel()
    {
        int prevIndex = SelectedRow.Value?.Index ?? -1;
        WorldProject? project = Workspace.CurrentProject.Value;
        SoundListDocument? doc = GetDocument();

        string? listDir = project is not null && doc is not null
            ? Path.GetDirectoryName(Path.Combine(project.WorldDirectory, doc.RelativePath)) ?? project.WorldDirectory
            : null;

        if (project is not null && doc is not null && listDir is not null && Entries.Count == doc.Entries.Count)
        {
            for (int i = 0; i < doc.Entries.Count; i++)
            {
                SoundListEntry entry = doc.Entries[i];
                bool exists = File.Exists(Path.GetFullPath(Path.Combine(listDir, entry.RelativePath)));
                Entries[i].Update(i, entry.Key, entry.RelativePath, exists);
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
                        SoundListEntry entry = doc.Entries[i];
                        bool exists = File.Exists(Path.GetFullPath(Path.Combine(listDir, entry.RelativePath)));
                        Entries.Add(new SoundListRowViewModel(i, entry.Key, entry.RelativePath, exists));
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

        if (!IsCommitting)
        {
            SyncInspectorFromSelection();
        }
    }

    private void SyncInspectorFromSelection()
    {
        IsSyncing = true;
        try
        {
            SoundListRowViewModel? row = SelectedRow.Value;
            if (row is null)
            {
                HasSelection.Value = false;
                EntryKey.Value = string.Empty;
                EntryPath.Value = string.Empty;
                return;
            }

            HasSelection.Value = true;
            EntryKey.Value = row.Key;
            EntryPath.Value = row.RelativePath;
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
