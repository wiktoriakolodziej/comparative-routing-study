namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsTrip
{
    public required string TripId { get; init; }
    public required string RouteId { get; init; }
    public required string ServiceId { get; init; }
    public string TripHeadsign { get; init; } = string.Empty;
}
