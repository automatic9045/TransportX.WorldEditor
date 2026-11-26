using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Rendering.Viewport;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Base Avalonia control that hosts an offscreen <see cref="TemplatePreviewRendererBase"/> and
/// blits frames into a <see cref="WriteableBitmap"/> on input and via a sparse periodic timer so
/// TransportX auto-exposure converges cleanly without freezing.
/// </summary>
public abstract class TemplatePreview3DControlBase : Control
{
    private static readonly Typeface MessageTypeface = new("Yu Gothic UI, Meiryo, Segoe UI");
    private static readonly TimeSpan PeriodicRefreshInterval = TimeSpan.FromMilliseconds(80);

    private DispatcherTimer? PeriodicTimer;
    private TemplatePreviewRendererBase? PreviewRenderer;
    private WriteableBitmap? FrameBitmap;
    private byte[]? PixelBuffer;
    private DateTime? LastRenderUtc;

    private bool HasInitialTemplateSynced;
    private bool TemplateNeedsSync = true;
    private bool RenderQueued;
    private bool IsOrbitDragging;
    private bool IsPanDragging;
    private Point LastPointerPos;
    private string? InitErrorMessage;

    protected TemplatePreview3DControlBase()
    {
        ClipToBounds = true;
    }

    protected abstract bool IsPreviewActive { get; }
    protected abstract bool ShowNetworkOverlay { get; }
    protected abstract WorldProject? GetProject();
    protected abstract TemplatePreviewRendererBase CreateRenderer(int width, int height);
    protected abstract void SyncPreviewTemplate(TemplatePreviewRendererBase renderer, WorldProject project);
    protected abstract void SubscribeToViewModel();
    protected abstract void UnsubscribeFromViewModel();

    protected void MarkTemplateDirtyAndRequestRender()
    {
        TemplateNeedsSync = true;
        if (IsPreviewActive)
        {
            RequestRender();
        }
    }

    protected void ResetCameraAndRequestRender()
    {
        if (PreviewRenderer is not null)
        {
            PreviewRenderer.ResetOrbit();
            RequestRender();
        }
    }

    protected void UpdateTimerState()
    {
        bool shouldRun = VisualRoot is not null
            && IsPreviewActive
            && IsVisible
            && IsEffectivelyVisible
            && !Design.IsDesignMode
            && OperatingSystem.IsWindows();

        if (shouldRun)
        {
            if (PeriodicTimer is null)
            {
                PeriodicTimer = new DispatcherTimer(PeriodicRefreshInterval, DispatcherPriority.Background, OnPeriodicTimerTick);
            }

            if (!PeriodicTimer.IsEnabled)
            {
                PeriodicTimer.Start();
            }
        }
        else if (PeriodicTimer is not null && PeriodicTimer.IsEnabled)
        {
            PeriodicTimer.Stop();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? double.Max(320.0, availableSize.Width) : 520.0;
        double height = double.IsFinite(availableSize.Height) ? double.Max(240.0, availableSize.Height) : 420.0;
        return new Size(width, height);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RebindViewModel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebindViewModel();
        UpdateTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopAndClearTimer();
        UnsubscribeFromViewModel();
        DisposePreviewResources();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == BoundsProperty)
        {
            UpdateTimerState();
            if (IsPreviewActive && IsVisible)
            {
                RequestRender();
            }
        }
    }

    private void RebindViewModel()
    {
        UnsubscribeFromViewModel();
        HasInitialTemplateSynced = false;
        TemplateNeedsSync = true;
        LastRenderUtc = null;

        SubscribeToViewModel();
        UpdateTimerState();

        if (IsPreviewActive)
        {
            RequestRender();
        }
        else
        {
            InvalidateVisual();
        }
    }

    private void OnPeriodicTimerTick(object? sender, EventArgs e)
    {
        if (!IsPreviewActive || !IsVisible || !IsEffectivelyVisible)
        {
            UpdateTimerState();
            return;
        }

        RequestRender();
    }

    protected void RequestRender()
    {
        if (RenderQueued)
        {
            return;
        }

        RenderQueued = true;
        Dispatcher.UIThread.Post(ExecuteRenderTick, DispatcherPriority.Render);
    }

    private void ExecuteRenderTick()
    {
        RenderQueued = false;

        if (!IsPreviewActive || !IsVisible || !IsEffectivelyVisible)
        {
            return;
        }

        if (Design.IsDesignMode || !OperatingSystem.IsWindows())
        {
            InvalidateVisual();
            return;
        }

        double scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = int.Clamp((int)double.Round(Bounds.Width * scaling), 1, 1920);
        int h = int.Clamp((int)double.Round(Bounds.Height * scaling), 1, 1080);
        if (w < 4 || h < 4)
        {
            return;
        }

        if (!EnsureRenderer(w, h))
        {
            InvalidateVisual();
            return;
        }

        TemplatePreviewRendererBase renderer = PreviewRenderer!;
        WorldProject? project = GetProject();
        if (project is null)
        {
            return;
        }

        renderer.SetShowNetworkOverlay(ShowNetworkOverlay);

        if (TemplateNeedsSync)
        {
            TemplateNeedsSync = false;
            SyncPreviewTemplate(renderer, project);
            if (!HasInitialTemplateSynced)
            {
                HasInitialTemplateSynced = true;
                renderer.ResetOrbit();
            }
        }

        int rowBytes = w * 4;
        int requiredLen = rowBytes * h;
        if (PixelBuffer is null || PixelBuffer.Length < requiredLen)
        {
            PixelBuffer = new byte[requiredLen];
        }

        DateTime now = DateTime.UtcNow;
        double? elapsedSeconds = LastRenderUtc.HasValue
            ? (now - LastRenderUtc.Value).TotalSeconds
            : null;
        LastRenderUtc = now;

        if (!renderer.RenderToRgbaBuffer(w, h, PixelBuffer, rowBytes, elapsedSeconds))
        {
            return;
        }

        EnsureBitmapSize(w, h);
        if (FrameBitmap is not null)
        {
            using ILockedFramebuffer fb = FrameBitmap.Lock();
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(PixelBuffer, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }

        InvalidateVisual();
    }

    private bool EnsureRenderer(int width, int height)
    {
        if (PreviewRenderer is not null)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(InitErrorMessage))
        {
            return false;
        }

        try
        {
            PreviewRenderer = CreateRenderer(width, height);
            return true;
        }
        catch (Exception ex)
        {
            InitErrorMessage = $"Direct3D 11 オフスクリーン 3D プレビュー初期化エラー: {ex.Message}";
            return false;
        }
    }

    private void EnsureBitmapSize(int width, int height)
    {
        if (FrameBitmap is not null
            && FrameBitmap.PixelSize.Width == width
            && FrameBitmap.PixelSize.Height == height)
        {
            return;
        }

        FrameBitmap?.Dispose();
        FrameBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96.0, 96.0),
            PixelFormats.Rgba8888,
            AlphaFormat.Opaque);
    }

    private void StopAndClearTimer()
    {
        if (PeriodicTimer is not null)
        {
            PeriodicTimer.Stop();
            PeriodicTimer.Tick -= OnPeriodicTimerTick;
            PeriodicTimer = null;
        }
    }

    private void DisposePreviewResources()
    {
        FrameBitmap?.Dispose();
        FrameBitmap = null;
        PixelBuffer = null;
        PreviewRenderer?.Dispose();
        PreviewRenderer = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect destRect = new(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#14181F")), destRect);

        if (FrameBitmap is not null && 0 < FrameBitmap.PixelSize.Width && 0 < FrameBitmap.PixelSize.Height)
        {
            Rect srcRect = new(0, 0, FrameBitmap.PixelSize.Width, FrameBitmap.PixelSize.Height);
            context.DrawImage(FrameBitmap, srcRect, destRect);
            return;
        }

        string statusMsg = !string.IsNullOrEmpty(InitErrorMessage)
            ? InitErrorMessage
            : "3D プレビューを読み込んでいます...";
        FormattedText ft = new(
            statusMsg,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            MessageTypeface,
            12,
            Brushes.Silver);
        context.DrawText(ft, new Point(16, 16));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PreviewRenderer is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        LastPointerPos = point.Position;
        e.Pointer.Capture(this);

        if (point.Properties.IsMiddleButtonPressed
            || (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            IsPanDragging = true;
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
        {
            IsOrbitDragging = true;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (PreviewRenderer is null || (!IsOrbitDragging && !IsPanDragging))
        {
            return;
        }

        Point pos = e.GetPosition(this);
        float dx = (float)(pos.X - LastPointerPos.X);
        float dy = (float)(pos.Y - LastPointerPos.Y);
        LastPointerPos = pos;

        if (IsPanDragging)
        {
            PreviewRenderer.PanBy(dx, dy);
            RequestRender();
            e.Handled = true;
        }
        else if (IsOrbitDragging)
        {
            PreviewRenderer.OrbitBy(dx * 0.45f, dy * 0.45f);
            RequestRender();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (IsOrbitDragging || IsPanDragging)
        {
            IsOrbitDragging = false;
            IsPanDragging = false;
            if (ReferenceEquals(e.Pointer.Captured, this))
            {
                e.Pointer.Capture(null);
            }

            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        IsOrbitDragging = false;
        IsPanDragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (PreviewRenderer is null)
        {
            return;
        }

        PreviewRenderer.ZoomBy((float)e.Delta.Y);
        RequestRender();
        e.Handled = true;
    }
}
