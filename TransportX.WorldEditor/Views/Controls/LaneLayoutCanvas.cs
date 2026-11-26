using System;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using R3;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Interactive 2D cross-sectional lane layout canvas with real-time opposition mirror preview,
/// pan/zoom navigation, and Undoable pointer-drag lane positioning.
/// </summary>
public sealed class LaneLayoutCanvas : Control
{
    private IDisposable? ModelSubscriptions;
    private INotifyCollectionChanged? HookedCollection;

    private double PixelsPerMeter = 36.0;
    private double PanX;
    private double PanY;
    private int DraggedLaneIndex = -1;
    private bool IsPanning;
    private Point LastPointerPos;

    private static readonly Typeface LabelTypeface = new Typeface("Yu Gothic UI, Meiryo, Segoe UI");

    public LaneLayoutCanvas()
    {
        ClipToBounds = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RebindDataContext();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookModel();
    }

    private void RebindDataContext()
    {
        UnhookModel();
        if (DataContext is not LaneLayoutEditorDocumentViewModel vm)
        {
            InvalidateVisual();
            return;
        }

        HookedCollection = vm.Lanes;
        HookedCollection.CollectionChanged += OnCollectionChanged;

        IDisposable selSub = vm.SelectedLane.Subscribe(_ => InvalidateVisual());
        IDisposable mirrorSub = vm.ShowOppositionMirror.Subscribe(_ => InvalidateVisual());
        ModelSubscriptions = Disposable.Combine(selSub, mirrorSub);
        InvalidateVisual();
    }

    private void UnhookModel()
    {
        if (HookedCollection is not null)
        {
            HookedCollection.CollectionChanged -= OnCollectionChanged;
            HookedCollection = null;
        }

        ModelSubscriptions?.Dispose();
        ModelSubscriptions = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#15181E")), new Rect(0, 0, bounds.Width, bounds.Height));

        if (DataContext is not LaneLayoutEditorDocumentViewModel vm)
        {
            return;
        }

        double centerX = bounds.Width * 0.5 + PanX;
        double primaryOriginY = vm.ShowOppositionMirror.Value ? bounds.Height * 0.38 + PanY : bounds.Height * 0.65 + PanY;

        DrawGrid(context, bounds, centerX, primaryOriginY);
        DrawLabel(context, "ポート正面断面 (現在レイアウト)", new Point(12, 10), Brushes.LightGray, 12);

        int selectedIdx = vm.SelectedLane.Value?.Index ?? -1;
        for (int i = 0; i < vm.Lanes.Count; i++)
        {
            DrawLaneBand(context, vm.Lanes[i], centerX, primaryOriginY, i == selectedIdx, false);
        }

        if (vm.ShowOppositionMirror.Value)
        {
            double dividerY = bounds.Height * 0.55;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#3B4252")), 1.5),
                new Point(0, dividerY),
                new Point(bounds.Width, dividerY));

            double mirrorOriginY = bounds.Height * 0.80 + PanY;
            DrawLabel(context, "対向接続鏡像プレビュー (Opposition: N-1-i / -X / In↔Out / L↔R)",
                new Point(12, dividerY + 8), new SolidColorBrush(Color.Parse("#9FB3D1")), 12);

            DrawCenterAxis(context, centerX, dividerY + 26, bounds.Height, mirrorOriginY);
            for (int i = 0; i < vm.MirrorLanes.Count; i++)
            {
                DrawLaneBand(context, vm.MirrorLanes[i], centerX, mirrorOriginY, false, true);
            }
        }
    }

    private void DrawGrid(DrawingContext context, Rect bounds, double originX, double originY)
    {
        Pen minorPen = new Pen(new SolidColorBrush(Color.Parse("#1E232D")), 1);
        Pen majorPen = new Pen(new SolidColorBrush(Color.Parse("#29303D")), 1);
        Pen axisPen = new Pen(new SolidColorBrush(Color.Parse("#4C566A")), 1.5);

        double maxX = (bounds.Width - originX) / PixelsPerMeter + 2;
        double minX = (-originX) / PixelsPerMeter - 2;
        int startM = (int)Math.Floor(minX);
        int endM = (int)Math.Ceiling(maxX);

        for (int m = startM; m <= endM; m++)
        {
            double sx = originX + m * PixelsPerMeter;
            Pen p = m == 0 ? axisPen : (m % 5 == 0 ? majorPen : minorPen);
            context.DrawLine(p, new Point(sx, 0), new Point(sx, bounds.Height));
            if (m % 5 == 0)
            {
                DrawLabel(context, $"{m}m", new Point(sx + 3, originY + 4), new SolidColorBrush(Color.Parse("#7F8A9E")), 10);
            }
        }

        context.DrawLine(axisPen, new Point(0, originY), new Point(bounds.Width, originY));
    }

    private static void DrawCenterAxis(DrawingContext context, double originX, double topY, double bottomY, double groundY)
    {
        Pen axisPen = new Pen(new SolidColorBrush(Color.Parse("#4C566A")), 1);
        context.DrawLine(axisPen, new Point(originX, topY), new Point(originX, bottomY));
        context.DrawLine(axisPen, new Point(0, groundY), new Point(3000, groundY));
    }

    private void DrawLaneBand(
        DrawingContext context,
        LaneRowViewModel row,
        double originX,
        double originY,
        bool isSelected,
        bool isMirror)
    {
        Color baseColor = Color.TryParse(row.ColorHex, out Color parsed) ? parsed : Color.Parse("#4DA6FF");
        Color fill = Color.FromArgb(isSelected ? (byte)130 : (byte)80, baseColor.R, baseColor.G, baseColor.B);
        Color stroke = isSelected ? Colors.White : baseColor;

        double leftScreenX = originX + (row.X - row.LeftWidth) * PixelsPerMeter;
        double rightScreenX = originX + (row.X + row.RightWidth) * PixelsPerMeter;
        double centerScreenX = originX + row.X * PixelsPerMeter;
        double laneTopY = originY - row.Y * PixelsPerMeter - 26;
        double rectWidth = Math.Max(4.0, rightScreenX - leftScreenX);
        Rect bandRect = new Rect(leftScreenX, laneTopY, rectWidth, 26);

        context.FillRectangle(new SolidColorBrush(fill), bandRect);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(stroke), isSelected ? 2.0 : 1.2), bandRect);

        double centerY = originY - row.Y * PixelsPerMeter;
        context.DrawEllipse(new SolidColorBrush(stroke), null, new Point(centerScreenX, centerY), 3.5, 3.5);

        string dirBadge = row.Directions switch
        {
            LaneFlowDirections.In => "↑In",
            LaneFlowDirections.Out => "↓Out",
            _ => "↕InOut",
        };
        string text = isMirror ? $"M#{row.Index} {dirBadge}" : $"#{row.Index} {row.AllowedTrafficKey}\n{dirBadge}";
        DrawLabel(context, text, new Point(leftScreenX + 4, laneTopY + 2), Brushes.White, 10);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not LaneLayoutEditorDocumentViewModel vm)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        LastPointerPos = pos;
        PointerPoint point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            IsPanning = true;
            e.Pointer.Capture(this);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            double centerX = Bounds.Width * 0.5 + PanX;
            double originY = vm.ShowOppositionMirror.Value ? Bounds.Height * 0.38 + PanY : Bounds.Height * 0.65 + PanY;

            int hitIndex = -1;
            for (int i = vm.Lanes.Count - 1; 0 <= i; i--)
            {
                LaneRowViewModel row = vm.Lanes[i];
                double leftX = centerX + (row.X - row.LeftWidth) * PixelsPerMeter;
                double rightX = centerX + (row.X + row.RightWidth) * PixelsPerMeter;
                double topY = originY - row.Y * PixelsPerMeter - 26;
                Rect rect = new Rect(leftX, topY, Math.Max(4.0, rightX - leftX), 32);
                if (rect.Contains(pos))
                {
                    hitIndex = i;
                    break;
                }
            }

            if (0 <= hitIndex)
            {
                vm.SelectLaneIndex(hitIndex);
                DraggedLaneIndex = hitIndex;
                e.Pointer.Capture(this);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        Vector delta = pos - LastPointerPos;
        LastPointerPos = pos;

        if (IsPanning)
        {
            PanX += delta.X;
            PanY += delta.Y;
            InvalidateVisual();
            return;
        }

        if (0 <= DraggedLaneIndex && DataContext is LaneLayoutEditorDocumentViewModel vm)
        {
            double centerX = Bounds.Width * 0.5 + PanX;
            double originY = vm.ShowOppositionMirror.Value ? Bounds.Height * 0.38 + PanY : Bounds.Height * 0.65 + PanY;

            float rawX = (float)((pos.X - centerX) / PixelsPerMeter);
            float rawY = (float)((originY - pos.Y) / PixelsPerMeter);

            bool fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            float step = fine ? 0.05f : 0.25f;
            float snappedX = MathF.Round(rawX / step) * step;
            float snappedY = MathF.Round(rawY / step) * step;

            vm.MoveLaneFromCanvas(DraggedLaneIndex, snappedX, snappedY);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        IsPanning = false;
        DraggedLaneIndex = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = 0 < e.Delta.Y ? 1.12 : 0.89;
        PixelsPerMeter = Math.Clamp(PixelsPerMeter * factor, 10.0, 220.0);
        InvalidateVisual();
    }

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush, double fontSize)
    {
        FormattedText ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontSize,
            brush);
        context.DrawText(ft, origin);
    }
}
