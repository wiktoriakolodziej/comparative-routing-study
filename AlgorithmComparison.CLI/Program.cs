using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.CLI;
using AlgorithmComparison.CLI.Commands;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.GTFS;

using System.Text.Json;

if (args.Length == 0) args = new[] { "menu" };
string command = args[0].ToLowerInvariant();
if (command is "--help" or "help" || (command == "menu" && args.Contains("--help")))
{
    Console.WriteLine("Commands: menu, benchmark-menu (checkboxes), benchmark (alias: bench), validate, validate-pareto, realtime, raptor-realtime, raptor-realtime-menu");
    Console.WriteLine("RAPTOR Realtime na żywo wymaga połączenia z internetem; odtwarzanie migawek działa offline.");
    return 0;
}
if (command == "menu")
{
    try
    {
        if (ApplicationMenu.Select() == ApplicationMode.Realtime)
            return await RealtimeRaptorCommand.ExecuteMenuAsync();
        command = "benchmark-menu";
    }
    catch (OperationCanceledException) { Console.WriteLine("Anulowano wybór."); return 130; }
    catch (ArgumentException exception) { return Fail(exception.Message); }
}
if (command == "raptor-realtime-menu")
    return await RealtimeRaptorCommand.ExecuteMenuAsync();

if (command == "raptor-realtime")
    return await RealtimeRaptorCommand.ExecuteAsync(args[1..]);

if (command == "realtime")
    return await RealtimeCommand.ExecuteAsync(args[1..]);

if (command == "benchmark-worker")
{
    if (args.Length != 2)
        return Fail("Usage: benchmark-worker <worker-settings.json>.");

    try
    {
        var workerSettings = JsonSerializer.Deserialize<BenchmarkWorkerRequest>(
            File.ReadAllText(args[1]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Worker settings file is empty or invalid.");

        return new BenchmarkWorkerCommand(workerSettings).Execute();
    }
    catch (Exception exception)
    {
        return Fail($"Benchmark worker failed: {exception.GetType().Name}: {exception.Message}");
    }
}

if (command is "benchmark" or "bench" or "benchmark-menu")
{
    if (!BenchmarkOptions.TryParse(args[1..], out var benchmarkOptions, out var error))
        return Fail(error!);

    var discovery = BenchmarkDatasetDiscovery.Discover();
    IReadOnlyList<BenchmarkDataset> selectedDatasets;
    try
    {
        if (command == "benchmark-menu")
        {
            var seed = BenchmarkSeedMenu.Read(benchmarkOptions!.Seed);
            benchmarkOptions = BenchmarkSelection.Interactive(benchmarkOptions with { Seed = seed }, discovery);
        }
        selectedDatasets = BenchmarkSelection.ResolveDatasets(discovery, benchmarkOptions!.DatasetIds);
        if (selectedDatasets.Count == 0) return Fail("No valid datasets selected.");
    }
    catch (OperationCanceledException) { Console.WriteLine("Anulowano benchmark."); return 130; }
    catch (ArgumentException exception) { return Fail(exception.Message); }
    var benchmarkMasterSeed = benchmarkOptions!.Seed ?? BenchmarkQueryWorkloadFactory.CreateRunSeed();
    Console.WriteLine("Benchmark configuration accepted:");
    Console.WriteLine($"  Queries per city: {benchmarkOptions.QueryCount}");
    Console.WriteLine($"  Walking: radius {benchmarkOptions.WalkingRadiusMeters} m, multi-hop, straight-line distances");
    Console.WriteLine($"  Warm-up queries: {benchmarkOptions.WarmupCount}");
    Console.WriteLine($"  Repetitions: {benchmarkOptions.Repetitions}");
    Console.WriteLine($"  Max transfers (single and Pareto): {benchmarkOptions.MaxTransfers}");
    Console.WriteLine($"  Variants: {string.Join(", ", benchmarkOptions.GetVariants().Select(v => v.Id))}");
    Console.WriteLine($"  Selected datasets: {string.Join(", ", selectedDatasets.Select(BenchmarkSelection.DatasetId))}");
    Console.WriteLine($"  Seed: {benchmarkMasterSeed}");
    Console.WriteLine($"  Data root: {discovery.DataRoot}");

    foreach (var dataset in discovery.Datasets)
    {
        var status = !dataset.IsValid ? "invalid" : selectedDatasets.Contains(dataset) ? "selected" : "not selected";
        Console.WriteLine($"  {dataset.CityName}: {status}");
        foreach (var issue in dataset.ValidationIssues)
            Console.WriteLine($"    - {issue}");
    }

    foreach (var issue in discovery.DiscoveryIssues)
        Console.WriteLine($"  - {issue}");

    var loadedCount = 0;
    var benchmarkExitCode = 0;
    foreach (var dataset in selectedDatasets)
    {
        try
        {
            var serviceDate = GtfsServiceDateSelector.SelectRepresentativeDate(dataset.DirectoryPath);
            var benchmarkLoader = new GtfsLoader();
            var benchmarkNetwork = benchmarkLoader.Load(dataset.DirectoryPath, serviceDate);
            if (benchmarkNetwork.Connections.Count == 0)
                throw new InvalidDataException($"No active connections were found for {serviceDate:yyyy-MM-dd}.");

            var datasetSeed = BenchmarkQueryWorkloadFactory.DeriveDatasetSeed(
                benchmarkMasterSeed,
                dataset.TransferCategory,
                dataset.CityName);
            var benchmarkWorkload = BenchmarkQueryWorkloadFactory.GenerateAndSave(
                benchmarkNetwork,
                benchmarkOptions.QueryCount,
                benchmarkMasterSeed,
                datasetSeed,
                dataset.TransferCategory,
                dataset.CityName,
                discovery.DataRoot,
                serviceDate);
            Console.WriteLine(
                $"  Saved {benchmarkWorkload.Queries.Count} queries for {dataset.CityName} " +
                $"(city seed {datasetSeed}) to {benchmarkWorkload.CsvPath}.");

            Console.WriteLine(
                $"  Loaded {dataset.CityName}: " +
                $"date {serviceDate:yyyy-MM-dd}, {benchmarkNetwork.Stops.Count} stops, " +
                $"{benchmarkNetwork.Trips.Count} active trips, {benchmarkNetwork.Connections.Count} connections, " +
                "graph radius, " +
                "selected variants " + string.Join(", ", benchmarkOptions.GetVariants().Select(v => v.Id)) + ".");

            var result = new BenchmarkWorkerCoordinator().Execute(
                dataset,
                serviceDate,
                benchmarkOptions,
                benchmarkWorkload,
                "radius-" + benchmarkOptions.WalkingRadiusMeters.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
                benchmarkNetwork.Stops.Count,
                benchmarkNetwork.Trips.Count,
                benchmarkNetwork.Connections.Count);
            if (result != 0)
                benchmarkExitCode = 1;
            loadedCount++;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"  Could not load {dataset.CityName}: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    Console.WriteLine($"  Loaded city datasets: {loadedCount}/{selectedDatasets.Count}");
    return loadedCount > 0 && loadedCount == selectedDatasets.Count && benchmarkExitCode == 0 ? 0 : 1;
}

if (command is not ("validate" or "validate-pareto"))
    return Fail($"Unknown command '{command}'. Valid commands: menu, benchmark-menu, benchmark, bench, validate, validate-pareto, realtime, raptor-realtime, raptor-realtime-menu.");

AppSettings settings = new AppSettings
{
    GtfsPath = @"..\..\..\..\Data",
    ServiceDate = DateOnly.FromDateTime(DateTime.Now),
};

var loader = new GtfsLoader();
var network = loader.Load(settings.GtfsPath, settings.ServiceDate);

var stops = network.Stops.Values.ToArray();
IFootpathGraph footpathGraph = new AlgorithmComparison.Core.Walking.RadiusFootpathGraph(stops);

var algorithms = new List<IRoutingAlgorithm>
{
    new RaptorAlgorithm(footpathGraph),
    new ConnectionScanAlgorithm(footpathGraph),
    new TimeDependentDijkstraAlgorithm(footpathGraph)
};

return command switch
{
    "validate" => RunValidate(algorithms, network, args[1..]),
    "validate-pareto" => RunValidatePareto(algorithms, network, args[1..]),
    _ => Fail($"Unknown command '{command}'."),
};

static int RunValidate(
    IReadOnlyList<IRoutingAlgorithm> algorithms,
    AlgorithmComparison.Core.Models.TransitNetwork network,
    string[] args)
{
    int queryCount = 19;
    int seed = 42;
    string outputPath = "results.txt";

    for (int i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--queries": int.TryParse(args[++i], out queryCount); break;
            case "--seed": int.TryParse(args[++i], out seed); break;
            case "--out": outputPath = args[++i]; break;
        }
    }

    var cmd = new ValidateCommand(algorithms, network);
    return cmd.Execute(queryCount, seed, outputPath);
}

static int RunValidatePareto(
    IReadOnlyList<IRoutingAlgorithm> algorithms,
    AlgorithmComparison.Core.Models.TransitNetwork network,
    string[] args)
{
    int queryCount = 19;
    int seed = 42;
    int maxTransfers = 7;
    string outputPath = "pareto-results.txt";

    for (int i = 0; i < args.Length; i++)
    {
        if (i + 1 >= args.Length)
            return Fail($"Missing value for option '{args[i]}'.");

        var value = args[++i];
        switch (args[i - 1])
        {
            case "--queries" when int.TryParse(value, out var parsedQueries):
                queryCount = parsedQueries;
                break;
            case "--seed" when int.TryParse(value, out var parsedSeed):
                seed = parsedSeed;
                break;
            case "--max-transfers" when int.TryParse(value, out var parsedTransfers):
                maxTransfers = parsedTransfers;
                break;
            case "--out":
                outputPath = value;
                break;
            case "--queries":
            case "--seed":
            case "--max-transfers":
                return Fail($"Invalid value '{value}' for option '{args[i - 1]}'.");
            default:
                return Fail($"Unknown option '{args[i - 1]}'.");
        }
    }

    try
    {
        return new ValidateParetoCommand(algorithms, network)
            .Execute(queryCount, seed, maxTransfers, outputPath);
    }
    catch (ArgumentException exception)
    {
        return Fail(exception.Message);
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

