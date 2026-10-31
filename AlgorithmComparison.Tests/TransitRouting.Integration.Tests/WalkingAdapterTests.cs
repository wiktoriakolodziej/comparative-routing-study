using System.Collections;
using System.Reflection;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Walking;
using AlgorithmComparison.Algorithms.Csa;
using Dijkstra = AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using Raptor = AlgorithmComparison.Algorithms.Raptor;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class WalkingAdapterTests
{
    private static Stop Stop(string id, double meters) => new()
    { Id = id, Name = id, Latitude = 0, Longitude = meters / 6_371_000 * 180 / Math.PI };
    private static WalkingTransferService Service() => new(new[] {
        Stop("O", 10000), Stop("A", 0), Stop("B", 350), Stop("C", 700), Stop("D", 1050) });
    private static int WalkingTime(WalkingTransferService service) =>
        new[] { ("A", "B"), ("B", "C"), ("C", "D") }.Sum(pair =>
        {
            Assert.True(service.Graph.TryGetDuration(pair.Item1, pair.Item2, out var duration)); return duration;
        });
    private static Dijkstra.TDDijkstraInput DijkstraState(WalkingTransferService service, params Dijkstra.Label[] sources)
    {
        var input = new Dijkstra.TDDijkstraInput();
        foreach (var id in service.StopIds.Order())
        {
            var index = input.Graph.StopIdToIndex.Count;
            input.Graph.StopIdToIndex.Add(id, index);
            input.Graph.OriginalStopIds.Add(index, id);
        }
        foreach (var label in sources)
        {
            var key = (label.StopId, label.TripId);
            if (!input.LabelsByState.TryGetValue(key, out var bag)) input.LabelsByState.Add(key, bag = new());
            bag.Add(label);
        }
        return input;
    }

    [Fact]
    public void DijkstraPreservesVehicleStatesAndParetoAndReconstructsEachWalkingEdge()
    {
        var service = Service();
        var fast = new Dijkstra.Label { StopId = 0, ArrivalTime = 100, Boardings = 2, TripId = 7 };
        var fewer = new Dijkstra.Label { StopId = 0, ArrivalTime = 200, Boardings = 1, TripId = 8 };
        var input = DijkstraState(service, fast, fewer); // Alphabetic mapping A=0, D=3.
        var router = new Dijkstra.TimeDependentDijkstraAlgorithm(service.Graph);
        var result = router.PropagateWalkingWithService(input, service, new[] { fast, fewer }, 3);
        Assert.Same(fast, Assert.Single(input.LabelsByState[(0, 7)]));
        Assert.Same(fewer, Assert.Single(input.LabelsByState[(0, 8)]));
        var target = input.LabelsByState[(3, null)].OrderBy(label => label.ArrivalTime).ToArray();
        Assert.Equal(new[] { 2, 1 }, target.Select(label => label.Boardings));
        Assert.Equal(new[] { 100 + WalkingTime(service), 200 + WalkingTime(service) }, target.Select(label => label.ArrivalTime));
        Assert.Equal(2, input.TargetLabels.Count);
        Assert.All(target, label =>
        {
            Assert.Null(label.TripId);
            var journey = (Journey)Invoke(typeof(Dijkstra.TimeDependentDijkstraAlgorithm), "ReconstructJourney", null, label, input)!;
            Assert.Equal(new[] { "B", "C", "D" }, journey.Legs.Select(leg => leg.ToStopId));
            Assert.All(journey.Legs, leg => Assert.True(leg.IsWalk));
        });
        Assert.Equal(2, result.LabelsByStop["D"].Count);
        Assert.True(input.PriorityQueue.Count > 0);
    }

    [Fact]
    public void DijkstraRespectsTargetPruningInactiveSourcesAndCancellation()
    {
        var service = Service();
        var seed = new Dijkstra.Label { StopId = 0, ArrivalTime = 100, Boardings = 1, TripId = 7 };
        var input = DijkstraState(service, seed);
        var router = new Dijkstra.TimeDependentDijkstraAlgorithm(service.Graph);
        input.TargetLabels.Add(new Dijkstra.Label { StopId = 3, ArrivalTime = 10, Boardings = 1 });
        router.PropagateWalkingWithService(input, service, new[] { seed }, 3);
        Assert.False(input.LabelsByState.ContainsKey((3, null)));
        Assert.Equal(0, input.PriorityQueue.Count);
        seed.Active = false;
        Assert.Empty(router.PropagateWalkingWithService(input, service, new[] { seed }, 3).TimesByStop);
        Assert.Throws<OperationCanceledException>(() => router.PropagateWalkingWithService(input, service, new[] { seed }, 3,
            new CancellationToken(true)));
        Assert.Single(input.LabelsByState);
    }

    private static Raptor.RaptorInput RaptorState(WalkingTransferService service, int round)
    {
        var input = new Raptor.RaptorInput();
        foreach (var id in service.StopIds.Order())
        {
            var index = input.StopIdToIndex.Count;
            input.StopIdToIndex.Add(id, index); input.StopIndexToId.Add(index, id);
            input.T.Add(index, new[] { int.MaxValue, int.MaxValue, int.MaxValue });
            input.TStar.Add(index, int.MaxValue);
        }
        var a = input.StopIdToIndex["A"]; var origin = input.StopIdToIndex["O"];
        input.T[a][round] = 100; input.TStar[a] = 100;
        if (round > 0)
        {
            input.T[origin][0] = 0; input.TStar[origin] = 0;
            input.Parents[(a, round)] = new(Raptor.RaptorParentKind.Ride, origin, 0, 50, 100, RouteId: 2, TripId: 7);
            input.TripIndexToId.Add(7, "trip"); input.RouteIndexToGtfsRouteId.Add(2, "route");
        }
        return input;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RaptorPropagatesWithinSameRoundAndKeepsPhysicalParentChain(int round)
    {
        var service = Service(); var input = RaptorState(service, round);
        var router = new Raptor.RaptorAlgorithm(service.Graph);
        router.PropagateWalkingWithService(input, service, round);
        var d = input.StopIdToIndex["D"];
        Assert.Equal(100 + WalkingTime(service), input.T[d][round]);
        Assert.Equal(input.T[d][round], input.TStar[d]);
        Assert.Equal(int.MaxValue, input.T[d][round == 0 ? 1 : 0]);
        Assert.All(new[] { "B", "C", "D" }, id =>
        {
            var stop = input.StopIdToIndex[id];
            Assert.Contains(stop, input.MarkedStops);
            Assert.Equal(round, input.Parents[(stop, round)].PreviousRound);
        });
        var journey = (Journey)Invoke(typeof(Raptor.RaptorAlgorithm), "ExtractJourney", null,
            input, input.StopIdToIndex[round == 0 ? "A" : "O"], d, round)!;
        Assert.Equal(round == 0 ? 3 : 4, journey.Legs.Count);
        Assert.Equal(new[] { "B", "C", "D" }, journey.Legs.Where(leg => leg.IsWalk).Select(leg => leg.ToStopId));
        Assert.Equal(0, journey.NumberOfTransfers);
        input.MarkedStops.Clear();
        router.PropagateWalkingWithService(input, service, round);
        Assert.Empty(input.MarkedStops);
    }

    [Fact]
    public void RaptorCompetingSourcesAndZeroEdgesDoNotCreateParentCycles()
    {
        var service = new WalkingTransferService(new[] { Stop("O", 10000), Stop("A", 0), Stop("B", 0), Stop("C", 350), Stop("D", 700) });
        var input = RaptorState(service, 1);
        var b = input.StopIdToIndex["B"];
        input.T[b][1] = 90; input.TStar[b] = 90;
        input.Parents[(b, 1)] = new(Raptor.RaptorParentKind.Ride, input.StopIdToIndex["O"], 0, 40, 90, RouteId: 2, TripId: 7);
        new Raptor.RaptorAlgorithm(service.Graph).PropagateWalkingWithService(input, service, 1);
        var journey = (Journey)Invoke(typeof(Raptor.RaptorAlgorithm), "ExtractJourney", null,
            input, input.StopIdToIndex["O"], input.StopIdToIndex["D"], 1)!;
        Assert.Equal("B", journey.Legs[0].ToStopId);
        Assert.Equal(3, journey.Legs.Count);
        Assert.Equal(90, input.T[input.StopIdToIndex["A"]][1]);
    }

    private static CsaInput CsaState(WalkingTransferService service) => new()
    {
        S = service.StopIds.ToDictionary(id => id, _ => int.MaxValue),
        J = service.StopIds.ToDictionary(id => id,
            _ => ((Connection?)null, (Connection?)null, (Footpath?)null))
    };
    private static Connection Ride(string id = "ride", int arrival = 100) => new()
    { Id = id, TripId = id, RouteId = "route", DepartureStopId = "O", ArrivalStopId = "A", DepartureTime = 50, ArrivalTime = arrival };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CsaForwardPreservesAggregatePointerAndReturnsDetailedPath(bool afterVehicle)
    {
        var service = Service(); var input = CsaState(service); var router = new ConnectionScanAlgorithm(service.Graph);
        var ride = Ride();
        input.S[afterVehicle ? "O" : "A"] = afterVehicle ? 0 : 100;
        var origin = afterVehicle ? new CsaWalkingOrigin(ride, ride) : new CsaWalkingOrigin(null, null);
        var result = router.PropagateWalkingWithService(input, service,
            new[] { new WalkingSeed<CsaWalkingOrigin>("A", 100, afterVehicle ? 1 : 0, origin) });
        Assert.Equal(100 + WalkingTime(service), input.S["D"]);
        Assert.Equal(3, result.LabelsByStop["D"][0].GetPath().Count);
        var journeyResult = (AlgorithmComparison.Core.Results.RoutingResult)Invoke(typeof(ConnectionScanAlgorithm),
            "ExtractJourney", router, "D", afterVehicle ? "O" : "A", afterVehicle ? 0 : 100, input)!;
        Assert.Equal(input.S["D"], journeyResult.BestJourney!.ArrivalTime);
        if (afterVehicle)
        {
            Assert.Same(ride, input.J["D"].EnterConnection);
            Assert.Same(ride, input.J["D"].ExitConnection);
            var footpath = Assert.IsType<Footpath>(input.J["D"].Footpath);
            Assert.Equal(WalkingTime(service), footpath.DurationSeconds);
            Assert.Equal("A", footpath.FromStopId);
        }
        else Assert.Null(input.J["D"].EnterConnection);
    }

    [Fact]
    public void CsaRejectsMismatchedVehicleOriginAndDoesNotOverwriteEarlierTimes()
    {
        var service = Service(); var input = CsaState(service); var router = new ConnectionScanAlgorithm(service.Graph);
        input.S["C"] = 5;
        var initial = new CsaWalkingOrigin(null, null);
        router.PropagateWalkingWithService(input, service, new[] { new WalkingSeed<CsaWalkingOrigin>("A", 100, 0, initial) });
        Assert.Equal(5, input.S["C"]);
        Assert.Throws<ArgumentException>(() => router.PropagateWalkingWithService(input, service,
            new[] { new WalkingSeed<CsaWalkingOrigin>("B", 100, 1, new CsaWalkingOrigin(Ride(), Ride())) }));
    }

    private static readonly Type Csa = typeof(ConnectionScanAlgorithm);
    private static Type Nested(string name) => Csa.GetNestedType(name, BindingFlags.NonPublic)!;
    private static IDictionary Profiles(WalkingTransferService service, int budgets)
    {
        var entry = Nested("ParetoProfileEntry"); var list = typeof(List<>).MakeGenericType(entry);
        var profiles = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), list))!;
        foreach (var stop in service.StopIds)
        {
            var entries = (IList)Activator.CreateInstance(list)!;
            entries.Add(Activator.CreateInstance(entry, int.MaxValue, Enumerable.Repeat(int.MaxValue, budgets).ToArray(),
                Array.CreateInstance(Nested("ProfilePointer"), budgets)));
            profiles.Add(stop, entries);
        }
        return profiles;
    }
    private static Array Pointers(int count, Connection ride)
    {
        var pointer = Activator.CreateInstance(Nested("ParetoPointer"), ride, ride, null, null, null);
        var pointers = Array.CreateInstance(Nested("ParetoPointer"), count);
        for (var i = 0; i < count; i++) pointers.SetValue(pointer, i);
        return pointers;
    }
    private static object Entry(IDictionary profiles, string stop, int departure) => Invoke(Csa, "EvaluateProfile", null, profiles[stop], departure)!;
    private static int[] Arrivals(object entry) => (int[])entry.GetType().GetProperty("ArrivalByMaxLegs")!.GetValue(entry)!;

    [Fact]
    public void CsaProfileAdapterPreservesBudgetsAndUsesLatestWalkingDeparture()
    {
        var service = Service(); var profiles = Profiles(service, 2);
        var ride = new Connection { Id = "t", TripId = "t", RouteId = "r", DepartureStopId = "D", ArrivalStopId = "O", DepartureTime = 2000, ArrivalTime = 2200 };
        var arrivals = new[] { int.MaxValue, 2200 }; var pointers = Pointers(2, ride); pointers.SetValue(null, 0);
        Invoke(Csa, "PropagateWalkingProfilesWithService", null, service, profiles, "D", 2000, arrivals, pointers, CancellationToken.None);
        var start = 2000 - WalkingTime(service);
        var entry = Entry(profiles, "A", start);
        Assert.Equal(new[] { int.MaxValue, 2200 }, Arrivals(entry));
        Assert.Equal(new[] { int.MaxValue, int.MaxValue }, Arrivals(Entry(profiles, "A", start + 1)));
        Assert.Equal(new[] { int.MaxValue, int.MaxValue }, Arrivals(Entry(profiles, "O", 0)));
        var profilePointer = ((Array)entry.GetType().GetProperty("Pointers")!.GetValue(entry)!).GetValue(1);
        var journey = (Journey)Invoke(Csa, "ReconstructParetoJourney", null, profilePointer)!;
        Assert.Equal("A", journey.DepartureStopId);
        Assert.Equal(start, journey.DepartureTime);
        Assert.Equal(2200, journey.ArrivalTime);
        Assert.Equal(WalkingTime(service), journey.Legs[0].ArrivalTime - journey.Legs[0].DepartureTime);
        arrivals[1] = 9999;
        Assert.Equal(2200, Arrivals(entry)[1]); // Profile must not alias scratch vectors.
    }

    [Fact]
    public void CsaWalkingToTargetIncludesMultipleHopsAndInfinityForDisconnectedStops()
    {
        var service = Service();
        var result = (Dictionary<string, (int Duration, Footpath? Path)>)Invoke(Csa, "PrepareWalkingToTargetWithService", null,
            service, "D", CancellationToken.None)!;
        Assert.Equal(WalkingTime(service), result["A"].Duration);
        Assert.Equal(0, result["D"].Duration);
        Assert.Null(result["D"].Path);
        Assert.Equal(int.MaxValue, result["O"].Duration);
        Assert.Equal("D", result["A"].Path!.ToStopId);
    }

    private static object? Invoke(Type type, string method, object? instance, params object?[] args) =>
        type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.Invoke(instance, args);
}