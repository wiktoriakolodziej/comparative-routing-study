namespace AlgorithmComparison.Core.Models;

public sealed class Trip
{
    public required string Id { get; init; }
    public required string RouteId { get; init; }
    public required string ServiceId { get; init; }
    public string Headsign { get; init; } = string.Empty;
    public required IReadOnlyList<StopTime> StopTimes { get; init; }
}
