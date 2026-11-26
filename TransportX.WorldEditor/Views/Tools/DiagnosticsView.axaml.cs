using Avalonia.Controls;
using Avalonia.Input;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.Views.Tools;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    private void OnDiagnosticDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DiagnosticsToolViewModel vm && vm.SelectedEntry.Value is DiagnosticEntryViewModel entry)
        {
            vm.NavigateToEntry(entry);
        }
    }
}
