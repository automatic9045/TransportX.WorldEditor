using System.Collections.Generic;
using System.Threading.Tasks;

namespace TransportX.WorldEditor.Services;

/// <summary>
/// Abstracts desktop file/folder picker dialogs for testability across ViewModels.
/// </summary>
public interface IFileDialogService
{
    Task<string?> PickOpenWorldInfoAsync();
    Task<string?> PickImportWorldSourceAsync();
    Task<string?> PickOpenFolderAsync(string title);
    Task<string?> PickAssetFileAsync(string title, string initialDirectory, string filterName, IReadOnlyList<string> extensions);
    Task<IReadOnlyList<string>> PickAssetFilesAsync(string title, string initialDirectory, string filterName, IReadOnlyList<string> extensions);
}
