using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.RaptorRealtime;
using AlgorithmComparison.Validation.Synthetic;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class WalkingRegressionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var algorithm in new[] { "raptor", "csa", "dijkstra", "realtime" })
            foreach (var graph in new[] { "full", "grouped", "same-name" })
                foreach (var pareto in new[] { false, true })
                    yield return [algorithm, graph, pareto];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReusedAndFreshRoutersPreserveEntireJourneysAcrossRepeatedQueries(string name, string model, bool pareto)
    {
        var net = SyntheticNetworkFactory.CreateLinear(24);
        IFootpathGraph graph = model switch
        {
            "full" => new FullFootpathGraph(net.Stops.Values),
            "grouped" => new GroupedFootpathGraph(net.Stops.Values, net.Stops.Keys.ToDictionary(id => id, id => (int.Parse(id[1..]) / 4).ToString())),
            _ => new SameNameFootpathGraph(net.Stops.Values)
        };
        var router = Create(name, net, graph);
        var random = new Random(20261020);
        for (var i = 0; i < 12; i++)
        {
            var query = new RoutingQuery
            {
                SourceStopId = $"S{random.Next(24)}",
                TargetStopId = $"S{random.Next(24)}",
                DepartureTime = 21000 + random.Next(12000),
                MaxTransfers = 2,
                MultiCriteria = pareto
            };
            var expected = Create(name, net, graph).Route(query, net);
            var actual = router.Route(query, net);
            Assert.Equal(Signature(expected), Signature(actual));
            foreach (var journey in Journeys(actual))
            {
                Assert.Equal(query.SourceStopId, journey.DepartureStopId);
                Assert.Equal(query.TargetStopId, journey.ArrivalStopId);
                Assert.True(journey.DepartureTime >= query.DepartureTime);
                for (var leg = 1; leg < journey.Legs.Count; leg++)
                {
                    Assert.Equal(journey.Legs[leg - 1].ToStopId, journey.Legs[leg].FromStopId);
                    Assert.True(journey.Legs[leg - 1].ArrivalTime <= journey.Legs[leg].DepartureTime);
                }
            }
        }
    }

    [Fact]
    public void IncomingAndOutgoingPreserveDirectionOrderAndDurations()
    {
        var net = SyntheticNetworkFactory.CreateLinear(30);
        var graph = new FullFootpathGraph(net.Stops.Values);
        foreach (var id in net.Stops.Keys)
        {
            Assert.Equal(graph.GetOutgoingFootpaths(id).Select(PathSignature), graph.GetOutgoingBatch(id).Select(PathSignature));
            Assert.Equal(graph.GetIncomingFootpaths(id).Select(PathSignature), graph.GetIncomingBatch(id).Select(PathSignature));
        }
        Assert.Empty(graph.GetIncomingBatch("missing"));
    }

    [Fact]
    public void ReplacingRealtimeSnapshotsMatchesAFreshRouterInBothModes()
    {
        var net = SyntheticNetworkFactory.CreateLinear(36);
        var graph = new FullFootpathGraph(net.Stops.Values);
        var clock = new FixedClock();
        var router = new RealtimeRaptorAlgorithm(net, graph, time: clock);
        foreach (var delay in new[] { 120, -20, 0 })
        {
            var feed = new TransitRealtime.FeedMessage { Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)clock.GetUtcNow().ToUnixTimeSeconds() } };
            feed.Entity.Add(new TransitRealtime.FeedEntity
            {
                Id = "t0",
                TripUpdate = new()
                {
                    Trip = new() { TripId = "T0", StartDate = "20240902" },
                    StopTimeUpdate = { new TransitRealtime.TripUpdate.Types.StopTimeUpdate { StopSequence = 1, Departure = new() { Delay = delay } } }
                }
            });
            Assert.True(router.ApplyFeed(feed).Accepted);
            var fresh = new RealtimeRaptorAlgorithm(net, graph, time: clock);
            Assert.True(fresh.ApplyFeed(feed).Accepted);
            foreach (var pareto in new[] { false, true })
            {
                var query = new RoutingQuery { SourceStopId = "S0", TargetStopId = "S35", DepartureTime = 21000, MaxTransfers = 3, MultiCriteria = pareto };
                Assert.True(router.RouteDetailed(query).UsedRealtime);
                Assert.Equal(Signature(fresh.Route(query, net)), Signature(router.Route(query, net)));
            }
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2024, 9, 2, 4, 0, 0, TimeSpan.Zero);
    }
    private static string PathSignature(Footpath path) => $"{path.FromStopId}/{path.ToStopId}/{path.DurationSeconds}";
    private static IEnumerable<Journey> Journeys(RoutingResult result) => result.BestJourney is { } best ? [best] : result.ParetoJourneys.Journeys;
    private static string Signature(RoutingResult result) => string.Join('|', Journeys(result).Select(j =>
        string.Join(';', j.Legs.Select(l => $"{l.FromStopId}/{l.ToStopId}/{l.DepartureTime}/{l.ArrivalTime}/{l.TripId}/{l.RouteId}"))));
    private static IRoutingAlgorithm Create(string name, TransitNetwork net, IFootpathGraph graph)
    {

        return name switch
        {
            "raptor" => new RaptorAlgorithm(graph),
            "csa" => new ConnectionScanAlgorithm(graph),
            "dijkstra" => new TimeDependentDijkstraAlgorithm(graph),
            _ => new RealtimeRaptorAlgorithm(net, graph)
        };
    }
}
