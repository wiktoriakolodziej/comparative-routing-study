using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;

namespace AlgorithmComparison.Core.Walking;

public sealed class WalkingTransferService
{
    public IFootpathGraph Graph { get; }
    public IReadOnlyCollection<string> StopIds { get; }
    private readonly HashSet<string> knownStops;
    private readonly bool validateEdges;

    public WalkingTransferService(IEnumerable<Stop> stops, double radiusMeters = 500,
        double speedMetersPerSecond = 1.11, CancellationToken cancellation = default)
        : this(new RadiusFootpathGraph(stops, radiusMeters, speedMetersPerSecond, cancellation)) { }
    public WalkingTransferService(RadiusFootpathGraph graph)
        : this(graph, graph?.Outgoing.Keys ?? throw new ArgumentNullException(nameof(graph))) { }

    public WalkingTransferService(IFootpathGraph graph, IEnumerable<string> stopIds)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        ArgumentNullException.ThrowIfNull(stopIds);
        knownStops = stopIds.ToHashSet(StringComparer.Ordinal);
        StopIds = Array.AsReadOnly(knownStops.Order(StringComparer.Ordinal).ToArray());
        validateEdges = graph is not RadiusFootpathGraph;
        if (graph is RadiusFootpathGraph radius && radius.Outgoing.Keys.Any(id => !knownStops.Contains(id)))
            throw new ArgumentException("Walking graph contains unknown stops.", nameof(graph));

    }

    public WalkingPropagationResult<string> PropagateTimes(IReadOnlyDictionary<string, int> seeds,
        WalkingDirection direction = WalkingDirection.ForwardEarliestArrival, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        return PropagateTimes(seeds.Select(p => new WalkingSeed<string>(p.Key, p.Value, 0, p.Key)), direction, cancellation);
    }

    public WalkingPropagationResult<TContext> PropagateTimes<TContext>(IEnumerable<WalkingSeed<TContext>> seeds,
        WalkingDirection direction = WalkingDirection.ForwardEarliestArrival, CancellationToken cancellation = default,
        Func<WalkingLabel<TContext>, bool>? accept = null) =>
        Propagate(seeds, false, direction, cancellation, accept);

    public WalkingPropagationResult<TContext> PropagatePareto<TContext>(IEnumerable<WalkingSeed<TContext>> seeds,
        WalkingDirection direction = WalkingDirection.ForwardEarliestArrival, CancellationToken cancellation = default,
        Func<WalkingLabel<TContext>, bool>? accept = null) =>
        Propagate(seeds, true, direction, cancellation, accept);

    private WalkingPropagationResult<TContext> Propagate<TContext>(IEnumerable<WalkingSeed<TContext>> seeds,
        bool pareto, WalkingDirection direction, CancellationToken cancellation, Func<WalkingLabel<TContext>, bool>? accept)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        cancellation.ThrowIfCancellationRequested();
        var labels = new Dictionary<string, List<WalkingLabel<TContext>>>(StringComparer.Ordinal);
        var pending = new Queue<WalkingLabel<TContext>>();
        var active = new HashSet<WalkingLabel<TContext>>();
        foreach (var seed in seeds)
        {
            cancellation.ThrowIfCancellationRequested();
            if (seed is null || seed.Boardings < 0 || !knownStops.Contains(seed.StopId))
                throw new ArgumentException("Seeds require a known stop and non-negative boardings.", nameof(seeds));
            Offer(seed.StopId, seed.Time, seed, null, null);
        }
        while (pending.TryDequeue(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!active.Contains(current)) continue;
            WalkingExpansion.Run(Graph, current.StopId, direction == WalkingDirection.BackwardLatestDeparture, edge =>
            {
                var nextStop = direction == WalkingDirection.ForwardEarliestArrival ? edge.ToStopId : edge.FromStopId;
                if (validateEdges && (edge.DurationSeconds < 0 || !knownStops.Contains(nextStop)
                    || (direction == WalkingDirection.ForwardEarliestArrival ? edge.FromStopId : edge.ToStopId) != current.StopId))
                    throw new InvalidDataException("Invalid walking edge.");
                var nextTime = direction == WalkingDirection.ForwardEarliestArrival
                    ? checked(current.Time + edge.DurationSeconds) : checked(current.Time - edge.DurationSeconds);
                return (Stop: nextStop, Time: nextTime, Edge: edge);
            }, next =>
            {
                if (next.Stop != current.StopId) Offer(next.Stop, next.Time, current.Origin, current, next.Edge);
            }, cancellation);
        }
        return new WalkingPropagationResult<TContext>(direction, labels);

        bool NoWorseTime(int a, int b) => direction == WalkingDirection.ForwardEarliestArrival ? a <= b : a >= b;
        void Offer(string stop, int time, WalkingSeed<TContext> origin, WalkingLabel<TContext>? parent, Footpath? step)
        {
            labels.TryGetValue(stop, out var bag);
            // Equality is discarded too: zero-duration cycles cannot cause infinite work.
            if (bag is not null && bag.Any(old => NoWorseTime(old.Time, time) && (!pareto || old.Boardings <= origin.Boardings))) return;
            var candidate = new WalkingLabel<TContext>(stop, time, origin, direction, parent, step);
            // Algorithms can reject globally dominated labels before further walking.
            // Such rejection must also dominate every walking continuation.
            if (accept is not null && !accept(candidate)) return;
            if (bag is null) labels.Add(stop, bag = new List<WalkingLabel<TContext>>());
            for (var i = bag.Count - 1; i >= 0; i--)
                if (NoWorseTime(time, bag[i].Time) && (!pareto || origin.Boardings <= bag[i].Boardings))
                {
                    active.Remove(bag[i]);
                    bag.RemoveAt(i);
                }
            bag.Add(candidate);
            active.Add(candidate);
            pending.Enqueue(candidate);
        }
    }
}
