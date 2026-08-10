using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace WinUIEx.Maps;

/// <summary>
/// Represents a lightweight icon whose normalized anchor point is positioned at a
/// geographic location.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IconElement"/> and <see cref="Location"/> reference getters and setters
/// publish one internally consistent state and may be used from a worker thread. Creating,
/// reading, or changing visual properties on an <see cref="IconElement"/> remains a WinUI
/// UI-thread operation.
/// </para>
/// <para>
/// Icon elements are rasterized and uploaded as textures. Reuse the same unparented
/// <see cref="IconElement"/> instance across many icons to share one raster and GPU texture.
/// Changes to dependency properties on a referenced icon element automatically regenerate
/// the shared raster.
/// </para>
/// </remarks>
public sealed class MapIcon : MapElement
{
    private MapIconState _state;

    /// <summary>
    /// Initializes an icon with the XAML visual to rasterize and its geographic anchor.
    /// </summary>
    /// <param name="iconElement">
    /// The unparented XAML icon to rasterize. The instance may be shared by multiple map
    /// icons.
    /// </param>
    /// <param name="location">
    /// The location at which to place the icon's default center anchor point.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="iconElement"/> or <paramref name="location"/> is
    /// <see langword="null"/>.
    /// </exception>
    public MapIcon(IconElement iconElement, Geopoint location)
    {
        ArgumentNullException.ThrowIfNull(iconElement);
        ArgumentNullException.ThrowIfNull(location);
        BasicGeoposition position = location.Position;
        _state = new MapIconState(
            iconElement,
            location,
            position.Longitude,
            position.Latitude,
            new Point(0.5, 0.5));
    }

    /// <summary>
    /// Gets or sets the unparented XAML icon rasterized for this map icon.
    /// </summary>
    /// <remarks>
    /// An <see cref="IconElement"/> that already belongs to another XAML visual tree is
    /// unsupported. Reusing the same unparented instance across MapIcons shares one CPU
    /// raster and one GPU texture. Replacing this reference is atomic and worker-safe, but
    /// the referenced XAML object must be created and visually modified on its UI thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The assigned value is <see langword="null"/>.
    /// </exception>
    public IconElement IconElement
    {
        get => Volatile.Read(ref _state).IconElement;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            while (true)
            {
                MapIconState current = Volatile.Read(ref _state);
                if (ReferenceEquals(current.IconElement, value))
                {
                    return;
                }

                MapIconState updated = current with { IconElement = value };
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, updated, current),
                    current))
                {
                    break;
                }
            }
            OnChanged();
        }
    }

    /// <summary>
    /// Gets or sets the geographic location at which the icon's normalized anchor point is
    /// positioned.
    /// </summary>
    /// <remarks>
    /// Reading or replacing this reference is atomic and worker-safe. The control coalesces
    /// changes and updates its spatial index on the UI thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The assigned value is <see langword="null"/>.
    /// </exception>
    public Geopoint Location
    {
        get => Volatile.Read(ref _state).Location;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            BasicGeoposition position = value.Position;
            while (true)
            {
                MapIconState current = Volatile.Read(ref _state);
                if (ReferenceEquals(current.Location, value))
                {
                    return;
                }

                MapIconState updated = current with
                {
                    Location = value,
                    Longitude = position.Longitude,
                    Latitude = position.Latitude,
                };
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, updated, current),
                    current))
                {
                    break;
                }
            }

            OnChanged();
        }
    }

    /// <summary>
    /// Gets or sets the anchor point of the map icon. The anchor point is the point on the
    /// icon that is positioned at the point on the map specified by the
    /// <see cref="Location"/> property.
    /// </summary>
    /// <remarks>
    /// When you display a map icon image that points to a specific location on the map - for
    /// example, a pushpin or an arrow - consider setting this value to the approximate
    /// location of the pointer on the image. The default value is (0.5, 0.5), which
    /// represents the center of the image. If the image's pointer is elsewhere, leaving the
    /// default unchanged may leave the image pointing to a different location when the
    /// map's <see cref="MapControl.ZoomLevel"/> changes.
    /// </remarks>
    public Point NormalizedAnchorPoint
    {
        get => Volatile.Read(ref _state).NormalizedAnchorPoint;
        set
        {
            while (true)
            {
                MapIconState current = Volatile.Read(ref _state);
                if (current.NormalizedAnchorPoint.Equals(value))
                {
                    return;
                }

                MapIconState updated = current with { NormalizedAnchorPoint = value };
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, updated, current),
                    current))
                {
                    break;
                }
            }

            OnChanged();
        }
    }

    internal MapIconState GetState() => Volatile.Read(ref _state);
}

internal sealed record MapIconState(
    IconElement IconElement,
    Geopoint Location,
    double Longitude,
    double Latitude,
    Point NormalizedAnchorPoint);
