using System.Globalization;
using System.Text;

namespace AlgorithmComparison.CLI.Commands;

public static class BenchmarkWorkerReportAggregator
{
    public static bool WriteReports(
        IReadOnlyList<BenchmarkWorkerResult> results,
        IReadOnlyList<BenchmarkVariant> expectedVariants,
        string outputDirectory,
        string graphName)
    {
        Directory.CreateDirectory(outputDirectory);
        var samples = results.SelectMany(result => result.Samples.Select(sample => (Result: result, Sample: sample)))
            .ToArray();
        WriteMeasurements(Path.Combine(outputDirectory, $"measurements-{graphName}.csv"), samples);
        WriteComparisons(Path.Combine(outputDirectory, $"comparisons-{graphName}.csv"), samples, expectedVariants);
        WriteSummary(Path.Combine(outputDirectory, $"summary-{graphName}.csv"), samples);

        return samples.Any(item => !item.Sample.IsWarmup && item.Sample.Error is not null);
    }

    private static void WriteMeasurements(
        string path,
        IReadOnlyList<(BenchmarkWorkerResult Result, BenchmarkWorkerSample Sample)> samples)
    {
        using var writer = CreateWriter(path);
        writer.WriteLine(
            "city,transfer_category,service_date,footpath_graph,mode,query_index,time_window," +
            "from_stop_id,to_stop_id,departure_time_seconds,repetition,is_warmup,algorithm," +
            "elapsed_ms,allocated_bytes,reachable,arrival_time_seconds,transfers,pareto_points,error");
        foreach (var (result, sample) in samples)
        {
            var parts = sample.Signature?.Split('|') ?? [];
            var reachable = sample.Signature is not null
                && !sample.Signature.StartsWith("unreachable|", StringComparison.Ordinal);
            writer.WriteLine(string.Join(",",
                Csv(result.CityName), Csv(result.TransferCategory), result.ServiceDate,
                result.FootpathGraph, result.Mode, sample.QueryIndex, Csv(sample.TimeWindow),
                Csv(sample.FromStopId), Csv(sample.ToStopId), sample.DepartureTime,
                sample.Repetition, sample.IsWarmup, Csv(DisplayName(result.Algorithm)),
                Number(sample.Metrics?.QueryTimeMs), sample.Metrics?.AllocatedBytes.ToString(CultureInfo.InvariantCulture) ?? "",
                reachable, Csv(parts.ElementAtOrDefault(0) ?? ""), Csv(parts.ElementAtOrDefault(1) ?? ""),
                Csv(result.Mode == "pareto" ? parts.ElementAtOrDefault(2) ?? "" : ""), Csv(sample.Error ?? "")));
        }
    }

    private static void WriteComparisons(
        string path,
        IReadOnlyList<(BenchmarkWorkerResult Result, BenchmarkWorkerSample Sample)> samples,
        IReadOnlyList<BenchmarkVariant> expectedVariants)
    {
        using var writer = CreateWriter(path);
        writer.WriteLine(
            "city,mode,query_index,time_window,from_stop_id,to_stop_id,departure_time_seconds," +
            "repetition,comparison_status,reference_signature,algorithm_signatures");
        var groups = samples.Where(item => !item.Sample.IsWarmup)
            .GroupBy(item => (item.Result.CityName, item.Result.Mode, item.Sample.QueryIndex, item.Sample.Repetition));
        foreach (var group in groups)
        {
            var expectedAlgorithms = expectedVariants.Where(v => v.Mode == group.Key.Mode).Select(v => v.Algorithm).ToArray();
            var ordered = group.OrderBy(item => Array.IndexOf(expectedAlgorithms, item.Result.Algorithm)).ToArray();
            var signaturesMatch = ordered.Length == expectedAlgorithms.Length
                && ordered.All(item => item.Sample.Error is null && item.Sample.Signature == ordered[0].Sample.Signature);
            var status = ordered.Any(item => item.Sample.Error is not null) || ordered.Length != expectedAlgorithms.Length
                ? "error"
                : expectedAlgorithms.Length < 2 ? "not_compared" : signaturesMatch ? "same" : "different";
            var first = ordered[0];
            writer.WriteLine(string.Join(",",
                Csv(first.Result.CityName), first.Result.Mode, first.Sample.QueryIndex,
                Csv(first.Sample.TimeWindow), Csv(first.Sample.FromStopId), Csv(first.Sample.ToStopId),
                first.Sample.DepartureTime, first.Sample.Repetition, status,
                Csv(first.Sample.Signature ?? ""),
                Csv(string.Join(";", ordered.Select(item =>
                    $"{DisplayName(item.Result.Algorithm)}={item.Sample.Signature ?? item.Sample.Error}")))));
        }
    }

    private static void WriteSummary(
        string path,
        IReadOnlyList<(BenchmarkWorkerResult Result, BenchmarkWorkerSample Sample)> samples)
    {
        using var writer = CreateWriter(path);
        writer.WriteLine(
            "city,transfer_category,service_date,footpath_graph,mode,algorithm,samples,successful,failures," +
            "query_count,master_seed,dataset_seed,warmup_count,repetitions,max_transfers," +
            "network_load_ms,footpath_graph_build_ms,prepare_wall_ms,preprocessing_ms," +
            "working_set_after_prepare_bytes,peak_working_set_bytes," +
            "mean_ms,median_ms,p95_ms,p99_ms,min_ms,max_ms,mean_allocated_bytes");
        foreach (var group in samples.Where(item => !item.Sample.IsWarmup)
                     .GroupBy(item => (item.Result.CityName, item.Result.TransferCategory, item.Result.ServiceDate,
                         item.Result.FootpathGraph, item.Result.Mode, item.Result.Algorithm)))
        {
            var times = group.Where(item => item.Sample.Metrics is not null)
                .Select(item => item.Sample.Metrics!.QueryTimeMs).Order().ToArray();
            var allocations = group.Where(item => item.Sample.Metrics is not null)
                .Select(item => (double)item.Sample.Metrics!.AllocatedBytes).ToArray();
            var failures = group.Count(item => item.Sample.Error is not null);
            writer.WriteLine(string.Join(",",
                Csv(group.Key.CityName), Csv(group.Key.TransferCategory), group.Key.ServiceDate,
                group.Key.FootpathGraph, group.Key.Mode, Csv(DisplayName(group.Key.Algorithm)),
                group.Count(), times.Length, failures,
                group.First().Result.QueryCount,
                group.First().Result.MasterSeed,
                group.First().Result.DatasetSeed,
                group.First().Result.WarmupCount,
                group.First().Result.Repetitions,
                group.First().Result.MaxTransfers,
                Number(group.First().Result.NetworkLoadMs),
                Number(group.First().Result.FootpathGraphBuildMs),
                Number(group.First().Result.PrepareWallMs),
                Number(group.First().Result.AlgorithmPreprocessingMs),
                group.First().Result.WorkingSetAfterPrepareBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                group.First().Result.PeakWorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                times.Length == 0 ? "" : Mean(times).ToString("R", CultureInfo.InvariantCulture),
                times.Length == 0 ? "" : Median(times).ToString("R", CultureInfo.InvariantCulture),
                times.Length == 0 ? "" : Percentile(times, .95).ToString("R", CultureInfo.InvariantCulture),
                times.Length == 0 ? "" : Percentile(times, .99).ToString("R", CultureInfo.InvariantCulture),
                times.Length == 0 ? "" : times[0].ToString("R", CultureInfo.InvariantCulture),
                times.Length == 0 ? "" : times[^1].ToString("R", CultureInfo.InvariantCulture),
                allocations.Length == 0 ? "" : Mean(allocations).ToString("R", CultureInfo.InvariantCulture)));
        }
    }

    private static StreamWriter CreateWriter(string path)
        => new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    private static double Mean(IReadOnlyList<double> values) => values.Average();
    private static double Median(IReadOnlyList<double> values)
        => values.Count % 2 == 0 ? (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2 : values[values.Count / 2];
    private static double Percentile(IReadOnlyList<double> values, double percentile)
        => values[Math.Clamp((int)Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1)];
    private static string Number(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string DisplayName(string algorithm) => algorithm switch
    {
        "raptor" => "RAPTOR Mine",
        "csa" => "CSA",
        "dijkstra" => "Time Dependent Dijkstra",
        _ => algorithm
    };
}
