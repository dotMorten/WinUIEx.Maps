using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

internal sealed partial class MapRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ExtrusionConstants(
        Vector4 Origin, Vector4 Viewport, Vector4 Rotation, Vector4 Light, Vector4 LightColor,
        Vector4 Paint, Vector4 ClipBounds, Vector4 PatternTransform);

    private sealed unsafe class ExtrusionResources : IDisposable
    {
        internal IntPtr VertexShader, PixelShader, Layout, Constants, DepthState, BlendState;
        internal IntPtr Color, MultisampleColor, Target, View, Depth, DepthView;
        internal uint Width, Height;
        internal int Samples;
        internal long ByteSize;

        internal ExtrusionResources(IntPtr device)
        {
            IntPtr vertex = IntPtr.Zero, pixel = IntPtr.Zero;
            try
            {
                vertex = CompileShader(MapExtrusionShaders.Vertex, "main", "vs_4_0");
                pixel = CompileShader(MapExtrusionShaders.Pixel, "main", "ps_4_0");
                VertexShader = CreateShader(device, (void*)GetBlobBufferPointer(vertex), GetBlobBufferSize(vertex), 12,
                    "Failed to create the extrusion vertex shader.");
                PixelShader = CreateShader(device, (void*)GetBlobBufferPointer(pixel), GetBlobBufferSize(pixel), 15,
                    "Failed to create the extrusion pixel shader.");
                byte[] names = Encoding.ASCII.GetBytes("POSITION\0NORMAL\0COLOR\0TEXCOORD\0");
                fixed (byte* text = names)
                {
                    D3D11_INPUT_ELEMENT_DESC* elements = stackalloc D3D11_INPUT_ELEMENT_DESC[4];
                    elements[0] = new() { SemanticName = (Windows.Win32.Foundation.PCSTR)text,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT };
                    elements[1] = new() { SemanticName = (Windows.Win32.Foundation.PCSTR)(text + 9),
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 12 };
                    elements[2] = new() { SemanticName = (Windows.Win32.Foundation.PCSTR)(text + 16),
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 28 };
                    elements[3] = new() { SemanticName = (Windows.Win32.Foundation.PCSTR)(text + 22),
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 44 };
                    Layout = CreateInputLayout(device, elements, 4, (void*)GetBlobBufferPointer(vertex), GetBlobBufferSize(vertex));
                }
                D3D11_BUFFER_DESC buffer = new()
                {
                    ByteWidth = (uint)sizeof(ExtrusionConstants), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                };
                Constants = CreateBuffer(device, &buffer, "Failed to create extrusion constants.");
                D3D11_DEPTH_STENCIL_DESC depth = new()
                {
                    DepthEnable = true,
                    DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL,
                    DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL,
                };
                DepthState = CreateState(device, &depth, 21, "Failed to create extrusion depth state.");
                BlendDescription blend = new()
                {
                    Target0 = new() { RenderTargetWriteMask = 15 },
                };
                BlendState = CreateState(device, &blend, 20, "Failed to create extrusion blend state.");
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                ReleasePointer(ref vertex);
                ReleasePointer(ref pixel);
            }
        }

        internal void EnsureSurface(IntPtr device, uint width, uint height, int samples)
        {
            if (Target != IntPtr.Zero && Width == width && Height == height && Samples == samples)
                return;
            ReleaseSurface();
            try
            {
                D3D11_TEXTURE2D_DESC description = new()
                {
                    Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleDesc = new() { Count = 1 }, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                };
                Color = CreateTexture(device, &description, null, "Failed to create the extrusion color target.");
                View = CreateView(device, Color, 7, "Failed to create the extrusion color view.");
                if (samples > 1)
                {
                    description.SampleDesc.Count = (uint)samples;
                    description.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
                    MultisampleColor = CreateTexture(device, &description, null, "Failed to create the extrusion multisample target.");
                }
                Target = CreateView(device, MultisampleColor != IntPtr.Zero ? MultisampleColor : Color,
                    9, "Failed to create the extrusion render target view.");
                description.SampleDesc.Count = (uint)samples;
                description.Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT;
                description.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL;
                Depth = CreateTexture(device, &description, null, "Failed to create the extrusion depth target.");
                DepthView = CreateView(device, Depth, 10, "Failed to create the extrusion depth view.");
                Width = width;
                Height = height;
                Samples = samples;
                ByteSize = checked((long)width * height * 4 * (samples > 1 ? samples * 2 + 1 : 2));
                GC.AddMemoryPressure(ByteSize);
            }
            catch
            {
                ReleaseSurface();
                throw;
            }
        }

        internal void ReleaseSurface()
        {
            ReleasePointer(ref DepthView);
            ReleasePointer(ref Depth);
            ReleasePointer(ref Target);
            ReleasePointer(ref View);
            ReleasePointer(ref MultisampleColor);
            ReleasePointer(ref Color);
            if (ByteSize != 0)
                GC.RemoveMemoryPressure(ByteSize);
            ByteSize = 0;
            Width = Height = 0;
            Samples = 0;
        }

        public void Dispose()
        {
            ReleaseSurface();
            ReleasePointer(ref Constants);
            ReleasePointer(ref BlendState);
            ReleasePointer(ref DepthState);
            ReleasePointer(ref Layout);
            ReleasePointer(ref PixelShader);
            ReleasePointer(ref VertexShader);
        }
    }
}
