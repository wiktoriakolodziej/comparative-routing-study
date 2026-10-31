using System.Diagnostics;
using System.Security.Cryptography;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using Google.Protobuf;
using TransitRealtime;

namespace AlgorithmComparison.RaptorRealtime;

public sealed class RealtimeRaptorAlgorithm : RaptorAlgorithm
{
    private readonly TransitNetwork network;
    private readonly object routingGate = new();
    private RaptorInput? routingInput;
    private CancellationToken routingCancellation;
    private readonly TimeSpan preparationTime;
    private readonly TimeProvider time;
    private readonly RealtimeRaptorOptions options;
    private readonly Dictionary<string, int> stopIndex;
    private readonly string[] stopIds;
    private readonly PreparedTimetable baseline;
    private readonly TripUpdateAdapter adapter;
    private readonly object updateGate = new();
    private PreparedTimetable current;
    private RealtimeUpdateReport? lastUpdate;

    public override string Name => "RAPTOR Realtime";
    public override TimeSpan PreprocessingTime => preparationTime;
    protected override CancellationToken RoutingCancellation => routingCancellation;
    protected override RaptorInput GetInput(TransitNetwork network) =>
        routingInput ?? throw new InvalidOperationException("No timetable selected for this query.");

    public override void Prepare(TransitNetwork network)
    {
        if (!ReferenceEquals(network, this.network))
            throw new ArgumentException("This realtime router is bound to its original network/service day.", nameof(network));
    }
    public RealtimeUpdateReport? LastUpdate => Volatile.Read(ref lastUpdate);

    public RealtimeRaptorAlgorithm(TransitNetwork network, IFootpathGraph footpaths,
        RealtimeRaptorOptions? options = null, TimeProvider? time = null)
        : base(footpaths, network.Stops.Keys)
    {
        var preparation = Stopwatch.StartNew();
        this.network = network;
        this.time = time ?? TimeProvider.System;
        this.options = options ?? new();
        if (this.options.MaxAge <= TimeSpan.Zero || this.options.FutureTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        stopIds = network.Stops.Keys.Order(StringComparer.Ordinal).ToArray();
        stopIndex = stopIds.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        var trips = PreparedTimetable.CopyBase(network);
        baseline = PreparedTimetable.Build(trips, stopIndex, "scheduled", null, null);
        current = baseline;
        adapter = new(network, this.options, trips, stopIndex);
        preparationTime = preparation.Elapsed;
    }

    public RealtimeUpdateReport ApplyFeed(FeedMessage feed)
    {
        var updateWatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        // Detach from the caller's mutable protobuf before preparing a view.
        var copy = feed.Clone();
        var version = Convert.ToHexString(SHA256.HashData(copy.ToByteArray()));
        lock (updateGate)
        {
            var routeGroupsMs = 0d;
            try
            {
                var next = adapter.Build(copy, time.GetUtcNow(), version, out routeGroupsMs);
                if (current.Report?.GeneratedAt is { } previous && next.Report!.GeneratedAt < previous)
                    throw new InvalidDataException("Feed timestamp regressed; retaining the previous view.");
                next = next with { Report = next.Report! with { Metrics = new(updateWatch.Elapsed.TotalMilliseconds,
                    routeGroupsMs, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) } };
                Volatile.Write(ref current, next);
                Volatile.Write(ref lastUpdate, next.Report);
                return next.Report!;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                var rejected = new RealtimeUpdateReport(false, version, null, null, 0, 0, 0, 0,
                    Array.AsReadOnly(new[] { new UpdateDiagnostic("RejectedFeed", null, ex.Message) }))
                { Metrics = new(updateWatch.Elapsed.TotalMilliseconds, routeGroupsMs, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) };
                Volatile.Write(ref lastUpdate, rejected);
                return rejected;
            }
        }
    }

    public IReadOnlyList<EffectiveStopTime>? GetEffectiveStopTimes(string tripId)
    {
        var view = ActiveView(Volatile.Read(ref current), time.GetUtcNow());
        return view.Trips.TryGetValue(tripId, out var trip) ? Array.AsReadOnly(trip.Stops) : null;
    }

    public override RoutingResult Route(RoutingQuery query, TransitNetwork network)
    {
        Prepare(network);
        return RouteDetailed(query).Routing;
    }

    public RealtimeRoutingResult RouteDetailed(RoutingQuery query, CancellationToken cancellationToken = default)
    {
        if (query.DepartureTime < 0 || query.MaxTransfers is < 0)
            throw new ArgumentOutOfRangeException(nameof(query));
        if (query.MaxWalkingSeconds.HasValue)
            throw new NotSupportedException("A total walking budget requires another label dimension and is not supported.");
        lock (routingGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var published = Volatile.Read(ref current);
            var view = ActiveView(published, time.GetUtcNow());
            var realtime = !ReferenceEquals(view, baseline);
            routingInput = view.Input;
            routingCancellation = cancellationToken;
            try
            {
                var boundedQuery = query.MaxTransfers.HasValue ? new RoutingQuery
                {
                    SourceStopId = query.SourceStopId,
                    TargetStopId = query.TargetStopId,
                    DepartureTime = query.DepartureTime,
                    MultiCriteria = query.MultiCriteria,
                    MaxTransfers = Math.Min(query.MaxTransfers.Value, Math.Max(0, view.Trips.Count - 1))
                } : query;
                var result = base.Route(boundedQuery, network);
                return new(result, realtime, view.Version, realtime ? null : published.Report is null
                    ? "No accepted realtime snapshot; scheduled times are used."
                    : "Realtime predictions expired; scheduled times are used.", published.Report, LastMetrics!);
            }
            finally
            {
                routingInput = null;
                routingCancellation = default;
            }
        }
    }

    private PreparedTimetable ActiveView(PreparedTimetable candidate, DateTimeOffset now) =>
        candidate.ValidUntil is { } until && now > until ? baseline : candidate;

}
