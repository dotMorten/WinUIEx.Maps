using System.Numerics;
using System.Runtime.InteropServices;
using WinUIEx.Maps.Rendering.Diagnostics;
using static WinUIEx.Maps.Rendering.DirectXInterop;

namespace WinUIEx.Maps.Rendering;

/// <summary>
/// Implements ordered polygon/polyline rendering and merges vector draws with icon draws at
/// their exact map-element collection positions.
/// </summary>
internal sealed partial class MapRenderer
{
    private const int GeometryVertexCapacity = 65_535;

    private unsafe void DrawMapElements(
        IntPtr context,
        int layerIndex,
        double layerOpacity)
    {
        GetVisibleMapElements(
            out MapIconSnapshot[] icons,
            out MapGeometrySnapshot[] geometries);
        int iconIndex = FindFirstLayerIcon(icons, layerIndex);
        int geometryIndex = FindFirstLayerGeometry(geometries, layerIndex);
        IconDrawResult iconResult = default;
        MapGeometryCamera camera = new(
            _displayLongitude,
            _displayLatitude,
            _displayZoom,
            _displayHeading,
            _viewportWidth,
            _viewportHeight,
            _displayPitch);

        while (IsLayerIcon(icons, iconIndex, layerIndex) ||
            IsLayerGeometry(geometries, geometryIndex, layerIndex))
        {
            int iconOrder = IsLayerIcon(icons, iconIndex, layerIndex)
                ? icons[iconIndex].OrderIndex
                : int.MaxValue;
            int geometryOrder = IsLayerGeometry(geometries, geometryIndex, layerIndex)
                ? geometries[geometryIndex].OrderIndex
                : int.MaxValue;
            if (iconOrder < geometryOrder)
            {
                long textureId = icons[iconIndex].TextureId;
                int runStart = iconIndex;
                do
                {
                    iconIndex++;
                }
                while (IsLayerIcon(icons, iconIndex, layerIndex) &&
                    icons[iconIndex].TextureId == textureId &&
                    icons[iconIndex].OrderIndex < geometryOrder);
                iconResult += DrawMapIconRun(
                    context,
                    icons,
                    runStart,
                    iconIndex - runStart,
                    layerOpacity);
            }
            else
            {
                DrawMapGeometry(
                    context,
                    geometries[geometryIndex],
                    camera,
                    layerOpacity);
                geometryIndex++;
            }
        }

        MapControlEventSource.Log.IconRenderBatch(
            iconResult.CandidateCount,
            iconResult.DrawableCount,
            iconResult.TextureBatchCount,
            iconResult.DrawCallCount);
    }

    private unsafe void DrawMapGeometry(
        IntPtr context,
        MapGeometrySnapshot snapshot,
        MapGeometryCamera camera,
        double layerOpacity)
    {
        if (snapshot.IsPolygon && snapshot.FillColor.A != 0)
        {
            DrawGeometryTriangles(
                context,
                MapGeometryOperations.BuildFillTriangles(snapshot.Geometry, camera),
                snapshot.FillColor.ToVector(layerOpacity));
        }
        if (snapshot.StrokeColor.A == 0 || snapshot.StrokeThickness <= 0)
        {
            return;
        }

        MapScreenSegment[] segments = MapGeometryOperations.BuildStrokeSegments(
            snapshot.Geometry,
            snapshot.IsPolygon,
            snapshot.StrokeThickness,
            snapshot.StrokeDashed,
            camera);
        DrawGeometryTriangles(
            context,
            MapGeometryOperations.ExpandStrokeTriangles(
                segments,
                snapshot.StrokeThickness),
            snapshot.StrokeColor.ToVector(layerOpacity));
    }

    private unsafe void DrawGeometryTriangles(
        IntPtr context,
        MapScreenPoint[] points,
        Vector4 color)
    {
        if (points.Length < 3 || color.W <= 0)
        {
            return;
        }

        GeometryVertex[] vertices = new GeometryVertex[points.Length];
        for (int index = 0; index < points.Length; index++)
        {
            vertices[index] = new GeometryVertex(
                new Vector2((float)points[index].X, (float)points[index].Y));
        }

        GeometryConstants constants = new(
            new Vector4(
                (float)(2 / Viewport.Width),
                (float)(-2 / Viewport.Height),
                -1,
                1),
            color,
            Vector4.Zero,
            Vector4.Zero);
        UpdateSubresource(context, _constantBufferPointer, &constants);
        SetBlendState(context, _blendStatePointer);
        SetInputLayout(context, _geometryInputLayoutPointer);
        SetVertexBuffer(
            context,
            _geometryVertexBufferPointer,
            (uint)Marshal.SizeOf<GeometryVertex>());
        SetVertexShader(context, _geometryVertexShaderPointer, _constantBufferPointer);
        SetPixelShader(context, _geometryPixelShaderPointer, _constantBufferPointer);

        int startIndex = 0;
        while (startIndex < vertices.Length)
        {
            int count = Math.Min(
                GeometryVertexCapacity,
                vertices.Length - startIndex);
            count -= count % 3;
            if (count == 0)
            {
                break;
            }

            fixed (GeometryVertex* vertexPointer = &vertices[startIndex])
            {
                WriteDiscardBuffer(
                    context,
                    _geometryVertexBufferPointer,
                    vertexPointer,
                    (nuint)(count * Marshal.SizeOf<GeometryVertex>()));
            }
            DrawVertices(context, (uint)count);
            startIndex += count;
        }
    }

    private static int FindFirstLayerIcon(MapIconSnapshot[] icons, int layerIndex)
    {
        int index = 0;
        while (index < icons.Length && icons[index].LayerIndex < layerIndex)
        {
            index++;
        }
        return index;
    }

    private static int FindFirstLayerGeometry(
        MapGeometrySnapshot[] geometries,
        int layerIndex)
    {
        int index = 0;
        while (index < geometries.Length && geometries[index].LayerIndex < layerIndex)
        {
            index++;
        }
        return index;
    }

    private static bool IsLayerIcon(
        MapIconSnapshot[] icons,
        int index,
        int layerIndex) =>
        (uint)index < (uint)icons.Length && icons[index].LayerIndex == layerIndex;

    private static bool IsLayerGeometry(
        MapGeometrySnapshot[] geometries,
        int index,
        int layerIndex) =>
        (uint)index < (uint)geometries.Length &&
        geometries[index].LayerIndex == layerIndex;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GeometryVertex(Vector2 Position);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GeometryConstants(
        Vector4 Transform,
        Vector4 Color,
        Vector4 Padding,
        Vector4 Padding2);
}
