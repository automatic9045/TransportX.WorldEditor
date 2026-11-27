using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using TransportX.WorldEditor.ViewModels.Dock;
using TransportX.WorldEditor.ViewModels.Editors;
using TransportX.WorldEditor.Views.Documents;
using TransportX.WorldEditor.Views.Editors;
using TransportX.WorldEditor.Views.Tools;

namespace TransportX.WorldEditor;

/// <summary>
/// Resolves concrete Avalonia views for dockable document and tool ViewModels without implementing
/// <see cref="IRecyclingDataTemplate"/>, preventing <c>Dock.Avalonia</c>'s <c>ControlRecyclingDataTemplate</c>
/// from reusing an incompatible <c>ViewportDocumentView</c> across tab switches.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        return param switch
        {
            ViewportDocumentViewModel => new ViewportDocumentView(),
            WorldSettingsDocumentViewModel => new WorldSettingsView(),
            ModelListEditorDocumentViewModel => new ModelListEditorView(),
            SoundListEditorDocumentViewModel => new SoundListEditorView(),
            CrossSectionListEditorDocumentViewModel => new CrossSectionListEditorView(),
            EnvironmentEditorDocumentViewModel => new EnvironmentEditorView(),
            LaneLayoutEditorDocumentViewModel => new LaneLayoutEditorView(),
            CrossSectionEditorDocumentViewModel => new CrossSectionEditorView(),
            SplineTemplateEditorDocumentViewModel => new SplineTemplateEditorView(),
            JunctionTemplateEditorDocumentViewModel => new JunctionTemplateEditorView(),
            SceneHierarchyToolViewModel => new SceneHierarchyView(),
            AssetBrowserToolViewModel => new AssetBrowserView(),
            InspectorToolViewModel => new InspectorView(),
            DiagnosticsToolViewModel => new DiagnosticsView(),
            TimelineToolViewModel => new SplineTimelineView(),
            _ => null,
        };
    }

    public bool Match(object? data)
    {
        return data is ViewportDocumentViewModel
            || data is WorldSettingsDocumentViewModel
            || data is ModelListEditorDocumentViewModel
            || data is SoundListEditorDocumentViewModel
            || data is CrossSectionListEditorDocumentViewModel
            || data is EnvironmentEditorDocumentViewModel
            || data is LaneLayoutEditorDocumentViewModel
            || data is CrossSectionEditorDocumentViewModel
            || data is SplineTemplateEditorDocumentViewModel
            || data is JunctionTemplateEditorDocumentViewModel
            || data is SceneHierarchyToolViewModel
            || data is AssetBrowserToolViewModel
            || data is InspectorToolViewModel
            || data is DiagnosticsToolViewModel
            || data is TimelineToolViewModel;
    }
}
