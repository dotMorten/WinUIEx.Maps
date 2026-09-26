using System.Buffers;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Graphics.Imaging;
using WinUIEx.Maps.Rendering;
using WinUIEx.Maps.Tests.UITestHelpers;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class AzureOverlayAcquisitionTests
{
    [TestMethod]
    [DataRow("icon_category", false)]
    [DataRow("icon_category_0", false)]
    [DataRow("icon_category", true)]
    [DataRow("icon_category_0", true)]
    public async Task IncidentCategoriesSelectDistinctHighDpiPictograms(string property, bool major)
    {
        string[] names = ["warning", "accident", "fog", "danger", "rain", "ice", "congestion",
            "lane-closed", "road-closed", "roadworks", "wind", "flooding", "detour", "cluster", "breakdown"];
        MapboxVectorTileBuilder builder = new();
        for (int category = 0; category < names.Length; category++)
            builder.AddPoint("incidents", 256 + category * 256, 2048,
                new Dictionary<string, object> { [property] = category, ["magnitude"] = major ? 3 : 2 });
        byte[] encoded = builder.Build();
        var tile = await AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(new(12, 100, 100), encoded);
        var symbols = tile.StyleAssets.ResolveSymbols(tile.Features, 12);
        Assert.AreEqual(0, symbols.EvaluationFailureCount);
        Assert.AreEqual(0, symbols.UnavailableSpriteCount);
        Assert.HasCount(15, symbols.Symbols);
        Assert.HasCount(15, tile.SpriteTextures);
        HashSet<string> fingerprints = [];
        for (int category = 0; category < names.Length; category++)
        {
            var symbol = symbols.Symbols[category];
            Assert.AreEqual(VectorSpriteAtlas.CreateTextureId(TrafficIncidentIcons.Identity,
                names[category] + (major ? "-major" : "")),
                symbol.TextureId);
            Assert.AreEqual(32d, symbol.Width);
            Assert.AreEqual(32d, symbol.Height);
            var texture = tile.SpriteTextures.Single(texture => texture.TextureId == symbol.TextureId);
            Assert.AreEqual(64u, texture.Width);
            Assert.AreEqual(64u, texture.Height);
            Assert.AreEqual(0, texture.Pixels[3], "A warning triangle must not fill its upper corners.");
            Assert.AreEqual(0, texture.Pixels[(63 * 4) + 3]);
            Assert.IsTrue(fingerprints.Add(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(texture.Pixels))), names[category]);
            Assert.IsTrue(texture.Pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha is > 0 and < 255),
                "Pictograms should have antialiased edges.");
            for (int pixel = 0; pixel < texture.Pixels.Length; pixel += 4)
            {
                byte alpha = texture.Pixels[pixel + 3];
                Assert.IsTrue(texture.Pixels[pixel] <= alpha &&
                    texture.Pixels[pixel + 1] <= alpha && texture.Pixels[pixel + 2] <= alpha,
                    "The shared upload pipeline requires premultiplied pixels.");
            }
        }
        var refreshed = await AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(new(12, 100, 100), encoded);
        Assert.AreSequenceEqual(tile.SpriteTextures.Select(t => t.TextureId).ToArray(),
            refreshed.SpriteTextures.Select(t => t.TextureId).ToArray());
        for (int i = 0; i < tile.SpriteTextures.Length; i++)
            Assert.AreSame(tile.SpriteTextures[i], refreshed.SpriteTextures[i]);
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    [DataRow(99, false)]
    public async Task OnlyMajorSeverityUsesRedSigns(int magnitude, bool major)
    {
        var encoded = new MapboxVectorTileBuilder().AddPoint("incidents", 2048, 2048,
            new Dictionary<string, object> { ["icon_category"] = 9, ["magnitude"] = magnitude }).Build();
        var tile = await AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(new(12, 100, 100), encoded);
        Assert.AreEqual(VectorSpriteAtlas.CreateTextureId(TrafficIncidentIcons.Identity,
            major ? "roadworks-major" : "roadworks"), tile.SpriteTextures.Single().TextureId);
    }

    [TestMethod]
    public void IncidentPickingFollowsTriangleRatherThanItsBoundingCircle()
    {
        Assert.IsTrue(TrafficIncidentIcons.Contains(0, -14));
        Assert.IsTrue(TrafficIncidentIcons.Contains(13, 13));
        Assert.IsTrue(TrafficIncidentIcons.Contains(-13, 13));
        Assert.IsFalse(TrafficIncidentIcons.Contains(13, -13));
        Assert.IsFalse(TrafficIncidentIcons.Contains(-13, -13));
        Assert.IsFalse(TrafficIncidentIcons.Contains(0, 17));
    }

    [TestMethod]
    public async Task IncidentIconFallbackAndCategoryPrecedenceAreDeterministic()
    {
        Dictionary<string, object>[] properties =
        [
            [],
            new() { ["icon_category"] = 99 },
            new() { ["icon_category"] = -1 },
            new() { ["icon_category"] = 1.5 },
            new() { ["icon_category"] = "accident" },
            new() { ["icon_category"] = 8, ["icon_category_0"] = 9 },
            new() { ["icon_category"] = "invalid", ["icon_category_0"] = 9 },
        ];
        MapboxVectorTileBuilder builder = new();
        foreach (var values in properties)
            builder.AddPoint("incidents", 2048, 2048, values);
        var tile = await AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(new(12, 100, 100), builder.Build());
        var resolved = tile.StyleAssets.ResolveSymbols(tile.Features, 12);
        Assert.AreEqual(0, resolved.EvaluationFailureCount);
        Assert.HasCount(properties.Length, resolved.Symbols);
        string[] expected = ["warning", "warning", "warning", "warning", "warning", "road-closed", "roadworks"];
        Assert.AreSequenceEqual(expected.Select(name =>
            VectorSpriteAtlas.CreateTextureId(TrafficIncidentIcons.Identity, name)).ToArray(),
            resolved.Symbols.Select(symbol => symbol.TextureId).ToArray());
    }

    [TestMethod]
    [DataRow("microsoft.weather.radar.main", 15)]
    [DataRow("microsoft.weather.infrared.main", 15)]
    [DataRow("microsoft.traffic.relative", 22)]
    [DataRow("microsoft.traffic.absolute", 22)]
    [DataRow("microsoft.traffic.delay", 22)]
    public void OverlayCapturesSourceCapabilitiesAndClampsZoom(string tileset, int maximumZoom)
    {
        var session = new AzureOverlayAcquisitionSession(tileset, "fixture-token", null, null);
        Assert.AreEqual(RasterSourceKind.Azure, session.SourceKind);
        Assert.AreEqual(tileset.StartsWith("microsoft.weather.", StringComparison.Ordinal)
            ? LayerRenderKind.RasterTiles : LayerRenderKind.VectorPoints, session.RenderKind);
        Assert.AreEqual(256, session.TileSize);
        Assert.AreEqual(0, session.MinSourceZoom);
        Assert.AreEqual(maximumZoom, session.MaxSourceZoom);
        Assert.IsTrue(session.SupportsAttribution);
        Assert.IsTrue(session.CanAcquire);
        Assert.AreEqual(maximumZoom, session.GetSourceZoom(
            MapCamera.CreateScene(0, 0, 22, 22, 256, 256, 0, 0)));
        Assert.IsFalse(session.IncludesTile(new(maximumZoom + 1, 0, 0)));
        Assert.IsFalse(session.IncludesTile(new(1, 2, 0)));
        Assert.IsFalse(session.IncludesTile(new(1, 0, -1)));
        Assert.IsTrue(session.IncludesTile(new(0, 0, 0)));
    }

    [TestMethod]
    public void ContentStateChangesIdentityWithoutLeakingIntoDiagnosticsOrRefreshQuery()
    {
        DateTimeOffset timestamp = new(2026, 9, 24, 16, 0, 0, TimeSpan.FromHours(-7));
        AzureOverlayAcquisitionSession Create(
            string tileset = "microsoft.weather.radar.main", string token = "private-fixture-token",
            string? language = "en-US", DateTimeOffset? time = null, long version = 0) =>
            new(tileset, token, language, time ?? timestamp, version);
        var first = Create();
        Assert.AreEqual(first.SourceKey, Create(language: " EN-us ", time: timestamp.ToUniversalTime()).SourceKey);
        Assert.AreNotEqual(first.SourceKey, Create(token: "different").SourceKey);
        Assert.AreNotEqual(first.SourceKey, Create(language: "fr").SourceKey);
        Assert.AreNotEqual(first.SourceKey, Create(time: timestamp.AddMinutes(5)).SourceKey);
        Assert.AreNotEqual(first.SourceKey, Create(version: 1).SourceKey);
        Assert.AreNotEqual(first.SourceKey, Create(tileset: "microsoft.weather.infrared.main").SourceKey);
        Assert.AreEqual("OverlaySourceKey", first.SourceKey.ToString());
        Assert.AreEqual(first.GetTileRequestPath(new(1, 0, 1)), Create(version: 1).GetTileRequestPath(new(1, 0, 1)));
        Assert.DoesNotContain("private-fixture-token", first.GetTileRequestPath(new(1, 0, 1)));
        Assert.IsFalse(new AzureOverlayAcquisitionSession(
            "microsoft.traffic.relative", " ", null, null).CanAcquire);
    }

    [TestMethod]
    [DataRow("microsoft.weather.radar.main", false)]
    [DataRow("microsoft.weather.infrared.main", false)]
    [DataRow("microsoft.traffic.absolute", false)]
    [DataRow("microsoft.traffic.relative", false)]
    [DataRow("microsoft.traffic.delay", false)]
    [DataRow("microsoft.traffic.incident", true)]
    public void LatestSessionsShareRefreshIdentityButNotVersionedSourceKey(string tileset, bool incidents)
    {
        var first = new AzureOverlayAcquisitionSession(tileset, "private-fixture-token", "en-US", null, 10, incidents);
        var next = new AzureOverlayAcquisitionSession(tileset, "private-fixture-token", " EN-us ", null, 11, incidents);
        Assert.IsNotNull(first.RefreshIdentity);
        Assert.AreEqual(first.RefreshIdentity, next.RefreshIdentity);
        Assert.AreNotEqual(first.SourceKey, next.SourceKey);
        Assert.AreEqual("OverlaySourceKey", first.RefreshIdentity.ToString());
    }

    [TestMethod]
    public void LatestRefreshIdentityChangesWithEveryContentConfigurationChange()
    {
        AzureOverlayAcquisitionSession Create(
            string tileset = "microsoft.traffic.relative",
            string token = "private-fixture-token",
            string? language = "en-US",
            bool incidents = false) =>
            new(tileset, token, language, null, 1, incidents);
        var first = Create();
        Assert.AreNotEqual(first.RefreshIdentity, Create(token: "other-token").RefreshIdentity);
        Assert.AreNotEqual(first.RefreshIdentity, Create(language: "fr-FR").RefreshIdentity);
        Assert.AreNotEqual(first.RefreshIdentity, Create(language: null).RefreshIdentity);
        foreach (string tileset in new[]
        {
            "microsoft.traffic.absolute",
            "microsoft.traffic.delay",
            "microsoft.weather.radar.main",
            "microsoft.weather.infrared.main",
        })
            Assert.AreNotEqual(first.RefreshIdentity, Create(tileset: tileset).RefreshIdentity);
        Assert.AreNotEqual(first.RefreshIdentity,
            Create(tileset: "microsoft.traffic.incident", incidents: true).RefreshIdentity);
        Assert.AreNotEqual(Create(tileset: "microsoft.weather.radar.main").RefreshIdentity,
            Create(tileset: "microsoft.weather.infrared.main").RefreshIdentity);
    }

    [TestMethod]
    [DataRow("microsoft.weather.radar.main")]
    [DataRow("microsoft.weather.infrared.main")]
    public void FixedTimestampSessionsNeverParticipateInLatestRefresh(string tileset)
    {
        foreach (long version in new long[] { 0, 1, 20 })
        {
            var session = new AzureOverlayAcquisitionSession(
                tileset, "token", "en-US", DateTimeOffset.UnixEpoch, version);
            Assert.IsNull(session.RefreshIdentity);
        }
    }

    [TestMethod]
    [DataRow("microsoft.weather.radar.main", 5)]
    [DataRow("microsoft.weather.infrared.main", 10)]
    public void WeatherTimestampIdentityUsesNearestUtcFrame(string tileset, int minutes)
    {
        DateTimeOffset frame = new(2026, 9, 24, 23, 0, 0, TimeSpan.Zero);
        AzureOverlayAcquisitionSession Create(DateTimeOffset time) =>
            new(tileset, "token", null, time);
        var exact = Create(frame);
        Assert.AreEqual(exact.SourceKey, Create(frame.AddSeconds(minutes * 30 - 1)).SourceKey);
        Assert.AreEqual(exact.SourceKey, Create(frame.AddSeconds(-minutes * 30 + 1)).SourceKey);
        Assert.AreEqual(Create(frame.AddMinutes(minutes)).SourceKey,
            Create(frame.AddSeconds(minutes * 30)).SourceKey);
        Assert.AreEqual(exact.GetTileRequestPath(new(0, 0, 0)),
            Create(frame.ToOffset(TimeSpan.FromHours(-7)).AddSeconds(1)).GetTileRequestPath(new(0, 0, 0)));
        Assert.IsNull(AzureOverlayAcquisitionSession.NormalizeTimestamp(tileset, null));
        Assert.AreEqual(TimeSpan.Zero,
            AzureOverlayAcquisitionSession.NormalizeTimestamp(tileset, DateTimeOffset.MaxValue)!.Value.Offset);
    }

    [TestMethod]
    public void WeatherTimestampUsesInvariantUtcAndEscapedLanguage()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var session = new AzureOverlayAcquisitionSession("microsoft.weather.radar.main",
                "token", "en&view=other",
                new DateTimeOffset(2026, 9, 24, 16, 5, 0, TimeSpan.FromHours(-7)));
            Assert.AreEqual(
                "map/tile?api-version=2024-04-01&tilesetId=microsoft.weather.radar.main&zoom=3&x=2&y=4&tileSize=256" +
                "&timeStamp=2026-09-24T23%3A05%3A00.0000000Z&language=en%26view%3Dother",
                session.GetTileRequestPath(new(3, 2, 4)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void LiveOverlayRequestsRevalidateWithoutChangingBaseMapCacheBehavior()
    {
        using var overlay = new Windows.Web.Http.HttpRequestMessage(
            Windows.Web.Http.HttpMethod.Get, new Uri("https://example.com/tile"));
        using var baseMap = new Windows.Web.Http.HttpRequestMessage(
            Windows.Web.Http.HttpMethod.Get, new Uri("https://example.com/tile"));
        AzureTileAcquisitionSession.ApplyRequestHeaders(overlay, "fixture", "image/png", revalidate: true);
        AzureTileAcquisitionSession.ApplyRequestHeaders(baseMap, "fixture", "image/png");
        Assert.Contains("no-cache", overlay.Headers.CacheControl.ToString());
        Assert.DoesNotContain("no-cache", baseMap.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public void InvalidModesAndUnsupportedTimestampFailBeforeAnyRequest()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.traffic.incident", "token", null, null));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.weather.radar.main", "token", null, null, incidents: true));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.traffic.relative", "token", null, DateTimeOffset.UnixEpoch));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.traffic.relative.main", "token", null, null));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.traffic.relative", "token", null, null, flowStyle: TrafficFlowStyle.Absolute));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "microsoft.weather.radar.main", "token", null, null, flowStyle: TrafficFlowStyle.Relative));
        Assert.ThrowsExactly<ArgumentException>(() => new AzureOverlayAcquisitionSession(
            "https://example.com/private", "token", null, null));
    }

    [TestMethod]
    public async Task EveryFlowModeRequestsAzureVectorsAndKeepsLocalStyleInSourceIdentity()
    {
        HashSet<object> keys = [];
        HashSet<object> refreshKeys = [];
        foreach (TrafficFlowStyle style in Enum.GetValues<TrafficFlowStyle>())
        {
            var session = new AzureOverlayAcquisitionSession(
                AzureTrafficLayer.GetTileset(style), "token", "en", null, 1, flowStyle: style);
            Assert.AreEqual(LayerRenderKind.VectorPoints, session.RenderKind);
            Assert.IsTrue(keys.Add(session.SourceKey));
            Assert.IsTrue(refreshKeys.Add(session.RefreshIdentity!));
            string path = session.GetTileRequestPath(new(3, 2, 4));
            Assert.StartsWith("map/tile?api-version=2024-04-01&tilesetId=microsoft.traffic.", path);
            Assert.DoesNotContain("tileSize", path);
            Assert.DoesNotContain(".main", path);
            Assert.DoesNotContain(".dark", path);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                session.GetTileAsync(new(3, 2, 4), CancellationToken.None));
        }
    }

    [TestMethod]
    [DataRow(TrafficFlowStyle.Absolute, 30d, 70d)]
    [DataRow(TrafficFlowStyle.Relative, 0.5, 1d)]
    [DataRow(TrafficFlowStyle.RelativeDark, 0.5, 1d)]
    [DataRow(TrafficFlowStyle.Reduced, 0.3, 0.9)]
    [DataRow(TrafficFlowStyle.Delay, 0.5, 1d)]
    public async Task FlowVectorsResolveCongestionColorsAndZoomDependentGeometry(
        TrafficFlowStyle style, double slow, double fast)
    {
        byte[] fixture = new MapboxVectorTileBuilder()
            .AddLine("Traffic flow", [new(0, 1000), new(4096, 1000)],
                new Dictionary<string, object> { ["traffic_level"] = slow })
            .AddLine("Traffic flow", [new(0, 3000), new(4096, 3000)],
                new Dictionary<string, object> { ["traffic_level"] = fast })
            .Build();
        var tile = await AzureOverlayAcquisitionSession.DecodeTrafficTileAsync(new(12, 100, 100), fixture, style);
        var lines = tile.StyleAssets.ResolveLines(tile.Features, 12);
        Assert.AreEqual(0, lines.EvaluationFailureCount);
        Assert.HasCount(style == TrafficFlowStyle.Delay ? 1 : 2, lines.Lines);
        Assert.IsEmpty(tile.SpriteTextures);
        Assert.IsNull(tile.Background);
        Assert.AreEqual(3d, lines.Lines[0].Style.Width);
        Assert.AreEqual(VectorLineCap.Round, lines.Lines[0].Style.Cap);
        if (style != TrafficFlowStyle.Delay)
            Assert.AreNotEqual(lines.Lines[0].Style.Color, lines.Lines[1].Style.Color);
        var zoomed = tile.StyleAssets.ResolveLines(tile.Features, 18);
        Assert.AreEqual(6d, zoomed.Lines[0].Style.Width);
    }

    [TestMethod]
    public async Task FlowDirectionOffsetsClosureAndMissingDataDoNotInventFreeFlow()
    {
        var builder = new MapboxVectorTileBuilder();
        foreach (bool left in new[] { false, true })
            builder.AddLine("Traffic flow", [new(0, 0), new(4096, 4096)],
                new Dictionary<string, object>
                {
                    ["traffic_level"] = 1d, ["traffic_road_coverage"] = "one_side",
                    ["left_hand_traffic"] = left,
                });
        builder.AddLine("Traffic flow", [new(0, 0), new(4096, 4096)],
            new Dictionary<string, object> { ["road_closure"] = true });
        builder.AddLine("Traffic flow", [new(0, 0), new(4096, 4096)], new Dictionary<string, object>());
        var tile = await AzureOverlayAcquisitionSession.DecodeTrafficTileAsync(
            new(12, 100, 100), builder.Build(), TrafficFlowStyle.Relative);
        var lines = tile.StyleAssets.ResolveLines(tile.Features, 12);
        Assert.AreEqual(0, lines.EvaluationFailureCount);
        Assert.HasCount(4, lines.Lines);
        Assert.AreEqual(1.5, lines.Lines[0].Style.Offset);
        Assert.AreEqual(-1.5, lines.Lines[1].Style.Offset);
        Assert.AreNotEqual(lines.Lines[0].Style.Color, lines.Lines[2].Style.Color);
        Assert.AreNotEqual(lines.Lines[0].Style.Color, lines.Lines[3].Style.Color);
        Assert.AreNotEqual(lines.Lines[2].Style.Color, lines.Lines[3].Style.Color);
    }

    [TestMethod]
    public async Task IncidentFixturePreservesMetadataAndRendersPointsAndLinesWithoutSchemaGuessing()
    {
        // Synthetic MVT fixture: the POI name/properties are evidenced by SDK 3's legacy
        // source, not a verified sample of the 2024 Render service. A deliberately unknown
        // line source-layer verifies we do not silently discard current-service geometry.
        byte[] fixture = new MapboxVectorTileBuilder()
            .AddPoint("Traffic incident POI", 2048, 1024,
                new Dictionary<string, object>
                {
                    ["id"] = "fixture-incident",
                    ["icon_category_0"] = 9,
                    ["description_0"] = "Fixture road works",
                    ["magnitude"] = 2,
                    ["delay"] = 120,
                })
            .AddLine("unspecified-current-service-line-layer",
                [new(0, 0), new(2048, 2048)],
                new Dictionary<string, object> { ["unrecognized-metadata"] = "preserved" })
            .Build();
        DecodedVectorTile tile = await AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(
            new(12, 100, 100), fixture);
        Assert.HasCount(2, tile.Features.Features);
        VectorTileFeature point = tile.Features.Features[0];
        Assert.IsTrue(point.TryGetProperty("id", out VectorTileValue id));
        Assert.AreEqual("fixture-incident", id.StringValue);
        Assert.IsTrue(point.TryGetProperty("description_0", out VectorTileValue description));
        Assert.AreEqual("Fixture road works", description.StringValue);
        Assert.IsTrue(tile.Features.Features[1].TryGetProperty("unrecognized-metadata", out VectorTileValue unknown));
        Assert.AreEqual("preserved", unknown.StringValue);
        Assert.HasCount(1, tile.StyleAssets.ResolveSymbols(tile.Features, 12).Symbols);
        Assert.HasCount(1, tile.StyleAssets.ResolveLines(tile.Features, 12).Lines);
        Assert.HasCount(1, tile.SpriteTextures);
        Assert.IsNull(tile.Background);
        var session = new AzureOverlayAcquisitionSession(
            "microsoft.traffic.incident", "token", null, null, incidents: true);
        Assert.AreEqual(LayerRenderKind.VectorPoints, session.RenderKind);
        Assert.DoesNotContain("tileSize", session.GetTileRequestPath(new(0, 0, 0)));
    }

    [TestMethod]
    public async Task IncidentDecodeRejectsOversizeAndHonorsCancellation()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(
                new(0, 0, 0), new byte[4 * 1024 * 1024 + 1]));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            AzureOverlayAcquisitionSession.DecodeIncidentTileAsync(
                new(0, 0, 0), ReadOnlyMemory<byte>.Empty, cancellationToken: cancellation.Token));
        var raster = new AzureOverlayAcquisitionSession("microsoft.weather.radar.main", "token", null, null);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            raster.GetTileAsync(new(0, 0, 0), cancellation.Token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            raster.GetVectorTileAsync(new(0, 0, 0), CancellationToken.None));
    }

    [TestMethod]
    public async Task SharedRasterDecoderPreservesStraightAlphaAndRejectsWrongDimensions()
    {
        byte[] fixture = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNocFBoAAADhQFhC+q+qAAAAABJRU5ErkJggg==");
        byte[] rented = ArrayPool<byte>.Shared.Rent(fixture.Length);
        fixture.CopyTo(rented, 0);
        using PooledByteBuffer encoded = new(rented, fixture.Length);
        var tile = await AzureTileAcquisitionSession.DecodeTilePixelsAsync(
            encoded, 1, BitmapAlphaMode.Straight, new BitmapTransform(), 0,
            CancellationToken.None, requireExactDimensions: true);
        Assert.AreSequenceEqual(new byte[] { 32, 64, 128, 128 }, tile.Pixels);
        Assert.AreEqual(1u, tile.Width);
        Assert.AreEqual(1u, tile.Height);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            AzureTileAcquisitionSession.DecodeTilePixelsAsync(
                encoded, 256, BitmapAlphaMode.Straight, new BitmapTransform(), 0,
                CancellationToken.None, requireExactDimensions: true));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            AzureTileAcquisitionSession.DecodeTilePixelsAsync(
                encoded, 0, BitmapAlphaMode.Straight, new BitmapTransform(), 0,
                CancellationToken.None));
    }
}
