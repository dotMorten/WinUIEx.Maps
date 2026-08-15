using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WinUIEx.Maps.Rendering.Diagnostics;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Owns the raster, icon, and vector shaders, geometry buffers, pipeline states, native
/// pointers, device epochs, and texture wrapper used by all <see cref="MapRenderer"/> draw
/// paths.
/// </summary>
/// <remarks>
/// <para>
/// The base renderer invokes this partial definition under the render lock when a D3D device
/// is created or released. Resource creation advances the device epoch, builds the raster,
/// icon, and vector pipelines, and requeues retained icon pixels. Raster tiles are
/// reacquired by the manager after the invalidation callback rather than copied across
/// devices.
/// </para>
/// <para>
/// Resource release advances the epoch again before draining queues. Upload completions from
/// the previous device are rejected, semaphore capacity is returned, texture ownership is
/// resolved, and COM wrappers and raw interface pointers are released in reverse dependency
/// order. <see cref="TileTexture"/> is the single cache-owned lifetime wrapper for a texture,
/// shader-resource view, native view pointer, byte size, and cache timestamps.
/// </para>
/// </remarks>
internal sealed partial class MapRenderer : DirectXRenderer
{
    private IntPtr _vertexShaderPointer;
    private IntPtr _iconVertexShaderPointer;
    private IntPtr _geometryVertexShaderPointer;
    private IntPtr _pixelShaderPointer;
    private IntPtr _iconPixelShaderPointer;
    private IntPtr _glyphPixelShaderPointer;
    private IntPtr _geometryPixelShaderPointer;
    private IntPtr _inputLayoutPointer;
    private IntPtr _iconInputLayoutPointer;
    private IntPtr _geometryInputLayoutPointer;
    private IntPtr _vertexBufferPointer;
    private IntPtr _iconInstanceBufferPointer;
    private IntPtr _geometryVertexBufferPointer;
    private IntPtr _patternVertexBufferPointer;
    private IntPtr _indexBufferPointer;
    private IntPtr _constantBufferPointer;
    private IntPtr _samplerPointer;
    private IntPtr _patternSamplerPointer;
    private IntPtr _rasterizerPointer;
    private IntPtr _blendStatePointer;
    private IntPtr _premultipliedBlendStatePointer;

    /// <summary>
    /// Creates all renderer-owned shaders, geometry, buffers, and pipeline states for a new
    /// device epoch, then requeues retained icon pixels for upload.
    /// </summary>
    protected override void CreateRendererResources()
    {
        Interlocked.Increment(ref _deviceEpoch);
        CreateShaders();
        CreateGeometry();
        CreateConstantBuffer();
        CreateStates();
        lock (_iconSync)
        {
            foreach (MapIconPixelData data in _iconPixels.Values)
            {
                _iconPixelUploads.Enqueue(data);
            }
        }
        _uploadRequested.Set();
    }

    /// <summary>
    /// Determines whether every native pointer required by raster and icon draw paths is
    /// available.
    /// </summary>
    protected override bool HasRendererResources() =>
        _vertexShaderPointer != IntPtr.Zero &&
        _iconVertexShaderPointer != IntPtr.Zero &&
        _geometryVertexShaderPointer != IntPtr.Zero &&
        _pixelShaderPointer != IntPtr.Zero &&
        _iconPixelShaderPointer != IntPtr.Zero &&
        _glyphPixelShaderPointer != IntPtr.Zero &&
        _geometryPixelShaderPointer != IntPtr.Zero &&
        _inputLayoutPointer != IntPtr.Zero &&
        _iconInputLayoutPointer != IntPtr.Zero &&
        _geometryInputLayoutPointer != IntPtr.Zero &&
        _vertexBufferPointer != IntPtr.Zero &&
        _iconInstanceBufferPointer != IntPtr.Zero &&
        _geometryVertexBufferPointer != IntPtr.Zero &&
        _patternVertexBufferPointer != IntPtr.Zero &&
        _indexBufferPointer != IntPtr.Zero &&
        _constantBufferPointer != IntPtr.Zero &&
        _samplerPointer != IntPtr.Zero &&
        _patternSamplerPointer != IntPtr.Zero &&
        _rasterizerPointer != IntPtr.Zero &&
        _blendStatePointer != IntPtr.Zero &&
        _premultipliedBlendStatePointer != IntPtr.Zero;

    /// <summary>
    /// Advances the device epoch, drains pending uploads, releases textures, and tears down
    /// all renderer-specific COM resources in dependency order.
    /// </summary>
    /// <remarks>
    /// Called under the render lock. Queue capacity is returned for every abandoned raster
    /// upload, and stale completions surrender texture ownership here.
    /// </remarks>
    protected override void ReleaseRendererResources()
    {
        MapControlEventSource.Log.DeviceResourcesReleased(
            GetType().Name,
            _rasterTiles.Count,
            _iconTextures.Count);
        Interlocked.Increment(ref _deviceEpoch);
        ReleaseRasterTileTextures();
        ReleaseVectorTiles();
        _lastRequiredTiles.Clear();
        _pendingRasterTiles.Clear();
        while (_rasterPixelUploads.TryDequeue(out _))
        {
            _pendingRasterUploadCapacity.Release();
        }
        while (_completedRasterUploads.TryDequeue(out CompletedRasterTileUpload completed))
        {
            _pendingRasterUploadCapacity.Release();
            completed.Texture.Dispose();
        }
        while (_completedIconUploads.TryDequeue(out CompletedIconUpload completed))
        {
            completed.Texture.Dispose();
        }
        foreach (TileTexture texture in _iconTextures.Values)
        {
            texture.Dispose();
        }
        _iconTextures.Clear();
        while (_textureDisposals.TryDequeue(out TileTexture? texture))
        {
            texture.Dispose();
        }

        ReleasePointer(ref _blendStatePointer);
        ReleasePointer(ref _premultipliedBlendStatePointer);
        ReleasePointer(ref _rasterizerPointer);
        ReleasePointer(ref _patternSamplerPointer);
        ReleasePointer(ref _samplerPointer);
        ReleasePointer(ref _constantBufferPointer);
        ReleasePointer(ref _indexBufferPointer);
        ReleasePointer(ref _vertexBufferPointer);
        ReleasePointer(ref _iconInstanceBufferPointer);
        ReleasePointer(ref _geometryVertexBufferPointer);
        ReleasePointer(ref _patternVertexBufferPointer);
        ReleasePointer(ref _inputLayoutPointer);
        ReleasePointer(ref _iconInputLayoutPointer);
        ReleasePointer(ref _geometryInputLayoutPointer);
        ReleasePointer(ref _pixelShaderPointer);
        ReleasePointer(ref _iconPixelShaderPointer);
        ReleasePointer(ref _glyphPixelShaderPointer);
        ReleasePointer(ref _geometryPixelShaderPointer);
        ReleasePointer(ref _vertexShaderPointer);
        ReleasePointer(ref _iconVertexShaderPointer);
        ReleasePointer(ref _geometryVertexShaderPointer);
    }
    /// <summary>
    /// Compiles raster and icon shaders, creates their device objects and input layouts, and
    /// captures the native pointers used by render-thread commands.
    /// </summary>
    private unsafe void CreateShaders()
    {
        IntPtr vertexBlob = CompileShader(MapShaders.Vertex, "main", "vs_4_0");
        IntPtr iconVertexBlob = CompileShader(MapShaders.IconVertex, "main", "vs_4_0");
        IntPtr geometryVertexBlob = CompileShader(
            MapShaders.GeometryVertex,
            "main",
            "vs_4_0");
        IntPtr pixelBlob = CompileShader(MapShaders.Pixel, "main", "ps_4_0");
        IntPtr iconPixelBlob = CompileShader(MapShaders.IconPixel, "main", "ps_4_0");
        IntPtr glyphPixelBlob = CompileShader(MapShaders.GlyphPixel, "main", "ps_4_0");
        IntPtr geometryPixelBlob = CompileShader(
            MapShaders.GeometryPixel,
            "main",
            "ps_4_0");
        try
        {
            _vertexShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(vertexBlob),
                GetBlobBufferSize(vertexBlob),
                12,
                "Failed to create the map vertex shader.");
            _iconVertexShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(iconVertexBlob),
                GetBlobBufferSize(iconVertexBlob),
                12,
                "Failed to create the MapIcon vertex shader.");
            _geometryVertexShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(geometryVertexBlob),
                GetBlobBufferSize(geometryVertexBlob),
                12,
                "Failed to create the map geometry vertex shader.");
            _pixelShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(pixelBlob),
                GetBlobBufferSize(pixelBlob),
                15,
                "Failed to create the map pixel shader.");
            _iconPixelShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(iconPixelBlob),
                GetBlobBufferSize(iconPixelBlob),
                15,
                "Failed to create the MapIcon pixel shader.");
            _glyphPixelShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(glyphPixelBlob),
                GetBlobBufferSize(glyphPixelBlob),
                15,
                "Failed to create the vector glyph pixel shader.");
            _geometryPixelShaderPointer = CreateShader(
                DevicePointer,
                (void*)GetBlobBufferPointer(geometryPixelBlob),
                GetBlobBufferSize(geometryPixelBlob),
                15,
                "Failed to create the map geometry pixel shader.");

            byte[] position = Encoding.ASCII.GetBytes("POSITION\0");
            byte[] texture = Encoding.ASCII.GetBytes("TEXCOORD\0");
            byte[] transform = Encoding.ASCII.GetBytes("TRANSFORM\0");
            byte[] offset = Encoding.ASCII.GetBytes("OFFSET\0");
            fixed (byte* positionPointer = position)
            fixed (byte* texturePointer = texture)
            fixed (byte* transformPointer = transform)
            fixed (byte* offsetPointer = offset)
            {
                D3D11_INPUT_ELEMENT_DESC[] elements =
                [
                    new()
                    {
                        SemanticName = (Windows.Win32.Foundation.PCSTR)positionPointer,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
                    },
                    new()
                    {
                        SemanticName = (Windows.Win32.Foundation.PCSTR)texturePointer,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                        AlignedByteOffset = 8,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
                    },
                ];
                fixed (D3D11_INPUT_ELEMENT_DESC* elementPointer = elements)
                {
                    _inputLayoutPointer = CreateInputLayout(
                        DevicePointer,
                        elementPointer,
                        (uint)elements.Length,
                        (void*)GetBlobBufferPointer(vertexBlob),
                        GetBlobBufferSize(vertexBlob));
                }

                D3D11_INPUT_ELEMENT_DESC[] iconElements =
                [
                    elements[0],
                    elements[1],
                    new()
                    {
                        SemanticName = (Windows.Win32.Foundation.PCSTR)transformPointer,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                        InputSlot = 1,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_INSTANCE_DATA,
                        InstanceDataStepRate = 1,
                    },
                    new()
                    {
                        SemanticName = (Windows.Win32.Foundation.PCSTR)offsetPointer,
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                        InputSlot = 1,
                        AlignedByteOffset = 16,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_INSTANCE_DATA,
                        InstanceDataStepRate = 1,
                    },
                ];
                fixed (D3D11_INPUT_ELEMENT_DESC* iconElementPointer = iconElements)
                {
                    _iconInputLayoutPointer = CreateInputLayout(
                        DevicePointer,
                        iconElementPointer,
                        (uint)iconElements.Length,
                        (void*)GetBlobBufferPointer(iconVertexBlob),
                        GetBlobBufferSize(iconVertexBlob));
                }

                D3D11_INPUT_ELEMENT_DESC geometryElement = elements[0];
                _geometryInputLayoutPointer = CreateInputLayout(
                    DevicePointer,
                    &geometryElement,
                    1,
                    (void*)GetBlobBufferPointer(geometryVertexBlob),
                    GetBlobBufferSize(geometryVertexBlob));
            }
        }
        finally
        {
            ReleasePointer(ref geometryPixelBlob);
            ReleasePointer(ref glyphPixelBlob);
            ReleasePointer(ref iconPixelBlob);
            ReleasePointer(ref pixelBlob);
            ReleasePointer(ref geometryVertexBlob);
            ReleasePointer(ref iconVertexBlob);
            ReleasePointer(ref vertexBlob);
        }
    }

    /// <summary>
    /// Creates and initializes the shared quad vertex/index buffers and dynamic icon-instance
    /// buffer.
    /// </summary>
    private unsafe void CreateGeometry()
    {
        TileVertex[] vertices =
        [
            new(new(0, 0), new(0, 0)),
            new(new(1, 0), new(1, 0)),
            new(new(1, 1), new(1, 1)),
            new(new(0, 1), new(0, 1)),
        ];
        ushort[] indices = [0, 1, 2, 0, 2, 3];

        D3D11_BUFFER_DESC vertexDescription = new()
        {
            ByteWidth = (uint)(vertices.Length * Marshal.SizeOf<TileVertex>()),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
        };
        D3D11_BUFFER_DESC indexDescription = new()
        {
            ByteWidth = (uint)(indices.Length * sizeof(ushort)),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER,
        };
        D3D11_BUFFER_DESC iconInstanceDescription = new()
        {
            ByteWidth = (uint)(IconInstanceCapacity * Marshal.SizeOf<IconInstance>()),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        D3D11_BUFFER_DESC geometryVertexDescription = new()
        {
            ByteWidth = (uint)(GeometryVertexCapacity * Marshal.SizeOf<GeometryVertex>()),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        D3D11_BUFFER_DESC patternVertexDescription =
            geometryVertexDescription with
            {
                ByteWidth = (uint)(
                    GeometryVertexCapacity * Marshal.SizeOf<TileVertex>()),
            };

        _vertexBufferPointer = CreateBuffer(
            DevicePointer,
            &vertexDescription,
            "Failed to create the map vertex buffer.");
        _indexBufferPointer = CreateBuffer(
            DevicePointer,
            &indexDescription,
            "Failed to create the map index buffer.");
        _iconInstanceBufferPointer = CreateBuffer(
            DevicePointer,
            &iconInstanceDescription,
            "Failed to create the MapIcon instance buffer.");
        _geometryVertexBufferPointer = CreateBuffer(
            DevicePointer,
            &geometryVertexDescription,
            "Failed to create the map geometry vertex buffer.");
        _patternVertexBufferPointer = CreateBuffer(
            DevicePointer,
            &patternVertexDescription,
            "Failed to create the map pattern vertex buffer.");
        fixed (TileVertex* verticesPointer = vertices)
        fixed (ushort* indicesPointer = indices)
        {
            UpdateSubresource(ContextPointer, _vertexBufferPointer, verticesPointer);
            UpdateSubresource(ContextPointer, _indexBufferPointer, indicesPointer);
        }
    }

    /// <summary>
    /// Creates the shader constant buffer shared by raster and icon draw paths.
    /// </summary>
    private unsafe void CreateConstantBuffer()
    {
        D3D11_BUFFER_DESC description = new()
        {
            ByteWidth = (uint)Marshal.SizeOf<TileConstants>(),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        };
        _constantBufferPointer = CreateBuffer(
            DevicePointer,
            &description,
            "Failed to create the map constant buffer.");
    }

    /// <summary>
    /// Creates sampler, rasterizer, straight-alpha blend, and premultiplied-alpha blend
    /// states.
    /// </summary>
    private unsafe void CreateStates()
    {
        D3D11_SAMPLER_DESC samplerDescription = new()
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MaxLOD = float.MaxValue,
        };
        D3D11_RASTERIZER_DESC rasterizerDescription = new()
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = true,
        };
        BlendDescription blendDescription = BlendDescription.CreateSourceAlpha();
        BlendDescription premultipliedBlendDescription =
            BlendDescription.CreatePremultipliedAlpha();

        _samplerPointer = CreateState(
            DevicePointer,
            &samplerDescription,
            23,
            "Failed to create the map sampler.");
        samplerDescription.AddressU =
            D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP;
        samplerDescription.AddressV =
            D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP;
        _patternSamplerPointer = CreateState(
            DevicePointer,
            &samplerDescription,
            23,
            "Failed to create the map pattern sampler.");
        _rasterizerPointer = CreateState(
            DevicePointer,
            &rasterizerDescription,
            22,
            "Failed to create the map rasterizer.");
        _blendStatePointer = CreateState(
            DevicePointer,
            &blendDescription,
            20,
            "Failed to create the map blend state.");
        _premultipliedBlendStatePointer = CreateState(
            DevicePointer,
            &premultipliedBlendDescription,
            20,
            "Failed to create the MapIcon blend state.");
    }
    /// <summary>
    /// Defines one unit-quad vertex and its texture coordinate in the shared geometry buffer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TileVertex(Vector2 Position, Vector2 TextureCoordinate);

    /// <summary>
    /// Mirrors the shader constant-buffer layout for quad transform and opacity values.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TileConstants(
        Vector4 Transform,
        Vector4 Rotation,
        Vector4 Pitch,
        Vector4 Opacity);

    /// <summary>
    /// Mirrors one native D3D11 render-target blend descriptor with explicit padding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderTargetBlendDescription
    {
        public int BlendEnable;
        public D3D11_BLEND SourceBlend;
        public D3D11_BLEND DestinationBlend;
        public D3D11_BLEND_OP BlendOperation;
        public D3D11_BLEND SourceBlendAlpha;
        public D3D11_BLEND DestinationBlendAlpha;
        public D3D11_BLEND_OP BlendOperationAlpha;
        public byte RenderTargetWriteMask;
        private byte _padding1;
        private byte _padding2;
        private byte _padding3;
    }

    /// <summary>
    /// Mirrors the native D3D11 blend descriptor containing state for eight render targets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlendDescription
    {
        public int AlphaToCoverageEnable;
        public int IndependentBlendEnable;
        public RenderTargetBlendDescription Target0;
        public RenderTargetBlendDescription Target1;
        public RenderTargetBlendDescription Target2;
        public RenderTargetBlendDescription Target3;
        public RenderTargetBlendDescription Target4;
        public RenderTargetBlendDescription Target5;
        public RenderTargetBlendDescription Target6;
        public RenderTargetBlendDescription Target7;

        /// <summary>
        /// Creates blend state for straight-alpha raster tile pixels.
        /// </summary>
        public static BlendDescription CreateSourceAlpha() => new()
        {
            Target0 = new()
            {
                BlendEnable = 1,
                SourceBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
                DestinationBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOperation = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SourceBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
                DestinationBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOperationAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
            },
        };

        /// <summary>
        /// Creates blend state for premultiplied-alpha icon pixels.
        /// </summary>
        public static BlendDescription CreatePremultipliedAlpha() => new()
        {
            Target0 = new()
            {
                BlendEnable = 1,
                SourceBlend = D3D11_BLEND.D3D11_BLEND_ONE,
                DestinationBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOperation = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SourceBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
                DestinationBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOperationAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
            },
        };
    }

    /// <summary>
    /// Owns a GPU texture, its shader-resource view and native pointer, plus cache timing and
    /// byte-accounting metadata.
    /// </summary>
    /// <remarks>
    /// Instances move between the upload worker, render-thread completion queues, caches, and
    /// the deferred disposal queue, but exactly one stage owns an instance at a time.
    /// </remarks>
    private sealed class TileTexture : IDisposable
    {
        private IntPtr _texturePointer;
        private int _disposed;

        /// <summary>
        /// Takes ownership of a D3D texture and shader-resource view, captures its native view
        /// pointer, and initializes cache-age metadata.
        /// </summary>
        public TileTexture(
            IntPtr texturePointer,
            IntPtr viewPointer,
            uint width,
            uint height)
        {
            _texturePointer = texturePointer;
            ViewPointer = viewPointer;
            ReadyTimestamp = Stopwatch.GetTimestamp();
            LastUsedTimestamp = ReadyTimestamp;
            ByteSize = (ulong)width * height * 4;
            GC.AddMemoryPressure(checked((long)ByteSize));
        }

        ~TileTexture()
        {
            Dispose(disposing: false);
        }

        public long ReadyTimestamp { get; }
        public long LastUsedTimestamp { get; private set; }
        public ulong ByteSize { get; }
        public IntPtr ViewPointer;

        /// <summary>
        /// Refreshes the last-use timestamp used by raster cache eviction.
        /// </summary>
        public void MarkUsed()
        {
            LastUsedTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Releases the captured view pointer and both owned COM resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
        }

        private void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            ReleasePointer(ref ViewPointer);
            ReleasePointer(ref _texturePointer);
            GC.RemoveMemoryPressure(checked((long)ByteSize));
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
