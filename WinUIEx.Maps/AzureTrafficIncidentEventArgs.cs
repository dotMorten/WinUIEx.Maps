using Windows.Devices.Geolocation;
using Windows.Foundation;

namespace WinUIEx.Maps;

/// <summary>Describes an Azure traffic incident selected on the map.</summary>
public sealed class AzureTrafficIncidentEventArgs : EventArgs
{
    internal AzureTrafficIncidentEventArgs(
        Geopoint location, string? id, int? category, int? magnitude,
        string? description = null, TimeSpan? delay = null, string? title = null,
        string? incidentType = null, Point position = default,
        DateTimeOffset? startTime = null, DateTimeOffset? endTime = null)
    {
        Location = location;
        Id = id;
        Category = category;
        Magnitude = magnitude;
        Description = description;
        Delay = delay;
        Title = title;
        IncidentType = incidentType;
        Position = position;
        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>Gets the geographic location at which the incident was selected.</summary>
    public Geopoint Location { get; }

    /// <summary>Gets the tap position in device-independent pixels relative to the map control.</summary>
    public Point Position { get; }

    /// <summary>Gets the service incident identifier, when supplied by the tile.</summary>
    public string? Id { get; }

    /// <summary>Gets the service incident category code, when supplied.</summary>
    public int? Category { get; }

    /// <summary>Gets the service incident magnitude code, when supplied.</summary>
    public int? Magnitude { get; }

    /// <summary>Gets the incident description supplied by the tile, when available.</summary>
    /// <remarks>This is display text, not markup, and must not be included in diagnostics.</remarks>
    public string? Description { get; }

    /// <summary>Gets the reported traffic delay, when supplied by the tile.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>Gets the incident or road title supplied by the tile, when available.</summary>
    public string? Title { get; }

    /// <summary>Gets the service incident type name, when supplied by the tile.</summary>
    public string? IncidentType { get; }

    /// <summary>Gets the reported start time, when supplied as an unambiguous timestamp by the tile.</summary>
    public DateTimeOffset? StartTime { get; }

    /// <summary>Gets the estimated end time, when supplied as an unambiguous timestamp by the tile.</summary>
    public DateTimeOffset? EndTime { get; }

    /// <summary>Gets or sets whether the underlying tap should stop routing to the map's other handlers.</summary>
    public bool Handled { get; set; }
}
