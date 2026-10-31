using System.Reflection;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using ModelConnection = AlgorithmComparison.Core.Models.Connection;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class DijkstraTransferLimitTests
{
    private static Trip Trip(string id, params (string Stop, int Arrival, int Departure)[] times) => new()
    {
        Id = id,
        RouteId = id,
        ServiceId = "service",
        StopTimes = times.Select((t, i) => new StopTime
        {
            TripId = id,
            StopId = t.Stop,
            StopSequence = i,
            ArrivalTime = t.Arrival,
            DepartureTime = t.Departure
        }).ToArray()
    };
    private static TransitNetwork Network(string[] stopIds, params Trip[] trips)
    {
        var stops = stopIds.Select((id, i) => new Stop { Id = id, Name = id, Latitude = 50 + i * 0.001, Longitude = 19 }).ToDictionary(s => s.Id);
        var connections = trips.SelectMany(trip => trip.StopTimes.Zip(trip.StopTimes.Skip(1), (a, b) => new ModelConnection
        {
            Id = $"{trip.Id}/{a.StopSequence}",
            TripId = trip.Id,
            RouteId = trip.RouteId,
            DepartureStopId = a.StopId,
            ArrivalStopId = b.StopId,
            DepartureTime = a.DepartureTime,
            ArrivalTime = b.ArrivalTime
        })).OrderBy(c => c.DepartureTime).ThenBy(c => c.ArrivalTime).ToArray();
        return new TransitNetwork
        {
            Stops = stops,
            Trips = trips.ToDictionary(t => t.Id),
            Routes = trips.ToDictionary(t => t.Id, t => new Route { Id = t.Id, ShortName = t.Id, RouteType = RouteType.Bus }),
            Connections = connections,
            ConnectionDict = connections.ToDictionary(c => c.Id),
            ServiceDate = new(2026, 10, 21),
        };
    }
    private static FootpathGraph Footpaths(IEnumerable<string> stops, params (string From, string To, int Duration)[] edges)
    {
        var paths = edges.Select(e => new Footpath { FromStopId = e.From, ToStopId = e.To, DurationSeconds = e.Duration }).ToArray();
        return new FootpathGraph
        {
            Nodes = stops.ToDictionary(id => id, id => new Node { StopId = id }),
            Edges = paths,
            OutgoingEdges = paths.GroupBy(p => p.FromStopId).ToDictionary(g => g.Key, g => g.ToHashSet()),
            IncomingEdges = paths.GroupBy(p => p.ToStopId).ToDictionary(g => g.Key, g => g.ToHashSet())
        };
    }
    private static RoutingQuery Query(int? limit, bool pareto = false) => new()
    { SourceStopId = "S", TargetStopId = "Z", DepartureTime = 0, MaxTransfers = limit, MultiCriteria = pareto };
    private static TimeDependentDijkstraAlgorithm Router(TransitNetwork net, IFootpathGraph? graph = null) =>
        new(graph ?? new SameNameFootpathGraph(net.Stops.Values));
    private static TDDijkstraInput State(TimeDependentDijkstraAlgorithm router) =>
        ((IExtractingPreprocessor<TDDijkstraInput>)typeof(TimeDependentDijkstraAlgorithm)
            .GetField("preprocessor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!).GetValue();
    private static (int, int)[] Points(RoutingResult result, bool pareto) =>
        (pareto ? result.ParetoJourneys.Journeys : result.BestJourney is { } journey ? new[] { journey } : Array.Empty<Journey>())
            .Select(j => (j.ArrivalTime, j.NumberOfTransfers)).OrderBy(p => p.ArrivalTime).ThenBy(p => p.NumberOfTransfers).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LimitRejectsTransfersBeforeAllocationAndResetsBetweenQueries(bool pareto)
    {
        var net = Network(new[] { "S", "A", "Z" }, Trip("first", ("S", 0, 0), ("A", 10, 10)),
            Trip("second", ("A", 11, 11), ("Z", 20, 20)));
        var router = Router(net);
        foreach (var limit in new int?[] { 0, 1, null, 0, int.MaxValue })
        {
            var result = router.Route(Query(limit, pareto), net);
            Assert.Equal(limit == 0 ? Array.Empty<(int, int)>() : new[] { (20, 1) }, Points(result, pareto));
            if (limit is int cap)
                Assert.All(State(router).LabelsByState.Values.SelectMany(x => x), label => Assert.True(label.Transfers <= cap));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtLimitCanContinueSameCourseAndWalkButCannotBoardOtherCourse(bool pareto)
    {
        var net = Network(new[] { "S", "B", "C", "Z" }, Trip("through", ("S", 5, 5), ("B", 20, 22), ("C", 30, 30)),
            Trip("express", ("B", 21, 21), ("Z", 25, 25)));
        var graph = Footpaths(net.Stops.Keys, ("C", "Z", 5));
        var router = Router(net, graph);
        var result = router.Route(Query(0, pareto), net);
        Assert.Equal(new[] { (35, 0) }, Points(result, pareto));
        var journey = pareto ? Assert.Single(result.ParetoJourneys.Journeys) : result.BestJourney!;
        Assert.Equal(2, journey.Legs.Count);
        Assert.False(journey.Legs[0].IsWalk);
        Assert.Equal("C", journey.Legs[0].ToStopId);
        Assert.True(journey.Legs[1].IsWalk);
        Assert.All(State(router).LabelsByState.Values.SelectMany(x => x), label => Assert.InRange(label.Boardings, 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetDiscoveryDoesNotTerminateBeforeEarlierRouteIsFound(bool pareto)
    {
        var net = Network(new[] { "S", "A", "Z" }, Trip("direct", ("S", 0, 0), ("Z", 100, 100)),
            Trip("first", ("S", 1, 1), ("A", 5, 5)), Trip("last", ("A", 6, 6), ("Z", 10, 10)));
        var result = Router(net).Route(Query(1, pareto), net);
        Assert.Equal(pareto ? new[] { (10, 1), (100, 0) } : new[] { (10, 1) }, Points(result, pareto));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyTerminationLeavesUnneededLaterLabelsInQueue(bool pareto)
    {
        var net = Network(new[] { "S", "B", "Z" }, Trip("late", ("S", 0, 0), ("B", 200, 200)),
            Trip("direct", ("S", 1, 1), ("Z", 100, 100)));
        var router = Router(net);
        Assert.Equal(new[] { (100, 0) }, Points(router.Route(Query(null, pareto), net), pareto));
        Assert.True(State(router).PriorityQueue.Count > 0); // Actual early exit, not just the same result after draining.
        Assert.InRange(router.LastMetrics!.StopsSettled, 1, 2);
    }

    [Fact]
    public void ParetoWithoutZeroTransferOptionKeepsLaterLowerTransferResult()
    {
        var net = Network(new[] { "S", "A", "B", "C", "Z" },
            Trip("a", ("S", 0, 0), ("A", 1, 1)), Trip("b", ("A", 2, 2), ("B", 3, 3)),
            Trip("c", ("B", 4, 4), ("Z", 10, 10)), Trip("d", ("S", 0, 0), ("C", 5, 5)),
            Trip("e", ("C", 6, 6), ("Z", 20, 20)));
        Assert.Equal(new[] { (10, 2), (20, 1) }, Points(Router(net).Route(Query(2, true), net), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroDurationWalkingCanImproveTransferTieBeforeSingleSettles(bool pareto)
    {
        var net = Network(new[] { "S", "A", "C", "Z" }, Trip("a", ("S", 0, 0), ("A", 5, 5)),
            Trip("b", ("A", 6, 6), ("Z", 10, 10)), Trip("direct", ("S", 0, 0), ("C", 10, 10)));
        var router = Router(net, Footpaths(net.Stops.Keys, ("C", "Z", 0)));
        Assert.Equal(new[] { (10, 0) }, Points(router.Route(Query(1, pareto), net), pareto));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeLimitIsRejected(bool pareto)
    {
        var net = Network(new[] { "S", "Z" });
        Assert.Throws<ArgumentOutOfRangeException>(() => Router(net).Route(Query(-1, pareto), net));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomNetworksMatchIndependentBoundedBoardingOracle(bool fullWalking)
    {
        var random = new Random(20261021);
        for (var sample = 0; sample < 30; sample++)
        {
            var ids = Enumerable.Range(0, 6).Select(i => $"S{i}").ToArray();
            var trips = Enumerable.Range(0, 12).Select(t =>
            {
                var time = random.Next(200);
                var selected = ids.OrderBy(_ => random.Next()).Take(random.Next(2, 6)).ToArray();
                var times = selected.Select(id =>
                {
                    var arrival = time; time += random.Next(8);
                    var departure = time; time += random.Next(40); return (id, arrival, departure);
                }).ToArray();
                return Trip($"T{t}", times);
            }).ToArray();
            var net = Network(ids, trips);
            IFootpathGraph graph = fullWalking ? new FullFootpathGraph(net.Stops.Values) : new SameNameFootpathGraph(net.Stops.Values);
            var router = Router(net, graph);
            for (var q = 0; q < 12; q++)
                foreach (var pareto in new[] { false, true })
                {
                    var query = new RoutingQuery
                    {
                        SourceStopId = ids[random.Next(ids.Length)],
                        TargetStopId = ids[random.Next(ids.Length)],
                        DepartureTime = random.Next(300),
                        MaxTransfers = q % 5 == 4 ? null : q % 5,
                        MultiCriteria = pareto
                    };
                    var expected = Oracle(net, graph, query);
                    if (!pareto) expected = expected.Take(1).ToArray();
                    Assert.Equal(expected, Points(router.Route(query, net), pareto));
                    if (query.MaxTransfers is int limit)
                        Assert.All(State(router).LabelsByState.Values.SelectMany(x => x), label => Assert.True(label.Transfers <= limit));
                }
        }
    }

    // One edge boards a whole trip and exits at any later stop: no TripId-state,
    // target pruning or early termination from the production implementation is reused.
    private static (int, int)[] Oracle(TransitNetwork net, IFootpathGraph graph, RoutingQuery query)
    {
        var limit = query.MaxTransfers is int cap ? cap + 1 : net.Trips.Count;
        var best = new Dictionary<(string Stop, int Rides), int> { [(query.SourceStopId, 0)] = query.DepartureTime };
        var queue = new PriorityQueue<(string Stop, int Rides), int>();
        queue.Enqueue((query.SourceStopId, 0), query.DepartureTime);
        void Relax((string Stop, int Rides) state, int time)
        {
            if (best.TryGetValue(state, out var old) && old <= time) return;
            best[state] = time; queue.Enqueue(state, time);
        }
        while (queue.TryDequeue(out var state, out var time))
        {
            if (best[state] != time) continue;
            foreach (var walk in graph.GetOutgoingFootpaths(state.Stop)) Relax((walk.ToStopId, state.Rides), time + walk.DurationSeconds);
            if (state.Rides >= limit) continue;
            foreach (var trip in net.Trips.Values)
                for (var i = 0; i < trip.StopTimes.Count; i++)
                {
                    if (trip.StopTimes[i].StopId != state.Stop || trip.StopTimes[i].DepartureTime < time) continue;
                    for (var j = i + 1; j < trip.StopTimes.Count; j++) Relax((trip.StopTimes[j].StopId, state.Rides + 1), trip.StopTimes[j].ArrivalTime);
                }
        }
        var points = best.Where(p => p.Key.Stop == query.TargetStopId).Select(p => (Time: p.Value, Transfers: Math.Max(0, p.Key.Rides - 1))).Distinct().ToArray();
        return points.Where(p => !points.Any(o => o != p && o.Time <= p.Time && o.Transfers <= p.Transfers))
            .OrderBy(p => p.Time).ThenBy(p => p.Transfers).Select(p => (p.Time, p.Transfers)).ToArray();
    }
}