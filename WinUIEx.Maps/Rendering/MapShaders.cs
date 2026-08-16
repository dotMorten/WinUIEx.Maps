using Windows.Win32.Graphics.Direct3D;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Contains the embedded HLSL programs used for transformed raster quads and instanced map
/// icons.
/// </summary>
/// <remarks>
/// Raster shaders consume per-tile transform and opacity constants with straight-alpha
/// textures. Icon shaders consume per-instance transforms and layer opacity with
/// premultiplied-alpha textures. Device-resource creation compiles these sources and owns the
/// resulting shader objects; <see cref="Validate"/> provides compilation-only verification.
/// </remarks>
internal static class MapShaders
{
    private const string TileConstants = """
cbuffer TileConstants : register(b0)
{
    float4 Transform;
    float4 Rotation;
    float4 Pitch;
    float4 Opacity;
};
""";

    internal const string Vertex = TileConstants + """

struct VertexInput
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PixelInput main(VertexInput input)
{
    PixelInput output;
    float2 position = float2(
        input.Position.x * Transform.x + Transform.z,
        input.Position.y * Transform.y + Transform.w);
    float2 rotated = float2(
        position.x * Rotation.x + position.y * Rotation.y,
        position.x * Rotation.z + position.y * Rotation.w);
    output.Position = float4(
        rotated.x * Pitch.z,
        rotated.y * Pitch.x * Pitch.z,
        0.0f,
        Pitch.z + rotated.y * Pitch.w * Pitch.y);
    output.TexCoord = input.TexCoord;
    return output;
}
""";

    internal const string Pixel = TileConstants + """

Texture2D TileTexture : register(t0);
SamplerState TileSampler : register(s0);

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float4 main(PixelInput input) : SV_TARGET
{
    float4 color = TileTexture.Sample(TileSampler, input.TexCoord);
    return float4(color.rgb, color.a * Opacity.x);
}
""";

    internal const string IconVertex = """
struct VertexInput
{
    float2 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
    float4 Transform : TRANSFORM;
    float4 Offset : OFFSET;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

PixelInput main(VertexInput input)
{
    PixelInput output;
    float2 local = input.Position - float2(0.5f, 0.5f);
    output.Position = float4(
        local.x * input.Transform.x +
            local.y * input.Transform.z + input.Offset.x,
        local.x * input.Transform.y +
            local.y * input.Transform.w + input.Offset.y,
        0.0f, 1.0f);
    output.TexCoord = input.TexCoord;
    return output;
}
""";

    internal const string IconPixel = TileConstants + """

Texture2D IconTexture : register(t0);
SamplerState IconSampler : register(s0);

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float4 main(PixelInput input) : SV_TARGET
{
    float4 sampled = IconTexture.Sample(IconSampler, input.TexCoord);
    float4 tinted = float4(
        Rotation.rgb * sampled.a,
        Rotation.a * sampled.a);
    float4 ordinary = sampled * Rotation.a;
    return lerp(ordinary, tinted, Opacity.y) * Opacity.x;
}
""";

    internal const string GlyphPixel = TileConstants + """

Texture2D GlyphTexture : register(t0);
SamplerState GlyphSampler : register(s0);

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

float4 main(PixelInput input) : SV_TARGET
{
    float distance = GlyphTexture.Sample(GlyphSampler, input.TexCoord).r;
    float smoothing = max(fwidth(distance), 1.0f / 64.0f);
    float fillCoverage = smoothstep(
        0.75f - smoothing,
        0.75f + smoothing,
        distance);
    float haloSmoothing = smoothing + Opacity.z;
    float haloCoverage = smoothstep(
        0.75f - Opacity.y - haloSmoothing,
        0.75f - Opacity.y + haloSmoothing,
        distance);
    float4 fill = Rotation * fillCoverage;
    float4 halo = Pitch * haloCoverage * (1.0f - fillCoverage);
    return (fill + halo) * Opacity.x;
}
""";

    private const string GeometryConstants = """
cbuffer GeometryConstants : register(b0)
{
    float4 Transform;
    float4 Color;
    float4 ProjectiveX;
    float4 ProjectiveY;
};
""";

    internal const string GeometryVertex = GeometryConstants + """

struct VertexInput
{
    float2 Position : POSITION;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
};

PixelInput main(VertexInput input)
{
    float2 centered = input.Position - Transform.zw;
    float denominator =
        ProjectiveX.w * centered.x +
        ProjectiveY.w * centered.y +
        1.0f;
    float2 projected = float2(
        dot(ProjectiveX.xyz, float3(centered, 1.0f)),
        dot(ProjectiveY.xyz, float3(centered, 1.0f))) / denominator;
    PixelInput output;
    output.Position = float4(
        projected.x * Transform.x,
        projected.y * Transform.y,
        0.0f,
        1.0f);
    return output;
}
""";

    internal const string GeometryPixel = GeometryConstants + """

struct PixelInput
{
    float4 Position : SV_POSITION;
};

float4 main(PixelInput input) : SV_TARGET
{
    return Color;
}
""";

    /// <summary>
    /// Compiles every embedded shader and releases the resulting blobs after verifying shader
    /// source validity.
    /// </summary>
    internal static void Validate()
    {
        IntPtr vertex = CompileShader(Vertex, "main", "vs_4_0");
        IntPtr iconVertex = CompileShader(IconVertex, "main", "vs_4_0");
        IntPtr geometryVertex = CompileShader(GeometryVertex, "main", "vs_4_0");
        IntPtr pixel = CompileShader(Pixel, "main", "ps_4_0");
        IntPtr iconPixel = CompileShader(IconPixel, "main", "ps_4_0");
        IntPtr glyphPixel = CompileShader(GlyphPixel, "main", "ps_4_0");
        IntPtr geometryPixel = CompileShader(GeometryPixel, "main", "ps_4_0");
        ReleasePointer(ref geometryPixel);
        ReleasePointer(ref glyphPixel);
        ReleasePointer(ref iconPixel);
        ReleasePointer(ref pixel);
        ReleasePointer(ref geometryVertex);
        ReleasePointer(ref iconVertex);
        ReleasePointer(ref vertex);
    }
}
