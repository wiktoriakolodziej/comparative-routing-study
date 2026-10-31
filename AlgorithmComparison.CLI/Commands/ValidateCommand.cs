using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.Validation;

namespace AlgorithmComparison.CLI.Commands;

public sealed class ValidateCommand
{
    private readonly IReadOnlyList<IRoutingAlgorithm> _algorithms;
    private readonly TransitNetwork _network;
    public ValidateCommand(IReadOnlyList<IRoutingAlgorithm> algorithms, TransitNetwork network)
    {
        _algorithms = algorithms;
        _network = network;
    }
    public int Execute(int queryCount = 200, int seed = 42, string outputPath = "results.txt")
    {
        var workload = QueryWorkload.CreateRandom(_network, queryCount, seed);

        Console.WriteLine($"Validating {_algorithms.Count} algorithms ({string.Join(", ", _algorithms.Select(a => a.Name))}) " +
                           $"over {queryCount} queries (seed {seed})...");

        var report = new CrossValidator(_algorithms).Validate(workload, _network);

        WriteReportFile(report, workload, seed, outputPath);

        if (!report.IsValid)
        {
            const string errorsPath = "errors.txt";
            WriteErrorsFile(report, errorsPath);
            Console.WriteLine($"Journey leg details for mismatches written to: {errorsPath}");
        }

        Console.WriteLine($"Passed: {report.PassedQueries}/{report.TotalQueries} ({report.PassRate:P1})");
        Console.WriteLine(report.IsValid
            ? "All algorithms agree on every query."
            : $"{report.Failures.Count} mismatch(es) found — see {outputPath}");
        Console.WriteLine($"Report written to: {outputPath}");

        return report.IsValid ? 0 : 1;
    }

    private static void WriteReportFile(
        ValidationReport report, QueryWorkload workload, int seed, string outputPath)
    {
        using var file = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        void W(string line = "") => file.WriteLine(line);

        W("══════════════════════════════════════════════════════════════════════════");
        W("  CROSS-VALIDATION REPORT");
        W("══════════════════════════════════════════════════════════════════════════");
        W($"  Workload:   {workload.Name} ({workload.Description})");
        W($"  Seed:       {seed}");
        W($"  Queries:    {report.TotalQueries}");
        W($"  Generated:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W();
        W("──────────────────────────────────────────────────────────────────────────");
        W("  SUMMARY");
        W("──────────────────────────────────────────────────────────────────────────");
        W($"  Passed:     {report.PassedQueries}/{report.TotalQueries} ({report.PassRate:P1})");
        W($"  Failures:   {report.Failures.Count}");
        W($"  Valid:      {(report.IsValid ? "YES" : "NO")}");
        W();

        if (report.Failures.Count > 0)
        {
            W("──────────────────────────────────────────────────────────────────────────");
            W("  MISMATCH DETAILS");
            W("──────────────────────────────────────────────────────────────────────────");
            W();

            int idx = 1;
            foreach (var failure in report.Failures)
            {
                W($"  #{idx,-4} {failure.Query.SourceStopId} → {failure.Query.TargetStopId}  " +
                  $"dep {FormatTime(failure.Query.DepartureTime)}");
                W($"       {failure.Description}");
                foreach (var (name, arrival) in failure.ArrivalTimeByAlgorithm)
                {
                    string arrStr = arrival == int.MaxValue ? "unreachable" : FormatTime(arrival);
                    W($"         {name,-14} arr {arrStr}");
                }
                W();
                idx++;
            }
        }

        W("══════════════════════════════════════════════════════════════════════════");
    }

    private static string FormatTime(int seconds)
    {
        int h = seconds / 3600;
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;
        return $"{h:D2}:{m:D2}:{s:D2}";
    }
    private static void WriteErrorsFile(ValidationReport report, string errorsPath)
    {
        using var file = new StreamWriter(errorsPath, append: false, System.Text.Encoding.UTF8);
        void W(string line = "") => file.WriteLine(line);

        W("══════════════════════════════════════════════════════════════════════════");
        W("  MISMATCH JOURNEY DETAILS");
        W("══════════════════════════════════════════════════════════════════════════");
        W($"  Generated:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"  Mismatches: {report.Failures.Count}");
        W();

        int idx = 1;
        foreach (var failure in report.Failures)
        {
            W("──────────────────────────────────────────────────────────────────────────");
            W($"  #{idx}  {failure.Query.SourceStopId} → {failure.Query.TargetStopId}  " +
              $"dep {FormatTime(failure.Query.DepartureTime)}");
            W($"       {failure.Description}");
            W();

            foreach (var (name, result) in failure.ResultByAlgorithm)
            {
                W($"  [{name}]");

                var journey = BestJourneyOf(failure.Query, result);
                if (journey is null)
                {
                    W("      unreachable");
                }
                else
                {
                    foreach (var leg in journey.Legs)
                    {
                        string kind = leg.IsWalk
                            ? "walk"
                            : $"trip {leg.TripId} (route {leg.RouteId})";
                        W($"      {FormatTime(leg.DepartureTime)}  {leg.FromStopId,-10} → " +
                          $"{FormatTime(leg.ArrivalTime)}  {leg.ToStopId,-10}  {kind}");
                    }
                    W($"      arrival: {FormatTime(journey.ArrivalTime)}  " +
                      $"transfers: {journey.NumberOfTransfers}");
                }
                W();
            }

            idx++;
        }

        W("══════════════════════════════════════════════════════════════════════════");
    }
    private static Journey? BestJourneyOf(Core.Queries.RoutingQuery query, RoutingResult result)
    {
        if (!query.MultiCriteria)
            return result.BestJourney;

        return result.ParetoJourneys.Count == 0
            ? null
            : result.ParetoJourneys.Journeys.OrderBy(j => j.ArrivalTime).First();
    }
}
