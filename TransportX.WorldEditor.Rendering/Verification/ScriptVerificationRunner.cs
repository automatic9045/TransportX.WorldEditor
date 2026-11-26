using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TransportX.Cameras;
using TransportX.Dependency;
using TransportX.Diagnostics;
using TransportX.IO;
using TransportX.Physics;
using TransportX.Rendering.Backend;
using TransportX.Scripting;
using TransportX.Scripting.Worlds;
using TransportX.WorldEditor.Core.Models;
using TransportX.WorldEditor.Rendering.Diagnostics;
using TransportX.WorldEditor.Rendering.Platform;
using TransportX.Worlds;

namespace TransportX.WorldEditor.Rendering.Verification;

/// <summary>
/// Mode 2 verification runner that executes the real TransportX <see cref="ScriptWorld"/> pipeline
/// inside an isolated collectible <see cref="PluginLoadContext"/> and <see cref="PhysicsHost"/>,
/// routing all compile and runtime <see cref="ScriptError"/> entries into <see cref="ErrorCollector"/>.
/// </summary>
public sealed class ScriptVerificationRunner : IDisposable
{
    private static readonly TimeSpan LimitComputingTime = TimeSpan.FromSeconds(1.0 / 60.0);
    private static readonly TimeSpan MaxFrameElapsed = TimeSpan.FromSeconds(0.1);

    private PhysicsHost? PhysicsHostValue;
    private PluginLoadContext? VerificationAppContext;
    private DummyAudioHost? AudioHostValue;
    private DummyAudioClient? AudioClientValue;
    private DummyInputHost? InputHostValue;
    private DummyInputClient? InputClientValue;
    private TimeManager? UpdateTimeManager;
    private TimeSpan ComputingAccumulator;
    private bool Disposed;

    public EditorErrorCollector ErrorCollector { get; } = new();
    public WorldBase? ActiveWorld { get; private set; }
    public bool HasFatalError { get; private set; }
    public bool IsRunning => !Disposed && ActiveWorld is not null && !HasFatalError;

    public bool Start(
        WorldProject project,
        TransportX.Platform platform,
        IGraphicsHost graphicsHost,
        IGraphicsClient graphicsClient,
        ICamera camera,
        bool isDebugMode = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(graphicsHost);
        ArgumentNullException.ThrowIfNull(graphicsClient);
        ArgumentNullException.ThrowIfNull(camera);

        if (Disposed)
        {
            throw new ObjectDisposedException(nameof(ScriptVerificationRunner));
        }

        CleanupRuntime();
        ErrorCollector.Clear();
        HasFatalError = false;
        ComputingAccumulator = TimeSpan.Zero;

        string? worldInfoPath = project.WorldInfoFilePath;
        if (string.IsNullOrWhiteSpace(worldInfoPath) || !File.Exists(worldInfoPath))
        {
            ErrorCollector.Report(new Error(
                ErrorLevel.Fatal,
                "スクリプト検証を実行する前にワールドをディスクへ保存してください (WorldInfo XML が見つかりません)。",
                worldInfoPath ?? project.Placement.RelativePath));
            HasFatalError = true;
            return false;
        }

        string? infoDir = Path.GetDirectoryName(worldInfoPath);
        string rawRuntimePath = string.IsNullOrWhiteSpace(project.WorldInfo.Path)
            ? "$(bin)TransportX.Scripting.dll"
            : project.WorldInfo.Path;
        string expandedPath = PathMacros.Expand(rawRuntimePath);
        string resolvedAssemblyPath = !string.IsNullOrEmpty(infoDir)
            ? Path.GetFullPath(Path.Combine(infoDir, expandedPath))
            : expandedPath;

        if (!File.Exists(resolvedAssemblyPath))
        {
            resolvedAssemblyPath = typeof(ScriptWorld).Assembly.Location;
        }

        VerificationWorldInfo worldInfo = new()
        {
            InfoPath = worldInfoPath,
            Title = project.WorldInfo.Title,
            Description = project.WorldInfo.Description,
            Author = project.WorldInfo.Author,
            Path = resolvedAssemblyPath,
            Identifier = project.WorldInfo.Identifier,
            AppPath = project.WorldInfo.AppPath ?? string.Empty,
            Args = project.WorldInfo.Args.ToArray(),
        };

        try
        {
            PhysicsHostValue = PhysicsHost.Create();
            AudioHostValue = new();
            AudioClientValue = new();
            InputHostValue = new();
            InputClientValue = new();
            UpdateTimeManager = new();
            VerificationAppContext = new(typeof(ScriptVerificationRunner).Assembly.Location);

            WorldBuilder builder = new(worldInfo)
            {
                Platform = platform,
                GraphicsHost = graphicsHost,
                GraphicsClient = graphicsClient,
                AudioHost = AudioHostValue,
                AudioClient = AudioClientValue,
                InputHost = InputHostValue,
                InputClient = InputClientValue,
                PhysicsHost = PhysicsHostValue,
                Options = new WorldOptions
                {
                    SimulationChunkCount = 2,
                    IsDebugMode = isDebugMode,
                },
                ErrorCollector = ErrorCollector,
                AppContext = VerificationAppContext,
                TimeManager = UpdateTimeManager,
                Camera = camera,
            };

            WorldBase builtWorld = builder.Build();
            if (ErrorCollector.HasFatalError)
            {
                HasFatalError = true;
                DisposeBuiltWorld(builtWorld);
                CleanupRuntime();
                return false;
            }

            ActiveWorld = builtWorld;
            builtWorld.OnStart();
            builtWorld.UpdateCameraChunk();
            if (ErrorCollector.HasFatalError)
            {
                HasFatalError = true;
                CleanupRuntime();
                return false;
            }

            return true;
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            ReportExceptionFatal(inner, worldInfoPath);
            CleanupRuntime();
            return false;
        }
        catch (Exception ex)
        {
            ReportExceptionFatal(ex, worldInfoPath);
            CleanupRuntime();
            return false;
        }
    }

    public void Tick(TimeSpan elapsed)
    {
        if (!IsRunning || ActiveWorld is null || PhysicsHostValue is null || UpdateTimeManager is null)
        {
            return;
        }

        try
        {
            TimeSpan clamped = MaxFrameElapsed < elapsed ? MaxFrameElapsed : (elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
            UpdateTimeManager.Tick(clamped);
            TimeSpan dt = UpdateTimeManager.DeltaTime;

            ComputingAccumulator += dt;
            while (LimitComputingTime <= ComputingAccumulator)
            {
                PhysicsHostValue.Simulation.Timestep((float)LimitComputingTime.TotalSeconds, PhysicsHostValue.ThreadDispatcher);
                ActiveWorld.SubTick(LimitComputingTime);
                ComputingAccumulator -= LimitComputingTime;
            }

            ActiveWorld.Tick(dt);
            ActiveWorld.UpdateCameraChunk();
            if (ErrorCollector.HasFatalError)
            {
                HasFatalError = true;
            }
        }
        catch (Exception ex)
        {
            ReportExceptionFatal(ex, (ActiveWorld as ScriptWorld)?.ScriptPath);
        }
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        CleanupRuntime();
    }

    private void ReportExceptionFatal(Exception ex, string? fallbackLocation)
    {
        HasFatalError = true;
        ScriptError scriptErr = new(ErrorLevel.Fatal, ex, useExceptionStackTrace: true, defaultLocation: fallbackLocation);
        ErrorCollector.Report(scriptErr);
    }

    private void DisposeBuiltWorld(WorldBase world)
    {
        try
        {
            world.Dispose();
        }
        catch
        {
            // Keep cleanup resilient when aborting failed verification builds.
        }

        if (VerificationAppContext is not null && !ReferenceEquals(world.WorldContext, VerificationAppContext))
        {
            VerificationAppContext.Children.Remove(world.WorldContext);
            try
            {
                world.WorldContext.Unload();
            }
            catch
            {
            }
        }
    }

    private void CleanupRuntime()
    {
        if (ActiveWorld is not null)
        {
            WorldBase toDispose = ActiveWorld;
            ActiveWorld = null;
            DisposeBuiltWorld(toDispose);
        }

        if (PhysicsHostValue is not null)
        {
            PhysicsHostValue.Dispose();
            PhysicsHostValue = null;
        }

        if (AudioHostValue is not null)
        {
            AudioHostValue.Dispose();
            AudioHostValue = null;
        }

        AudioClientValue = null;

        if (InputHostValue is not null)
        {
            InputHostValue.Dispose();
            InputHostValue = null;
        }

        InputClientValue = null;
        UpdateTimeManager = null;

        if (VerificationAppContext is not null)
        {
            VerificationAppContext.Unload();
            VerificationAppContext = null;

            // Match TransportX.Player/Game.cs workaround for collectible AssemblyLoadContext reclamation
            if (!System.Diagnostics.Debugger.IsAttached)
            {
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }
    }

    private sealed class VerificationWorldInfo : IWorldInfo
    {
        public string InfoPath { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string? Identifier { get; init; }
        public string AppPath { get; init; } = string.Empty;
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    }
}
