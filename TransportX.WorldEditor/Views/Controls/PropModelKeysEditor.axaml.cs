using Avalonia.Controls;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Reusable compact list editor for repeating <c>PutProp</c> model key sequences,
/// including explicit empty (<c>""</c>) slots and item reordering.
/// </summary>
public partial class PropModelKeysEditor : UserControl
{
    public PropModelKeysEditor()
    {
        InitializeComponent();
    }
}
