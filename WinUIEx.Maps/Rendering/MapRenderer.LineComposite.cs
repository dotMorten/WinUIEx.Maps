using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    private static readonly float[] TransparentLineCompositeColor = [0, 0, 0, 0];
    private IntPtr _lineCompositeTexture;
    private IntPtr _lineCompositeMultisampleTexture;
    private IntPtr _lineCompositeTarget;
    private IntPtr _lineCompositeView;
    private uint _lineCompositeWidth, _lineCompositeHeight;
    private int _lineCompositeSamples;
    private long _lineCompositeBytes;

    private unsafe bool DrawCompositedTrafficLines(
        IntPtr context, LayerRenderSnapshot[] plan, int start, out bool used)
    {
        LayerRenderSnapshot first = plan[start];
        bool fading = false;
        int sources = 0;
        used = false;
        try
        {
            // Flow and affected incident roads share one public layer and one opacity pass.
            for (int index = start; index < plan.Length; index++)
            {
                LayerRenderSnapshot layer = plan[index];
                if (layer.LayerIndex != first.LayerIndex || layer.LineCompositeOpacity >= 1)
                    break;
                if (!layer.IsVisible || layer.Opacity <= 0 ||
                    _displayZoom < layer.MinZoom || _displayZoom >= layer.MaxZoom ||
                    !_rasterLayers.TryGetValue(layer.RuntimeId, out RasterLayerState? state) ||
                    state.Scene is null)
                    continue;
                if (!used)
                {
                    EnsureLineComposite();
                    SetPixelShader(context, _iconPixelShaderPointer, IntPtr.Zero, _samplerPointer, _constantBufferPointer);
                    SetRenderTarget(context, _lineCompositeTarget);
                    used = true;
                    Clear(context, _lineCompositeTarget, TransparentLineCompositeColor);
                }
                // Traffic has no polygons; accept prepared geometry using the same
                // opaque line state rather than the public opacity used for its icons.
                LayerRenderSnapshot opaqueLines = layer with { Opacity = 1 };
                ApplyCompletedVectorGeometryPreparation(opaqueLines, state);
                fading |= DrawVectorLineLayer(context, opaqueLines);
                sources++;
            }
        }
        finally
        {
            if (used)
                SetRenderTarget(context, RenderTargetPointer);
        }
        if (!used)
            return false;
        if (_lineCompositeMultisampleTexture != IntPtr.Zero)
            ResolveColorTarget(context, _lineCompositeTexture, _lineCompositeMultisampleTexture);
        double opacity = first.Opacity * first.LineCompositeOpacity;
        TileConstants constants = CreateQuadConstants(0, 0, Viewport.Width, Viewport.Height, (float)opacity)
            with { TextureTransform = new(1, 1, 0, 0) };
        UpdateSubresource(context, _constantBufferPointer, &constants);
        SetBlendState(context, _premultipliedBlendStatePointer);
        SetInputLayout(context, _inputLayoutPointer);
        SetVertexBuffer(context, _vertexBufferPointer, (uint)Marshal.SizeOf<TileVertex>());
        SetIndexBuffer(context, _indexBufferPointer);
        SetVertexShader(context, _vertexShaderPointer, _constantBufferPointer);
        SetPixelShader(context, _iconPixelShaderPointer, _lineCompositeView, _samplerPointer, _constantBufferPointer);
        DrawIndexed(context);
        SetPixelShader(context, _iconPixelShaderPointer, IntPtr.Zero, _samplerPointer, _constantBufferPointer);
        MapControlEventSource.Log.VectorLineComposite(sources, (int)_lineCompositeWidth,
            (int)_lineCompositeHeight, _lineCompositeSamples, opacity, _lineCompositeBytes);
        return fading;
    }

    private unsafe void EnsureLineComposite()
    {
        uint width = SurfaceSize.PixelWidth, height = SurfaceSize.PixelHeight;
        if (_lineCompositeTarget != IntPtr.Zero && _lineCompositeWidth == width &&
            _lineCompositeHeight == height && _lineCompositeSamples == RenderSampleCount)
            return;
        ReleaseLineComposite();
        try
        {
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            _lineCompositeTexture = CreateTexture(DevicePointer, &description, null,
                "Failed to create the traffic line composite texture.");
            _lineCompositeView = CreateView(DevicePointer, _lineCompositeTexture, 7,
                "Failed to create the traffic line composite shader view.");
            if (RenderSampleCount > 1)
            {
                description.SampleDesc.Count = (uint)RenderSampleCount;
                description.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
                _lineCompositeMultisampleTexture = CreateTexture(DevicePointer, &description, null,
                    "Failed to create the traffic line multisample texture.");
            }
            _lineCompositeTarget = CreateView(DevicePointer,
                _lineCompositeMultisampleTexture != IntPtr.Zero ? _lineCompositeMultisampleTexture : _lineCompositeTexture,
                9, "Failed to create the traffic line composite target.");
            _lineCompositeWidth = width;
            _lineCompositeHeight = height;
            _lineCompositeSamples = RenderSampleCount;
            long bytes = checked((long)width * height * 4 * (RenderSampleCount > 1 ? RenderSampleCount + 1 : 1));
            GC.AddMemoryPressure(bytes);
            _lineCompositeBytes = bytes;
        }
        catch
        {
            ReleaseLineComposite();
            throw;
        }
    }

    private void ReleaseLineComposite()
    {
        ReleasePointer(ref _lineCompositeTarget);
        ReleasePointer(ref _lineCompositeView);
        ReleasePointer(ref _lineCompositeMultisampleTexture);
        ReleasePointer(ref _lineCompositeTexture);
        if (_lineCompositeBytes != 0)
            GC.RemoveMemoryPressure(_lineCompositeBytes);
        _lineCompositeBytes = 0;
        _lineCompositeWidth = _lineCompositeHeight = 0;
        _lineCompositeSamples = 0;
    }
}
