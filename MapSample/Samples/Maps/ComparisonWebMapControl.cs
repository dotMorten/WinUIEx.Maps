using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace MapSample.Samples.Maps;

// Sample-only workaround for WinUI's initial zoom reset and reversed updateCenter arguments.
public sealed partial class ComparisonWebMapControl : MapControl
{
    // Keep aligned with AzureVectorStyleProvider.GetAssetPaths.
    internal const string RoadStyleVersion = "2023-01-01";
    private WebView2? _webView;
    private bool _ready;

    public event EventHandler? InitialViewFailed;
    internal event EventHandler? InitialViewApplied;
    internal event EventHandler<ComparisonCamera>? CameraChanged;
    internal event EventHandler<string>? StatusChanged;

    internal async Task<bool> SetCameraAsync(ComparisonCamera camera)
    {
        if (!_ready || _webView is null)
            return false;
        return await ExecuteAsync(_webView, FormattableString.Invariant($$"""
            window.comparisonSetCamera({
                center: [{{camera.Longitude}}, {{camera.Latitude}}],
                zoom: {{camera.ZoomLevel - 1}},
                bearing: {{camera.Heading}}, pitch: {{camera.Pitch}}, type: "jump"
            });
            """));
    }

    protected override void OnApplyTemplate()
    {
        if (_webView is not null)
        {
            _webView.NavigationCompleted -= OnNavigationCompleted;
            _webView.WebMessageReceived -= OnWebMessageReceived;
        }
        base.OnApplyTemplate();
        _ready = false;
        _webView = GetTemplateChild("PART_WebView2") as WebView2;
        if (_webView is null)
        {
            InitialViewFailed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.WebMessageReceived += OnWebMessageReceived;
        StatusChanged?.Invoke(this, "Waiting for map navigation");
    }

    private async void OnNavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        StatusChanged?.Invoke(this, "Initializing comparison camera");
        if (!args.IsSuccess || Center is null)
        {
            InitialViewFailed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var position = Center.Position;
        // Navigation handlers can run before or after WinUI queues initializeMap.
        // Hook that function if needed, then wait for Azure's ready event.
        string script = FormattableString.Invariant($$"""
            (() => {
                const report = (success, stage) => window.chrome.webview.postMessage({
                    type: "comparison-initial-view", success, stage
                });
                let applying = false;
                window.comparisonSetCamera = camera => {
                    try {
                        applying = true;
                        map.setCamera(camera);
                        return true;
                    } finally { applying = false; }
                };
                const applyInitialCamera = () => {
                    try {
                        window.comparisonSetCamera({
                            center: [{{position.Longitude}}, {{position.Latitude}}],
                            zoom: {{ZoomLevel}}, bearing: 0, pitch: 0, type: "jump"
                        });
                        const camera = map.getCamera();
                        const worldSize = 512 * Math.pow(2, camera.zoom);
                        const worldY = latitude => {
                            const sin = Math.sin(latitude * Math.PI / 180);
                            return 0.5 - Math.log((1 + sin) / (1 - sin)) / (4 * Math.PI);
                        };
                        // Azure rounds the center; compare projected error, not exact degrees.
                        report(Math.abs(camera.center[0] - {{position.Longitude}}) / 360 * worldSize < 0.5 &&
                               Math.abs(worldY(camera.center[1]) - worldY({{position.Latitude}})) * worldSize < 0.5 &&
                               Math.abs(camera.zoom - {{ZoomLevel}}) < 0.000001, "camera");
                        map.events.add("move", () => {
                            if (applying) return;
                            const current = map.getCamera();
                            window.chrome.webview.postMessage({
                                type: "comparison-camera",
                                longitude: current.center[0], latitude: current.center[1],
                                zoom: current.zoom, heading: current.bearing, pitch: current.pitch
                            });
                        });
                    } catch { report(false, "ready"); }
                };
                const attach = () => {
                    map.setServiceOptions({
                        styleDefinitionsVersion: "{{RoadStyleVersion}}",
                        styleAPIVersion: "2.0"
                    });
                    map.events.addOnce("ready", applyInitialCamera);
                };
                try {
                    if (typeof map !== "undefined" && map) { attach(); }
                    else {
                        const initialize = window.initializeMap;
                        window.initializeMap = function(...args) {
                            try {
                                const result = initialize.apply(this, args);
                                attach();
                                return result;
                            } catch { report(false, "initialize"); }
                        };
                    }
                } catch { report(false, "hook"); }
                return true;
            })();
            """);
        await ExecuteAsync(sender, script);
    }

    private async Task<bool> ExecuteAsync(WebView2 sender, string script)
    {
        try
        {
            string result = await sender.ExecuteScriptAsync(script);
            if (result == "true")
                return true;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
        }
        if (IsLoaded && ReferenceEquals(sender, _webView))
            InitialViewFailed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private void OnWebMessageReceived(
        WebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson);
        JsonElement message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out JsonElement type) ||
            type.ValueKind != JsonValueKind.String)
            return;

        if (type.GetString() == "comparison-initial-view" &&
            message.TryGetProperty("success", out JsonElement success))
        {
            if (success.ValueKind == JsonValueKind.True)
            {
                _ready = true;
                StatusChanged?.Invoke(this, $"Ready (road style {RoadStyleVersion})");
                InitialViewApplied?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                string stage = message.TryGetProperty("stage", out JsonElement stageValue) &&
                    stageValue.ValueKind == JsonValueKind.String
                    ? stageValue.GetString() switch
                    {
                        "camera" => "camera",
                        "ready" => "ready",
                        "hook" => "hook",
                        "initialize" => "initialize",
                        _ => "unknown",
                    }
                    : "unknown";
                StatusChanged?.Invoke(this, $"Camera initialization failed ({stage})");
                InitialViewFailed?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (type.GetString() == "comparison-camera" &&
            TryGetNumber(message, "longitude", out double longitude) &&
            TryGetNumber(message, "latitude", out double latitude) &&
            TryGetNumber(message, "zoom", out double zoom) &&
            TryGetNumber(message, "heading", out double heading) &&
            TryGetNumber(message, "pitch", out double pitch))
        {
            CameraChanged?.Invoke(this, new ComparisonCamera(
                longitude, latitude, zoom + 1, heading, pitch));
        }
    }

    private static bool TryGetNumber(JsonElement message, string name, out double value)
    {
        value = 0;
        return message.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) && double.IsFinite(value);
    }
}

internal sealed record ComparisonCamera(
    double Longitude, double Latitude, double ZoomLevel, double Heading, double Pitch)
{
    internal bool Matches(ComparisonCamera other)
    {
        double worldSize = 256 * Math.Pow(2, ZoomLevel);
        double longitudeDistance = Math.Abs(Longitude - other.Longitude) % 360;
        double headingDistance = Math.Abs(Heading - other.Heading) % 360;
        return Math.Min(longitudeDistance, 360 - longitudeDistance) / 360 * worldSize < 0.5 &&
            Math.Abs(WorldY(Latitude) - WorldY(other.Latitude)) * worldSize < 0.5 &&
            Math.Abs(ZoomLevel - other.ZoomLevel) < 0.000001 &&
            Math.Min(headingDistance, 360 - headingDistance) < 0.000001 &&
            Math.Abs(Pitch - other.Pitch) < 0.000001;
    }

    private static double WorldY(double latitude)
    {
        double sin = Math.Sin(Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180);
        return 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI);
    }
}
