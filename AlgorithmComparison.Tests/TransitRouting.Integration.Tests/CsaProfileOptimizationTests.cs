using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using Before = AlgorithmComparison.Tools.CsaBaseline.ConnectionScanAlgorithm;
using After = AlgorithmComparison.Algorithms.Csa.ConnectionScanAlgorithm;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class CsaProfileOptimizationTests
{
    [Theory]
    [InlineData("full")]

    [InlineData("grouped")]

    [InlineData("same-name")]

    public void RandomNetworksMatchFrozenBaselineAndIndependentBoardingOracle(string model)
    {
        var random = new Random(73451);
        for (var sample = 0; sample < 40; sample++)
        {
            var net = Network(random);
            IFootpathGraph graph = model switch
            {
                "full" => new FullFootpathGraph(net.Stops.Values),
                "grouped" => new GroupedFootpathGraph(net.Stops.Values, net.Stops.Keys.ToDictionary(id => id, id => (int.Parse(id) / 3).ToString())),
                _ => new SameNameFootpathGraph(net.Stops.Values)
            };

            var before = new Before(graph);
            var after = new After(graph);
            RoutingResult? retained = null;
            string? retainedSignature = null;
            for (var q = 0; q < 10; q++)
            {
                var query = new RoutingQuery
                {
                    SourceStopId = random.Next(6).ToString(),
                    TargetStopId = random.Next(6).ToString(),
                    DepartureTime = random.Next(0, 1200),
                    MaxTransfers = q % 4,
                    MultiCriteria = true
                };
                var expected = before.Route(query, net);
                var actual = after.Route(query, net);
                Assert.Equal(Signature(expected), Signature(actual));
                var points = actual.ParetoJourneys.Journeys.Select(j => (j.ArrivalTime, j.NumberOfTransfers)).ToArray();
                Assert.Equal(Oracle(net, graph, query), points);
                if (retained is not null) Assert.Equal(retainedSignature, Signature(retained));
                retained = actual; retainedSignature = Signature(actual);
                foreach (var journey in actual.ParetoJourneys.Journeys) CheckJourney(journey, query, net, graph);
            }
        }
    }

    private static void CheckJourney(Journey journey, RoutingQuery query, TransitNetwork net, IFootpathGraph graph)
    {
        Assert.Equal(query.SourceStopId, journey.DepartureStopId);
        Assert.Equal(query.TargetStopId, journey.ArrivalStopId);
        var time = query.DepartureTime; var stop = query.SourceStopId;
        foreach (var leg in journey.Legs)
        {
            Assert.Equal(stop, leg.FromStopId);
            Assert.True(leg.DepartureTime >= time);
            Assert.True(leg.ArrivalTime >= leg.DepartureTime);
            if (leg.IsWalk)
            {
                Assert.True(graph.TryGetDuration(leg.FromStopId, leg.ToStopId, out var seconds));
                Assert.Equal(seconds, leg.ArrivalTime - leg.DepartureTime);
            }
            else
            {
                var times = net.Trips[leg.TripId!].StopTimes;
                Assert.Contains(times, s => s.StopId == leg.FromStopId && s.DepartureTime == leg.DepartureTime);
                Assert.Contains(times, s => s.StopId == leg.ToStopId && s.ArrivalTime == leg.ArrivalTime);
            }
            time = leg.ArrivalTime; stop = leg.ToStopId;
        }
    }

    // Independent forward graph search: one transit edge boards a trip and leaves at
    // any later stop. No CSA profiles, scan order or pointer logic are used here.
    private static (int, int)[] Oracle(TransitNetwork net, IFootpathGraph graph, RoutingQuery query)
    {
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
            if (state.Rides == query.MaxTransfers + 1) continue;
            foreach (var trip in net.Trips.Values)
                for (var i = 0; i < trip.StopTimes.Count; i++)
                {
                    var board = trip.StopTimes[i];
                    if (board.StopId != state.Stop || board.DepartureTime < time) continue;
                    for (var j = i + 1; j < trip.StopTimes.Count; j++)
                        Relax((trip.StopTimes[j].StopId, state.Rides + 1), trip.StopTimes[j].ArrivalTime);
                }
        }
        var points = best.Where(p => p.Key.Stop == query.TargetStopId).Select(p => (Time: p.Value, Transfers: Math.Max(0, p.Key.Rides - 1))).Distinct().ToArray();
        return points.Where(p => !points.Any(o => o != p && o.Time <= p.Time && o.Transfers <= p.Transfers)).OrderBy(p => p.Time).Select(p => (p.Time, p.Transfers)).ToArray();
    }

    private static string Signature(RoutingResult r) => string.Join('|', r.ParetoJourneys.Journeys.Select(j => string.Join(';', j.Legs.Select(l => $"{l.FromStopId}/{l.ToStopId}/{l.DepartureTime}/{l.ArrivalTime}/{l.TripId}/{l.RouteId}"))));
    private static TransitNetwork Network(Random random)
    {
        var stops = Enumerable.Range(0, 6).Select(i => new Stop { Id = i.ToString(), Name = (i / 2).ToString(), Latitude = 50 + random.NextDouble() * 0.01, Longitude = 19 + random.NextDouble() * 0.01 }).ToDictionary(s => s.Id);
        var trips = new List<Trip>(); var connections = new List<Connection>();
        for (var t = 0; t < 16; t++)
        {
            var time = random.Next(0, 700);
            var selected = stops.Keys.OrderBy(_ => random.Next()).Take(random.Next(2, 6)).ToArray();
            var times = selected.Select((s, i) => { var arrival = time + random.Next(20, 100); time = arrival + random.Next(0, 30); return new StopTime { TripId = $"T{t}", StopId = s, StopSequence = i, ArrivalTime = arrival, DepartureTime = time }; }).ToArray();
            trips.Add(new Trip { Id = $"T{t}", RouteId = "R", ServiceId = "S", StopTimes = times });
            for (var i = 0; i < times.Length - 1; i++) connections.Add(new Connection { Id = $"{t}/{i}", TripId = $"T{t}", RouteId = "R", DepartureStopId = times[i].StopId, ArrivalStopId = times[i + 1].StopId, DepartureTime = times[i].DepartureTime, ArrivalTime = times[i + 1].ArrivalTime });
        }
        return new TransitNetwork
        {
            Stops = stops,
            Trips = trips.ToDictionary(t => t.Id),
            Routes = new Dictionary<string, Route> { ["R"] = new() { Id = "R", ShortName = "R", RouteType = RouteType.Bus } },
            Connections = connections.OrderBy(c => c.DepartureTime).ToArray(),
            ConnectionDict = connections.ToDictionary(c => c.Id),
            ServiceDate = new(2026, 10, 20),

        };
    }
}
