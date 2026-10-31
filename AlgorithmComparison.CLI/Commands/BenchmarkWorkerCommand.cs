using System.Text.Json;
using System.Diagnostics;
using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.GTFS;
using AlgorithmComparison.Core.Walking;

using GtfsStop = AlgorithmComparison.Core.Models.Stop;

namespace AlgorithmComparison.CLI.Commands;

public sealed class BenchmarkWorkerCommand
{
    private readonly BenchmarkWorkerRequest request;

    public BenchmarkWorkerCommand(BenchmarkWorkerRequest request)
        => this.request = request ?? throw new ArgumentNullException(nameof(request));

    public int Execute()
    {
        ValidateRequest();
        var serviceDate = DateOnly.Parse(request.ServiceDate, System.Globalization.CultureInfo.InvariantCulture);
        var loader = new GtfsLoader();
        var networkLoadTimer = Stopwatch.StartNew();
        var network = loader.Load(request.DatasetPath, serviceDate);
        networkLoadTimer.Stop();
        if (network.Connections.Count == 0)
            throw new InvalidDataException($"No active connections were found for {serviceDate:yyyy-MM-dd}.");

        var graphBuildTimer = Stopwatch.StartNew();
        var graph = new RadiusFootpathGraph(network.Stops.Values, request.WalkingRadiusMeters);
        graphBuildTimer.Stop();

        var algorithm = CreateAlgorithm(request.Algorithm, graph);
        var prepareTimer = Stopwatch.StartNew();
        if (algorithm is IBenchmarkPreparableRoutingAlgorithm preparable)
            preparable.Prepare(network);
        prepareTimer.Stop();
        var algorithmPreprocessingMs = (algorithm as IBenchmarkPreparableRoutingAlgorithm)?.PreprocessingTime.TotalMilliseconds;
        using var currentProcess = Process.GetCurrentProcess();
        currentProcess.Refresh();
        var workingSetAfterPrepareBytes = currentProcess.WorkingSet64;

        var samples = new List<BenchmarkWorkerSample>();
        var multiCriteria = request.Mode.Equals("pareto", StringComparison.OrdinalIgnoreCase);
        var warmupCount = Math.Min(request.WarmupCount, request.Queries.Count);
        for (var i = 0; i < warmupCount; i++)
            samples.Add(RunOne(request.Queries[i], 0, true, algorithm, network, multiCriteria));

        foreach (var query in request.Queries)
            for (var repetition = 1; repetition <= request.Repetitions; repetition++)
                samples.Add(RunOne(query, repetition, false, algorithm, network, multiCriteria));

        var result = new BenchmarkWorkerResult
        {
            WalkingRadiusMeters = request.WalkingRadiusMeters,
            CityName = request.CityName,
            TransferCategory = request.TransferCategory,
            ServiceDate = request.ServiceDate,
            FootpathGraph = "radius",
            Algorithm = request.Algorithm,
            Mode = request.Mode.ToLowerInvariant(),
            MasterSeed = request.MasterSeed,
            DatasetSeed = request.DatasetSeed,
            QueryCount = request.Queries.Count,
            WarmupCount = request.WarmupCount,
            Repetitions = request.Repetitions,
            MaxTransfers = request.MaxTransfers,
            NetworkLoadMs = networkLoadTimer.Elapsed.TotalMilliseconds,
            FootpathGraphBuildMs = graphBuildTimer.Elapsed.TotalMilliseconds,
            PrepareWallMs = prepareTimer.Elapsed.TotalMilliseconds,
            AlgorithmPreprocessingMs = algorithmPreprocessingMs,
            WorkingSetAfterPrepareBytes = workingSetAfterPrepareBytes,
            PeakWorkingSetBytes = GetPeakWorkingSet(currentProcess),
            Samples = samples
        };

        var outputPath = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        BenchmarkTextReportWriter.Write(result, Path.GetDirectoryName(outputPath)!);
        Console.WriteLine($"Worker result written: {outputPath}");
        return samples.Any(sample => !sample.IsWarmup && sample.Error is not null) ? 1 : 0;
    }

    private static long GetPeakWorkingSet(Process process)
    {
        process.Refresh();
        return process.PeakWorkingSet64;
    }

    private BenchmarkWorkerSample RunOne(
        BenchmarkWorkerQuery query,
        int repetition,
        bool isWarmup,
        IRoutingAlgorithm algorithm,
        TransitNetwork network,
        bool multiCriteria)
    {
        try
        {
            var result = algorithm.Route(new RoutingQuery
            {
                SourceStopId = query.FromStopId,
                TargetStopId = query.ToStopId,
                DepartureTime = query.DepartureTime,
                MaxTransfers = request.MaxTransfers,
                MultiCriteria = multiCriteria
            }, network);

            return new BenchmarkWorkerSample
            {
                QueryIndex = query.Index,
                TimeWindow = query.TimeWindow,
                FromStopId = query.FromStopId,
                ToStopId = query.ToStopId,
                DepartureTime = query.DepartureTime,
                Repetition = repetition,
                IsWarmup = isWarmup,
                Signature = GetSignature(result, multiCriteria),
                Metrics = algorithm.LastMetrics
            };
        }
        catch (Exception exception)
        {
            return new BenchmarkWorkerSample
            {
                QueryIndex = query.Index,
                TimeWindow = query.TimeWindow,
                FromStopId = query.FromStopId,
                ToStopId = query.ToStopId,
                DepartureTime = query.DepartureTime,
                Repetition = repetition,
                IsWarmup = isWarmup,
                Error = $"{exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    private static IRoutingAlgorithm CreateAlgorithm(string name, IFootpathGraph graph)
        => BenchmarkAlgorithmCatalog.Create(name, graph);

    private void ValidateRequest()
    {
        if (!request.FootpathGraph.Equals("radius", StringComparison.OrdinalIgnoreCase) || !double.IsFinite(request.WalkingRadiusMeters) || request.WalkingRadiusMeters < 0)
            throw new ArgumentException("Benchmark requires a finite nonnegative walking radius and radius graph.");
        if (request.Queries.Count == 0) throw new ArgumentException("Worker needs at least one query.");
        if (request.WarmupCount < 0 || request.Repetitions < 1 || request.MaxTransfers < 0 || request.MaxTransfers > int.MaxValue - 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Warmups and transfers must be nonnegative; repetitions must be positive.");
        if (request.Mode is not ("earliest" or "pareto"))
            throw new ArgumentException("Worker mode must be 'earliest' or 'pareto'.");
        if (!Directory.Exists(request.DatasetPath))
            throw new DirectoryNotFoundException($"Dataset directory not found: {request.DatasetPath}");
    }

    private static string GetSignature(RoutingResult result, bool multiCriteria)
    {
        if (!multiCriteria)
        {
            var journey = result.BestJourney;
            return journey is null || journey.ArrivalTime == int.MaxValue
                ? "unreachable||"
                : $"{journey.ArrivalTime}|{journey.NumberOfTransfers}|";
        }

        var points = result.ParetoJourneys.Journeys
            .Select(journey => (journey.ArrivalTime, journey.NumberOfTransfers))
            .OrderBy(point => point.ArrivalTime)
            .ThenBy(point => point.NumberOfTransfers)
            .Select(point => $"{point.ArrivalTime}:{point.NumberOfTransfers}");
        var serialized = string.Join(";", points);
        var first = result.ParetoJourneys.Journeys
            .OrderBy(journey => journey.ArrivalTime)
            .ThenBy(journey => journey.NumberOfTransfers)
            .FirstOrDefault();
        return first is null ? $"unreachable||{serialized}" : $"{first.ArrivalTime}|{first.NumberOfTransfers}|{serialized}";
    }
}

