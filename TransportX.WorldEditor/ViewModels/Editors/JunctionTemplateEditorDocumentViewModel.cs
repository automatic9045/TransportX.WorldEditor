using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using R3;
using TransportX.WorldEditor.Core.Commands;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Services;
using TransportX.WorldEditor.ViewModels.Dock;

namespace TransportX.WorldEditor.ViewModels.Editors;

public sealed record JunctionPortRow(int Index, string Key, string LayoutKey, string Summary);

public sealed record JunctionRelayRow(int Index, string InputKey, string OutputKey, string LayoutKey, string Summary);

public sealed record JunctionPathRow(int Index, string Key, string Summary);

public sealed record JunctionSegmentRow(int Index, string Summary);

public sealed record JunctionWidthRow(int Index, string Summary);

public sealed record JunctionCurveRow(int Index, string Key, string Summary);

public sealed record JunctionRulePropRow(int Index, string Summary);

public sealed record JunctionRuleExtrusionRow(int Index, string Summary);

public sealed record JunctionStaticPropRow(int Index, string Summary);

/// <summary>
/// Document ViewModel for editing a single <c>Templates/Junctions/&lt;Key&gt;.csx</c> file:
/// Ports, Relays, wired <c>JunctionPathTemplate</c> (<c>Wire</c> + <c>Width</c> + rules + <c>TrafficDensity</c>),
/// auxiliary <c>AddCurve</c> curves (with crosswalk/stop-line extrusions &amp; props), static props, and <c>&lt;user-code&gt;</c>.
/// </summary>
public sealed class JunctionTemplateEditorDocumentViewModel : EditorDocumentBase
{
    public const int PathEditorTabIndex = 2;
    public const int CurveEditorTabIndex = 3;

    private const string AbsoluteAnchorOption = "(絶対距離)";

    private readonly WorldWorkspaceService Workspace;
    private bool IsSyncing;
    private bool IsUpdatingLists;

    public string TemplateKey { get; }

    public BindableReactiveProperty<string> RelativePath { get; }
    public BindableReactiveProperty<bool> IsProtected { get; }
    public BindableReactiveProperty<bool> IsEditable { get; }
    public BindableReactiveProperty<bool> HasWarningBanner { get; }
    public BindableReactiveProperty<string> WarningBannerText { get; }
    public BindableReactiveProperty<string> UserCode { get; }
    public BindableReactiveProperty<int> CanvasVersion { get; }
    public BindableReactiveProperty<int> SelectedEditorTabIndex { get; }
    public BindableReactiveProperty<bool> Is3DPreviewMode { get; }
    public BindableReactiveProperty<bool> Show3DNetworkOverlay { get; }

    public ReactiveCommand<Unit> SwitchTo2DPreviewCommand { get; }
    public ReactiveCommand<Unit> SwitchTo3DPreviewCommand { get; }
    public ReactiveCommand<Unit> Reset3DPreviewCameraCommand { get; }
    public event Action? Reset3DCameraRequested;

    public ObservableCollection<string> AvailableLaneLayoutKeys { get; } = new();
    public ObservableCollection<string> AvailableCrossSectionKeys { get; } = new();
    public ObservableCollection<string> AvailableModelKeys { get; } = new();
    public ObservableCollection<string> AvailablePortKeys { get; } = new();
    public ObservableCollection<string> AvailablePathAnchorOptions { get; } = new();
    public ObservableCollection<string> AvailableCurveAnchorOptions { get; } = new();

    public IReadOnlyList<JunctionCurveSegmentKind> PathSegmentKinds { get; } =
    [
        JunctionCurveSegmentKind.StraightTo,
        JunctionCurveSegmentKind.BezierTo,
        JunctionCurveSegmentKind.StraightToEnd,
        JunctionCurveSegmentKind.BezierToEnd,
    ];

    public IReadOnlyList<JunctionWidthSegmentKind> WidthSegmentKinds { get; } =
    [
        JunctionWidthSegmentKind.Constant,
        JunctionWidthSegmentKind.TransitionTo,
    ];

    public IReadOnlyList<JunctionCurveOriginKind> CurveOriginKinds { get; } =
    [
        JunctionCurveOriginKind.AbsolutePose,
        JunctionCurveOriginKind.PortPinRelative,
    ];

    public IReadOnlyList<JunctionCurveSegmentKind> AuxSegmentKinds { get; } =
    [
        JunctionCurveSegmentKind.StraightTo,
        JunctionCurveSegmentKind.BezierTo,
    ];

    // Ports tab
    public ObservableCollection<JunctionPortRow> Ports { get; } = new();
    public BindableReactiveProperty<JunctionPortRow?> SelectedPort { get; }
    public BindableReactiveProperty<bool> HasSelectedPort { get; }
    public BindableReactiveProperty<string> PortKey { get; }
    public BindableReactiveProperty<string> PortLayoutKey { get; }
    public BindableReactiveProperty<decimal?> PortX { get; }
    public BindableReactiveProperty<decimal?> PortY { get; }
    public BindableReactiveProperty<decimal?> PortZ { get; }
    public BindableReactiveProperty<decimal?> PortRotX { get; }
    public BindableReactiveProperty<decimal?> PortRotY { get; }
    public BindableReactiveProperty<decimal?> PortRotZ { get; }

    // Relays tab
    public ObservableCollection<JunctionRelayRow> Relays { get; } = new();
    public BindableReactiveProperty<JunctionRelayRow?> SelectedRelay { get; }
    public BindableReactiveProperty<bool> HasSelectedRelay { get; }
    public BindableReactiveProperty<string> RelayInputKey { get; }
    public BindableReactiveProperty<string> RelayOutputKey { get; }
    public BindableReactiveProperty<string> RelayLayoutKey { get; }
    public BindableReactiveProperty<decimal?> RelayX { get; }
    public BindableReactiveProperty<decimal?> RelayY { get; }
    public BindableReactiveProperty<decimal?> RelayZ { get; }
    public BindableReactiveProperty<decimal?> RelayRotX { get; }
    public BindableReactiveProperty<decimal?> RelayRotY { get; }
    public BindableReactiveProperty<decimal?> RelayRotZ { get; }

    // Paths (Wire) tab
    public ObservableCollection<JunctionPathRow> Paths { get; } = new();
    public BindableReactiveProperty<JunctionPathRow?> SelectedPath { get; }
    public BindableReactiveProperty<bool> HasSelectedPath { get; }
    public BindableReactiveProperty<string> PathKey { get; }
    public BindableReactiveProperty<string> PathFromPortKey { get; }
    public BindableReactiveProperty<decimal?> PathFromPinIndex { get; }
    public BindableReactiveProperty<string> PathToPortKey { get; }
    public BindableReactiveProperty<decimal?> PathToPinIndex { get; }
    public BindableReactiveProperty<decimal?> PathTrafficDensityFactor { get; }

    public ObservableCollection<JunctionSegmentRow> PathSegments { get; } = new();
    public BindableReactiveProperty<JunctionSegmentRow?> SelectedPathSegment { get; }
    public BindableReactiveProperty<bool> HasSelectedPathSegment { get; }
    public BindableReactiveProperty<JunctionCurveSegmentKind> PathSegKind { get; }
    public BindableReactiveProperty<bool> PathSegHasTargetPose { get; }
    public BindableReactiveProperty<bool> PathSegIsBezier { get; }
    public BindableReactiveProperty<decimal?> PathSegX { get; }
    public BindableReactiveProperty<decimal?> PathSegY { get; }
    public BindableReactiveProperty<decimal?> PathSegZ { get; }
    public BindableReactiveProperty<decimal?> PathSegRotX { get; }
    public BindableReactiveProperty<decimal?> PathSegRotY { get; }
    public BindableReactiveProperty<decimal?> PathSegRotZ { get; }
    public BindableReactiveProperty<decimal?> PathSegHandleScale { get; }
    public BindableReactiveProperty<string> PathSegAnchorId { get; }

    public ObservableCollection<JunctionWidthRow> PathWidths { get; } = new();
    public BindableReactiveProperty<JunctionWidthRow?> SelectedPathWidth { get; }
    public BindableReactiveProperty<bool> HasSelectedPathWidth { get; }
    public BindableReactiveProperty<JunctionWidthSegmentKind> WidthKind { get; }
    public BindableReactiveProperty<bool> WidthIsTransition { get; }
    public BindableReactiveProperty<decimal?> WidthLeft { get; }
    public BindableReactiveProperty<decimal?> WidthRight { get; }
    public BindableReactiveProperty<string> WidthLengthAnchor { get; }
    public BindableReactiveProperty<decimal?> WidthLengthOffset { get; }
    public BindableReactiveProperty<string> WidthAnchorId { get; }

    public ObservableCollection<JunctionRulePropRow> PathProps { get; } = new();
    public BindableReactiveProperty<JunctionRulePropRow?> SelectedPathProp { get; }
    public BindableReactiveProperty<bool> HasSelectedPathProp { get; }
    public BindableReactiveProperty<string> PathPropModelKeysText { get; }
    public BindableReactiveProperty<string> PathPropModelToAdd { get; }
    public BindableReactiveProperty<decimal?> PathPropPosX { get; }
    public BindableReactiveProperty<decimal?> PathPropPosY { get; }
    public BindableReactiveProperty<decimal?> PathPropPosZ { get; }
    public BindableReactiveProperty<decimal?> PathPropRotX { get; }
    public BindableReactiveProperty<decimal?> PathPropRotY { get; }
    public BindableReactiveProperty<decimal?> PathPropRotZ { get; }
    public BindableReactiveProperty<string> PathPropFromAnchor { get; }
    public BindableReactiveProperty<decimal?> PathPropFromOffset { get; }
    public BindableReactiveProperty<decimal?> PathPropSpan { get; }
    public BindableReactiveProperty<decimal?> PathPropInterval { get; }
    public BindableReactiveProperty<decimal?> PathPropCount { get; }

    public ObservableCollection<JunctionRuleExtrusionRow> PathExtrusions { get; } = new();
    public BindableReactiveProperty<JunctionRuleExtrusionRow?> SelectedPathExtrusion { get; }
    public BindableReactiveProperty<bool> HasSelectedPathExtrusion { get; }
    public BindableReactiveProperty<string> PathExtKey { get; }
    public BindableReactiveProperty<string> PathExtCrossSectionKey { get; }
    public BindableReactiveProperty<decimal?> PathExtOffsetX { get; }
    public BindableReactiveProperty<decimal?> PathExtOffsetY { get; }
    public BindableReactiveProperty<string> PathExtFromAnchor { get; }
    public BindableReactiveProperty<decimal?> PathExtFromOffset { get; }
    public BindableReactiveProperty<bool> PathExtHasTo { get; }
    public BindableReactiveProperty<string> PathExtToAnchor { get; }
    public BindableReactiveProperty<decimal?> PathExtToOffset { get; }

    // Auxiliary Curves (AddCurve) tab
    public ObservableCollection<JunctionCurveRow> Curves { get; } = new();
    public BindableReactiveProperty<JunctionCurveRow?> SelectedCurve { get; }
    public BindableReactiveProperty<bool> HasSelectedCurve { get; }
    public BindableReactiveProperty<string> CurveKey { get; }
    public BindableReactiveProperty<JunctionCurveOriginKind> CurveOriginKind { get; }
    public BindableReactiveProperty<bool> CurveOriginIsPortPin { get; }
    public BindableReactiveProperty<string> CurveOriginPortKey { get; }
    public BindableReactiveProperty<decimal?> CurveOriginPinIndex { get; }
    public BindableReactiveProperty<bool> CurveOriginHasOffset { get; }
    public BindableReactiveProperty<decimal?> CurveOriginX { get; }
    public BindableReactiveProperty<decimal?> CurveOriginY { get; }
    public BindableReactiveProperty<decimal?> CurveOriginZ { get; }
    public BindableReactiveProperty<decimal?> CurveOriginRotX { get; }
    public BindableReactiveProperty<decimal?> CurveOriginRotY { get; }
    public BindableReactiveProperty<decimal?> CurveOriginRotZ { get; }

    public ObservableCollection<JunctionSegmentRow> CurveSegments { get; } = new();
    public BindableReactiveProperty<JunctionSegmentRow?> SelectedCurveSegment { get; }
    public BindableReactiveProperty<bool> HasSelectedCurveSegment { get; }
    public BindableReactiveProperty<JunctionCurveSegmentKind> CurveSegKind { get; }
    public BindableReactiveProperty<bool> CurveSegIsBezier { get; }
    public BindableReactiveProperty<decimal?> CurveSegX { get; }
    public BindableReactiveProperty<decimal?> CurveSegY { get; }
    public BindableReactiveProperty<decimal?> CurveSegZ { get; }
    public BindableReactiveProperty<decimal?> CurveSegRotX { get; }
    public BindableReactiveProperty<decimal?> CurveSegRotY { get; }
    public BindableReactiveProperty<decimal?> CurveSegRotZ { get; }
    public BindableReactiveProperty<decimal?> CurveSegHandleScale { get; }
    public BindableReactiveProperty<string> CurveSegAnchorId { get; }

    public ObservableCollection<JunctionRulePropRow> CurveProps { get; } = new();
    public BindableReactiveProperty<JunctionRulePropRow?> SelectedCurveProp { get; }
    public BindableReactiveProperty<bool> HasSelectedCurveProp { get; }
    public BindableReactiveProperty<string> CurvePropModelKeysText { get; }
    public BindableReactiveProperty<string> CurvePropModelToAdd { get; }
    public BindableReactiveProperty<decimal?> CurvePropPosX { get; }
    public BindableReactiveProperty<decimal?> CurvePropPosY { get; }
    public BindableReactiveProperty<decimal?> CurvePropPosZ { get; }
    public BindableReactiveProperty<decimal?> CurvePropRotX { get; }
    public BindableReactiveProperty<decimal?> CurvePropRotY { get; }
    public BindableReactiveProperty<decimal?> CurvePropRotZ { get; }
    public BindableReactiveProperty<string> CurvePropFromAnchor { get; }
    public BindableReactiveProperty<decimal?> CurvePropFromOffset { get; }
    public BindableReactiveProperty<decimal?> CurvePropSpan { get; }
    public BindableReactiveProperty<decimal?> CurvePropInterval { get; }
    public BindableReactiveProperty<decimal?> CurvePropCount { get; }

    public ObservableCollection<JunctionRuleExtrusionRow> CurveExtrusions { get; } = new();
    public BindableReactiveProperty<JunctionRuleExtrusionRow?> SelectedCurveExtrusion { get; }
    public BindableReactiveProperty<bool> HasSelectedCurveExtrusion { get; }
    public BindableReactiveProperty<string> CurveExtKey { get; }
    public BindableReactiveProperty<string> CurveExtCrossSectionKey { get; }
    public BindableReactiveProperty<decimal?> CurveExtOffsetX { get; }
    public BindableReactiveProperty<decimal?> CurveExtOffsetY { get; }
    public BindableReactiveProperty<string> CurveExtFromAnchor { get; }
    public BindableReactiveProperty<decimal?> CurveExtFromOffset { get; }
    public BindableReactiveProperty<bool> CurveExtHasTo { get; }
    public BindableReactiveProperty<string> CurveExtToAnchor { get; }
    public BindableReactiveProperty<decimal?> CurveExtToOffset { get; }

    // Static Props tab
    public ObservableCollection<JunctionStaticPropRow> StaticProps { get; } = new();
    public BindableReactiveProperty<JunctionStaticPropRow?> SelectedStaticProp { get; }
    public BindableReactiveProperty<bool> HasSelectedStaticProp { get; }
    public BindableReactiveProperty<string> StaticPropModelKey { get; }
    public BindableReactiveProperty<decimal?> StaticPropX { get; }
    public BindableReactiveProperty<decimal?> StaticPropY { get; }
    public BindableReactiveProperty<decimal?> StaticPropZ { get; }
    public BindableReactiveProperty<decimal?> StaticPropRotX { get; }
    public BindableReactiveProperty<decimal?> StaticPropRotY { get; }
    public BindableReactiveProperty<decimal?> StaticPropRotZ { get; }

    // Commands
    public ReactiveCommand<Unit> AddPortCommand { get; }
    public ReactiveCommand<Unit> RemovePortCommand { get; }
    public ReactiveCommand<Unit> MovePortUpCommand { get; }
    public ReactiveCommand<Unit> MovePortDownCommand { get; }

    public ReactiveCommand<Unit> AddRelayCommand { get; }
    public ReactiveCommand<Unit> RemoveRelayCommand { get; }

    public ReactiveCommand<Unit> AddPathCommand { get; }
    public ReactiveCommand<Unit> RemovePathCommand { get; }
    public ReactiveCommand<Unit> ClearTrafficDensityCommand { get; }
    public ReactiveCommand<Unit> AddPathSegmentCommand { get; }
    public ReactiveCommand<Unit> RemovePathSegmentCommand { get; }
    public ReactiveCommand<Unit> AddPathWidthCommand { get; }
    public ReactiveCommand<Unit> RemovePathWidthCommand { get; }
    public ReactiveCommand<Unit> AddPathPropCommand { get; }
    public ReactiveCommand<Unit> RemovePathPropCommand { get; }
    public ReactiveCommand<Unit> MovePathPropUpCommand { get; }
    public ReactiveCommand<Unit> MovePathPropDownCommand { get; }
    public ReactiveCommand<Unit> AppendPathPropModelCommand { get; }
    public ReactiveCommand<Unit> AddPathExtrusionCommand { get; }
    public ReactiveCommand<Unit> RemovePathExtrusionCommand { get; }
    public ReactiveCommand<Unit> MovePathExtrusionUpCommand { get; }
    public ReactiveCommand<Unit> MovePathExtrusionDownCommand { get; }

    public ReactiveCommand<Unit> AddCurveCommand { get; }
    public ReactiveCommand<Unit> RemoveCurveCommand { get; }
    public ReactiveCommand<Unit> AddCurveSegmentCommand { get; }
    public ReactiveCommand<Unit> RemoveCurveSegmentCommand { get; }
    public ReactiveCommand<Unit> AddCurvePropCommand { get; }
    public ReactiveCommand<Unit> RemoveCurvePropCommand { get; }
    public ReactiveCommand<Unit> MoveCurvePropUpCommand { get; }
    public ReactiveCommand<Unit> MoveCurvePropDownCommand { get; }
    public ReactiveCommand<Unit> AppendCurvePropModelCommand { get; }
    public ReactiveCommand<Unit> AddCurveExtrusionCommand { get; }
    public ReactiveCommand<Unit> RemoveCurveExtrusionCommand { get; }
    public ReactiveCommand<Unit> MoveCurveExtrusionUpCommand { get; }
    public ReactiveCommand<Unit> MoveCurveExtrusionDownCommand { get; }

    public ReactiveCommand<Unit> AddStaticPropCommand { get; }
    public ReactiveCommand<Unit> RemoveStaticPropCommand { get; }
    public ReactiveCommand<Unit> MoveStaticPropUpCommand { get; }
    public ReactiveCommand<Unit> MoveStaticPropDownCommand { get; }

    public JunctionTemplateEditorDocumentViewModel(WorldWorkspaceService workspace, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(templateKey);

        Workspace = workspace;
        TemplateKey = templateKey;
        Id = $"Doc_JunctionTemplate_{templateKey}";
        Title = $"JunctionTpl: {templateKey}";

        RelativePath = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        IsProtected = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        IsEditable = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        HasWarningBanner = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        WarningBannerText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        UserCode = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CanvasVersion = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        SelectedEditorTabIndex = new BindableReactiveProperty<int>(0).AddTo(Disposables);
        Is3DPreviewMode = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        Show3DNetworkOverlay = new BindableReactiveProperty<bool>(true).AddTo(Disposables);

        SwitchTo2DPreviewCommand = new ReactiveCommand<Unit>(_ => Is3DPreviewMode.Value = false).AddTo(Disposables);
        SwitchTo3DPreviewCommand = new ReactiveCommand<Unit>(_ => Is3DPreviewMode.Value = true).AddTo(Disposables);
        Reset3DPreviewCameraCommand = new ReactiveCommand<Unit>(_ => Reset3DCameraRequested?.Invoke()).AddTo(Disposables);

        SelectedPort = new BindableReactiveProperty<JunctionPortRow?>(null).AddTo(Disposables);
        HasSelectedPort = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PortLayoutKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PortX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PortY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PortZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PortRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PortRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PortRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedRelay = new BindableReactiveProperty<JunctionRelayRow?>(null).AddTo(Disposables);
        HasSelectedRelay = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        RelayInputKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        RelayOutputKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        RelayLayoutKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        RelayX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RelayY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RelayZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RelayRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RelayRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        RelayRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedPath = new BindableReactiveProperty<JunctionPathRow?>(null).AddTo(Disposables);
        HasSelectedPath = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathFromPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathFromPinIndex = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathToPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathToPinIndex = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathTrafficDensityFactor = new BindableReactiveProperty<decimal?>(null).AddTo(Disposables);

        SelectedPathSegment = new BindableReactiveProperty<JunctionSegmentRow?>(null).AddTo(Disposables);
        HasSelectedPathSegment = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathSegKind = new BindableReactiveProperty<JunctionCurveSegmentKind>(JunctionCurveSegmentKind.BezierToEnd).AddTo(Disposables);
        PathSegHasTargetPose = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathSegIsBezier = new BindableReactiveProperty<bool>(true).AddTo(Disposables);
        PathSegX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathSegHandleScale = new BindableReactiveProperty<decimal?>(0.5m).AddTo(Disposables);
        PathSegAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedPathWidth = new BindableReactiveProperty<JunctionWidthRow?>(null).AddTo(Disposables);
        HasSelectedPathWidth = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        WidthKind = new BindableReactiveProperty<JunctionWidthSegmentKind>(JunctionWidthSegmentKind.Constant).AddTo(Disposables);
        WidthIsTransition = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        WidthLeft = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        WidthRight = new BindableReactiveProperty<decimal?>(2m).AddTo(Disposables);
        WidthLengthAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        WidthLengthOffset = new BindableReactiveProperty<decimal?>(5m).AddTo(Disposables);
        WidthAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedPathProp = new BindableReactiveProperty<JunctionRulePropRow?>(null).AddTo(Disposables);
        HasSelectedPathProp = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathPropModelKeysText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathPropModelToAdd = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathPropPosX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropPosY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropPosZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropFromAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        PathPropFromOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropSpan = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathPropInterval = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        PathPropCount = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedPathExtrusion = new BindableReactiveProperty<JunctionRuleExtrusionRow?>(null).AddTo(Disposables);
        HasSelectedPathExtrusion = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathExtKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathExtCrossSectionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        PathExtOffsetX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathExtOffsetY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathExtFromAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        PathExtFromOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        PathExtHasTo = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        PathExtToAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        PathExtToOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedCurve = new BindableReactiveProperty<JunctionCurveRow?>(null).AddTo(Disposables);
        HasSelectedCurve = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurveOriginKind = new BindableReactiveProperty<JunctionCurveOriginKind>(JunctionCurveOriginKind.AbsolutePose).AddTo(Disposables);
        CurveOriginIsPortPin = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveOriginPortKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurveOriginPinIndex = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginHasOffset = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveOriginX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveOriginRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedCurveSegment = new BindableReactiveProperty<JunctionSegmentRow?>(null).AddTo(Disposables);
        HasSelectedCurveSegment = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveSegKind = new BindableReactiveProperty<JunctionCurveSegmentKind>(JunctionCurveSegmentKind.StraightTo).AddTo(Disposables);
        CurveSegIsBezier = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveSegX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveSegHandleScale = new BindableReactiveProperty<decimal?>(0.5m).AddTo(Disposables);
        CurveSegAnchorId = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);

        SelectedCurveProp = new BindableReactiveProperty<JunctionRulePropRow?>(null).AddTo(Disposables);
        HasSelectedCurveProp = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurvePropModelKeysText = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurvePropModelToAdd = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurvePropPosX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropPosY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropPosZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropFromAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        CurvePropFromOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropSpan = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurvePropInterval = new BindableReactiveProperty<decimal?>(1m).AddTo(Disposables);
        CurvePropCount = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedCurveExtrusion = new BindableReactiveProperty<JunctionRuleExtrusionRow?>(null).AddTo(Disposables);
        HasSelectedCurveExtrusion = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveExtKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurveExtCrossSectionKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        CurveExtOffsetX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveExtOffsetY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveExtFromAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        CurveExtFromOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        CurveExtHasTo = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        CurveExtToAnchor = new BindableReactiveProperty<string>(AbsoluteAnchorOption).AddTo(Disposables);
        CurveExtToOffset = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        SelectedStaticProp = new BindableReactiveProperty<JunctionStaticPropRow?>(null).AddTo(Disposables);
        HasSelectedStaticProp = new BindableReactiveProperty<bool>(false).AddTo(Disposables);
        StaticPropModelKey = new BindableReactiveProperty<string>(string.Empty).AddTo(Disposables);
        StaticPropX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        StaticPropY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        StaticPropZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        StaticPropRotX = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        StaticPropRotY = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);
        StaticPropRotZ = new BindableReactiveProperty<decimal?>(0m).AddTo(Disposables);

        AddPortCommand = new ReactiveCommand<Unit>(_ => AddPort()).AddTo(Disposables);
        RemovePortCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPort()).AddTo(Disposables);
        MovePortUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPort(-1)).AddTo(Disposables);
        MovePortDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPort(1)).AddTo(Disposables);

        AddRelayCommand = new ReactiveCommand<Unit>(_ => AddRelay()).AddTo(Disposables);
        RemoveRelayCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedRelay()).AddTo(Disposables);

        AddPathCommand = new ReactiveCommand<Unit>(_ => AddPath()).AddTo(Disposables);
        RemovePathCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPath()).AddTo(Disposables);
        ClearTrafficDensityCommand = new ReactiveCommand<Unit>(_ => PathTrafficDensityFactor.Value = null).AddTo(Disposables);
        AddPathSegmentCommand = new ReactiveCommand<Unit>(_ => AddPathSegment()).AddTo(Disposables);
        RemovePathSegmentCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPathSegment()).AddTo(Disposables);
        AddPathWidthCommand = new ReactiveCommand<Unit>(_ => AddPathWidth()).AddTo(Disposables);
        RemovePathWidthCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPathWidth()).AddTo(Disposables);
        AddPathPropCommand = new ReactiveCommand<Unit>(_ => AddPathProp()).AddTo(Disposables);
        RemovePathPropCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPathProp()).AddTo(Disposables);
        MovePathPropUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPathProp(-1)).AddTo(Disposables);
        MovePathPropDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPathProp(1)).AddTo(Disposables);
        AppendPathPropModelCommand = new ReactiveCommand<Unit>(_ => AppendModelToRule(PathPropModelKeysText, PathPropModelToAdd)).AddTo(Disposables);
        AddPathExtrusionCommand = new ReactiveCommand<Unit>(_ => AddPathExtrusion()).AddTo(Disposables);
        RemovePathExtrusionCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedPathExtrusion()).AddTo(Disposables);
        MovePathExtrusionUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPathExtrusion(-1)).AddTo(Disposables);
        MovePathExtrusionDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedPathExtrusion(1)).AddTo(Disposables);

        AddCurveCommand = new ReactiveCommand<Unit>(_ => AddCurve()).AddTo(Disposables);
        RemoveCurveCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedCurve()).AddTo(Disposables);
        AddCurveSegmentCommand = new ReactiveCommand<Unit>(_ => AddCurveSegment()).AddTo(Disposables);
        RemoveCurveSegmentCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedCurveSegment()).AddTo(Disposables);
        AddCurvePropCommand = new ReactiveCommand<Unit>(_ => AddCurveProp()).AddTo(Disposables);
        RemoveCurvePropCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedCurveProp()).AddTo(Disposables);
        MoveCurvePropUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedCurveProp(-1)).AddTo(Disposables);
        MoveCurvePropDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedCurveProp(1)).AddTo(Disposables);
        AppendCurvePropModelCommand = new ReactiveCommand<Unit>(_ => AppendModelToRule(CurvePropModelKeysText, CurvePropModelToAdd)).AddTo(Disposables);
        AddCurveExtrusionCommand = new ReactiveCommand<Unit>(_ => AddCurveExtrusion()).AddTo(Disposables);
        RemoveCurveExtrusionCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedCurveExtrusion()).AddTo(Disposables);
        MoveCurveExtrusionUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedCurveExtrusion(-1)).AddTo(Disposables);
        MoveCurveExtrusionDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedCurveExtrusion(1)).AddTo(Disposables);

        AddStaticPropCommand = new ReactiveCommand<Unit>(_ => AddStaticProp()).AddTo(Disposables);
        RemoveStaticPropCommand = new ReactiveCommand<Unit>(_ => RemoveSelectedStaticProp()).AddTo(Disposables);
        MoveStaticPropUpCommand = new ReactiveCommand<Unit>(_ => MoveSelectedStaticProp(-1)).AddTo(Disposables);
        MoveStaticPropDownCommand = new ReactiveCommand<Unit>(_ => MoveSelectedStaticProp(1)).AddTo(Disposables);

        SelectedPort.Subscribe(_ => { if (!IsUpdatingLists) SyncPortInspector(); }).AddTo(Disposables);
        HookCommit(PortKey, CommitSelectedPort);
        HookCommit(PortLayoutKey, CommitSelectedPort);
        HookCommit(PortX, CommitSelectedPort);
        HookCommit(PortY, CommitSelectedPort);
        HookCommit(PortZ, CommitSelectedPort);
        HookCommit(PortRotX, CommitSelectedPort);
        HookCommit(PortRotY, CommitSelectedPort);
        HookCommit(PortRotZ, CommitSelectedPort);

        SelectedRelay.Subscribe(_ => { if (!IsUpdatingLists) SyncRelayInspector(); }).AddTo(Disposables);
        HookCommit(RelayInputKey, CommitSelectedRelay);
        HookCommit(RelayOutputKey, CommitSelectedRelay);
        HookCommit(RelayLayoutKey, CommitSelectedRelay);
        HookCommit(RelayX, CommitSelectedRelay);
        HookCommit(RelayY, CommitSelectedRelay);
        HookCommit(RelayZ, CommitSelectedRelay);
        HookCommit(RelayRotX, CommitSelectedRelay);
        HookCommit(RelayRotY, CommitSelectedRelay);
        HookCommit(RelayRotZ, CommitSelectedRelay);

        SelectedPath.Subscribe(_ => { if (!IsUpdatingLists) SyncPathInspector(); }).AddTo(Disposables);
        HookCommit(PathKey, CommitSelectedPathMeta);
        HookCommit(PathFromPortKey, CommitSelectedPathMeta);
        HookCommit(PathFromPinIndex, CommitSelectedPathMeta);
        HookCommit(PathToPortKey, CommitSelectedPathMeta);
        HookCommit(PathToPinIndex, CommitSelectedPathMeta);
        HookCommit(PathTrafficDensityFactor, CommitSelectedPathMeta);

        SelectedPathSegment.Subscribe(_ => { if (!IsUpdatingLists) SyncPathSegmentInspector(); }).AddTo(Disposables);
        HookCommit(PathSegKind, CommitSelectedPathSegment);
        HookCommit(PathSegX, CommitSelectedPathSegment);
        HookCommit(PathSegY, CommitSelectedPathSegment);
        HookCommit(PathSegZ, CommitSelectedPathSegment);
        HookCommit(PathSegRotX, CommitSelectedPathSegment);
        HookCommit(PathSegRotY, CommitSelectedPathSegment);
        HookCommit(PathSegRotZ, CommitSelectedPathSegment);
        HookCommit(PathSegHandleScale, CommitSelectedPathSegment);
        HookCommit(PathSegAnchorId, CommitSelectedPathSegment);

        SelectedPathWidth.Subscribe(_ => { if (!IsUpdatingLists) SyncPathWidthInspector(); }).AddTo(Disposables);
        HookCommit(WidthKind, CommitSelectedPathWidth);
        HookCommit(WidthLeft, CommitSelectedPathWidth);
        HookCommit(WidthRight, CommitSelectedPathWidth);
        HookCommit(WidthLengthAnchor, CommitSelectedPathWidth);
        HookCommit(WidthLengthOffset, CommitSelectedPathWidth);
        HookCommit(WidthAnchorId, CommitSelectedPathWidth);

        SelectedPathProp.Subscribe(_ => { if (!IsUpdatingLists) SyncPathPropInspector(); }).AddTo(Disposables);
        HookCommit(PathPropModelKeysText, CommitSelectedPathProp);
        HookCommit(PathPropPosX, CommitSelectedPathProp);
        HookCommit(PathPropPosY, CommitSelectedPathProp);
        HookCommit(PathPropPosZ, CommitSelectedPathProp);
        HookCommit(PathPropRotX, CommitSelectedPathProp);
        HookCommit(PathPropRotY, CommitSelectedPathProp);
        HookCommit(PathPropRotZ, CommitSelectedPathProp);
        HookCommit(PathPropFromAnchor, CommitSelectedPathProp);
        HookCommit(PathPropFromOffset, CommitSelectedPathProp);
        HookCommit(PathPropSpan, CommitSelectedPathProp);
        HookCommit(PathPropInterval, CommitSelectedPathProp);
        HookCommit(PathPropCount, CommitSelectedPathProp);

        SelectedPathExtrusion.Subscribe(_ => { if (!IsUpdatingLists) SyncPathExtrusionInspector(); }).AddTo(Disposables);
        HookCommit(PathExtKey, CommitSelectedPathExtrusion);
        HookCommit(PathExtCrossSectionKey, CommitSelectedPathExtrusion);
        HookCommit(PathExtOffsetX, CommitSelectedPathExtrusion);
        HookCommit(PathExtOffsetY, CommitSelectedPathExtrusion);
        HookCommit(PathExtFromAnchor, CommitSelectedPathExtrusion);
        HookCommit(PathExtFromOffset, CommitSelectedPathExtrusion);
        HookCommit(PathExtHasTo, CommitSelectedPathExtrusion);
        HookCommit(PathExtToAnchor, CommitSelectedPathExtrusion);
        HookCommit(PathExtToOffset, CommitSelectedPathExtrusion);

        SelectedCurve.Subscribe(_ => { if (!IsUpdatingLists) SyncCurveInspector(); }).AddTo(Disposables);
        HookCommit(CurveKey, CommitSelectedCurveMeta);
        HookCommit(CurveOriginKind, CommitSelectedCurveMeta);
        HookCommit(CurveOriginPortKey, CommitSelectedCurveMeta);
        HookCommit(CurveOriginPinIndex, CommitSelectedCurveMeta);
        HookCommit(CurveOriginHasOffset, CommitSelectedCurveMeta);
        HookCommit(CurveOriginX, CommitSelectedCurveMeta);
        HookCommit(CurveOriginY, CommitSelectedCurveMeta);
        HookCommit(CurveOriginZ, CommitSelectedCurveMeta);
        HookCommit(CurveOriginRotX, CommitSelectedCurveMeta);
        HookCommit(CurveOriginRotY, CommitSelectedCurveMeta);
        HookCommit(CurveOriginRotZ, CommitSelectedCurveMeta);

        SelectedCurveSegment.Subscribe(_ => { if (!IsUpdatingLists) SyncCurveSegmentInspector(); }).AddTo(Disposables);
        HookCommit(CurveSegKind, CommitSelectedCurveSegment);
        HookCommit(CurveSegX, CommitSelectedCurveSegment);
        HookCommit(CurveSegY, CommitSelectedCurveSegment);
        HookCommit(CurveSegZ, CommitSelectedCurveSegment);
        HookCommit(CurveSegRotX, CommitSelectedCurveSegment);
        HookCommit(CurveSegRotY, CommitSelectedCurveSegment);
        HookCommit(CurveSegRotZ, CommitSelectedCurveSegment);
        HookCommit(CurveSegHandleScale, CommitSelectedCurveSegment);
        HookCommit(CurveSegAnchorId, CommitSelectedCurveSegment);

        SelectedCurveProp.Subscribe(_ => { if (!IsUpdatingLists) SyncCurvePropInspector(); }).AddTo(Disposables);
        HookCommit(CurvePropModelKeysText, CommitSelectedCurveProp);
        HookCommit(CurvePropPosX, CommitSelectedCurveProp);
        HookCommit(CurvePropPosY, CommitSelectedCurveProp);
        HookCommit(CurvePropPosZ, CommitSelectedCurveProp);
        HookCommit(CurvePropRotX, CommitSelectedCurveProp);
        HookCommit(CurvePropRotY, CommitSelectedCurveProp);
        HookCommit(CurvePropRotZ, CommitSelectedCurveProp);
        HookCommit(CurvePropFromAnchor, CommitSelectedCurveProp);
        HookCommit(CurvePropFromOffset, CommitSelectedCurveProp);
        HookCommit(CurvePropSpan, CommitSelectedCurveProp);
        HookCommit(CurvePropInterval, CommitSelectedCurveProp);
        HookCommit(CurvePropCount, CommitSelectedCurveProp);

        SelectedCurveExtrusion.Subscribe(_ => { if (!IsUpdatingLists) SyncCurveExtrusionInspector(); }).AddTo(Disposables);
        HookCommit(CurveExtKey, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtCrossSectionKey, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtOffsetX, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtOffsetY, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtFromAnchor, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtFromOffset, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtHasTo, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtToAnchor, CommitSelectedCurveExtrusion);
        HookCommit(CurveExtToOffset, CommitSelectedCurveExtrusion);

        SelectedStaticProp.Subscribe(_ => { if (!IsUpdatingLists) SyncStaticPropInspector(); }).AddTo(Disposables);
        HookCommit(StaticPropModelKey, CommitSelectedStaticProp);
        HookCommit(StaticPropX, CommitSelectedStaticProp);
        HookCommit(StaticPropY, CommitSelectedStaticProp);
        HookCommit(StaticPropZ, CommitSelectedStaticProp);
        HookCommit(StaticPropRotX, CommitSelectedStaticProp);
        HookCommit(StaticPropRotY, CommitSelectedStaticProp);
        HookCommit(StaticPropRotZ, CommitSelectedStaticProp);

        HookCommit(UserCode, CommitUserCode);

        Workspace.ProjectRevision.Subscribe(_ => SyncFromModel()).AddTo(Disposables);
        SyncFromModel();
        BindDirtyTitle(
            Workspace,
            $"JunctionTpl: {TemplateKey}",
            (project, target) =>
            {
                if (target.Kind != WorldFileKind.JunctionTemplateCsx)
                {
                    return false;
                }

                return project.JunctionTemplates.TryGetValue(TemplateKey, out JunctionTemplateDocument? doc)
                    && string.Equals(
                        WorldProject.NormalizeRelativePath(target.RelativePath),
                        WorldProject.NormalizeRelativePath(doc.RelativePath),
                        StringComparison.OrdinalIgnoreCase);
            });
    }

    public WorldProject? GetProject() => Workspace.CurrentProject.Value;

    public JunctionTemplateDocument? GetDocument()
    {
        WorldProject? project = Workspace.CurrentProject.Value;
        return project is not null && project.JunctionTemplates.TryGetValue(TemplateKey, out JunctionTemplateDocument? doc)
            ? doc
            : null;
    }

    public void SelectPortIndex(int index)
    {
        if (0 <= index && index < Ports.Count)
        {
            SelectedPort.Value = Ports[index];
        }
    }

    public void SelectRelayIndex(int index)
    {
        if (0 <= index && index < Relays.Count)
        {
            SelectedRelay.Value = Relays[index];
        }
    }

    public void SelectPathIndex(int index)
    {
        if (0 <= index && index < Paths.Count)
        {
            SelectedPath.Value = Paths[index];
        }
    }

    public void SelectCurveIndex(int index)
    {
        if (0 <= index && index < Curves.Count)
        {
            SelectedCurve.Value = Curves[index];
        }
    }

    public void MovePortFromCanvas(int index, double x, double z)
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null || index < 0 || doc.Ports.Count <= index)
        {
            return;
        }

        double roundedX = Math.Round(x, 2);
        double roundedZ = Math.Round(z, 2);
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"ポート '{doc.Ports[index].Key}' を移動",
            draft =>
            {
                if (0 <= index && index < draft.Ports.Count)
                {
                    JunctionPortDefinition p = draft.Ports[index];
                    draft.Ports[index] = p with { Pose = p.Pose with { X = roundedX, Z = roundedZ } };
                }
            },
            $"JunctionTemplate.{TemplateKey}.PortDrag.{index}");
    }

    public void MoveRelayFromCanvas(int index, double x, double z)
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null || index < 0 || doc.Relays.Count <= index)
        {
            return;
        }

        double roundedX = Math.Round(x, 2);
        double roundedZ = Math.Round(z, 2);
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"Relay '{doc.Relays[index].InputKey}' を移動",
            draft =>
            {
                if (0 <= index && index < draft.Relays.Count)
                {
                    JunctionRelayDefinition r = draft.Relays[index];
                    draft.Relays[index] = r with { Pose = r.Pose with { X = roundedX, Z = roundedZ } };
                }
            },
            $"JunctionTemplate.{TemplateKey}.RelayDrag.{index}");
    }

    private void HookCommit<T>(BindableReactiveProperty<T> prop, Action commit)
    {
        prop.Skip(1).Subscribe(_ =>
        {
            if (!IsSyncing)
            {
                commit();
            }
        }).AddTo(Disposables);
    }

    private void SyncFromModel()
    {
        bool prevSync = IsSyncing;
        bool prevUpdate = IsUpdatingLists;
        IsSyncing = true;
        IsUpdatingLists = true;
        try
        {
            int prevPortIdx = SelectedPort.Value?.Index ?? -1;
            int prevRelayIdx = SelectedRelay.Value?.Index ?? -1;
            int prevPathIdx = SelectedPath.Value?.Index ?? -1;
            int prevCurveIdx = SelectedCurve.Value?.Index ?? -1;
            int prevStaticIdx = SelectedStaticProp.Value?.Index ?? -1;

            WorldProject? project = Workspace.CurrentProject.Value;
            JunctionTemplateDocument? doc = GetDocument();
            if (project is null || doc is null)
            {
                Ports.Clear();
                Relays.Clear();
                Paths.Clear();
                Curves.Clear();
                StaticProps.Clear();
                CanvasVersion.Value++;
                return;
            }

            RelativePath.Value = doc.RelativePath;
            string normalizedRel = WorldProject.NormalizeRelativePath(doc.RelativePath);
            bool isProtected = project.IsProtected(normalizedRel);
            IsProtected.Value = isProtected;
            IsEditable.Value = !isProtected;
            HasWarningBanner.Value = isProtected;
            WarningBannerText.Value = isProtected
                ? $"'{normalizedRel}' は <editor-generated> パース失敗のため上書き保護されています。保護解除まで GUI 編集はできません。"
                : string.Empty;

            UserCode.Value = doc.UserCode ?? string.Empty;

            List<string> layoutKeys = project.LaneLayouts.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> csKeys = project.CrossSections.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> modelKeys = project.ModelLists.Values
                .SelectMany(static ml => ml.Entries)
                .Select(static e => e.Key)
                .Where(static k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> portKeys = JunctionAnchorUtilities.EnumerateAvailablePortKeys(doc)
                .Select(static p => p.PortKey)
                .ToList();

            SyncStringCollection(AvailableLaneLayoutKeys, layoutKeys);
            SyncStringCollection(AvailableCrossSectionKeys, csKeys);
            SyncStringCollection(AvailableModelKeys, modelKeys);
            SyncStringCollection(AvailablePortKeys, portKeys);

            Ports.Clear();
            for (int i = 0; i < doc.Ports.Count; i++)
            {
                JunctionPortDefinition p = doc.Ports[i];
                Ports.Add(new JunctionPortRow(i, p.Key, p.LayoutKey, $"{p.Key} ({p.LayoutKey}) @ ({p.Pose.X:0.#}, {p.Pose.Z:0.#}) ry={p.Pose.RotationY:0.#}°"));
            }

            Relays.Clear();
            for (int i = 0; i < doc.Relays.Count; i++)
            {
                JunctionRelayDefinition r = doc.Relays[i];
                Relays.Add(new JunctionRelayRow(i, r.InputKey, r.OutputKey, r.LayoutKey, $"{r.InputKey}->{r.OutputKey} ({r.LayoutKey}) @ ({r.Pose.X:0.#}, {r.Pose.Z:0.#})"));
            }

            Paths.Clear();
            for (int i = 0; i < doc.Paths.Count; i++)
            {
                JunctionPathTemplateModel w = doc.Paths[i];
                string td = w.TrafficDensityFactor.HasValue ? $" td={w.TrafficDensityFactor.Value:0.##}" : string.Empty;
                Paths.Add(new JunctionPathRow(i, w.Key, $"{w.Key}: {w.FromPortKey}[{w.FromPinIndex}]->{w.ToPortKey}[{w.ToPinIndex}]{td}"));
            }

            Curves.Clear();
            for (int i = 0; i < doc.Curves.Count; i++)
            {
                JunctionCurveTemplateModel c = doc.Curves[i];
                string org = c.Origin.Kind == JunctionCurveOriginKind.PortPinRelative
                    ? $"{c.Origin.PortKey}[{c.Origin.PinIndex}]"
                    : $"({c.Origin.Pose.X:0.#},{c.Origin.Pose.Z:0.#})";
                Curves.Add(new JunctionCurveRow(i, c.Key, $"{c.Key}: {org} ({c.Segments.Count}seg/{c.Extrusions.Count}ext)"));
            }

            StaticProps.Clear();
            for (int i = 0; i < doc.Props.Count; i++)
            {
                JunctionStaticPropPlacement sp = doc.Props[i];
                StaticProps.Add(new JunctionStaticPropRow(i, $"{sp.ModelKey} ({sp.Pose.X:0.#}, {sp.Pose.Y:0.#}, {sp.Pose.Z:0.#}) ry={sp.Pose.RotationY:0.#}°"));
            }

            SelectedPort.Value = 0 <= prevPortIdx && prevPortIdx < Ports.Count ? Ports[prevPortIdx] : Ports.FirstOrDefault();
            SelectedRelay.Value = 0 <= prevRelayIdx && prevRelayIdx < Relays.Count ? Relays[prevRelayIdx] : Relays.FirstOrDefault();
            SelectedPath.Value = 0 <= prevPathIdx && prevPathIdx < Paths.Count ? Paths[prevPathIdx] : Paths.FirstOrDefault();
            SelectedCurve.Value = 0 <= prevCurveIdx && prevCurveIdx < Curves.Count ? Curves[prevCurveIdx] : Curves.FirstOrDefault();
            SelectedStaticProp.Value = 0 <= prevStaticIdx && prevStaticIdx < StaticProps.Count ? StaticProps[prevStaticIdx] : StaticProps.FirstOrDefault();

            SyncPortInspector();
            SyncRelayInspector();
            SyncPathInspector();
            SyncCurveInspector();
            SyncStaticPropInspector();

            CanvasVersion.Value++;
        }
        finally
        {
            IsUpdatingLists = prevUpdate;
            IsSyncing = prevSync;
        }
    }

    private void SyncPortInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionTemplateDocument? doc = GetDocument();
            JunctionPortRow? row = SelectedPort.Value;
            if (doc is null || row is null || row.Index < 0 || doc.Ports.Count <= row.Index)
            {
                HasSelectedPort.Value = false;
                return;
            }

            HasSelectedPort.Value = true;
            JunctionPortDefinition p = doc.Ports[row.Index];
            PortKey.Value = p.Key;
            PortLayoutKey.Value = p.LayoutKey;
            PortX.Value = (decimal)p.Pose.X;
            PortY.Value = (decimal)p.Pose.Y;
            PortZ.Value = (decimal)p.Pose.Z;
            PortRotX.Value = (decimal)p.Pose.RotationX;
            PortRotY.Value = (decimal)p.Pose.RotationY;
            PortRotZ.Value = (decimal)p.Pose.RotationZ;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncRelayInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionTemplateDocument? doc = GetDocument();
            JunctionRelayRow? row = SelectedRelay.Value;
            if (doc is null || row is null || row.Index < 0 || doc.Relays.Count <= row.Index)
            {
                HasSelectedRelay.Value = false;
                return;
            }

            HasSelectedRelay.Value = true;
            JunctionRelayDefinition r = doc.Relays[row.Index];
            RelayInputKey.Value = r.InputKey;
            RelayOutputKey.Value = r.OutputKey;
            RelayLayoutKey.Value = r.LayoutKey;
            RelayX.Value = (decimal)r.Pose.X;
            RelayY.Value = (decimal)r.Pose.Y;
            RelayZ.Value = (decimal)r.Pose.Z;
            RelayRotX.Value = (decimal)r.Pose.RotationX;
            RelayRotY.Value = (decimal)r.Pose.RotationY;
            RelayRotZ.Value = (decimal)r.Pose.RotationZ;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncPathInspector()
    {
        bool prevSync = IsSyncing;
        bool prevUpdate = IsUpdatingLists;
        IsSyncing = true;
        IsUpdatingLists = true;
        try
        {
            int prevSegIdx = SelectedPathSegment.Value?.Index ?? -1;
            int prevWIdx = SelectedPathWidth.Value?.Index ?? -1;
            int prevPropIdx = SelectedPathProp.Value?.Index ?? -1;
            int prevExtIdx = SelectedPathExtrusion.Value?.Index ?? -1;

            WorldProject? project = GetProject();
            JunctionTemplateDocument? doc = GetDocument();
            JunctionPathRow? row = SelectedPath.Value;
            if (project is null || doc is null || row is null || row.Index < 0 || doc.Paths.Count <= row.Index)
            {
                HasSelectedPath.Value = false;
                PathSegments.Clear();
                PathWidths.Clear();
                PathProps.Clear();
                PathExtrusions.Clear();
                SyncStringCollection(AvailablePathAnchorOptions, new List<string> { AbsoluteAnchorOption });
                SyncPathSegmentInspector();
                SyncPathWidthInspector();
                SyncPathPropInspector();
                SyncPathExtrusionInspector();
                return;
            }

            HasSelectedPath.Value = true;
            JunctionPathTemplateModel path = doc.Paths[row.Index];
            PathKey.Value = path.Key;
            PathFromPortKey.Value = path.FromPortKey;
            PathFromPinIndex.Value = path.FromPinIndex;
            PathToPortKey.Value = path.ToPortKey;
            PathToPinIndex.Value = path.ToPinIndex;
            PathTrafficDensityFactor.Value = path.TrafficDensityFactor.HasValue ? (decimal)path.TrafficDensityFactor.Value : null;

            List<string> anchorOpts = new() { AbsoluteAnchorOption };
            IReadOnlyList<ResolvedAnchorInfo> pathAnchors = JunctionAnchorUtilities.CollectPathAnchors(doc, project.LaneLayouts, path);
            foreach (ResolvedAnchorInfo info in pathAnchors)
            {
                if (!anchorOpts.Contains(info.AnchorId, StringComparer.Ordinal))
                {
                    anchorOpts.Add(info.AnchorId);
                }
            }

            SyncStringCollection(AvailablePathAnchorOptions, anchorOpts);

            PathSegments.Clear();
            for (int i = 0; i < path.Segments.Count; i++)
            {
                JunctionCurveSegment seg = path.Segments[i];
                string anc = string.IsNullOrEmpty(seg.OutputAnchorId) ? string.Empty : $" out {seg.OutputAnchorId}";
                PathSegments.Add(new JunctionSegmentRow(i, $"#{i}: {seg.Kind} ({seg.TargetPose.X:0.#}, {seg.TargetPose.Z:0.#}){anc}"));
            }

            PathWidths.Clear();
            for (int i = 0; i < path.WidthSegments.Count; i++)
            {
                JunctionWidthSegment ws = path.WidthSegments[i];
                string anc = string.IsNullOrEmpty(ws.OutputAnchorId) ? string.Empty : $" out {ws.OutputAnchorId}";
                PathWidths.Add(new JunctionWidthRow(i, $"#{i}: {ws.Kind} L={ws.LeftWidth:0.##} R={ws.RightWidth:0.##} len={ws.Length}{anc}"));
            }

            PathProps.Clear();
            for (int i = 0; i < path.Props.Count; i++)
            {
                CurvePropPlacementRule pr = path.Props[i];
                PathProps.Add(new JunctionRulePropRow(i, $"[{string.Join(',', pr.ModelKeys)}] from={pr.From} iv={pr.Interval:0.#}"));
            }

            PathExtrusions.Clear();
            for (int i = 0; i < path.Extrusions.Count; i++)
            {
                CurveExtrusionPlacementRule ex = path.Extrusions[i];
                string toStr = ex.To.HasValue ? $" to={ex.To.Value}" : string.Empty;
                PathExtrusions.Add(new JunctionRuleExtrusionRow(i, $"{ex.CrossSectionKey} dx={ex.OffsetX:0.##} from={ex.From}{toStr}"));
            }

            SelectedPathSegment.Value = 0 <= prevSegIdx && prevSegIdx < PathSegments.Count ? PathSegments[prevSegIdx] : PathSegments.FirstOrDefault();
            SelectedPathWidth.Value = 0 <= prevWIdx && prevWIdx < PathWidths.Count ? PathWidths[prevWIdx] : PathWidths.FirstOrDefault();
            SelectedPathProp.Value = 0 <= prevPropIdx && prevPropIdx < PathProps.Count ? PathProps[prevPropIdx] : PathProps.FirstOrDefault();
            SelectedPathExtrusion.Value = 0 <= prevExtIdx && prevExtIdx < PathExtrusions.Count ? PathExtrusions[prevExtIdx] : PathExtrusions.FirstOrDefault();

            SyncPathSegmentInspector();
            SyncPathWidthInspector();
            SyncPathPropInspector();
            SyncPathExtrusionInspector();
        }
        finally
        {
            IsUpdatingLists = prevUpdate;
            IsSyncing = prevSync;
        }
    }

    private void SyncPathSegmentInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionPathTemplateModel? path = GetSelectedPathModel();
            JunctionSegmentRow? row = SelectedPathSegment.Value;
            if (path is null || row is null || row.Index < 0 || path.Segments.Count <= row.Index)
            {
                HasSelectedPathSegment.Value = false;
                return;
            }

            HasSelectedPathSegment.Value = true;
            JunctionCurveSegment seg = path.Segments[row.Index];
            PathSegKind.Value = seg.Kind;
            PathSegHasTargetPose.Value = seg.Kind == JunctionCurveSegmentKind.StraightTo || seg.Kind == JunctionCurveSegmentKind.BezierTo;
            PathSegIsBezier.Value = seg.Kind == JunctionCurveSegmentKind.BezierTo || seg.Kind == JunctionCurveSegmentKind.BezierToEnd;
            PathSegX.Value = (decimal)seg.TargetPose.X;
            PathSegY.Value = (decimal)seg.TargetPose.Y;
            PathSegZ.Value = (decimal)seg.TargetPose.Z;
            PathSegRotX.Value = (decimal)seg.TargetPose.RotationX;
            PathSegRotY.Value = (decimal)seg.TargetPose.RotationY;
            PathSegRotZ.Value = (decimal)seg.TargetPose.RotationZ;
            PathSegHandleScale.Value = (decimal)seg.HandleScale;
            PathSegAnchorId.Value = seg.OutputAnchorId ?? string.Empty;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncPathWidthInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionPathTemplateModel? path = GetSelectedPathModel();
            JunctionWidthRow? row = SelectedPathWidth.Value;
            if (path is null || row is null || row.Index < 0 || path.WidthSegments.Count <= row.Index)
            {
                HasSelectedPathWidth.Value = false;
                return;
            }

            HasSelectedPathWidth.Value = true;
            JunctionWidthSegment ws = path.WidthSegments[row.Index];
            WidthKind.Value = ws.Kind;
            WidthIsTransition.Value = ws.Kind == JunctionWidthSegmentKind.TransitionTo;
            WidthLeft.Value = (decimal)ws.LeftWidth;
            WidthRight.Value = (decimal)ws.RightWidth;
            WidthLengthAnchor.Value = string.IsNullOrEmpty(ws.Length.AnchorId) ? AbsoluteAnchorOption : ws.Length.AnchorId;
            WidthLengthOffset.Value = (decimal)ws.Length.Offset;
            WidthAnchorId.Value = ws.OutputAnchorId ?? string.Empty;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncPathPropInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionPathTemplateModel? path = GetSelectedPathModel();
            JunctionRulePropRow? row = SelectedPathProp.Value;
            if (path is null || row is null || row.Index < 0 || path.Props.Count <= row.Index)
            {
                HasSelectedPathProp.Value = false;
                return;
            }

            HasSelectedPathProp.Value = true;
            CurvePropPlacementRule pr = path.Props[row.Index];
            PathPropModelKeysText.Value = string.Join(", ", pr.ModelKeys);
            PathPropPosX.Value = (decimal)pr.Pose.X;
            PathPropPosY.Value = (decimal)pr.Pose.Y;
            PathPropPosZ.Value = (decimal)pr.Pose.Z;
            PathPropRotX.Value = (decimal)pr.Pose.RotationX;
            PathPropRotY.Value = (decimal)pr.Pose.RotationY;
            PathPropRotZ.Value = (decimal)pr.Pose.RotationZ;
            PathPropFromAnchor.Value = string.IsNullOrEmpty(pr.From.AnchorId) ? AbsoluteAnchorOption : pr.From.AnchorId;
            PathPropFromOffset.Value = (decimal)pr.From.Offset;
            PathPropSpan.Value = (decimal)pr.Span;
            PathPropInterval.Value = (decimal)pr.Interval;
            PathPropCount.Value = pr.Count == int.MaxValue ? 0m : pr.Count;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncPathExtrusionInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionPathTemplateModel? path = GetSelectedPathModel();
            JunctionRuleExtrusionRow? row = SelectedPathExtrusion.Value;
            if (path is null || row is null || row.Index < 0 || path.Extrusions.Count <= row.Index)
            {
                HasSelectedPathExtrusion.Value = false;
                return;
            }

            HasSelectedPathExtrusion.Value = true;
            CurveExtrusionPlacementRule ex = path.Extrusions[row.Index];
            PathExtKey.Value = ex.Key ?? string.Empty;
            PathExtCrossSectionKey.Value = ex.CrossSectionKey;
            PathExtOffsetX.Value = (decimal)ex.OffsetX;
            PathExtOffsetY.Value = (decimal)ex.OffsetY;
            PathExtFromAnchor.Value = string.IsNullOrEmpty(ex.From.AnchorId) ? AbsoluteAnchorOption : ex.From.AnchorId;
            PathExtFromOffset.Value = (decimal)ex.From.Offset;
            PathExtHasTo.Value = ex.To.HasValue;
            PathExtToAnchor.Value = ex.To.HasValue && !string.IsNullOrEmpty(ex.To.Value.AnchorId) ? ex.To.Value.AnchorId : AbsoluteAnchorOption;
            PathExtToOffset.Value = ex.To.HasValue ? (decimal)ex.To.Value.Offset : 0m;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncCurveInspector()
    {
        bool prevSync = IsSyncing;
        bool prevUpdate = IsUpdatingLists;
        IsSyncing = true;
        IsUpdatingLists = true;
        try
        {
            int prevSegIdx = SelectedCurveSegment.Value?.Index ?? -1;
            int prevPropIdx = SelectedCurveProp.Value?.Index ?? -1;
            int prevExtIdx = SelectedCurveExtrusion.Value?.Index ?? -1;

            WorldProject? project = GetProject();
            JunctionTemplateDocument? doc = GetDocument();
            JunctionCurveRow? row = SelectedCurve.Value;
            if (project is null || doc is null || row is null || row.Index < 0 || doc.Curves.Count <= row.Index)
            {
                HasSelectedCurve.Value = false;
                CurveSegments.Clear();
                CurveProps.Clear();
                CurveExtrusions.Clear();
                SyncStringCollection(AvailableCurveAnchorOptions, new List<string> { AbsoluteAnchorOption });
                SyncCurveSegmentInspector();
                SyncCurvePropInspector();
                SyncCurveExtrusionInspector();
                return;
            }

            HasSelectedCurve.Value = true;
            JunctionCurveTemplateModel curve = doc.Curves[row.Index];
            CurveKey.Value = curve.Key;
            CurveOriginKind.Value = curve.Origin.Kind;
            CurveOriginIsPortPin.Value = curve.Origin.Kind == JunctionCurveOriginKind.PortPinRelative;
            CurveOriginPortKey.Value = curve.Origin.PortKey ?? string.Empty;
            CurveOriginPinIndex.Value = curve.Origin.PinIndex;
            CurveOriginHasOffset.Value = curve.Origin.HasPortPinOffset;
            CurveOriginX.Value = (decimal)curve.Origin.Pose.X;
            CurveOriginY.Value = (decimal)curve.Origin.Pose.Y;
            CurveOriginZ.Value = (decimal)curve.Origin.Pose.Z;
            CurveOriginRotX.Value = (decimal)curve.Origin.Pose.RotationX;
            CurveOriginRotY.Value = (decimal)curve.Origin.Pose.RotationY;
            CurveOriginRotZ.Value = (decimal)curve.Origin.Pose.RotationZ;

            List<string> anchorOpts = new() { AbsoluteAnchorOption };
            IReadOnlyList<ResolvedAnchorInfo> curveAnchors = JunctionAnchorUtilities.CollectCurveAnchors(doc, project.LaneLayouts, curve);
            foreach (ResolvedAnchorInfo info in curveAnchors)
            {
                if (!anchorOpts.Contains(info.AnchorId, StringComparer.Ordinal))
                {
                    anchorOpts.Add(info.AnchorId);
                }
            }

            SyncStringCollection(AvailableCurveAnchorOptions, anchorOpts);

            CurveSegments.Clear();
            for (int i = 0; i < curve.Segments.Count; i++)
            {
                JunctionCurveSegment seg = curve.Segments[i];
                string anc = string.IsNullOrEmpty(seg.OutputAnchorId) ? string.Empty : $" out {seg.OutputAnchorId}";
                CurveSegments.Add(new JunctionSegmentRow(i, $"#{i}: {seg.Kind} ({seg.TargetPose.X:0.#}, {seg.TargetPose.Z:0.#}){anc}"));
            }

            CurveProps.Clear();
            for (int i = 0; i < curve.Props.Count; i++)
            {
                CurvePropPlacementRule pr = curve.Props[i];
                CurveProps.Add(new JunctionRulePropRow(i, $"[{string.Join(',', pr.ModelKeys)}] from={pr.From} iv={pr.Interval:0.#}"));
            }

            CurveExtrusions.Clear();
            for (int i = 0; i < curve.Extrusions.Count; i++)
            {
                CurveExtrusionPlacementRule ex = curve.Extrusions[i];
                string toStr = ex.To.HasValue ? $" to={ex.To.Value}" : string.Empty;
                CurveExtrusions.Add(new JunctionRuleExtrusionRow(i, $"{ex.CrossSectionKey} dx={ex.OffsetX:0.##} from={ex.From}{toStr}"));
            }

            SelectedCurveSegment.Value = 0 <= prevSegIdx && prevSegIdx < CurveSegments.Count ? CurveSegments[prevSegIdx] : CurveSegments.FirstOrDefault();
            SelectedCurveProp.Value = 0 <= prevPropIdx && prevPropIdx < CurveProps.Count ? CurveProps[prevPropIdx] : CurveProps.FirstOrDefault();
            SelectedCurveExtrusion.Value = 0 <= prevExtIdx && prevExtIdx < CurveExtrusions.Count ? CurveExtrusions[prevExtIdx] : CurveExtrusions.FirstOrDefault();

            SyncCurveSegmentInspector();
            SyncCurvePropInspector();
            SyncCurveExtrusionInspector();
        }
        finally
        {
            IsUpdatingLists = prevUpdate;
            IsSyncing = prevSync;
        }
    }

    private void SyncCurveSegmentInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionCurveTemplateModel? curve = GetSelectedCurveModel();
            JunctionSegmentRow? row = SelectedCurveSegment.Value;
            if (curve is null || row is null || row.Index < 0 || curve.Segments.Count <= row.Index)
            {
                HasSelectedCurveSegment.Value = false;
                return;
            }

            HasSelectedCurveSegment.Value = true;
            JunctionCurveSegment seg = curve.Segments[row.Index];
            CurveSegKind.Value = seg.Kind == JunctionCurveSegmentKind.BezierTo ? JunctionCurveSegmentKind.BezierTo : JunctionCurveSegmentKind.StraightTo;
            CurveSegIsBezier.Value = seg.Kind == JunctionCurveSegmentKind.BezierTo;
            CurveSegX.Value = (decimal)seg.TargetPose.X;
            CurveSegY.Value = (decimal)seg.TargetPose.Y;
            CurveSegZ.Value = (decimal)seg.TargetPose.Z;
            CurveSegRotX.Value = (decimal)seg.TargetPose.RotationX;
            CurveSegRotY.Value = (decimal)seg.TargetPose.RotationY;
            CurveSegRotZ.Value = (decimal)seg.TargetPose.RotationZ;
            CurveSegHandleScale.Value = (decimal)seg.HandleScale;
            CurveSegAnchorId.Value = seg.OutputAnchorId ?? string.Empty;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncCurvePropInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionCurveTemplateModel? curve = GetSelectedCurveModel();
            JunctionRulePropRow? row = SelectedCurveProp.Value;
            if (curve is null || row is null || row.Index < 0 || curve.Props.Count <= row.Index)
            {
                HasSelectedCurveProp.Value = false;
                return;
            }

            HasSelectedCurveProp.Value = true;
            CurvePropPlacementRule pr = curve.Props[row.Index];
            CurvePropModelKeysText.Value = string.Join(", ", pr.ModelKeys);
            CurvePropPosX.Value = (decimal)pr.Pose.X;
            CurvePropPosY.Value = (decimal)pr.Pose.Y;
            CurvePropPosZ.Value = (decimal)pr.Pose.Z;
            CurvePropRotX.Value = (decimal)pr.Pose.RotationX;
            CurvePropRotY.Value = (decimal)pr.Pose.RotationY;
            CurvePropRotZ.Value = (decimal)pr.Pose.RotationZ;
            CurvePropFromAnchor.Value = string.IsNullOrEmpty(pr.From.AnchorId) ? AbsoluteAnchorOption : pr.From.AnchorId;
            CurvePropFromOffset.Value = (decimal)pr.From.Offset;
            CurvePropSpan.Value = (decimal)pr.Span;
            CurvePropInterval.Value = (decimal)pr.Interval;
            CurvePropCount.Value = pr.Count == int.MaxValue ? 0m : pr.Count;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncCurveExtrusionInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionCurveTemplateModel? curve = GetSelectedCurveModel();
            JunctionRuleExtrusionRow? row = SelectedCurveExtrusion.Value;
            if (curve is null || row is null || row.Index < 0 || curve.Extrusions.Count <= row.Index)
            {
                HasSelectedCurveExtrusion.Value = false;
                return;
            }

            HasSelectedCurveExtrusion.Value = true;
            CurveExtrusionPlacementRule ex = curve.Extrusions[row.Index];
            CurveExtKey.Value = ex.Key ?? string.Empty;
            CurveExtCrossSectionKey.Value = ex.CrossSectionKey;
            CurveExtOffsetX.Value = (decimal)ex.OffsetX;
            CurveExtOffsetY.Value = (decimal)ex.OffsetY;
            CurveExtFromAnchor.Value = string.IsNullOrEmpty(ex.From.AnchorId) ? AbsoluteAnchorOption : ex.From.AnchorId;
            CurveExtFromOffset.Value = (decimal)ex.From.Offset;
            CurveExtHasTo.Value = ex.To.HasValue;
            CurveExtToAnchor.Value = ex.To.HasValue && !string.IsNullOrEmpty(ex.To.Value.AnchorId) ? ex.To.Value.AnchorId : AbsoluteAnchorOption;
            CurveExtToOffset.Value = ex.To.HasValue ? (decimal)ex.To.Value.Offset : 0m;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    private void SyncStaticPropInspector()
    {
        bool prevSync = IsSyncing;
        IsSyncing = true;
        try
        {
            JunctionTemplateDocument? doc = GetDocument();
            JunctionStaticPropRow? row = SelectedStaticProp.Value;
            if (doc is null || row is null || row.Index < 0 || doc.Props.Count <= row.Index)
            {
                HasSelectedStaticProp.Value = false;
                return;
            }

            HasSelectedStaticProp.Value = true;
            JunctionStaticPropPlacement sp = doc.Props[row.Index];
            StaticPropModelKey.Value = sp.ModelKey;
            StaticPropX.Value = (decimal)sp.Pose.X;
            StaticPropY.Value = (decimal)sp.Pose.Y;
            StaticPropZ.Value = (decimal)sp.Pose.Z;
            StaticPropRotX.Value = (decimal)sp.Pose.RotationX;
            StaticPropRotY.Value = (decimal)sp.Pose.RotationY;
            StaticPropRotZ.Value = (decimal)sp.Pose.RotationZ;
        }
        finally
        {
            IsSyncing = prevSync;
        }
    }

    // ---- Ports CRUD ----

    private void AddPort()
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        string defaultLayout = AvailableLaneLayoutKeys.FirstOrDefault() ?? string.Empty;
        string newKey = GenerateUniqueKey("P", doc.Ports.Select(p => p.Key));
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"ポート '{newKey}' を追加",
            draft => draft.Ports.Add(new JunctionPortDefinition(newKey, defaultLayout, new PoseDegreesModel(0, 0, 12, 0, 0, 0))));
        SelectPortIndex(Ports.Count - 1);
    }

    private void RemoveSelectedPort()
    {
        JunctionPortRow? row = SelectedPort.Value;
        if (row is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"ポート '{row.Key}' を削除",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Ports.Count)
                {
                    draft.Ports.RemoveAt(row.Index);
                }
            });
    }

    private void MoveSelectedPort(int delta)
    {
        JunctionPortRow? row = SelectedPort.Value;
        if (row is null)
        {
            return;
        }

        JunctionTemplateDocument? doc = GetDocument();
        int nextIdx = row.Index + delta;
        if (doc is null || row.Index < 0 || doc.Ports.Count <= row.Index || nextIdx < 0 || doc.Ports.Count <= nextIdx)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"ポート '{row.Key}' の順序を変更",
            draft =>
            {
                JunctionPortDefinition item = draft.Ports[row.Index];
                draft.Ports.RemoveAt(row.Index);
                draft.Ports.Insert(nextIdx, item);
            });
        SelectPortIndex(nextIdx);
    }

    private void CommitSelectedPort()
    {
        JunctionPortRow? row = SelectedPort.Value;
        if (row is null)
        {
            return;
        }

        string key = PortKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        string layout = PortLayoutKey.Value.Trim();
        PoseDegreesModel pose = new(
            (double)(PortX.Value ?? 0m),
            (double)(PortY.Value ?? 0m),
            (double)(PortZ.Value ?? 0m),
            (double)(PortRotX.Value ?? 0m),
            (double)(PortRotY.Value ?? 0m),
            (double)(PortRotZ.Value ?? 0m));

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"ポート '{key}' を編集",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Ports.Count)
                {
                    draft.Ports[row.Index] = new JunctionPortDefinition(key, layout, pose);
                }
            },
            $"JunctionTemplate.{TemplateKey}.Port.{row.Index}");
    }

    // ---- Relays CRUD ----

    private void AddRelay()
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        string layout = AvailableLaneLayoutKeys.FirstOrDefault() ?? string.Empty;
        string baseKey = GenerateUniqueKey("Relay", doc.Relays.Select(r => r.InputKey.Replace("In", string.Empty, StringComparison.Ordinal)));
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"Relay '{baseKey}' を追加",
            draft => draft.Relays.Add(new JunctionRelayDefinition($"{baseKey}In", $"{baseKey}Out", layout, new PoseDegreesModel(0, 0, 8, 0, 15, 0))));
        SelectRelayIndex(Relays.Count - 1);
    }

    private void RemoveSelectedRelay()
    {
        JunctionRelayRow? row = SelectedRelay.Value;
        if (row is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"Relay '{row.InputKey}' を削除",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Relays.Count)
                {
                    draft.Relays.RemoveAt(row.Index);
                }
            });
    }

    private void CommitSelectedRelay()
    {
        JunctionRelayRow? row = SelectedRelay.Value;
        if (row is null)
        {
            return;
        }

        string inKey = RelayInputKey.Value.Trim();
        string outKey = RelayOutputKey.Value.Trim();
        if (string.IsNullOrEmpty(inKey) || string.IsNullOrEmpty(outKey))
        {
            return;
        }

        string layout = RelayLayoutKey.Value.Trim();
        PoseDegreesModel pose = new(
            (double)(RelayX.Value ?? 0m),
            (double)(RelayY.Value ?? 0m),
            (double)(RelayZ.Value ?? 0m),
            (double)(RelayRotX.Value ?? 0m),
            (double)(RelayRotY.Value ?? 0m),
            (double)(RelayRotZ.Value ?? 0m));

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"Relay '{inKey}' を編集",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Relays.Count)
                {
                    draft.Relays[row.Index] = new JunctionRelayDefinition(inKey, outKey, layout, pose);
                }
            },
            $"JunctionTemplate.{TemplateKey}.Relay.{row.Index}");
    }

    // ---- Paths (Wire) CRUD ----

    private void AddPath()
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null || doc.Ports.Count == 0)
        {
            return;
        }

        string fromPort = doc.Ports[0].Key;
        string toPort = 1 < doc.Ports.Count ? doc.Ports[1].Key : doc.Ports[0].Key;
        string newKey = GenerateUniqueKey("Path", doc.Paths.Select(p => p.Key));

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{newKey}' を追加",
            draft =>
            {
                JunctionPathTemplateModel created = new()
                {
                    Key = newKey,
                    FromPortKey = fromPort,
                    FromPinIndex = 0,
                    ToPortKey = toPort,
                    ToPinIndex = 0,
                };
                created.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.BezierToEnd, PoseDegreesModel.Identity, 0.5));
                draft.Paths.Add(created);
            });
        SelectPathIndex(Paths.Count - 1);
    }

    private void RemoveSelectedPath()
    {
        JunctionPathRow? row = SelectedPath.Value;
        if (row is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{row.Key}' を削除",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Paths.Count)
                {
                    draft.Paths.RemoveAt(row.Index);
                }
            });
    }

    private void CommitSelectedPathMeta()
    {
        JunctionPathRow? row = SelectedPath.Value;
        if (row is null)
        {
            return;
        }

        string key = PathKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        double? td = PathTrafficDensityFactor.Value.HasValue ? (double)PathTrafficDensityFactor.Value.Value : null;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{key}' を編集",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Paths.Count)
                {
                    JunctionPathTemplateModel p = draft.Paths[row.Index];
                    p.Key = key;
                    p.FromPortKey = PathFromPortKey.Value.Trim();
                    p.FromPinIndex = int.Max(0, (int)(PathFromPinIndex.Value ?? 0m));
                    p.ToPortKey = PathToPortKey.Value.Trim();
                    p.ToPinIndex = int.Max(0, (int)(PathToPinIndex.Value ?? 0m));
                    p.TrafficDensityFactor = td;
                }
            },
            $"JunctionTemplate.{TemplateKey}.PathMeta.{row.Index}");
    }

    private void AddPathSegment()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        if (pRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' にセグメントを追加",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    draft.Paths[pRow.Index].Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.BezierToEnd, PoseDegreesModel.Identity, 0.5));
                }
            });
        SelectedPathSegment.Value = PathSegments.LastOrDefault();
    }

    private void RemoveSelectedPathSegment()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionSegmentRow? sRow = SelectedPathSegment.Value;
        WorldProject? project = GetProject();
        JunctionTemplateDocument? doc = GetDocument();
        if (pRow is null || sRow is null || project is null || doc is null)
        {
            return;
        }

        string? removedAnchor = 0 <= pRow.Index && pRow.Index < doc.Paths.Count && 0 <= sRow.Index && sRow.Index < doc.Paths[pRow.Index].Segments.Count
            ? doc.Paths[pRow.Index].Segments[sRow.Index].OutputAnchorId
            : null;
        double? resolvedS = ResolveAnchorS(doc, project, pRow.Index, isPath: true, removedAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' のセグメントを削除",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    JunctionPathTemplateModel p = draft.Paths[pRow.Index];
                    if (0 <= sRow.Index && sRow.Index < p.Segments.Count)
                    {
                        p.Segments.RemoveAt(sRow.Index);
                        if (!string.IsNullOrEmpty(removedAnchor) && resolvedS.HasValue)
                        {
                            JunctionAnchorUtilities.ExpandPathAnchorReferencesToAbsolute(p, removedAnchor, resolvedS.Value);
                        }
                    }
                }
            });
    }

    private void CommitSelectedPathSegment()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionSegmentRow? sRow = SelectedPathSegment.Value;
        if (pRow is null || sRow is null)
        {
            return;
        }

        JunctionCurveSegmentKind kind = PathSegKind.Value;
        PathSegHasTargetPose.Value = kind == JunctionCurveSegmentKind.StraightTo || kind == JunctionCurveSegmentKind.BezierTo;
        PathSegIsBezier.Value = kind == JunctionCurveSegmentKind.BezierTo || kind == JunctionCurveSegmentKind.BezierToEnd;

        string? newAnchor = string.IsNullOrWhiteSpace(PathSegAnchorId.Value) ? null : PathSegAnchorId.Value.Trim();
        JunctionTemplateDocument? doc = GetDocument();
        if (!string.IsNullOrEmpty(newAnchor)
            && doc is not null
            && 0 <= pRow.Index
            && pRow.Index < doc.Paths.Count
            && HasDuplicateAnchorInPath(doc.Paths[pRow.Index], newAnchor, isWidthSegment: false, excludeIndex: sRow.Index))
        {
            string? oldAnchor = 0 <= sRow.Index && sRow.Index < doc.Paths[pRow.Index].Segments.Count
                ? doc.Paths[pRow.Index].Segments[sRow.Index].OutputAnchorId
                : null;
            IsSyncing = true;
            try
            {
                PathSegAnchorId.Value = oldAnchor ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"進路パス '{pRow.Key}' 内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        JunctionCurveSegment updated = new(
            kind,
            new PoseDegreesModel(
                (double)(PathSegX.Value ?? 0m),
                (double)(PathSegY.Value ?? 0m),
                (double)(PathSegZ.Value ?? 0m),
                (double)(PathSegRotX.Value ?? 0m),
                (double)(PathSegRotY.Value ?? 0m),
                (double)(PathSegRotZ.Value ?? 0m)),
            (double)(PathSegHandleScale.Value ?? 0.5m),
            newAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' のセグメントを編集",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= sRow.Index && sRow.Index < draft.Paths[pRow.Index].Segments.Count)
                {
                    draft.Paths[pRow.Index].Segments[sRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.PathSeg.{pRow.Index}.{sRow.Index}");
    }

    private void AddPathWidth()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        if (pRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' に Width セグメントを追加",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    draft.Paths[pRow.Index].WidthSegments.Add(new JunctionWidthSegment(JunctionWidthSegmentKind.Constant, 2.0, 2.0, AnchoredDistance.Absolute(5.0)));
                }
            });
        SelectedPathWidth.Value = PathWidths.LastOrDefault();
    }

    private void RemoveSelectedPathWidth()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionWidthRow? wRow = SelectedPathWidth.Value;
        WorldProject? project = GetProject();
        JunctionTemplateDocument? doc = GetDocument();
        if (pRow is null || wRow is null || project is null || doc is null)
        {
            return;
        }

        string? removedAnchor = 0 <= pRow.Index && pRow.Index < doc.Paths.Count && 0 <= wRow.Index && wRow.Index < doc.Paths[pRow.Index].WidthSegments.Count
            ? doc.Paths[pRow.Index].WidthSegments[wRow.Index].OutputAnchorId
            : null;
        double? resolvedS = ResolveAnchorS(doc, project, pRow.Index, isPath: true, removedAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の Width セグメントを削除",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    JunctionPathTemplateModel p = draft.Paths[pRow.Index];
                    if (0 <= wRow.Index && wRow.Index < p.WidthSegments.Count)
                    {
                        p.WidthSegments.RemoveAt(wRow.Index);
                        if (!string.IsNullOrEmpty(removedAnchor) && resolvedS.HasValue)
                        {
                            JunctionAnchorUtilities.ExpandPathAnchorReferencesToAbsolute(p, removedAnchor, resolvedS.Value);
                        }
                    }
                }
            });
    }

    private void CommitSelectedPathWidth()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionWidthRow? wRow = SelectedPathWidth.Value;
        if (pRow is null || wRow is null)
        {
            return;
        }

        WidthIsTransition.Value = WidthKind.Value == JunctionWidthSegmentKind.TransitionTo;
        string? newAnchor = string.IsNullOrWhiteSpace(WidthAnchorId.Value) ? null : WidthAnchorId.Value.Trim();
        JunctionTemplateDocument? doc = GetDocument();
        if (!string.IsNullOrEmpty(newAnchor)
            && doc is not null
            && 0 <= pRow.Index
            && pRow.Index < doc.Paths.Count
            && HasDuplicateAnchorInPath(doc.Paths[pRow.Index], newAnchor, isWidthSegment: true, excludeIndex: wRow.Index))
        {
            string? oldAnchor = 0 <= wRow.Index && wRow.Index < doc.Paths[pRow.Index].WidthSegments.Count
                ? doc.Paths[pRow.Index].WidthSegments[wRow.Index].OutputAnchorId
                : null;
            IsSyncing = true;
            try
            {
                WidthAnchorId.Value = oldAnchor ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"進路パス '{pRow.Key}' 内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        JunctionWidthSegment updated = new(
            WidthKind.Value,
            (double)(WidthLeft.Value ?? 2m),
            (double)(WidthRight.Value ?? 2m),
            BuildAnchoredDistance(WidthLengthAnchor.Value, WidthLengthOffset.Value),
            newAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の Width を編集",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= wRow.Index && wRow.Index < draft.Paths[pRow.Index].WidthSegments.Count)
                {
                    draft.Paths[pRow.Index].WidthSegments[wRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.PathWidth.{pRow.Index}.{wRow.Index}");
    }

    private void AddPathProp()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        if (pRow is null)
        {
            return;
        }

        string defaultModel = AvailableModelKeys.FirstOrDefault() ?? string.Empty;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' に PutProp を追加",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    CurvePropPlacementRule rule = new() { Interval = 1.0, Count = int.MaxValue };
                    if (!string.IsNullOrEmpty(defaultModel))
                    {
                        rule.ModelKeys.Add(defaultModel);
                    }

                    draft.Paths[pRow.Index].Props.Add(rule);
                }
            });
        SelectedPathProp.Value = PathProps.LastOrDefault();
    }

    private void RemoveSelectedPathProp()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRulePropRow? rRow = SelectedPathProp.Value;
        if (pRow is null || rRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の PutProp を削除",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= rRow.Index && rRow.Index < draft.Paths[pRow.Index].Props.Count)
                {
                    draft.Paths[pRow.Index].Props.RemoveAt(rRow.Index);
                }
            });
    }

    private void CommitSelectedPathProp()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRulePropRow? rRow = SelectedPathProp.Value;
        if (pRow is null || rRow is null)
        {
            return;
        }

        CurvePropPlacementRule updated = BuildPropRuleFromInputs(
            PathPropModelKeysText.Value,
            PathPropPosX.Value,
            PathPropPosY.Value,
            PathPropPosZ.Value,
            PathPropRotX.Value,
            PathPropRotY.Value,
            PathPropRotZ.Value,
            PathPropFromAnchor.Value,
            PathPropFromOffset.Value,
            PathPropSpan.Value,
            PathPropInterval.Value,
            PathPropCount.Value);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の PutProp を編集",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= rRow.Index && rRow.Index < draft.Paths[pRow.Index].Props.Count)
                {
                    draft.Paths[pRow.Index].Props[rRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.PathProp.{pRow.Index}.{rRow.Index}");
    }

    private void AddPathExtrusion()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        if (pRow is null)
        {
            return;
        }

        string defaultCs = AvailableCrossSectionKeys.FirstOrDefault() ?? string.Empty;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' に PutExtrusion を追加",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count)
                {
                    draft.Paths[pRow.Index].Extrusions.Add(new CurveExtrusionPlacementRule { CrossSectionKey = defaultCs });
                }
            });
        SelectedPathExtrusion.Value = PathExtrusions.LastOrDefault();
    }

    private void RemoveSelectedPathExtrusion()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRuleExtrusionRow? eRow = SelectedPathExtrusion.Value;
        if (pRow is null || eRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の PutExtrusion を削除",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= eRow.Index && eRow.Index < draft.Paths[pRow.Index].Extrusions.Count)
                {
                    draft.Paths[pRow.Index].Extrusions.RemoveAt(eRow.Index);
                }
            });
    }

    private void CommitSelectedPathExtrusion()
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRuleExtrusionRow? eRow = SelectedPathExtrusion.Value;
        if (pRow is null || eRow is null)
        {
            return;
        }

        CurveExtrusionPlacementRule updated = BuildExtrusionRuleFromInputs(
            PathExtKey.Value,
            PathExtCrossSectionKey.Value,
            PathExtOffsetX.Value,
            PathExtOffsetY.Value,
            PathExtFromAnchor.Value,
            PathExtFromOffset.Value,
            PathExtHasTo.Value,
            PathExtToAnchor.Value,
            PathExtToOffset.Value);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"パス '{pRow.Key}' の PutExtrusion を編集",
            draft =>
            {
                if (0 <= pRow.Index && pRow.Index < draft.Paths.Count && 0 <= eRow.Index && eRow.Index < draft.Paths[pRow.Index].Extrusions.Count)
                {
                    draft.Paths[pRow.Index].Extrusions[eRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.PathExt.{pRow.Index}.{eRow.Index}");
    }

    // ---- Auxiliary Curves (AddCurve) CRUD ----

    private void AddCurve()
    {
        JunctionTemplateDocument? doc = GetDocument();
        if (doc is null)
        {
            return;
        }

        string newKey = GenerateUniqueKey("Curve", doc.Curves.Select(c => c.Key));
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{newKey}' を追加",
            draft =>
            {
                JunctionCurveTemplateModel curve = new()
                {
                    Key = newKey,
                    Origin = JunctionCurveOriginModel.Absolute(PoseDegreesModel.Identity),
                };
                curve.Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, new PoseDegreesModel(0, 0, 20, 0, 0, 0), 0.5, null));
                draft.Curves.Add(curve);
            });
        SelectCurveIndex(Curves.Count - 1);
    }

    private void RemoveSelectedCurve()
    {
        JunctionCurveRow? row = SelectedCurve.Value;
        if (row is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{row.Key}' を削除",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Curves.Count)
                {
                    draft.Curves.RemoveAt(row.Index);
                }
            });
    }

    private void CommitSelectedCurveMeta()
    {
        JunctionCurveRow? row = SelectedCurve.Value;
        if (row is null)
        {
            return;
        }

        string key = CurveKey.Value.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        CurveOriginIsPortPin.Value = CurveOriginKind.Value == JunctionCurveOriginKind.PortPinRelative;
        PoseDegreesModel pose = new(
            (double)(CurveOriginX.Value ?? 0m),
            (double)(CurveOriginY.Value ?? 0m),
            (double)(CurveOriginZ.Value ?? 0m),
            (double)(CurveOriginRotX.Value ?? 0m),
            (double)(CurveOriginRotY.Value ?? 0m),
            (double)(CurveOriginRotZ.Value ?? 0m));

        string portKey = string.IsNullOrWhiteSpace(CurveOriginPortKey.Value)
            ? (AvailablePortKeys.FirstOrDefault() ?? string.Empty)
            : CurveOriginPortKey.Value.Trim();
        int pinIdx = int.Max(0, (int)(CurveOriginPinIndex.Value ?? 0m));

        JunctionCurveOriginModel origin = CurveOriginKind.Value == JunctionCurveOriginKind.PortPinRelative
            ? (CurveOriginHasOffset.Value
                ? JunctionCurveOriginModel.FromPortPinWithOffset(portKey, pinIdx, pose)
                : JunctionCurveOriginModel.FromPortPin(portKey, pinIdx))
            : JunctionCurveOriginModel.Absolute(pose);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{key}' を編集",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Curves.Count)
                {
                    JunctionCurveTemplateModel c = draft.Curves[row.Index];
                    c.Key = key;
                    c.Origin = origin;
                }
            },
            $"JunctionTemplate.{TemplateKey}.CurveMeta.{row.Index}");
    }

    private void AddCurveSegment()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        if (cRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' にセグメントを追加",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count)
                {
                    draft.Curves[cRow.Index].Segments.Add(new JunctionCurveSegment(JunctionCurveSegmentKind.StraightTo, new PoseDegreesModel(0, 0, 10, 0, 0, 0)));
                }
            });
        SelectedCurveSegment.Value = CurveSegments.LastOrDefault();
    }

    private void RemoveSelectedCurveSegment()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionSegmentRow? sRow = SelectedCurveSegment.Value;
        WorldProject? project = GetProject();
        JunctionTemplateDocument? doc = GetDocument();
        if (cRow is null || sRow is null || project is null || doc is null)
        {
            return;
        }

        string? removedAnchor = 0 <= cRow.Index && cRow.Index < doc.Curves.Count && 0 <= sRow.Index && sRow.Index < doc.Curves[cRow.Index].Segments.Count
            ? doc.Curves[cRow.Index].Segments[sRow.Index].OutputAnchorId
            : null;
        double? resolvedS = ResolveAnchorS(doc, project, cRow.Index, isPath: false, removedAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' のセグメントを削除",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count)
                {
                    JunctionCurveTemplateModel c = draft.Curves[cRow.Index];
                    if (0 <= sRow.Index && sRow.Index < c.Segments.Count)
                    {
                        c.Segments.RemoveAt(sRow.Index);
                        if (!string.IsNullOrEmpty(removedAnchor) && resolvedS.HasValue)
                        {
                            JunctionAnchorUtilities.ExpandCurveAnchorReferencesToAbsolute(c, removedAnchor, resolvedS.Value);
                        }
                    }
                }
            });
    }

    private void CommitSelectedCurveSegment()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionSegmentRow? sRow = SelectedCurveSegment.Value;
        if (cRow is null || sRow is null)
        {
            return;
        }

        CurveSegIsBezier.Value = CurveSegKind.Value == JunctionCurveSegmentKind.BezierTo;
        string? newAnchor = string.IsNullOrWhiteSpace(CurveSegAnchorId.Value) ? null : CurveSegAnchorId.Value.Trim();
        JunctionTemplateDocument? doc = GetDocument();
        if (!string.IsNullOrEmpty(newAnchor)
            && doc is not null
            && 0 <= cRow.Index
            && cRow.Index < doc.Curves.Count
            && HasDuplicateAnchorInCurve(doc.Curves[cRow.Index], newAnchor, excludeIndex: sRow.Index))
        {
            string? oldAnchor = 0 <= sRow.Index && sRow.Index < doc.Curves[cRow.Index].Segments.Count
                ? doc.Curves[cRow.Index].Segments[sRow.Index].OutputAnchorId
                : null;
            IsSyncing = true;
            try
            {
                CurveSegAnchorId.Value = oldAnchor ?? string.Empty;
            }
            finally
            {
                IsSyncing = false;
            }

            Workspace.LastStatusMessage.Value = $"補助曲線 '{cRow.Key}' 内でアンカー '{newAnchor}' が重複しています。別の名前を指定してください。";
            return;
        }

        JunctionCurveSegment updated = new(
            CurveSegKind.Value,
            new PoseDegreesModel(
                (double)(CurveSegX.Value ?? 0m),
                (double)(CurveSegY.Value ?? 0m),
                (double)(CurveSegZ.Value ?? 0m),
                (double)(CurveSegRotX.Value ?? 0m),
                (double)(CurveSegRotY.Value ?? 0m),
                (double)(CurveSegRotZ.Value ?? 0m)),
            (double)(CurveSegHandleScale.Value ?? 0.5m),
            newAnchor);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' のセグメントを編集",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count && 0 <= sRow.Index && sRow.Index < draft.Curves[cRow.Index].Segments.Count)
                {
                    draft.Curves[cRow.Index].Segments[sRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.CurveSeg.{cRow.Index}.{sRow.Index}");
    }

    private static bool HasDuplicateAnchorInPath(
        JunctionPathTemplateModel path,
        string candidate,
        bool isWidthSegment,
        int excludeIndex)
    {
        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (!isWidthSegment && i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(path.Segments[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (int i = 0; i < path.WidthSegments.Count; i++)
        {
            if (isWidthSegment && i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(path.WidthSegments[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDuplicateAnchorInCurve(
        JunctionCurveTemplateModel curve,
        string candidate,
        int excludeIndex)
    {
        for (int i = 0; i < curve.Segments.Count; i++)
        {
            if (i == excludeIndex)
            {
                continue;
            }

            if (string.Equals(curve.Segments[i].OutputAnchorId, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void AddCurveProp()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        if (cRow is null)
        {
            return;
        }

        string defaultModel = AvailableModelKeys.FirstOrDefault() ?? string.Empty;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' に PutProp を追加",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count)
                {
                    CurvePropPlacementRule rule = new() { Interval = 1.0, Count = int.MaxValue };
                    if (!string.IsNullOrEmpty(defaultModel))
                    {
                        rule.ModelKeys.Add(defaultModel);
                    }

                    draft.Curves[cRow.Index].Props.Add(rule);
                }
            });
        SelectedCurveProp.Value = CurveProps.LastOrDefault();
    }

    private void RemoveSelectedCurveProp()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRulePropRow? rRow = SelectedCurveProp.Value;
        if (cRow is null || rRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' の PutProp を削除",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count && 0 <= rRow.Index && rRow.Index < draft.Curves[cRow.Index].Props.Count)
                {
                    draft.Curves[cRow.Index].Props.RemoveAt(rRow.Index);
                }
            });
    }

    private void CommitSelectedCurveProp()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRulePropRow? rRow = SelectedCurveProp.Value;
        if (cRow is null || rRow is null)
        {
            return;
        }

        CurvePropPlacementRule updated = BuildPropRuleFromInputs(
            CurvePropModelKeysText.Value,
            CurvePropPosX.Value,
            CurvePropPosY.Value,
            CurvePropPosZ.Value,
            CurvePropRotX.Value,
            CurvePropRotY.Value,
            CurvePropRotZ.Value,
            CurvePropFromAnchor.Value,
            CurvePropFromOffset.Value,
            CurvePropSpan.Value,
            CurvePropInterval.Value,
            CurvePropCount.Value);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' の PutProp を編集",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count && 0 <= rRow.Index && rRow.Index < draft.Curves[cRow.Index].Props.Count)
                {
                    draft.Curves[cRow.Index].Props[rRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.CurveProp.{cRow.Index}.{rRow.Index}");
    }

    private void AddCurveExtrusion()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        if (cRow is null)
        {
            return;
        }

        string defaultCs = AvailableCrossSectionKeys.FirstOrDefault() ?? string.Empty;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' に PutExtrusion を追加",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count)
                {
                    draft.Curves[cRow.Index].Extrusions.Add(new CurveExtrusionPlacementRule { CrossSectionKey = defaultCs });
                }
            });
        SelectedCurveExtrusion.Value = CurveExtrusions.LastOrDefault();
    }

    private void RemoveSelectedCurveExtrusion()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRuleExtrusionRow? eRow = SelectedCurveExtrusion.Value;
        if (cRow is null || eRow is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' の PutExtrusion を削除",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count && 0 <= eRow.Index && eRow.Index < draft.Curves[cRow.Index].Extrusions.Count)
                {
                    draft.Curves[cRow.Index].Extrusions.RemoveAt(eRow.Index);
                }
            });
    }

    private void CommitSelectedCurveExtrusion()
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRuleExtrusionRow? eRow = SelectedCurveExtrusion.Value;
        if (cRow is null || eRow is null)
        {
            return;
        }

        CurveExtrusionPlacementRule updated = BuildExtrusionRuleFromInputs(
            CurveExtKey.Value,
            CurveExtCrossSectionKey.Value,
            CurveExtOffsetX.Value,
            CurveExtOffsetY.Value,
            CurveExtFromAnchor.Value,
            CurveExtFromOffset.Value,
            CurveExtHasTo.Value,
            CurveExtToAnchor.Value,
            CurveExtToOffset.Value);

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"補助曲線 '{cRow.Key}' の PutExtrusion を編集",
            draft =>
            {
                if (0 <= cRow.Index && cRow.Index < draft.Curves.Count && 0 <= eRow.Index && eRow.Index < draft.Curves[cRow.Index].Extrusions.Count)
                {
                    draft.Curves[cRow.Index].Extrusions[eRow.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.CurveExt.{cRow.Index}.{eRow.Index}");
    }

    // ---- Static Props CRUD ----

    private void AddStaticProp()
    {
        string defaultModel = AvailableModelKeys.FirstOrDefault() ?? string.Empty;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            "静的プロップを追加",
            draft => draft.Props.Add(new JunctionStaticPropPlacement(defaultModel, PoseDegreesModel.Identity)));
        SelectedStaticProp.Value = StaticProps.LastOrDefault();
    }

    private void RemoveSelectedStaticProp()
    {
        JunctionStaticPropRow? row = SelectedStaticProp.Value;
        if (row is null)
        {
            return;
        }

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            "静的プロップを削除",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Props.Count)
                {
                    draft.Props.RemoveAt(row.Index);
                }
            });
    }

    private void MoveSelectedStaticProp(int delta)
    {
        JunctionStaticPropRow? row = SelectedStaticProp.Value;
        if (row is null)
        {
            return;
        }

        if (TryMoveTemplateListItem(
            static doc => doc.Props,
            row.Index,
            delta,
            "静的プロップの順序を変更",
            out int nextIdx)
            && 0 <= nextIdx
            && nextIdx < StaticProps.Count)
        {
            SelectedStaticProp.Value = StaticProps[nextIdx];
        }
    }

    private void MoveSelectedPathProp(int delta)
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRulePropRow? rRow = SelectedPathProp.Value;
        if (pRow is null || rRow is null)
        {
            return;
        }

        if (TryMoveTemplateListItem(
            doc => 0 <= pRow.Index && pRow.Index < doc.Paths.Count ? doc.Paths[pRow.Index].Props : null,
            rRow.Index,
            delta,
            $"パス '{pRow.Key}' の PutProp 順序を変更",
            out int nextIdx)
            && 0 <= nextIdx
            && nextIdx < PathProps.Count)
        {
            SelectedPathProp.Value = PathProps[nextIdx];
        }
    }

    private void MoveSelectedPathExtrusion(int delta)
    {
        JunctionPathRow? pRow = SelectedPath.Value;
        JunctionRuleExtrusionRow? eRow = SelectedPathExtrusion.Value;
        if (pRow is null || eRow is null)
        {
            return;
        }

        if (TryMoveTemplateListItem(
            doc => 0 <= pRow.Index && pRow.Index < doc.Paths.Count ? doc.Paths[pRow.Index].Extrusions : null,
            eRow.Index,
            delta,
            $"パス '{pRow.Key}' の PutExtrusion 順序を変更",
            out int nextIdx)
            && 0 <= nextIdx
            && nextIdx < PathExtrusions.Count)
        {
            SelectedPathExtrusion.Value = PathExtrusions[nextIdx];
        }
    }

    private void MoveSelectedCurveProp(int delta)
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRulePropRow? rRow = SelectedCurveProp.Value;
        if (cRow is null || rRow is null)
        {
            return;
        }

        if (TryMoveTemplateListItem(
            doc => 0 <= cRow.Index && cRow.Index < doc.Curves.Count ? doc.Curves[cRow.Index].Props : null,
            rRow.Index,
            delta,
            $"補助曲線 '{cRow.Key}' の PutProp 順序を変更",
            out int nextIdx)
            && 0 <= nextIdx
            && nextIdx < CurveProps.Count)
        {
            SelectedCurveProp.Value = CurveProps[nextIdx];
        }
    }

    private void MoveSelectedCurveExtrusion(int delta)
    {
        JunctionCurveRow? cRow = SelectedCurve.Value;
        JunctionRuleExtrusionRow? eRow = SelectedCurveExtrusion.Value;
        if (cRow is null || eRow is null)
        {
            return;
        }

        if (TryMoveTemplateListItem(
            doc => 0 <= cRow.Index && cRow.Index < doc.Curves.Count ? doc.Curves[cRow.Index].Extrusions : null,
            eRow.Index,
            delta,
            $"補助曲線 '{cRow.Key}' の PutExtrusion 順序を変更",
            out int nextIdx)
            && 0 <= nextIdx
            && nextIdx < CurveExtrusions.Count)
        {
            SelectedCurveExtrusion.Value = CurveExtrusions[nextIdx];
        }
    }

    private bool TryMoveTemplateListItem<TItem>(
        Func<JunctionTemplateDocument, List<TItem>?> listSelector,
        int index,
        int delta,
        string description,
        out int nextIdx)
    {
        nextIdx = index + delta;
        JunctionTemplateDocument? doc = GetDocument();
        List<TItem>? currentList = doc is not null ? listSelector(doc) : null;
        if (currentList is null
            || index < 0
            || currentList.Count <= index
            || nextIdx < 0
            || currentList.Count <= nextIdx)
        {
            return false;
        }

        int targetIdx = nextIdx;
        return Workspace.MutateJunctionTemplate(
            TemplateKey,
            description,
            draft =>
            {
                List<TItem>? list = listSelector(draft);
                if (list is null || index < 0 || list.Count <= index || targetIdx < 0 || list.Count <= targetIdx)
                {
                    return;
                }

                TItem item = list[index];
                list.RemoveAt(index);
                list.Insert(targetIdx, item);
            });
    }

    private void CommitSelectedStaticProp()
    {
        JunctionStaticPropRow? row = SelectedStaticProp.Value;
        if (row is null)
        {
            return;
        }

        JunctionStaticPropPlacement updated = new(
            StaticPropModelKey.Value.Trim(),
            new PoseDegreesModel(
                (double)(StaticPropX.Value ?? 0m),
                (double)(StaticPropY.Value ?? 0m),
                (double)(StaticPropZ.Value ?? 0m),
                (double)(StaticPropRotX.Value ?? 0m),
                (double)(StaticPropRotY.Value ?? 0m),
                (double)(StaticPropRotZ.Value ?? 0m)));

        Workspace.MutateJunctionTemplate(
            TemplateKey,
            "静的プロップを編集",
            draft =>
            {
                if (0 <= row.Index && row.Index < draft.Props.Count)
                {
                    draft.Props[row.Index] = updated;
                }
            },
            $"JunctionTemplate.{TemplateKey}.StaticProp.{row.Index}");
    }

    private void CommitUserCode()
    {
        string? nextCode = string.IsNullOrEmpty(UserCode.Value) ? null : UserCode.Value;
        Workspace.MutateJunctionTemplate(
            TemplateKey,
            $"JunctionTemplate '{TemplateKey}' の <user-code> を編集",
            draft => draft.UserCode = nextCode,
            $"JunctionTemplate.{TemplateKey}.UserCode");
    }

    private JunctionPathTemplateModel? GetSelectedPathModel()
    {
        JunctionTemplateDocument? doc = GetDocument();
        JunctionPathRow? row = SelectedPath.Value;
        return doc is not null && row is not null && 0 <= row.Index && row.Index < doc.Paths.Count
            ? doc.Paths[row.Index]
            : null;
    }

    private JunctionCurveTemplateModel? GetSelectedCurveModel()
    {
        JunctionTemplateDocument? doc = GetDocument();
        JunctionCurveRow? row = SelectedCurve.Value;
        return doc is not null && row is not null && 0 <= row.Index && row.Index < doc.Curves.Count
            ? doc.Curves[row.Index]
            : null;
    }

    private static AnchoredDistance BuildAnchoredDistance(string anchorOption, decimal? offset)
    {
        double val = (double)(offset ?? 0m);
        return string.IsNullOrWhiteSpace(anchorOption) || string.Equals(anchorOption, AbsoluteAnchorOption, StringComparison.Ordinal)
            ? AnchoredDistance.Absolute(val)
            : AnchoredDistance.RelativeTo(anchorOption.Trim(), val);
    }

    private static CurvePropPlacementRule BuildPropRuleFromInputs(
        string modelKeysText,
        decimal? x,
        decimal? y,
        decimal? z,
        decimal? rx,
        decimal? ry,
        decimal? rz,
        string fromAnchor,
        decimal? fromOffset,
        decimal? span,
        decimal? interval,
        decimal? count)
    {
        CurvePropPlacementRule rule = new()
        {
            Pose = new PoseDegreesModel(
                (double)(x ?? 0m),
                (double)(y ?? 0m),
                (double)(z ?? 0m),
                (double)(rx ?? 0m),
                (double)(ry ?? 0m),
                (double)(rz ?? 0m)),
            From = BuildAnchoredDistance(fromAnchor, fromOffset),
            Span = (double)(span ?? 0m),
            Interval = double.Max(0.0, (double)(interval ?? 1m)),
            Count = count.HasValue && 0m < count.Value ? (int)count.Value : int.MaxValue,
        };
        foreach (string part in modelKeysText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            rule.ModelKeys.Add(part);
        }

        return rule;
    }

    private static CurveExtrusionPlacementRule BuildExtrusionRuleFromInputs(
        string key,
        string crossSectionKey,
        decimal? dx,
        decimal? dy,
        string fromAnchor,
        decimal? fromOffset,
        bool hasTo,
        string toAnchor,
        decimal? toOffset)
    {
        return new CurveExtrusionPlacementRule
        {
            Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            CrossSectionKey = crossSectionKey.Trim(),
            OffsetX = (double)(dx ?? 0m),
            OffsetY = (double)(dy ?? 0m),
            From = BuildAnchoredDistance(fromAnchor, fromOffset),
            To = hasTo ? BuildAnchoredDistance(toAnchor, toOffset) : null,
        };
    }

    private static void AppendModelToRule(BindableReactiveProperty<string> targetText, BindableReactiveProperty<string> keyToAdd)
    {
        string addition = keyToAdd.Value.Trim();
        if (string.IsNullOrEmpty(addition))
        {
            return;
        }

        string current = targetText.Value.Trim();
        targetText.Value = string.IsNullOrEmpty(current) ? addition : $"{current}, {addition}";
    }

    private static double? ResolveAnchorS(
        JunctionTemplateDocument doc,
        WorldProject project,
        int itemIndex,
        bool isPath,
        string? anchorId)
    {
        if (string.IsNullOrEmpty(anchorId))
        {
            return null;
        }

        IReadOnlyList<ResolvedAnchorInfo> anchors = isPath && 0 <= itemIndex && itemIndex < doc.Paths.Count
            ? JunctionAnchorUtilities.CollectPathAnchors(doc, project.LaneLayouts, doc.Paths[itemIndex])
            : (!isPath && 0 <= itemIndex && itemIndex < doc.Curves.Count
                ? JunctionAnchorUtilities.CollectCurveAnchors(doc, project.LaneLayouts, doc.Curves[itemIndex])
                : Array.Empty<ResolvedAnchorInfo>());
        foreach (ResolvedAnchorInfo a in anchors)
        {
            if (string.Equals(a.AnchorId, anchorId, StringComparison.Ordinal))
            {
                return a.ResolvedS;
            }
        }

        return null;
    }

    private static string GenerateUniqueKey(string prefix, IEnumerable<string> existing)
    {
        HashSet<string> used = new(existing, StringComparer.Ordinal);
        int i = 1;
        while (true)
        {
            string candidate = $"{prefix}{i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }

            i++;
        }
    }

    private static void SyncStringCollection(ObservableCollection<string> target, List<string> next)
    {
        if (target.SequenceEqual(next, StringComparer.Ordinal))
        {
            return;
        }

        for (int i = target.Count - 1; 0 <= i; i--)
        {
            if (!next.Contains(target[i], StringComparer.Ordinal))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < next.Count; i++)
        {
            string desired = next[i];
            int currentIdx = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (string.Equals(target[j], desired, StringComparison.Ordinal))
                {
                    currentIdx = j;
                    break;
                }
            }

            if (currentIdx == i)
            {
                continue;
            }

            if (0 <= currentIdx)
            {
                target.Move(currentIdx, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }
    }
}
