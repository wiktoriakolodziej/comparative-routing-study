using System.Globalization;
using AlgorithmComparison.Realtime;

namespace AlgorithmComparison.CLI.Commands;

public static class RealtimeCommand
{
    public const string Usage = "realtime [--once | --watch] [--interval-seconds 30] [--count N] " +
        "[--out <directory>] [--trip-updates-only] [--timeout-seconds 20] [--max-age-seconds 300]";

    public static async Task<int> ExecuteAsync(string[] args)
    {
        var options = new GzmRealtimeOptions();
        var output = DefaultOutputDirectory();
        var watch = false;
        var modeSpecified = false;
        int? count = null;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                int PositiveInt()
                {
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
                        throw new ArgumentException("Expected a positive integer.");
                    return value;
                }
                switch (args[i])
                {
                    case "--help": Console.WriteLine(Usage); return 0;
                    case "--once":
                    case "--watch":
                        if (modeSpecified) throw new ArgumentException("Specify only one of --once and --watch.");
                        watch = args[i] == "--watch";
                        modeSpecified = true;
                        break;
                    case "--count": count = PositiveInt(); break;
                    case "--out": output = Path.GetFullPath(Value()); break;
                    case "--interval-seconds": options = options with { PollInterval = TimeSpan.FromSeconds(PositiveInt()) }; break;
                    case "--timeout-seconds": options = options with { RequestTimeout = TimeSpan.FromSeconds(PositiveInt()) }; break;
                    case "--max-age-seconds": options = options with { MaxFeedAge = TimeSpan.FromSeconds(PositiveInt()) }; break;
                    case "--trip-updates-only": options = options with { IncludeVehiclePositions = false }; break;
                    default: throw new ArgumentException($"Unknown realtime option: {args[i]}");
                }
            }
            if (count.HasValue && !watch) throw new ArgumentException("--count requires --watch.");
            options.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"{ex.Message}\nUsage: {Usage}");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var store = new RealtimeSnapshotStore(output);
            var collector = new RealtimeCollector(new GzmRealtimeClient(http, options), store, options);
            Console.WriteLine($"GZM GTFS-RT -> {store.RootDirectory}");
            Console.WriteLine(watch
                ? $"Polling with a {options.PollInterval.TotalSeconds:0}s delay after each batch. Ctrl+C stops."
                : "Downloading one snapshot per selected feed.");
            var hadErrors = false;
            var cycles = 0;
            if (!watch)
                return Print(await collector.CollectOnceAsync(cancellation.Token)) ? 0 : 1;

            await foreach (var results in collector.WatchAsync(cancellation.Token))
            {
                hadErrors |= !Print(results);
                cycles++;
                if (count.HasValue && cycles >= count.Value) break;
            }
            return hadErrors ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("Realtime collection stopped.");
            return 0;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static bool Print(IReadOnlyList<RealtimeCollectionResult> results)
    {
        foreach (var result in results)
        {
            if (!result.Success)
            {
                Console.Error.WriteLine($"{result.Kind}: {result.Error} Last saved snapshot retained.");
                continue;
            }
            var snapshot = result.Snapshot!;
            Console.WriteLine($"{snapshot.FeedName}: {snapshot.EntityCount} entities, " +
                $"source {snapshot.GeneratedAt:O}, age at receipt {snapshot.SourceAgeSecondsAtReceipt:0}s, " +
                $"{(snapshot.WasStaleAtReceipt ? "STALE" : "fresh")}; {snapshot.SnapshotDirectory}");
        }
        return results.All(result => result.Success);
    }

    private static string DefaultOutputDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "DataRealTime", "Slask")))
                    return Path.Combine(directory.FullName, "DataRealTime", "Slask", "live");
            }
        }
        return Path.GetFullPath(Path.Combine("DataRealTime", "Slask", "live"));
    }
}
