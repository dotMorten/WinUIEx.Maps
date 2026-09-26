using Microsoft.UI.Xaml;
using WinUIEx.Maps.Rendering;

namespace WinUIEx.Maps;

/// <summary>Specifies the Azure weather imagery product.</summary>
public enum WeatherLayerKind
{
    /// <summary>Precipitation radar, with five-minute frames.</summary>
    Radar,
    /// <summary>Infrared cloud-temperature imagery, with ten-minute frames.</summary>
    Infrared,
}

/// <summary>Displays Azure radar or infrared imagery at a selected time or the latest frame.</summary>
/// <remarks>
/// Radar supports approximately 90 minutes of history and two hours of forecast.
/// Infrared supports three hours of history. Availability depends on Azure coverage.
/// A fixed timestamp is not advanced automatically; unavailable frames report service failures.
/// </remarks>
public sealed class AzureWeatherLayer : AzureTileLayer
{
    /// <summary>Initializes a radar layer displaying the latest imagery.</summary>
    public AzureWeatherLayer() { }

    /// <summary>Gets or sets the weather product. Defaults to Radar.</summary>
    public WeatherLayerKind Kind
    {
        get => (WeatherLayerKind)GetValue(KindProperty);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetValue(KindProperty, value);
        }
    }

    /// <summary>Identifies the Kind dependency property.</summary>
    public static readonly DependencyProperty KindProperty = RegisterAzureProperty(
        nameof(Kind), typeof(WeatherLayerKind), typeof(AzureWeatherLayer), WeatherLayerKind.Radar,
        static value => value is WeatherLayerKind kind && Enum.IsDefined(kind));

    /// <summary>
    /// Gets or sets the requested frame. Null displays latest imagery and refreshes on the
    /// product cadence. Azure rounds fixed timestamps to the nearest available time interval.
    /// </summary>
    public DateTimeOffset? Timestamp
    {
        get => (DateTimeOffset?)GetValue(TimestampProperty);
        set => SetValue(TimestampProperty, value);
    }

    /// <summary>Identifies the Timestamp dependency property.</summary>
    public static readonly DependencyProperty TimestampProperty = RegisterAzureProperty(
        nameof(Timestamp), typeof(DateTimeOffset?), typeof(AzureWeatherLayer), null);

    internal override TimeSpan? RefreshCadence => Timestamp is null ? Cadence(Kind) : null;

    internal static TimeSpan Cadence(WeatherLayerKind kind) =>
        TimeSpan.FromMinutes(kind == WeatherLayerKind.Radar ? 5 : 10);

    internal override IEnumerable<TileLayerSnapshot> CreateSnapshots(
        string token, string? language, DateTimeOffset now)
    {
        yield return CreateOverlaySnapshot(
            Kind == WeatherLayerKind.Radar
                ? "microsoft.weather.radar.main" : "microsoft.weather.infrared.main",
            token, language, now, Timestamp);
    }
}
