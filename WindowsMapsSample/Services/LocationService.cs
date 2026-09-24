using Windows.Devices.Geolocation;
using WindowsMapsSample.Models;

namespace WindowsMapsSample.Services;

internal interface ILocationService
{
    Task<Place> LocateAsync(CancellationToken cancellationToken);
}

internal sealed class LocationService : ILocationService
{
    public async Task<Place> LocateAsync(CancellationToken cancellationToken)
    {
        // Windows requires the permission request to originate on the foreground UI thread.
        var access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
        if (access != GeolocationAccessStatus.Allowed)
            throw new UnauthorizedAccessException("Location permission is not granted.");
        var position = await new Geolocator { DesiredAccuracy = PositionAccuracy.High }
            .GetGeopositionAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)).AsTask(cancellationToken);
        var point = position.Coordinate.Point.Position;
        return new Place("My location", $"Accuracy: {position.Coordinate.Accuracy:N0} m", point.Latitude, point.Longitude);
    }
}
