namespace AlgorithmComparison.Core.Models;

public sealed class Stop
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public string? ParentStopId { get; init; }
}
