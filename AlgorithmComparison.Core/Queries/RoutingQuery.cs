namespace AlgorithmComparison.Core.Queries;

public sealed class RoutingQuery
{
    public required string SourceStopId { get; init; }
    public required string TargetStopId { get; init; }
    public required int DepartureTime { get; init; }
    public int? MaxTransfers { get; init; }
    public int? MaxWalkingSeconds { get; init; }
    public bool MultiCriteria { get; init; } = false;
}
