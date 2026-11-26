using System;
using System.Collections.Generic;
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
/// Interactive 2D cross-section profile editor canvas supporting pan/zoom, vertex dragging,
/// surface polyline rendering with U labels, and normal-vector direction dragging.
/// </summary>
public sealed class CrossSectionCanvas : Control
{
    private enum DragMode
    {
        None,
        Pan,
        VertexPosition,
        VertexNormal,
    }

    private IDisposable? ModelSubscriptions;
    private INotifyCollectionChanged? HookedVertices;
    private INotifyCollectionChanged? HookedSurfaces;

    private double PixelsPerMeter = 55.0;
    private double PanX;
    private double PanY;
    private DragMode CurrentDrag = DragMode.None;
    private int DragVertexIndex = -1;
    private Point LastPointerPos;

    private static readonly Typeface LabelTypeface = new Typeface("Yu Gothic UI, Meiryo, Segoe UI");

    public CrossSectionCanvas()
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
        if (DataContext is not CrossSectionEditorDocumentViewModel vm)
        {
            InvalidateVisual();
            return;
        }

        HookedVertices = vm.Vertices;
        HookedVertices.CollectionChanged += OnCollectionChanged;
        HookedSurfaces = vm.Surfaces;
        HookedSurfaces.CollectionChanged += OnCollectionChanged;

        ModelSubscriptions = Disposable.Combine(
            vm.SelectedVertex.Subscribe(_ => InvalidateVisual()),
            vm.SelectedSurface.Subscribe(_ => InvalidateVisual()));
        InvalidateVisual();
    }

    private void UnhookModel()
    {
        if (HookedVertices is not null)
        {
            HookedVertices.CollectionChanged -= OnCollectionChanged;
            HookedVertices = null;
        }

        if (HookedSurfaces is not null)
        {
            HookedSurfaces.CollectionChanged -= OnCollectionChanged;
            HookedSurfaces = null;
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

        double originX = bounds.Width * 0.5 + PanX;
        double originY = bounds.Height * 0.62 + PanY;

        DrawMetricGrid(context, bounds, originX, originY);

        if (DataContext is not CrossSectionEditorDocumentViewModel vm)
        {
            return;
        }

        CrossSectionDocument? doc = vm.GetDocument();
        if (doc is null)
        {
            return;
        }

        Dictionary<string, CrossSectionVertexModel> vertexMap = new Dictionary<string, CrossSectionVertexModel>(StringComparer.Ordinal);
        foreach (CrossSectionVertexModel v in doc.Vertices)
        {
            if (!string.IsNullOrEmpty(v.Key))
            {
                vertexMap.TryAdd(v.Key, v);
            }
        }

        int selectedSurfaceIdx = vm.SelectedSurface.Value?.Index ?? -1;
        for (int sIdx = 0; sIdx < doc.Surfaces.Count; sIdx++)
        {
            CrossSectionSurfaceModel surface = doc.Surfaces[sIdx];
            bool isSelectedSurface = sIdx == selectedSurfaceIdx;
            Pen pen = isSelectedSurface
                ? new Pen(new SolidColorBrush(Color.Parse("#FFD166")), 3.0)
                : new Pen(new SolidColorBrush(Color.Parse("#63B3ED")), 2.0);

            for (int k = 0; k + 1 < surface.VertexKeys.Count; k++)
            {
                if (vertexMap.TryGetValue(surface.VertexKeys[k], out CrossSectionVertexModel? v1)
                    && vertexMap.TryGetValue(surface.VertexKeys[k + 1], out CrossSectionVertexModel? v2))
                {
                    Point p1 = ToScreen(v1.X, v1.Y, originX, originY);
                    Point p2 = ToScreen(v2.X, v2.Y, originX, originY);
                    context.DrawLine(pen, p1, p2);
                }
            }
        }

        int selectedVertexIdx = vm.SelectedVertex.Value?.Index ?? -1;
        Pen normalPen = new Pen(new SolidColorBrush(Color.Parse("#5AD8A6")), 1.5);
        for (int i = 0; i < doc.Vertices.Count; i++)
        {
            CrossSectionVertexModel v = doc.Vertices[i];
            Point p = ToScreen(v.X, v.Y, originX, originY);
            Point nTip = GetNormalTipScreen(v, originX, originY);

            context.DrawLine(normalPen, p, nTip);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#5AD8A6")), new Rect(nTip.X - 3, nTip.Y - 3, 6, 6));

            bool isSelected = i == selectedVertexIdx;
            Color vColor = isSelected ? Color.Parse("#FF9F1C") : Colors.White;
            context.DrawEllipse(
                new SolidColorBrush(vColor),
                new Pen(Brushes.Black, 1.0),
                p,
                isSelected ? 5.5 : 4.2,
                isSelected ? 5.5 : 4.2);

            DrawLabel(context, $"{v.Key} (U={v.U:0.##})", new Point(p.X + 7, p.Y - 12),
                isSelected ? new SolidColorBrush(Color.Parse("#FFD166")) : new SolidColorBrush(Color.Parse("#C9D1D9")),
                10);
        }

        DrawLabel(context, "左クリック: 頂点/法線ドラッグ | 右/中ドラッグ: パン | ホイール: ズーム",
            new Point(10, 8), new SolidColorBrush(Color.Parse("#8B949E")), 11);
    }

    private void DrawMetricGrid(DrawingContext context, Rect bounds, double originX, double originY)
    {
        Pen minorPen = new Pen(new SolidColorBrush(Color.Parse("#1E232D")), 1);
        Pen majorPen = new Pen(new SolidColorBrush(Color.Parse("#2B3240")), 1);
        Pen axisXPen = new Pen(new SolidColorBrush(Color.Parse("#A34848")), 1.5);
        Pen axisYPen = new Pen(new SolidColorBrush(Color.Parse("#488B68")), 1.5);

        int minX = (int)Math.Floor((-originX) / PixelsPerMeter) - 1;
        int maxX = (int)Math.Ceiling((bounds.Width - originX) / PixelsPerMeter) + 1;
        for (int m = minX; m <= maxX; m++)
        {
            double sx = originX + m * PixelsPerMeter;
            Pen pen = m == 0 ? axisYPen : (m % 5 == 0 ? majorPen : minorPen);
            context.DrawLine(pen, new Point(sx, 0), new Point(sx, bounds.Height));
            if (m % 2 == 0)
            {
                DrawLabel(context, $"{m}m", new Point(sx + 2, originY + 3), new SolidColorBrush(Color.Parse("#747E91")), 10);
            }
        }

        int minY = (int)Math.Floor((originY - bounds.Height) / PixelsPerMeter) - 1;
        int maxY = (int)Math.Ceiling(originY / PixelsPerMeter) + 1;
        for (int m = minY; m <= maxY; m++)
        {
            double sy = originY - m * PixelsPerMeter;
            Pen pen = m == 0 ? axisXPen : (m % 5 == 0 ? majorPen : minorPen);
            context.DrawLine(pen, new Point(0, sy), new Point(bounds.Width, sy));
        }
    }

    private Point ToScreen(float x, float y, double originX, double originY)
    {
        return new Point(originX + x * PixelsPerMeter, originY - y * PixelsPerMeter);
    }

    private Point GetNormalTipScreen(CrossSectionVertexModel v, double originX, double originY)
    {
        Point basePt = ToScreen(v.X, v.Y, originX, originY);
        double nLenSq = v.NormalX * v.NormalX + v.NormalY * v.NormalY;
        double nx = 1e-6 < nLenSq ? v.NormalX / Math.Sqrt(nLenSq) : 0.0;
        double ny = 1e-6 < nLenSq ? v.NormalY / Math.Sqrt(nLenSq) : 1.0;
        return new Point(basePt.X + nx * 24.0, basePt.Y - ny * 24.0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not CrossSectionEditorDocumentViewModel vm)
        {
            return;
        }

        CrossSectionDocument? doc = vm.GetDocument();
        if (doc is null)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        LastPointerPos = pos;
        PointerPoint point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            CurrentDrag = DragMode.Pan;
            e.Pointer.Capture(this);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            double originX = Bounds.Width * 0.5 + PanX;
            double originY = Bounds.Height * 0.62 + PanY;

            for (int i = doc.Vertices.Count - 1; 0 <= i; i--)
            {
                Point tip = GetNormalTipScreen(doc.Vertices[i], originX, originY);
                if (Distance(pos, tip) <= 9.0)
                {
                    vm.SelectVertexIndex(i);
                    DragVertexIndex = i;
                    CurrentDrag = DragMode.VertexNormal;
                    e.Pointer.Capture(this);
                    return;
                }
            }

            for (int i = doc.Vertices.Count - 1; 0 <= i; i--)
            {
                Point vp = ToScreen(doc.Vertices[i].X, doc.Vertices[i].Y, originX, originY);
                if (Distance(pos, vp) <= 10.0)
                {
                    vm.SelectVertexIndex(i);
                    DragVertexIndex = i;
                    CurrentDrag = DragMode.VertexPosition;
                    e.Pointer.Capture(this);
                    return;
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        Vector delta = pos - LastPointerPos;
        LastPointerPos = pos;

        if (CurrentDrag == DragMode.Pan)
        {
            PanX += delta.X;
            PanY += delta.Y;
            InvalidateVisual();
            return;
        }

        if (DragVertexIndex < 0 || DataContext is not CrossSectionEditorDocumentViewModel vm)
        {
            return;
        }

        CrossSectionDocument? doc = vm.GetDocument();
        if (doc is null || doc.Vertices.Count <= DragVertexIndex)
        {
            return;
        }

        double originX = Bounds.Width * 0.5 + PanX;
        double originY = Bounds.Height * 0.62 + PanY;

        if (CurrentDrag == DragMode.VertexPosition)
        {
            float rawX = (float)((pos.X - originX) / PixelsPerMeter);
            float rawY = (float)((originY - pos.Y) / PixelsPerMeter);
            float step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.01f : 0.05f;
            float snappedX = MathF.Round(rawX / step) * step;
            float snappedY = MathF.Round(rawY / step) * step;
            vm.MoveVertexFromCanvas(DragVertexIndex, snappedX, snappedY);
        }
        else if (CurrentDrag == DragMode.VertexNormal)
        {
            CrossSectionVertexModel v = doc.Vertices[DragVertexIndex];
            Point vp = ToScreen(v.X, v.Y, originX, originY);
            float nx = (float)(pos.X - vp.X);
            float ny = (float)(vp.Y - pos.Y);
            vm.SetVertexNormalFromCanvas(DragVertexIndex, nx, ny);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        CurrentDrag = DragMode.None;
        DragVertexIndex = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = 0 < e.Delta.Y ? 1.15 : 0.87;
        PixelsPerMeter = Math.Clamp(PixelsPerMeter * factor, 12.0, 400.0);
        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
