using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.RaptorRealtime;
using StaticRaptor = AlgorithmComparison.Algorithms.Raptor.RaptorAlgorithm;

namespace AlgorithmComparison.CLI;

public sealed record BenchmarkMeasurement(double TimeMs, long AllocatedBytes);
public sealed record BenchmarkStatistics(int Count, double Mean, double Median, double P95, double Maximum)
{
    public static BenchmarkStatistics Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0);
        var n = sorted.Length;
        return new(n, sorted.Average(), n % 2 == 0 ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2 : sorted[n / 2],
            sorted[(int)Math.Ceiling(n * .95) - 1], sorted[^1]);
    }
}
public sealed record RaptorBenchmarkVariant(string Id, IReadOnlyList<Journey> Journeys,
    IReadOnlyList<BenchmarkMeasurement> Samples, string? Error)
{
    public BenchmarkStatistics TimeMs => BenchmarkStatistics.Of(Samples.Select(s => s.TimeMs));
    public BenchmarkStatistics AllocatedMB => BenchmarkStatistics.Of(Samples.Select(s => s.AllocatedBytes / 1_000_000d));
}
public sealed record TransferAssessment(int Index, string FromTripId, string ToTripId, bool Catchable, int ReadyAt, int DepartsAt);
public sealed record JourneyAssessment(string Status, int? ArrivalTime, IReadOnlyList<TransferAssessment> Transfers);
public sealed record RaptorQueryComparison(int QueryIndex, RoutingQuery Query,
    RaptorBenchmarkVariant Original, RaptorBenchmarkVariant ScheduledControl, RaptorBenchmarkVariant Realtime,
    bool? BaselineCriteriaAgree, bool? ChangedByRealtime, int? EarliestArrivalDeltaSeconds,
    int DepartureRelativeToObservationSeconds, int? ReplannedVsOriginalPlanSeconds,
    IReadOnlyList<JourneyAssessment> OriginalJourneysInRealtime,
    IReadOnlyList<JourneyAssessment> ControlJourneysInRealtime,
    int NewlyMissedTransfers, int RecoveredTransfers, bool ComparableToPreviousCycle);
public sealed record RaptorBenchmarkCycle(int Cycle, DateTimeOffset EvaluatedAt, bool UsedRealtime, string Version,
    string? FallbackReason, string? AcquisitionError, RealtimeUpdateReport? Update,
    RealtimeAcquisitionMetrics? Acquisition, int Warmups, int Repetitions,
    double OriginalPreparationMs, double ControlPreparationMs, IReadOnlyList<RaptorQueryComparison> Queries);

public sealed class RealtimeRaptorBenchmark
{
    private readonly TransitNetwork network;
    private readonly StaticRaptor original;
    private readonly RealtimeRaptorAlgorithm control;
    private readonly RealtimeRaptorAlgorithm realtime;
    private readonly int warmups;
    private readonly int repetitions;
    private readonly double originalPreparationMs;
    private readonly double controlPreparationMs;
    private readonly Dictionary<string, Dictionary<string, bool>> previousTransfers = new(StringComparer.Ordinal);

    public RealtimeRaptorBenchmark(TransitNetwork network, IFootpathGraph walking, RealtimeRaptorAlgorithm realtime,
        TimeProvider clock, int warmups = 1, int repetitions = 5)
    {
        if (warmups < 0 || repetitions < 1) throw new ArgumentOutOfRangeException(nameof(repetitions));
        this.network = network;
        this.realtime = realtime;
        this.warmups = warmups;
        this.repetitions = repetitions;
        var watch = Stopwatch.StartNew();
        original = new(walking);
        original.Prepare(network);
        originalPreparationMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        control = new(network, walking, time: clock);
        controlPreparationMs = watch.Elapsed.TotalMilliseconds;
    }

    public RaptorBenchmarkCycle Run(int cycleNumber, RealtimeSessionCycle cycle, IReadOnlyList<RoutingQuery>? queries = null,
        CancellationToken cancellation = default)
    {
        queries ??= new[] { cycle.Query };
        var comparisons = new List<RaptorQueryComparison>();
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < queries.Count; index++)
        {
            var query = queries[index];
            Func<RoutingResult>[] engines = { () => original.Route(query, network), () => control.RouteDetailed(query, cancellation).Routing,
                () => realtime.RouteDetailed(query, cancellation).Routing };
            var results = new RoutingResult?[3];
            var errors = new string?[3];
            var samples = Enumerable.Range(0, 3).Select(_ => new List<BenchmarkMeasurement>()).ToArray();
            for (var round = -warmups; round < repetitions; round++)
                for (var position = 0; position < 3; position++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var engine = ((round + warmups + position) % 3);
                    if (errors[engine] is not null) continue;
                    try
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var watch = Stopwatch.StartNew();
                        var result = engines[engine]();
                        watch.Stop();
                        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        results[engine] = result;
                        if (round >= 0) samples[engine].Add(new(watch.Elapsed.TotalMilliseconds, allocated));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    { errors[engine] = $"{ex.GetType().Name}: {ex.Message}"; }
                }
            var variants = Enumerable.Range(0, 3).Select(i => new RaptorBenchmarkVariant(
                new[] { "raptor-original", "raptor-scheduled-control", "raptor-realtime" }[i],
                Journeys(results[i], query.MultiCriteria), samples[i], errors[i])).ToArray();
            var originalResult = variants[0];
            var baseline = variants[1];
            var updated = variants[2];
            var originalAssessments = cycle.Result.UsedRealtime && originalResult.Error is null
                ? originalResult.Journeys.Select(j => Assess(j, query, realtime)).ToArray() : Array.Empty<JourneyAssessment>();
            var controlAssessments = cycle.Result.UsedRealtime && baseline.Error is null
                ? baseline.Journeys.Select(j => Assess(j, query, realtime)).ToArray() : Array.Empty<JourneyAssessment>();
            var key = $"{index}|{query.SourceStopId}|{query.TargetStopId}|{query.DepartureTime}|{query.MaxTransfers}|{query.MultiCriteria}";
            currentKeys.Add(key);
            var transferStates = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var j = 0; j < controlAssessments.Length; j++)
                foreach (var transfer in controlAssessments[j].Transfers)
                    transferStates[$"{PathSignature(baseline.Journeys[j], false)}|{transfer.Index}"] = transfer.Catchable;
            var comparable = previousTransfers.TryGetValue(key, out var previous);
            var lost = 0;
            var recovered = 0;
            if (cycle.Result.UsedRealtime && baseline.Error is null)
            {
                foreach (var (transfer, catchable) in transferStates)
                {
                    var hadState = comparable && previous!.TryGetValue(transfer, out var wasCatchable);
                    if (!catchable && (!hadState || previous![transfer])) lost++;
                    if (catchable && hadState && !previous![transfer]) recovered++;
                }
                previousTransfers[key] = transferStates;
            }
            else previousTransfers.Remove(key);
            bool? agreement = originalResult.Error is null && baseline.Error is null
                ? CriteriaSignature(originalResult.Journeys) == CriteriaSignature(baseline.Journeys) : null;
            bool? changed = cycle.Result.UsedRealtime && baseline.Error is null && updated.Error is null
                ? SetSignature(baseline.Journeys) != SetSignature(updated.Journeys) : null;
            var baseArrival = baseline.Journeys.Select(j => (int?)j.ArrivalTime).Min();
            var rtArrival = updated.Journeys.Select(j => (int?)j.ArrivalTime).Min();
            comparisons.Add(new(index, query, originalResult, baseline, updated, agreement, changed,
                cycle.Result.UsedRealtime ? rtArrival - baseArrival : null,
                query.DepartureTime - ServiceDayTime.ToSeconds(cycle.EvaluatedAt.ToUnixTimeSeconds(), network.ServiceDate,
                    TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")),
                cycle.Result.UsedRealtime ? rtArrival - originalAssessments.Where(a => a.Status == "Feasible").Select(a => a.ArrivalTime).Min() : null,
                originalAssessments, controlAssessments,
                lost, recovered, comparable && cycle.Result.UsedRealtime));
        }
        foreach (var oldKey in previousTransfers.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
            previousTransfers.Remove(oldKey);
        return new(cycleNumber, cycle.EvaluatedAt, cycle.Result.UsedRealtime, cycle.Result.Version,
            cycle.Result.FallbackReason, cycle.AcquisitionError, cycle.LatestUpdateAttempt, cycle.AcquisitionMetrics,
            warmups, repetitions, originalPreparationMs, controlPreparationMs, comparisons);
    }

    private static IReadOnlyList<Journey> Journeys(RoutingResult? result, bool pareto) => result is null ? Array.Empty<Journey>()
        : pareto ? result.ParetoJourneys.Journeys.ToArray() : result.BestJourney is { } best ? new[] { best } : Array.Empty<Journey>();

    private static string CriteriaSignature(IReadOnlyList<Journey> journeys) => string.Join(';', journeys
        .Select(j => (j.ArrivalTime, j.NumberOfTransfers)).Distinct().Order().Select(p => $"{p.ArrivalTime},{p.NumberOfTransfers}"));
    private static string SetSignature(IReadOnlyList<Journey> journeys) => string.Join(';', journeys.Select(j => PathSignature(j, true)).Order(StringComparer.Ordinal));
    private static string PathSignature(Journey j, bool times) => JsonSerializer.Serialize(j.Legs.Select(l => new
    { l.FromStopId, l.ToStopId, l.TripId, Departure = times ? (int?)l.DepartureTime : null, Arrival = times ? (int?)l.ArrivalTime : null }));

    public static JourneyAssessment Assess(Journey journey, RoutingQuery query, RealtimeRaptorAlgorithm router)
    {
        var ready = query.DepartureTime;
        string? previousTrip = null;
        var transfers = new List<TransferAssessment>();
        foreach (var leg in journey.Legs)
        {
            if (leg.IsWalk) { ready = checked(ready + leg.Duration); continue; }
            var trip = router.GetEffectiveStopTimes(leg.TripId!);
            if (trip is null) return new("TripUnavailable", null, transfers);
            var matches = (from i in Enumerable.Range(0, trip.Count)
                from j in Enumerable.Range(i + 1, trip.Count - i - 1)
                where trip[i].StopId == leg.FromStopId && trip[j].StopId == leg.ToStopId
                select (Board: trip[i], Alight: trip[j])).Take(2).ToArray();
            if (matches.Length != 1) return new("AmbiguousOrMissingStopVisit", null, transfers);
            var (board, alight) = matches[0];
            if (!board.CanBoard || !alight.CanAlight) return new("BoardingOrAlightingForbidden", null, transfers);
            var catchable = ready <= board.Departure;
            if (previousTrip is not null)
                transfers.Add(new(transfers.Count, previousTrip, leg.TripId!, catchable, ready, board.Departure));
            if (!catchable) return new(previousTrip is null ? "MissedFirstBoarding" : "MissedTransfer", null, transfers);
            ready = alight.Arrival;
            previousTrip = leg.TripId;
        }
        return new("Feasible", ready, transfers);
    }
}

public sealed class RealtimeBenchmarkReportWriter
{
    private sealed class VariantTotals
    {
        public readonly List<double> Times = new();
        public readonly List<double> AllocatedMB = new();
        public int Errors;
        public void Add(RaptorBenchmarkVariant variant)
        {
            if (variant.Error is not null) { Errors++; return; }
            foreach (var sample in variant.Samples)
            {
                Times.Add(sample.TimeMs);
                AllocatedMB.Add(sample.AllocatedBytes / 1_000_000d);
            }
        }
    }

    private const string CsvHeader = "cycle,query,departure,variant,used_realtime,version,mean_ms,median_ms,p95_ms,max_ms,mean_allocated_MB,journeys,changed,arrival_delta_seconds,departure_relative_to_observation_seconds,replanned_vs_original_plan_seconds,newly_missed_transfers,recovered_transfers,error";
    private const string UpdatesHeader = "cycle,evaluated_at,used_realtime,version,acquisition_ms,apply_ms,route_groups_ms,acquisition_error,update_accepted";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string directory;
    private readonly Dictionary<string, VariantTotals> variants = new(StringComparer.Ordinal);
    private int cycleCount;
    private long queryCount;
    private long realtimeCycles;
    private long changedQueries;
    private long comparableQueries;
    private long baselineMismatches;
    private long missedTransfers;
    private long recoveredTransfers;
    private long assessedJourneys;
    private long feasibleJourneys;
    private double acquisitionMs;
    private double updateMs;
    private bool configurationWritten;

    public RealtimeBenchmarkReportWriter(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        if (Directory.EnumerateFileSystemEntries(this.directory).Any())
            throw new IOException("Katalog benchmarku musi być pusty, aby nie nadpisać wcześniejszych pomiarów.");
    }

    public async Task WriteAsync(RaptorBenchmarkCycle cycle, object configuration, CancellationToken cancellation = default)
    {
        if (cycle.Cycle != cycleCount + 1) throw new InvalidOperationException("Benchmark cycles must be written once in consecutive order.");
        if (!configurationWritten)
        {
            await WriteAtomicAsync("configuration.json", JsonSerializer.Serialize(configuration, Json), cancellation);
            configurationWritten = true;
        }
        await WriteAtomicAsync($"cycle-{cycle.Cycle:000000}.json", JsonSerializer.Serialize(cycle, Json), cancellation);
        var rows = new List<string>();
        foreach (var q in cycle.Queries)
            foreach (var v in new[] { q.Original, q.ScheduledControl, q.Realtime })
                rows.Add(CsvRow(cycle.Cycle, q.QueryIndex, q.Query.DepartureTime, v.Id, cycle.UsedRealtime, cycle.Version,
                    v.TimeMs.Mean, v.TimeMs.Median, v.TimeMs.P95, v.TimeMs.Maximum, v.AllocatedMB.Mean, v.Journeys.Count,
                    q.ChangedByRealtime, q.EarliestArrivalDeltaSeconds, q.DepartureRelativeToObservationSeconds,
                    q.ReplannedVsOriginalPlanSeconds, q.NewlyMissedTransfers, q.RecoveredTransfers, v.Error));
        var updateRow = CsvRow(cycle.Cycle, cycle.EvaluatedAt.ToString("O"), cycle.UsedRealtime, cycle.Version,
            cycle.Acquisition?.TotalMs, cycle.Update?.Metrics?.TotalMs, cycle.Update?.Metrics?.RouteGroupsMs,
            cycle.AcquisitionError, cycle.Update?.Accepted);
        await RealtimeReportFiles.AppendAsync(new[]
        {
            (Path.Combine(directory, "comparison.csv"), CsvHeader, rows.Count == 0 ? "" : string.Join(Environment.NewLine, rows) + Environment.NewLine),
            (Path.Combine(directory, "updates.csv"), UpdatesHeader, updateRow + Environment.NewLine)
        }, cancellation);

        cycleCount++;
        if (cycle.UsedRealtime) realtimeCycles++;
        acquisitionMs += cycle.Acquisition?.TotalMs ?? 0;
        updateMs += cycle.Update?.Metrics?.TotalMs ?? 0;
        foreach (var query in cycle.Queries)
        {
            queryCount++;
            if (query.ChangedByRealtime == true) changedQueries++;
            if (query.ChangedByRealtime.HasValue) comparableQueries++;
            if (query.BaselineCriteriaAgree == false) baselineMismatches++;
            missedTransfers += query.NewlyMissedTransfers;
            recoveredTransfers += query.RecoveredTransfers;
            assessedJourneys += query.OriginalJourneysInRealtime.Count;
            feasibleJourneys += query.OriginalJourneysInRealtime.Count(a => a.Status == "Feasible");
            foreach (var variant in new[] { query.Original, query.ScheduledControl, query.Realtime })
            {
                if (!variants.TryGetValue(variant.Id, out var totals)) variants.Add(variant.Id, totals = new());
                totals.Add(variant);
            }
        }
        var lines = new List<string>
        {
            "RAPTOR — porównanie rozkładu bazowego z realtime",
            $"Cykle: {cycleCount}; zapytania × cykle: {queryCount}; cykle z prognozą: {realtimeCycles}.",
            $"Zmienione wyniki (kontrola bazowa vs realtime): {changedQueries} / {comparableQueries}.",
            $"Rozbieżności kryteriów zwykły RAPTOR vs kontrola bazowa: {baselineMismatches}.",
            $"Nowe utracone przesiadki: {missedTransfers}; odzyskane: {recoveredTransfers}.",
            $"Ocenione podróże zwykłego RAPTOR-a: {assessedJourneys}; wykonalne: {feasibleJourneys}.",
            "Przesiadki dotyczą wyłącznie sprawdzonych podróży z kontrolnego rozkładu bazowego. Odzyskanie wymaga poprzedniego cyklu z identycznym zapytaniem.",
            "Dodatnia różnica przyjazdu oznacza późniejszą prognozę. Różnica nie jest pomiarem faktycznego przyjazdu pojazdu.",
            "ReplannedVsOriginalPlanSeconds porównuje nowy wynik z najwcześniejszą nadal wykonalną podróżą zwykłego RAPTOR-a ocenioną według tej samej prognozy. Wartość ujemna oznacza wcześniejszy przyjazd; brak wykonalnego planu daje null.",
            "DepartureRelativeToObservationSeconds < 0 oznacza retrospektywne zapytanie sprzed obserwacji, nie planowanie na podstawie prognozy dostępnej przy wyjeździe.",
            "Każdy wariant: ten sam GTFS, graf pieszy, zapytanie i limit. Rozgrzewki wyłączono ze statystyk; kolejność wariantów rotuje.",
            "Pomiary wykonywane sekwencyjnie w jednym procesie, bez izolacji workerów. Alokacje zapytania: bieżący wątek, MB = 1 000 000 B; nie jest to zajęcie RAM.",
            "Czas grupowania jest częścią zastosowania feedu; nie należy ich sumować. Pobranie obejmuje odczyt, parsowanie i archiwizację.",
            "Wersja czasu jest zamrożona na cykl dla porównywalności powtórzeń. UsedRealtime może oznaczać zachowaną prognozę po awarii; sprawdź AcquisitionError.",
            "Historia kosztów kolejnych aktualizacji: updates.csv. Pełne podróże: cycle-*.json. Statystyki przechowują tylko próbki liczbowe, bez podróży.",
            FormattableString.Invariant($"Łączne pobranie/odczyt: {acquisitionMs:F3} ms; zastosowanie feedów: {updateMs:F3} ms."),
            ""
        };
        foreach (var (id, totals) in variants)
        {
            var time = BenchmarkStatistics.Of(totals.Times);
            var allocated = BenchmarkStatistics.Of(totals.AllocatedMB);
            lines.Add(FormattableString.Invariant($"{id}: n={time.Count}; średnia {time.Mean:F3} ms; mediana {time.Median:F3} ms; P95 {time.P95:F3} ms; max {time.Maximum:F3} ms; średnia alokacja {allocated.Mean:F3} MB; błędy {totals.Errors}."));
        }
        lines.Add(FormattableString.Invariant($"Ostatni cykl {cycle.Cycle}: realtime={cycle.UsedRealtime}, pobranie/odczyt {cycle.Acquisition?.TotalMs:F3} ms; zastosowanie {cycle.Update?.Metrics?.TotalMs:F3} ms (w tym grupy {cycle.Update?.Metrics?.RouteGroupsMs:F3} ms); błąd: {cycle.AcquisitionError}."));
        await WriteAtomicAsync("summary.txt", string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellation);
    }

    private static string CsvRow(params object?[] values) => string.Join(',', values.Select(value =>
        "\"" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));

    private Task WriteAtomicAsync(string name, string content, CancellationToken cancellation) =>
        RealtimeReportFiles.WriteAtomicAsync(Path.Combine(directory, name), content, cancellation);
}
