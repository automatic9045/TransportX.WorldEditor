using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.ViewModels.Editors;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Interactive 2D top-down plan-view canvas (<c>+X</c> right, <c>+Z</c> up) for
/// <see cref="JunctionTemplateEditorDocumentViewModel"/>: renders ports, relays, wired lane paths,
/// auxiliary curves, and static props with pan/zoom and drag positioning.
/// </summary>
public sealed class JunctionPlanCanvas : Control
{
    private IDisposable? Subscriptions;
    private double PixelsPerMeter = 12.0;
    private double PanX;
    private double PanY;
    private bool IsPanning;
    private int DraggedPortIndex = -1;
    private int DraggedRelayIndex = -1;
    private Point LastPointerPos;

    private static readonly Typeface LabelTypeface = new Typeface("Yu Gothic UI, Meiryo, Segoe UI");

    public JunctionPlanCanvas()
    {
        ClipToBounds = true;
    }

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? double.Max(320.0, availableSize.Width) : 520.0;
        double height = double.IsFinite(availableSize.Height) ? double.Max(240.0, availableSize.Height) : 420.0;
        return new Avalonia.Size(width, height);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscriptions?.Dispose();
        Subscriptions = null;
    }

    private void Rebind()
    {
        Subscriptions?.Dispose();
        Subscriptions = null;

        if (DataContext is not JunctionTemplateEditorDocumentViewModel vm)
        {
            InvalidateVisual();
            return;
        }

        IDisposable s1 = vm.CanvasVersion.Subscribe(_ => InvalidateVisual());
        IDisposable s2 = vm.SelectedPort.Subscribe(_ => InvalidateVisual());
        IDisposable s3 = vm.SelectedRelay.Subscribe(_ => InvalidateVisual());
        IDisposable s4 = vm.SelectedPath.Subscribe(_ => InvalidateVisual());
        IDisposable s5 = vm.SelectedCurve.Subscribe(_ => InvalidateVisual());
        IDisposable s6 = vm.SelectedStaticProp.Subscribe(_ => InvalidateVisual());
        IDisposable s7 = vm.SelectedEditorTabIndex.Subscribe(_ => InvalidateVisual());
        Subscriptions = Disposable.Combine(s1, s2, s3, s4, s5, s6, s7);
        InvalidateVisual();
    }

    internal static bool ShouldHighlightPath(JunctionTemplateEditorDocumentViewModel vm, int pathIndex)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return vm.SelectedEditorTabIndex.Value == JunctionTemplateEditorDocumentViewModel.PathEditorTabIndex
            && (vm.SelectedPath.Value?.Index ?? -1) == pathIndex;
    }

    internal static bool ShouldHighlightCurve(JunctionTemplateEditorDocumentViewModel vm, int curveIndex)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return vm.SelectedEditorTabIndex.Value == JunctionTemplateEditorDocumentViewModel.CurveEditorTabIndex
            && (vm.SelectedCurve.Value?.Index ?? -1) == curveIndex;
    }

    private (double OriginX, double OriginY) GetScreenOrigin()
    {
        return (Bounds.Width * 0.5 + PanX, Bounds.Height * 0.62 + PanY);
    }

    private Point ToScreen(double worldX, double worldZ, double originX, double originY)
    {
        return new Point(originX + worldX * PixelsPerMeter, originY - worldZ * PixelsPerMeter);
    }

    private (double WorldX, double WorldZ) ToWorld(Point screenPoint, double originX, double originY)
    {
        return ((screenPoint.X - originX) / PixelsPerMeter, (originY - screenPoint.Y) / PixelsPerMeter);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#14181F")), new Rect(0, 0, bounds.Width, bounds.Height));

        if (DataContext is not JunctionTemplateEditorDocumentViewModel vm)
        {
            return;
        }

        WorldProject? project = vm.GetProject();
        JunctionTemplateDocument? doc = vm.GetDocument();
        (double originX, double originY) = GetScreenOrigin();
        DrawGrid(context, bounds, originX, originY);

        if (project is null || doc is null)
        {
            return;
        }

        // 1. Auxiliary Curves (AddCurve) - highlight only while on Auxiliary Curves tab
        for (int i = 0; i < doc.Curves.Count; i++)
        {
            JunctionCurveTemplateModel curve = doc.Curves[i];
            JunctionSampledCurveResult sampled = JunctionAnchorUtilities.SampleCurveGeometry(doc, project.LaneLayouts, curve);
            bool isSel = ShouldHighlightCurve(vm, i);
            Color col = isSel ? Color.Parse("#FFAE42") : Color.Parse("#A66F2C");
            DrawPolylinePoints(context, sampled.Points, col, isSel ? 2.6 : 1.5, originX, originY, dashed: !isSel);
        }

        // 2. Wired Paths (Wire) - highlight only while on Paths tab
        for (int i = 0; i < doc.Paths.Count; i++)
        {
            JunctionPathTemplateModel path = doc.Paths[i];
            JunctionSampledCurveResult sampled = JunctionAnchorUtilities.SamplePathGeometry(doc, project.LaneLayouts, path);
            bool isSel = ShouldHighlightPath(vm, i);
            Color col = isSel ? Color.Parse("#FFE066") : Color.Parse("#4DA6FF");
            DrawPolylinePoints(context, sampled.Points, col, isSel ? 2.8 : 1.6, originX, originY, dashed: false);
        }

        // 3. Static Props
        int selectedPropIdx = vm.SelectedStaticProp.Value?.Index ?? -1;
        for (int i = 0; i < doc.Props.Count; i++)
        {
            JunctionStaticPropPlacement sp = doc.Props[i];
            Point p = ToScreen(sp.Pose.X, sp.Pose.Z, originX, originY);
            bool isSel = i == selectedPropIdx;
            IBrush fill = new SolidColorBrush(isSel ? Color.Parse("#FFDF66") : Color.Parse("#8BC34A"));
            context.DrawEllipse(fill, null, p, isSel ? 5.5 : 4.0, isSel ? 5.5 : 4.0);
            DrawLabel(context, sp.ModelKey, new Point(p.X + 7, p.Y - 6), Brushes.LightGreen, 10);
        }

        // 4. Relays
        int selectedRelayIdx = vm.SelectedRelay.Value?.Index ?? -1;
        for (int i = 0; i < doc.Relays.Count; i++)
        {
            JunctionRelayDefinition relay = doc.Relays[i];
            DrawPortBar(context, doc, project, relay.InputKey, relay.LayoutKey, Pose3D.FromDegrees(relay.Pose), i == selectedRelayIdx, isRelay: true, originX, originY);
        }

        // 5. Ports
        int selectedPortIdx = vm.SelectedPort.Value?.Index ?? -1;
        for (int i = 0; i < doc.Ports.Count; i++)
        {
            JunctionPortDefinition port = doc.Ports[i];
            DrawPortBar(context, doc, project, port.Key, port.LayoutKey, Pose3D.FromDegrees(port.Pose), i == selectedPortIdx, isRelay: false, originX, originY);
        }

        DrawLabel(context, $"2D 平面図 (+X右 / +Z上)  1m/{PixelsPerMeter:0.#}px", new Point(10, 8), Brushes.Silver, 11);
    }

    private void DrawGrid(DrawingContext context, Rect bounds, double originX, double originY)
    {
        double stepMeters = PixelsPerMeter < 6.0 ? 10.0 : (PixelsPerMeter < 14.0 ? 5.0 : 2.0);
        Pen minorPen = new Pen(new SolidColorBrush(Color.Parse("#222833")), 1);
        Pen axisPen = new Pen(new SolidColorBrush(Color.Parse("#4A5568")), 1.5);

        double leftM = (0 - originX) / PixelsPerMeter;
        double rightM = (bounds.Width - originX) / PixelsPerMeter;
        int startX = (int)double.Floor(leftM / stepMeters) - 1;
        int endX = (int)double.Ceiling(rightM / stepMeters) + 1;
        for (int ix = startX; ix <= endX; ix++)
        {
            double x = originX + ix * stepMeters * PixelsPerMeter;
            context.DrawLine(ix == 0 ? axisPen : minorPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        double bottomM = (originY - bounds.Height) / PixelsPerMeter;
        double topM = originY / PixelsPerMeter;
        int startZ = (int)double.Floor(bottomM / stepMeters) - 1;
        int endZ = (int)double.Ceiling(topM / stepMeters) + 1;
        for (int iz = startZ; iz <= endZ; iz++)
        {
            double y = originY - iz * stepMeters * PixelsPerMeter;
            context.DrawLine(iz == 0 ? axisPen : minorPen, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private void DrawPolylinePoints(
        DrawingContext context,
        IReadOnlyList<Vector3> points,
        Color color,
        double thickness,
        double originX,
        double originY,
        bool dashed)
    {
        if (points.Count < 2)
        {
            return;
        }

        Pen pen = dashed
            ? new Pen(new SolidColorBrush(color), thickness, new DashStyle(new double[] { 4, 3 }, 0))
            : new Pen(new SolidColorBrush(color), thickness);

        for (int i = 0; i < points.Count - 1; i++)
        {
            Point a = ToScreen(points[i].X, points[i].Z, originX, originY);
            Point b = ToScreen(points[i + 1].X, points[i + 1].Z, originX, originY);
            context.DrawLine(pen, a, b);
        }
    }

    private void DrawPortBar(
        DrawingContext context,
        JunctionTemplateDocument doc,
        WorldProject project,
        string labelKey,
        string layoutKey,
        Pose3D portPose,
        bool isSelected,
        bool isRelay,
        double originX,
        double originY)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, portPose.Orientation);
        Vector3 right = Vector3.Transform(Vector3.UnitX, portPose.Orientation);

        double halfExtent = 4.0;
        List<(double X, LaneFlowDirections Dir)> pins = new();
        if (project.LaneLayouts.TryGetValue(layoutKey, out LaneLayoutDocument? layout) && 0 < layout.Lanes.Count)
        {
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            foreach (LaneDefinition lane in layout.Lanes)
            {
                minX = double.Min(minX, lane.X - lane.LeftWidth);
                maxX = double.Max(maxX, lane.X + lane.RightWidth);
                pins.Add((lane.X, lane.Directions));
            }

            halfExtent = double.Max(2.0, double.Max(double.Abs(minX), double.Abs(maxX)));
        }

        Vector3 a3 = portPose.Position - right * (float)halfExtent;
        Vector3 b3 = portPose.Position + right * (float)halfExtent;
        Point pA = ToScreen(a3.X, a3.Z, originX, originY);
        Point pB = ToScreen(b3.X, b3.Z, originX, originY);
        Point center = ToScreen(portPose.Position.X, portPose.Position.Z, originX, originY);
        Point fwdEnd = ToScreen(portPose.Position.X + forward.X * 2.5, portPose.Position.Z + forward.Z * 2.5, originX, originY);

        Color barColor = isSelected
            ? Colors.Yellow
            : (isRelay ? Color.Parse("#C586C0") : Color.Parse("#4EC9B0"));
        context.DrawLine(new Pen(new SolidColorBrush(barColor), isSelected ? 3.5 : 2.2), pA, pB);
        context.DrawLine(new Pen(new SolidColorBrush(barColor), 1.8), center, fwdEnd);

        for (int i = 0; i < pins.Count; i++)
        {
            (double laneX, LaneFlowDirections dir) = pins[i];
            Vector3 pinPos = portPose.Position + right * (float)laneX;
            Point pt = ToScreen(pinPos.X, pinPos.Z, originX, originY);
            Color pinCol = dir == LaneFlowDirections.Out
                ? Color.Parse("#569CD6")
                : (dir == LaneFlowDirections.In ? Color.Parse("#F48771") : Color.Parse("#B5CEA8"));
            context.DrawEllipse(new SolidColorBrush(pinCol), new Pen(Brushes.Black, 1), pt, 4.0, 4.0);
        }

        DrawLabel(context, labelKey, new Point(center.X + 6, center.Y + 4), Brushes.White, 11);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not JunctionTemplateEditorDocumentViewModel vm)
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

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        JunctionTemplateDocument? doc = vm.GetDocument();
        WorldProject? project = vm.GetProject();
        if (doc is null || project is null)
        {
            return;
        }

        (double originX, double originY) = GetScreenOrigin();

        for (int i = 0; i < doc.Ports.Count; i++)
        {
            Point p = ToScreen(doc.Ports[i].Pose.X, doc.Ports[i].Pose.Z, originX, originY);
            if (Distance(pos, p) <= 14.0)
            {
                vm.SelectPortIndex(i);
                DraggedPortIndex = i;
                e.Pointer.Capture(this);
                return;
            }
        }

        for (int i = 0; i < doc.Relays.Count; i++)
        {
            Point p = ToScreen(doc.Relays[i].Pose.X, doc.Relays[i].Pose.Z, originX, originY);
            if (Distance(pos, p) <= 14.0)
            {
                vm.SelectRelayIndex(i);
                DraggedRelayIndex = i;
                e.Pointer.Capture(this);
                return;
            }
        }

        int hitPath = HitTestPolylineList(pos, doc, project, isPath: true, originX, originY);
        if (0 <= hitPath)
        {
            vm.SelectPathIndex(hitPath);
            return;
        }

        int hitCurve = HitTestPolylineList(pos, doc, project, isPath: false, originX, originY);
        if (0 <= hitCurve)
        {
            vm.SelectCurveIndex(hitCurve);
        }
    }

    private int HitTestPolylineList(
        Point pos,
        JunctionTemplateDocument doc,
        WorldProject project,
        bool isPath,
        double originX,
        double originY)
    {
        int bestIdx = -1;
        double bestDist = 10.0;
        int count = isPath ? doc.Paths.Count : doc.Curves.Count;

        for (int i = 0; i < count; i++)
        {
            IReadOnlyList<Vector3> pts = isPath
                ? JunctionAnchorUtilities.SamplePathGeometry(doc, project.LaneLayouts, doc.Paths[i]).Points
                : JunctionAnchorUtilities.SampleCurveGeometry(doc, project.LaneLayouts, doc.Curves[i]).Points;

            for (int k = 0; k < pts.Count - 1; k++)
            {
                Point a = ToScreen(pts[k].X, pts[k].Z, originX, originY);
                Point b = ToScreen(pts[k + 1].X, pts[k + 1].Z, originX, originY);
                double d = DistancePointToSegment2D(pos, a, b);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        Avalonia.Vector delta = pos - LastPointerPos;
        LastPointerPos = pos;

        if (IsPanning)
        {
            PanX += delta.X;
            PanY += delta.Y;
            InvalidateVisual();
            return;
        }

        if (DataContext is not JunctionTemplateEditorDocumentViewModel vm)
        {
            return;
        }

        if (0 <= DraggedPortIndex || 0 <= DraggedRelayIndex)
        {
            (double originX, double originY) = GetScreenOrigin();
            (double wx, double wz) = ToWorld(pos, originX, originY);
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.5;
            double snappedX = Math.Round(wx / step) * step;
            double snappedZ = Math.Round(wz / step) * step;

            if (0 <= DraggedPortIndex)
            {
                vm.MovePortFromCanvas(DraggedPortIndex, snappedX, snappedZ);
            }
            else if (0 <= DraggedRelayIndex)
            {
                vm.MoveRelayFromCanvas(DraggedRelayIndex, snappedX, snappedZ);
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        IsPanning = false;
        DraggedPortIndex = -1;
        DraggedRelayIndex = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = 0 < e.Delta.Y ? 1.12 : 0.89;
        PixelsPerMeter = Math.Clamp(PixelsPerMeter * factor, 2.0, 80.0);
        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistancePointToSegment2D(Point p, Point a, Point b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6)
        {
            return Distance(p, a);
        }

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
        Point proj = new Point(a.X + t * dx, a.Y + t * dy);
        return Distance(p, proj);
    }

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

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
