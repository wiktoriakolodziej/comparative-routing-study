using System.Text.Json;
using AlgorithmComparison.CLI;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.RaptorRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchmarkReportsChangedArrivalLostTransferAndRecovery(bool pareto)
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)),
            Trip("second", ("B", 210), ("C", 300)), Trip("direct", ("A", 110), ("C", 400)));
        var clock = new Clock(Now);
        var walking = Paths(net);
        var router = new RealtimeRaptorAlgorithm(net, walking, time: clock);
        using var session = new RealtimeRoutingSession(router, new SnapshotTripUpdateSource(Feed()), time: clock);
        var benchmark = new RealtimeRaptorBenchmark(net, walking, router, clock, warmups: 1, repetitions: 2);
        var query = Query(pareto: pareto);
        RealtimeSessionCycle Cycle(TransitRealtime.FeedMessage feed)
        {
            var update = router.ApplyFeed(feed);
            return new(clock.Now, query, null, update, router.RouteDetailed(query));
        }
        var first = benchmark.Run(1, Cycle(Feed(Delay("first", 0, 30))));
        var pair = Assert.Single(first.Queries);
        Assert.True(pair.BaselineCriteriaAgree);
        Assert.True(pair.ChangedByRealtime);
        Assert.Equal(100, pair.EarliestArrivalDeltaSeconds);
        Assert.True(pair.DepartureRelativeToObservationSeconds < 0);
        Assert.Equal(1, pair.NewlyMissedTransfers);
        Assert.Equal(0, pair.RecoveredTransfers);
        Assert.Contains(pair.ControlJourneysInRealtime, j => j.Status == "MissedTransfer");
        Assert.Equal(2, pair.Original.Samples.Count);
        Assert.Equal(2, pair.ScheduledControl.Samples.Count);
        Assert.Equal(2, pair.Realtime.Samples.Count);
        Assert.All(pair.Realtime.Samples, sample => { Assert.True(sample.TimeMs >= 0); Assert.True(sample.AllocatedBytes >= 0); });
        Assert.True(first.Update!.Metrics!.TotalMs >= first.Update.Metrics.RouteGroupsMs);
        Assert.True(first.Update.Metrics.AllocatedBytes >= 0);
        var repeat = Assert.Single(benchmark.Run(2, Cycle(Feed(Delay("first", 0, 30)))).Queries);
        Assert.Equal(0, repeat.NewlyMissedTransfers);
        var recovered = Assert.Single(benchmark.Run(3, Cycle(Feed())).Queries);
        Assert.Equal(1, recovered.RecoveredTransfers);
        Assert.True(recovered.ComparableToPreviousCycle);
        Assert.False(recovered.ChangedByRealtime);
        Assert.Equal(0, recovered.EarliestArrivalDeltaSeconds);
        Assert.Equal(200, net.Trips["first"].StopTimes[1].ArrivalTime);
        await Task.CompletedTask;
    }

    [Fact]
    public void BenchmarkDoesNotTreatFallbackAsRealtimeObservation()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var clock = new Clock(Now);
        var graph = Paths(net);
        var router = new RealtimeRaptorAlgorithm(net, graph, time: clock);
        var benchmark = new RealtimeRaptorBenchmark(net, graph, router, clock, 0, 1);
        var result = benchmark.Run(1, new(Now, Query(), "API offline", null, router.RouteDetailed(Query())));
        Assert.False(result.UsedRealtime);
        var pair = Assert.Single(result.Queries);
        Assert.Null(pair.ChangedByRealtime);
        Assert.Null(pair.EarliestArrivalDeltaSeconds);
        Assert.Empty(pair.ControlJourneysInRealtime);
        Assert.Equal(0, pair.RecoveredTransfers);
    }

    [Fact]
    public void JourneyEvaluationAccountsForWalkingSkippedStopsAndCancellation()
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)), Trip("second", ("D", 220), ("C", 300)));
        var router = Router(net);
        var journey = new Journey { Legs = new[]
        {
            new JourneyLeg { FromStopId = "A", ToStopId = "B", DepartureTime = 100, ArrivalTime = 200, TripId = "first" },
            new JourneyLeg { FromStopId = "B", ToStopId = "D", DepartureTime = 200, ArrivalTime = 210 },
            new JourneyLeg { FromStopId = "D", ToStopId = "C", DepartureTime = 220, ArrivalTime = 300, TripId = "second" }
        } };
        Assert.Equal("Feasible", RealtimeRaptorBenchmark.Assess(journey, Query(), router).Status);
        router.ApplyFeed(Feed(Delay("first", 0, 11)));
        var missed = RealtimeRaptorBenchmark.Assess(journey, Query(), router);
        Assert.Equal("MissedTransfer", missed.Status);
        Assert.Equal(221, Assert.Single(missed.Transfers).ReadyAt);
        var canceled = Update("second");
        canceled.Trip.ScheduleRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship.Canceled;
        router.ApplyFeed(Feed(canceled));
        Assert.Equal("TripUnavailable", RealtimeRaptorBenchmark.Assess(journey, Query(), router).Status);
        router.ApplyFeed(Feed(Update("second", new TransitRealtime.TripUpdate.Types.StopTimeUpdate { StopSequence = 0,
            ScheduleRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship.Skipped })));
        Assert.Equal("BoardingOrAlightingForbidden", RealtimeRaptorBenchmark.Assess(journey, Query(), router).Status);
    }

    [Fact]
    public void RecoveryIsNotInferredWhenQueryDepartureChanges()
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)), Trip("second", ("B", 210), ("C", 300)));
        var clock = new Clock(Now);
        var graph = Paths(net);
        var router = new RealtimeRaptorAlgorithm(net, graph, time: clock);
        var benchmark = new RealtimeRaptorBenchmark(net, graph, router, clock, 0, 1);
        var update = router.ApplyFeed(Feed(Delay("first", 0, 30)));
        benchmark.Run(1, new(Now, Query(), null, update, router.RouteDetailed(Query())));
        update = router.ApplyFeed(Feed());
        var changedQuery = Query(departure: 1);
        var result = Assert.Single(benchmark.Run(2, new(Now, changedQuery, null, update, router.RouteDetailed(changedQuery))).Queries);
        Assert.False(result.ComparableToPreviousCycle);
        Assert.Equal(0, result.RecoveredTransfers);
    }

    [Fact]
    public async Task ReportKeepsBothCyclesAndIncludesStatisticsAndConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "realtime-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var net = Network(Trip("t", ("A", 100), ("C", 200)));
            var clock = new Clock(Now);
            var graph = Paths(net);
            var router = new RealtimeRaptorAlgorithm(net, graph, time: clock);
            var benchmark = new RealtimeRaptorBenchmark(net, graph, router, clock, 0, 1);
            var writer = new RealtimeBenchmarkReportWriter(root);
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var update = router.ApplyFeed(Feed(Delay("t", 0, cycle * 10)));
                await writer.WriteAsync(benchmark.Run(cycle, new(Now, Query(), null, update, router.RouteDetailed(Query()))), new { Radius = 500 });
            }
            Assert.Equal(2, Directory.GetFiles(root, "cycle-*.json").Length);
            Assert.Equal(7, File.ReadAllLines(Path.Combine(root, "comparison.csv")).Length);
            Assert.Contains("P95", File.ReadAllText(Path.Combine(root, "summary.txt")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "cycle-000001.json")));
            Assert.Equal(10, json.RootElement.GetProperty("Queries")[0].GetProperty("EarliestArrivalDeltaSeconds").GetInt32());
            Assert.Throws<IOException>(() => new RealtimeBenchmarkReportWriter(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StatisticsUseMedianAndNearestRankP95()
    {
        var stats = BenchmarkStatistics.Of(Enumerable.Range(1, 20).Select(i => (double)i));
        Assert.Equal(10.5, stats.Median);
        Assert.Equal(19, stats.P95);
        Assert.Equal(20, stats.Maximum);
    }
}
