using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TransportX.Cameras;
using TransportX.Dependency;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.Rendering.Pipelines;
using TransportX.Rendering;
using TransportX.Spatial;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Rendering.Diagnostics;
using TransportX.WorldEditor.Rendering.Interaction;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.WorldEditor.Rendering.Verification;
using TransportX.WorldEditor.Rendering.Worlds;
using TransportX.Worlds;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using TxPlatform = TransportX.Platform;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// Minimal <see cref="IWorldInfo"/> describing the in-editor live preview world.
/// </summary>
internal sealed class EditorPreviewWorldInfo : IWorldInfo
{
    public string InfoPath { get; init; } = string.Empty;
    public string Title { get; init; } = "EditorPreview";
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? Identifier { get; init; }
    public string AppPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Owns the TransportX Direct3D 11 device, swap chain bound to a hosted child <c>HWND</c>,
/// physics host, 7-pass <see cref="Renderer"/>, <see cref="EditorWorld"/>, and free camera.
/// Must be driven from a single (UI) thread.
/// </summary>
public sealed class EditorViewportRenderer : IDisposable
{
    public const int DefaultDrawChunkCount = 3;

    private readonly ViewportWindowAdapter WindowAdapter;
    private readonly GraphicsHost GraphicsHostValue;
    private readonly GraphicsClient GraphicsClientValue;
    private readonly PhysicsHost PhysicsHostValue;
    private readonly Renderer RendererValue;
    private readonly PluginLoadContext AppContextValue;
    private readonly TxPlatform PlatformValue;
    private readonly DummyInputHost InputHost = new DummyInputHost();
    private readonly DummyInputContext InputContext = new DummyInputContext();
    private readonly TimeManager RenderTime = new TimeManager();
    private ScriptVerificationRunner? VerificationRunner;
    private EditorRenderMode ActiveModeValue = EditorRenderMode.EditLivePreview;
    private bool Disposed;

    public EditorErrorCollector ErrorCollector { get; } = new EditorErrorCollector();
    public FreeCameraController CameraController { get; }
    public EditorWorld World { get; }
    public EditorRenderMode ActiveMode => ActiveModeValue;
    public IReadOnlyList<TransportX.Diagnostics.Error> VerificationErrors =>
        VerificationRunner?.ErrorCollector.Errors ?? Array.Empty<TransportX.Diagnostics.Error>();

    public int PixelWidth => WindowAdapter.Size.X;
    public int PixelHeight => WindowAdapter.Size.Y;

    public EditorViewportRenderer(nint hwnd, int pixelWidth, int pixelHeight)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("HWND が無効です。", nameof(hwnd));
        }

        WindowAdapter = new ViewportWindowAdapter(hwnd, pixelWidth, pixelHeight);
        int w = WindowAdapter.Size.X;
        int h = WindowAdapter.Size.Y;

        GraphicsHostValue = new GraphicsHost();

        SwapChainDescription1 swapChainDesc = new SwapChainDescription1
        {
            BufferCount = 2,
            Width = (uint)w,
            Height = (uint)h,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            BufferUsage = Usage.RenderTargetOutput,
        };
        SwapChainFullscreenDescription fullscreenDesc = new SwapChainFullscreenDescription
        {
            Windowed = true,
        };
        IDXGISwapChain1 swapChain = GraphicsHostValue.DXGIFactory.CreateSwapChainForHwnd(
            GraphicsHostValue.Device, hwnd, swapChainDesc, fullscreenDesc);

        GraphicsClientValue = new GraphicsClient(hwnd, swapChain);
        GraphicsClientValue.Resize(GraphicsHostValue.Device, w, h);

        PhysicsHostValue = PhysicsHost.Create();

        PlatformValue = new TxPlatform
        {
            Window = WindowAdapter,
            Input = InputContext,
        };

        RendererOptions rendererOptions = new RendererOptions
        {
            MaxAnisotropy = 8,
            DrawChunkCount = DefaultDrawChunkCount,
            ShadowOptions = new ShadowOptions
            {
                DrawChunkCount = 2,
                Resolution = 2048,
            },
        };
        RendererValue = new Renderer(PlatformValue, GraphicsHostValue, GraphicsClientValue, rendererOptions);

        CameraController = new FreeCameraController();
        AppContextValue = new PluginLoadContext(typeof(EditorViewportRenderer).Assembly.Location);

        WorldBuilder builder = new WorldBuilder(new EditorPreviewWorldInfo
        {
            InfoPath = Path.Combine(AppContext.BaseDirectory, "EditorPreview.xml"),
            Path = typeof(EditorViewportRenderer).Assembly.Location,
        })
        {
            Platform = PlatformValue,
            GraphicsHost = GraphicsHostValue,
            GraphicsClient = GraphicsClientValue,
            AudioHost = new DummyAudioHost(),
            AudioClient = new DummyAudioClient(),
            InputHost = InputHost,
            InputClient = new DummyInputClient(),
            PhysicsHost = PhysicsHostValue,
            Options = new WorldOptions
            {
                SimulationChunkCount = 2,
                IsDebugMode = false,
            },
            ErrorCollector = ErrorCollector,
            AppContext = AppContextValue,
            TimeManager = new TimeManager(),
            Camera = CameraController.Camera,
        };

        World = new EditorWorld(builder)
        {
            GridDrawChunkCount = rendererOptions.DrawChunkCount,
        };
        RendererValue.InitializeComponents(Array.Empty<ISceneCaptureComponent>());
    }

    public bool StartScriptVerification(
        WorldProject project,
        out IReadOnlyList<TransportX.Diagnostics.Error> errors)
    {
        (bool started, ScriptVerificationRunner runner, IReadOnlyList<TransportX.Diagnostics.Error> startErrors) =
            PrepareScriptVerificationOnBackground(project);
        CommitScriptVerification(runner, started);
        errors = startErrors;
        return started;
    }

    /// <summary>
    /// Compiles and initializes a Mode 2 <see cref="ScriptVerificationRunner"/> so heavy loading can run
    /// on a background worker while frame rendering is paused. Call <see cref="CommitScriptVerification"/> on the UI thread next.
    /// </summary>
    public (bool Started, ScriptVerificationRunner Runner, IReadOnlyList<TransportX.Diagnostics.Error> Errors)
        PrepareScriptVerificationOnBackground(WorldProject project, bool isDebugMode = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ScriptVerificationRunner runner = new();
        bool started = runner.Start(
            project,
            PlatformValue,
            GraphicsHostValue,
            GraphicsClientValue,
            CameraController.Camera,
            isDebugMode);
        return (started, runner, runner.ErrorCollector.Errors);
    }

    /// <summary>
    /// Installs a prepared <see cref="ScriptVerificationRunner"/> as the active Mode 2 scene on the render thread.
    /// </summary>
    public void CommitScriptVerification(
        ScriptVerificationRunner runner,
        bool started,
        ViewportOverlayFlags overlayFlags = ViewportOverlayFlags.Default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        if (Disposed)
        {
            runner.Dispose();
            return;
        }

        if (VerificationRunner is not null && !ReferenceEquals(VerificationRunner, runner))
        {
            VerificationRunner.Dispose();
            VerificationRunner = null;
        }

        VerificationRunner = runner;
        ActiveModeValue = EditorRenderMode.ScriptVerification;

        if (started && runner.ActiveWorld is WorldBase verifyWorld)
        {
            ApplyVerificationOverlayFlags(overlayFlags);
            RendererValue.InitializeComponents(
                verifyWorld.Components.Values
                    .Concat(verifyWorld.Avatar?.Components.Values ?? [])
                    .OfType<ISceneCaptureComponent>());
            verifyWorld.UpdateCameraChunk();
        }
    }

    /// <summary>
    /// Applies editor layer toggles (<c>Colliders</c>, <c>Network</c>, <c>Traffic</c>) to the active
    /// Mode 2 camera's <see cref="ICamera.VisibleLayers"/>.
    /// </summary>
    public void ApplyVerificationOverlayFlags(ViewportOverlayFlags overlayFlags)
    {
        if (Disposed || ActiveModeValue != EditorRenderMode.ScriptVerification)
        {
            return;
        }

        ICamera.VisualLayers layers = ICamera.VisualLayers.Normal;
        if (overlayFlags.HasFlag(ViewportOverlayFlags.Colliders))
        {
            layers |= ICamera.VisualLayers.Colliders;
        }

        if (overlayFlags.HasFlag(ViewportOverlayFlags.Network))
        {
            layers |= ICamera.VisualLayers.Network;
        }

        if (overlayFlags.HasFlag(ViewportOverlayFlags.Traffic))
        {
            layers |= ICamera.VisualLayers.Traffic;
        }

        CameraController.Camera.VisibleLayers = layers;
    }

    public void StopScriptVerification()
    {
        if (VerificationRunner is not null)
        {
            VerificationRunner.Dispose();
            VerificationRunner = null;
        }

        ActiveModeValue = EditorRenderMode.EditLivePreview;
        if (!Disposed)
        {
            RendererValue.InitializeComponents(Array.Empty<ISceneCaptureComponent>());
            World.UpdateCameraChunk();
        }
    }

    public void Resize(int pixelWidth, int pixelHeight)
    {
        if (Disposed || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        if (pixelWidth == WindowAdapter.Size.X && pixelHeight == WindowAdapter.Size.Y)
        {
            return;
        }

        WindowAdapter.UpdateSize(pixelWidth, pixelHeight);
        GraphicsHostValue.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        GraphicsHostValue.Context.Flush();
        GraphicsClientValue.Resize(GraphicsHostValue.Device, WindowAdapter.Size.X, WindowAdapter.Size.Y);
    }

    public void RebuildFromProject(WorldProject project, bool relocateCamera)
    {
        ArgumentNullException.ThrowIfNull(project);
        ErrorCollector.Clear();
        World.RebuildFromProject(project);
        if (relocateCamera)
        {
            CameraController.Locate(project.Placement.Camera.Pose);
        }

        World.UpdateCameraChunk();
    }

    public void SyncFromProjectIncremental(WorldProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        World.SyncFromProjectIncremental(project);
        World.UpdateCameraChunk();
    }

    public ViewContext CreateViewContext()
    {
        return CameraController.Camera.CreateViewContext(new SizeI(WindowAdapter.Size.X, WindowAdapter.Size.Y));
    }

    public void RenderFrame(TimeSpan elapsed)
    {
        if (Disposed || GraphicsClientValue.Surface is null)
        {
            return;
        }

        RenderTime.Tick(elapsed);
        WindowAdapter.StepTime(elapsed.TotalSeconds);
        CameraController.SyncCamera(RenderTime.DeltaTime);

        WorldBase renderWorld = World;
        if (ActiveModeValue == EditorRenderMode.ScriptVerification && VerificationRunner is not null)
        {
            if (VerificationRunner.IsRunning && VerificationRunner.ActiveWorld is WorldBase verifyWorld)
            {
                VerificationRunner.Tick(elapsed);
                verifyWorld.UpdateCameraChunk();
                renderWorld = verifyWorld;
            }
        }
        else
        {
            World.UpdateCameraChunk();
        }

        GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        RendererValue.Render(CameraController.Camera, renderWorld, RenderTime.DeltaTime);
        GraphicsHostValue.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        GraphicsClientValue.SwapChain.Present(1, PresentFlags.None);
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;

        StopScriptVerification();
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
    }
}
