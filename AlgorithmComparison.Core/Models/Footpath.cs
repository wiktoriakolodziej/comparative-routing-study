namespace AlgorithmComparison.Core.Models;

public sealed class Footpath
{
    public required string FromStopId { get; init; }
    public required string ToStopId { get; init; }
    public required int DurationSeconds { get; init; }
}
