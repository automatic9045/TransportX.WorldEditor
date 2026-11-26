using System;
using System.Runtime.InteropServices;
using TransportX.Rendering.Backend;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TransportX.WorldEditor.Rendering.Viewport;

/// <summary>
/// Headless Direct3D 11 <see cref="IGraphicsClient"/> backed by an offscreen <see cref="ID3D11Texture2D"/>
/// render target, depth-stencil buffer, and CPU-readable staging texture for blitting frames into
/// Avalonia UI bitmaps without spawning a secondary Win32 <c>HWND</c> or swap chain.
/// </summary>
public sealed class OffscreenGraphicsClient : IGraphicsClient
{
    private ID3D11Texture2D? ColorTexture;
    private ID3D11Texture2D? DepthTexture;
    private ID3D11Texture2D? StagingTexture;
    private bool Disposed;

    public nint Hwnd => 0;
    public IDXGISwapChain1 SwapChain => null!;
    public RenderSurface? Surface { get; private set; }
    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    public void Resize(ID3D11Device device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);
        ObjectDisposedException.ThrowIf(Disposed, this);

        int targetW = int.Max(1, width);
        int targetH = int.Max(1, height);
        if (Surface.HasValue && PixelWidth == targetW && PixelHeight == targetH)
        {
            return;
        }

        ReleaseSurfaceResources();

        Texture2DDescription colorDesc = new()
        {
            Format = Format.R8G8B8A8_UNorm_SRgb,
            ArraySize = 1,
            MipLevels = 1,
            Width = (uint)targetW,
            Height = (uint)targetH,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        ColorTexture = device.CreateTexture2D(colorDesc);

        RenderTargetViewDescription rtvDesc = new()
        {
            Format = Format.R8G8B8A8_UNorm_SRgb,
            ViewDimension = RenderTargetViewDimension.Texture2D,
        };
        ID3D11RenderTargetView rtv = device.CreateRenderTargetView(ColorTexture, rtvDesc);

        Texture2DDescription depthDesc = new()
        {
            Format = Format.D32_Float_S8X24_UInt,
            ArraySize = 1,
            MipLevels = 1,
            Width = (uint)targetW,
            Height = (uint)targetH,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        DepthTexture = device.CreateTexture2D(depthDesc);

        DepthStencilViewDescription dsvDesc = new()
        {
            Format = depthDesc.Format,
            ViewDimension = DepthStencilViewDimension.Texture2D,
        };
        dsvDesc.Texture2D.MipSlice = 0;
        ID3D11DepthStencilView dsv = device.CreateDepthStencilView(DepthTexture, dsvDesc);

        Texture2DDescription stagingDesc = new()
        {
            Format = Format.R8G8B8A8_UNorm_SRgb,
            ArraySize = 1,
            MipLevels = 1,
            Width = (uint)targetW,
            Height = (uint)targetH,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        StagingTexture = device.CreateTexture2D(stagingDesc);

        Surface = new RenderSurface(rtv, dsv);
        PixelWidth = targetW;
        PixelHeight = targetH;
    }

    public bool CopyPixelsRgba(ID3D11DeviceContext context, byte[] destination, int destinationStride)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destination);

        if (Disposed || ColorTexture is null || StagingTexture is null || PixelWidth <= 0 || PixelHeight <= 0)
        {
            return false;
        }

        int rowBytes = PixelWidth * 4;
        if (destinationStride < rowBytes || destination.Length < destinationStride * PixelHeight)
        {
            return false;
        }

        context.CopyResource(StagingTexture, ColorTexture);
        MappedSubresource mapped = context.Map(StagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            nint srcPtr = mapped.DataPointer;
            int srcPitch = (int)mapped.RowPitch;
            for (int y = 0; y < PixelHeight; y++)
            {
                Marshal.Copy(srcPtr + y * srcPitch, destination, y * destinationStride, rowBytes);
            }
        }
        finally
        {
            context.Unmap(StagingTexture, 0);
        }

        return true;
    }

    private void ReleaseSurfaceResources()
    {
        if (Surface.HasValue)
        {
            Surface.Value.RenderTarget.Dispose();
            Surface.Value.DepthStencil.Dispose();
            Surface = null;
        }

        StagingTexture?.Dispose();
        StagingTexture = null;
        DepthTexture?.Dispose();
        DepthTexture = null;
        ColorTexture?.Dispose();
        ColorTexture = null;
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        ReleaseSurfaceResources();
    }
}
