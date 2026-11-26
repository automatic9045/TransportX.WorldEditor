using System;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// Abstraction over the native Direct3D 11 viewport surface hosted inside the desktop GUI.
/// </summary>
public interface IViewportHost : IDisposable
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    bool IsInitialized { get; }
    ViewportOverlayFlags OverlayFlags { get; set; }

    event EventHandler? ViewportResized;

    void RequestRedraw();
}
