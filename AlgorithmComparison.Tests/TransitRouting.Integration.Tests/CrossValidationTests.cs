using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.Validation.Synthetic;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

/// <summary>
/// Shared correctness tests for all routing algorithms.
/// Each test method creates a synthetic network, runs every registered algorithm,
/// and asserts that results are identical. Add new algorithm instances to
/// <see cref="AllAlgorithms"/> when implementing them; existing tests then cover them automatically.
/// </summary>
public class CrossValidationTests
{
    // ── Register algorithms here as they are implemented ─────────────────────

    private static IReadOnlyList<IRoutingAlgorithm> AllAlgorithms(TransitNetwork network) =>
    [
        new TimeDependentDijkstraAlgorithm(new SameNameFootpathGraph(network.Stops.Values)),
        new ConnectionScanAlgorithm(new SameNameFootpathGraph(network.Stops.Values)),
        new RaptorAlgorithm(new SameNameFootpathGraph(network.Stops.Values)),
    ];

    // ── Helper ───────────────────────────────────────────────────────────────

    private static RoutingResult Run(IRoutingAlgorithm algo, RoutingQuery q, TransitNetwork net)
        => algo.Route(q, net);

    /// <summary>
    /// Asserts that all registered algorithms return the same arrival time for the given query.
    /// </summary>
    private static void AssertAllAgree(RoutingQuery query, TransitNetwork network, int expectedArrival)
    {
        foreach (var algo in AllAlgorithms(network))
        {
            var result = Run(algo, query, network);
            Assert.True(result.IsReachable,
                $"{algo.Name}: destination should be reachable.");
            Assert.Equal(expectedArrival, result.BestJourney!.ArrivalTime);
        }
    }

    private static void AssertAllUnreachable(RoutingQuery query, TransitNetwork network)
    {
        foreach (var algo in AllAlgorithms(network))
        {
            var result = Run(algo, query, network);
            Assert.False(result.IsReachable,
                $"{algo.Name}: destination should NOT be reachable.");
            Assert.Null(result.BestJourney);
            Assert.Empty(result.ParetoJourneys.Journeys);
        }
    }

    // ── Linear network tests ──────────────────────────────────────────────────

    [Fact]
    public void Linear_DirectRide_ReturnsFirstAvailableTrip()
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 3);
        // S0 → S2: depart before 06:00 → first trip departs 06:00, arrives S2 at 06:04.
        var query = new RoutingQuery { SourceStopId = "S0", TargetStopId = "S2", DepartureTime = 5 * 3600 };
        AssertAllAgree(query, net, expectedArrival: 6 * 3600 + 4 * 60);
    }

    [Fact]
    public void Linear_DepartMidDay_PicksCorrectTrip()
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 3);
        // Depart just after the 09:00 trip leaves S0 → next trip is 10:00, arrives S1 at 10:02.
        var query = new RoutingQuery { SourceStopId = "S0", TargetStopId = "S1", DepartureTime = 9 * 3600 + 1 };
        AssertAllAgree(query, net, expectedArrival: 10 * 3600 + 2 * 60);
    }

    [Fact]
    public void Linear_DepartExactlyAtTripTime_BoardsThatTrip()
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 3);
        // Depart at exactly 08:00 → boards the 08:00 trip, arrives S2 at 08:04.
        var query = new RoutingQuery { SourceStopId = "S0", TargetStopId = "S2", DepartureTime = 8 * 3600 };
        AssertAllAgree(query, net, expectedArrival: 8 * 3600 + 4 * 60);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linear_AfterLastTrip_Unreachable(bool multiCriteria)
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 3);
        // Last trip departs 22:00. Departing at 22:01 → no trip available.
        var query = new RoutingQuery
        {
            SourceStopId = "S0",
            TargetStopId = "S2",
            DepartureTime = 22 * 3600 + 1,
            MultiCriteria = multiCriteria
        };
        AssertAllUnreachable(query, net);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linear_SourceEqualsTarget_ZeroTravelTime(bool multiCriteria)
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 3);
        var query = new RoutingQuery
        {
            SourceStopId = "S1",
            TargetStopId = "S1",
            DepartureTime = 8 * 3600,
            MultiCriteria = multiCriteria
        };
        foreach (var algo in AllAlgorithms(net))
        {
            var result = Run(algo, query, net);
            Assert.True(result.IsReachable, $"{algo.Name}: source is already the destination.");
            var journey = multiCriteria
                ? Assert.Single(result.ParetoJourneys.Journeys)
                : Assert.IsType<Journey>(result.BestJourney);
            if (multiCriteria)
                Assert.Null(result.BestJourney);
            else
                Assert.Empty(result.ParetoJourneys.Journeys);

            Assert.Equal(query.SourceStopId, journey.DepartureStopId);
            Assert.Equal(query.TargetStopId, journey.ArrivalStopId);
            Assert.Equal(query.DepartureTime, journey.DepartureTime);
            Assert.Equal(query.DepartureTime, journey.ArrivalTime);
            Assert.Equal(0, journey.TotalTime);
            Assert.Equal(0, journey.NumberOfTransfers);
            Assert.True(Assert.Single(journey.Legs).IsWalk);
        }
    }

    [Fact]
    public void Linear_IntermediateStop_CorrectArrival()
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 5);
        // S1 → S3: board 07:00 trip at S1 (07:02), arrive S3 at 07:06.
        var query = new RoutingQuery { SourceStopId = "S1", TargetStopId = "S3", DepartureTime = 7 * 3600 };
        AssertAllAgree(query, net, expectedArrival: 7 * 3600 + 6 * 60);
    }

    // ── Transfer network tests ────────────────────────────────────────────────

    [Fact]
    public void Transfer_BestJourney_ArrivesAt0830()
    {
        // A 08:00 → B 08:10 (Route R0), B 08:20 → C 08:30 (Route R1).
        var net = SyntheticNetworkFactory.CreateWithTransfer();
        var query = new RoutingQuery { SourceStopId = "A", TargetStopId = "C", DepartureTime = 7 * 3600 + 3000 };
        AssertAllAgree(query, net, expectedArrival: 8 * 3600 + 30 * 60);
    }

    [Fact]
    public void Transfer_JourneyHasOneTransfer()
    {
        var net = SyntheticNetworkFactory.CreateWithTransfer();
        var query = new RoutingQuery { SourceStopId = "A", TargetStopId = "C", DepartureTime = 7 * 3600 };
        foreach (var algo in AllAlgorithms(net))
        {
            var result = Run(algo, query, net);
            Assert.True(result.IsReachable, $"{algo.Name}: should be reachable.");
            Assert.Equal(1, result.BestJourney!.NumberOfTransfers);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_AfterLastConnectingTrip_Unreachable(bool multiCriteria)
    {
        var net = SyntheticNetworkFactory.CreateWithTransfer();
        // Route R0 only has one trip (T0). Departing too late to catch it.
        var query = new RoutingQuery
        {
            SourceStopId = "A",
            TargetStopId = "C",
            DepartureTime = 9 * 3600,
            MultiCriteria = multiCriteria
        };
        AssertAllUnreachable(query, net);
    }

    [Fact]
    public void Transfer_DirectLeg_NoTransferNeeded_WhenAlreadyAtB()
    {
        var net = SyntheticNetworkFactory.CreateWithTransfer();
        // Start at B, board R1 directly → 0 transfers.
        var query = new RoutingQuery { SourceStopId = "B", TargetStopId = "C", DepartureTime = 8 * 3600 };
        foreach (var algo in AllAlgorithms(net))
        {
            var result = Run(algo, query, net);
            Assert.True(result.IsReachable, $"{algo.Name}: should be reachable.");
            Assert.Equal(0, result.BestJourney!.NumberOfTransfers);
            Assert.Equal(8 * 3600 + 1800, result.BestJourney.ArrivalTime);
        }
    }

    // ── Journey structure tests ───────────────────────────────────────────────

    [Fact]
    public void Journey_LegsAreSortedChronologically()
    {
        var net = SyntheticNetworkFactory.CreateWithTransfer();
        var query = new RoutingQuery { SourceStopId = "A", TargetStopId = "C", DepartureTime = 7 * 3600 };
        foreach (var algo in AllAlgorithms(net))
        {
            var result = Run(algo, query, net);
            var legs = result.BestJourney!.Legs;
            for (int i = 1; i < legs.Count; i++)
                Assert.True(legs[i].DepartureTime >= legs[i - 1].ArrivalTime,
                    $"{algo.Name}: leg {i} departs before leg {i - 1} arrives.");
        }
    }

    [Fact]
    public void Journey_ConsecutiveSameTripLegs_AreMerged()
    {
        var net = SyntheticNetworkFactory.CreateLinear(stopCount: 5);
        // S0 → S4: one vehicle leg even though it spans 4 edges; self-footpaths may remain.
        var query = new RoutingQuery { SourceStopId = "S0", TargetStopId = "S4", DepartureTime = 8 * 3600 };
        foreach (var algo in AllAlgorithms(net))
        {
            var result = Run(algo, query, net);
            Assert.True(result.IsReachable, $"{algo.Name}: should be reachable.");
            Assert.Single(result.BestJourney!.Legs.Where(leg => !leg.IsWalk));
            Assert.All(result.BestJourney.Legs.Where(leg => leg.IsWalk), leg =>
            {
                Assert.Equal(leg.FromStopId, leg.ToStopId);
                Assert.Equal(0, leg.Duration);
            });
        }
    }
}
