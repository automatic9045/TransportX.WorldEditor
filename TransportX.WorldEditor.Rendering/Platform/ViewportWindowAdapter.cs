using System;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace TransportX.WorldEditor.Rendering.Platform;

/// <summary>
/// Minimal <see cref="INativeWindow"/> exposing an externally owned Win32 child <c>HWND</c>.
/// </summary>
public sealed class HwndNativeWindow : INativeWindow
{
    private readonly nint HwndValue;
    private readonly nint HInstanceValue;

    public HwndNativeWindow(nint hwnd, nint hInstance = 0)
    {
        HwndValue = hwnd;
        HInstanceValue = hInstance;
    }

    public NativeWindowFlags Kind => NativeWindowFlags.Win32;

    public (nint Display, nuint Window)? X11 => null;
    public nint? Cocoa => null;
    public (nint Display, nint Surface)? Wayland => null;
    public nint? WinRT => null;
    public (nint Window, uint Framebuffer, uint Colorbuffer, uint ResolveFramebuffer)? UIKit => null;
    public (nint Hwnd, nint HDC, nint HInstance)? Win32 => (HwndValue, 0, HInstanceValue);
    public (nint Display, nint Window)? Vivante => null;
    public (nint Window, nint Surface)? Android => null;
    public nint? Glfw => null;
    public nint? Sdl => null;
    public nint? DXHandle => null;
    public (nint? Display, nint? Surface)? EGL => null;
}

/// <summary>
/// Thin <see cref="IWindow"/> adapter backing TransportX's <c>Platform.Window</c>
/// with an Avalonia-hosted Win32 child <c>HWND</c> and physical pixel dimensions.
/// </summary>
public sealed class ViewportWindowAdapter : IWindow
{
    private readonly INativeWindow NativeWindow;
    private Vector2D<int> PixelSize;
    private bool Disposed;

    public ViewportWindowAdapter(nint hwnd, int pixelWidth, int pixelHeight, nint hInstance = 0)
    {
        NativeWindow = new HwndNativeWindow(hwnd, hInstance);
        PixelSize = new Vector2D<int>(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight));
    }

    public void UpdateSize(int pixelWidth, int pixelHeight)
    {
        int w = Math.Max(1, pixelWidth);
        int h = Math.Max(1, pixelHeight);
        if (w == PixelSize.X && h == PixelSize.Y)
        {
            return;
        }

        PixelSize = new Vector2D<int>(w, h);
        Resize?.Invoke(PixelSize);
        FramebufferResize?.Invoke(PixelSize);
    }

    public INativeWindow? Native => NativeWindow;
    public Vector2D<int> Size
    {
        get => PixelSize;
        set => UpdateSize(value.X, value.Y);
    }

    public Vector2D<int> FramebufferSize => PixelSize;
    public string Title { get; set; } = "TransportX.WorldEditor";
    public Vector2D<int> Position { get; set; }
    public bool IsClosing { get; set; }
    public double FramesPerSecond { get; set; } = 60.0;
    public double UpdatesPerSecond { get; set; } = 60.0;
    public GraphicsAPI API => GraphicsAPI.None;
    public bool VSync { get; set; }
    public VideoMode VideoMode => VideoMode.Default;
    public int? PreferredStencilBufferBits => null;
    public int? PreferredDepthBufferBits => null;
    public Vector4D<int>? PreferredBitDepth => null;
    public int? Samples => null;
    public IWindowHost? Parent => null;
    public IGLContext? SharedContext => null;
    public string? WindowClass => null;
    public Rectangle<int> BorderSize => new Rectangle<int>(0, 0, 0, 0);
    public bool IsInitialized => !Disposed;
    public bool ShouldSwapAutomatically { get; set; }
    public bool IsEventDriven { get; set; } = true;
    public bool IsContextControlDisabled { get; set; }
    public double Time { get; private set; }
    public WindowState WindowState { get; set; } = WindowState.Normal;
    public WindowBorder WindowBorder { get; set; } = WindowBorder.Hidden;
    public bool TransparentFramebuffer => false;
    public bool TopMost { get; set; }
    public IGLContext? GLContext => null;
    public IVkSurface? VkSurface => null;
    public IMonitor? Monitor { get; set; }
    public bool IsVisible { get; set; } = true;

    public nint Handle => NativeWindow.Win32?.Hwnd ?? 0;

    public object Invoke(Delegate d, params object[] args) => d.DynamicInvoke(args)!;
    public Vector2D<int> PointToClient(Vector2D<int> point) => point;
    public Vector2D<int> PointToScreen(Vector2D<int> point) => point;
    public Vector2D<int> PointToFramebuffer(Vector2D<int> point) => point;
    public IWindow CreateWindow(WindowOptions opts) => throw new NotSupportedException();
    public void Close() => IsClosing = true;
    public void Initialize() { }
    public void DoEvents() { }
    public void DoUpdate() { }
    public void DoRender() { }
    public void ContinueEvents() { }
    public void Reset() { }
    public void Run(Action onFrame) { }
    public void Focus() { }
    public void SetWindowIcon(ReadOnlySpan<Silk.NET.Core.RawImage> icons) { }

    public void StepTime(double deltaSeconds)
    {
        Time += deltaSeconds;
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            Disposed = true;
            Closing?.Invoke();
        }
    }

    public event Action<Vector2D<int>>? Move { add { } remove { } }
    public event Action<WindowState>? StateChanged { add { } remove { } }
    public event Action<string[]>? FileDrop { add { } remove { } }
    public event Action<Vector2D<int>>? Resize;
    public event Action<Vector2D<int>>? FramebufferResize;
    public event Action? Closing;
    public event Action<bool>? FocusChanged { add { } remove { } }
    public event Action? Load { add { } remove { } }
    public event Action<double>? Update { add { } remove { } }
    public event Action<double>? Render { add { } remove { } }
}
