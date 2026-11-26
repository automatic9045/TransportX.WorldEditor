using Avalonia.Controls;
using Avalonia.Input;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.Views.Tools;

public partial class SceneHierarchyView : UserControl
{
    public SceneHierarchyView()
    {
        InitializeComponent();
    }

    private void OnSceneItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SceneHierarchyToolViewModel vm)
        {
            vm.FocusSelected();
        }
    }
}
