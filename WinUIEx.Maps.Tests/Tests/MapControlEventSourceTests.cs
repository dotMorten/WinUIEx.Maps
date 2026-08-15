using WinUIEx.Maps.Rendering.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinUIEx.Maps.Tests;

[TestClass]
public sealed class MapControlEventSourceTests
{
    [TestMethod]
    public void ProviderHasStableName()
    {
        Assert.AreEqual(
            "WinUIEx-Maps-Rendering",
            MapControlEventSource.Log.Name);
        Assert.DoesNotContain(
            source => source.Name == "Microsoft-MapControl-Rendering",
            EventSource.GetSources());
    }

    [TestMethod]
    public void PublicControlHasWinUIExMapsIdentity()
    {
        Type controlType = typeof(global::WinUIEx.Maps.MapControl);

        Assert.AreEqual("WinUIEx.Maps.MapControl", controlType.FullName);
        Assert.AreEqual("WinUIEx.Maps", controlType.Assembly.GetName().Name);
        Assert.IsNull(controlType.Assembly.GetType("MapControl.MapControl"));
    }

    [TestMethod]
    public void KeyEventsExposeStableIdsNamesAndPayloads()
    {
        using TestEventListener listener = new();
        listener.Enable();

        MapControlEventSource.Log.TileWaveStart(42, 7, 2, 12, 20, 8, 4, 8);
        MapControlEventSource.Log.TileRequestFailed(
            12,
            654,
            1582,
            2,
            42,
            401,
            "ServiceResponse",
            "AzureMapsRequestException");
        MapControlEventSource.Log.TileCacheEvicted(3, 786432, 133169152);
        MapControlEventSource.Log.TileUploadCommitSummary(6, 1, 1);
        MapControlEventSource.Log.LayersChanged("LayersAdd", 2, 2, 100003);
        MapControlEventSource.Log.CustomTileLayerConfigured(true, 256, 0, 19, false);
        MapControlEventSource.Log.CustomTileRequestFailed(
            4, 3, 2, 9, 404, "Network", "HttpRequestException");
        MapControlEventSource.Log.TextureDisposalSummary(12, 3145728, 0);
        MapControlEventSource.Log.TilePipelineBacklog(24, 3, 2, 1, 5);
        MapControlEventSource.Log.TileRequestTiming(1, 24, 30, 2, 4, 36, 8, 8);
        MapControlEventSource.Log.TileUploadTiming(8, 8, 0, 3, 1, 5, 1);
        MapControlEventSource.Log.RasterCoverageMilestone(
            1, 24, 3, "OpaqueCoverage", 48, 48, 48, 1250, 12582912, 48);
        MapControlEventSource.Log.TileSchedulerSummary(1, 24, 3, 48, 48, 48, 8, 0, 900);
        MapControlEventSource.Log.CameraHeadingTargetChanged(315, true);
        MapControlEventSource.Log.CameraPitchTargetChanged(45, false);
        MapControlEventSource.Log.VectorTileCommitSummary(6, 4, 1, 120, 7, 18, 4096);
        MapControlEventSource.Log.VectorStyleAssetsLoaded(
            6, 36, 5, 1400, 1024, 1024, 250);
        MapControlEventSource.Log.VectorSymbolRenderBatch(
            6, 120, 96, 2, 3, 12, 12);
        MapControlEventSource.Log.VectorGlyphRangeLoaded(6, 220, 74511, 80);
        MapControlEventSource.Log.VectorLabelRenderBatch(
            6, 840, 810, 2, 4, 42, 42);
        MapControlEventSource.Log.VectorGlyphRangeUnavailable(
            6, 256, 400, "AzureMapsRequestException", 75);
        MapControlEventSource.Log.VectorLabelCollisionSummary(
            6, 120, 80, 40, 215);
        MapControlEventSource.Log.VectorLineRenderBatch(
            6, 400, 360, 7200, 2, 24);
        MapControlEventSource.Log.VectorLineFallbackSummary(
            6, 18, 12, 6, 3);
        MapControlEventSource.Log.VectorPolygonRenderBatch(
            6, 250, 220, 4800, 3, 4, 16);
        MapControlEventSource.Log.VectorGeometryFallbackOpacitySummary(
            6, 1, 18, 8, 2, 0.25, 1);
        MapControlEventSource.Log.VectorLineSymbolPlacementSummary(
            6, 240, 180, 150);
        MapControlEventSource.Log.VectorGeometryFrameCacheSummary(
            6, 1, 1, 750000, 12000000);
        MapControlEventSource.Log.VectorGeometryDeferredRebuildSummary(
            6, 1, 2, 96, -24);
        MapControlEventSource.Log.VectorGeometryPreparationSummary(
            6, 1, 750000, 120000, 85.5, 4.25);
        MapControlEventSource.Log.VectorLabelTextureReadinessSummary(
            6, 12, 84);
        MapControlEventSource.Log.VectorLabelFadeSummary(
            6, 8, 42);
        MapControlEventSource.Log.VectorLineDecorationSummary(
            6, 2, 14, 112);
        MapControlEventSource.Log.VectorPolygonDecorationSummary(
            6, 9, 180, 72);
        MapControlEventSource.Log.VectorAdvancedLineStyleSummary(
            6, 3, 4, 5, 6, 7);
        MapControlEventSource.Log.VectorAdvancedSymbolStyleSummary(
            6, 8, 9, 10, 11, 12);
        MapControlEventSource.Log.CameraViewChangeRequested(1, true, false, true);

        CapturedEvent wave = listener.Single(11);
        Assert.AreEqual("TileWaveStart", wave.Name);
        Assert.AreSequenceEqual(
            ["generation", "sceneVersion", "style", "tileZoom", "requiredCount",
             "cacheHitCount", "pendingCount", "batchCount"],
            wave.PayloadNames);
        Assert.AreEqual(42L, wave.Payload[0]);

        CapturedEvent failure = listener.Single(13);
        Assert.AreEqual("TileRequestFailed", failure.Name);
        Assert.AreSequenceEqual(
            ["zoom", "x", "y", "style", "generation", "statusCode", "failureKind",
             "exceptionType"],
            failure.PayloadNames);
        Assert.AreEqual(42L, failure.Payload[4]);
        Assert.AreEqual(401, failure.Payload[5]);
        Assert.AreEqual("ServiceResponse", failure.Payload[6]);

        CapturedEvent eviction = listener.Single(20);
        Assert.AreEqual("TileCacheEvicted", eviction.Name);
        Assert.AreEqual(3, eviction.Payload[0]);

        CapturedEvent commit = listener.Single(31);
        Assert.AreEqual("TileUploadCommitSummary", commit.Name);
        Assert.AreSequenceEqual(
            ["acceptedCount", "staleDroppedCount", "duplicateDroppedCount"],
            commit.PayloadNames);
        Assert.AreEqual(6, commit.Payload[0]);

        CapturedEvent layers = listener.Single(32);
        Assert.AreEqual("LayersChanged", layers.Name);
        Assert.AreSequenceEqual(
            ["operation", "layerCount", "mapElementsLayerCount", "elementCount"],
            layers.PayloadNames);
        Assert.AreEqual("LayersAdd", layers.Payload[0]);
        Assert.AreEqual(2, layers.Payload[1]);
        Assert.AreEqual(100003, layers.Payload[3]);

        CapturedEvent customConfiguration = listener.Single(33);
        Assert.AreEqual("CustomTileLayerConfigured", customConfiguration.Name);
        Assert.AreSequenceEqual(
            ["added", "tileSize", "minimumSourceZoom", "maximumSourceZoom", "isTms"],
            customConfiguration.PayloadNames);
        CapturedEvent customFailure = listener.Single(37);
        Assert.AreEqual("CustomTileRequestFailed", customFailure.Name);
        Assert.DoesNotContain(
            name => name.Contains("url", StringComparison.OrdinalIgnoreCase),
            customFailure.PayloadNames);
        CapturedEvent disposal = listener.Single(41);
        Assert.AreEqual("TextureDisposalSummary", disposal.Name);
        Assert.AreSequenceEqual(
            ["disposedCount", "disposedBytes", "remainingCount"],
            disposal.PayloadNames);
        Assert.AreEqual(12, disposal.Payload[0]);
        Assert.AreEqual(3145728L, disposal.Payload[1]);
        Assert.AreEqual(0, disposal.Payload[2]);
        CapturedEvent backlog = listener.Single(42);
        Assert.AreEqual("TilePipelineBacklog", backlog.Name);
        Assert.AreSequenceEqual(
            ["generation", "decodedQueueCount", "completedQueueCount",
             "disposalQueueCount", "occupiedUploadSlots"],
            backlog.PayloadNames);
        Assert.AreEqual(24L, backlog.Payload[0]);
        Assert.AreEqual(5, backlog.Payload[4]);
        CapturedEvent requestTiming = listener.Single(43);
        Assert.AreSequenceEqual(
            ["sourceKind", "generation", "downloadMilliseconds", "decodeMilliseconds",
             "uploadWaitMilliseconds", "totalMilliseconds", "activeRequests", "peakRequests"],
            requestTiming.PayloadNames);
        CapturedEvent uploadTiming = listener.Single(44);
        Assert.AreSequenceEqual(
            ["uploadedCount", "queueStartCount", "queueRemainingCount",
             "textureCreateMilliseconds", "renderLockWaitMilliseconds",
             "totalMilliseconds", "renderWakeCount"],
            uploadTiming.PayloadNames);
        CapturedEvent coverage = listener.Single(45);
        Assert.AreEqual("OpaqueCoverage", coverage.Payload[3]);
        Assert.DoesNotContain(
            name => name.Contains("url", StringComparison.OrdinalIgnoreCase),
            coverage.PayloadNames);
        CapturedEvent scheduler = listener.Single(46);
        Assert.AreEqual(8, scheduler.Payload[6]);
        CapturedEvent heading = listener.Single(47);
        Assert.AreSequenceEqual(
            ["heading", "isImmediate"],
            heading.PayloadNames);
        Assert.AreEqual(315d, heading.Payload[0]);
        Assert.AreEqual(true, heading.Payload[1]);
        CapturedEvent pitch = listener.Single(48);
        Assert.AreSequenceEqual(
            ["pitch", "isImmediate"],
            pitch.PayloadNames);
        Assert.AreEqual(45d, pitch.Payload[0]);
        Assert.AreEqual(false, pitch.Payload[1]);
        CapturedEvent vectorCommit = listener.Single(49);
        Assert.AreSequenceEqual(
            ["style", "acceptedCount", "staleDroppedCount", "acceptedPointCount",
             "preparedSpriteCount", "cacheEntryCount", "cacheBytes"],
            vectorCommit.PayloadNames);
        Assert.AreEqual(120, vectorCommit.Payload[3]);
        CapturedEvent styleAssets = listener.Single(50);
        Assert.AreSequenceEqual(
            ["style", "symbolLayerCount", "unsupportedLayerCount",
             "spriteEntryCount", "atlasWidth", "atlasHeight",
             "durationMilliseconds"],
            styleAssets.PayloadNames);
        Assert.AreEqual(5, styleAssets.Payload[2]);
        CapturedEvent vectorBatch = listener.Single(51);
        Assert.AreSequenceEqual(
            ["style", "candidateCount", "drawableCount", "evaluationFailureCount",
             "unavailableSpriteCount", "textureBatchCount", "drawCallCount"],
            vectorBatch.PayloadNames);
        Assert.AreEqual(96, vectorBatch.Payload[2]);
        CapturedEvent glyphRange = listener.Single(52);
        Assert.AreSequenceEqual(
            ["style", "glyphCount", "encodedByteCount", "durationMilliseconds"],
            glyphRange.PayloadNames);
        Assert.AreEqual(220, glyphRange.Payload[1]);
        CapturedEvent labelBatch = listener.Single(53);
        Assert.AreSequenceEqual(
            ["style", "candidateGlyphCount", "drawableGlyphCount",
             "evaluationFailureCount", "unavailableGlyphCount",
             "textureBatchCount", "drawCallCount"],
            labelBatch.PayloadNames);
        Assert.AreEqual(810, labelBatch.Payload[2]);
        CapturedEvent unavailableGlyphRange = listener.Single(54);
        Assert.AreSequenceEqual(
            ["style", "rangeStart", "statusCode", "exceptionType",
             "durationMilliseconds"],
            unavailableGlyphRange.PayloadNames);
        Assert.AreEqual(256, unavailableGlyphRange.Payload[1]);
        Assert.AreEqual(400, unavailableGlyphRange.Payload[2]);
        CapturedEvent labelCollisions = listener.Single(55);
        Assert.AreSequenceEqual(
            ["style", "candidateLabelCount", "acceptedLabelCount",
             "suppressedLabelCount", "suppressedGlyphCount"],
            labelCollisions.PayloadNames);
        Assert.AreEqual(120, labelCollisions.Payload[1]);
        Assert.AreEqual(80, labelCollisions.Payload[2]);
        Assert.AreEqual(40, labelCollisions.Payload[3]);
        Assert.AreEqual(215, labelCollisions.Payload[4]);
        CapturedEvent vectorLines = listener.Single(56);
        Assert.AreSequenceEqual(
            ["style", "candidateLineCount", "drawableLineCount", "triangleCount",
             "evaluationFailureCount", "drawCallCount"],
            vectorLines.PayloadNames);
        Assert.AreEqual(400, vectorLines.Payload[1]);
        Assert.AreEqual(360, vectorLines.Payload[2]);
        Assert.AreEqual(7200, vectorLines.Payload[3]);
        CapturedEvent lineFallback = listener.Single(57);
        Assert.AreSequenceEqual(
            ["style", "candidateInstanceCount", "drawnInstanceCount",
             "suppressedDistantInstanceCount", "maximumZoomDifference"],
            lineFallback.PayloadNames);
        Assert.AreEqual(18, lineFallback.Payload[1]);
        Assert.AreEqual(12, lineFallback.Payload[2]);
        Assert.AreEqual(6, lineFallback.Payload[3]);
        Assert.AreEqual(3d, lineFallback.Payload[4]);
        CapturedEvent vectorPolygons = listener.Single(58);
        Assert.AreSequenceEqual(
            ["style", "candidatePolygonCount", "drawablePolygonCount",
             "triangleCount", "evaluationFailureCount",
             "suppressedFallbackInstanceCount", "drawCallCount"],
            vectorPolygons.PayloadNames);
        Assert.AreEqual(250, vectorPolygons.Payload[1]);
        Assert.AreEqual(220, vectorPolygons.Payload[2]);
        Assert.AreEqual(4800, vectorPolygons.Payload[3]);
        CapturedEvent fallbackOpacity = listener.Single(59);
        Assert.AreSequenceEqual(
            ["style", "geometryKind", "fallbackInstanceCount",
             "fadedInstanceCount", "suppressedInstanceCount",
             "minimumOpacity", "maximumOpacity"],
            fallbackOpacity.PayloadNames);
        Assert.AreEqual(1, fallbackOpacity.Payload[1]);
        Assert.AreEqual(8, fallbackOpacity.Payload[3]);
        Assert.AreEqual(0.25d, fallbackOpacity.Payload[5]);
        CapturedEvent lineSymbols = listener.Single(60);
        Assert.AreSequenceEqual(
            ["style", "candidateComponentCount", "projectedComponentCount",
             "drawnComponentCount"],
            lineSymbols.PayloadNames);
        Assert.AreEqual(240, lineSymbols.Payload[1]);
        Assert.AreEqual(180, lineSymbols.Payload[2]);
        Assert.AreEqual(150, lineSymbols.Payload[3]);
        CapturedEvent geometryFrameCache = listener.Single(61);
        Assert.AreSequenceEqual(
            ["style", "geometryKind", "reused", "vertexCount", "retainedBytes"],
            geometryFrameCache.PayloadNames);
        Assert.AreEqual(1, geometryFrameCache.Payload[1]);
        Assert.AreEqual(1, geometryFrameCache.Payload[2]);
        Assert.AreEqual(750000, geometryFrameCache.Payload[3]);
        Assert.AreEqual(12000000L, geometryFrameCache.Payload[4]);
        CapturedEvent deferredGeometry = listener.Single(62);
        Assert.AreSequenceEqual(
            ["style", "geometryKind", "pendingTileCount", "offsetX", "offsetY"],
            deferredGeometry.PayloadNames);
        Assert.AreEqual(2, deferredGeometry.Payload[2]);
        Assert.AreEqual(96d, deferredGeometry.Payload[3]);
        Assert.AreEqual(-24d, deferredGeometry.Payload[4]);
        CapturedEvent preparedGeometry = listener.Single(63);
        Assert.AreSequenceEqual(
            ["style", "accepted", "lineVertexCount", "polygonVertexCount",
             "preparationMilliseconds", "uploadMilliseconds"],
            preparedGeometry.PayloadNames);
        Assert.AreEqual(1, preparedGeometry.Payload[1]);
        Assert.AreEqual(750000, preparedGeometry.Payload[2]);
        Assert.AreEqual(120000, preparedGeometry.Payload[3]);
        Assert.AreEqual(85.5d, preparedGeometry.Payload[4]);
        CapturedEvent labelReadiness = listener.Single(64);
        Assert.AreSequenceEqual(
            ["style", "pendingLabelCount", "pendingGlyphCount"],
            labelReadiness.PayloadNames);
        Assert.AreEqual(12, labelReadiness.Payload[1]);
        Assert.AreEqual(84, labelReadiness.Payload[2]);
        CapturedEvent labelFade = listener.Single(65);
        Assert.AreSequenceEqual(
            ["style", "fadingLabelCount", "fadingGlyphCount"],
            labelFade.PayloadNames);
        Assert.AreEqual(8, labelFade.Payload[1]);
        Assert.AreEqual(42, labelFade.Payload[2]);
        CapturedEvent lineDecoration = listener.Single(66);
        Assert.AreSequenceEqual(
            ["style", "decorationKind", "candidateLineCount",
             "drawablePrimitiveCount"],
            lineDecoration.PayloadNames);
        Assert.AreEqual(2, lineDecoration.Payload[1]);
        Assert.AreEqual(14, lineDecoration.Payload[2]);
        Assert.AreEqual(112, lineDecoration.Payload[3]);
        CapturedEvent polygonDecoration = listener.Single(67);
        Assert.AreSequenceEqual(
            ["style", "patternedPolygonCount", "patternTriangleCount",
             "outlineTriangleCount"],
            polygonDecoration.PayloadNames);
        Assert.AreEqual(9, polygonDecoration.Payload[1]);
        Assert.AreEqual(180, polygonDecoration.Payload[2]);
        Assert.AreEqual(72, polygonDecoration.Payload[3]);
        CapturedEvent advancedLine = listener.Single(68);
        Assert.AreSequenceEqual(
            ["style", "offsetLineCount", "gapLineCount",
             "gradientLineCount", "blurredLineCount", "miterLineCount"],
            advancedLine.PayloadNames);
        Assert.AreEqual(3, advancedLine.Payload[1]);
        Assert.AreEqual(4, advancedLine.Payload[2]);
        Assert.AreEqual(5, advancedLine.Payload[3]);
        Assert.AreEqual(6, advancedLine.Payload[4]);
        Assert.AreEqual(7, advancedLine.Payload[5]);
        CapturedEvent advancedSymbol = listener.Single(69);
        Assert.AreSequenceEqual(
            ["style", "rotatedIconCount", "tintedIconCount",
             "fittedIconCount", "sortedSymbolCount",
             "collisionOverrideSymbolCount"],
            advancedSymbol.PayloadNames);
        Assert.AreEqual(8, advancedSymbol.Payload[1]);
        Assert.AreEqual(9, advancedSymbol.Payload[2]);
        Assert.AreEqual(10, advancedSymbol.Payload[3]);
        Assert.AreEqual(11, advancedSymbol.Payload[4]);
        Assert.AreEqual(12, advancedSymbol.Payload[5]);
        CapturedEvent viewChange = listener.Single(70);
        Assert.AreEqual("CameraViewChangeRequested", viewChange.Name);
        Assert.AreSequenceEqual(
            ["animationKind", "hasZoomLevel", "hasHeading", "hasPitch"],
            viewChange.PayloadNames);
        Assert.AreEqual(1, viewChange.Payload[0]);
        Assert.AreEqual(true, viewChange.Payload[1]);
        Assert.AreEqual(false, viewChange.Payload[2]);
        Assert.AreEqual(true, viewChange.Payload[3]);
        Assert.AreEqual(4.25d, preparedGeometry.Payload[5]);
        Assert.DoesNotContain(captured => captured.Id == 0, listener.Events);
    }

    [TestMethod]
    public void EventIdsAreUniqueAndContiguous()
    {
        EventAttribute[] events = typeof(MapControlEventSource)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<EventAttribute>())
            .OfType<EventAttribute>()
            .ToArray();

        Assert.AreSequenceEqual(
            Enumerable.Range(1, 70),
            events.Select(attribute => attribute.EventId).Order());
        Assert.AreEqual(events.Length, events.Select(attribute => attribute.EventId).Distinct().Count());
    }

    [TestMethod]
    public void ErrorEventsDoNotExposeTokenOrUrlPayloads()
    {
        const string secret = "SECRET-MAP-TOKEN-DO-NOT-LOG";
        using TestEventListener listener = new();
        listener.Enable();

        MapControlEventSource.Log.TileRequestFailed(
            4,
            3,
            2,
            (int)MapStyle.Road,
            17,
            0,
            "Network",
            typeof(HttpRequestException).FullName!);
        MapControlEventSource.Log.AttributionRequestFailed(
            (int)MapStyle.Road,
            4,
            403,
            "ServiceResponse",
            "AzureMapsRequestException");
        MapControlEventSource.Log.CustomTileRequestFailed(
            4,
            3,
            2,
            17,
            0,
            "Network",
            typeof(HttpRequestException).FullName!);

        MethodInfo[] eventMethods = typeof(MapControlEventSource)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<EventAttribute>() is not null)
            .ToArray();
        Assert.DoesNotContain(
            parameter =>
                parameter.Name!.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("sourceLayer", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("propertyName", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Contains("spriteName", StringComparison.OrdinalIgnoreCase),
            eventMethods.SelectMany(method => method.GetParameters()));

        string payloadText = string.Join(
            "|",
            listener.Events.SelectMany(captured => captured.Payload)
                .Select(value => value?.ToString() ?? string.Empty));
        Assert.DoesNotContain(secret, payloadText, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RemovedMapServiceErrorApiIsNotPublic()
    {
        Type controlType = typeof(global::WinUIEx.Maps.MapControl);

        Assert.IsNull(controlType.GetEvent("MapServiceErrorOccurred"));
        Assert.IsNull(controlType.Assembly.GetType(
            "MapControl.MapControlMapServiceErrorOccurredEventArgs"));
    }

    [TestMethod]
    public void MapControlDoesNotExposeDisposalState()
    {
        Type controlType = typeof(global::WinUIEx.Maps.MapControl);

        Assert.IsFalse(typeof(IDisposable).IsAssignableFrom(controlType));
        Assert.IsNull(controlType.GetMethod(
            "Dispose",
            BindingFlags.Instance | BindingFlags.Public));
    }

    [TestMethod]
    public void XamlRootChangedSubscriptionDoesNotRetainControlOrRoot()
    {
        Type controlType = typeof(global::WinUIEx.Maps.MapControl);
        Type subscriptionType = controlType.GetNestedType(
            "WeakXamlRootChangedSubscription",
            BindingFlags.NonPublic)!;

        Assert.IsNotNull(subscriptionType);
        FieldInfo[] fields = subscriptionType.GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            field => field.FieldType == controlType ||
                field.FieldType == typeof(Microsoft.UI.Xaml.XamlRoot),
            fields);
    }

    [TestMethod]
    public void MapIconServiceDoesNotOwnXamlRoot()
    {
        Type serviceType = typeof(global::WinUIEx.Maps.MapControl).Assembly.GetType(
            "WinUIEx.Maps.MapIconService")!;
        FieldInfo[] fields = serviceType.GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo[] methods = serviceType.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            field => ContainsXamlRoot(field.FieldType),
            fields);
        Assert.DoesNotContain(
            method => ContainsXamlRoot(method.ReturnType) ||
                method.GetParameters().Any(parameter =>
                    ContainsXamlRoot(parameter.ParameterType)),
            methods);
    }

    private static bool ContainsXamlRoot(Type type) =>
        type == typeof(Microsoft.UI.Xaml.XamlRoot) ||
        type.GenericTypeArguments.Any(ContainsXamlRoot);

    private sealed class TestEventListener : EventListener
    {
        private readonly ConcurrentQueue<CapturedEvent> _events = new();

        internal IEnumerable<CapturedEvent> Events => _events;

        internal void Enable()
        {
            EnableEvents(
                MapControlEventSource.Log,
                EventLevel.Verbose,
                EventKeywords.All);
        }

        internal CapturedEvent Single(int eventId)
        {
            CapturedEvent[] matches = _events.Where(captured => captured.Id == eventId).ToArray();
            Assert.IsTrue(
                matches.Length == 1,
                $"Expected event {eventId}; captured: {string.Join(
                    "; ",
                    _events.Select(captured =>
                        $"{captured.Id}:{captured.Name} [{string.Join(", ", captured.Payload)}]"))}");
            return matches[0];
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != MapControlEventSource.ProviderName)
            {
                return;
            }

            _events.Enqueue(new CapturedEvent(
                eventData.EventId,
                eventData.EventName ?? string.Empty,
                eventData.PayloadNames?.ToArray() ?? [],
                eventData.Payload?.ToArray() ?? []));
        }
    }

    private sealed record CapturedEvent(
        int Id,
        string Name,
        IReadOnlyList<string> PayloadNames,
        IReadOnlyList<object?> Payload);
}
