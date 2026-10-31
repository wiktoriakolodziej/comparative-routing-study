using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Walking;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class WalkingTransferServiceTests
{
    private static Stop Stop(string id, double meters) => new()
    { Id = id, Name = id, Latitude = 0, Longitude = meters / 6_371_000 * 180 / Math.PI };
    private static WalkingTransferService Chain() => new(new[] { Stop("A", 0), Stop("B", 350), Stop("C", 700), Stop("D", 1050) });

    [Fact]
    public void RadiusIsLocalButPropagationCanWalkBeyondIt()
    {
        var service = Chain();
        Assert.Equal(500, Assert.IsType<RadiusFootpathGraph>(service.Graph).RadiusMeters);
        Assert.True(service.Graph.TryGetDuration("A", "B", out var ab));
        Assert.False(service.Graph.TryGetDuration("A", "C", out _));
        Assert.True(service.Graph.TryGetDuration("B", "C", out var bc));
        Assert.True(service.Graph.TryGetDuration("C", "D", out var cd));
        var result = service.PropagateTimes(new Dictionary<string, int> { ["A"] = 8 * 3600 });
        Assert.Equal(8 * 3600 + ab + bc + cd, result.TimesByStop["D"]);
        var path = result.LabelsByStop["D"][0].GetPath();
        Assert.Equal(new[] { "B", "C", "D" }, path.Select(edge => edge.ToStopId));
        Assert.Equal("A", result.LabelsByStop["D"][0].Origin.Context);
        Assert.False(result.LabelsByStop["A"][0].HasWalk);
    }

    [Fact]
    public void ConfigurableRadiusIncludesOnlyPermittedNeighborsAndIsSymmetric()
    {
        var graph = new RadiusFootpathGraph(new[] { Stop("A", 0), Stop("B", 499), Stop("C", 501) });
        Assert.True(graph.TryGetDuration("A", "B", out var ab));
        Assert.True(graph.TryGetDuration("B", "A", out var ba));
        Assert.Equal(ab, ba);
        Assert.False(graph.TryGetDuration("A", "C", out _));
        Assert.True(graph.TryGetDuration("A", "A", out var self));
        Assert.Equal(0, self);
        Assert.Empty(graph.GetIncomingBatch("missing"));
        var smaller = new RadiusFootpathGraph(new[] { Stop("A", 0), Stop("B", 499) }, 400);
        Assert.False(smaller.TryGetDuration("A", "B", out _));
    }

    [Fact]
    public void ZeroDurationCyclesTerminateAndDisconnectedStopsAreAbsent()
    {
        var service = new WalkingTransferService(new[] { Stop("A", 0), Stop("B", 0), Stop("C", 1000) }, 0);
        var result = service.PropagatePareto(new[] {
            new WalkingSeed<string>("A", 100, 1, "first"), new WalkingSeed<string>("B", 100, 1, "equal") });
        Assert.Equal(2, result.LabelsByStop.Count);
        Assert.All(result.LabelsByStop.Values, labels => Assert.Single(labels));
        Assert.False(result.TimesByStop.ContainsKey("C"));
    }

    [Fact]
    public void ParetoRetainsTimeBoardingTradeoffsAndOriginContexts()
    {
        var service = Chain();
        var result = service.PropagatePareto(new[] {
            new WalkingSeed<string>("A", 100, 2, "fast"),
            new WalkingSeed<string>("A", 200, 1, "few-boardings"),
            new WalkingSeed<string>("A", 300, 3, "dominated") });
        Assert.Equal(new[] { 2, 1 }, result.LabelsByStop["D"].Select(label => label.Boardings));
        Assert.Equal(new[] { "fast", "few-boardings" }, result.LabelsByStop["D"].Select(label => label.Origin.Context));
        Assert.All(result.LabelsByStop["D"], label => Assert.True(label.HasWalk));
    }

    [Fact]
    public void BackwardPropagationFindsLatestDepartureAndReconstructsTravelOrder()
    {
        var service = Chain();
        var result = service.PropagateTimes(new Dictionary<string, int> { ["D"] = 1000 }, WalkingDirection.BackwardLatestDeparture);
        var label = result.LabelsByStop["A"][0];
        var path = label.GetPath();
        Assert.Equal(new[] { "A", "B", "C" }, path.Select(edge => edge.FromStopId));
        Assert.Equal(1000 - path.Sum(edge => edge.DurationSeconds), label.Time);
        Assert.Equal("D", label.Origin.StopId);
    }

    [Fact]
    public void RepeatedAndConcurrentCallsDoNotShareQueryState()
    {
        var service = Chain();
        var original = service.PropagateTimes(new Dictionary<string, int> { ["A"] = 100 });
        var saved = original.TimesByStop["D"];
        Parallel.For(0, 20, i =>
        {
            var result = service.PropagateTimes(new Dictionary<string, int> { ["A"] = 100 + i });
            Assert.Equal(saved + i, result.TimesByStop["D"]);
        });
        Assert.Equal(saved, original.TimesByStop["D"]);
    }

    [Theory]
    [InlineData(WalkingDirection.ForwardEarliestArrival)]
    [InlineData(WalkingDirection.BackwardLatestDeparture)]
    public void RandomNetworksMatchIndependentAllPairsReference(WalkingDirection direction)
    {
        var random = new Random(20261021);
        for (var sample = 0; sample < 40; sample++)
        {
            var stops = Enumerable.Range(0, 12).Select(i => new Stop
            {
                Id = i.ToString(),
                Name = i.ToString(),
                Latitude = 50 + random.NextDouble() * 0.012,
                Longitude = 19 + random.NextDouble() * 0.012
            }).ToArray();
            var service = new WalkingTransferService(stops, 300);
            const long infinity = long.MaxValue / 4;
            var distances = new long[12, 12];
            for (var i = 0; i < 12; i++) for (var j = 0; j < 12; j++)
                    distances[i, j] = service.Graph.TryGetDuration(i.ToString(), j.ToString(), out var seconds) ? seconds : infinity;
            // Floyd-Warshall has no queue, labels or shared propagation logic.
            for (var k = 0; k < 12; k++) for (var i = 0; i < 12; i++) for (var j = 0; j < 12; j++)
                        distances[i, j] = Math.Min(distances[i, j], distances[i, k] + distances[k, j]);
            var seeds = Enumerable.Range(0, 6).Select(i => new WalkingSeed<int>(random.Next(12).ToString(),
                random.Next(500, 1500), random.Next(4), i)).ToArray();
            var actual = service.PropagatePareto(seeds, direction);
            var single = service.PropagateTimes(seeds, direction);
            for (var stop = 0; stop < 12; stop++)
            {
                var points = seeds.Select(seed => (Seed: seed, Distance: direction == WalkingDirection.ForwardEarliestArrival
                    ? distances[int.Parse(seed.StopId), stop] : distances[stop, int.Parse(seed.StopId)]))
                    .Where(x => x.Distance != infinity).Select(x => (Time: (int)(x.Seed.Time +
                        (direction == WalkingDirection.ForwardEarliestArrival ? x.Distance : -x.Distance)), x.Seed.Boardings)).Distinct().ToArray();
                bool NoWorse(int a, int b) => direction == WalkingDirection.ForwardEarliestArrival ? a <= b : a >= b;
                var expected = points.Where(p => !points.Any(q => q != p && NoWorse(q.Time, p.Time) && q.Boardings <= p.Boardings))
                    .OrderBy(p => p.Time).ThenBy(p => p.Boardings).ToArray();
                if (expected.Length == 0) { Assert.False(actual.LabelsByStop.ContainsKey(stop.ToString())); Assert.False(single.TimesByStop.ContainsKey(stop.ToString())); continue; }
                Assert.Equal(expected, actual.LabelsByStop[stop.ToString()].Select(label => (label.Time, label.Boardings)).OrderBy(p => p.Time).ThenBy(p => p.Boardings));
                Assert.Equal(direction == WalkingDirection.ForwardEarliestArrival ? points.Min(p => p.Time) : points.Max(p => p.Time), single.TimesByStop[stop.ToString()]);
                foreach (var label in actual.LabelsByStop[stop.ToString()])
                {
                    var path = label.GetPath();
                    var current = direction == WalkingDirection.ForwardEarliestArrival ? label.Origin.StopId : label.StopId;
                    foreach (var edge in path) { Assert.Equal(current, edge.FromStopId); current = edge.ToStopId; }
                    Assert.Equal(direction == WalkingDirection.ForwardEarliestArrival ? label.StopId : label.Origin.StopId, current);
                    Assert.Equal(direction == WalkingDirection.ForwardEarliestArrival ? label.Time - label.Origin.Time : label.Origin.Time - label.Time,
                        path.Sum(edge => edge.DurationSeconds));
                }
            }
        }
    }

    [Fact]
    public void CancellationDuringPropagationDoesNotLeakStateIntoTheNextCall()
    {
        var service = Chain();
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => service.PropagateTimes(
            new[] { new WalkingSeed<int>("A", 0, 0, 0) }, cancellation: cancellation.Token,
            accept: label =>
            {
                if (label.HasWalk) cancellation.Cancel();
                return true;
            }));
        var actual = service.PropagateTimes(new Dictionary<string, int> { ["A"] = 0 });
        var expected = Chain().PropagateTimes(new Dictionary<string, int> { ["A"] = 0 });
        Assert.Equal(expected.TimesByStop.OrderBy(p => p.Key), actual.TimesByStop.OrderBy(p => p.Key));
    }

    [Fact]
    public void InvalidInputsCancellationAndOverflowAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadiusFootpathGraph(new[] { Stop("A", 0) }, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadiusFootpathGraph(new[] { Stop("A", 0) }, speedMetersPerSecond: 0));
        Assert.Throws<ArgumentException>(() => new RadiusFootpathGraph(new[] { Stop("A", 0), Stop("A", 10) }));
        var service = Chain();
        Assert.Throws<ArgumentException>(() => service.PropagateTimes(new Dictionary<string, int> { ["unknown"] = 0 }));
        Assert.Throws<ArgumentException>(() => service.PropagatePareto(new[] { new WalkingSeed<int>("A", 0, -1, 0) }));
        Assert.Throws<OperationCanceledException>(() => service.PropagateTimes(new Dictionary<string, int> { ["A"] = 0 }, cancellation: new CancellationToken(true)));
        Assert.Throws<OverflowException>(() => service.PropagateTimes(new Dictionary<string, int> { ["A"] = int.MaxValue }));
        Assert.Empty(service.PropagateTimes(new Dictionary<string, int>()).TimesByStop);
    }
}