namespace AlgorithmComparison.Core.Models;

public sealed class Connection
{
    public required string Id { get; init; }
    public required string DepartureStopId { get; init; }
    public required string ArrivalStopId { get; init; }
    public required int DepartureTime { get; init; }
    public required int ArrivalTime { get; init; }
    public required string TripId { get; init; }
    public required string RouteId { get; init; }
}
