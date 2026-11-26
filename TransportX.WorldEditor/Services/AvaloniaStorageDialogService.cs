using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace TransportX.WorldEditor.Services;

/// <summary>
/// Avalonia <see cref="IStorageProvider"/>-based implementation of <see cref="IFileDialogService"/>.
/// </summary>
public sealed class AvaloniaStorageDialogService : IFileDialogService
{
    private readonly Func<Window?> WindowProvider;

    public AvaloniaStorageDialogService(Func<Window?> windowProvider)
    {
        ArgumentNullException.ThrowIfNull(windowProvider);
        WindowProvider = windowProvider;
    }

    public async Task<string?> PickOpenWorldInfoAsync()
    {
        Window? owner = WindowProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "TransportX ワールド情報ファイルを開く",
            AllowMultiple = false,
            FileTypeFilter = new FilePickerFileType[]
            {
                new FilePickerFileType("WorldInfo XML")
                {
                    Patterns = new string[] { "*.xml" },
                },
            },
        });

        return 0 < files.Count ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImportWorldSourceAsync()
    {
        Window? owner = WindowProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "インポート元のワールド情報 (.xml) またはワールドスクリプト (.csx) を選択",
            AllowMultiple = false,
            FileTypeFilter = new FilePickerFileType[]
            {
                new FilePickerFileType("TransportX ワールド (.xml; .csx)")
                {
                    Patterns = new string[] { "*.xml", "*.csx" },
                },
                FilePickerFileTypes.All,
            },
        });

        return 0 < files.Count ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenFolderAsync(string title)
    {
        Window? owner = WindowProvider();
        if (owner?.StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return 0 < folders.Count ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickAssetFileAsync(
        string title,
        string initialDirectory,
        string filterName,
        IReadOnlyList<string> extensions)
    {
        IReadOnlyList<string> paths = await PickAssetFilesCoreAsync(title, initialDirectory, filterName, extensions, allowMultiple: false);
        return 0 < paths.Count ? paths[0] : null;
    }

    public Task<IReadOnlyList<string>> PickAssetFilesAsync(
        string title,
        string initialDirectory,
        string filterName,
        IReadOnlyList<string> extensions)
    {
        return PickAssetFilesCoreAsync(title, initialDirectory, filterName, extensions, allowMultiple: true);
    }

    private async Task<IReadOnlyList<string>> PickAssetFilesCoreAsync(
        string title,
        string initialDirectory,
        string filterName,
        IReadOnlyList<string> extensions,
        bool allowMultiple)
    {
        Window? owner = WindowProvider();
        if (owner?.StorageProvider is null)
        {
            return Array.Empty<string>();
        }

        IStorageFolder? suggestedStart = null;
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
        {
            suggestedStart = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
        }

        List<string> patterns = new List<string>(extensions.Count);
        foreach (string ext in extensions)
        {
            patterns.Add(ext.StartsWith("*.", StringComparison.Ordinal) ? ext : $"*.{ext.TrimStart('.')}");
        }

        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = suggestedStart,
            FileTypeFilter = new FilePickerFileType[]
            {
                new FilePickerFileType(filterName)
                {
                    Patterns = patterns,
                },
                FilePickerFileTypes.All,
            },
        });

        List<string> paths = new List<string>(files.Count);
        foreach (IStorageFile file in files)
        {
            string? localPath = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                paths.Add(localPath);
            }
        }

        return paths;
    }
}
