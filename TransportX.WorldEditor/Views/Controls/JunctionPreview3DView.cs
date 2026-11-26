using System;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Offscreen Direct3D 11 3D preview control for <see cref="JunctionTemplateEditorDocumentViewModel"/>.
/// </summary>
public sealed class JunctionPreview3DView : TemplatePreview3DControlBase
{
    private IDisposable? Subscriptions;
    private JunctionTemplateEditorDocumentViewModel? BoundVm;

    protected override bool IsPreviewActive => BoundVm is not null && BoundVm.Is3DPreviewMode.Value;
    protected override bool ShowNetworkOverlay => BoundVm is not null && BoundVm.Show3DNetworkOverlay.Value;

    protected override WorldProject? GetProject() => BoundVm?.GetProject();

    protected override TemplatePreviewRendererBase CreateRenderer(int width, int height)
    {
        return new JunctionTemplatePreviewRenderer(width, height);
    }

    protected override void SyncPreviewTemplate(TemplatePreviewRendererBase renderer, WorldProject project)
    {
        if (BoundVm is not null && renderer is JunctionTemplatePreviewRenderer junctionRenderer)
        {
            junctionRenderer.SyncTemplate(project, BoundVm.TemplateKey);
        }
    }

    protected override void SubscribeToViewModel()
    {
        if (DataContext is not JunctionTemplateEditorDocumentViewModel vm)
        {
            BoundVm = null;
            return;
        }

        BoundVm = vm;
        vm.Reset3DCameraRequested += OnReset3DCameraRequested;

        IDisposable s1 = vm.Is3DPreviewMode.Subscribe(is3D =>
        {
            UpdateTimerState();
            if (is3D)
            {
                MarkTemplateDirtyAndRequestRender();
            }
        });
        IDisposable s2 = vm.CanvasVersion.Subscribe(_ => MarkTemplateDirtyAndRequestRender());
        IDisposable s3 = vm.Show3DNetworkOverlay.Subscribe(_ =>
        {
            if (vm.Is3DPreviewMode.Value)
            {
                RequestRender();
            }
        });

        Subscriptions = Disposable.Combine(s1, s2, s3);
    }

    protected override void UnsubscribeFromViewModel()
    {
        if (BoundVm is not null)
        {
            BoundVm.Reset3DCameraRequested -= OnReset3DCameraRequested;
        }

        Subscriptions?.Dispose();
        Subscriptions = null;
        BoundVm = null;
    }

    private void OnReset3DCameraRequested()
    {
        ResetCameraAndRequestRender();
    }
}
