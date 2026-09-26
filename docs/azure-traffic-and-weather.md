# Azure traffic and weather

`AzureTrafficLayer` and `AzureWeatherLayer` share the map's Azure token and automatic attribution.
Add them to `Layers` in the desired order; neither changes `MapStyle`.

## Add traffic and weather layers

```csharp
var weather = new AzureWeatherLayer { Kind = WeatherLayerKind.Radar, Opacity = 0.65 };
var traffic = new AzureTrafficLayer
{
    FlowStyle = TrafficFlowStyle.Relative,
    ShowIncidents = true,
    MinIncidentZoom = 12, // Default: incidents appear at city/neighborhood scale.
};
Map.Layers.Add(weather);
Map.Layers.Add(traffic); // Traffic and incidents above weather.
traffic.IncidentTapped += (_, e) =>
{
    ShowIncident(e.Description, e.Delay, e.Category, e.Location);
    e.Handled = true; // Do not also handle this as a general map tap.
};
```

## Traffic flow and road opacity

`Absolute` colors measured speed; `Relative` compares speed with free-flow conditions;
`RelativeDark` uses a dark palette; `Delay` highlights congestion only; `Reduced` requires
larger slowdowns to change color. Flow and incidents both use Azure vector tiles, decoded
and drawn as geometry, not traffic raster images. `RelativeDark` and `Reduced` are local
styles of `microsoft.traffic.relative`; `Reduced` uses lower congestion thresholds
(20%, 40%, and 60% of free-flow speed) rather than the raster reduced-sensitivity product.
Unknown flow data is gray, not assumed uncongested.

Traffic road lines use 50% opacity without fading incident icons. Flow and affected-incident
roads are composited together before applying opacity, so overlapping segments do not
become darker. This road-only opacity is currently an internal constant; inherited
`Opacity` still affects the whole layer, including incident icons.

## Incident visibility and interaction

`MinIncidentZoom` is an inclusive camera-zoom threshold (default **12**). Below it,
incident markers and affected roads are neither drawn, hit-tested, nor requested; flow
remains visible. Fractional zoom values are supported. Set it to `0` to allow incidents
at every supported zoom; valid values are finite numbers from `0` through `24`.

`IncidentTapped` includes the map-relative tap `Position` for anchoring a details popup.
Incident points use local, high-contrast pictograms for accidents, fog, dangerous
conditions, rain, ice, congestion, lane and road closures, roadworks, wind, flooding,
detours, mixed clusters, and breakdowns. The 32-DIP warning triangles use a shared high-DPI
sprite atlas, not a font or an additional service request. Major incidents (`magnitude = 3`)
use white pictograms on red; other or unspecified severities use dark pictograms on yellow.
Selection uses the numeric
`icon_category` or `icon_category_0` tile property and Azure's
[IconCategory mapping](https://learn.microsoft.com/rest/api/maps/traffic/get-traffic-incident-detail#iconcategory);
missing or unrecognized categories show a generic warning. The current Render API
does not document its MVT property schema, so unsupported values are not guessed.

Descriptions, delay, title, type, codes, identifiers, and start/end times are optional tile-supplied values;
the event is not a full incident-detail service response. Display descriptions as plain
text, never log them. WindowsMapsSample shows available incident information in a flyout
when an incident is clicked, without dropping a pin. Its callout shows the incident type,
highlighted delay, description, and available start/estimated-end times in local time.
`StartTime` and `EndTime` accept explicit-zone ISO timestamps from `startTime`/`endTime`
or `start_time`/`end_time`; ambiguous dates and numeric timestamps with unspecified units
are omitted. No separate incident-detail request is made.

## Weather frames and live refresh

Weather supports radar and infrared. A null `Timestamp` requests latest imagery, refreshing
radar every five minutes and infrared every ten minutes. Set `Timestamp` to a
`DateTimeOffset` for a fixed frame; callers may advance it themselves. Azure rounds radar
times to five-minute frames (about 90 minutes of history and two hours of forecast) and
infrared times to ten-minute frames (three hours of history). Coverage and availability
depend on the service. Traffic refreshes every minute. Refreshing stops for hidden,
transparent, or unloaded layers. Fixed weather frames do not auto-advance.
Live refreshes retain cached imagery while replacement tiles load; a failed refresh can
therefore leave older imagery visible. Changing the product, token, or fixed timestamp
clears incompatible cached content instead.

## Layer behavior and samples

All layer properties are UI-thread-only. `MapStyle.Blank` suppresses **all** built-in Azure
layers and requests; custom sources remain usable without a token. Azure overlays render
above the entire base map, including its labels; there is no insertion point within the
base-map style. See **Azure traffic** and **Azure weather** in the sample for flow,
incident, and timestamp controls. WindowsMapsSample also provides a **Traffic** switch
in its map-view picker, placing traffic below routes and application markers.

These layers use [Render Get Map Tile](https://learn.microsoft.com/en-us/rest/api/maps/render/get-map-tile)
and its required attribution endpoint, not the deprecated Traffic v1 endpoint.
