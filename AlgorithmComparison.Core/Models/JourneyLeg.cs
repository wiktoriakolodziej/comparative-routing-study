namespace AlgorithmComparison.Core.Models;

public sealed class JourneyLeg
{
    public required string FromStopId { get; init; }
    public required string ToStopId { get; init; }
    public required int DepartureTime { get; init; }
    public required int ArrivalTime { get; init; }
    public string? TripId { get; init; }
    public string? RouteId { get; init; }
    public bool IsWalk => TripId is null;
    public int Duration => ArrivalTime - DepartureTime;
}
