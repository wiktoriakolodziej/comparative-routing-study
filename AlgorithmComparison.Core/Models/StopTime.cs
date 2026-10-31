namespace AlgorithmComparison.Core.Models;

public sealed class StopTime
{
    public required string TripId { get; init; }
    public required string StopId { get; init; }
    public required int StopSequence { get; init; }
    public required int ArrivalTime { get; init; }
    public required int DepartureTime { get; init; }
}
