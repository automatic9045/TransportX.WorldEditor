using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TransportX.WorldEditor.ViewModels;

namespace TransportX.WorldEditor.Views.Dialogs;

/// <summary>
/// Confirmation dialog shown when closing the editor while dirty files exist.
/// </summary>
public partial class UnsavedChangesDialogWindow : Window
{
    public UnsavedChangesDialogWindow()
    {
        InitializeComponent();
    }

    public void SetDirtyFiles(IReadOnlyList<string> dirtyPaths)
    {
        DirtyFilesListBox.ItemsSource = dirtyPaths;
    }

    private void OnSaveAndExitClicked(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesExitChoice.SaveAndExit);
    }

    private void OnDiscardAndExitClicked(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesExitChoice.DiscardAndExit);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesExitChoice.Cancel);
    }
}
