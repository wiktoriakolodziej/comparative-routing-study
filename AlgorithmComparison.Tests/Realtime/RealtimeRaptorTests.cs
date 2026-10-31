using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.RaptorRealtime;
using TransitRealtime;
using Trip = AlgorithmComparison.Core.Models.Trip;
using Stop = AlgorithmComparison.Core.Models.Stop;
using StopTimeUpdate = TransitRealtime.TripUpdate.Types.StopTimeUpdate;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    private static readonly DateOnly Date = new(2026, 10, 18);
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DelayBreaksTransferAndSelectsAlternativeWithoutChangingBase()
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)),
            Trip("connection", ("B", 210), ("C", 300)), Trip("direct", ("A", 110), ("C", 400)));
        var router = Router(net);
        Assert.Equal(300, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.True(router.ApplyFeed(Feed(Delay("first", 0, 30))).Accepted);
        var result = router.RouteDetailed(Query());
        Assert.True(result.UsedRealtime);
        Assert.Equal(400, result.Routing.BestJourney!.ArrivalTime);
        Assert.Equal("direct", Assert.Single(result.Routing.BestJourney.Legs).TripId);
        Assert.Equal(200, net.Trips["first"].StopTimes[1].ArrivalTime);
        Assert.Equal(230, router.GetEffectiveStopTimes("first")![1].Arrival);
    }

    [Fact]
    public void FullSnapshotsReplaceInsteadOfAccumulatingAndRestoreAbsentTrips()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        var feed = Feed(Delay("t", 0, 60));
        router.ApplyFeed(feed);
        router.ApplyFeed(feed);
        Assert.Equal(260, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        router.ApplyFeed(Feed());
        Assert.Equal(200, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void CanceledCourseIsRemovedAndCanBeRestoredByNextFullFeed()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        var update = Update("t");
        update.Trip.ScheduleRelationship = TripDescriptor.Types.ScheduleRelationship.Canceled;
        Assert.Equal(1, router.ApplyFeed(Feed(update)).CanceledTrips);
        Assert.False(router.RouteDetailed(Query()).Routing.IsReachable);
        Assert.Null(router.GetEffectiveStopTimes("t"));
        router.ApplyFeed(Feed());
        Assert.True(router.RouteDetailed(Query()).Routing.IsReachable);
    }

    [Fact]
    public void SkippedStopCannotBeBoardedOrAlightedButCanBePassed()
    {
        var net = Network(Trip("t", ("A", 100), ("B", 200), ("C", 300)));
        var router = Router(net);
        var update = Update("t", new StopTimeUpdate { StopSequence = 1, ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.Skipped });
        Assert.True(router.ApplyFeed(Feed(update)).Accepted);
        Assert.Equal(300, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.False(router.RouteDetailed(Query(to: "B")).Routing.IsReachable);
        Assert.False(router.RouteDetailed(Query(from: "B")).Routing.IsReachable);
    }

    [Fact]
    public void SparseNegativeDelayAndNoDataHaveExplicitOrigins()
    {
        var net = Network(Trip("t", ("A", 100), ("B", 300), ("C", 500), ("D", 700)));
        var router = Router(net);
        var update = Update("t", new() { StopSequence = 1, Arrival = new() { Delay = -30 } },
            new() { StopSequence = 3, ScheduleRelationship = StopTimeUpdate.Types.ScheduleRelationship.NoData });
        Assert.Equal(1, router.ApplyFeed(Feed(update)).AppliedTrips);
        var times = router.GetEffectiveStopTimes("t")!;
        Assert.Equal(new[] { 100, 270, 470, 700 }, times.Select(t => t.Arrival));
        Assert.Equal(TimeOrigin.Scheduled, times[0].ArrivalOrigin);
        Assert.Equal(TimeOrigin.ReportedDelay, times[1].ArrivalOrigin);
        Assert.Equal(TimeOrigin.EstimatedDeparture, times[1].DepartureOrigin);
        Assert.Equal(TimeOrigin.Propagated, times[2].ArrivalOrigin);
        Assert.Equal(TimeOrigin.NoData, times[3].ArrivalOrigin);
    }

    [Fact]
    public void AbsoluteTimeTakesPrecedenceAndSupportsAfterMidnight()
    {
        var net = Network(Trip("t", ("A", 90000), ("C", 90600)));
        var router = Router(net);
        var origin = ServiceDayTime.Origin(Date, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        var update = Update("t", new StopTimeUpdate
        {
            StopSequence = 0,
            Arrival = new() { Time = origin.AddSeconds(90030).ToUnixTimeSeconds(), Delay = 999 }
        });
        Assert.Equal(1, router.ApplyFeed(Feed(update)).AppliedTrips);
        Assert.Equal(90630, router.RouteDetailed(Query(departure: 89999)).Routing.BestJourney!.ArrivalTime);
        Assert.Equal(TimeOrigin.ReportedTime, router.GetEffectiveStopTimes("t")![0].ArrivalOrigin);
    }

    [Theory]
    [InlineData(2026, 3, 29)]
    [InlineData(2026, 10, 25)]
    public void ServiceOriginUsesNoonMinusTwelveHoursOnDstTransitions(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var origin = ServiceDayTime.Origin(date, zone);
        var localNoon = TimeZoneInfo.ConvertTime(origin.AddHours(12), zone);
        Assert.Equal(12, localNoon.Hour);
        Assert.Equal(date, DateOnly.FromDateTime(localNoon.DateTime));
        Assert.Equal(90000, ServiceDayTime.ToSeconds(origin.AddHours(25).ToUnixTimeSeconds(), date, zone));
    }

    [Fact]
    public void OvertakingInsideSamePatternDoesNotHideFasterTrip()
    {
        var net = Network(Trip("early", ("A", 100), ("B", 200), ("C", 300)),
            Trip("later", ("A", 120), ("B", 220), ("C", 320)));
        var router = Router(net);
        var report = router.ApplyFeed(Feed(Delay("early", 1, 150)));
        Assert.True(report.Accepted);
        Assert.Equal(2, report.RouteGroups);
        var result = router.RouteDetailed(Query()).Routing.BestJourney!;
        Assert.Equal(320, result.ArrivalTime);
        Assert.Equal("later", Assert.Single(result.Legs).TripId);
    }

    [Fact]
    public void ParetoKeepsFastTransferAndSlowDirectJourneyAndRespectsLimit()
    {
        var net = Network(Trip("one", ("A", 100), ("B", 150)),
            Trip("two", ("B", 160), ("C", 200)), Trip("direct", ("A", 110), ("C", 300)));
        var router = Router(net);
        router.ApplyFeed(Feed());
        var result = router.RouteDetailed(Query(pareto: true)).Routing.ParetoJourneys.Journeys;
        Assert.Equal(new[] { (200, 1), (300, 0) }, result.Select(j => (j.ArrivalTime, j.NumberOfTransfers)));
        Assert.Equal(300, router.RouteDetailed(Query(maxTransfers: 0)).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void ExpiredOrRejectedDataUsesExplicitScheduledFallback()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var clock = new Clock(Now);
        var router = Router(net, clock);
        router.ApplyFeed(Feed(Delay("t", 0, 30)));
        var invalid = Feed(Delay("t", 0, 60));
        invalid.Header.Incrementality = FeedHeader.Types.Incrementality.Differential;
        Assert.False(router.ApplyFeed(invalid).Accepted);
        Assert.Equal(230, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        clock.Now = Now.AddMinutes(6);
        var result = router.RouteDetailed(Query());
        Assert.False(result.UsedRealtime);
        Assert.NotNull(result.FallbackReason);
        Assert.Equal(200, result.Routing.BestJourney!.ArrivalTime);
        Assert.False(router.ApplyFeed(Feed()).Accepted);
    }

    [Fact]
    public void WrongServiceDateAndUnknownFeedCannotOverrideCurrentView()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        router.ApplyFeed(Feed(Delay("t", 0, 30)));
        var wrongDate = Delay("t", 0, 60);
        wrongDate.Trip.StartDate = "20261019";
        Assert.False(router.ApplyFeed(Feed(wrongDate)).Accepted);
        Assert.False(router.ApplyFeed(Feed(Delay("other", 0, 100))).Accepted);
        Assert.Equal(230, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void StaleEntityIsNotMadeFreshByFeedHeader()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        var update = Delay("t", 0, 100);
        update.Timestamp = (ulong)Now.AddMinutes(-10).ToUnixTimeSeconds();
        var report = router.ApplyFeed(Feed(update));
        Assert.True(report.Accepted);
        Assert.Contains(report.Diagnostics, d => d.Code == "StaleTrip");
        Assert.Equal(200, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void InvalidChronologyExcludesTripAndDoesNotSilentlyClampTimes()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        var report = router.ApplyFeed(Feed(Delay("t", 1, -150)));
        Assert.Equal(1, report.ExcludedTrips);
        Assert.Contains(report.Diagnostics, d => d.Code == "InvalidTripUpdate");
        Assert.False(router.RouteDetailed(Query()).Routing.IsReachable);
    }

    [Fact]
    public void RepeatedStopUsesSequenceAndAmbiguousStopIdIsRejected()
    {
        var net = Network(Trip("loop", ("A", 100), ("B", 200), ("A", 300), ("C", 400)));
        var router = Router(net);
        Assert.Equal(400, router.RouteDetailed(Query(departure: 250)).Routing.BestJourney!.ArrivalTime);
        Assert.Equal(1, router.ApplyFeed(Feed(Delay("loop", 2, 20))).AppliedTrips);
        Assert.Equal(100, router.GetEffectiveStopTimes("loop")![0].Arrival);
        Assert.Equal(320, router.GetEffectiveStopTimes("loop")![2].Arrival);
        var ambiguous = Update("loop", new StopTimeUpdate { StopId = "A", Arrival = new() { Delay = 20 } });
        Assert.Equal(1, router.ApplyFeed(Feed(ambiguous)).ExcludedTrips);
    }

    [Fact]
    public void WalkingBeforeAndAfterRideAndWalkingOnlyAreSupported()
    {
        var net = Network(Trip("t", ("A", 100), ("B", 200)), Trip("unused", ("S", 1000), ("C", 1100)));
        var graph = Paths(net, ("S", "A", 5), ("B", "C", 7));
        var router = new RealtimeRaptorAlgorithm(net, graph, time: new Clock(Now));
        var result = router.RouteDetailed(Query(from: "S", departure: 90)).Routing.BestJourney!;
        Assert.Equal(207, result.ArrivalTime);
        Assert.Equal(0, result.NumberOfTransfers);
        Assert.Equal(3, result.Legs.Count);
        Assert.Equal(95, router.RouteDetailed(Query(from: "S", to: "A", departure: 90, maxTransfers: 0)).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void QueryKeepsItsCapturedViewWhenUpdateIsPublishedDuringSearch()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        RealtimeRaptorAlgorithm? router = null;
        var graph = new CallbackGraph(Paths(net), () => router!.ApplyFeed(Feed(Delay("t", 0, 50))));
        router = new RealtimeRaptorAlgorithm(net, graph, time: new Clock(Now));
        Assert.Equal(200, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.Equal(250, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public void RandomSmallNetworksMatchIndependentBoardingEdgeOracle()
    {
        var random = new Random(14513);
        for (var sample = 0; sample < 100; sample++)
        {
            var trips = Enumerable.Range(0, 8).Select(index =>
            {
                var stops = new[] { "A", "B", "C", "D" }.OrderBy(_ => random.Next()).ToArray();
                var t = random.Next(50, 150);
                return Trip($"t{index}", stops.Select(s => { t += random.Next(1, 100); return (s, t); }).ToArray());
            }).ToArray();
            var net = Network(trips);
            var router = Router(net);
            var updates = trips.Select(t => Delay(t.Id, 0, random.Next(-20, 80))).ToArray();
            Assert.True(router.ApplyFeed(Feed(updates)).Accepted);
            var query = Query(departure: random.Next(0, 150), maxTransfers: 2, pareto: true);
            var expected = Oracle(router, net, query);
            var actual = router.RouteDetailed(query).Routing.ParetoJourneys.Journeys.Select(j => (j.ArrivalTime, j.NumberOfTransfers)).ToArray();
            Assert.True(expected.SequenceEqual(actual), $"Random sample {sample}: expected {string.Join(';', expected)}, actual {string.Join(';', actual)}");
        }
    }

    // Independent time-dependent graph: an edge boards one trip and exits at ANY later stop.
    // No route grouping, marking, route scanning or parent logic is shared with RAPTOR.
    private static (int, int)[] Oracle(RealtimeRaptorAlgorithm router, TransitNetwork net, RoutingQuery query, IFootpathGraph? walking = null)
    {
        var best = new Dictionary<(string Stop, int Rides), int> { [(query.SourceStopId, 0)] = query.DepartureTime };
        var queue = new PriorityQueue<(string Stop, int Rides), int>();
        queue.Enqueue((query.SourceStopId, 0), query.DepartureTime);
        var trips = net.Trips.Keys.Select(router.GetEffectiveStopTimes).Where(t => t is not null).ToArray();
        while (queue.TryDequeue(out var state, out var time))
        {
            if (best[state] != time) continue;
            foreach (var path in walking?.GetOutgoingFootpaths(state.Stop) ?? [])
            {
                var next = (path.ToStopId, state.Rides);
                var arrival = time + path.DurationSeconds;
                if (best.TryGetValue(next, out var old) && old <= arrival) continue;
                best[next] = arrival;
                queue.Enqueue(next, arrival);
            }
            if (state.Rides == query.MaxTransfers + 1) continue;
            foreach (var trip in trips)
                for (var i = 0; i < trip!.Count; i++)
                {
                    if (trip[i].StopId != state.Stop || !trip[i].CanBoard || trip[i].Departure < time) continue;
                    for (var j = i + 1; j < trip.Count; j++)
                    {
                        if (!trip[j].CanAlight) continue;
                        var next = (trip[j].StopId, state.Rides + 1);
                        if (best.TryGetValue(next, out var old) && old <= trip[j].Arrival) continue;
                        best[next] = trip[j].Arrival;
                        queue.Enqueue(next, trip[j].Arrival);
                    }
                }
        }
        var candidates = best.Where(p => p.Key.Stop == query.TargetStopId)
            .Select(p => (Time: p.Value, Transfers: Math.Max(0, p.Key.Rides - 1))).Distinct().ToArray();
        return candidates.Where(c => !candidates.Any(o => o.Time <= c.Time && o.Transfers <= c.Transfers && o != c))
            .OrderBy(c => c.Time).Select(c => (c.Time, c.Transfers)).ToArray();
    }

    private static RealtimeRaptorAlgorithm Router(TransitNetwork net, Clock? time = null) => new(net, Paths(net), time: time ?? new Clock(Now));
    private static RoutingQuery Query(string from = "A", string to = "C", int departure = 0, int maxTransfers = 3, bool pareto = false) =>
        new() { SourceStopId = from, TargetStopId = to, DepartureTime = departure, MaxTransfers = maxTransfers, MultiCriteria = pareto };
    private static FeedMessage Feed(params TripUpdate[] updates)
    {
        var result = new FeedMessage { Header = new() { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)Now.ToUnixTimeSeconds() } };
        for (var i = 0; i < updates.Length; i++) result.Entity.Add(new FeedEntity { Id = i.ToString(), TripUpdate = updates[i] });
        return result;
    }
    private static TripUpdate Delay(string trip, uint sequence, int delay) => Update(trip,
        new StopTimeUpdate { StopSequence = sequence, Arrival = new() { Delay = delay } });
    private static TripUpdate Update(string trip, params StopTimeUpdate[] changes)
    {
        var result = new TripUpdate { Trip = new() { TripId = trip } };
        result.StopTimeUpdate.Add(changes);
        return result;
    }
    private static Trip Trip(string id, params (string Stop, int Time)[] stops) => new()
    {
        Id = id,
        RouteId = "r",
        ServiceId = "service",
        StopTimes = stops.Select((s, i) => new StopTime
        {
            TripId = id,
            StopId = s.Stop,
            StopSequence = i,
            ArrivalTime = s.Time,
            DepartureTime = s.Time
        }).ToArray()
    };
    private static TransitNetwork Network(params Trip[] trips)
    {
        var stops = trips.SelectMany(t => t.StopTimes).Select(s => s.StopId).Distinct().ToDictionary(id => id,
            id => new Stop { Id = id, Name = id, Latitude = 50, Longitude = 19 });
        return new TransitNetwork
        {
            Stops = stops,
            Trips = trips.ToDictionary(t => t.Id),
            ServiceDate = Date,
            Routes = new Dictionary<string, Route> { ["r"] = new() { Id = "r", ShortName = "r", RouteType = RouteType.Bus } },
            Connections = [],
            ConnectionDict = new Dictionary<string, Connection>(),

        };
    }
    private static FootpathGraph Paths(TransitNetwork net, params (string From, string To, int Duration)[] paths)
    {
        var edges = paths.Select(p => new Footpath { FromStopId = p.From, ToStopId = p.To, DurationSeconds = p.Duration }).ToArray();
        return new()
        {
            Nodes = net.Stops.Keys.ToDictionary(id => id, id => new Node { StopId = id }),
            Edges = edges,
            OutgoingEdges = edges.GroupBy(e => e.FromStopId).ToDictionary(g => g.Key, g => g.ToHashSet()),
            IncomingEdges = edges.GroupBy(e => e.ToStopId).ToDictionary(g => g.Key, g => g.ToHashSet())
        };
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CallbackGraph(IFootpathGraph inner, Action callback) : IFootpathGraph
    {
        private bool done;
        public bool TryGetDuration(string fromStopId, string toStopId, out int durationSeconds) => inner.TryGetDuration(fromStopId, toStopId, out durationSeconds);
        public IEnumerable<Footpath> GetIncomingFootpaths(string stopId) => inner.GetIncomingFootpaths(stopId);
        public IEnumerable<Footpath> GetOutgoingFootpaths(string stopId)
        { if (!done) { done = true; callback(); } return inner.GetOutgoingFootpaths(stopId); }
    }
}
