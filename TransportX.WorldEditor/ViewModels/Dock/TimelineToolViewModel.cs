using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Dock.Model.Mvvm.Controls;
using R3;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Assets;
using TransportX.WorldEditor.Core.Models.Placement;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Services;

namespace TransportX.WorldEditor.ViewModels.Dock;

/// <summary>
/// Identifies one of the 7 horizontal tracks displayed in the multi-track spline timeline.
/// </summary>
public enum SplineTimelineTrackKind
{
    Curves,
    Gradients,
    Cants,
    Props,
    PropSkips,
    Extrusions,
    ExtrusionSkips,
}

/// <summary>
/// Immutable visual/selection block representing a single segment or rule on a timeline track.
/// </summary>
public sealed record SplineTimelineBlockItem(
    SplineTimelineTrackKind Track,
    int Index,
    double StartS,
    double EndS,
    string Label,
    string? AnchorId,
    bool HasUnresolvedAnchor = false)
{
    public string TrackDisplayName => Track switch
    {
        SplineTimelineTrackKind.Curves => "円曲線",
        SplineTimelineTrackKind.Gradients => "縦曲線",
        SplineTimelineTrackKind.Cants => "カント",
        SplineTimelineTrackKind.Props => "プロップ",
        SplineTimelineTrackKind.PropSkips => "プロップスキップ",
        SplineTimelineTrackKind.Extrusions => "断面",
        SplineTimelineTrackKind.ExtrusionSkips => "断面スキップ",
        _ => Track.ToString(),
    };

    public string Summary => $"{TrackDisplayName} #{Index}: {Label} [{StartS:0.##}m..{EndS:0.##}m]";
}

/// <summary>
/// Multi-track spline longitudinal <c>s</c>-axis timeline ViewModel with 3-part
/// <see cref="AnchoredDistance"/> editing and automatic anchor-reference expansion on segment deletion.
/// </summary>
public sealed class TimelineToolViewModel : Tool, IDisposable
{
    private readonly WorldWorkspaceService? Workspace;
    private readonly CompositeDisposable Disposables = new();
    private bool IsSyncing;
    private bool IsCommitting;

    public BindableReactiveProperty<string> PlaceholderMessage { get; }
    public BindableReactiveProperty<bool> HasSelectedSpline { get; }
    public BindableReactiveProperty<string> HeaderTitle { get; }
    public BindableReactiveProperty<double> TotalCurveLength { get; }
    public BindableReactiveProperty<double> ZoomPixelsPerMeter { get; }

    public IReadOnlyList<SplineCurveSegmentKind> CurveKinds { get; } = Enum.GetValues<SplineCurveSegmentKind>();
    public IReadOnlyList<SplineGradientSegmentKind> GradientKinds { get; } = Enum.GetValues<SplineGradientSegmentKind>();
    public IReadOnlyList<SplineCantSegmentKind> CantKinds { get; } = Enum.GetValues<SplineCantSegmentKind>();
    public IReadOnlyList<string> DistanceModeLabels { get; } = ["絶対距離 (m)", "アンカー相対"];

    public ObservableCollection<ResolvedAnchorInfo> AvailableAnchors { get; } = new();
    public ObservableCollection<string> AvailableAnchorNames { get; } = new();
    public ObservableCollection<string> AvailableFromAnchorNames { get; } = new();
    public ObservableCollection<string> AvailableToAnchorNames { get; } = new();
    public ObservableCollection<string> AvailableCrossSectionKeys { get; } = new();
    public ObservableCollection<string> AvailableModelKeys { get; } = new();
    public ObservableCollection<SplineTimelineBlockItem> TimelineBlocks { get; } = new();

    public BindableReactiveProperty<SplineTimelineTrackKind?> SelectedTrackKind { get; }
    public BindableReactiveProperty<int> SelectedItemIndex { get; }
    public BindableReactiveProperty<SplineTimelineBlockItem?> SelectedBlock { get; }

    public BindableReactiveProperty<bool> HasSelection { get; }
    public BindableReactiveProperty<bool> IsCurveSelected { get; }
    public BindableReactiveProperty<bool> IsGradientSelected { get; }
    public BindableReactiveProperty<bool> IsCantSelected { get; }
    public BindableReactiveProperty<bool> IsPropSelected { get; }
    public BindableReactiveProperty<bool> IsSkipSelected { get; }
    public BindableReactiveProperty<bool> IsExtrusionSelected { get; }
    public BindableReactiveProperty<bool> CanReorderSelected { get; }

    // Curve segment properties
    public BindableReactiveProperty<SplineCurveSegmentKind> CurveKind { get; }
    public BindableReactiveProperty<bool> CurveHasValueParameter { get; }
    public BindableReactiveProperty<string> CurveValueLabel { get; }
    public BindableReactiveProperty<decimal?> CurveValue { get; }
    public BindableReactiveProperty<decimal?> CurveLength { get; }
    public BindableReactiveProperty<string> CurveAnchorId { get; }

    // Gradient segment properties
    public BindableReactiveProperty<SplineGradientSegmentKind> GradientKind { get; }
    public BindableReactiveProperty<bool> GradientHasDeltaParameter { get; }
    public BindableReactiveProperty<string> GradientDeltaLabel { get; }
    public BindableReactiveProperty<decimal?> GradientDelta { get; }
    public BindableReactiveProperty<decimal?> GradientLength { get; }
    public BindableReactiveProperty<string> GradientAnchorId { get; }

    // Cant segment properties
    public BindableReactiveProperty<SplineCantSegmentKind> CantKind { get; }
    public BindableReactiveProperty<bool> CantHasTargetParameter { get; }
    public BindableReactiveProperty<string> CantTargetLabel { get; }
    public BindableReactiveProperty<bool> CantHasGaugeParameter { get; }
    public BindableReactiveProperty<decimal?> CantTarget { get; }
    public BindableReactiveProperty<decimal?> CantGauge { get; }
    public BindableReactiveProperty<decimal?> CantLength { get; }
    public BindableReactiveProperty<string> CantAnchorId { get; }

    // Instance Prop properties
    public BindableReactiveProperty<string> PropRuleKey { get; }
    public BindableReactiveProperty<string> PropModelKeysText { get; }
    public BindableReactiveProperty<string> PropModelKeyToAdd { get; }
    public BindableReactiveProperty<decimal?> PropPosX { get; }
    public BindableReactiveProperty<decimal?> PropPosY { get; }
    public BindableReactiveProperty<decimal?> PropPosZ { get; }
    public BindableReactiveProperty<decimal?> PropRotX { get; }
    public BindableReactiveProperty<decimal?> PropRotY { get; }
    public BindableReactiveProperty<decimal?> PropRotZ { get; }
    public BindableReactiveProperty<decimal?> PropSpan { get; }
    public BindableReactiveProperty<decimal?> PropInterval { get; }
    public BindableReactiveProperty<decimal?> PropCount { get; }

    // Instance Extrusion properties
    public BindableReactiveProperty<string> ExtRuleKey { get; }
    public BindableReactiveProperty<string> ExtCrossSectionKey { get; }
    public BindableReactiveProperty<decimal?> ExtOffsetX { get; }
    public BindableReactiveProperty<decimal?> ExtOffsetY { get; }
    public BindableReactiveProperty<decimal?> ExtBaseInterval { get; }
    public BindableReactiveProperty<decimal?> ExtMaxDeviation { get; }
    public BindableReactiveProperty<decimal?> ExtMaxAngleDeltaDeg { get; }
    public BindableReactiveProperty<decimal?> ExtVIntervalScale { get; }
    public BindableReactiveProperty<decimal?> ExtVOffset { get; }

    // Skip Rule Key
    public BindableReactiveProperty<string> SkipRuleKey { get; }

    // 3-Part AnchoredDistance Picker (From / To)
    public BindableReactiveProperty<bool> ShowFromPicker { get; }
    public BindableReactiveProperty<bool> CanToggleFromNull { get; }
    public BindableReactiveProperty<bool> HasFromDistance { get; }
    public BindableReactiveProperty<int> FromModeIndex { get; }
    public BindableReactiveProperty<bool> FromIsRelative { get; }
    public BindableReactiveProperty<string> FromAnchorId { get; }
    public BindableReactiveProperty<decimal?> FromOffset { get; }
    public BindableReactiveProperty<string> FromResolvedHint { get; }

    public BindableReactiveProperty<bool> ShowToPicker { get; }
    public BindableReactiveProperty<bool> CanToggleToNull { get; }
    public BindableReactiveProperty<bool> HasToDistance { get; }
    public BindableReactiveProperty<int> ToModeIndex { get; }
    public BindableReactiveProperty<bool> ToIsRelative { get; }
    public BindableReactiveProperty<string> ToAnchorId { get; }
    public BindableReactiveProperty<decimal?> ToOffset { get; }
    public BindableReactiveProperty<string> ToResolvedHint { get; }

    public ReactiveCommand<Unit> ZoomInCommand { get; }
    public ReactiveCommand<Unit> ZoomOutCommand { get; }
    public ReactiveCommand<Unit> AddCurveCommand { get; }
    public ReactiveCommand<Unit> AddGradientCommand { get; }
    public ReactiveCommand<Unit> AddCantCommand { get; }
    public ReactiveCommand<Unit> AddPropCommand { get; }
    public ReactiveCommand<Unit> AddPropSkipCommand { get; }
    public ReactiveCommand<Unit> AddExtrusionCommand { get; }
    public ReactiveCommand<Unit> AddExtrusionSkipCommand { get; }
    public ReactiveCommand<Unit> RemoveSelectedItemCommand { get; }
    public ReactiveCommand<Unit> MoveSelectedUpCommand { get; }
    public ReactiveCommand<Unit> MoveSelectedDownCommand { get; }
    public ReactiveCommand<Unit> AppendModelKeyToPropCommand { get; }

    public TimelineToolViewModel()
        : this(null)
    {
    }

    public TimelineToolViewModel(WorldWorkspaceService? workspace)
    {
        Workspace = workspace;

        PlaceholderMessage = new BindableReactiveProperty<string>(
            "スプラインを選択すると距離 s 軸のマルチトラック・タイムライン (Curves / Gradients / Cants / Props / Extrusions / Skips) を編集できます").AddTo(Disposables);
        HasSelectedSpline = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HeaderTitle = new BindableReactiveProperty<string>("未選択").AddTo(Disposables);
        TotalCurveLength = new BindableReactiveProperty<double>(0.0).AddTo(Disposables);
        ZoomPixelsPerMeter = new BindableReactiveProperty<double>(6.0).AddTo(Disposables);

        SelectedTrackKind = new BindableReactiveProperty<SplineTimelineTrackKind?>(null).AddTo(Disposables);
        SelectedItemIndex = new BindableReactiveProperty<int>(-1).AddTo(Disposables);
        SelectedBlock = new BindableReactiveProperty<SplineTimelineBlockItem?>(null).AddTo(Disposables);

        HasSelection = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsCurveSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsGradientSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsCantSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsPropSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsSkipSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsExtrusionSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanReorderSelected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);

        CurveKind = new BindableReactiveProperty<SplineCurveSegmentKind>(SplineCurveSegmentKind.Straight).AddTo(Disposables);
        CurveHasValueParameter = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveValueLabel = new BindableReactiveProperty<string>("半径/曲率").AddTo(Disposables);
        CurveValue = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveLength = new BindableReactiveProperty<decimal?>(50m).AddTo(Disposables);
        CurveAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        GradientKind = new BindableReactiveProperty<SplineGradientSegmentKind>(SplineGradientSegmentKind.Constant).AddTo(Disposables);
        GradientHasDeltaParameter = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        GradientDeltaLabel = new BindableReactiveProperty<string>("変化量 (Delta)").AddTo(Disposables);
        GradientDelta = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        GradientLength = new BindableReactiveProperty<decimal?>(50m).AddTo(Disposables);
        GradientAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        CantKind = new BindableReactiveProperty<SplineCantSegmentKind>(SplineCantSegmentKind.Constant).AddTo(Disposables);
        CantHasTargetParameter = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CantTargetLabel = new BindableReactiveProperty<string>("目標値 (Target)").AddTo(Disposables);
        CantHasGaugeParameter = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CantTarget = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CantGauge = new BindableReactiveProperty<decimal?>(1.067m).AddTo(Disposables);
        CantLength = new BindableReactiveProperty<decimal?>(50m).AddTo(Disposables);
        CantAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        PropRuleKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PropModelKeysText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PropModelKeyToAdd = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PropPosX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropPosY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropPosZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropSpan = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PropInterval = new BindableReactiveProperty<decimal?>(20m).AddTo(Disposables);
        PropCount = new BindableReactiveProperty<decimal?>(100m).AddTo(Disposables);

        ExtRuleKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ExtCrossSectionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ExtOffsetX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ExtOffsetY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ExtBaseInterval = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);
        ExtMaxDeviation = new BindableReactiveProperty<decimal?>(0.01m).AddTo(Disposables);
        ExtMaxAngleDeltaDeg = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        ExtVIntervalScale = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        ExtVOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SkipRuleKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        ShowFromPicker = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanToggleFromNull = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasFromDistance = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        FromModeIndex = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        FromIsRelative = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        FromAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        FromOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        FromResolvedHint = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        ShowToPicker = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CanToggleToNull = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        HasToDistance = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ToModeIndex = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        ToIsRelative = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        ToAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        ToOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        ToResolvedHint = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        ZoomInCommand = new ReactiveCommand<Unit>(_ => ZoomPixelsPerMeter.Value = double.Clamp(ZoomPixelsPerMeter.Value * 1.25, 1.0, 48.0)).AddTo(Disposables);
        ZoomOutCommand = new ReactiveCommand<Unit>(_ => ZoomPixelsPerMeter.Value = double.Clamp(ZoomPixelsPerMeter.Value / 1.25, 1.0, 48.0)).AddTo(Disposables);
        AddCurveCommand = new ReactiveCommand<Unit>(_ => AddCurveSegment()).AddTo(Disposables);
        AddGradientCommand = new ReactiveCommand<Unit>(_ => AddGradientSegment()).AddTo(Disposables);
        AddCantCommand = new ReactiveCommand<Unit>(_ => AddCantSegment()).AddTo(Disposables);
        AddPropCommand = new ReactiveCommand<Unit>(_ => AddPropOverride()).AddTo(Disposables);
        AddPropSkipCommand = new ReactiveCommand<Unit>(_ => AddPropSkip()).AddTo(Disposables);
        AddExtrusionCommand = new ReactiveCommand<Unit>(_ => AddExtrusionOverride()).AddTo(Disposables);
        AddExtrusionSkipCommand = new ReactiveCommand<Unit>(_ => AddExtrusionSkip()).AddTo(Disposables);
        RemoveSelectedItemCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedItem()).AddTo(Disposables);
        MoveSelectedUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedItem(-1)).AddTo(Disposables);
        MoveSelectedDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedItem(1)).AddTo(Disposables);
        AppendModelKeyToPropCommand = new ReactiveCommand<Unit>(_ => AppendModelKeyToProp()).AddTo(Disposables);

        SelectedBlock.Skip(1).Subscribe(block =>
        {
            if (!IsSyncing && block is not null)
            {
                SelectTrackItem(block.Track, block.Index);
            }
        }).AddTo(Disposables);

        HookCommit(CurveKind, CommitCurveSegment);
        HookCommit(CurveValue, CommitCurveSegment);
        HookCommit(CurveLength, CommitCurveSegment);
        HookCommit(CurveAnchorId, CommitCurveSegment);

        HookCommit(GradientKind, CommitGradientSegment);
        HookCommit(GradientDelta, CommitGradientSegment);
        HookCommit(GradientLength, CommitGradientSegment);
        HookCommit(GradientAnchorId, CommitGradientSegment);

        HookCommit(CantKind, CommitCantSegment);
        HookCommit(CantTarget, CommitCantSegment);
        HookCommit(CantGauge, CommitCantSegment);
        HookCommit(CantLength, CommitCantSegment);
        HookCommit(CantAnchorId, CommitCantSegment);

        HookCommit(PropRuleKey, CommitSelectedAnchoredRule);
        HookCommit(PropModelKeysText, CommitSelectedAnchoredRule);
        HookCommit(PropPosX, CommitSelectedAnchoredRule);
        HookCommit(PropPosY, CommitSelectedAnchoredRule);
        HookCommit(PropPosZ, CommitSelectedAnchoredRule);
        HookCommit(PropRotX, CommitSelectedAnchoredRule);
        HookCommit(PropRotY, CommitSelectedAnchoredRule);
        HookCommit(PropRotZ, CommitSelectedAnchoredRule);
        HookCommit(PropSpan, CommitSelectedAnchoredRule);
        HookCommit(PropInterval, CommitSelectedAnchoredRule);
        HookCommit(PropCount, CommitSelectedAnchoredRule);

        HookCommit(ExtRuleKey, CommitSelectedAnchoredRule);
        HookCommit(ExtCrossSectionKey, CommitSelectedAnchoredRule);
        HookCommit(ExtOffsetX, CommitSelectedAnchoredRule);
        HookCommit(ExtOffsetY, CommitSelectedAnchoredRule);
        HookCommit(ExtBaseInterval, CommitSelectedAnchoredRule);
        HookCommit(ExtMaxDeviation, CommitSelectedAnchoredRule);
        HookCommit(ExtMaxAngleDeltaDeg, CommitSelectedAnchoredRule);
        HookCommit(ExtVIntervalScale, CommitSelectedAnchoredRule);
        HookCommit(ExtVOffset, CommitSelectedAnchoredRule);

        HookCommit(SkipRuleKey, CommitSelectedAnchoredRule);

        FromModeIndex.Skip(1).Subscribe(idx =>
        {
            if (IsSyncing)
            {
                return;
            }

            FromIsRelative.Value = idx == 1;
            if (idx == 1 && string.IsNullOrEmpty(FromAnchorId.Value) && 0 < AvailableAnchorNames.Count)
            {
                IsSyncing = true;
                try
                {
                    FromAnchorId.Value = AvailableAnchorNames[0];
                }
                finally
                {
                    IsSyncing = false;
                }
            }

            CommitSelectedAnchoredRule();
        }).AddTo(Disposables);

        ToModeIndex.Skip(1).Subscribe(idx =>
        {
            if (IsSyncing)
            {
                return;
            }

            ToIsRelative.Value = idx == 1;
            if (idx == 1 && string.IsNullOrEmpty(ToAnchorId.Value) && 0 < AvailableAnchorNames.Count)
            {
                IsSyncing = true;
                try
                {
                    ToAnchorId.Value = AvailableAnchorNames[0];
                }
                finally
                {
                    IsSyncing = false;
                }
            }

            CommitSelectedAnchoredRule();
        }).AddTo(Disposables);

        HookCommit(HasFromDistance, CommitSelectedAnchoredRule);
        HookCommit(FromAnchorId, CommitSelectedAnchoredRule);
        HookCommit(FromOffset, CommitSelectedAnchoredRule);
        HookCommit(HasToDistance, CommitSelectedAnchoredRule);
        HookCommit(ToAnchorId, CommitSelectedAnchoredRule);
        HookCommit(ToOffset, CommitSelectedAnchoredRule);

        if (Workspace is not null)
        {
            Workspace.SelectedSplineId.Subscribe(_ => Rebuild()).AddTo(Disposables);
            Workspace.ProjectRevision.Subscribe(_ => Rebuild()).AddTo(Disposables);
        }

        Rebuild();
    }

    public void SelectTrackItem(SplineTimelineTrackKind track, int index)
    {
        if (IsSyncing)
        {
            return;
        }

        SelectedTrackKind.Value = track;
        SelectedItemIndex.Value = index;
        SyncSelectionInspector();
    }

    private void HookCommit<T>(BindableReactiveProperty<T> property, Action commitAction)
    {
        property.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                commitAction();
            }
        }).AddTo(Disposables);
    }

    private static void SyncStringCollection(ObservableCollection<string> target, IList<string> next)
    {
        if (target.SequenceEqual(next, StringComparer.Ordinal))
        {
            return;
        }

        target.Clear();
        foreach (string item in next)
        {
            target.Add(item);
        }
    }

    private static void SyncTimelineBlocks(ObservableCollection<SplineTimelineBlockItem> target, IList<SplineTimelineBlockItem> next)
    {
        if (target.SequenceEqual(next))
        {
            return;
        }

        int sharedCount = int.Min(target.Count, next.Count);
        for (int i = 0; i < sharedCount; i++)
        {
            if (!Equals(target[i], next[i]))
            {
                target[i] = next[i];
            }
        }

        while (next.Count < target.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (int i = sharedCount; i < next.Count; i++)
        {
            target.Add(next[i]);
        }
    }

    private void Rebuild()
    {
        IsSyncing = true;
        try
        {
            WorldProject? project = Workspace?.CurrentProject.Value;
            SplinePlacementNode? spline = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
            if (project is null || spline is null)
            {
                AvailableAnchors.Clear();
                AvailableAnchorNames.Clear();
                AvailableCrossSectionKeys.Clear();
                AvailableModelKeys.Clear();
                TimelineBlocks.Clear();
                HasSelectedSpline.Value = false;
                HeaderTitle.Value = "スプライン未選択";
                TotalCurveLength.Value = 0.0;
                SelectedTrackKind.Value = null;
                SelectedItemIndex.Value = -1;
                SyncSelectionInspector();
                return;
            }

            HasSelectedSpline.Value = true;
            double totalLength = SplineAnchorUtilities.ComputeCurveTotalLength(spline);
            TotalCurveLength.Value = totalLength;
            HeaderTitle.Value = string.IsNullOrEmpty(spline.Key)
                ? $"{spline.Id} (Tpl:{spline.TemplateKey ?? "なし"}, L={totalLength:0.##}m)"
                : $"{spline.Key} [{spline.Id}] (Tpl:{spline.TemplateKey ?? "なし"}, L={totalLength:0.##}m)";

            List<string> nextCsKeys = project.CrossSections.Keys
                .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SyncStringCollection(AvailableCrossSectionKeys, nextCsKeys);

            List<string> nextModelKeys = new();
            foreach (ModelListDocument list in project.ModelLists.Values)
            {
                foreach (ModelListEntry entry in list.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && !nextModelKeys.Contains(entry.Key))
                    {
                        nextModelKeys.Add(entry.Key);
                    }
                }
            }

            SyncStringCollection(AvailableModelKeys, nextModelKeys);

            if (string.IsNullOrEmpty(PropModelKeyToAdd.Value) && 0 < AvailableModelKeys.Count)
            {
                PropModelKeyToAdd.Value = AvailableModelKeys[0];
            }

            IReadOnlyList<ResolvedAnchorInfo> resolvedAnchors = SplineAnchorUtilities.CollectSplineAnchors(spline);
            AvailableAnchors.Clear();
            List<string> nextAnchorNames = new();
            Dictionary<string, double> anchorMap = new(StringComparer.Ordinal);
            foreach (ResolvedAnchorInfo info in resolvedAnchors)
            {
                AvailableAnchors.Add(info);
                if (!nextAnchorNames.Contains(info.AnchorId))
                {
                    nextAnchorNames.Add(info.AnchorId);
                }

                anchorMap[info.AnchorId] = info.ResolvedS;
            }

            SyncStringCollection(AvailableAnchorNames, nextAnchorNames);
            List<SplineTimelineBlockItem> nextBlocks = new();

            // Track 1: Curves
            double s = 0.0;
            for (int i = 0; i < spline.Curves.Count; i++)
            {
                SplineCurveSegment seg = spline.Curves[i];
                double end = s + seg.Length;
                string label = seg.Kind switch
                {
                    SplineCurveSegmentKind.Straight => $"Straight({seg.Length:0.#}m)",
                    SplineCurveSegmentKind.ByRadius => $"R={seg.Value:0.#}m L={seg.Length:0.#}m",
                    SplineCurveSegmentKind.ByCurvature => $"K={seg.Value:0.####} L={seg.Length:0.#}m",
                    _ => seg.Kind.ToString(),
                };
                nextBlocks.Add(new SplineTimelineBlockItem(SplineTimelineTrackKind.Curves, i, s, end, label, seg.OutputAnchorId));
                s = end;
            }

            // Track 2: Gradients
            s = 0.0;
            for (int i = 0; i < spline.Gradients.Count; i++)
            {
                SplineGradientSegment seg = spline.Gradients[i];
                double end = s + seg.Length;
                string label = $"{seg.Kind} d={seg.Delta:0.##} ({seg.Length:0.#}m)";
                nextBlocks.Add(new SplineTimelineBlockItem(SplineTimelineTrackKind.Gradients, i, s, end, label, seg.OutputAnchorId));
                s = end;
            }

            // Track 3: Cants
            s = 0.0;
            for (int i = 0; i < spline.Cants.Count; i++)
            {
                SplineCantSegment seg = spline.Cants[i];
                double end = s + seg.Length;
                string label = $"{seg.Kind} t={seg.Target:0.##} ({seg.Length:0.#}m)";
                nextBlocks.Add(new SplineTimelineBlockItem(SplineTimelineTrackKind.Cants, i, s, end, label, seg.OutputAnchorId));
                s = end;
            }

            // Track 4: Props overrides
            for (int i = 0; i < spline.Props.Count; i++)
            {
                SplineInstancePropRule rule = spline.Props[i];
                bool resolved = rule.From.TryResolve(anchorMap, out double fromS);
                if (!resolved)
                {
                    fromS = 0.0;
                }

                double endS = 0.0 < rule.Span
                    ? fromS + rule.Span
                    : (rule.Interval <= 0.0 ? fromS : double.Max(fromS, totalLength));
                string keyLabel = string.IsNullOrEmpty(rule.Key) ? $"Prop#{i}" : rule.Key;
                nextBlocks.Add(new SplineTimelineBlockItem(
                    SplineTimelineTrackKind.Props,
                    i,
                    fromS,
                    endS,
                    $"{keyLabel} ({string.Join(",", rule.ModelKeys)})",
                    rule.From.AnchorId,
                    !resolved));
            }

            // Track 5: PropSkips
            for (int i = 0; i < spline.PropSkips.Count; i++)
            {
                SplineSkipRule skip = spline.PropSkips[i];
                (double startS, double endS, bool unresolved) = ResolveSkipSpan(skip, anchorMap, totalLength);
                nextBlocks.Add(new SplineTimelineBlockItem(
                    SplineTimelineTrackKind.PropSkips,
                    i,
                    startS,
                    endS,
                    $"SkipProp:{skip.Key}",
                    skip.From?.AnchorId ?? skip.To?.AnchorId,
                    unresolved));
            }

            // Track 6: Extrusions overrides
            for (int i = 0; i < spline.Extrusions.Count; i++)
            {
                CurveExtrusionPlacementRule ext = spline.Extrusions[i];
                bool fromOk = ext.From.TryResolve(anchorMap, out double fromS);
                if (!fromOk)
                {
                    fromS = 0.0;
                }

                double toS = totalLength;
                bool toOk = true;
                if (ext.To.HasValue)
                {
                    toOk = ext.To.Value.TryResolve(anchorMap, out toS);
                    if (!toOk)
                    {
                        toS = totalLength;
                    }
                }

                string keyLabel = string.IsNullOrEmpty(ext.Key) ? ext.CrossSectionKey : $"{ext.Key}:{ext.CrossSectionKey}";
                nextBlocks.Add(new SplineTimelineBlockItem(
                    SplineTimelineTrackKind.Extrusions,
                    i,
                    fromS,
                    double.Max(fromS, toS),
                    keyLabel,
                    ext.From.AnchorId ?? ext.To?.AnchorId,
                    !(fromOk && toOk)));
            }

            // Track 7: ExtrusionSkips
            for (int i = 0; i < spline.ExtrusionSkips.Count; i++)
            {
                SplineSkipRule skip = spline.ExtrusionSkips[i];
                (double startS, double endS, bool unresolved) = ResolveSkipSpan(skip, anchorMap, totalLength);
                nextBlocks.Add(new SplineTimelineBlockItem(
                    SplineTimelineTrackKind.ExtrusionSkips,
                    i,
                    startS,
                    endS,
                    $"SkipExt:{skip.Key}",
                    skip.From?.AnchorId ?? skip.To?.AnchorId,
                    unresolved));
            }

            SyncTimelineBlocks(TimelineBlocks, nextBlocks);

            // Validate current selection or default to Curves[0]
            if (!IsTrackIndexValid(spline, SelectedTrackKind.Value, SelectedItemIndex.Value))
            {
                if (0 < spline.Curves.Count)
                {
                    SelectedTrackKind.Value = SplineTimelineTrackKind.Curves;
                    SelectedItemIndex.Value = 0;
                }
                else
                {
                    SelectedTrackKind.Value = null;
                    SelectedItemIndex.Value = -1;
                }
            }

            if (!IsCommitting)
            {
                SyncSelectionInspector();
            }
            else
            {
                SplineTimelineBlockItem? activeBlock = SelectedTrackKind.Value.HasValue && 0 <= SelectedItemIndex.Value
                    ? TimelineBlocks.FirstOrDefault(b => b.Track == SelectedTrackKind.Value && b.Index == SelectedItemIndex.Value)
                    : null;
                SelectedBlock.Value = null;
                SelectedBlock.Value = activeBlock;
            }
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void SyncSelectionInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            SplinePlacementNode? spline = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
            SplineTimelineTrackKind? track = SelectedTrackKind.Value;
            int idx = SelectedItemIndex.Value;

            bool valid = spline is not null && IsTrackIndexValid(spline, track, idx);
            HasSelection.Value = valid;
            IsCurveSelected.Value = valid && track == SplineTimelineTrackKind.Curves;
            IsGradientSelected.Value = valid && track == SplineTimelineTrackKind.Gradients;
            IsCantSelected.Value = valid && track == SplineTimelineTrackKind.Cants;
            IsPropSelected.Value = valid && track == SplineTimelineTrackKind.Props;
            IsSkipSelected.Value = valid && (track == SplineTimelineTrackKind.PropSkips || track == SplineTimelineTrackKind.ExtrusionSkips);
            IsExtrusionSelected.Value = valid && track == SplineTimelineTrackKind.Extrusions;
            CanReorderSelected.Value = valid && (
                track == SplineTimelineTrackKind.Curves
                || track == SplineTimelineTrackKind.Gradients
                || track == SplineTimelineTrackKind.Cants);

            ShowFromPicker.Value = valid && (
                track == SplineTimelineTrackKind.Props
                || track == SplineTimelineTrackKind.Extrusions
                || track == SplineTimelineTrackKind.PropSkips
                || track == SplineTimelineTrackKind.ExtrusionSkips);
            CanToggleFromNull.Value = valid && (
                track == SplineTimelineTrackKind.PropSkips
                || track == SplineTimelineTrackKind.ExtrusionSkips);

            ShowToPicker.Value = valid && (
                track == SplineTimelineTrackKind.Extrusions
                || track == SplineTimelineTrackKind.PropSkips
                || track == SplineTimelineTrackKind.ExtrusionSkips);
            CanToggleToNull.Value = ShowToPicker.Value;

            SplineTimelineBlockItem? matchedBlock = valid
                ? TimelineBlocks.FirstOrDefault(b => b.Track == track && b.Index == idx)
                : null;
            SelectedBlock.Value = null;
            SelectedBlock.Value = matchedBlock;

            if (!valid || spline is null || !track.HasValue)
            {
                return;
            }

            Dictionary<string, double> anchorMap = new(StringComparer.Ordinal);
            foreach (ResolvedAnchorInfo info in AvailableAnchors)
            {
                anchorMap[info.AnchorId] = info.ResolvedS;
            }

            switch (track.Value)
            {
                case SplineTimelineTrackKind.Curves:
                {
                    SplineCurveSegment seg = spline.Curves[idx];
                    CurveKind.Value = seg.Kind;
                    UpdateCurveParameterState(seg.Kind);
                    CurveValue.Value = (decimal)seg.Value;
                    CurveLength.Value = (decimal)seg.Length;
                    CurveAnchorId.Value = seg.OutputAnchorId ?? string.Empty;
                    break;
                }

                case SplineTimelineTrackKind.Gradients:
                {
                    SplineGradientSegment seg = spline.Gradients[idx];
                    GradientKind.Value = seg.Kind;
                    UpdateGradientParameterState(seg.Kind);
                    GradientDelta.Value = (decimal)seg.Delta;
                    GradientLength.Value = (decimal)seg.Length;
                    GradientAnchorId.Value = seg.OutputAnchorId ?? string.Empty;
                    break;
                }

                case SplineTimelineTrackKind.Cants:
                {
                    SplineCantSegment seg = spline.Cants[idx];
                    CantKind.Value = seg.Kind;
                    UpdateCantParameterState(seg.Kind);
                    CantTarget.Value = (decimal)seg.Target;
                    CantGauge.Value = (decimal)seg.Gauge;
                    CantLength.Value = (decimal)seg.Length;
                    CantAnchorId.Value = seg.OutputAnchorId ?? string.Empty;
                    break;
                }

                case SplineTimelineTrackKind.Props:
                {
                    SplineInstancePropRule prop = spline.Props[idx];
                    PropRuleKey.Value = prop.Key ?? string.Empty;
                    PropModelKeysText.Value = string.Join(", ", prop.ModelKeys);
                    PropPosX.Value = (decimal)prop.Pose.X;
                    PropPosY.Value = (decimal)prop.Pose.Y;
                    PropPosZ.Value = (decimal)prop.Pose.Z;
                    PropRotX.Value = (decimal)prop.Pose.RotationX;
                    PropRotY.Value = (decimal)prop.Pose.RotationY;
                    PropRotZ.Value = (decimal)prop.Pose.RotationZ;
                    PropSpan.Value = (decimal)prop.Span;
                    PropInterval.Value = (decimal)prop.Interval;
                    PropCount.Value = prop.Count;
                    PopulateDistancePicker(true, prop.From, anchorMap, isFrom: true);
                    break;
                }

                case SplineTimelineTrackKind.Extrusions:
                {
                    CurveExtrusionPlacementRule ext = spline.Extrusions[idx];
                    ExtRuleKey.Value = ext.Key ?? string.Empty;
                    ExtCrossSectionKey.Value = ext.CrossSectionKey;
                    ExtOffsetX.Value = (decimal)ext.OffsetX;
                    ExtOffsetY.Value = (decimal)ext.OffsetY;
                    ExtBaseInterval.Value = (decimal)ext.BaseInterval;
                    ExtMaxDeviation.Value = (decimal)ext.MaxDeviation;
                    ExtMaxAngleDeltaDeg.Value = (decimal)ext.MaxAngleDeltaDeg;
                    ExtVIntervalScale.Value = (decimal)ext.VIntervalScale;
                    ExtVOffset.Value = (decimal)ext.VOffset;
                    PopulateDistancePicker(true, ext.From, anchorMap, isFrom: true);
                    PopulateDistancePicker(ext.To.HasValue, ext.To ?? AnchoredDistance.Absolute(TotalCurveLength.Value), anchorMap, isFrom: false);
                    break;
                }

                case SplineTimelineTrackKind.PropSkips:
                case SplineTimelineTrackKind.ExtrusionSkips:
                {
                    SplineSkipRule skip = track.Value == SplineTimelineTrackKind.PropSkips
                        ? spline.PropSkips[idx]
                        : spline.ExtrusionSkips[idx];
                    SkipRuleKey.Value = skip.Key;
                    PopulateDistancePicker(skip.From.HasValue, skip.From ?? AnchoredDistance.Zero, anchorMap, isFrom: true);
                    PopulateDistancePicker(skip.To.HasValue, skip.To ?? AnchoredDistance.Absolute(TotalCurveLength.Value), anchorMap, isFrom: false);
                    break;
                }
            }
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void PopulateDistancePicker(
        bool hasValue,
        AnchoredDistance dist,
        IReadOnlyDictionary<string, double> anchorMap,
        bool isFrom)
    {
        ObservableCollection<string> anchorChoices = isFrom ? AvailableFromAnchorNames : AvailableToAnchorNames;
        List<string> nextChoices = new(AvailableAnchorNames);
        if (dist.IsRelative && !string.IsNullOrEmpty(dist.AnchorId) && !nextChoices.Contains(dist.AnchorId))
        {
            nextChoices.Add(dist.AnchorId);
        }

        SyncStringCollection(anchorChoices, nextChoices);

        if (isFrom)
        {
            HasFromDistance.Value = hasValue;
            FromIsRelative.Value = dist.IsRelative;
            FromModeIndex.Value = dist.IsRelative ? 1 : 0;
            FromAnchorId.Value = dist.AnchorId ?? string.Empty;
            FromOffset.Value = (decimal)dist.Offset;
            FromResolvedHint.Value = FormatResolvedHint(hasValue, dist, anchorMap);
        }
        else
        {
            HasToDistance.Value = hasValue;
            ToIsRelative.Value = dist.IsRelative;
            ToModeIndex.Value = dist.IsRelative ? 1 : 0;
            ToAnchorId.Value = dist.AnchorId ?? string.Empty;
            ToOffset.Value = (decimal)dist.Offset;
            ToResolvedHint.Value = FormatResolvedHint(hasValue, dist, anchorMap);
        }
    }

    private static string FormatResolvedHint(bool hasValue, AnchoredDistance dist, IReadOnlyDictionary<string, double> anchorMap)
    {
        if (!hasValue)
        {
            return "未指定 (範囲端なし)";
        }

        if (!dist.IsRelative)
        {
            return $"絶対位置 s = {dist.Offset:0.##}m";
        }

        if (dist.TryResolve(anchorMap, out double resolved))
        {
            double anchorBase = anchorMap[dist.AnchorId!];
            string sign = dist.Offset < 0.0 ? "-" : "+";
            return $"{dist.AnchorId} ({anchorBase:0.##}m) {sign} {double.Abs(dist.Offset):0.##}m = {resolved:0.##}m";
        }

        return $"⚠ アンカー '{dist.AnchorId}' が未定義です";
    }

    private AnchoredDistance ReadFromPickerDistance()
    {
        double offset = (double)(FromOffset.Value ?? 0m);
        if (FromModeIndex.Value == 1 && !string.IsNullOrWhiteSpace(FromAnchorId.Value))
        {
            return AnchoredDistance.RelativeTo(FromAnchorId.Value.Trim(), offset);
        }

        return AnchoredDistance.Absolute(offset);
    }

    private AnchoredDistance ReadToPickerDistance()
    {
        double offset = (double)(ToOffset.Value ?? 0m);
        if (ToModeIndex.Value == 1 && !string.IsNullOrWhiteSpace(ToAnchorId.Value))
        {
            return AnchoredDistance.RelativeTo(ToAnchorId.Value.Trim(), offset);
        }

        return AnchoredDistance.Absolute(offset);
    }

    private void CommitCurveSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null || SelectedTrackKind.Value != SplineTimelineTrackKind.Curves)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        if (idx < 0 || current.Curves.Count <= idx)
        {
            return;
        }

        SplineCurveSegment oldSeg = current.Curves[idx];
        UpdateCurveParameterState(CurveKind.Value);
        string? newAnchor = string.IsNullOrWhiteSpace(CurveAnchorId.Value) ? null : CurveAnchorId.Value.Trim();
        if (!string.IsNullOrEmpty(newAnchor)
            && HasDuplicateAnchorInSpline(current, newAnchor, SplineTimelineTrackKind.Curves, idx))
        {
            IsSyncing = true;
            try
            {
                CurveAnchorId.Value = oldSeg.OutputAnchorId ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"同一スプライン内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        SplineCurveSegment updated = new(
            CurveKind.Value,
            (double)(CurveValue.Value ?? 0m),
            double.Max(0.1, (double)(CurveLength.Value ?? 50m)),
            newAnchor);

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        ExpandAnchorIfRemovedOrRenamed(current, draft, oldSeg.OutputAnchorId, newAnchor, SplineTimelineTrackKind.Curves, idx);
        draft.Curves[idx] = updated;

        IsCommitting = true;
        try
        {
            Workspace.ReplaceSplineSnapshot(
                current.Id,
                draft,
                $"Curves[{idx}] を変更",
                $"Timeline.{current.Id}.Curve.{idx}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void CommitGradientSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null || SelectedTrackKind.Value != SplineTimelineTrackKind.Gradients)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        if (idx < 0 || current.Gradients.Count <= idx)
        {
            return;
        }

        SplineGradientSegment oldSeg = current.Gradients[idx];
        UpdateGradientParameterState(GradientKind.Value);
        string? newAnchor = string.IsNullOrWhiteSpace(GradientAnchorId.Value) ? null : GradientAnchorId.Value.Trim();
        if (!string.IsNullOrEmpty(newAnchor)
            && HasDuplicateAnchorInSpline(current, newAnchor, SplineTimelineTrackKind.Gradients, idx))
        {
            IsSyncing = true;
            try
            {
                GradientAnchorId.Value = oldSeg.OutputAnchorId ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"同一スプライン内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        SplineGradientSegment updated = new(
            GradientKind.Value,
            (double)(GradientDelta.Value ?? 0m),
            double.Max(0.1, (double)(GradientLength.Value ?? 50m)),
            newAnchor);

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        ExpandAnchorIfRemovedOrRenamed(current, draft, oldSeg.OutputAnchorId, newAnchor, SplineTimelineTrackKind.Gradients, idx);
        draft.Gradients[idx] = updated;

        IsCommitting = true;
        try
        {
            Workspace.ReplaceSplineSnapshot(
                current.Id,
                draft,
                $"Gradients[{idx}] を変更",
                $"Timeline.{current.Id}.Gradient.{idx}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void CommitCantSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null || SelectedTrackKind.Value != SplineTimelineTrackKind.Cants)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        if (idx < 0 || current.Cants.Count <= idx)
        {
            return;
        }

        SplineCantSegment oldSeg = current.Cants[idx];
        UpdateCantParameterState(CantKind.Value);
        string? newAnchor = string.IsNullOrWhiteSpace(CantAnchorId.Value) ? null : CantAnchorId.Value.Trim();
        if (!string.IsNullOrEmpty(newAnchor)
            && HasDuplicateAnchorInSpline(current, newAnchor, SplineTimelineTrackKind.Cants, idx))
        {
            IsSyncing = true;
            try
            {
                CantAnchorId.Value = oldSeg.OutputAnchorId ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"同一スプライン内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        SplineCantSegment updated = new(
            CantKind.Value,
            (double)(CantTarget.Value ?? 0m),
            double.Max(0.1, (double)(CantGauge.Value ?? 1.067m)),
            double.Max(0.1, (double)(CantLength.Value ?? 50m)),
            newAnchor);

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        ExpandAnchorIfRemovedOrRenamed(current, draft, oldSeg.OutputAnchorId, newAnchor, SplineTimelineTrackKind.Cants, idx);
        draft.Cants[idx] = updated;

        IsCommitting = true;
        try
        {
            Workspace.ReplaceSplineSnapshot(
                current.Id,
                draft,
                $"Cants[{idx}] を変更",
                $"Timeline.{current.Id}.Cant.{idx}");
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void UpdateCurveParameterState(SplineCurveSegmentKind kind)
    {
        CurveHasValueParameter.Value = kind != SplineCurveSegmentKind.Straight;
        CurveValueLabel.Value = kind == SplineCurveSegmentKind.ByCurvature
            ? "曲率 κ (1/m)"
            : "半径 R (m)";
    }

    private void UpdateGradientParameterState(SplineGradientSegmentKind kind)
    {
        GradientHasDeltaParameter.Value = kind != SplineGradientSegmentKind.Constant;
        GradientDeltaLabel.Value = kind switch
        {
            SplineGradientSegmentKind.TransitionByRadian => "変化量 (rad)",
            SplineGradientSegmentKind.TransitionByDegree => "変化量 (°)",
            SplineGradientSegmentKind.TransitionByPercent => "変化量 (%)",
            SplineGradientSegmentKind.TransitionByPermil => "変化量 (‰)",
            _ => "変化量 (Delta)",
        };
    }

    private void UpdateCantParameterState(SplineCantSegmentKind kind)
    {
        CantHasTargetParameter.Value = kind != SplineCantSegmentKind.Constant;
        CantHasGaugeParameter.Value = kind == SplineCantSegmentKind.TransitionToHeight;
        CantTargetLabel.Value = kind switch
        {
            SplineCantSegmentKind.TransitionToRadian => "目標値 (rad)",
            SplineCantSegmentKind.TransitionToDegree => "目標値 (°)",
            SplineCantSegmentKind.TransitionToPercent => "目標値 (%)",
            SplineCantSegmentKind.TransitionToHeight => "カント量 H (m)",
            _ => "目標値 (Target)",
        };
    }

    private static bool HasDuplicateAnchorInSpline(
        SplinePlacementNode node,
        string candidate,
        SplineTimelineTrackKind currentTrack,
        int excludeIndex)
    {
        for (int i = 0; i < node.Curves.Count; i++)
        {
            if (currentTrack == SplineTimelineTrackKind.Curves && i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(node.Curves[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Gradients.Count; i++)
        {
            if (currentTrack == SplineTimelineTrackKind.Gradients && i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(node.Gradients[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Cants.Count; i++)
        {
            if (currentTrack == SplineTimelineTrackKind.Cants && i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(node.Cants[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (string.Equals(node.ConnectBezier?.OutputAnchorId, candidate, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private void CommitSelectedAnchoredRule()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        SplineTimelineTrackKind? track = SelectedTrackKind.Value;
        if (Workspace is null || current is null || !track.HasValue)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        if (!IsTrackIndexValid(current, track, idx))
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        string mergeKey;
        string desc;

        switch (track.Value)
        {
            case SplineTimelineTrackKind.Props:
            {
                SplineInstancePropRule updated = new()
                {
                    Key = string.IsNullOrWhiteSpace(PropRuleKey.Value) ? null : PropRuleKey.Value.Trim(),
                    Pose = new PoseDegreesModel(
                        (double)(PropPosX.Value ?? 0m),
                        (double)(PropPosY.Value ?? 0m),
                        (double)(PropPosZ.Value ?? 0m),
                        (double)(PropRotX.Value ?? 0m),
                        (double)(PropRotY.Value ?? 0m),
                        (double)(PropRotZ.Value ?? 0m)),
                    From = ReadFromPickerDistance(),
                    Span = double.Max(0.0, (double)(PropSpan.Value ?? 0m)),
                    Interval = double.Max(0.0, (double)(PropInterval.Value ?? 0m)),
                    Count = int.Max(1, (int)(PropCount.Value ?? 100m)),
                };
                string[] parsedKeys = PropModelKeysText.Value.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (string mk in parsedKeys)
                {
                    updated.ModelKeys.Add(mk);
                }

                draft.Props[idx] = updated;
                mergeKey = $"Timeline.{current.Id}.Prop.{idx}";
                desc = $"Props[{idx}] を変更";
                break;
            }

            case SplineTimelineTrackKind.Extrusions:
            {
                CurveExtrusionPlacementRule updated = new()
                {
                    Key = string.IsNullOrWhiteSpace(ExtRuleKey.Value) ? null : ExtRuleKey.Value.Trim(),
                    CrossSectionKey = ExtCrossSectionKey.Value.Trim(),
                    OffsetX = (double)(ExtOffsetX.Value ?? 0m),
                    OffsetY = (double)(ExtOffsetY.Value ?? 0m),
                    BaseInterval = double.Max(0.1, (double)(ExtBaseInterval.Value ?? 5m)),
                    MaxDeviation = double.Max(0.0001, (double)(ExtMaxDeviation.Value ?? 0.01m)),
                    MaxAngleDeltaDeg = double.Max(0.1, (double)(ExtMaxAngleDeltaDeg.Value ?? 2m)),
                    VIntervalScale = double.Max(0.01, (double)(ExtVIntervalScale.Value ?? 1m)),
                    VOffset = (double)(ExtVOffset.Value ?? 0m),
                    From = ReadFromPickerDistance(),
                    To = HasToDistance.Value ? ReadToPickerDistance() : null,
                };
                draft.Extrusions[idx] = updated;
                mergeKey = $"Timeline.{current.Id}.Ext.{idx}";
                desc = $"Extrusions[{idx}] を変更";
                break;
            }

            case SplineTimelineTrackKind.PropSkips:
            {
                SplineSkipRule updated = new(
                    SkipRuleKey.Value.Trim(),
                    HasFromDistance.Value ? ReadFromPickerDistance() : null,
                    HasToDistance.Value ? ReadToPickerDistance() : null);
                draft.PropSkips[idx] = updated;
                mergeKey = $"Timeline.{current.Id}.PropSkip.{idx}";
                desc = $"SkipProp[{idx}] を変更";
                break;
            }

            case SplineTimelineTrackKind.ExtrusionSkips:
            {
                SplineSkipRule updated = new(
                    SkipRuleKey.Value.Trim(),
                    HasFromDistance.Value ? ReadFromPickerDistance() : null,
                    HasToDistance.Value ? ReadToPickerDistance() : null);
                draft.ExtrusionSkips[idx] = updated;
                mergeKey = $"Timeline.{current.Id}.ExtSkip.{idx}";
                desc = $"SkipExtrusion[{idx}] を変更";
                break;
            }

            default:
                return;
        }

        IsCommitting = true;
        try
        {
            Workspace.ReplaceSplineSnapshot(current.Id, draft, desc, mergeKey);
        }
        finally
        {
            IsCommitting = false;
        }
    }

    private void AddCurveSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.Curves.Count;
        draft.Curves.Add(new SplineCurveSegment(SplineCurveSegmentKind.Straight, 0.0, 25.0, null));
        SelectedTrackKind.Value = SplineTimelineTrackKind.Curves;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, $"Curves にセグメントを追加");
    }

    private void AddGradientSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.Gradients.Count;
        double len = double.Max(10.0, SplineAnchorUtilities.ComputeCurveTotalLength(current) / double.Max(1, draft.Gradients.Count + 1));
        draft.Gradients.Add(new SplineGradientSegment(SplineGradientSegmentKind.Constant, 0.0, double.Round(len, 2)));
        SelectedTrackKind.Value = SplineTimelineTrackKind.Gradients;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Gradients にセグメントを追加");
    }

    private void AddCantSegment()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.Cants.Count;
        double len = double.Max(10.0, SplineAnchorUtilities.ComputeCurveTotalLength(current) / double.Max(1, draft.Cants.Count + 1));
        draft.Cants.Add(new SplineCantSegment(SplineCantSegmentKind.Constant, 0.0, 1.067, double.Round(len, 2)));
        SelectedTrackKind.Value = SplineTimelineTrackKind.Cants;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Cants にセグメントを追加");
    }

    private void AddPropOverride()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.Props.Count;
        SplineInstancePropRule rule = new()
        {
            Key = $"Prop{idx + 1}",
            From = AnchoredDistance.Zero,
            Span = 0.0,
            Interval = 20.0,
            Count = 100,
        };
        if (0 < AvailableModelKeys.Count)
        {
            rule.ModelKeys.Add(AvailableModelKeys[0]);
        }

        draft.Props.Add(rule);
        SelectedTrackKind.Value = SplineTimelineTrackKind.Props;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Spline に Prop ルールを追加");
    }

    private void AddPropSkip()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        string defaultKey = ResolveDefaultSkipKey(current, isProp: true);
        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.PropSkips.Count;
        draft.PropSkips.Add(new SplineSkipRule(defaultKey, AnchoredDistance.Zero, AnchoredDistance.Absolute(double.Min(10.0, TotalCurveLength.Value))));
        SelectedTrackKind.Value = SplineTimelineTrackKind.PropSkips;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Spline に SkipProp ルールを追加");
    }

    private void AddExtrusionOverride()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.Extrusions.Count;
        draft.Extrusions.Add(new CurveExtrusionPlacementRule
        {
            Key = $"Ext{idx + 1}",
            CrossSectionKey = AvailableCrossSectionKeys.FirstOrDefault() ?? string.Empty,
            From = AnchoredDistance.Zero,
        });
        SelectedTrackKind.Value = SplineTimelineTrackKind.Extrusions;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Spline に Extrusion ルールを追加");
    }

    private void AddExtrusionSkip()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        if (Workspace is null || current is null)
        {
            return;
        }

        string defaultKey = ResolveDefaultSkipKey(current, isProp: false);
        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int idx = draft.ExtrusionSkips.Count;
        draft.ExtrusionSkips.Add(new SplineSkipRule(defaultKey, AnchoredDistance.Zero, AnchoredDistance.Absolute(double.Min(10.0, TotalCurveLength.Value))));
        SelectedTrackKind.Value = SplineTimelineTrackKind.ExtrusionSkips;
        SelectedItemIndex.Value = idx;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, "Spline に SkipExtrusion ルールを追加");
    }

    public void RemoveSelectedItem()
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        SplineTimelineTrackKind? track = SelectedTrackKind.Value;
        if (Workspace is null || current is null || !track.HasValue)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        if (!IsTrackIndexValid(current, track, idx))
        {
            return;
        }

        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);
        int expandedCount = 0;

        switch (track.Value)
        {
            case SplineTimelineTrackKind.Curves:
            {
                string? anchorId = current.Curves[idx].OutputAnchorId;
                expandedCount = ExpandAnchorBeforeRemoval(current, draft, anchorId, track.Value, idx);
                draft.Curves.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.Curves.Count ? int.Clamp(idx, 0, draft.Curves.Count - 1) : -1;
                break;
            }

            case SplineTimelineTrackKind.Gradients:
            {
                string? anchorId = current.Gradients[idx].OutputAnchorId;
                expandedCount = ExpandAnchorBeforeRemoval(current, draft, anchorId, track.Value, idx);
                draft.Gradients.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.Gradients.Count ? int.Clamp(idx, 0, draft.Gradients.Count - 1) : -1;
                break;
            }

            case SplineTimelineTrackKind.Cants:
            {
                string? anchorId = current.Cants[idx].OutputAnchorId;
                expandedCount = ExpandAnchorBeforeRemoval(current, draft, anchorId, track.Value, idx);
                draft.Cants.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.Cants.Count ? int.Clamp(idx, 0, draft.Cants.Count - 1) : -1;
                break;
            }

            case SplineTimelineTrackKind.Props:
                draft.Props.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.Props.Count ? int.Clamp(idx, 0, draft.Props.Count - 1) : -1;
                break;

            case SplineTimelineTrackKind.PropSkips:
                draft.PropSkips.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.PropSkips.Count ? int.Clamp(idx, 0, draft.PropSkips.Count - 1) : -1;
                break;

            case SplineTimelineTrackKind.Extrusions:
                draft.Extrusions.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.Extrusions.Count ? int.Clamp(idx, 0, draft.Extrusions.Count - 1) : -1;
                break;

            case SplineTimelineTrackKind.ExtrusionSkips:
                draft.ExtrusionSkips.RemoveAt(idx);
                SelectedItemIndex.Value = 0 < draft.ExtrusionSkips.Count ? int.Clamp(idx, 0, draft.ExtrusionSkips.Count - 1) : -1;
                break;
        }

        string desc = 0 < expandedCount
            ? $"{track.Value}[{idx}] を削除 (アンカー参照 {expandedCount} 件を絶対距離へ展開)"
            : $"{track.Value}[{idx}] を削除";
        Workspace.ReplaceSplineSnapshot(current.Id, draft, desc);
        if (0 < expandedCount)
        {
            Workspace.LastStatusMessage.Value = $"削除されたアンカーの相対参照 {expandedCount} 件を絶対距離 (m) に自動展開しました。";
        }
    }

    private void MoveSelectedItem(int delta)
    {
        SplinePlacementNode? current = Workspace?.FindSpline(Workspace.SelectedSplineId.Value);
        SplineTimelineTrackKind? track = SelectedTrackKind.Value;
        if (Workspace is null || current is null || !track.HasValue)
        {
            return;
        }

        int idx = SelectedItemIndex.Value;
        int target = idx + delta;
        SplinePlacementNode draft = SplineAnchorUtilities.DeepCloneSpline(current);

        switch (track.Value)
        {
            case SplineTimelineTrackKind.Curves:
                if (idx < 0 || draft.Curves.Count <= idx || target < 0 || draft.Curves.Count <= target)
                {
                    return;
                }

                (draft.Curves[idx], draft.Curves[target]) = (draft.Curves[target], draft.Curves[idx]);
                break;

            case SplineTimelineTrackKind.Gradients:
                if (idx < 0 || draft.Gradients.Count <= idx || target < 0 || draft.Gradients.Count <= target)
                {
                    return;
                }

                (draft.Gradients[idx], draft.Gradients[target]) = (draft.Gradients[target], draft.Gradients[idx]);
                break;

            case SplineTimelineTrackKind.Cants:
                if (idx < 0 || draft.Cants.Count <= idx || target < 0 || draft.Cants.Count <= target)
                {
                    return;
                }

                (draft.Cants[idx], draft.Cants[target]) = (draft.Cants[target], draft.Cants[idx]);
                break;

            default:
                return;
        }

        SelectedItemIndex.Value = target;
        Workspace.ReplaceSplineSnapshot(current.Id, draft, $"{track.Value} セグメントの順序を変更");
    }

    private void AppendModelKeyToProp()
    {
        string key = PropModelKeyToAdd.Value.Trim();
        if (string.IsNullOrEmpty(key) || !IsPropSelected.Value)
        {
            return;
        }

        string current = PropModelKeysText.Value.Trim();
        PropModelKeysText.Value = string.IsNullOrEmpty(current) ? key : $"{current}, {key}";
    }

    private static int ExpandAnchorBeforeRemoval(
        SplinePlacementNode original,
        SplinePlacementNode draft,
        string? removedAnchorId,
        SplineTimelineTrackKind removedFromTrack,
        int removedIndex)
    {
        if (string.IsNullOrEmpty(removedAnchorId) || HasRemainingAnchorDefinition(original, removedAnchorId, removedFromTrack, removedIndex))
        {
            return 0;
        }

        if (!TryResolveAnchorS(original, removedAnchorId, out double resolvedS))
        {
            return 0;
        }

        return SplineAnchorUtilities.ExpandAnchorReferencesToAbsolute(draft, removedAnchorId, resolvedS);
    }

    private static void ExpandAnchorIfRemovedOrRenamed(
        SplinePlacementNode original,
        SplinePlacementNode draft,
        string? oldAnchorId,
        string? newAnchorId,
        SplineTimelineTrackKind editedTrack,
        int editedIndex)
    {
        if (string.IsNullOrEmpty(oldAnchorId)
            || string.Equals(oldAnchorId, newAnchorId, StringComparison.Ordinal)
            || HasRemainingAnchorDefinition(original, oldAnchorId, editedTrack, editedIndex))
        {
            return;
        }

        if (TryResolveAnchorS(original, oldAnchorId, out double resolvedS))
        {
            SplineAnchorUtilities.ExpandAnchorReferencesToAbsolute(draft, oldAnchorId, resolvedS);
        }
    }

    private static bool TryResolveAnchorS(SplinePlacementNode node, string anchorId, out double resolvedS)
    {
        IReadOnlyList<ResolvedAnchorInfo> anchors = SplineAnchorUtilities.CollectSplineAnchors(node);
        for (int i = 0; i < anchors.Count; i++)
        {
            if (string.Equals(anchors[i].AnchorId, anchorId, StringComparison.Ordinal))
            {
                resolvedS = anchors[i].ResolvedS;
                return true;
            }
        }

        resolvedS = 0.0;
        return false;
    }

    private static bool HasRemainingAnchorDefinition(
        SplinePlacementNode node,
        string anchorId,
        SplineTimelineTrackKind excludingTrack,
        int excludingIndex)
    {
        for (int i = 0; i < node.Curves.Count; i++)
        {
            if (excludingTrack == SplineTimelineTrackKind.Curves && i == excludingIndex)
            {
                continue;
            }

            if (string.Equals(node.Curves[i].OutputAnchorId, anchorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Gradients.Count; i++)
        {
            if (excludingTrack == SplineTimelineTrackKind.Gradients && i == excludingIndex)
            {
                continue;
            }

            if (string.Equals(node.Gradients[i].OutputAnchorId, anchorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Cants.Count; i++)
        {
            if (excludingTrack == SplineTimelineTrackKind.Cants && i == excludingIndex)
            {
                continue;
            }

            if (string.Equals(node.Cants[i].OutputAnchorId, anchorId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private string ResolveDefaultSkipKey(SplinePlacementNode spline, bool isProp)
    {
        WorldProject? project = Workspace?.CurrentProject.Value;
        if (project is not null
            && !string.IsNullOrEmpty(spline.TemplateKey)
            && project.SplineTemplates.TryGetValue(spline.TemplateKey, out SplineTemplateDocument? tpl))
        {
            if (isProp)
            {
                foreach (SplineTemplatePropRule p in tpl.Props)
                {
                    if (!string.IsNullOrWhiteSpace(p.Key))
                    {
                        return p.Key;
                    }
                }
            }
            else
            {
                foreach (SplineTemplateExtrusionRule ext in tpl.Extrusions)
                {
                    if (!string.IsNullOrWhiteSpace(ext.Key))
                    {
                        return ext.Key;
                    }
                }
            }
        }

        return isProp ? "Prop1" : "RoadSurface";
    }

    private static (double StartS, double EndS, bool Unresolved) ResolveSkipSpan(
        SplineSkipRule skip,
        IReadOnlyDictionary<string, double> anchorMap,
        double totalLength)
    {
        bool fromOk = true;
        double startS = 0.0;
        if (skip.From.HasValue)
        {
            fromOk = skip.From.Value.TryResolve(anchorMap, out startS);
            if (!fromOk)
            {
                startS = 0.0;
            }
        }

        bool toOk = true;
        double endS = totalLength;
        if (skip.To.HasValue)
        {
            toOk = skip.To.Value.TryResolve(anchorMap, out endS);
            if (!toOk)
            {
                endS = totalLength;
            }
        }

        return (startS, double.Max(startS, endS), !(fromOk && toOk));
    }

    private static bool IsTrackIndexValid(SplinePlacementNode spline, SplineTimelineTrackKind? track, int index)
    {
        if (!track.HasValue || index < 0)
        {
            return false;
        }

        return track.Value switch
        {
            SplineTimelineTrackKind.Curves => index < spline.Curves.Count,
            SplineTimelineTrackKind.Gradients => index < spline.Gradients.Count,
            SplineTimelineTrackKind.Cants => index < spline.Cants.Count,
            SplineTimelineTrackKind.Props => index < spline.Props.Count,
            SplineTimelineTrackKind.PropSkips => index < spline.PropSkips.Count,
            SplineTimelineTrackKind.Extrusions => index < spline.Extrusions.Count,
            SplineTimelineTrackKind.ExtrusionSkips => index < spline.ExtrusionSkips.Count,
            _ => false,
        };
    }

    public void Dispose()
    {
        Disposables.Dispose();
    }
}
