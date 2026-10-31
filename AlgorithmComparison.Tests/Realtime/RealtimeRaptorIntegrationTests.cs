using System.Net;
using System.Security.Cryptography;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Walking;
using AlgorithmComparison.GTFS;
using AlgorithmComparison.RaptorRealtime;
using AlgorithmComparison.Realtime;
using Google.Protobuf;
using TransitRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Fact]
    public async Task HttpFailureRetainsPredictionUntilExpiryAndRecoveryPublishesNewView()
    {
        var clock = new Clock(Now);
        var router = Router(Network(Trip("t", ("A", 100), ("C", 200))), clock);
        var calls = 0;
        using var http = new HttpClient(new SequenceHandler(() =>
        {
            if (++calls == 2) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var feed = Feed(Delay("t", 0, calls == 1 ? 30 : -10));
            feed.Header.Timestamp = (ulong)clock.Now.ToUnixTimeSeconds();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.ToByteArray()) };
        }));
        var client = new GzmRealtimeClient(http, new() { IncludeVehiclePositions = false }, clock);
        var first = router.ApplyFeed((await client.DownloadAsync(RealtimeFeedKind.TripUpdates)).Feed);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(RealtimeFeedKind.TripUpdates));
        Assert.Equal(first.Version, router.RouteDetailed(Query()).Version);
        Assert.Equal(230, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        clock.Now = Now.AddMinutes(6);
        Assert.False(router.RouteDetailed(Query()).UsedRealtime);
        Assert.Equal(200, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.True(router.ApplyFeed((await client.DownloadAsync(RealtimeFeedKind.TripUpdates)).Feed).Accepted);
        Assert.True(router.RouteDetailed(Query()).UsedRealtime);
        Assert.Equal(190, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ArchivedGzm")]
    public void ArchivedGzmSnapshotsMatchStaticDayAndReplaceViewInSameRouter(bool radiusWalking)
    {
        var root = ArchiveRoot();
        var network = new GtfsLoader().Load(
            Path.Combine(root, "static", "gtfs-20261003"), new DateOnly(2026, 10, 3));
        Assert.Equal(13762, network.Trips.Count);
        IFootpathGraph walking = radiusWalking ? new RadiusFootpathGraph(network.Stops.Values, 500)
            : new SameNameFootpathGraph(network.Stops.Values);
        var clock = new Clock(Now);
        var router = new RealtimeRaptorAlgorithm(network, walking, time: clock);
        RealtimeRoutingResult? previous = null;
        foreach (var (folder, hash, applied, excluded, arrival) in new[]
        {
            ("20261003T151906Z", "4A188F12722F976D101F189A072A893F8AFE1CE971327A8FD681D471925C58F1", 671, 1, 64200),
            ("20261003T152700Z", "A127B8BB345F3376B382371AE0F225027E6DA98CC917764A3CF6FABCA248906D", 663, 2, 64080)
        })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, folder, "tripUpdates.pb"));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)));
            var feed = FeedMessage.Parser.ParseFrom(bytes);
            clock.Now = DateTimeOffset.FromUnixTimeSeconds((long)feed.Header.Timestamp);
            var report = router.ApplyFeed(feed);
            Assert.True(report.Accepted);
            Assert.Equal(applied, report.AppliedTrips);
            Assert.Equal(excluded, report.ExcludedTrips);
            Assert.DoesNotContain(report.Diagnostics, d => d.Code is "UnknownTrip" or "InstanceMismatch");
            var query = Query(from: "1256", to: "8036", departure: 62340, maxTransfers: 7);
            var result = router.RouteDetailed(query);
            Assert.True(result.UsedRealtime);
            var expectedArrival = radiusWalking ? (folder == "20261003T151906Z" ? 64196 : 64020) : arrival;
            Assert.Equal(expectedArrival, result.Routing.BestJourney!.ArrivalTime);
            Assert.Equal("2_6947299", result.Routing.BestJourney.Legs.First(l => !l.IsWalk).TripId);
            var pareto = router.RouteDetailed(Query(from: "1256", to: "8036", departure: 62340, maxTransfers: 7, pareto: true));
            var expectedPareto = radiusWalking && folder == "20261003T152700Z" ? new[] { 64020, 64076 } : new[] { expectedArrival };
            Assert.Equal(expectedPareto, pareto.Routing.ParetoJourneys.Journeys.Select(j => j.ArrivalTime));
            if (radiusWalking) Assert.Contains(result.Routing.BestJourney.Legs, l => l.IsWalk);
            // A newly created instance must produce the same effective timetable as replacement.
            var fresh = new RealtimeRaptorAlgorithm(network, walking, time: clock);
            Assert.True(fresh.ApplyFeed(feed).Accepted);
            foreach (var id in network.Trips.Keys)
                Assert.Equal(fresh.GetEffectiveStopTimes(id)?.ToArray(), router.GetEffectiveStopTimes(id)?.ToArray());
            if (previous is not null)
            {
                Assert.Equal(radiusWalking ? 64196 : 64200, previous.Routing.BestJourney!.ArrivalTime);
                Assert.NotEqual(previous.Version, result.Version);
            }
            previous = result;
        }
    }

    private static string ArchiveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GZM_ARCHIVE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DataRealTime", "Slask");
            if (Directory.Exists(Path.Combine(candidate, "static", "gtfs-20261003"))) return candidate;
        }
        throw new DirectoryNotFoundException("Archived GZM integration test requires DataRealTime/Slask or GZM_ARCHIVE_ROOT. Run Category!=ArchivedGzm for synthetic tests only.");
    }

    private sealed class SequenceHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
}