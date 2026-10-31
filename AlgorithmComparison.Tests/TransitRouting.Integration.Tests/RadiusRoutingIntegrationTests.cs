using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Walking;
using AlgorithmComparison.RaptorRealtime;
using ModelTrip = AlgorithmComparison.Core.Models.Trip;
using ModelRoute = AlgorithmComparison.Core.Models.Route;
using ModelStop = AlgorithmComparison.Core.Models.Stop;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class RadiusRoutingIntegrationTests
{
    private static ModelStop Stop(string id, double metres) => new()
    { Id = id, Name = id, Latitude = 0, Longitude = metres / 6_371_000 * 180 / Math.PI };
    private static ModelTrip Trip(string id, params (string Stop, int Time)[] times) => new()
    {
        Id = id,
        RouteId = id,
        ServiceId = "service",
        StopTimes = times.Select((t, i) => new StopTime
        {
            TripId = id,
            StopId = t.Stop,
            StopSequence = i,
            ArrivalTime = t.Time,
            DepartureTime = t.Time
        }).ToArray()
    };
    private static TransitNetwork Network(ModelStop[] stops, params ModelTrip[] trips)
    {
        var connections = trips.SelectMany(trip => trip.StopTimes.Zip(trip.StopTimes.Skip(1), (a, b) => new AlgorithmComparison.Core.Models.Connection
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
            Stops = stops.ToDictionary(s => s.Id),
            Trips = trips.ToDictionary(t => t.Id),
            Routes = trips.ToDictionary(t => t.Id, t => new ModelRoute { Id = t.Id, ShortName = t.Id, RouteType = RouteType.Bus }),
            Connections = connections,
            ConnectionDict = connections.ToDictionary(c => c.Id),
            ServiceDate = new(2026, 10, 21),
        };
    }
    private static IRoutingAlgorithm Router(string name, TransitNetwork net, RadiusFootpathGraph graph)
    {
        return name switch
        {
            "CSA" => new ConnectionScanAlgorithm(graph),
            "RAPTOR" => new RaptorAlgorithm(graph),
            "Realtime" => new RealtimeRaptorAlgorithm(net, graph),
            _ => new TimeDependentDijkstraAlgorithm(graph)
        };
    }
    private static RoutingQuery Query(string from, string to, int time, bool pareto) => new()
    {
        SourceStopId = from,
        TargetStopId = to,
        DepartureTime = time,
        MultiCriteria = pareto,
        MaxTransfers = pareto ? 2 : null
    };
    private static IReadOnlyList<Journey> Journeys(AlgorithmComparison.Core.Results.RoutingResult r, bool pareto) =>
        pareto ? r.ParetoJourneys.Journeys : r.BestJourney is { } j ? new[] { j } : Array.Empty<Journey>();
    public static IEnumerable<object[]> Algorithms() =>
        from name in new[] { "CSA", "RAPTOR", "Dijkstra", "Realtime" } from pareto in new[] { false, true } select new object[] { name, pareto };

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void ActualSearchPropagatesBeforeAfterAndBetweenRides(string name, bool pareto)
    {
        var a = Stop("A", 0); var b = Stop("B", 350); var c = Stop("C", 700);
        var s = Stop("S", 10000); var z = Stop("Z", 20000);
        foreach (var scenario in new[] { "initial", "final", "transfer", "walk", "zero" })
        {
            TransitNetwork net; RoutingQuery query;
            if (scenario == "initial") { net = Network(new[] { a, b, c, z }, Trip("last", ("C", 1000), ("Z", 1100))); query = Query("A", "Z", 0, pareto); }
            else if (scenario == "final") { net = Network(new[] { s, a, b, c }, Trip("first", ("S", 0), ("A", 100))); query = Query("S", "C", 0, pareto); }
            else if (scenario == "transfer")
            {
                net = Network(new[] { s, a, b, c, z }, Trip("first", ("S", 0), ("A", 100)),
                Trip("last", ("C", 800), ("Z", 900)), Trip("direct", ("S", 0), ("Z", 2000))); query = Query("S", "Z", 0, pareto);
            }
            else if (scenario == "walk") { net = Network(new[] { a, b, c }); query = Query("A", "C", 0, pareto); }
            else { net = Network(new[] { a, Stop("B", 0), c, Stop("D", 350) }); query = Query("A", "C", 0, pareto); }
            var graph = new RadiusFootpathGraph(net.Stops.Values);
            Assert.False(graph.TryGetDuration(query.SourceStopId, query.TargetStopId, out _));
            Check(Router(name, net, graph), net, graph, query);
        }
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void RadiusChangesReachabilityAndQueriesDoNotLeakState(string name, bool pareto)
    {
        var net = Network(new[] { Stop("A", 0), Stop("B", 350), Stop("C", 700), Stop("X", 10000) });
        foreach (var radius in new[] { 300, 500 })
        {
            var graph = new RadiusFootpathGraph(net.Stops.Values, radius);
            var router = Router(name, net, graph);
            foreach (var q in new[] { Query("A", "C", 0, pareto), Query("A", "X", 0, pareto), Query("C", "A", 86400, pareto) })
                Check(router, net, graph, q);
        }
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public void RandomWholeSearchesMatchIndependentBoardingStateOracle(string name, bool pareto)
    {
        var random = new Random(20261021);
        for (var sample = 0; sample < 30; sample++)
        {
            var stops = Enumerable.Range(0, 6).Select(i => Stop($"S{i}", i / 3 * 10000 + i % 3 * 350)).ToArray();
            var trips = Enumerable.Range(0, 12).Select(i =>
            {
                var time = random.Next(500, 5000);
                return Trip($"T{i}", stops.OrderBy(_ => random.Next()).Take(random.Next(2, 5))
                    .Select(s => { time += random.Next(1, 300); return (s.Id, time); }).ToArray());
            }).ToArray();
            var net = Network(stops, trips); var graph = new RadiusFootpathGraph(net.Stops.Values);
            var router = Router(name, net, graph);
            for (var q = 0; q < 8; q++)
            {
                var from = random.Next(stops.Length); var to = (from + random.Next(1, stops.Length)) % stops.Length;
                Check(router, net, graph, Query(stops[from].Id, stops[to].Id, random.Next(3000), pareto));
            }
        }
    }

    [Fact]
    public void ProfileCsaIgnoresPastDeparturesWithoutChangingFutureJourney()
    {
        var net = Network(new[] { Stop("S", 0), Stop("Z", 10000) },
            Trip("past", ("S", 0), ("Z", 10)), Trip("future", ("S", 110), ("Z", 150)));
        var graph = new RadiusFootpathGraph(net.Stops.Values);
        var router = new ConnectionScanAlgorithm(graph);
        Check(router, net, graph, Query("S", "Z", 100, true));
        Assert.Equal(1, router.LastMetrics!.TripsOrConnectionsScanned);
        Check(router, net, graph, Query("S", "Z", 0, true)); // Query-local profiles must reset.
        Assert.Equal(2, router.LastMetrics!.TripsOrConnectionsScanned);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealtimeUpdatesLoseAndRestoreATransferReachedByMultipleWalks(bool pareto)
    {
        var net = Network(new[] { Stop("S", 10000), Stop("A", 0), Stop("B", 350), Stop("C", 700), Stop("Z", 20000) },
            Trip("first", ("S", 0), ("A", 100)), Trip("last", ("C", 800), ("Z", 900)),
            Trip("direct", ("S", 0), ("Z", 2000)));
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var router = new RealtimeRaptorAlgorithm(net, new RadiusFootpathGraph(net.Stops.Values), time: new FixedClock(now));
        var query = Query("S", "Z", 0, pareto);
        Assert.Equal(900, Journeys(router.Route(query, net), pareto).Min(j => j.ArrivalTime));
        var delayed = new TransitRealtime.FeedMessage { Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)now.ToUnixTimeSeconds() } };
        delayed.Entity.Add(new TransitRealtime.FeedEntity { Id = "delay", TripUpdate = new() { Trip = new() { TripId = "first" } } });
        delayed.Entity[0].TripUpdate.StopTimeUpdate.Add(new TransitRealtime.TripUpdate.Types.StopTimeUpdate
        { StopSequence = 1, Arrival = new() { Delay = 100 }, Departure = new() { Delay = 100 } });
        Assert.True(router.ApplyFeed(delayed).Accepted);
        Assert.Equal(2000, Journeys(router.Route(query, net), pareto).Min(j => j.ArrivalTime));
        var restored = new TransitRealtime.FeedMessage { Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)now.ToUnixTimeSeconds() + 1 } };
        Assert.True(router.ApplyFeed(restored).Accepted);
        Assert.Equal(900, Journeys(router.Route(query, net), pareto).Min(j => j.ArrivalTime));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void Check(IRoutingAlgorithm router, TransitNetwork net, RadiusFootpathGraph graph, RoutingQuery query)
    {
        var expected = Oracle(net, graph, query);
        var journeys = Journeys(router.Route(query, net), query.MultiCriteria);
        var actual = journeys.Select(j => (j.ArrivalTime, j.NumberOfTransfers)).OrderBy(x => x.ArrivalTime).ThenBy(x => x.NumberOfTransfers).ToArray();
        if (query.MultiCriteria) Assert.Equal(expected, actual);
        else Assert.Equal(expected.Take(1).Select(p => p.Item1), actual.Select(p => p.ArrivalTime));
        foreach (var journey in journeys)
        {
            Assert.Equal(query.SourceStopId, journey.DepartureStopId);
            Assert.Equal(query.TargetStopId, journey.ArrivalStopId);
            var current = query.SourceStopId; var time = query.DepartureTime;
            foreach (var leg in journey.Legs)
            {
                Assert.Equal(current, leg.FromStopId);
                Assert.True(leg.DepartureTime >= time, $"Nonchronological journey in {router.Name}");
                Assert.True(leg.ArrivalTime >= leg.DepartureTime);
                if (leg.IsWalk)
                {
                    var distance = ShortestWalk(graph, leg.FromStopId, leg.ToStopId);
                    Assert.Equal(distance, leg.ArrivalTime - leg.DepartureTime);
                }
                else
                {
                    var trip = router is TimeDependentDijkstraAlgorithm ? net.Trips.Values.OrderBy(t => t.Id, StringComparer.Ordinal).ElementAt(int.Parse(leg.TripId!)) : net.Trips[leg.TripId!];
                    Assert.Contains(trip.StopTimes, t => t.StopId == leg.FromStopId && t.DepartureTime == leg.DepartureTime);
                    Assert.Contains(trip.StopTimes, t => t.StopId == leg.ToStopId && t.ArrivalTime == leg.ArrivalTime);
                }
                current = leg.ToStopId; time = leg.ArrivalTime;
            }
        }
    }
    private static int ShortestWalk(RadiusFootpathGraph graph, string from, string to)
    {
        var distances = graph.Outgoing.Keys.ToDictionary(s => s, _ => int.MaxValue); distances[from] = 0;
        for (var pass = 0; pass < distances.Count; pass++)
            foreach (var stop in graph.Outgoing.Keys)
                foreach (var edge in graph.GetOutgoingFootpaths(stop))
                    if (distances[stop] != int.MaxValue)
                        distances[edge.ToStopId] = Math.Min(distances[edge.ToStopId], distances[stop] + edge.DurationSeconds);
        return distances[to];
    }
    // Independent state-space search: a ride boards once and may alight at any later stop.
    // It expands individual radius edges, without production propagation or profile code.
    private static (int, int)[] Oracle(TransitNetwork net, RadiusFootpathGraph graph, RoutingQuery query)
    {
        var cap = query.MaxTransfers is int n ? n + 1 : net.Trips.Count;
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
            foreach (var walk in graph.GetOutgoingFootpaths(state.Stop))
                Relax((walk.ToStopId, state.Rides), time + walk.DurationSeconds);
            if (state.Rides == cap) continue;
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





