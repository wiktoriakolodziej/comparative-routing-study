namespace AlgorithmComparison.Validation;

public sealed class ValidationReport
{
    public required int TotalQueries { get; init; }
    public required int PassedQueries { get; init; }
    public required IReadOnlyList<ValidationFailure> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;
    public double PassRate => TotalQueries == 0 ? 1.0 : (double)PassedQueries / TotalQueries;
}
public sealed class ValidationFailure
{
    public required Core.Queries.RoutingQuery Query { get; init; }
    public required IReadOnlyDictionary<string, int> ArrivalTimeByAlgorithm { get; init; }
    public required IReadOnlyDictionary<string, Core.Results.RoutingResult> ResultByAlgorithm { get; init; }
    public required string Description { get; init; }
}
