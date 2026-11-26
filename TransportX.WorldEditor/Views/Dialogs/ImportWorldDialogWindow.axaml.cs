using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TransportX.WorldEditor.Serialization.Import;
using TransportX.WorldEditor.Services;

namespace TransportX.WorldEditor.Views.Dialogs;

/// <summary>
/// Modal dialog collecting source entry script/XML, target project location, and metadata
/// for importing non-editor TransportX world data into an editor-managed project.
/// </summary>
public partial class ImportWorldDialogWindow : Window
{
    private readonly IFileDialogService? DialogService;
    private string? LastAutoPopulatedSourcePath;

    public bool Confirmed { get; private set; }
    public WorldImportOptions? ResultOptions { get; private set; }

    public ImportWorldDialogWindow()
        : this(null)
    {
    }

    public ImportWorldDialogWindow(IFileDialogService? dialogService)
    {
        DialogService = dialogService;
        InitializeComponent();

        SourcePathBox.TextChanged += (_, _) => OnSourcePathChanged();
        DestParentDirBox.TextChanged += (_, _) => UpdateValidationState();
        AuthorBox.TextChanged += (_, _) => UpdateValidationState();
        WorldKeyBox.TextChanged += (_, _) => UpdateValidationState();

        UpdateValidationState();
    }

    private async void OnBrowseSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DialogService is null)
        {
            return;
        }

        string? picked = await DialogService.PickImportWorldSourceAsync();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            SourcePathBox.Text = picked;
        }
    }

    private async void OnBrowseDestClicked(object? sender, RoutedEventArgs e)
    {
        if (DialogService is null)
        {
            return;
        }

        string? folder = await DialogService.PickOpenFolderAsync("インポート先プロジェクトの親フォルダを選択 (例: Data)");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            DestParentDirBox.Text = folder;
        }
    }

    private void OnSourcePathChanged()
    {
        string rawSource = (SourcePathBox.Text ?? string.Empty).Trim();
        if (!string.Equals(rawSource, LastAutoPopulatedSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            WorldImportSourcePreview? preview = WorldProjectImporter.ReadSourcePreview(rawSource);
            if (preview is not null)
            {
                LastAutoPopulatedSourcePath = rawSource;
                AuthorBox.Text = preview.SuggestedAuthor;
                WorldKeyBox.Text = preview.SuggestedWorldKey;
                TitleBox.Text = preview.SuggestedTitle;
                DescriptionBox.Text = preview.SuggestedDescription;
                if (string.IsNullOrWhiteSpace(DestParentDirBox.Text))
                {
                    DestParentDirBox.Text = preview.SuggestedDestinationParentDirectory;
                }
            }
        }

        UpdateValidationState();
    }

    private void UpdateValidationState()
    {
        string sourcePath = (SourcePathBox.Text ?? string.Empty).Trim();
        string destParent = (DestParentDirBox.Text ?? string.Empty).Trim();
        string author = (AuthorBox.Text ?? string.Empty).Trim();
        string worldKey = (WorldKeyBox.Text ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(destParent) && !string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(worldKey))
        {
            try
            {
                string destDir = Path.GetFullPath(Path.Combine(destParent, author, worldKey));
                string destXml = Path.GetFullPath(Path.Combine(destParent, $"{author}.{worldKey}.xml"));
                OutputPreviewText.Text = $"出力先 XML: {destXml}\n出力先フォルダ: {destDir}";
            }
            catch
            {
                OutputPreviewText.Text = "出力先パスの形式が無効です。";
            }
        }
        else
        {
            OutputPreviewText.Text = "インポート元と出力先を指定してください。";
        }

        string? error = ValidateInputs(sourcePath, destParent, author, worldKey);
        if (error is not null)
        {
            ValidationMessageText.Text = error;
            ValidationMessageText.IsVisible = true;
            ImportButton.IsEnabled = false;
        }
        else
        {
            ValidationMessageText.Text = string.Empty;
            ValidationMessageText.IsVisible = false;
            ImportButton.IsEnabled = true;
        }
    }

    private static string? ValidateInputs(string sourcePath, string destParent, string author, string worldKey)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return "インポート元の .xml または .csx を指定してください。";
        }

        if (!File.Exists(sourcePath))
        {
            return "指定されたインポート元ファイルが見つかりません。";
        }

        if (string.IsNullOrWhiteSpace(destParent))
        {
            return "出力先親フォルダを指定してください。";
        }

        if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(worldKey))
        {
            return "著者名とフォルダ名 (Key) を入力してください。";
        }

        if (author.Contains('/') || author.Contains('\\') || worldKey.Contains('/') || worldKey.Contains('\\'))
        {
            return "著者名およびフォルダ名にパス区切り文字は使用できません。";
        }

        try
        {
            string destDir = Path.GetFullPath(Path.Combine(destParent, author, worldKey));
            string destXml = Path.GetFullPath(Path.Combine(destParent, $"{author}.{worldKey}.xml"));

            WorldImportSourcePreview? preview = WorldProjectImporter.ReadSourcePreview(sourcePath);
            if (preview is not null && IsSameOrSubdirectory(destDir, preview.SourceWorldDirectory))
            {
                return "インポート元と同じフォルダまたはサブフォルダには出力できません。";
            }

            if (Directory.Exists(destDir) || File.Exists(destXml))
            {
                return "出力先のプロジェクトまたはフォルダは既に存在します。別のフォルダ名を指定してください。";
            }
        }
        catch
        {
            return "パスの解決中にエラーが発生しました。有効なパスを入力してください。";
        }

        return null;
    }

    private static bool IsSameOrSubdirectory(string candidate, string root)
    {
        string c = candidate.EndsWith(Path.DirectorySeparatorChar) ? candidate : candidate + Path.DirectorySeparatorChar;
        string r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        UpdateValidationState();
        if (!ImportButton.IsEnabled)
        {
            return;
        }

        ResultOptions = new WorldImportOptions(
            (SourcePathBox.Text ?? string.Empty).Trim(),
            (DestParentDirBox.Text ?? string.Empty).Trim(),
            (AuthorBox.Text ?? string.Empty).Trim(),
            (WorldKeyBox.Text ?? string.Empty).Trim(),
            (TitleBox.Text ?? string.Empty).Trim(),
            DescriptionBox.Text ?? string.Empty,
            CopyAssets: CopyAssetsCheckBox.IsChecked ?? true,
            OverwriteExisting: false);
        Confirmed = true;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        ResultOptions = null;
        Close(false);
    }
}
