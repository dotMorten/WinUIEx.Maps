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

        Assert.AreSequenceEqual(Enumerable.Range(1, 48), events.Select(attribute => attribute.EventId).Order());
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
                parameter.Name.Contains("query", StringComparison.OrdinalIgnoreCase),
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
