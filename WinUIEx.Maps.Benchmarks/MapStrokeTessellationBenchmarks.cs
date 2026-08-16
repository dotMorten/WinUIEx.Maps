using BenchmarkDotNet.Attributes;
using Windows.Devices.Geolocation;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
[BenchmarkCategory("CPU", "Rendering", "MapElements")]
public class MapStrokeTessellationBenchmarks
{
    private MapGeometryData _geometry = null!;
    private bool _closed;
    private readonly MapGeometryCamera _camera =
        new(0, 0, 8, 0, 1024, 768);

    [Params(
        MapStrokeShape.Rectangle,
        MapStrokeShape.Acute,
        MapStrokeShape.Zigzag256)]
    public MapStrokeShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (Shape == MapStrokeShape.Rectangle)
        {
            _closed = true;
            var polygon = new MapPolygon
            {
                Path = CreatePath((-1, -1), (1, -1), (1, 1), (-1, 1)),
            };
            _geometry = polygon.GetState().Geometry;
            return;
        }

        _closed = false;
        BasicGeoposition[] positions = Shape == MapStrokeShape.Acute
            ?
            [
                Position(-1, 0),
                Position(0, 0),
                Position(-.95, .08),
            ]
            : Enumerable.Range(0, 256)
                .Select(index => Position(
                    -2 + (index * 4d / 255),
                    Math.Sin(index * Math.PI / 8) * .15))
                .ToArray();
        var polyline = new MapPolyline
        {
            Path = new Geopath(positions),
        };
        _geometry = polyline.GetState().Geometry;
    }

    [Benchmark(Baseline = true)]
    public object SegmentsOnly() =>
        Tessellate(MapStrokeJoinPolicy.SegmentsOnly);

    [Benchmark]
    public object Round() =>
        Tessellate(MapStrokeJoinPolicy.Round);

    private MapScreenPoint[] Tessellate(MapStrokeJoinPolicy joinPolicy) =>
        MapGeometryOperations.BuildStrokeTriangles(
            _geometry,
            _closed,
            thickness: 14,
            dashed: false,
            _camera,
            joinPolicy);

    private static Geopath CreatePath(
        params (double Longitude, double Latitude)[] points) =>
        new(points.Select(point => Position(point.Longitude, point.Latitude)));

    private static BasicGeoposition Position(double longitude, double latitude) =>
        new()
        {
            Longitude = longitude,
            Latitude = latitude,
        };
}

public enum MapStrokeShape
{
    Rectangle,
    Acute,
    Zigzag256,
}
