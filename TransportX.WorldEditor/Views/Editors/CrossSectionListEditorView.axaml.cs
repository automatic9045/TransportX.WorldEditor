using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.Views.Editors;

/// <summary>
/// View for <see cref="CrossSectionListEditorDocumentViewModel"/>.
/// </summary>
public partial class CrossSectionListEditorView : UserControl
{
    public CrossSectionListEditorView()
    {
        InitializeComponent();
    }

    private void OnEntriesListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CrossSectionListEditorDocumentViewModel vm)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            ListBoxItem? item = sourceVisual as ListBoxItem ?? sourceVisual.FindAncestorOfType<ListBoxItem>();
            if (item?.DataContext is CrossSectionListRowViewModel row)
            {
                vm.SelectedRow.Value = row;
            }
        }

        vm.OpenSelectedCrossSection();
    }
}
