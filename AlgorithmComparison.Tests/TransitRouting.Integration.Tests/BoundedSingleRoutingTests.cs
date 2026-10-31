using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Walking;
using ModelStop = AlgorithmComparison.Core.Models.Stop;
using ModelTrip = AlgorithmComparison.Core.Models.Trip;
using ModelRoute = AlgorithmComparison.Core.Models.Route;
using ModelConnection = AlgorithmComparison.Core.Models.Connection;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class BoundedSingleRoutingTests
{
    private static ModelStop Stop(string id, double metres) => new()
    {
        Id = id,
        Name = id,
        Latitude = 0,
        Longitude = metres / 6_371_000 * 180 / Math.PI
    };
    private static ModelTrip Trip(string id, params (string Stop, int Time)[] times) => new()
    {
        Id = id,
        RouteId = id,
        ServiceId = "s",
        StopTimes = times.Select((t, i) => new StopTime
        {
            TripId = id,
            StopId = t.Stop,
            StopSequence = i,
            ArrivalTime = t.Time,
            DepartureTime = t.Time
        }).ToArray()
    };
    private static TransitNetwork Net(ModelStop[] stops, params ModelTrip[] trips)
    {
        var cs = trips.SelectMany(t => t.StopTimes.Zip(t.StopTimes.Skip(1), (a, b) => new ModelConnection
        {
            Id = $"{t.Id}/{a.StopSequence}",
            TripId = t.Id,
            RouteId = t.RouteId,
            DepartureStopId = a.StopId,
            ArrivalStopId = b.StopId,
            DepartureTime = a.DepartureTime,
            ArrivalTime = b.ArrivalTime
        })).OrderBy(c => c.DepartureTime).ThenBy(c => c.ArrivalTime).ToArray();
        return new TransitNetwork
        {
            Stops = stops.ToDictionary(s => s.Id),
            Trips = trips.ToDictionary(t => t.Id),
            Routes = trips.ToDictionary(t => t.Id, t => new ModelRoute { Id = t.Id, ShortName = t.Id, RouteType = RouteType.Bus }),
            Connections = cs,
            ConnectionDict = cs.ToDictionary(c => c.Id),
            ServiceDate = new(2026, 10, 24),
        };
    }
    private static IRoutingAlgorithm Router(string key, RadiusFootpathGraph graph) => key switch
    {
        "csa" => new ConnectionScanAlgorithm(graph),
        "raptor" => new RaptorAlgorithm(graph),
        _ => new TimeDependentDijkstraAlgorithm(graph)
    };
    private static RoutingQuery Query(int? cap, string source = "S", string target = "Z", int departure = 0) => new()
    { SourceStopId = source, TargetStopId = target, DepartureTime = departure, MaxTransfers = cap, MultiCriteria = false };

    [Theory]
    [InlineData("csa")]
    [InlineData("raptor")]
    [InlineData("dijkstra")]
    public void DifferentBudgetsPreserveLaterArrivalWithFewerBoardingsAndReset(string name)
    {
        var net = Net(new[] { Stop("S", 0), Stop("A", 10000), Stop("B", 20000), Stop("Z", 30000) },
            Trip("first", ("S", 0), ("B", 5)), Trip("second", ("B", 6), ("A", 10)),
            Trip("slower", ("S", 0), ("A", 20)), Trip("fast-last", ("A", 11), ("Z", 15)),
            Trip("last", ("A", 21), ("Z", 30)), Trip("direct", ("S", 0), ("Z", 100)));
        var graph = new RadiusFootpathGraph(net.Stops.Values); var router = Router(name, graph);
        foreach (var (cap, arrival) in new (int?, int)[] { (0, 100), (1, 30), (2, 15), (null, 15), (0, 100) })
        {
            var journey = router.Route(Query(cap), net).BestJourney;
            Assert.NotNull(journey); Assert.Equal(arrival, journey.ArrivalTime);
            if (cap.HasValue) Assert.InRange(journey.NumberOfTransfers, 0, cap.Value);
        }
    }

    [Theory]
    [InlineData("csa")]
    [InlineData("raptor")]
    [InlineData("dijkstra")]
    public void BudgetCountsVehicleChangesAndAllowsMultiHopWalking(string name)
    {
        var net = Net(new[] { Stop("S", 10000), Stop("A", 0), Stop("B", 350), Stop("C", 700), Stop("Z", 20000) },
            Trip("first", ("S", 0), ("A", 100)), Trip("last", ("C", 800), ("Z", 900)));
        var graph = new RadiusFootpathGraph(net.Stops.Values); var router = Router(name, graph);
        Assert.Null(router.Route(Query(0), net).BestJourney);
        var journey = router.Route(Query(1), net).BestJourney;
        Assert.NotNull(journey); Assert.Equal(900, journey.ArrivalTime); Assert.Equal(1, journey.NumberOfTransfers);
        Assert.Contains(journey.Legs, l => l.IsWalk && l.ToStopId == "C");
    }

    [Theory]
    [InlineData("csa")]
    [InlineData("raptor")]
    [InlineData("dijkstra")]
    public void ZeroTransferBudgetAllowsStayingOnTripAndFinalWalkingAfterMidnight(string name)
    {
        var net = Net(new[] { Stop("S", 10000), Stop("B", 20000), Stop("A", 0), Stop("Z", 350) },
            Trip("through", ("S", 86500), ("B", 86600), ("A", 86700)));
        var graph = new RadiusFootpathGraph(net.Stops.Values); var router = Router(name, graph);
        var j = router.Route(Query(0, departure: 86400), net).BestJourney;
        Assert.NotNull(j); Assert.Equal(0, j.NumberOfTransfers); Assert.Equal(87016, j.ArrivalTime);
        Assert.Single(j.Legs.Where(l => !l.IsWalk));
        Assert.Null(router.Route(Query(0, departure: 90000), net).BestJourney);
    }

    [Fact]
    public void CsaHandlesSimultaneousConnectionsAcrossLayersAndZeroWalkingCycles()
    {
        var net = Net(new[] { Stop("S", 0), Stop("A", 10000), Stop("B", 10000), Stop("Z", 20000) },
            Trip("second", ("B", 10), ("Z", 10)), Trip("first", ("S", 10), ("A", 10)));
        var router = Router("csa", new RadiusFootpathGraph(net.Stops.Values));
        Assert.Null(router.Route(Query(0), net).BestJourney);
        Assert.Equal(10, router.Route(Query(1), net).BestJourney!.ArrivalTime);
        Assert.Equal(0, router.Route(Query(0, "A", "B"), net).BestJourney!.ArrivalTime);
        Assert.Equal(0, router.Route(Query(0, "S", "S"), net).BestJourney!.ArrivalTime);
        Assert.ThrowsAny<ArgumentException>(() => router.Route(Query(-1), net));
    }

    [Theory]
    [InlineData("csa", false)]
    [InlineData("raptor", false)]
    [InlineData("dijkstra", false)]
    [InlineData("csa", true)]
    [InlineData("raptor", true)]
    [InlineData("dijkstra", true)]
    public void RandomBoundedSingleQueriesMatchIndependentStateSpaceOracle(string name, bool allowZero)
    {
        var rnd = new Random(20261024);
        for (var sample = 0; sample < 30; sample++)
        {
            var stops = Enumerable.Range(0, 6).Select(i => Stop($"P{i}", i / 3 * 10000 + i % 3 * 350)).ToArray();
            var trips = Enumerable.Range(0, 12).Select(t =>
            {
                var time = rnd.Next(300, 3000);
                return Trip($"T{t}", stops.OrderBy(_ => rnd.Next()).Take(rnd.Next(2, 5)).Select(s =>
                {
                    time += rnd.Next(allowZero ? 0 : 1, 150); return (s.Id, time);
                }).ToArray());
            }).ToArray();
            var net = Net(stops, trips); var graph = new RadiusFootpathGraph(stops); var router = Router(name, graph);
            for (var q = 0; q < 12; q++)
            {
                var a = rnd.Next(6); var b = (a + rnd.Next(1, 6)) % 6; var cap = q % 4;
                var query = Query(cap, stops[a].Id, stops[b].Id, rnd.Next(2000));
                var expected = Oracle(net, graph, query); var journey = router.Route(query, net).BestJourney;
                Assert.Equal(expected, journey?.ArrivalTime);
                if (journey is null) continue;
                Assert.InRange(journey.NumberOfTransfers, 0, cap);
                var current = query.SourceStopId; var time = query.DepartureTime;
                foreach (var leg in journey.Legs)
                {
                    Assert.Equal(current, leg.FromStopId); Assert.True(leg.DepartureTime >= time);
                    Assert.True(leg.ArrivalTime >= leg.DepartureTime);
                    if (!leg.IsWalk)
                    {
                        var trip = name == "dijkstra" ? net.Trips.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ElementAt(int.Parse(leg.TripId!)) : net.Trips[leg.TripId!];
                        var boarding = trip.StopTimes.Select((t, i) => (t, i)).Where(x => x.t.StopId == leg.FromStopId && x.t.DepartureTime == leg.DepartureTime).Select(x => x.i).ToArray();
                        var alighting = trip.StopTimes.Select((t, i) => (t, i)).Where(x => x.t.StopId == leg.ToStopId && x.t.ArrivalTime == leg.ArrivalTime).Select(x => x.i).ToArray();
                        Assert.Contains(boarding, a => alighting.Any(b => b > a));
                    }
                    else
                    {
                        var distances = stops.ToDictionary(s => s.Id, _ => int.MaxValue); distances[leg.FromStopId] = 0;
                        for (var pass = 0; pass < stops.Length; pass++)
                            foreach (var stop in stops)
                                foreach (var walk in graph.GetOutgoingFootpaths(stop.Id))
                                    if (distances[stop.Id] != int.MaxValue)
                                        distances[walk.ToStopId] = Math.Min(distances[walk.ToStopId], distances[stop.Id] + walk.DurationSeconds);
                        Assert.Equal(distances[leg.ToStopId], leg.ArrivalTime - leg.DepartureTime);
                    }
                    current = leg.ToStopId; time = leg.ArrivalTime;
                }
                Assert.Equal(query.TargetStopId, current);
            }
        }
    }
    // Each oracle ride boards a whole trip and may alight at any later stop.
    // It uses individual walking edges, a priority queue and exact boarding states;
    // no CSA layer, RAPTOR rounds or production dominance logic is reused.
    private static int? Oracle(TransitNetwork net, RadiusFootpathGraph graph, RoutingQuery query)
    {
        var best = new Dictionary<(string Stop, int Rides), int> { { (query.SourceStopId, 0), query.DepartureTime } };
        var queue = new PriorityQueue<(string Stop, int Rides), int>(); queue.Enqueue((query.SourceStopId, 0), query.DepartureTime);
        void Relax((string Stop, int Rides) state, int time) { if (best.TryGetValue(state, out var old) && old <= time) return; best[state] = time; queue.Enqueue(state, time); }
        while (queue.TryDequeue(out var state, out var time))
        {
            if (best[state] != time) continue;
            foreach (var w in graph.GetOutgoingFootpaths(state.Stop)) Relax((w.ToStopId, state.Rides), time + w.DurationSeconds);
            if (state.Rides >= query.MaxTransfers + 1) continue;
            foreach (var trip in net.Trips.Values)
                for (var i = 0; i < trip.StopTimes.Count; i++)
                {
                    var s = trip.StopTimes[i]; if (s.StopId != state.Stop || s.DepartureTime < time) continue;
                    for (var j = i + 1; j < trip.StopTimes.Count; j++) Relax((trip.StopTimes[j].StopId, state.Rides + 1), trip.StopTimes[j].ArrivalTime);
                }
        }
        return best.Where(p => p.Key.Stop == query.TargetStopId).Select(p => (int?)p.Value).Min();
    }
}
