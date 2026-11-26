using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TransportX.Cameras;
using TransportX.Dependency;
using TransportX.Physics;
using TransportX.Rendering;
using TransportX.Rendering.Backend;
using TransportX.Rendering.Pipelines;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Core.Models.Environment;
using TransportX.WorldEditor.Core.Models.Network;
using TransportX.WorldEditor.Core.Models.Primitives;
using TransportX.WorldEditor.Core.Models.Templates;
using TransportX.WorldEditor.Core.Validation;
using TransportX.WorldEditor.Rendering.Diagnostics;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.Worlds;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

using TxPlatform = TransportX.Platform;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// Shared offscreen Direct3D 11 preview host reusing TransportX's 7-pass PBR <see cref="Renderer"/>
/// and <see cref="EditorWorld"/> for isolated template inspection without spawning a secondary HWND.
/// </summary>
public abstract class TemplatePreviewRendererBase : IDisposable
{
    private readonly ViewportWindowAdapter WindowAdapter;
    private readonly GraphicsHost GraphicsHostValue;
    private readonly OffscreenGraphicsClient GraphicsClientValue;
    private readonly PhysicsHost PhysicsHostValue;
    private readonly Renderer RendererValue;
    private readonly PluginLoadContext AppContextValue;
    private readonly DummyInputHost InputHost = new();
    private readonly DummyInputContext InputContext = new();
    private readonly TimeManager RenderTime = new();

    private Vector3 OrbitTarget = Vector3.Zero;
    private float OrbitYawDeg = -35f;
    private float OrbitPitchDeg = 32f;
    private float OrbitDistance = 42f;
    private bool ShowNetworkOverlay = true;
    private bool NeedsExposureWarmup = true;
    private bool Disposed;

    public EditorErrorCollector ErrorCollector { get; } = new();
    public FreeCameraController CameraController { get; }
    public EditorWorld World { get; }

    public int PixelWidth => GraphicsClientValue.PixelWidth;
    public int PixelHeight => GraphicsClientValue.PixelHeight;
    public Vector3 CurrentOrbitTarget => OrbitTarget;
    public float CurrentOrbitDistance => OrbitDistance;

    protected TemplatePreviewRendererBase(string previewInfoFileName, int initialWidth = 640, int initialHeight = 480)
    {
        int w = int.Max(1, initialWidth);
        int h = int.Max(1, initialHeight);

        WindowAdapter = new ViewportWindowAdapter(0, w, h);
        GraphicsHostValue = new GraphicsHost();
        GraphicsClientValue = new OffscreenGraphicsClient();
        GraphicsClientValue.Resize(GraphicsHostValue.Device, w, h);

        PhysicsHostValue = PhysicsHost.Create();

        TxPlatform platform = new()
        {
            Window = WindowAdapter,
            Input = InputContext,
        };

        RendererOptions rendererOptions = new()
        {
            MaxAnisotropy = 4,
            DrawChunkCount = 3,
            ShadowOptions = new ShadowOptions
            {
                DrawChunkCount = 3,
                Resolution = 1024,
            },
        };
        RendererValue = new Renderer(platform, GraphicsHostValue, GraphicsClientValue, rendererOptions);

        CameraController = new FreeCameraController();
        AppContextValue = new PluginLoadContext(typeof(TemplatePreviewRendererBase).Assembly.Location);

        WorldBuilder builder = new(new EditorPreviewWorldInfo
        {
            InfoPath = Path.Combine(AppContext.BaseDirectory, previewInfoFileName),
            Path = typeof(TemplatePreviewRendererBase).Assembly.Location,
        })
        {
            Platform = platform,
            GraphicsHost = GraphicsHostValue,
            GraphicsClient = GraphicsClientValue,
            AudioHost = new DummyAudioHost(),
            AudioClient = new DummyAudioClient(),
            InputHost = InputHost,
            InputClient = new DummyInputClient(),
            PhysicsHost = PhysicsHostValue,
            Options = new WorldOptions
            {
                SimulationChunkCount = 3,
                IsDebugMode = false,
            },
            ErrorCollector = ErrorCollector,
            AppContext = AppContextValue,
            TimeManager = new TimeManager(),
            Camera = CameraController.Camera,
        };

        World = new EditorWorld(builder);
        RendererValue.InitializeComponents(Array.Empty<ISceneCaptureComponent>());
        SyncOrbitCamera();
    }

    public void SetShowNetworkOverlay(bool showNetwork)
    {
        ShowNetworkOverlay = showNetwork;
    }

    protected abstract bool TryGetPreferredOrbitBounds(out BoundingBox bounds);

    public void ResetOrbit()
    {
        Vector3 center = Vector3.Zero;
        float distance = 42f;

        if (TryGetPreferredOrbitBounds(out BoundingBox bounds))
        {
            Vector3 size = bounds.Max - bounds.Min;
            float maxSpan = float.Max(size.X, float.Max(size.Y, size.Z));
            if (0.5f < maxSpan && float.IsFinite(maxSpan))
            {
                center = new Vector3(
                    (bounds.Min.X + bounds.Max.X) * 0.5f,
                    (bounds.Min.Y + bounds.Max.Y) * 0.5f,
                    (bounds.Min.Z + bounds.Max.Z) * 0.5f);
                distance = float.Clamp(maxSpan * 1.15f, 12f, 220f);
            }
        }

        OrbitTarget = center;
        OrbitYawDeg = -35f;
        OrbitPitchDeg = 32f;
        OrbitDistance = distance;
        SyncOrbitCamera();
    }

    public void OrbitBy(float deltaYawDeg, float deltaPitchDeg)
    {
        OrbitYawDeg = (OrbitYawDeg + deltaYawDeg) % 360f;
        OrbitPitchDeg = float.Clamp(OrbitPitchDeg + deltaPitchDeg, -82f, 82f);
        SyncOrbitCamera();
    }

    public void PanBy(float deltaX, float deltaY)
    {
        if (float.Abs(deltaX) < 1e-4f && float.Abs(deltaY) < 1e-4f)
        {
            return;
        }

        Pose camPose = CameraController.WorldPose.Pose;
        Vector3 right = Vector3.Transform(Vector3.UnitX, camPose.Orientation);
        Vector3 up = Vector3.Transform(Vector3.UnitY, camPose.Orientation);
        float scale = OrbitDistance * 0.002f;
        OrbitTarget -= right * (deltaX * scale);
        OrbitTarget += up * (deltaY * scale);
        SyncOrbitCamera();
    }

    public void ZoomBy(float wheelSteps)
    {
        if (float.Abs(wheelSteps) < 1e-4f)
        {
            return;
        }

        float factor = float.Pow(0.86f, wheelSteps);
        OrbitDistance = float.Clamp(OrbitDistance * factor, 4f, 400f);
        SyncOrbitCamera();
    }

    protected void ApplyPreviewProject(WorldProject previewProject)
    {
        ArgumentNullException.ThrowIfNull(previewProject);
        ObjectDisposedException.ThrowIf(Disposed, this);

        World.SyncFromProjectIncremental(previewProject);

        ViewportOverlayFlags flags = ViewportOverlayFlags.Normal;
        if (ShowNetworkOverlay)
        {
            flags |= ViewportOverlayFlags.Network;
        }

        World.UpdateOverlays(
            selectedPropId: null,
            selectedSplineId: null,
            pendingConnectPort: null,
            EditorToolMode.Select,
            GizmoAxis.None,
            flags,
            placementCursorWorldPos: null,
            ChunkIndex.Zero,
            selectedJunctionId: null);
    }

    protected void ClearPreviewWorld()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        World.ClearWorldContents();
    }

    public bool RenderToRgbaBuffer(int pixelWidth, int pixelHeight, byte[] destination, int destinationStride, double? elapsedSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Disposed)
        {
            return false;
        }

        int w = int.Clamp(pixelWidth, 1, 1920);
        int h = int.Clamp(pixelHeight, 1, 1080);

        if (w != WindowAdapter.Size.X || h != WindowAdapter.Size.Y || GraphicsClientValue.Surface is null)
        {
            WindowAdapter.UpdateSize(w, h);
            GraphicsHostValue.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
            GraphicsHostValue.Context.Flush();
            GraphicsClientValue.Resize(GraphicsHostValue.Device, w, h);
        }

        if (GraphicsClientValue.Surface is null)
        {
            return false;
        }

        SyncOrbitCamera();
        ViewportOverlayFlags flags = ViewportOverlayFlags.Normal;
        if (ShowNetworkOverlay)
        {
            flags |= ViewportOverlayFlags.Network;
        }

        World.UpdateOverlays(
            selectedPropId: null,
            selectedSplineId: null,
            pendingConnectPort: null,
            EditorToolMode.Select,
            GizmoAxis.None,
            flags,
            placementCursorWorldPos: null,
            ChunkIndex.Zero,
            selectedJunctionId: null);

        double stepSeconds = elapsedSeconds.HasValue
            ? double.Clamp(elapsedSeconds.Value, 1.0 / 120.0, 0.25)
            : 1.0 / 30.0;
        TimeSpan elapsed = TimeSpan.FromSeconds(stepSeconds);
        RenderTime.Tick(elapsed);
        WindowAdapter.StepTime(elapsed.TotalSeconds);
        CameraController.SyncCamera(RenderTime.DeltaTime);
        World.UpdateCameraChunk();

        if (NeedsExposureWarmup)
        {
            NeedsExposureWarmup = false;
            TimeSpan warmupElapsed = TimeSpan.FromSeconds(0.25);
            GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            RendererValue.Render(CameraController.Camera, World, warmupElapsed);
            GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            RendererValue.Render(CameraController.Camera, World, warmupElapsed);
        }
        else
        {
            GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            RendererValue.Render(CameraController.Camera, World, elapsed);
        }

        GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        GraphicsHostValue.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);

        return GraphicsClientValue.CopyPixelsRgba(GraphicsHostValue.Context, destination, destinationStride);
    }

    private void SyncOrbitCamera()
    {
        PoseDegreesModel rotPose = new(0.0, 0.0, 0.0, OrbitPitchDeg, OrbitYawDeg, 0.0);
        Pose3D orientationPose = Pose3D.FromDegrees(rotPose);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, orientationPose.Orientation);
        Vector3 camPos = OrbitTarget - forward * OrbitDistance;

        WorldPoseDegreesModel camPose = new(
            ChunkIndexModel.Zero,
            new PoseDegreesModel(camPos.X, camPos.Y, camPos.Z, OrbitPitchDeg, OrbitYawDeg, 0.0));
        CameraController.Locate(camPose);
    }

    protected static WorldProject CreateBasePreviewProject(WorldProject source)
    {
        ArgumentNullException.ThrowIfNull(source);

        WorldProject preview = new()
        {
            WorldInfoFilePath = source.WorldInfoFilePath,
            WorldDirectory = source.WorldDirectory,
            WorldInfo = source.WorldInfo,
            Init = source.Init,
        };

        foreach (KeyValuePair<string, Core.Models.Assets.ModelListDocument> kv in source.ModelLists)
        {
            preview.ModelLists[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Core.Models.Assets.SoundListDocument> kv in source.SoundLists)
        {
            preview.SoundLists[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, LaneLayoutDocument> kv in source.LaneLayouts)
        {
            preview.LaneLayouts[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, CrossSectionDocument> kv in source.CrossSections)
        {
            preview.CrossSections[kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, EnvironmentProfileDocument> kv in source.Environments)
        {
            preview.Environments[kv.Key] = kv.Value;
        }

        preview.Placement.EnvironmentPath = source.Placement.EnvironmentPath;
        preview.Placement.DirectionalLight = source.Placement.DirectionalLight;

        return preview;
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        World.Dispose();
        PhysicsHostValue.Dispose();
        RendererValue.Dispose();

        GraphicsHostValue.Context.ClearState();
        GraphicsHostValue.Context.Flush();

        GraphicsClientValue.Dispose();
        GraphicsHostValue.Dispose();
        InputHost.Dispose();
        WindowAdapter.Dispose();
        AppContextValue.Unload();
        GC.SuppressFinalize(this);
    }
}
