using WinUIEx.Maps.Rendering;
using Windows.Graphics.Imaging;
using Windows.Web.Http.Filters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureTileLayerTests
{
    [TestMethod]
    public void AzureHttpClientReadsAndWritesTheResponseCache()
    {
        using HttpBaseProtocolFilter filter =
            AzureTileAcquisitionSession.CreateHttpFilter();

        Assert.AreEqual(
            HttpCacheReadBehavior.Default,
            filter.CacheControl.ReadBehavior);
        Assert.AreEqual(
            HttpCacheWriteBehavior.Default,
            filter.CacheControl.WriteBehavior);
    }

    [TestMethod]
    [DataRow(MapStyle.RoadRaster, 10, "microsoft.base.road")]
    [DataRow(MapStyle.GrayscaleDarkRaster, 10, "microsoft.base.darkgrey")]
    [DataRow(MapStyle.Satellite, 10, "microsoft.imagery")]
    [DataRow(MapStyle.RoadShadedReliefRaster, 10, "microsoft.base.road")]
    public void RasterStylesUseSupportedRasterTilesets(
        MapStyle style,
        int zoom,
        string expectedTileset)
    {
        Assert.AreSequenceEqual([expectedTileset], AzureTileAcquisitionSession.GetTilesetIds(style, zoom));
    }

    [TestMethod]
    public void VectorStylesUseTheSharedAzureBaseTileset()
    {
        foreach (MapStyle style in Enum.GetValues<MapStyle>()
            .Where(AzureTileAcquisitionSession.IsVectorStyle))
        {
            Assert.AreSequenceEqual(
                style == MapStyle.SatelliteWithRoads
                    ? ["microsoft.imagery", "microsoft.base"]
                    : ["microsoft.base"],
                AzureTileAcquisitionSession.GetTilesetIds(style, 6));
            Assert.AreEqual(
                style == MapStyle.SatelliteWithRoads
                    ? LayerRenderKind.HybridTiles
                    : LayerRenderKind.VectorPoints,
                new AzureTileAcquisitionSession(style, "token").RenderKind);
        }
    }

    [TestMethod]
    public void LegacyStylesRetainRasterRendering()
    {
        foreach (MapStyle style in new[]
        {
            MapStyle.RoadRaster,
            MapStyle.GrayscaleDarkRaster,
            MapStyle.Satellite,
            MapStyle.RoadShadedReliefRaster,
        })
        {
            Assert.AreEqual(
                LayerRenderKind.RasterTiles,
                new AzureTileAcquisitionSession(style, "token").RenderKind);
        }
    }

    [TestMethod]
    public void ShadedReliefUsesRoadAndTerrainAtSupportedZooms()
    {
        Assert.AreSequenceEqual(
            ["microsoft.base.road", "microsoft.terra.main"],
            AzureTileAcquisitionSession.GetTilesetIds(
                MapStyle.RoadShadedReliefRaster,
                6));
        Assert.AreSequenceEqual(
            ["microsoft.base.road"],
            AzureTileAcquisitionSession.GetTilesetIds(
                MapStyle.RoadShadedReliefRaster,
                7));
    }

    [TestMethod]
    public void RoadOverlayAlphaCompositesOverTerrain()
    {
        byte[] terrain = [20, 40, 60, 255];
        byte[] roads = [220, 140, 100, 128];

        byte[] result = AzureTileAcquisitionSession.CompositePixels(terrain, roads);

        Assert.AreSequenceEqual(new byte[] { 120, 90, 80, 255 }, result);
    }

    [TestMethod]
    public void RoadOverlayCompositionStopsBeforeAllocatingObsoleteResult()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            AzureTileAcquisitionSession.CompositePixels(
                new byte[256 * 256 * 4],
                new byte[256 * 256 * 4],
                cancellation.Token));
    }

    [TestMethod]
    public void ScaledTileUsesScaledPixelDimensions()
    {
        BitmapTransform transform = new()
        {
            ScaledWidth = 128,
            ScaledHeight = 128,
        };

        Assert.AreEqual(
            (128u, 128u),
            AzureTileAcquisitionSession.GetDecodedDimensions(256, 256, transform));
    }

    [TestMethod]
    public void SatelliteUsesItsNativeMaximumTileLevel()
    {
        Assert.AreEqual(19, AzureTileAcquisitionSession.GetMaximumTileZoom(MapStyle.Satellite));
        Assert.AreEqual(
            MapCamera.MaximumTileZoom,
            AzureTileAcquisitionSession.GetMaximumTileZoom(MapStyle.Road));
        Assert.AreEqual(
            19,
            AzureTileAcquisitionSession.GetMaximumTileZoom(
                MapStyle.SatelliteWithRoads));
    }

    [TestMethod]
    public void AzureVectorStylesUseMvtAcquisition()
    {
        MapStyle[] vectorStyles =
        [
            MapStyle.Road,
            MapStyle.GrayscaleDark,
            MapStyle.RoadShadedRelief,
            MapStyle.BlankAccessible,
            MapStyle.GrayscaleLight,
            MapStyle.Night,
            MapStyle.HighContrastDark,
            MapStyle.HighContrastLight,
            MapStyle.SatelliteWithRoads,
        ];

        foreach (MapStyle style in vectorStyles)
        {
            Assert.IsTrue(AzureTileAcquisitionSession.IsVectorStyle(style));
        }
        Assert.IsFalse(AzureTileAcquisitionSession.IsVectorStyle(MapStyle.Blank));
        Assert.IsFalse(
            AzureTileAcquisitionSession.IsVectorStyle(MapStyle.RoadRaster));
        Assert.IsFalse(
            AzureTileAcquisitionSession.IsVectorStyle(
                MapStyle.GrayscaleDarkRaster));
        Assert.IsFalse(
            AzureTileAcquisitionSession.IsVectorStyle(
                MapStyle.RoadShadedReliefRaster));
        Assert.IsFalse(AzureTileAcquisitionSession.IsVectorStyle(MapStyle.Satellite));
        Assert.IsTrue(
            AzureTileAcquisitionSession.IsHybridStyle(
                MapStyle.SatelliteWithRoads));
        Assert.AreEqual(
            MapCamera.MaximumTileZoom,
            AzureTileAcquisitionSession.GetMaximumTileZoom(MapStyle.HighContrastDark));
    }

    [TestMethod]
    [DataRow(MapStyle.RoadRaster, "road")]
    [DataRow(MapStyle.GrayscaleDarkRaster, "grayscale_dark")]
    [DataRow(MapStyle.RoadShadedReliefRaster, "road_shaded_relief")]
    [DataRow(MapStyle.Road, "road")]
    [DataRow(MapStyle.GrayscaleDark, "grayscale_dark")]
    [DataRow(MapStyle.Satellite, "satellite")]
    [DataRow(MapStyle.RoadShadedRelief, "road_shaded_relief")]
    [DataRow(MapStyle.Blank, "blank")]
    [DataRow(MapStyle.BlankAccessible, "blank_accessible")]
    [DataRow(MapStyle.GrayscaleLight, "grayscale_light")]
    [DataRow(MapStyle.Night, "night")]
    [DataRow(MapStyle.HighContrastDark, "high_contrast_dark")]
    [DataRow(MapStyle.HighContrastLight, "high_contrast_light")]
    [DataRow(MapStyle.SatelliteWithRoads, "satellite_road_labels")]
    public void PublicStylesMapToAzureStyleNames(
        MapStyle style,
        string expected)
    {
        Assert.AreEqual(expected, AzureTileAcquisitionSession.GetAzureStyleName(style));
    }

    [TestMethod]
    public void SatelliteRoadStyleAssetsUseServiceSlug()
    {
        AzureVectorStyleAssetPaths paths = AzureVectorStyleProvider.GetAssetPaths(
            MapStyle.SatelliteWithRoads);

        Assert.Contains("satellite_road_labels", paths.Style);
        Assert.Contains("satellite_road_labels", paths.SpriteIndex);
        Assert.Contains("satellite_road_labels", paths.SpriteImage);
        Assert.DoesNotContain("satellite_with_roads", paths.Style);
    }

    [TestMethod]
    [DataRow("fr-CA")]
    [DataRow("zh-Hant-TW")]
    [DataRow("eo")]
    public void FrameworkElementLanguageIsPassedThroughToAzure(
        string language)
    {
        Assert.AreEqual(
            language,
            AzureTileAcquisitionSession.GetRequestLanguage(
                language),
            ignoreCase: true);
    }

    [TestMethod]
    public void EmptyFrameworkElementLanguageUsesAzureDefault()
    {
        Assert.IsNull(AzureTileAcquisitionSession.GetRequestLanguage(null));
        Assert.IsNull(AzureTileAcquisitionSession.GetRequestLanguage(""));
        Assert.IsNull(AzureTileAcquisitionSession.GetRequestLanguage("  "));
    }

    [TestMethod]
    public void AzureLanguageIsAddedToTileQueryWhenPresent()
    {
        const string path = "map/tile?api-version=2024-04-01";

        Assert.AreEqual(
            $"{path}&language=fr",
            AzureTileAcquisitionSession.AddLanguageToQuery(path, "fr"));
        Assert.AreEqual(
            path,
            AzureTileAcquisitionSession.AddLanguageToQuery(path, null));
    }

    [TestMethod]
    public void StyleAndAuthenticationArePartOfImmutableSourceIdentity()
    {
        AzureTileAcquisitionSession original =
            new(MapStyle.Road, "token", "en-US");

        Assert.AreEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(
                MapStyle.Road,
                "token",
                "en-US").SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(
                MapStyle.Satellite,
                "token",
                "en-US").SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(
                MapStyle.Road,
                "replacement",
                "en-US").SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(
                MapStyle.Road,
                "token",
                "fr").SourceKey);
        Assert.DoesNotContain("token", original.SourceKey.ToString()!, StringComparison.Ordinal);
    }
}
