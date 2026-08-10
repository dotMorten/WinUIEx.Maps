using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinUIEx.Maps.Rendering;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class DirectXInteropTests
{
    [TestMethod]
    [DataRow(32u, 32u, 4096, true)]
    [DataRow(256u, 256u, 262144, true)]
    [DataRow(256u, 256u, 4096, false)]
    [DataRow(0u, 256u, 0, false)]
    public void TextureUploadRequiresExactBgraBufferLength(
        uint width,
        uint height,
        int byteCount,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            MapRenderer.IsValidPixelBuffer(new byte[byteCount], width, height));
    }

    [TestMethod]
    public unsafe void D3D11SamplerStateCanBeCreated()
    {
        D3D_FEATURE_LEVEL[] levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
        CreateDevice(levels, out IntPtr devicePointer, out IntPtr contextPointer);

        IntPtr samplerPointer = IntPtr.Zero;
        try
        {
            D3D11_SAMPLER_DESC description = new()
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            samplerPointer = CreateState(
                devicePointer,
                &description,
                23,
                "Failed to create the test sampler state.");

            Assert.AreNotEqual(IntPtr.Zero, samplerPointer);
        }
        finally
        {
            ReleasePointer(ref samplerPointer);
            ReleasePointer(ref contextPointer);
            ReleasePointer(ref devicePointer);
        }
    }

    [TestMethod]
    public unsafe void D3D11TextureAndShaderResourceViewCanBeCreated()
    {
        D3D_FEATURE_LEVEL[] levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
        CreateDevice(levels, out IntPtr devicePointer, out IntPtr contextPointer);

        IntPtr texturePointer = IntPtr.Zero;
        IntPtr viewPointer = IntPtr.Zero;
        try
        {
            byte[] pixels = new byte[256 * 256 * 4];
            D3D11_TEXTURE2D_DESC description = new()
            {
                Width = 256,
                Height = 256,
                MipLevels = 1,
                ArraySize = 1,
                Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new() { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            fixed (byte* pixelPointer = pixels)
            {
                D3D11_SUBRESOURCE_DATA data = new()
                {
                    pSysMem = pixelPointer,
                    SysMemPitch = 256 * 4,
                };
                texturePointer = CreateTexture(devicePointer, &description, &data);
            }
            viewPointer = CreateView(
                devicePointer,
                texturePointer,
                7,
                "Failed to create the test shader resource view.");

            Assert.AreNotEqual(IntPtr.Zero, viewPointer);
        }
        finally
        {
            ReleasePointer(ref viewPointer);
            ReleasePointer(ref texturePointer);
            ReleasePointer(ref contextPointer);
            ReleasePointer(ref devicePointer);
        }
    }
}
