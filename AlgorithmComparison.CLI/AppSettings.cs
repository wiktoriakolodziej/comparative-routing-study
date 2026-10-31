namespace AlgorithmComparison.CLI;

public sealed class AppSettings
{
    public required string GtfsPath { get; init; }
    public required DateOnly ServiceDate { get; init; }

}
