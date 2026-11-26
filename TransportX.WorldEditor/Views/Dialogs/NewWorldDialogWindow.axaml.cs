using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TransportX.WorldEditor.Views.Dialogs;

/// <summary>
/// Input dialog collecting Author, World key, Title, and Description for creating a new world folder.
/// </summary>
public partial class NewWorldDialogWindow : Window
{
    public bool Confirmed { get; private set; }
    public string AuthorText => AuthorBox.Text ?? "Author";
    public string WorldKeyText => WorldKeyBox.Text ?? "NewWorld";
    public string TitleText => TitleBox.Text ?? "NewWorld";
    public string DescriptionText => DescriptionBox.Text ?? string.Empty;

    public NewWorldDialogWindow()
    {
        InitializeComponent();
    }

    private void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close(false);
    }
}
