using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Provides the narrow COM and D3D11 interop surface used by the renderer to create
/// resources, bind pipeline state, issue draws, and present frames.
/// </summary>
/// <remarks>
/// <para>
/// The Windows projections used by this project expose some D3D11 operations only through
/// native vtables. This helper centralizes those calls and raw interface-pointer ownership so
/// the renderer does not depend on built-in COM interop.
/// </para>
/// <para>
/// Methods returning an <see cref="IntPtr"/> return an owned COM reference unless documented
/// otherwise. Owned pointers must be released through <see cref="ReleasePointer"/> in reverse
/// dependency order. Rendering code invokes context and swap-chain methods only while the
/// renderer's render lock protects the device lifecycle.
/// </para>
/// </remarks>
internal static class DirectXInterop
{
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int D3D11CreateDeviceNative(
        IntPtr adapter,
        D3D_DRIVER_TYPE driverType,
        IntPtr software,
        D3D11_CREATE_DEVICE_FLAG flags,
        D3D_FEATURE_LEVEL* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        IntPtr* device,
        D3D_FEATURE_LEVEL* selectedFeatureLevel,
        IntPtr* immediateContext);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int D3DCompileNative(
        void* sourceData,
        nuint sourceDataSize,
        byte* sourceName,
        void* defines,
        IntPtr include,
        byte* entryPoint,
        byte* target,
        uint flags1,
        uint flags2,
        IntPtr* code,
        IntPtr* errors);

    /// <summary>
    /// Releases an owned COM interface pointer and clears the storage to prevent reuse.
    /// </summary>
    internal static void ReleasePointer(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Converts a failing HRESULT into an <see cref="InvalidOperationException"/> with the
    /// operation context and native error attached.
    /// </summary>
    internal static void ThrowIfFailed(Windows.Win32.Foundation.HRESULT result, string message)
    {
        if (result.Value < 0)
        {
            throw new InvalidOperationException(
                $"{message} HRESULT: 0x{result.Value:X8}.",
                Marshal.GetExceptionForHR(result.Value));
        }
    }

    /// <summary>
    /// Attaches a DXGI swap chain to a XAML swap-chain panel without invoking legacy
    /// runtime interface marshalling.
    /// </summary>
    internal static unsafe void SetSwapChain(object panel, IntPtr swapChain)
    {
        IntPtr inspectablePointer =
            WinRT.MarshalInspectable<object>.FromManaged(panel);
        IntPtr panelPointer = IntPtr.Zero;
        try
        {
            int result = Marshal.QueryInterface(
                inspectablePointer,
                in IidSwapChainPanelNative,
                out panelPointer);
            Marshal.ThrowExceptionForHR(result);
            IntPtr* vtable = *(IntPtr**)panelPointer;
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
            ThrowIfFailed(
                new(method(panelPointer, swapChain)),
                "Failed to associate the map swap chain with its panel.");
        }
        finally
        {
            ReleasePointer(ref panelPointer);
            WinRT.MarshalInspectable<object>.DisposeAbi(inspectablePointer);
        }
    }

    /// <summary>
    /// Creates the D3D11 device through the pointer-only native ABI.
    /// </summary>
    internal static unsafe void CreateDevice(
        ReadOnlySpan<D3D_FEATURE_LEVEL> featureLevels,
        out IntPtr devicePointer,
        out IntPtr contextPointer)
    {
        IntPtr createdDevicePointer = IntPtr.Zero;
        IntPtr createdContextPointer = IntPtr.Zero;
        fixed (D3D_FEATURE_LEVEL* featureLevelPointer = featureLevels)
        {
            int result = D3D11CreateDeviceNative(
                IntPtr.Zero,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                featureLevelPointer,
                (uint)featureLevels.Length,
                7,
                &createdDevicePointer,
                null,
                &createdContextPointer);
            if (result < 0)
            {
                ReleasePointer(ref createdContextPointer);
                ReleasePointer(ref createdDevicePointer);
                ThrowIfFailed(new(result), "Failed to create the D3D11 device.");
            }
        }
        devicePointer = createdDevicePointer;
        contextPointer = createdContextPointer;
    }

    /// <summary>
    /// Creates a composition swap chain without generated COM-interface marshalling.
    /// </summary>
    internal static unsafe IntPtr CreateSwapChainForComposition(
        IntPtr devicePointer,
        DXGI_SWAP_CHAIN_DESC1* description)
    {
        IntPtr dxgiDevicePointer = IntPtr.Zero;
        IntPtr adapterPointer = IntPtr.Zero;
        IntPtr factoryPointer = IntPtr.Zero;
        IntPtr swapChainPointer = IntPtr.Zero;
        try
        {
            int result = Marshal.QueryInterface(devicePointer, in IidDxgiDevice, out dxgiDevicePointer);
            Marshal.ThrowExceptionForHR(result);

            IntPtr* dxgiDeviceVtable = *(IntPtr**)dxgiDevicePointer;
            var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)dxgiDeviceVtable[7];
            ThrowIfFailed(new(getAdapter(dxgiDevicePointer, &adapterPointer)), "Failed to get the DXGI adapter.");

            IntPtr* adapterVtable = *(IntPtr**)adapterPointer;
            var getParent = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)adapterVtable[6];
            fixed (Guid* factoryId = &IidDxgiFactory2)
            {
                ThrowIfFailed(
                    new(getParent(adapterPointer, factoryId, &factoryPointer)),
                    "Failed to get the DXGI factory.");
            }

            IntPtr* factoryVtable = *(IntPtr**)factoryPointer;
            var createSwapChain = (delegate* unmanaged[Stdcall]<
                IntPtr,
                IntPtr,
                DXGI_SWAP_CHAIN_DESC1*,
                IntPtr,
                IntPtr*,
                int>)factoryVtable[24];
            ThrowIfFailed(
                new(createSwapChain(
                    factoryPointer,
                    devicePointer,
                    description,
                    IntPtr.Zero,
                    &swapChainPointer)),
                "Failed to create the map swap chain.");

            IntPtr resultPointer = swapChainPointer;
            swapChainPointer = IntPtr.Zero;
            return resultPointer;
        }
        finally
        {
            ReleasePointer(ref swapChainPointer);
            ReleasePointer(ref factoryPointer);
            ReleasePointer(ref adapterPointer);
            ReleasePointer(ref dxgiDevicePointer);
        }
    }

    /// <summary>
    /// Resizes a swap chain through its native vtable.
    /// </summary>
    internal static unsafe void ResizeBuffers(
        IntPtr swapChain,
        uint bufferCount,
        uint width,
        uint height,
        DXGI_FORMAT format)
    {
        IntPtr* vtable = *(IntPtr**)swapChain;
        var method = (delegate* unmanaged[Stdcall]<
            IntPtr,
            uint,
            uint,
            uint,
            DXGI_FORMAT,
            uint,
            int>)vtable[13];
        ThrowIfFailed(
            new(method(swapChain, bufferCount, width, height, format, 0)),
            "Failed to resize the map swap chain.");
    }

    /// <summary>
    /// Gets a swap-chain back buffer without generated object marshalling.
    /// </summary>
    internal static unsafe IntPtr GetBackBuffer(IntPtr swapChain)
    {
        IntPtr bufferPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)swapChain;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)vtable[9];
        fixed (Guid* textureId = &IidD3D11Texture2D)
        {
            ThrowIfFailed(
                new(method(swapChain, 0, textureId, &bufferPointer)),
                "Failed to get the map swap-chain buffer.");
        }

        IntPtr resultPointer = bufferPointer;
        bufferPointer = IntPtr.Zero;
        return resultPointer;
    }

    /// <summary>
    /// Creates a D3D11 buffer and returns its owned native pointer.
    /// </summary>
    internal static unsafe IntPtr CreateBuffer(
        IntPtr devicePointer,
        D3D11_BUFFER_DESC* description,
        string message)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)vtable[3];
        ThrowIfFailed(new(method(devicePointer, description, null, &resultPointer)), message);
        return resultPointer;
    }

    /// <summary>
    /// Creates a D3D11 texture through an existing device interface pointer.
    /// </summary>
    internal static unsafe IntPtr CreateTexture(
        IntPtr devicePointer,
        D3D11_TEXTURE2D_DESC* description,
        D3D11_SUBRESOURCE_DATA* data)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)vtable[5];
        ThrowIfFailed(new(method(devicePointer, description, data, &resultPointer)), "Failed to create a map tile texture.");
        return resultPointer;
    }

    /// <summary>
    /// Invokes the selected D3D11 device view factory and wraps its returned COM interface.
    /// </summary>
    internal static unsafe IntPtr CreateView(
        IntPtr devicePointer,
        IntPtr resourcePointer,
        int vtableIndex,
        string message)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)vtable[vtableIndex];
        ThrowIfFailed(new(method(devicePointer, resourcePointer, null, &resultPointer)), message);
        return resultPointer;
    }

    /// <summary>
    /// Creates the vertex input layout that interprets shader bytecode and element
    /// declarations.
    /// </summary>
    internal static unsafe IntPtr CreateInputLayout(
        IntPtr devicePointer,
        D3D11_INPUT_ELEMENT_DESC* elements,
        uint count,
        void* bytecode,
        nuint length)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, void*, nuint, IntPtr*, int>)vtable[11];
        ThrowIfFailed(new(method(devicePointer, elements, count, bytecode, length, &resultPointer)), "Failed to create the map input layout.");
        return resultPointer;
    }

    /// <summary>
    /// Creates a typed D3D11 shader object from compiled bytecode using the appropriate
    /// device vtable entry.
    /// </summary>
    internal static unsafe IntPtr CreateShader(
        IntPtr devicePointer,
        void* bytecode,
        nuint length,
        int vtableIndex,
        string message)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)vtable[vtableIndex];
        ThrowIfFailed(new(method(devicePointer, bytecode, length, IntPtr.Zero, &resultPointer)), message);
        return resultPointer;
    }

    /// <summary>
    /// Creates a typed immutable D3D11 pipeline-state object from its native description.
    /// </summary>
    internal static unsafe IntPtr CreateState(
        IntPtr devicePointer,
        void* description,
        int vtableIndex,
        string message)
    {
        IntPtr resultPointer = IntPtr.Zero;
        IntPtr* vtable = *(IntPtr**)devicePointer;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, void*, IntPtr*, int>)vtable[vtableIndex];
        ThrowIfFailed(new(method(devicePointer, description, &resultPointer)), message);
        return resultPointer;
    }

    /// <summary>
    /// Compiles HLSL source for an entry point and shader target, returning an owned compiled
    /// bytecode blob.
    /// </summary>
    internal static unsafe IntPtr CompileShader(string source, string entryPoint, string target)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] entryPointBytes = Encoding.ASCII.GetBytes(entryPoint + "\0");
        byte[] targetBytes = Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* sourcePointer = sourceBytes)
        fixed (byte* entryPointPointer = entryPointBytes)
        fixed (byte* targetPointer = targetBytes)
        {
            IntPtr shaderPointer = IntPtr.Zero;
            IntPtr errorPointer = IntPtr.Zero;
            int result = D3DCompileNative(
                sourcePointer,
                (nuint)sourceBytes.Length,
                null,
                null,
                IntPtr.Zero,
                entryPointPointer,
                targetPointer,
                0,
                0,
                &shaderPointer,
                &errorPointer);

            if (result < 0)
            {
                string detail = errorPointer == IntPtr.Zero
                    ? "Shader compilation failed."
                    : Marshal.PtrToStringAnsi(
                        GetBlobBufferPointer(errorPointer),
                        checked((int)GetBlobBufferSize(errorPointer)))
                        ?? "Shader compilation failed.";
                ReleasePointer(ref errorPointer);
                ReleasePointer(ref shaderPointer);
                throw new InvalidOperationException(detail);
            }

            ReleasePointer(ref errorPointer);
            IntPtr resultPointer = shaderPointer;
            shaderPointer = IntPtr.Zero;
            return resultPointer;
        }
    }

    internal static unsafe IntPtr GetBlobBufferPointer(IntPtr blob)
    {
        IntPtr* vtable = *(IntPtr**)blob;
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtable[3])(blob);
    }

    internal static unsafe nuint GetBlobBufferSize(IntPtr blob)
    {
        IntPtr* vtable = *(IntPtr**)blob;
        return ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtable[4])(blob);
    }

    /// <summary>
    /// Replaces the contents of a default-usage D3D11 resource from caller-owned memory.
    /// </summary>
    internal static unsafe void UpdateSubresource(IntPtr context, IntPtr resource, void* data, uint rowPitch = 0)
    {
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)vtable[48];
        method(context, resource, 0, null, data, rowPitch, 0);
    }

    /// <summary>
    /// Maps a dynamic buffer with write-discard semantics, copies the supplied bytes, and
    /// always unmaps the resource.
    /// </summary>
    internal static unsafe void WriteDiscardBuffer(
        IntPtr context,
        IntPtr resource,
        void* data,
        nuint byteCount)
    {
        D3D11_MAPPED_SUBRESOURCE mapped = default;
        IntPtr* vtable = *(IntPtr**)context;
        var map = (delegate* unmanaged[Stdcall]<
            IntPtr,
            IntPtr,
            uint,
            D3D11_MAP,
            uint,
            D3D11_MAPPED_SUBRESOURCE*,
            int>)vtable[14];
        ThrowIfFailed(
            new(map(
                context,
                resource,
                0,
                D3D11_MAP.D3D11_MAP_WRITE_DISCARD,
                0,
                &mapped)),
            "Failed to map the MapIcon instance buffer.");
        try
        {
            Buffer.MemoryCopy(data, mapped.pData, byteCount, byteCount);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtable[15])(
                context,
                resource,
                0);
        }
    }

    /// <summary>
    /// Clears a render-target view to the specified RGBA color.
    /// </summary>
    internal static unsafe void Clear(IntPtr context, IntPtr target, float[] color)
    {
        fixed (float* colorPointer = color)
        {
            IntPtr* vtable = *(IntPtr**)context;
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)vtable[50];
            method(context, target, colorPointer);
        }
    }

    /// <summary>
    /// Binds one render-target view and removes any depth-stencil target.
    /// </summary>
    internal static unsafe void SetRenderTarget(IntPtr context, IntPtr target)
    {
        IntPtr* targets = stackalloc IntPtr[1] { target };
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)vtable[33];
        method(context, 1, targets, IntPtr.Zero);
    }

    /// <summary>
    /// Unbinds all output-merger render targets before resize or teardown.
    /// </summary>
    internal static unsafe void UnsetRenderTarget(IntPtr context)
    {
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)vtable[33];
        method(context, 0, null, IntPtr.Zero);
    }

    /// <summary>
    /// Binds the viewport used to rasterize the current map surface.
    /// </summary>
    internal static unsafe void SetViewport(IntPtr context, D3D11_VIEWPORT viewport)
    {
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, D3D11_VIEWPORT*, void>)vtable[44];
        method(context, 1, &viewport);
    }

    /// <summary>
    /// Binds one vertex buffer at slot zero with a zero byte offset.
    /// </summary>
    internal static unsafe void SetVertexBuffer(IntPtr context, IntPtr buffer, uint stride)
    {
        uint offset = 0;
        IntPtr* buffers = stackalloc IntPtr[1] { buffer };
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)vtable[18];
        method(context, 0, 1, buffers, &stride, &offset);
    }

    /// <summary>
    /// Binds geometry and per-instance vertex streams at slots zero and one.
    /// </summary>
    internal static unsafe void SetVertexBuffers(
        IntPtr context,
        IntPtr vertexBuffer,
        uint vertexStride,
        IntPtr instanceBuffer,
        uint instanceStride)
    {
        IntPtr* buffers = stackalloc IntPtr[2] { vertexBuffer, instanceBuffer };
        uint* strides = stackalloc uint[2] { vertexStride, instanceStride };
        uint* offsets = stackalloc uint[2] { 0, 0 };
        IntPtr* vtable = *(IntPtr**)context;
        var method =
            (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)vtable[18];
        method(context, 0, 2, buffers, strides, offsets);
    }

    /// <summary>
    /// Binds the shared 16-bit index buffer used to draw map quads.
    /// </summary>
    internal static unsafe void SetIndexBuffer(IntPtr context, IntPtr buffer)
    {
        IntPtr* vtable = *(IntPtr**)context;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DXGI_FORMAT, uint, void>)vtable[19];
        method(context, buffer, DXGI_FORMAT.DXGI_FORMAT_R16_UINT, 0);
    }

    /// <summary>
    /// Binds an input layout and configures triangle-list primitive topology.
    /// </summary>
    internal static unsafe void SetInputLayout(IntPtr context, IntPtr layout)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)vtable[17])(context, layout);
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D_PRIMITIVE_TOPOLOGY, void>)vtable[24])(
            context,
            D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    }

    /// <summary>
    /// Binds a vertex shader and its slot-zero constant buffer.
    /// </summary>
    internal static unsafe void SetVertexShader(IntPtr context, IntPtr shader, IntPtr constants)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)vtable[11])(context, shader, null, 0);
        IntPtr* buffers = stackalloc IntPtr[1] { constants };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[7])(context, 0, 1, buffers);
    }

    /// <summary>
    /// Binds a vertex shader without changing its constant-buffer bindings.
    /// </summary>
    internal static unsafe void SetVertexShader(IntPtr context, IntPtr shader)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)vtable[11])(
            context,
            shader,
            null,
            0);
    }

    /// <summary>
    /// Binds a pixel shader together with its slot-zero texture, sampler, and constant
    /// buffer.
    /// </summary>
    internal static unsafe void SetPixelShader(
        IntPtr context,
        IntPtr shader,
        IntPtr resource,
        IntPtr sampler,
        IntPtr constants)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)vtable[9])(context, shader, null, 0);
        IntPtr* resources = stackalloc IntPtr[1] { resource };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[8])(context, 0, 1, resources);
        IntPtr* samplers = stackalloc IntPtr[1] { sampler };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[10])(context, 0, 1, samplers);
        IntPtr* buffers = stackalloc IntPtr[1] { constants };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[16])(context, 0, 1, buffers);
    }

    /// <summary>
    /// Binds a color-only pixel shader and its slot-zero constant buffer.
    /// </summary>
    internal static unsafe void SetPixelShader(
        IntPtr context,
        IntPtr shader,
        IntPtr constants)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)vtable[9])(
            context,
            shader,
            null,
            0);
        IntPtr* buffers = stackalloc IntPtr[1] { constants };
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[16])(
            context,
            0,
            1,
            buffers);
    }

    /// <summary>
    /// Binds the rasterizer state for subsequent map draws.
    /// </summary>
    internal static unsafe void SetRasterizer(IntPtr context, IntPtr rasterizer)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)vtable[43])(context, rasterizer);
    }

    /// <summary>
    /// Binds the output-merger blend state with the full sample mask.
    /// </summary>
    internal static unsafe void SetBlendState(IntPtr context, IntPtr blendState)
    {
        float* blendFactor = stackalloc float[4];
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, uint, void>)vtable[35])(
            context,
            blendState,
            blendFactor,
            uint.MaxValue);
    }

    /// <summary>
    /// Draws one six-index quad from the currently bound geometry.
    /// </summary>
    internal static unsafe void DrawIndexed(IntPtr context)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, void>)vtable[12])(context, 6, 0, 0);
    }

    /// <summary>
    /// Draws the shared six-index quad for each bound icon instance.
    /// </summary>
    internal static unsafe void DrawIndexedInstanced(IntPtr context, uint instanceCount)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int, uint, void>)vtable[20])(
            context,
            6,
            instanceCount,
            0,
            0,
            0);
    }

    /// <summary>
    /// Draws the requested number of vertices from the currently bound triangle-list buffer.
    /// </summary>
    internal static unsafe void DrawVertices(IntPtr context, uint vertexCount)
    {
        IntPtr* vtable = *(IntPtr**)context;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)vtable[13])(
            context,
            vertexCount,
            0);
    }

    /// <summary>
    /// Presents the composed map frame with vertical synchronization.
    /// </summary>
    internal static unsafe void Present(IntPtr swapChain)
    {
        IntPtr* vtable = *(IntPtr**)swapChain;
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)vtable[8];
        ThrowIfFailed(new(method(swapChain, 1, 0)), "Failed to present the map.");
    }
}
