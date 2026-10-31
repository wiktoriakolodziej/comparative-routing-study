namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsRoute
{
    public required string RouteId { get; init; }
    public required string RouteShortName { get; init; }
    public string RouteLongName { get; init; } = string.Empty;
    public required int RouteType { get; init; }
}
