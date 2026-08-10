using WinUIEx.Maps.Rendering;
using System.Net;
using Windows.Graphics.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureTileLayerTests
{
    [TestMethod]
    public void ConnectionCandidatesPreferIPv4AndRetainIPv6Fallback()
    {
        IPAddress ipv6 = IPAddress.Parse("2001:db8::1");
        IPAddress ipv4 = IPAddress.Parse("192.0.2.1");

        IPAddress[] candidates = AzureTileAcquisitionSession.SelectConnectionAddresses(
            [ipv6, IPAddress.Parse("2001:db8::2"), ipv4, IPAddress.Parse("192.0.2.2")]);

        Assert.AreSequenceEqual([ipv4, ipv6], candidates);
    }

    [TestMethod]
    [DataRow(MapStyle.Road, 10, "microsoft.base.road")]
    [DataRow(MapStyle.GrayscaleDark, 10, "microsoft.base.darkgrey")]
    [DataRow(MapStyle.Satellite, 10, "microsoft.imagery")]
    [DataRow(MapStyle.RoadShadedRelief, 10, "microsoft.base.road")]
    public void RasterStylesUseSupportedRasterTilesets(
        MapStyle style,
        int zoom,
        string expectedTileset)
    {
        Assert.AreSequenceEqual([expectedTileset], AzureTileAcquisitionSession.GetTilesetIds(style, zoom));
    }

    [TestMethod]
    public void ShadedReliefCombinesTerrainAndRoadOverlayAtSupportedZooms()
    {
        Assert.AreSequenceEqual(
            ["microsoft.base.road", "microsoft.terra.main"],
            AzureTileAcquisitionSession.GetTilesetIds(MapStyle.RoadShadedRelief, 6));
        Assert.AreEqual(512, AzureTileAcquisitionSession.GetTileRequestSize("microsoft.terra.main"));
        Assert.AreEqual(512, AzureTileAcquisitionSession.GetStyleTileSize(MapStyle.RoadShadedRelief, 6));
        Assert.AreEqual(256, AzureTileAcquisitionSession.GetStyleTileSize(MapStyle.Road, 6));
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
    }

    [TestMethod]
    public void StyleAndAuthenticationArePartOfImmutableSourceIdentity()
    {
        AzureTileAcquisitionSession original = new(MapStyle.Road, "token");

        Assert.AreEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(MapStyle.Road, "token").SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(MapStyle.Satellite, "token").SourceKey);
        Assert.AreNotEqual(
            original.SourceKey,
            new AzureTileAcquisitionSession(MapStyle.Road, "replacement").SourceKey);
        Assert.DoesNotContain("token", original.SourceKey.ToString()!, StringComparison.Ordinal);
    }
}
