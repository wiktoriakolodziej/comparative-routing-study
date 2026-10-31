using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using ConnectionScanAlgorithm = AlgorithmComparison.Algorithms.Csa.ConnectionScanAlgorithm;
using RaptorAlgorithm = AlgorithmComparison.Algorithms.Raptor.RaptorAlgorithm;
using TimeDependentDijkstraAlgorithm = AlgorithmComparison.Algorithms.TimeDependentDijkstra.TimeDependentDijkstraAlgorithm;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public class FootpathInjectionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var algorithm in new[] { "CSA", "RAPTOR", "Dijkstra" })
            foreach (var model in new[] { "same-name", "grouped", "full" })
                foreach (var pareto in new[] { false, true })
                    yield return [algorithm, model, pareto];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConstructorGraphControlsTransfers_InsteadOfFeedGraph(string algorithmName, string model, bool pareto)
    {
        var network = CreateNetwork();
        var algorithm = CreateAlgorithm(algorithmName, CreateGraph(model, network));
        var result = algorithm.Route(Query("1", "4", 0, pareto), network);

        var points = Journeys(result, pareto)
            .Select(j => (j.ArrivalTime, j.NumberOfTransfers)).ToArray();
        var expected = model == "same-name"
            ? new[] { (900, 0) }
            : pareto ? new[] { (350, 1), (900, 0) } : new[] { (350, 1) };
        Assert.Equal(expected, points);
        Assert.All(Journeys(result, pareto), journey => AssertEndpoints(journey, "1", "4"));

        // Reusing an algorithm must reset query state while retaining its injected graph.
        var later = algorithm.Route(Query("1", "4", 15, pareto), network);
        var laterJourney = Assert.Single(Journeys(later, pareto));
        Assert.Equal(900, laterJourney.ArrivalTime);
        AssertEndpoints(laterJourney, "1", "4");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConstructorGraphControlsInitialAndFinalWalking(string algorithmName, string model, bool pareto)
    {
        var network = CreateNetwork(sameHubName: true);
        var algorithm = CreateAlgorithm(algorithmName, CreateGraph(model, network));

        // Walk 2->3 before boarding 3->4.
        var initial = algorithm.Route(Query("2", "4", 0, pareto), network);
        var initialJourney = Assert.Single(Journeys(initial, pareto));
        Assert.Equal(350, initialJourney.ArrivalTime);
        AssertEndpoints(initialJourney, "2", "4");
        Assert.Contains(initialJourney.Legs, l => l.IsWalk && l.FromStopId == "2" && l.ToStopId == "3");

        // Ride 1->2, then walk 2->3.
        var final = algorithm.Route(Query("1", "3", 0, pareto), network);
        var finalJourney = Assert.Single(Journeys(final, pareto));
        Assert.Equal(201, finalJourney.ArrivalTime);
        AssertEndpoints(finalJourney, "1", "3");
        Assert.Contains(finalJourney.Legs, l => l.IsWalk && l.FromStopId == "2" && l.ToStopId == "3");
    }

    private static IRoutingAlgorithm CreateAlgorithm(string name, IFootpathGraph graph) => name switch
    {
        "CSA" => new ConnectionScanAlgorithm(graph),
        "RAPTOR" => new RaptorAlgorithm(graph),
        "Dijkstra" => new TimeDependentDijkstraAlgorithm(graph),
        _ => throw new ArgumentException("Unknown test algorithm.", nameof(name))
    };

    private static IFootpathGraph CreateGraph(string model, TransitNetwork network) => model switch
    {
        "same-name" => new SameNameFootpathGraph(network.Stops.Values),
        "grouped" => new GroupedFootpathGraph(network.Stops.Values, new Dictionary<string, string>
        {
            ["1"] = "source",
            ["2"] = "hub",
            ["3"] = "hub",
            ["4"] = "target"
        }),
        "full" => new FullFootpathGraph(network.Stops.Values),
        _ => throw new ArgumentException("Unknown test model.", nameof(model))
    };

    private static RoutingQuery Query(string source, string target, int departure, bool pareto) => new()
    {
        SourceStopId = source,
        TargetStopId = target,
        DepartureTime = departure,
        MaxTransfers = 2,
        MultiCriteria = pareto
    };

    private static IReadOnlyList<Journey> Journeys(RoutingResult result, bool pareto)
        => pareto ? result.ParetoJourneys.Journeys : [Assert.IsType<Journey>(result.BestJourney)];

    private static void AssertEndpoints(Journey journey, string source, string target)
    {
        Assert.Equal(source, journey.DepartureStopId);
        Assert.Equal(target, journey.ArrivalStopId);
        for (var i = 1; i < journey.Legs.Count; i++)
        {
            Assert.Equal(journey.Legs[i - 1].ToStopId, journey.Legs[i].FromStopId);
            Assert.True(journey.Legs[i - 1].ArrivalTime <= journey.Legs[i].DepartureTime);
        }
    }

    private static TransitNetwork CreateNetwork(bool sameHubName = false)
    {
        Stop[] stops =
        [
            new() { Id = "1", Name = "Origin", Latitude = 0, Longitude = 0 },
            new() { Id = "2", Name = "Hub", Latitude = 0, Longitude = 1 },
            new() { Id = "3", Name = sameHubName ? "Hub" : "Other hub", Latitude = 0, Longitude = 1.001 },
            new() { Id = "4", Name = "Destination", Latitude = 0, Longitude = 2 }
        ];
        Connection[] connections =
        [
            new() { Id = "a", TripId = "a", RouteId = "a", DepartureStopId = "1", ArrivalStopId = "2", DepartureTime = 10, ArrivalTime = 100 },
            new() { Id = "b", TripId = "b", RouteId = "b", DepartureStopId = "1", ArrivalStopId = "4", DepartureTime = 20, ArrivalTime = 900 },
            new() { Id = "c", TripId = "c", RouteId = "c", DepartureStopId = "3", ArrivalStopId = "4", DepartureTime = 250, ArrivalTime = 350 }
        ];

        // The feed deliberately allows free walking everywhere. Using it would give arrival 0.
        var feedEdges = (from a in stops
                         from b in stops
                         where a.Id != b.Id
                         select new Footpath { FromStopId = a.Id, ToStopId = b.Id, DurationSeconds = 0 }).ToArray();
        return new TransitNetwork
        {
            Stops = stops.ToDictionary(s => s.Id),
            Routes = connections.ToDictionary(c => c.RouteId, c => new Route { Id = c.RouteId, ShortName = c.RouteId, RouteType = RouteType.Bus }),
            Trips = connections.ToDictionary(c => c.TripId, c => new Trip
            {
                Id = c.TripId,
                RouteId = c.RouteId,
                ServiceId = "service",
                StopTimes =
                [
                    new() { TripId = c.TripId, StopId = c.DepartureStopId, StopSequence = 1, ArrivalTime = c.DepartureTime, DepartureTime = c.DepartureTime },
                    new() { TripId = c.TripId, StopId = c.ArrivalStopId, StopSequence = 2, ArrivalTime = c.ArrivalTime, DepartureTime = c.ArrivalTime }
                ]
            }),
            Connections = connections,
            ConnectionDict = connections.ToDictionary(c => c.Id),


            ServiceDate = new DateOnly(2026, 9, 27)
        };
    }
}
