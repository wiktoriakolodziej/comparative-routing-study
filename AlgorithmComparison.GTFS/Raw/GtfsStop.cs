namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsStop
{
    public required string StopId { get; init; }
    public required string StopName { get; init; }
    public required double StopLat { get; init; }
    public required double StopLon { get; init; }
    public string? ParentStation { get; init; }
}
