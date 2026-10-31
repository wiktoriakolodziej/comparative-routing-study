using AlgorithmComparison.Core.Models;
using AlgorithmComparison.RaptorRealtime;
using TransitRealtime;
using StopTimeUpdate = TransitRealtime.TripUpdate.Types.StopTimeUpdate;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Fact]
    public void SuccessivePredictionsLoseAndRegainTransferInBothModes()
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)),
            Trip("connection", ("B", 210), ("C", 300)), Trip("direct", ("A", 110), ("C", 400)));
        var router = Router(net);
        var versions = new HashSet<string>();
        foreach (var (delay, arrival, transfers) in new[] { (30, 400, 0), (5, 305, 1), (-10, 290, 1) })
        {
            // Both services are updated independently in each complete snapshot.
            var report = router.ApplyFeed(Feed(Delay("first", 0, delay), Delay("connection", 0, delay == 30 ? 0 : delay)));
            Assert.True(report.Accepted);
            versions.Add(report.Version);
            Assert.Equal(arrival, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
            var pareto = router.RouteDetailed(Query(pareto: true)).Routing.ParetoJourneys.Journeys;
            Assert.Equal((arrival, transfers), (pareto[0].ArrivalTime, pareto[0].NumberOfTransfers));
        }
        Assert.Equal(3, versions.Count);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("start")]
    public void IncorrectTripInstanceCannotReplaceAcceptedVersion(string mismatch)
    {
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
        var first = router.ApplyFeed(Feed(Delay("t", 0, 10)));
        var wrong = Delay("t", 0, 99);
        wrong.Trip.RouteId = mismatch == "route" ? "wrong" : "r";
        wrong.Trip.StartTime = mismatch == "start" ? "00:01:41" : "00:01:40";
        Assert.False(router.ApplyFeed(Feed(wrong)).Accepted);
        Assert.Equal(first.Version, router.RouteDetailed(Query()).Version);
        Assert.Equal(210, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void ExplicitServiceDateMatchesPreviousDaysTripAfterMidnight()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 18, 23, 0, 0, TimeSpan.Zero));
        var router = Router(Network(Trip("night", ("A", 90000), ("C", 90600))), clock);
        var update = Delay("night", 0, 20);
        update.Trip.StartTime = "25:00:00";
        var feed = Feed(update);
        feed.Header.Timestamp = (ulong)clock.Now.ToUnixTimeSeconds();
        Assert.False(router.ApplyFeed(feed).Accepted); // No unambiguous service date.
        update.Trip.StartDate = "20261018";
        Assert.True(router.ApplyFeed(feed).Accepted);
        Assert.Equal(90620, router.RouteDetailed(Query(departure: 90000)).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void OldestAppliedObservationExpiresEvenWithFreshHeader()
    {
        var clock = new Clock(Now);
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))), clock);
        var update = Delay("t", 0, 50);
        update.Timestamp = (ulong)Now.AddMinutes(-4).ToUnixTimeSeconds();
        Assert.Equal(Now.AddMinutes(1), router.ApplyFeed(Feed(update)).ValidUntil);
        clock.Now = Now.AddSeconds(60);
        Assert.True(router.RouteDetailed(Query()).UsedRealtime);
        clock.Now = Now.AddSeconds(61);
        var result = router.RouteDetailed(Query());
        Assert.False(result.UsedRealtime);
        Assert.NotNull(result.FallbackReason);
        Assert.Equal(200, result.Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void DuplicateTripAndRegressingTimestampDoNotPartiallyPublish()
    {
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
        var original = router.ApplyFeed(Feed(Delay("t", 0, 10)));
        Assert.False(router.ApplyFeed(Feed(Delay("t", 0, 99), Delay("t", 0, 100))).Accepted);
        var older = Feed(Delay("t", 0, 90));
        older.Header.Timestamp -= 1;
        Assert.False(router.ApplyFeed(older).Accepted);
        var result = router.RouteDetailed(Query());
        Assert.Equal(original.Version, result.Version);
        Assert.Equal(210, result.Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void ArrivalAndDeparturePredictionsPreserveDwellAndPropagateDepartureDelay()
    {
        var trip = Trip("t", ("A", 100), ("B", 300), ("C", 500));
        trip = new AlgorithmComparison.Core.Models.Trip { Id = trip.Id, RouteId = trip.RouteId, ServiceId = trip.ServiceId, StopTimes = trip.StopTimes.Select(s => new StopTime { TripId = s.TripId, StopId = s.StopId, StopSequence = s.StopSequence, ArrivalTime = s.ArrivalTime, DepartureTime = s.ArrivalTime + 20 }).ToArray() };
        var router = Router(Network(trip));
        var update = Update("t", new StopTimeUpdate
        {
            StopSequence = 1,
            Arrival = new() { Delay = 10 },
            Departure = new() { Delay = 40 }
        });
        Assert.Equal(1, router.ApplyFeed(Feed(update)).AppliedTrips);
        var times = router.GetEffectiveStopTimes("t")!;
        Assert.Equal(310, times[1].Arrival);
        Assert.Equal(360, times[1].Departure);
        Assert.Equal(540, times[2].Arrival);
        update.StopTimeUpdate[0].Arrival = null;
        router.ApplyFeed(Feed(update));
        times = router.GetEffectiveStopTimes("t")!;
        Assert.Equal(340, times[1].Arrival);
        Assert.Equal(TimeOrigin.EstimatedArrival, times[1].ArrivalOrigin);
        Assert.Equal(20, times[1].Departure - times[1].Arrival);
    }

    [Fact]
    public void SkippedKeepsPropagationAndNoDataStopsItUntilNextPrediction()
    {
        var router = Router(Network(Trip("t", ("A", 100), ("B", 300), ("C", 500), ("D", 700), ("E", 900))));
        var update = Update("t", new StopTimeUpdate { StopSequence = 1, ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.Skipped },
            new() { StopSequence = 2, ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.NoData },
            new() { StopSequence = 4, Departure = new() { Delay = -10 } });
        update.Delay = 30;
        Assert.Equal(1, router.ApplyFeed(Feed(update)).AppliedTrips);
        var times = router.GetEffectiveStopTimes("t")!;
        Assert.Equal(new[] { 130, 330, 500, 700, 890 }, times.Select(t => t.Arrival));
        Assert.False(times[1].CanAlight);
        Assert.Equal(TimeOrigin.Propagated, times[1].ArrivalOrigin);
        Assert.Equal(TimeOrigin.NoData, times[2].ArrivalOrigin);
        Assert.Equal(TimeOrigin.Scheduled, times[3].ArrivalOrigin);
    }

    [Fact]
    public void MutatingInputAndPublishingNextFeedCannotChangeOldResults()
    {
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
        var feed = Feed(Delay("t", 0, 10));
        router.ApplyFeed(feed);
        var old = router.RouteDetailed(Query());
        var oldTimes = router.GetEffectiveStopTimes("t")!;
        feed.Entity[0].TripUpdate.StopTimeUpdate[0].Arrival.Delay = 70;
        Assert.Equal(210, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        router.ApplyFeed(feed);
        Assert.Equal(270, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.Equal(210, old.Routing.BestJourney!.ArrivalTime);
        Assert.Equal(210, oldTimes[1].Arrival);
    }

    [Fact]
    public void EarlyDepartureCanRemoveJourneyAvailableInScheduledTimetable()
    {
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
        Assert.True(router.RouteDetailed(Query(departure: 95)).Routing.IsReachable);
        router.ApplyFeed(Feed(Delay("t", 0, -10)));
        Assert.False(router.RouteDetailed(Query(departure: 95)).Routing.IsReachable);
        Assert.Equal(190, router.RouteDetailed(Query(departure: 90)).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void WalkingClosureHandlesDirectedChainsAndZeroCyclesAfterLastBoarding()
    {
        var net = Network(Trip("t", ("A", 100), ("B", 200)), Trip("unused", ("X", 1000), ("C", 1100)));
        var paths = Paths(net, ("B", "X", 0), ("X", "B", 0), ("X", "C", 7));
        var router = new RealtimeRaptorAlgorithm(net, paths, time: new Clock(Now));
        Assert.Equal(207, router.RouteDetailed(Query(maxTransfers: 0)).Routing.BestJourney!.ArrivalTime);
        Assert.False(router.RouteDetailed(Query(from: "C", to: "B", maxTransfers: 0)).Routing.IsReachable);
    }

    [Fact]
    public void RandomSequentialSnapshotsWithWalkingAndStatusesMatchIndependentOracle()
    {
        var random = new Random(20261020);
        for (var sample = 0; sample < 50; sample++)
        {
            var trips = Enumerable.Range(0, 8).Select(index =>
            {
                var stops = new[] { "A", "B", "C", "D" }.OrderBy(_ => random.Next()).ToArray();
                var t = random.Next(80, 160);
                return Trip($"t{index}", stops.Select(s => { t += random.Next(70, 100); return (s, t); }).ToArray());
            }).ToArray();
            var net = Network(trips);
            var walks = Paths(net, ("A", "B", random.Next(20, 250)), ("B", "D", random.Next(20, 250)),
                ("D", "C", random.Next(20, 250)), ("D", "B", 0));
            var router = new RealtimeRaptorAlgorithm(net, walks, time: new Clock(Now));
            for (var snapshot = 0; snapshot < 3; snapshot++)
            {
                var updates = trips.Where(_ => random.Next(4) != 0).Select(t =>
                {
                    var change = Delay(t.Id, 0, random.Next(-20, 30));
                    switch (random.Next(4))
                    {
                        case 0: change.Trip.ScheduleRelationship = TripDescriptor.Types.ScheduleRelationship.Canceled; break;
                        case 1:
                            change.StopTimeUpdate.Add(new StopTimeUpdate
                            {
                                StopSequence = 1,
                                ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.Skipped
                            }); break;
                        case 2:
                            change.StopTimeUpdate.Add(new StopTimeUpdate
                            {
                                StopSequence = 2,
                                ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.NoData
                            }); break;
                    }
                    return change;
                }).ToArray();
                var report = router.ApplyFeed(Feed(updates));
                Assert.True(report.Accepted);
                Assert.Equal(0, report.ExcludedTrips);
                var query = Query(departure: random.Next(0, 250), maxTransfers: random.Next(0, 3), pareto: true);
                var expected = Oracle(router, net, query, walks);
                var journeys = router.RouteDetailed(query).Routing.ParetoJourneys.Journeys;
                var actual = journeys.Select(j => (j.ArrivalTime, j.NumberOfTransfers)).ToArray();
                Assert.True(expected.SequenceEqual(actual), $"Seed 20261020, network {sample}, snapshot {snapshot}: expected {string.Join(';', expected)}, actual {string.Join(';', actual)}");
                var single = router.RouteDetailed(Query(departure: query.DepartureTime, maxTransfers: query.MaxTransfers!.Value)).Routing.BestJourney;
                Assert.Equal(expected.Length == 0 ? (int?)null : expected[0].Item1, single?.ArrivalTime);
                foreach (var journey in journeys)
                {
                    var stop = query.SourceStopId;
                    var time = query.DepartureTime;
                    foreach (var leg in journey.Legs)
                    {
                        Assert.Equal(stop, leg.FromStopId);
                        Assert.True(leg.DepartureTime >= time);
                        Assert.True(leg.ArrivalTime >= leg.DepartureTime);
                        if (leg.IsWalk)
                        {
                            Assert.True(walks.TryGetDuration(leg.FromStopId, leg.ToStopId, out var duration));
                            Assert.Equal(duration, leg.ArrivalTime - leg.DepartureTime);
                        }
                        else
                        {
                            var effective = router.GetEffectiveStopTimes(leg.TripId!)!;
                            Assert.Contains(effective, s => s.StopId == leg.FromStopId && s.Departure == leg.DepartureTime && s.CanBoard);
                            Assert.Contains(effective, s => s.StopId == leg.ToStopId && s.Arrival == leg.ArrivalTime && s.CanAlight);
                        }
                        stop = leg.ToStopId;
                        time = leg.ArrivalTime;
                    }
                    Assert.Equal(query.TargetStopId, stop);
                }
            }
        }
    }
}