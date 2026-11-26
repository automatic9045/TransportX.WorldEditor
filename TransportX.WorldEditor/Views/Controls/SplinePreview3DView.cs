using System;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Rendering.Viewport;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Offscreen Direct3D 11 3D preview control for <see cref="SplineTemplateEditorDocumentViewModel"/>.
/// </summary>
public sealed class SplinePreview3DView : TemplatePreview3DControlBase
{
    private IDisposable? Subscriptions;
    private SplineTemplateEditorDocumentViewModel? BoundVm;

    protected override bool IsPreviewActive => BoundVm is not null;
    protected override bool ShowNetworkOverlay => BoundVm is not null && BoundVm.Show3DNetworkOverlay.Value;

    protected override WorldProject? GetProject() => BoundVm?.GetProject();

    protected override TemplatePreviewRendererBase CreateRenderer(int width, int height)
    {
        return new SplineTemplatePreviewRenderer(width, height);
    }

    protected override void SyncPreviewTemplate(TemplatePreviewRendererBase renderer, WorldProject project)
    {
        if (BoundVm is not null && renderer is SplineTemplatePreviewRenderer splineRenderer)
        {
            splineRenderer.SyncTemplate(project, BoundVm.TemplateKey, BoundVm.UseCurvedPreview.Value);
        }
    }

    protected override void SubscribeToViewModel()
    {
        if (DataContext is not SplineTemplateEditorDocumentViewModel vm)
        {
            BoundVm = null;
            return;
        }

        BoundVm = vm;
        vm.Reset3DCameraRequested += OnReset3DCameraRequested;

        IDisposable s1 = vm.PreviewVersion.Subscribe(_ => MarkTemplateDirtyAndRequestRender());
        IDisposable s2 = vm.Show3DNetworkOverlay.Subscribe(_ => RequestRender());
        IDisposable s3 = vm.UseCurvedPreview.Subscribe(_ => MarkTemplateDirtyAndRequestRender());

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
