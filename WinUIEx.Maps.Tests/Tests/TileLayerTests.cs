using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class TileLayerTests
{
    [TestMethod]
    public void PublicApiHasDocumentedDependencyPropertyShape()
    {
        Assert.IsTrue(typeof(MapLayer).IsAssignableFrom(typeof(TileLayer)));
        Assert.IsFalse(typeof(TileLayer).IsSealed);
        ConstructorInfo constructor = Assert.ContainsSingle(typeof(TileLayer).GetConstructors());
        ParameterInfo[] parameters = constructor.GetParameters();
        Assert.AreSequenceEqual([typeof(TileLayerOptions), typeof(string)], parameters.Select(p => p.ParameterType));
        foreach (var parameter in parameters)
        {
            Assert.IsTrue(parameter.IsOptional);
        }
        Assert.AreSequenceEqual(
            ["TileUrl", "Bounds", "IsTMS", "MaxSourceZoom", "MinSourceZoom",
             "Subdomains", "TileSize", "MinZoom", "MaxZoom", "FadeDuration"],
            typeof(TileLayer)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(property => property.CanWrite)
                .Select(property => property.Name));

        foreach (string name in new[]
        {
            nameof(MapLayer.IsVisible),
            nameof(MapLayer.Opacity),
        })
        {
            AssertDependencyProperty(typeof(MapLayer), name);
        }
        foreach (string name in new[]
        {
            nameof(TileLayer.TileUrl),
            nameof(TileLayer.Bounds),
            nameof(TileLayer.IsTMS),
            nameof(TileLayer.MaxSourceZoom),
            nameof(TileLayer.MinSourceZoom),
            nameof(TileLayer.Subdomains),
            nameof(TileLayer.TileSize),
            nameof(TileLayer.MinZoom),
            nameof(TileLayer.MaxZoom),
            nameof(TileLayer.FadeDuration),
        })
        {
            AssertDependencyProperty(typeof(TileLayer), name);
        }
        PropertyInfo id = typeof(TileLayer).GetProperty(nameof(TileLayer.Id))!;
        Assert.IsTrue(id.CanRead);
        Assert.IsFalse(id.CanWrite);
        MethodInfo createSnapshot = typeof(TileLayer).GetMethod(
            "CreateSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsTrue(createSnapshot.IsVirtual);
        Assert.IsFalse(createSnapshot.IsPublic);
        Assert.IsNull(typeof(TileLayer).Assembly.GetType("WinUIEx.Maps.TileSource"));
        Assert.IsTrue(typeof(TileLayer).IsAssignableFrom(typeof(AzureTileLayer)));
        Assert.IsTrue(typeof(AzureTileLayer).IsSealed);
        Assert.IsNull(typeof(TileLayer).Assembly.GetType(
            "WinUIEx.Maps.Rendering.TileManager"));
        Assert.IsNull(typeof(TileLayer).Assembly.GetType(
            "WinUIEx.Maps.Rendering.CustomRasterTileManager"));
        Assert.IsNull(typeof(TileLayer).Assembly.GetType(
            "WinUIEx.Maps.Rendering.AzureMapsTileSource"));
    }

    [TestMethod]
    public void OptionDefaultsMatchRasterContract()
    {
        TileLayerOptions options = new();

        Assert.AreEqual(string.Empty, options.TileUrl);
        Assert.AreEqual(TileLayerBounds.World, options.Bounds);
        Assert.IsFalse(options.IsTMS);
        Assert.AreEqual(22, options.MaxSourceZoom);
        Assert.AreEqual(0, options.MinSourceZoom);
        Assert.IsEmpty(options.Subdomains);
        Assert.AreEqual(512, options.TileSize);
        Assert.AreEqual(0, options.MinZoom);
        Assert.AreEqual(24, options.MaxZoom);
        Assert.Contains(nameof(MapStyle.Blank), Enum.GetNames<MapStyle>());
        Assert.AreSequenceEqual(
            [
                nameof(MapStyle.RoadRaster),
                nameof(MapStyle.GrayscaleDarkRaster),
                nameof(MapStyle.Satellite),
                nameof(MapStyle.RoadShadedReliefRaster),
                nameof(MapStyle.Blank),
                nameof(MapStyle.BlankAccessible),
                nameof(MapStyle.GrayscaleLight),
                nameof(MapStyle.Night),
                nameof(MapStyle.HighContrastDark),
                nameof(MapStyle.HighContrastLight),
                nameof(MapStyle.SatelliteWithRoads),
                nameof(MapStyle.Road),
                nameof(MapStyle.GrayscaleDark),
                nameof(MapStyle.RoadShadedRelief),
            ],
            Enum.GetNames<MapStyle>());
        Assert.AreEqual(
            0,
            (int)Enum.Parse<MapStyle>(nameof(MapStyle.RoadRaster)));
        Assert.AreEqual(
            1,
            (int)Enum.Parse<MapStyle>(nameof(MapStyle.GrayscaleDarkRaster)));
        Assert.AreEqual(
            3,
            (int)Enum.Parse<MapStyle>(nameof(MapStyle.RoadShadedReliefRaster)));
        Assert.AreEqual(4, (int)Enum.Parse<MapStyle>(nameof(MapStyle.Blank)));
        Assert.AreEqual(11, (int)Enum.Parse<MapStyle>(nameof(MapStyle.Road)));
        Assert.AreEqual(
            12,
            (int)Enum.Parse<MapStyle>(nameof(MapStyle.GrayscaleDark)));
        Assert.AreEqual(
            13,
            (int)Enum.Parse<MapStyle>(nameof(MapStyle.RoadShadedRelief)));
    }

    [TestMethod]
    public void BoundsRejectInvalidOrWrappedBoxes()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TileLayerBounds(-181, -10, 10, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TileLayerBounds(10, -10, -10, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TileLayerBounds(-10, double.NaN, 10, 10));
    }

    [TestMethod]
    public void UrlExpansionSupportsAliasesTmsQuadkeySubdomainAndBbox()
    {
        TileLayerSnapshot aliases = Snapshot(
            "https://tiles.example/[level]/[column]/[row].png",
            isTms: false);
        Assert.AreEqual(
            "https://tiles.example/3/2/5.png",
            CustomAcquisition(aliases).ExpandUrl(new TileId(3, 2, 5)));

        TileLayerSnapshot braces = Snapshot(
            "https://{subdomain}.example/{z}/{x}/{y}/{quadkey}?bbox={bbox-epsg-3857}",
            isTms: true,
            subdomains: ["a"]);
        string expanded = CustomAcquisition(braces).ExpandUrl(new TileId(3, 2, 5));
        Assert.StartsWith("https://a.example/3/2/2/212?bbox=", expanded);
        Assert.DoesNotContain("{", expanded);
    }

    [TestMethod]
    [DataRow(8.9, 256, 8)]
    [DataRow(8.9, 512, 7)]
    [DataRow(8.9, 128, 9)]
    [DataRow(8.9, 1, 11)]
    public void TileSizeSelectsLogicalSourceZoom(double zoom, int tileSize, int expected)
    {
        Assert.AreEqual(
            expected,
            CustomRasterTileAcquisitionSession.GetSourceZoom(zoom, tileSize));
    }

    [TestMethod]
    public void BoundsFilterUsesTileWorldCoverage()
    {
        TileLayerBounds northWest = new(-180, 0, 0, MapCamera.MaximumLatitude);

        Assert.IsTrue(CustomRasterTileAcquisitionSession.IntersectsBounds(
            new TileId(1, 0, 0),
            northWest));
        Assert.IsFalse(CustomRasterTileAcquisitionSession.IntersectsBounds(
            new TileId(1, 1, 1),
            northWest));
    }

    [TestMethod]
    public void QuadkeyUsesStandardDigitOrder()
    {
        Assert.AreEqual("1203", CustomRasterTileAcquisitionSession.GetQuadKey(4, 9, 5));
    }

    [TestMethod]
    [DataRow(0, 300, 0.5, 0)]
    [DataRow(150, 300, 0.5, 0.25)]
    [DataRow(300, 300, 0.5, 0.5)]
    [DataRow(0, 0, 0.5, 0.5)]
    [DataRow(600, 300, 1, 1)]
    public void FadeMultipliesLayerOpacity(
        double elapsedMilliseconds,
        double fadeMilliseconds,
        double opacity,
        double expected)
    {
        double actual = MapRenderer.ComputeLayerTileOpacity(
                TimeSpan.FromMilliseconds(elapsedMilliseconds),
                TimeSpan.FromMilliseconds(fadeMilliseconds),
                opacity);

        Assert.AreEqual(Math.Round(expected, 10), Math.Round(actual, 10));
    }

    [TestMethod]
    public void VisibilityOpacityAndZoomControlAcquisition()
    {
        TileLayerSnapshot visible = Snapshot("https://tiles.example/{z}/{x}/{y}", false);

        Assert.IsTrue(RasterTileManager.ShouldAcquire(visible, 12));
        Assert.IsFalse(RasterTileManager.ShouldAcquire(visible with { IsVisible = false }, 12));
        Assert.IsFalse(RasterTileManager.ShouldAcquire(visible with { Opacity = 0 }, 12));
        Assert.IsFalse(RasterTileManager.ShouldAcquire(visible with { MinZoom = 13 }, 12));
        Assert.IsFalse(RasterTileManager.ShouldAcquire(visible with { MaxZoom = 12 }, 12));
        Assert.IsNull(RasterTileManager.NormalizeSourceZoom(12, 13, 22));
    }

    [TestMethod]
    public void SourceKeyChangesOnlyForAcquisitionConfiguration()
    {
        TileLayerSnapshot original = Snapshot("https://tiles.example/{z}/{x}/{y}", false);

        Assert.AreEqual(original.SourceKey, (original with { Opacity = 0.25 }).SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            Snapshot("https://other.example/{z}/{x}/{y}", false).SourceKey);
        Assert.AreNotEqual(original.SourceKey, Snapshot(
            "https://tiles.example/{z}/{x}/{y}",
            true).SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            Snapshot("https://tiles.example/{z}/{x}/{y}", false, tileSize: 512).SourceKey);
    }

    [TestMethod]
    public void HeterogeneousRenderPlanPreservesFirstToLastOrder()
    {
        LayerRenderPlanBuilder builder = new();
        builder.Add(new(
            LayerRenderKind.RasterTiles, 0, 11, true, 1, TimeSpan.Zero, 0, 24, 0, 256));
        builder.Add(new(
            LayerRenderKind.MapElements, 1, 0, true, 0.75, TimeSpan.Zero, 0, 24, 0, 256));
        builder.Add(new(
            LayerRenderKind.RasterTiles, 2, 12, true, 0.5, TimeSpan.Zero, 0, 24, 0, 256));
        builder.Add(new(
            LayerRenderKind.MapElements, 3, 0, true, 1, TimeSpan.Zero, 0, 24, 0, 256));

        LayerRenderSnapshot[] plan = builder.Build();

        Assert.AreSequenceEqual(
            [LayerRenderKind.RasterTiles, LayerRenderKind.MapElements,
             LayerRenderKind.RasterTiles, LayerRenderKind.MapElements],
            plan.Select(item => item.Kind));
        Assert.AreSequenceEqual([0, 1, 2, 3], plan.Select(item => item.LayerIndex));
    }

    [TestMethod]
    public void HiddenAzureLayerIsPrependedWithoutChangingPublicLayerIdentityOrIndexes()
    {
        TileLayerSnapshot azure = new(
            100,
            0,
            new AzureTileAcquisitionSession(MapStyle.RoadRaster, "token"),
            0,
            24,
            true,
            1,
            TimeSpan.FromMilliseconds(250));
        TileLayerSnapshot raster = Snapshot(
            "https://tiles.example/{z}/{x}/{y}",
            false) with { RuntimeId = 200 };
        LayerRenderSnapshot[] publicPlan =
        [
            new(
                LayerRenderKind.RasterTiles,
                0,
                raster.RuntimeId,
                true,
                1,
                TimeSpan.FromMilliseconds(300),
                0,
                24,
                0,
                256),
            new(
                LayerRenderKind.MapElements,
                1,
                0,
                true,
                1,
                TimeSpan.Zero,
                0,
                24,
                0,
                256),
        ];

        LayerSnapshotPublication publication = LayerSnapshotPublication.PrependHiddenAzure(
            azure,
            publicPlan,
            [raster]);

        Assert.AreEqual(3, publication.RenderPlan.Length);
        Assert.AreEqual(azure.RuntimeId, publication.RenderPlan[0].RuntimeId);
        Assert.AreEqual(-1, publication.RenderPlan[0].LayerIndex);
        Assert.AreEqual(raster.RuntimeId, publication.RenderPlan[1].RuntimeId);
        Assert.AreEqual(0, publication.RenderPlan[1].LayerIndex);
        Assert.AreEqual(1, publication.RenderPlan[2].LayerIndex);
        Assert.AreSequenceEqual([azure.RuntimeId, raster.RuntimeId],
            publication.RasterLayers.Select(layer => layer.RuntimeId));
        Assert.AreEqual(publicPlan[0], publication.RenderPlan[1]);
    }

    [TestMethod]
    public void BlankPublicationAddsNoHiddenRasterState()
    {
        LayerRenderSnapshot[] publicPlan =
        [
            new(
                LayerRenderKind.MapElements,
                0,
                0,
                true,
                1,
                TimeSpan.Zero,
                0,
                24,
                0,
                256),
        ];
        TileLayerSnapshot[] publicRaster = [];

        LayerSnapshotPublication publication =
            LayerSnapshotPublication.PrependHiddenAzure(null, publicPlan, publicRaster);

        Assert.AreSame(publicPlan, publication.RenderPlan);
        Assert.AreSame(publicRaster, publication.RasterLayers);
        Assert.DoesNotContain(
            layer => layer.Acquisition.SourceKind == RasterSourceKind.Azure,
            publication.RasterLayers);
    }

    [TestMethod]
    public void SnapshotOwnsImmutableAcquisitionStateAfterUiLayerChanges()
    {
        string[] mutableSubdomains = ["a"];
        CustomRasterTileAcquisitionSession acquisition = new(
            "https://{subdomain}.example/{z}/{x}/{y}",
            TileLayerBounds.World,
            false,
            22,
            0,
            mutableSubdomains,
            256);
        object sourceKey = acquisition.SourceKey;

        mutableSubdomains[0] = "mutated";

        Assert.AreEqual(
            "https://a.example/2/1/1",
            acquisition.ExpandUrl(new TileId(2, 1, 1)));
        Assert.AreEqual(256, acquisition.TileSize);
        Assert.AreEqual(sourceKey, acquisition.SourceKey);
        Assert.DoesNotContain(
            "https://",
            acquisition.SourceKey.ToString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void CustomTileHttpClientReadsAndWritesTheResponseCache()
    {
        using HttpBaseProtocolFilter filter =
            CustomRasterTileAcquisitionSession.CreateHttpFilter();

        Assert.AreEqual(
            HttpCacheReadBehavior.Default,
            filter.CacheControl.ReadBehavior);
        Assert.AreEqual(
            HttpCacheWriteBehavior.Default,
            filter.CacheControl.WriteBehavior);
    }

    [TestMethod]
    public async Task EncodedTileReadRejectsDeclaredOversizeContent()
    {
        using HttpBufferContent content = new(new byte[17].AsBuffer());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => WinRtHttpContentReader.ReadBoundedAsync(
                content,
                16,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task EncodedTileReadAcceptsContentAtLimit()
    {
        byte[] expected = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        using HttpBufferContent content = new(expected.AsBuffer());

        byte[] actual = await WinRtHttpContentReader.ReadBoundedAsync(
            content,
            expected.Length,
            CancellationToken.None);

        Assert.AreSequenceEqual(expected, actual);
    }

    private static TileLayerSnapshot Snapshot(
        string template,
        bool isTms,
        string[]? subdomains = null,
        int tileSize = 256) =>
        new(
            1,
            0,
            new CustomRasterTileAcquisitionSession(
                template,
                TileLayerBounds.World,
                isTms,
                22,
                0,
                subdomains ?? [],
                tileSize),
            0,
            24,
            true,
            1,
            TimeSpan.FromMilliseconds(300));

    private static CustomRasterTileAcquisitionSession CustomAcquisition(
        TileLayerSnapshot snapshot) =>
        Assert.IsInstanceOfType<CustomRasterTileAcquisitionSession>(snapshot.Acquisition);

    private static void AssertDependencyProperty(Type type, string name)
    {
        FieldInfo field = type.GetField(
            name + "Property",
            BindingFlags.Public | BindingFlags.Static)!;
        Assert.IsNotNull(field);
        Assert.AreEqual(typeof(DependencyProperty), field.FieldType);
        Assert.IsTrue(field.IsInitOnly);
    }
}
