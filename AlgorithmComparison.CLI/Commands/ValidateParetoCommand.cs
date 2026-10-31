using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.Validation;

namespace AlgorithmComparison.CLI.Commands;

public sealed class ValidateParetoCommand
{
    private sealed record Outcome(RoutingResult? Result, string? Error, IReadOnlyList<string> Issues);
    private sealed record QueryCheck(
        RoutingQuery Query,
        IReadOnlyDictionary<string, Outcome> Outcomes,
        IReadOnlyList<string> Issues);

    private readonly IReadOnlyList<IRoutingAlgorithm> _algorithms;
    private readonly TransitNetwork _network;

    public ValidateParetoCommand(
        IReadOnlyList<IRoutingAlgorithm> algorithms,
        TransitNetwork network)
    {
        if (algorithms.Count < 2)
            throw new ArgumentException("At least two algorithms are required.", nameof(algorithms));

        _algorithms = algorithms;
        _network = network;
    }

    public int Execute(
        int queryCount = 19,
        int seed = 42,
        int maxTransfers = 7,
        string outputPath = "pareto-results.txt")
    {
        if (queryCount < 1)
            throw new ArgumentOutOfRangeException(nameof(queryCount), "Query count must be positive.");
        if (maxTransfers < 0)
            throw new ArgumentOutOfRangeException(nameof(maxTransfers), "Maximum transfers cannot be negative.");

        var workload = QueryWorkload.CreateRandom(_network, queryCount, seed);
        var checks = new List<QueryCheck>(queryCount);

        Console.WriteLine(
            $"Validating Pareto fronts from {_algorithms.Count} algorithms over {queryCount} queries " +
            $"(seed {seed}, max transfers {maxTransfers})...");

        for (var queryIndex = 0; queryIndex < workload.Queries.Count; queryIndex++)
        {
            var original = workload.Queries[queryIndex];
            var query = new RoutingQuery
            {
                SourceStopId = original.SourceStopId,
                TargetStopId = original.TargetStopId,
                DepartureTime = original.DepartureTime,
                MaxTransfers = maxTransfers,
                MaxWalkingSeconds = original.MaxWalkingSeconds,
                MultiCriteria = true
            };

            var outcomes = new Dictionary<string, Outcome>();
            foreach (var algorithm in _algorithms)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    Console.Write($"\r  Q{queryIndex + 1}/{queryCount}: {algorithm.Name}...");
                    Console.Out.Flush();
                    var result = algorithm.Route(query, _network);
                    stopwatch.Stop();
                    Console.WriteLine($" done in {stopwatch.Elapsed.TotalSeconds:F1}s");
                    var issues = ValidateResult(query, result, maxTransfers);
                    outcomes[algorithm.Name] = new Outcome(result, null, issues);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($" failed: {exception.GetType().Name}");
                    outcomes[algorithm.Name] = new Outcome(
                        null,
                        $"{exception.GetType().Name}: {exception.Message}",
                        []);
                }
            }

            var queryIssues = outcomes
                .Where(pair => pair.Value.Error is not null || pair.Value.Issues.Count > 0)
                .SelectMany(pair => pair.Value.Error is { } error
                    ? [$"{pair.Key} threw {error}"]
                    : pair.Value.Issues.Select(issue => $"{pair.Key}: {issue}"))
                .ToList();

            var successful = outcomes
                .Where(pair => pair.Value.Result is not null)
                .ToList();
            if (successful.Count > 1)
            {
                var reference = successful[0];
                var referencePoints = Points(reference.Value.Result!.ParetoJourneys);
                foreach (var candidate in successful.Skip(1))
                {
                    var candidatePoints = Points(candidate.Value.Result!.ParetoJourneys);
                    if (!referencePoints.SetEquals(candidatePoints))
                    {
                        var missing = referencePoints.Except(candidatePoints).OrderBy(point => point.Arrival).ThenBy(point => point.Transfers);
                        var extra = candidatePoints.Except(referencePoints).OrderBy(point => point.Arrival).ThenBy(point => point.Transfers);
                        queryIssues.Add(
                            $"Pareto front differs between {reference.Key} and {candidate.Key}; " +
                            $"missing from {candidate.Key}: [{string.Join(", ", missing.Select(FormatPoint))}], " +
                            $"only in {candidate.Key}: [{string.Join(", ", extra.Select(FormatPoint))}].");
                    }
                }
            }

            checks.Add(new QueryCheck(query, outcomes, queryIssues));
            Console.Write($"\r  {queryIndex + 1}/{queryCount} queries checked");
        }

        Console.WriteLine();
        WriteReports(checks, queryCount, seed, maxTransfers, outputPath);

        var failed = checks.Count(check => check.Issues.Count > 0);
        Console.WriteLine($"Passed: {queryCount - failed}/{queryCount}  |  Pareto mismatches: {failed}");
        Console.WriteLine($"Report written to: {Path.GetFullPath(outputPath)}");
        return failed == 0 ? 0 : 1;
    }

    private static IReadOnlyList<string> ValidateResult(
        RoutingQuery query,
        RoutingResult result,
        int maxTransfers)
    {
        var issues = ParetoValidator.Validate(result.ParetoJourneys).ToList();
        if (result.BestJourney is not null)
            issues.Add("Multi-criteria result also populated BestJourney.");

        foreach (var journey in result.ParetoJourneys.Journeys)
        {
            if (journey.Legs.Count == 0)
            {
                issues.Add("A journey has no legs.");
                continue;
            }

            if (journey.DepartureStopId != query.SourceStopId)
                issues.Add($"Journey starts at {journey.DepartureStopId}, expected {query.SourceStopId}.");
            if (journey.ArrivalStopId != query.TargetStopId)
                issues.Add($"Journey ends at {journey.ArrivalStopId}, expected {query.TargetStopId}.");
            if (journey.DepartureTime < query.DepartureTime)
                issues.Add("Journey departs before the query departure time.");
            if (journey.NumberOfTransfers > maxTransfers)
                issues.Add($"Journey has {journey.NumberOfTransfers} transfers, over the limit {maxTransfers}.");

            for (var i = 0; i < journey.Legs.Count; i++)
            {
                var leg = journey.Legs[i];
                if (leg.ArrivalTime < leg.DepartureTime)
                    issues.Add($"Journey leg {i + 1} arrives before it departs.");
                if (i > 0)
                {
                    var previous = journey.Legs[i - 1];
                    if (previous.ToStopId != leg.FromStopId)
                        issues.Add($"Journey is disconnected between legs {i} and {i + 1}.");
                    if (leg.DepartureTime < previous.ArrivalTime)
                        issues.Add($"Journey legs {i} and {i + 1} overlap in time.");
                }
            }
        }

        return issues.Distinct().ToList();
    }

    private static HashSet<(int Arrival, int Transfers)> Points(ParetoSet set)
        => set.Journeys
            .Select(journey => (journey.ArrivalTime, journey.NumberOfTransfers))
            .ToHashSet();

    private static void WriteReports(
        IReadOnlyList<QueryCheck> checks,
        int queryCount,
        int seed,
        int maxTransfers,
        string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var errorsPath = Path.ChangeExtension(fullPath, ".errors.txt");

        using (var report = new StreamWriter(fullPath, false, System.Text.Encoding.UTF8))
        {
            report.WriteLine("PARETO CROSS-VALIDATION");
            report.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.WriteLine($"Queries: {queryCount}; seed: {seed}; max transfers: {maxTransfers}");
            report.WriteLine($"Algorithms: {string.Join(", ", checks.FirstOrDefault()?.Outcomes.Keys ?? [])}");
            report.WriteLine($"Passed: {checks.Count(check => check.Issues.Count == 0)}/{queryCount}");
            report.WriteLine($"Mismatches: {checks.Count(check => check.Issues.Count > 0)}");

            foreach (var (check, index) in checks.Select((value, index) => (value, index + 1)))
            {
                if (check.Issues.Count == 0) continue;
                report.WriteLine();
                report.WriteLine($"Q{index}: {check.Query.SourceStopId} -> {check.Query.TargetStopId} " +
                                 $"at {FormatTime(check.Query.DepartureTime)}");
                foreach (var issue in check.Issues)
                    report.WriteLine($"  ERROR: {issue}");
                foreach (var (name, outcome) in check.Outcomes)
                {
                    if (outcome.Error is not null)
                    {
                        report.WriteLine($"  {name}: ERROR {outcome.Error}");
                        continue;
                    }
                    var points = Points(outcome.Result!.ParetoJourneys)
                        .OrderBy(point => point.Arrival)
                        .ThenBy(point => point.Transfers);
                    report.WriteLine($"  {name}: [{string.Join(", ", points.Select(FormatPoint))}]");
                }
            }
        }

        using (var errors = new StreamWriter(errorsPath, false, System.Text.Encoding.UTF8))
        {
            errors.WriteLine("PARETO VALIDATION DETAILS");
            foreach (var (check, index) in checks.Select((value, index) => (value, index + 1)))
            {
                if (check.Issues.Count == 0) continue;
                errors.WriteLine();
                errors.WriteLine($"Q{index}: {check.Query.SourceStopId} -> {check.Query.TargetStopId} " +
                                 $"at {FormatTime(check.Query.DepartureTime)}");
                foreach (var issue in check.Issues)
                    errors.WriteLine($"  ERROR: {issue}");
                foreach (var (name, outcome) in check.Outcomes)
                {
                    errors.WriteLine($"  [{name}]");
                    if (outcome.Error is not null)
                    {
                        errors.WriteLine($"    ERROR: {outcome.Error}");
                        continue;
                    }
                    foreach (var journey in outcome.Result!.ParetoJourneys.Journeys)
                    {
                        errors.WriteLine($"    arrival {FormatTime(journey.ArrivalTime)}, " +
                                         $"transfers {journey.NumberOfTransfers}");
                        foreach (var leg in journey.Legs)
                        {
                            errors.WriteLine($"      {FormatTime(leg.DepartureTime)} {leg.FromStopId} -> " +
                                             $"{FormatTime(leg.ArrivalTime)} {leg.ToStopId} " +
                                             (leg.IsWalk ? "walk" : $"trip {leg.TripId}"));
                        }
                    }
                }
            }
        }
    }

    private static string FormatPoint((int Arrival, int Transfers) point)
        => $"({FormatTime(point.Arrival)}, {point.Transfers})";

    private static string FormatTime(int seconds)
        => $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}";
}
