namespace AlgorithmComparison.Core.Results;

public sealed class AlgorithmMetrics
{
    public required string AlgorithmName { get; init; }
    public required double QueryTimeMs { get; init; }
    public required long AllocatedBytes { get; init; }
    public required int StopsSettled { get; init; }
    public required int TripsOrConnectionsScanned { get; init; }
    public int Relaxations { get; init; }
    public int RaptorRounds { get; init; }
}
