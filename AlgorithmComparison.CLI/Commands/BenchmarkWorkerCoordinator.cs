using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AlgorithmComparison.CLI.Commands;

public sealed class BenchmarkWorkerCoordinator
{
    private sealed record WorkerRunInfo(
        string algorithm,
        string mode,
        string status,
        int exitCode,
        string settingsFile,
        string resultFile,
        string? error = null);

    public int Execute(
        BenchmarkDataset dataset,
        DateOnly serviceDate,
        BenchmarkOptions options,
        BenchmarkQueryWorkload workload,
        string graphName,
        int stopCount,
        int tripCount,
        int connectionCount)
    {
        var jobs = options.GetVariants();
        var runStartedUtc = DateTimeOffset.UtcNow;
        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(workload.CsvPath)!,
            "workers",
            SafeName(dataset.CityName),
            graphName,
            runStartedUtc.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outputDirectory);

        var results = new List<BenchmarkWorkerResult>();
        var workerRuns = new List<WorkerRunInfo>();
        foreach (var (algorithm, mode) in jobs)
        {
            var jobName = $"{algorithm}-{mode}";
            var settingsPath = Path.Combine(outputDirectory, $"{jobName}-settings.json");
            var resultPath = Path.Combine(outputDirectory, $"{jobName}-result.json");
            var request = CreateRequest(
                dataset, serviceDate, options, workload, algorithm, mode, resultPath);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(request, JsonOptions));

            Console.WriteLine($"  Starting {dataset.CityName}: {algorithm}/{mode}...");
            var exitCode = -1;
            var standardOutput = string.Empty;
            var standardError = string.Empty;
            try
            {
                exitCode = RunWorker(settingsPath, out standardOutput, out standardError);
            }
            catch (Exception exception)
            {
                standardError = $"{exception.GetType().Name}: {exception.Message}";
            }
            if (!string.IsNullOrWhiteSpace(standardOutput))
                Console.WriteLine(standardOutput.TrimEnd());

            if (File.Exists(resultPath))
            {
                var result = JsonSerializer.Deserialize<BenchmarkWorkerResult>(
                    File.ReadAllText(resultPath), JsonOptions);
                if (result is not null)
                {
                    var hasQueryErrors = result.Samples.Any(sample => !sample.IsWarmup && sample.Error is not null);
                    workerRuns.Add(new WorkerRunInfo(
                        algorithm,
                        mode,
                        hasQueryErrors ? "completed_with_query_errors" : "completed",
                        exitCode,
                        settingsPath,
                        resultPath));
                    if (exitCode != 0)
                        Console.WriteLine($"  Worker {jobName} reported query errors; retaining its completed samples.");
                    results.Add(result);
                    continue;
                }
            }

            var reason = $"Worker exited with code {exitCode}. " + standardError.Trim();
            Console.WriteLine($"  Worker {jobName} failed: {reason}");
            workerRuns.Add(new WorkerRunInfo(
                algorithm, mode, "failed", exitCode, settingsPath, resultPath, reason));
            results.Add(CreateFailureResult(
                dataset, serviceDate, options, workload, graphName, algorithm, mode, reason));
        }

        foreach (var result in results) BenchmarkTextReportWriter.Write(result, outputDirectory);
        var failed = BenchmarkWorkerReportAggregator.WriteReports(
            results,
            jobs,
            outputDirectory,
            graphName);
        WriteRunReport(
            outputDirectory, dataset, serviceDate, options, workload, graphName,
            stopCount, tripCount, connectionCount, runStartedUtc, workerRuns, failed);
        Console.WriteLine($"  Benchmark report: {Path.Combine(outputDirectory, "benchmark-report.md")}");
        Console.WriteLine($"  Detailed metadata: {Path.Combine(outputDirectory, "benchmark-report.json")}");
        Console.WriteLine($"  Summary CSV: {Path.Combine(outputDirectory, $"summary-{graphName}.csv")}");
        return failed ? 1 : 0;
    }

    private static void WriteRunReport(
        string outputDirectory,
        BenchmarkDataset dataset,
        DateOnly serviceDate,
        BenchmarkOptions options,
        BenchmarkQueryWorkload workload,
        string graphName,
        int stopCount,
        int tripCount,
        int connectionCount,
        DateTimeOffset runStartedUtc,
        IReadOnlyList<WorkerRunInfo> workerRuns,
        bool hasFailures)
    {
        var windowCounts = workload.Queries
            .GroupBy(query => query.TimeWindow)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count());
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
        var report = new
        {
            schemaVersion = 1,
            runStartedUtc,
            runFinishedUtc = DateTimeOffset.UtcNow,
            status = hasFailures ? "completed_with_errors" : "completed",
            dataset = new
            {
                city = dataset.CityName,
                transferCategory = dataset.TransferCategory,
                directory = Path.GetFullPath(dataset.DirectoryPath),
                serviceDate = serviceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                stops = stopCount,
                activeTrips = tripCount,
                connections = connectionCount
            },
            parameters = new
            {
                walkingRadiusMeters = options.WalkingRadiusMeters,
                queryCount = options.QueryCount,
                footpathGraph = graphName,
                masterSeed = workload.MasterSeed,
                datasetSeed = workload.DatasetSeed,
                warmupCount = options.WarmupCount,
                repetitions = options.Repetitions,
                maxTransfers = options.MaxTransfers,
                queryTimeWindows = windowCounts,
                algorithms = workerRuns.Select(r => BenchmarkAlgorithmCatalog.DisplayName(r.algorithm)).Distinct().ToArray(),
                modes = workerRuns.Select(r => r.mode).Distinct().ToArray(),
                variants = options.GetVariants().Select(v => v.Id).ToArray(),
                maxTransfersAppliesTo = "single-and-pareto"
            },
            machine = new
            {
                machineName = Environment.MachineName,
                operatingSystem = RuntimeInformation.OSDescription,
                processArchitecture,
                framework = RuntimeInformation.FrameworkDescription,
                logicalProcessorCount = Environment.ProcessorCount,
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            },
            workloadFile = Path.GetFullPath(workload.CsvPath),
            reportFiles = new[]
            {
                $"measurements-{graphName}.csv",
                $"comparisons-{graphName}.csv",
                $"summary-{graphName}.csv"
            }.Concat(workerRuns.Select(r => BenchmarkTextReportWriter.FileName(dataset.CityName, r.algorithm, r.mode))).ToArray(),
            measurementDefinitions = new Dictionary<string, string>
            {
                ["network_load_ms"] = "Wall-clock time for loading the active GTFS network in the worker process.",
                ["footpath_graph_build_ms"] = "Wall-clock time for constructing the selected footpath graph.",
                ["prepare_wall_ms"] = "Wall-clock time for the algorithm Prepare call.",
                ["preprocessing_ms"] = "Preprocessing duration reported by the algorithm implementation.",
                ["working_set_after_prepare_bytes"] = "Worker process resident working set immediately after preparation.",
                ["peak_working_set_bytes"] = "Peak resident working set for the worker process from startup through its query workload.",
                ["elapsed_ms"] = "Per-query duration measured by the routing algorithm base class.",
                ["allocated_bytes"] = "Managed bytes allocated during a query, as reported by the algorithm."
            },
            workerRuns
        };

        File.WriteAllText(
            Path.Combine(outputDirectory, "benchmark-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var markdown = new StringBuilder()
            .AppendLine($"# Benchmark report: {dataset.CityName}")
            .AppendLine()
            .AppendLine($"Status: **{(hasFailures ? "completed with errors" : "completed")}**")
            .AppendLine($"Started (UTC): {runStartedUtc:O}")
            .AppendLine($"Finished (UTC): {DateTimeOffset.UtcNow:O}")
            .AppendLine()
            .AppendLine("## Dataset and network")
            .AppendLine()
            .AppendLine($"- City: {dataset.CityName}")
            .AppendLine($"- Transfer category: {dataset.TransferCategory}")
            .AppendLine($"- Dataset folder: {Path.GetFullPath(dataset.DirectoryPath)}")
            .AppendLine($"- Service date: {serviceDate:yyyy-MM-dd}")
            .AppendLine($"- Stops: {stopCount}; active trips: {tripCount}; connections: {connectionCount}")
            .AppendLine()
            .AppendLine("## Parameters")
            .AppendLine()
            .AppendLine($"- Footpath graph: {graphName}")
            .AppendLine($"- Query count: {options.QueryCount}")
            .AppendLine($"- Query windows: {string.Join(", ", windowCounts.Select(pair => $"{pair.Key}={pair.Value}"))}")
            .AppendLine($"- Master seed: {workload.MasterSeed}")
            .AppendLine($"- City seed: {workload.DatasetSeed}")
            .AppendLine($"- Warm-up queries per worker: {options.WarmupCount}")
            .AppendLine($"- Repetitions per query: {options.Repetitions}")
            .AppendLine($"- Maximum transfers (single and Pareto): {options.MaxTransfers}")
            .AppendLine($"- Algorithms: {string.Join(", ", workerRuns.Select(w => BenchmarkAlgorithmCatalog.DisplayName(w.algorithm)).Distinct())}")
            .AppendLine($"- Modes: {string.Join(", ", workerRuns.Select(w => w.mode).Distinct())}")
            .AppendLine()
            .AppendLine("## Machine and runtime")
            .AppendLine()
            .AppendLine($"- Machine: {Environment.MachineName}")
            .AppendLine($"- Operating system: {RuntimeInformation.OSDescription}")
            .AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"- Process architecture: {processArchitecture}")
            .AppendLine($"- Logical processors: {Environment.ProcessorCount}")
            .AppendLine($"- CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "not available"}")
            .AppendLine($"- Available memory reported by runtime: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes")
            .AppendLine()
            .AppendLine("## Measurement definitions")
            .AppendLine()
            .AppendLine("- `network_load_ms`: load time for the active GTFS network in each worker.")
            .AppendLine("- `footpath_graph_build_ms`: time to create the selected walking graph.")
            .AppendLine("- `prepare_wall_ms`: total wall time of the algorithm preparation call.")
            .AppendLine("- `preprocessing_ms`: preprocessing time reported by the algorithm.")
            .AppendLine("- `working_set_after_prepare_bytes`: worker resident memory immediately after preparation.")
            .AppendLine("- `peak_working_set_bytes`: worker process peak resident memory, including startup and its query workload.")
            .AppendLine("- `elapsed_ms` and `allocated_bytes`: per-query runtime and managed allocations.")
            .AppendLine()
            .AppendLine("## Worker status")
            .AppendLine()
            .AppendLine("| Algorithm | Mode | Status | Exit code |")
            .AppendLine("|---|---|---|---:|");
        foreach (var worker in workerRuns)
            markdown.AppendLine($"| {worker.algorithm} | {worker.mode} | {worker.status} | {worker.exitCode} |");
        markdown.AppendLine()
            .AppendLine("## Files")
            .AppendLine()
            .AppendLine($"- Shared queries: `{Path.GetFullPath(workload.CsvPath)}`")
            .AppendLine($"- Measurements: `measurements-{graphName}.csv`")
            .AppendLine($"- Comparisons: `comparisons-{graphName}.csv`")
            .AppendLine($"- Summary: `summary-{graphName}.csv`")
            .AppendLine("- Machine-readable run details: `benchmark-report.json`");
        foreach (var worker in workerRuns)
            markdown.AppendLine($"- Text report: {BenchmarkTextReportWriter.FileName(dataset.CityName, worker.algorithm, worker.mode)}");
        File.WriteAllText(Path.Combine(outputDirectory, "benchmark-report.md"), markdown.ToString());
    }

    private static BenchmarkWorkerRequest CreateRequest(
        BenchmarkDataset dataset,
        DateOnly serviceDate,
        BenchmarkOptions options,
        BenchmarkQueryWorkload workload,
        string algorithm,
        string mode,
        string resultPath)
        => new()
        {
            DatasetPath = dataset.DirectoryPath,
            CityName = dataset.CityName,
            TransferCategory = dataset.TransferCategory,
            ServiceDate = serviceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            FootpathGraph = "radius",
            WalkingRadiusMeters = options.WalkingRadiusMeters,
            Algorithm = algorithm,
            Mode = mode,
            MasterSeed = workload.MasterSeed,
            DatasetSeed = workload.DatasetSeed,
            QueryCount = workload.Queries.Count,
            MaxTransfers = options.MaxTransfers,
            WarmupCount = options.WarmupCount,
            Repetitions = options.Repetitions,
            OutputPath = resultPath,
            Queries = workload.Queries.Select(entry => new BenchmarkWorkerQuery(
                entry.Index,
                entry.TimeWindow,
                entry.Query.SourceStopId,
                entry.Query.TargetStopId,
                entry.Query.DepartureTime)).ToArray()
        };

    private static BenchmarkWorkerResult CreateFailureResult(
        BenchmarkDataset dataset,
        DateOnly serviceDate,
        BenchmarkOptions options,
        BenchmarkQueryWorkload workload,
        string graphName,
        string algorithm,
        string mode,
        string reason)
        => new()
        {
            CityName = dataset.CityName,
            TransferCategory = dataset.TransferCategory,
            ServiceDate = serviceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            FootpathGraph = "radius",
            WalkingRadiusMeters = options.WalkingRadiusMeters,
            Algorithm = algorithm,
            Mode = mode,
            MasterSeed = workload.MasterSeed,
            DatasetSeed = workload.DatasetSeed,
            QueryCount = workload.Queries.Count,
            WarmupCount = options.WarmupCount,
            Repetitions = options.Repetitions,
            MaxTransfers = options.MaxTransfers,
            Samples = workload.Queries.SelectMany(query => Enumerable.Range(1, options.Repetitions)
                .Select(repetition => new BenchmarkWorkerSample
                {
                    QueryIndex = query.Index,
                    TimeWindow = query.TimeWindow,
                    FromStopId = query.Query.SourceStopId,
                    ToStopId = query.Query.TargetStopId,
                    DepartureTime = query.Query.DepartureTime,
                    Repetition = repetition,
                    IsWarmup = false,
                    Error = reason
                })).ToArray()
        };

    private static int RunWorker(string settingsPath, out string standardOutput, out string standardError)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current executable path.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new InvalidOperationException("Cannot determine the CLI assembly path for dotnet worker launch.");
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("benchmark-worker");
        startInfo.ArgumentList.Add(settingsPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the benchmark worker process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        standardOutput = stdoutTask.Result;
        standardError = stderrTask.Result;
        return process.ExitCode;
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

