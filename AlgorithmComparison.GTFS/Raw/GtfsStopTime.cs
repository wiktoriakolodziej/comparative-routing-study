namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsStopTime
{
    public required string TripId { get; init; }
    public required string StopId { get; init; }
    public required int StopSequence { get; init; }
    public required int ArrivalTime { get; init; }
    public required int DepartureTime { get; init; }
}
