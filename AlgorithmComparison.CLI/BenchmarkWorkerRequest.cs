namespace AlgorithmComparison.CLI;

public sealed record BenchmarkWorkerRequest
{
    public required string DatasetPath { get; init; }
    public required string CityName { get; init; }
    public required string TransferCategory { get; init; }
    public required string ServiceDate { get; init; }
    public double WalkingRadiusMeters { get; init; } = 500;
    public required string FootpathGraph { get; init; }
    public required string Algorithm { get; init; }
    public required string Mode { get; init; }
    public required int MasterSeed { get; init; }
    public required int DatasetSeed { get; init; }
    public int QueryCount { get; init; }
    public required int MaxTransfers { get; init; }
    public required int WarmupCount { get; init; }
    public required int Repetitions { get; init; }
    public required string OutputPath { get; init; }
    public required IReadOnlyList<BenchmarkWorkerQuery> Queries { get; init; }
}

public sealed record BenchmarkWorkerQuery(
    int Index,
    string TimeWindow,
    string FromStopId,
    string ToStopId,
    int DepartureTime);

public sealed record BenchmarkWorkerResult
{
    public required string CityName { get; init; }
    public required string TransferCategory { get; init; }
    public required string ServiceDate { get; init; }
    public double WalkingRadiusMeters { get; init; } = 500;
    public required string FootpathGraph { get; init; }
    public required string Algorithm { get; init; }
    public required string Mode { get; init; }
    public required int MasterSeed { get; init; }
    public required int DatasetSeed { get; init; }
    public int QueryCount { get; init; }
    public int WarmupCount { get; init; }
    public int Repetitions { get; init; }
    public int MaxTransfers { get; init; }
    public double? NetworkLoadMs { get; init; }
    public double? FootpathGraphBuildMs { get; init; }
    public double? PrepareWallMs { get; init; }
    public double? AlgorithmPreprocessingMs { get; init; }
    public long? WorkingSetAfterPrepareBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public required IReadOnlyList<BenchmarkWorkerSample> Samples { get; init; }
}

public sealed record BenchmarkWorkerSample
{
    public required int QueryIndex { get; init; }
    public required string TimeWindow { get; init; }
    public required string FromStopId { get; init; }
    public required string ToStopId { get; init; }
    public required int DepartureTime { get; init; }
    public required int Repetition { get; init; }
    public required bool IsWarmup { get; init; }
    public string? Signature { get; init; }
    public AlgorithmComparison.Core.Results.AlgorithmMetrics? Metrics { get; init; }
    public string? Error { get; init; }
}
