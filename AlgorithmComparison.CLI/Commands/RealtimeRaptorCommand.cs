using System.Globalization;
using System.Text.Json;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.GTFS;
using AlgorithmComparison.Core.Walking;
using AlgorithmComparison.RaptorRealtime;
using AlgorithmComparison.Realtime;
using TransitRealtime;

namespace AlgorithmComparison.CLI.Commands;

public static class RealtimeRaptorCommand
{
    public const string Usage = "raptor-realtime --gtfs <directory> --date yyyy-MM-dd --from <stop_id> --to <stop_id> " +
        "[--departure HH:mm:ss|now] [--snapshot <tripUpdates.pb> --replay] [--watch --count N] " +
        "[--interval-seconds 30] [--max-age-seconds 300] [--max-transfers 7] [--pareto] " +
        "[--radius-metres 500] [--archive <directory>] [--out <report.json>] [--compare] [--warmups 1] [--repetitions 5] [--queries-file <queries.json>] [--snapshots <paths.json> --replay] [--benchmark-dir <directory>] [--session-dir <directory>]";

    public static async Task<int> ExecuteAsync(string[] args, TransitNetwork? preparedNetwork = null)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        RealtimeSessionReportWriter? sessionWriter = null;
        var sessionStatus = "failed";
        string? sessionError = null;
        var exitCode = 1;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            var valueNames = new HashSet<string> { "--gtfs", "--date", "--from", "--to", "--departure", "--snapshot",
                "--count", "--interval-seconds", "--max-age-seconds", "--max-transfers", "--radius-metres", "--archive", "--out", "--warmups", "--repetitions", "--queries-file", "--snapshots", "--benchmark-dir", "--session-dir" };
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help") { Console.WriteLine(Usage); return 0; }
                if (args[i] is "--replay" or "--watch" or "--pareto" or "--compare")
                {
                    if (!flags.Add(args[i])) throw new ArgumentException("Duplicate flag.");
                }
                else if (valueNames.Contains(args[i]) && i + 1 < args.Length)
                {
                    var key = args[i];
                    if (!values.TryAdd(key, args[++i])) throw new ArgumentException($"Duplicate option {key}.");
                }
                else throw new ArgumentException($"Unknown option or missing value: {args[i]}");
            }
            string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
            int Number(string key, int fallback, int minimum = 1) => !values.TryGetValue(key, out var value) ? fallback
                : int.TryParse(value, out var n) && n >= minimum ? n : throw new ArgumentException($"Invalid {key}.");
            var gtfs = Path.GetFullPath(Required("--gtfs"));
            var date = DateOnly.ParseExact(Required("--date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var from = Required("--from");
            var to = Required("--to");
            var watch = flags.Contains("--watch");
            var replay = flags.Contains("--replay");
            var snapshot = values.GetValueOrDefault("--snapshot");
            var compare = flags.Contains("--compare");
            var snapshotList = values.GetValueOrDefault("--snapshots");
            if ((snapshotList is not null && (!compare || !replay || snapshot is not null)) ||
                (!compare && new[] { "--warmups", "--repetitions", "--queries-file", "--benchmark-dir" }.Any(values.ContainsKey)))
                throw new ArgumentException("Benchmark options require --compare; --snapshots also requires --replay and excludes --snapshot.");
            if ((replay && snapshot is null && snapshotList is null) || ((snapshot is not null || snapshotList is not null) && watch) || (values.ContainsKey("--count") && !watch))
                throw new ArgumentException("--replay requires --snapshot; --snapshot cannot be watched; --count requires --watch.");
            var options = new GzmRealtimeOptions
            {
                IncludeVehiclePositions = false,
                PollInterval = TimeSpan.FromSeconds(Number("--interval-seconds", 30)),
                MaxFeedAge = TimeSpan.FromSeconds(Number("--max-age-seconds", 300))
            };
            options.Validate();
            var maxTransfers = Number("--max-transfers", 7, 0);
            var count = Number("--count", watch ? int.MaxValue : 1);
            var warmups = Number("--warmups", 1, 0);
            var repetitions = Number("--repetitions", 5);
            string[]? snapshotPaths = null;
            if (snapshotList is not null)
            {
                var listPath = Path.GetFullPath(snapshotList);
                snapshotPaths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(listPath, cancellation.Token))
                    ?? throw new InvalidDataException("Snapshot list is empty.");
                if (snapshotPaths.Length == 0 || snapshotPaths.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Snapshot list is empty/invalid.");
                snapshotPaths = snapshotPaths.Select(p => Path.GetFullPath(p, Path.GetDirectoryName(listPath)!)).ToArray();
                snapshot = snapshotPaths[0];
                count = snapshotPaths.Length;
            }
            var departure = values.GetValueOrDefault("--departure", "now");
            if (departure != "now") _ = ParseTime(departure);
            var radiusText = values.GetValueOrDefault("--radius-metres", "500");
            if (!double.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius)
                || !double.IsFinite(radius) || radius < 0)
                throw new ArgumentException("Invalid --radius-metres.");
            var network = preparedNetwork ?? new GtfsLoader().Load(gtfs, date);
            if (network.ServiceDate != date) throw new ArgumentException("Prepared network has a different service date.");
            if (network.Trips.Count == 0)
                throw new InvalidDataException("GTFS has no active trips on the selected day. Check calendar.txt/feed_info.txt; publication date is not the validity date.");
            var footpaths = new RadiusFootpathGraph(network.Stops.Values, radius, cancellation: cancellation.Token);
            if (!network.Stops.ContainsKey(from) || !network.Stops.ContainsKey(to))
                throw new ArgumentException("Unknown source or target stop ID.");
            FeedMessage? offline = snapshot is null ? null : FeedMessage.Parser.ParseFrom(await File.ReadAllBytesAsync(snapshot, cancellation.Token));
            if (replay && offline?.Header is not { HasTimestamp: true }) throw new InvalidDataException("Replay requires a timestamped feed.");
            TimeProvider clock = replay
                ? new ReplayClock(DateTimeOffset.FromUnixTimeSeconds(checked((long)offline!.Header.Timestamp))) : TimeProvider.System;

            if (compare) clock = new RealtimeBenchmarkClock(clock.GetUtcNow());
            RoutingQuery[]? benchmarkQueries = null;
            if (values.TryGetValue("--queries-file", out var queryFile))
            {
                var inputQueries = JsonSerializer.Deserialize<RoutingQuery[]>(await File.ReadAllTextAsync(queryFile, cancellation.Token))
                    ?? throw new InvalidDataException("Query file is empty.");
                if (inputQueries.Length == 0 || inputQueries.Any(q => q is null || string.IsNullOrWhiteSpace(q.SourceStopId)
                    || string.IsNullOrWhiteSpace(q.TargetStopId) || q.MaxWalkingSeconds.HasValue))
                    throw new InvalidDataException("Query file is empty/invalid; a total walking budget is unsupported.");
                benchmarkQueries = inputQueries.Select(q => new RoutingQuery
                {
                    SourceStopId = q.SourceStopId, TargetStopId = q.TargetStopId, DepartureTime = q.DepartureTime,
                    MultiCriteria = flags.Contains("--pareto"), MaxTransfers = maxTransfers
                }).ToArray();
                if (benchmarkQueries.Any(q => !network.Stops.ContainsKey(q.SourceStopId) || !network.Stops.ContainsKey(q.TargetStopId) || q.DepartureTime < 0))
                    throw new ArgumentException("Queries must reference known stops and nonnegative departure times.");
            }
            var router = new RealtimeRaptorAlgorithm(network, footpaths,
                new RealtimeRaptorOptions { MaxAge = options.MaxFeedAge }, clock);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
            var archive = values.GetValueOrDefault("--archive", DefaultArchive());
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var store = new RealtimeSnapshotStore(archive);
            var collector = new RealtimeCollector(new GzmRealtimeClient(http, options), store, options);
            var hadErrors = false;
            Console.WriteLine($"RAPTOR Realtime: {date:yyyy-MM-dd}, {network.Trips.Count} base trips, radius {radius} m walking, replay={replay}.");
            ITripUpdateSource source = offline is null
                ? new GzmTripUpdateSource(collector, store) : new SnapshotTripUpdateSource(offline);
            if (snapshotPaths is not null) source = new RealtimeSnapshotSequence(snapshotPaths);
            else if (snapshot is not null) source = new RealtimeSnapshotSequence(new[] { snapshot });
            if (compare) source = new RealtimeBenchmarkSource(source, (RealtimeBenchmarkClock)clock, replay);
            var benchmark = compare ? new RealtimeRaptorBenchmark(network, footpaths, router, clock, warmups, repetitions) : null;
            var defaultReport = Path.GetFullPath(values.GetValueOrDefault("--out",
                Path.Combine(RealtimeRaptorMenu.FindDataRoot(), "validation", "session.json")));
            var benchmarkDirectory = values.GetValueOrDefault("--benchmark-dir", Path.Combine(
                Path.GetDirectoryName(defaultReport)!, Path.GetFileNameWithoutExtension(defaultReport) +
                $"-benchmark-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"));
            var benchmarkWriter = compare ? new RealtimeBenchmarkReportWriter(benchmarkDirectory) : null;
            if (compare) Console.WriteLine($"Benchmark: ordinary RAPTOR / scheduled control / realtime; results: {benchmarkDirectory}");
            var configuration = new
                    {
                        GtfsDirectory = gtfs, GtfsInputs = InputFingerprints(gtfs), ServiceDate = date, WalkingRadiusMeters = radius,
                        SourceStopId = from, TargetStopId = to, Departure = departure, Queries = benchmarkQueries,
                        RequestedCycles = count, ApiBaseUri = options.BaseUri, ArchiveDirectory = Path.GetFullPath(archive),
                        MaxTransfers = maxTransfers, Pareto = flags.Contains("--pareto"), Replay = replay,
                        PollIntervalSeconds = options.PollInterval.TotalSeconds, MaxAgeSeconds = options.MaxFeedAge.TotalSeconds,
                        Snapshots = snapshotPaths ?? (snapshot is null ? null : new[] { Path.GetFullPath(snapshot) }),
                        Warmups = warmups, Repetitions = repetitions, QueryFile = values.GetValueOrDefault("--queries-file"),
                        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ProcessorCount = Environment.ProcessorCount
                    };
            var sessionDirectory = values.GetValueOrDefault("--session-dir", compare
                ? Path.Combine(benchmarkDirectory, "session")
                : Path.Combine(Path.GetDirectoryName(defaultReport)!, Path.GetFileNameWithoutExtension(defaultReport) + $"-session-{Guid.NewGuid():N}"));
            if (compare && Path.GetFullPath(sessionDirectory).Equals(Path.GetFullPath(benchmarkDirectory), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Session and benchmark directories must be different.");
            sessionWriter = new RealtimeSessionReportWriter(sessionDirectory);
            await sessionWriter.StartAsync(configuration, count, cancellation.Token);
            Console.WriteLine($"Session history: {sessionDirectory}");
            var cycleNumber = 0;
            using var session = new RealtimeRoutingSession(router, source, options.PollInterval, clock);
            RoutingQuery MakeQuery(DateTimeOffset now) => benchmarkQueries?[0] ?? new RoutingQuery
            {
                SourceStopId = from,
                TargetStopId = to,
                DepartureTime = departure == "now" ? ServiceDayTime.ToSeconds(now.ToUnixTimeSeconds(), date, zone) : ParseTime(departure),
                MaxTransfers = maxTransfers,
                MultiCriteria = flags.Contains("--pareto")
            };
            async IAsyncEnumerable<RealtimeSessionCycle> Cycles()
            {
                if (snapshotPaths is null)
                {
                    await foreach (var cycle in session.RunAsync(MakeQuery, count, cancellation.Token)) yield return cycle;
                }
                else
                    for (var i = 0; i < count; i++) yield return await session.RefreshAndRouteAsync(MakeQuery, cancellation.Token);
            }
            await foreach (var cycle in Cycles())
            {
                cycleNumber++;
                var acquisitionError = cycle.AcquisitionError;
                var update = cycle.LatestUpdateAttempt;
                var query = cycle.Query;
                var result = cycle.Result;
                hadErrors |= !cycle.Success;
                if (acquisitionError is not null) Console.Error.WriteLine(acquisitionError);
                if (update is not null)
                {
                    Console.WriteLine($"Update accepted={update.Accepted}, applied={update.AppliedTrips}, canceled={update.CanceledTrips}, " +
                        $"excluded={update.ExcludedTrips}, route groups={update.RouteGroups}, diagnostics={update.Diagnostics.Count}.");
                    Console.WriteLine($"Feed timestamp={update.GeneratedAt:O}, valid until={update.ValidUntil:O}.");
                    foreach (var issue in update.Diagnostics.Take(5)) Console.WriteLine($"  {issue.Code} {issue.TripId}: {issue.Message}");
                }

                Console.WriteLine($"Mode={(result.UsedRealtime ? "realtime" : "scheduled fallback")}, version={result.Version}, {result.FallbackReason}");
                var journeys = result.Routing.BestJourney is { } best ? new[] { best } : result.Routing.ParetoJourneys.Journeys.ToArray();
                if (journeys.Length == 0) Console.WriteLine("No journey found.");
                foreach (var journey in journeys)
                {
                    Console.WriteLine($"Arrival {FormatTime(journey.ArrivalTime)}, transfers {journey.NumberOfTransfers}.");
                    foreach (var leg in journey.Legs)
                        Console.WriteLine($"  {leg.FromStopId} -> {leg.ToStopId} {FormatTime(leg.DepartureTime)}-{FormatTime(leg.ArrivalTime)} " +
                            $"{(leg.IsWalk ? "walk" : $"trip={leg.TripId} route={leg.RouteId}")}");
                }
                RaptorBenchmarkCycle? comparison = null;
                if (benchmark is not null)
                {
                    comparison = benchmark.Run(cycleNumber, cycle, benchmarkQueries, cancellation.Token);
                    hadErrors |= comparison.Queries.Any(q => q.Original.Error is not null || q.ScheduledControl.Error is not null || q.Realtime.Error is not null);
                    await benchmarkWriter!.WriteAsync(comparison, configuration, cancellation.Token);
                    foreach (var pair in comparison.Queries)
                        Console.WriteLine($"Comparison query {pair.QueryIndex}: original {pair.Original.TimeMs.Median:F3} ms, control {pair.ScheduledControl.TimeMs.Median:F3} ms, " +
                            $"realtime {pair.Realtime.TimeMs.Median:F3} ms; changed={pair.ChangedByRealtime}, arrival delta={pair.EarliestArrivalDeltaSeconds} s, " +
                            $"missed={pair.NewlyMissedTransfers}, recovered={pair.RecoveredTransfers}, baseline agreement={pair.BaselineCriteriaAgree}.");
                }
                var record = new RealtimeSessionRecord(cycleNumber, cycle.EvaluatedAt, replay, query, radius,
                    acquisitionError, update, result, comparison, cycle.AcquisitionMetrics, cycle.SourceSnapshot);
                await sessionWriter.WriteAsync(record, cancellation.Token);
                if (values.TryGetValue("--out", out var reportFile))
                {
                    var path = Path.GetFullPath(reportFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { await File.WriteAllTextAsync(temp, json, cancellation.Token); File.Move(temp, path, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
            sessionStatus = hadErrors ? "completed-with-errors" : "completed";
            exitCode = hadErrors ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { sessionStatus = "canceled"; Console.WriteLine("Realtime routing stopped."); exitCode = 0; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or AggregateException or Google.Protobuf.InvalidProtocolBufferException)
        { sessionError = ex.ToString(); Console.Error.WriteLine($"{ex.Message}\nUsage: {Usage}"); exitCode = 1; }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (sessionWriter is not null && !await RealtimeSessionFinalizer.TryFinishAsync(
                () => sessionWriter.FinishAsync(sessionStatus, sessionError), sessionStatus, sessionError, Console.Error))
                exitCode = 1;
        }
        return exitCode;
    }

    public static async Task<int> ExecuteMenuAsync()
    {
        try
        {
            var selection = RealtimeRaptorMenu.Read();
            return await ExecuteAsync(selection.Arguments, selection.Network);
        }
        catch (OperationCanceledException) { Console.WriteLine("Anulowano konfigurację realtime."); return 130; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException
            or UnauthorizedAccessException or JsonException or Google.Protobuf.InvalidProtocolBufferException)
        { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static object[] InputFingerprints(string directory) => Directory.EnumerateFiles(directory, "*.txt")
        .Order(StringComparer.Ordinal).Select(file =>
        {
            using var stream = File.OpenRead(file);
            return (object)new { File = Path.GetFileName(file), Bytes = stream.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)) };
        }).ToArray();

    private static string DefaultArchive()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "DataRealTime", "Slask")))
                    return Path.Combine(dir.FullName, "DataRealTime", "Slask", "live");
        return Path.GetFullPath(Path.Combine("DataRealTime", "Slask", "live"));
    }

    private static int ParseTime(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) ||
            !int.TryParse(parts[2], out var s) || h < 0 || m is < 0 or > 59 || s is < 0 or > 59)
            throw new ArgumentException("Departure must be HH:mm:ss (hours may exceed 23) or now.");
        return checked(h * 3600 + m * 60 + s);
    }
    private static string FormatTime(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
    private sealed class ReplayClock(DateTimeOffset timestamp) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => timestamp; }
}
