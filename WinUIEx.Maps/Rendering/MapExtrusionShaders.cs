namespace WinUIEx.Maps.Rendering;

internal static class MapExtrusionShaders
{
    private const string Constants = """
cbuffer ExtrusionConstants : register(b0)
{
    float4 Origin;
    float4 Viewport;
    float4 Rotation;
    float4 Light;
    float4 LightColor;
    float4 Paint;
    float4 ClipBounds;
    float4 PatternTransform;
};
""";

    internal const string Vertex = Constants + """
struct VertexInput
{
    float3 Position : POSITION;
    float4 Normal : NORMAL;
    float4 Color : COLOR;
    float2 Texture : TEXCOORD;
};
struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Normal : NORMAL;
    float4 Color : COLOR;
    float2 Texture : TEXCOORD;
    float2 TilePosition : TEXCOORD1;
};
PixelInput main(VertexInput input)
{
    float3 position = input.Position * Origin.z;
    position.xy += Origin.xy;
    float x = position.x * Rotation.x + position.y * Rotation.y;
    float y = -position.x * Rotation.y + position.y * Rotation.x;
    float w = Viewport.z - y * Rotation.w - position.z * Rotation.z;
    PixelInput output;
    output.Position = float4(x * Viewport.z * Viewport.x,
        (y * Rotation.z - position.z * Rotation.w) * Viewport.z * Viewport.y, w - 1, w);
    output.Normal = input.Normal;
    output.Color = input.Color;
    output.Texture = input.Texture * PatternTransform.x + PatternTransform.yz * input.Normal.z;
    output.TilePosition = input.Position.xy;
    return output;
}
""";

    internal const string Pixel = Constants + """
Texture2D Pattern : register(t0);
SamplerState PatternSampler : register(s0);
struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Normal : NORMAL;
    float4 Color : COLOR;
    float2 Texture : TEXCOORD;
    float2 TilePosition : TEXCOORD1;
};
float4 main(PixelInput input) : SV_TARGET
{
    clip(input.TilePosition - ClipBounds.xy);
    clip(ClipBounds.zw - input.TilePosition);
    float4 color = input.Color;
    if (Paint.x > 0)
    {
        color = Pattern.Sample(PatternSampler, input.Texture);
        clip(color.a - 0.001);
    }
    float diffuse = max(0, dot(normalize(input.Normal.xyz), Light.xyz));
    float3 illumination = (1 - Light.w) + LightColor.rgb * (diffuse * Light.w);
    float gradient = Paint.y > 0 && input.Normal.z < 0.5 ? lerp(0.7, 1, input.Normal.w) : 1;
    return float4(color.rgb * illumination * gradient, color.a);
}
""";
}
