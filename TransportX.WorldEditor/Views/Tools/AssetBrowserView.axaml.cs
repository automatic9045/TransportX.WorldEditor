using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.Views.Tools;

public partial class AssetBrowserView : UserControl
{
    public AssetBrowserView()
    {
        InitializeComponent();
        AssetListBox.AddHandler(
            PointerPressedEvent,
            OnAssetListPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void OnAssetListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not AssetBrowserToolViewModel vm)
        {
            return;
        }

        AssetBrowserEntry? entry = ResolveEntryFromEventSource(e.Source) ?? vm.SelectedItem.Value;
        if (entry is not null)
        {
            vm.SelectedItem.Value = entry;
            vm.OpenEntry(entry);
        }
    }

    private void OnAssetListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(AssetListBox);
        if (!point.Properties.IsRightButtonPressed && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        AssetBrowserEntry? entry = ResolveEntryFromEventSource(e.Source);
        if (entry is not null && DataContext is AssetBrowserToolViewModel vm)
        {
            vm.SelectedItem.Value = entry;
        }
    }

    private static AssetBrowserEntry? ResolveEntryFromEventSource(object? source)
    {
        if (source is not Visual sourceVisual)
        {
            return null;
        }

        ListBoxItem? item = sourceVisual as ListBoxItem ?? sourceVisual.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext as AssetBrowserEntry;
    }
}
