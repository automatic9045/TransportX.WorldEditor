using System;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using R3;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.Views.Controls;

/// <summary>
/// Interactive multi-track longitudinal <c>s</c>-axis canvas rendering Curves, Gradients,
/// Cants, Props, PropSkips, Extrusions, ExtrusionSkips, and <c>out var s</c> anchor markers.
/// </summary>
public sealed class SplineTimelineCanvas : Control
{
    private const double HeaderLabelWidth = 118.0;
    private const double RulerHeight = 24.0;
    private const double TrackRowHeight = 26.0;
    private const int TrackCount = 7;

    private static readonly Typeface UiTypeface = new("Yu Gothic UI, Meiryo, Segoe UI");

    private IDisposable? Subscriptions;
    private INotifyCollectionChanged? HookedBlocks;
    private INotifyCollectionChanged? HookedAnchors;

    public SplineTimelineCanvas()
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
        Unhook();
    }

    private void RebindDataContext()
    {
        Unhook();
        if (DataContext is not TimelineToolViewModel vm)
        {
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        HookedBlocks = vm.TimelineBlocks;
        HookedBlocks.CollectionChanged += OnCollectionChanged;

        HookedAnchors = vm.AvailableAnchors;
        HookedAnchors.CollectionChanged += OnCollectionChanged;

        CompositeDisposable d = new();
        vm.HasSelectedSpline.Subscribe(_ => RefreshLayout()).AddTo(d);
        vm.TotalCurveLength.Subscribe(_ => RefreshLayout()).AddTo(d);
        vm.ZoomPixelsPerMeter.Subscribe(_ => RefreshLayout()).AddTo(d);
        vm.SelectedTrackKind.Subscribe(_ => InvalidateVisual()).AddTo(d);
        vm.SelectedItemIndex.Subscribe(_ => InvalidateVisual()).AddTo(d);
        Subscriptions = d;

        RefreshLayout();
    }

    private void Unhook()
    {
        if (HookedBlocks is not null)
        {
            HookedBlocks.CollectionChanged -= OnCollectionChanged;
            HookedBlocks = null;
        }

        if (HookedAnchors is not null)
        {
            HookedAnchors.CollectionChanged -= OnCollectionChanged;
            HookedAnchors = null;
        }

        Subscriptions?.Dispose();
        Subscriptions = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        TimelineToolViewModel? vm = DataContext as TimelineToolViewModel;
        double totalMeters = vm?.TotalCurveLength.Value ?? 50.0;
        double ppm = vm?.ZoomPixelsPerMeter.Value ?? 6.0;
        double width = double.Max(420.0, HeaderLabelWidth + (double.Max(20.0, totalMeters) * ppm) + 60.0);
        double height = RulerHeight + (TrackCount * TrackRowHeight) + 12.0;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#14171F")), bounds);

        if (DataContext is not TimelineToolViewModel vm || !vm.HasSelectedSpline.Value)
        {
            return;
        }

        double ppm = double.Max(0.5, vm.ZoomPixelsPerMeter.Value);
        double totalLen = double.Max(1.0, vm.TotalCurveLength.Value);

        // Header column background
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1B202B")), new Rect(0, 0, HeaderLabelWidth, bounds.Height));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1E2430")), new Rect(0, 0, bounds.Width, RulerHeight));

        // Ruler ticks
        double tickStep = DetermineMeterTickStep(ppm);
        double maxDrawMeters = double.Max(totalLen + 10.0, (bounds.Width - HeaderLabelWidth) / ppm);
        Pen tickPen = new(new SolidColorBrush(Color.Parse("#445066")), 1.0);
        Pen gridPen = new(new SolidColorBrush(Color.Parse("#232A36")), 1.0);

        for (double m = 0.0; m <= maxDrawMeters; m += tickStep)
        {
            double x = HeaderLabelWidth + (m * ppm);
            if (bounds.Width < x)
            {
                break;
            }

            context.DrawLine(tickPen, new Point(x, RulerHeight - 7.0), new Point(x, RulerHeight));
            context.DrawLine(gridPen, new Point(x, RulerHeight), new Point(x, RulerHeight + (TrackCount * TrackRowHeight)));
            FormattedText txt = CreateText($"{m:0.#}m", 10.5, Color.Parse("#AEB8C8"));
            context.DrawText(txt, new Point(x + 2.0, 4.0));
        }

        // End of spline marker
        double endX = HeaderLabelWidth + (totalLen * ppm);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#E5A93B")), 1.5),
            new Point(endX, RulerHeight),
            new Point(endX, RulerHeight + (TrackCount * TrackRowHeight)));

        // Track lanes & headers
        SolidColorBrush evenRow = new(Color.Parse("#171B24"));
        SolidColorBrush oddRow = new(Color.Parse("#1A1F29"));
        Pen rowBorder = new(new SolidColorBrush(Color.Parse("#262D3B")), 1.0);

        for (int row = 0; row < TrackCount; row++)
        {
            double y = RulerHeight + (row * TrackRowHeight);
            Rect trackRect = new(HeaderLabelWidth, y, double.Max(0.0, bounds.Width - HeaderLabelWidth), TrackRowHeight);
            context.FillRectangle(row % 2 == 0 ? evenRow : oddRow, trackRect);
            context.DrawLine(rowBorder, new Point(0, y + TrackRowHeight), new Point(bounds.Width, y + TrackRowHeight));

            FormattedText headerText = CreateText(GetTrackLabel(row), 11.5, Color.Parse("#CFD7E6"));
            context.DrawText(headerText, new Point(8.0, y + 5.0));
        }

        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#364155")), 1.0),
            new Point(HeaderLabelWidth, 0),
            new Point(HeaderLabelWidth, bounds.Height));

        // Blocks across all 7 tracks
        Pen selectedPen = new(new SolidColorBrush(Color.Parse("#FFD94A")), 2.0);
        Pen unresolvedPen = new(new SolidColorBrush(Color.Parse("#FF5555")), 1.8);
        Pen normalPen = new(new SolidColorBrush(Color.Parse("#8AA0B8")), 1.0);

        foreach (SplineTimelineBlockItem block in vm.TimelineBlocks)
        {
            int row = GetTrackRowIndex(block.Track);
            double y = RulerHeight + (row * TrackRowHeight) + 3.0;
            double h = TrackRowHeight - 6.0;
            double x0 = HeaderLabelWidth + (block.StartS * ppm);
            double x1 = HeaderLabelWidth + (block.EndS * ppm);
            double w = double.Max(12.0, x1 - x0);

            Rect blockRect = new(x0, y, w, h);
            bool isSelected = vm.SelectedTrackKind.Value == block.Track && vm.SelectedItemIndex.Value == block.Index;
            IBrush fill = GetTrackFill(block.Track, isSelected);
            Pen stroke = block.HasUnresolvedAnchor ? unresolvedPen : (isSelected ? selectedPen : normalPen);

            context.DrawRectangle(fill, stroke, blockRect, 3.0, 3.0);

            using (context.PushClip(blockRect))
            {
                FormattedText blockTxt = CreateText(block.Label, 11.0, Colors.White);
                context.DrawText(blockTxt, new Point(x0 + 4.0, y + 2.0));
            }
        }

        // Anchor vertical dashed markers
        Pen anchorPen = new(new SolidColorBrush(Color.Parse("#5CE1E6")), 1.2, new DashStyle([4.0, 3.0], 0.0));
        foreach (ResolvedAnchorInfo anchor in vm.AvailableAnchors)
        {
            double ax = HeaderLabelWidth + (anchor.ResolvedS * ppm);
            context.DrawLine(anchorPen, new Point(ax, RulerHeight), new Point(ax, RulerHeight + (TrackCount * TrackRowHeight)));

            FormattedText anchorTag = CreateText($"@{anchor.AnchorId}", 10.0, Color.Parse("#5CE1E6"));
            Rect pillRect = new(ax + 2.0, RulerHeight + 1.0, anchorTag.Width + 6.0, 14.0);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#D0142430")), pillRect, 2.0f);
            context.DrawText(anchorTag, new Point(ax + 5.0, RulerHeight + 1.0));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not TimelineToolViewModel vm || !vm.HasSelectedSpline.Value)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        if (pos.X < HeaderLabelWidth || pos.Y < RulerHeight)
        {
            return;
        }

        int row = (int)double.Floor((pos.Y - RulerHeight) / TrackRowHeight);
        if (row < 0 || TrackCount <= row)
        {
            return;
        }

        SplineTimelineTrackKind clickedTrack = GetTrackByRowIndex(row);
        double ppm = double.Max(0.5, vm.ZoomPixelsPerMeter.Value);

        SplineTimelineBlockItem? bestMatch = null;
        double bestSpan = double.MaxValue;
        foreach (SplineTimelineBlockItem block in vm.TimelineBlocks)
        {
            if (block.Track != clickedTrack)
            {
                continue;
            }

            double x0 = HeaderLabelWidth + (block.StartS * ppm);
            double x1 = HeaderLabelWidth + (block.EndS * ppm);
            double w = double.Max(12.0, x1 - x0);
            if (x0 <= pos.X && pos.X <= x0 + w)
            {
                if (w < bestSpan)
                {
                    bestSpan = w;
                    bestMatch = block;
                }
            }
        }

        if (bestMatch is not null)
        {
            vm.SelectTrackItem(bestMatch.Track, bestMatch.Index);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is TimelineToolViewModel vm && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double factor = 0.0 < e.Delta.Y ? 1.15 : (1.0 / 1.15);
            vm.ZoomPixelsPerMeter.Value = double.Clamp(vm.ZoomPixelsPerMeter.Value * factor, 1.0, 48.0);
            e.Handled = true;
        }
    }

    private static double DetermineMeterTickStep(double ppm)
    {
        if (ppm < 2.0)
        {
            return 50.0;
        }

        if (ppm < 5.0)
        {
            return 20.0;
        }

        if (ppm < 12.0)
        {
            return 10.0;
        }

        return 5.0;
    }

    private static int GetTrackRowIndex(SplineTimelineTrackKind track)
    {
        return track switch
        {
            SplineTimelineTrackKind.Curves => 0,
            SplineTimelineTrackKind.Gradients => 1,
            SplineTimelineTrackKind.Cants => 2,
            SplineTimelineTrackKind.Props => 3,
            SplineTimelineTrackKind.PropSkips => 4,
            SplineTimelineTrackKind.Extrusions => 5,
            SplineTimelineTrackKind.ExtrusionSkips => 6,
            _ => 0,
        };
    }

    private static SplineTimelineTrackKind GetTrackByRowIndex(int row)
    {
        return row switch
        {
            0 => SplineTimelineTrackKind.Curves,
            1 => SplineTimelineTrackKind.Gradients,
            2 => SplineTimelineTrackKind.Cants,
            3 => SplineTimelineTrackKind.Props,
            4 => SplineTimelineTrackKind.PropSkips,
            5 => SplineTimelineTrackKind.Extrusions,
            6 => SplineTimelineTrackKind.ExtrusionSkips,
            _ => SplineTimelineTrackKind.Curves,
        };
    }

    private static string GetTrackLabel(int row)
    {
        return row switch
        {
            0 => "円曲線",
            1 => "縦曲線",
            2 => "カント",
            3 => "プロップ",
            4 => "プロップスキップ",
            5 => "断面",
            6 => "断面スキップ",
            _ => string.Empty,
        };
    }

    private static IBrush GetTrackFill(SplineTimelineTrackKind track, bool selected)
    {
        string hex = (track, selected) switch
        {
            (SplineTimelineTrackKind.Curves, false) => "#2D5F8E",
            (SplineTimelineTrackKind.Curves, true) => "#3D7FC0",
            (SplineTimelineTrackKind.Gradients, false) => "#2E7359",
            (SplineTimelineTrackKind.Gradients, true) => "#3E9977",
            (SplineTimelineTrackKind.Cants, false) => "#64488A",
            (SplineTimelineTrackKind.Cants, true) => "#845FB8",
            (SplineTimelineTrackKind.Props, false) => "#946222",
            (SplineTimelineTrackKind.Props, true) => "#C2822E",
            (SplineTimelineTrackKind.PropSkips, false) => "#8A3636",
            (SplineTimelineTrackKind.PropSkips, true) => "#B54646",
            (SplineTimelineTrackKind.Extrusions, false) => "#2C6B7A",
            (SplineTimelineTrackKind.Extrusions, true) => "#3C8FA3",
            (SplineTimelineTrackKind.ExtrusionSkips, false) => "#82405A",
            (SplineTimelineTrackKind.ExtrusionSkips, true) => "#AC5477",
            _ => "#3B4A61",
        };
        return new SolidColorBrush(Color.Parse(hex));
    }

    private static FormattedText CreateText(string text, double size, Color color)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            UiTypeface,
            size,
            new SolidColorBrush(color));
    }
}
