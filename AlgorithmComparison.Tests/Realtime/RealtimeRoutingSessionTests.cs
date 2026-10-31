using System.Net;
using AlgorithmComparison.RaptorRealtime;
using AlgorithmComparison.Realtime;
using Google.Protobuf;
using TransitRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Fact]
    public async Task SessionRefreshesFullSnapshotsAndBuildsQueryAfterAcquisition()
    {
        var clock = new Clock(Now);
        var calls = 0;
        var source = new SessionSource(_ =>
        {
            clock.Now = Now.AddSeconds(++calls);
            return Task.FromResult(calls == 1 ? Feed(Delay("t", 0, 30)) : Feed());
        });
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200))), clock), source, time: clock);
        var first = await session.RefreshAndRouteAsync(now => { Assert.Equal(clock.Now, now); return Query(); });
        Assert.True(first.Success);
        Assert.Equal(230, first.Result.Routing.BestJourney!.ArrivalTime);
        var second = await session.RefreshAndRouteAsync(_ => Query());
        Assert.Equal(200, second.Result.Routing.BestJourney!.ArrivalTime);
        Assert.Equal(Now.AddSeconds(2), second.EvaluatedAt);
        Assert.Equal(2, calls);
        _ = session.Route(Query());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SessionRetainsFreshViewOnOutageFallsBackAfterExpiryAndRecovers()
    {
        var clock = new Clock(Now);
        var failing = false;
        var source = new SessionSource(_ =>
        {
            if (failing) throw new HttpRequestException("API unavailable");
            var feed = Feed(Delay("t", 0, 40));
            feed.Header.Timestamp = (ulong)clock.Now.ToUnixTimeSeconds();
            return Task.FromResult(feed);
        });
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200))), clock), source, time: clock);
        var accepted = await session.RefreshAndRouteAsync(_ => Query());
        failing = true;
        var outage = await session.RefreshAndRouteAsync(_ => Query());
        Assert.False(outage.Success);
        Assert.Contains("API unavailable", outage.AcquisitionError);
        Assert.Null(outage.LatestUpdateAttempt);
        Assert.Equal(accepted.Result.Version, outage.Result.Version);
        Assert.Equal(240, outage.Result.Routing.BestJourney!.ArrivalTime);
        clock.Now = Now.AddMinutes(6);
        var expired = await session.RefreshAndRouteAsync(_ => Query());
        Assert.False(expired.Result.UsedRealtime);
        Assert.NotNull(expired.Result.FallbackReason);
        Assert.Equal(200, expired.Result.Routing.BestJourney!.ArrivalTime);
        failing = false;
        var recovered = await session.RefreshAndRouteAsync(_ => Query());
        Assert.True(recovered.Success);
        Assert.True(recovered.Result.UsedRealtime);
        Assert.Equal(240, recovered.Result.Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public async Task RejectedUpdateIsReportedSeparatelyFromRetainedView()
    {
        var bad = Feed();
        bad.Header.Incrementality = FeedHeader.Types.Incrementality.Differential;
        var feeds = new Queue<FeedMessage>(new[] { Feed(Delay("t", 0, 10)), bad });
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200)))),
            new SessionSource(_ => Task.FromResult(feeds.Dequeue())));
        var first = await session.RefreshAndRouteAsync(_ => Query());
        var second = await session.RefreshAndRouteAsync(_ => Query());
        Assert.False(second.Success);
        Assert.False(second.LatestUpdateAttempt!.Accepted);
        Assert.Equal(first.Result.Version, second.Result.Version);
        Assert.Equal(210, second.Result.Routing.BestJourney!.ArrivalTime);
    }

    [Fact]
    public async Task SessionCancellationStopsBeforeFetchAndBetweenCycles()
    {
        var calls = 0;
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200)))),
            new SessionSource(_ => { calls++; return Task.FromResult(Feed()); }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RefreshAndRouteAsync(_ => Query(), cancellation.Token));
        Assert.Equal(0, calls);
        using var between = new CancellationTokenSource();
        await using var cycles = session.RunAsync(_ => Query(), 2, between.Token).GetAsyncEnumerator();
        Assert.True(await cycles.MoveNextAsync());
        between.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cycles.MoveNextAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SessionSerializesRefreshesWhileAllowingReadOnlyQueries()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200)))),
            new SessionSource(async token =>
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
                return Feed();
            }));
        var first = session.RefreshAndRouteAsync(_ => Query());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = session.RefreshAndRouteAsync(_ => Query());
        Assert.Equal(1, calls);
        Assert.Equal(200, session.Route(Query()).Routing.BestJourney!.ArrivalTime);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SingleCycleDoesNotWaitAndSnapshotSourceReturnsIsolatedCopies()
    {
        var feed = Feed(Delay("t", 0, 20));
        var source = new SnapshotTripUpdateSource(feed);
        feed.Entity.Clear();
        var copy = await source.ReadAsync();
        Assert.Single(copy.Entity);
        copy.Entity.Clear();
        using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200)))), source, TimeSpan.FromDays(1));
        var count = 0;
        await foreach (var cycle in session.RunAsync(_ => Query()))
        {
            count++;
            Assert.Equal(220, cycle.Result.Routing.BestJourney!.ArrivalTime);
        }
        Assert.Equal(1, count);
        await using var invalid = session.RunAsync(_ => Query(), 0).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await invalid.MoveNextAsync());
    }

    [Fact]
    public async Task GzmSessionSourceUsesCollectorArchivesFeedAndRoutesIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "raptor-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(Now);
            var feed = Feed(Delay("t", 0, 50));
            using var handler = new SessionHttpHandler(feed.ToByteArray());
            using var http = new HttpClient(handler);
            var options = new GzmRealtimeOptions { IncludeVehiclePositions = false };
            var store = new RealtimeSnapshotStore(folder);
            var collector = new RealtimeCollector(new GzmRealtimeClient(http, options, clock), store, options, clock);
            using var session = new RealtimeRoutingSession(Router(Network(Trip("t", ("A", 100), ("C", 200)))),
                new GzmTripUpdateSource(collector, store), time: clock);
            var result = await session.RefreshAndRouteAsync(_ => Query());
            Assert.True(result.Success);
            Assert.Equal(250, result.Result.Routing.BestJourney!.ArrivalTime);
            Assert.Single(Directory.GetFiles(folder, "tripUpdates.pb", SearchOption.AllDirectories));
            Assert.Equal("/gtfsrt/gzm/tripUpdates", handler.RequestPath);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class SessionSource(Func<CancellationToken, Task<FeedMessage>> read) : ITripUpdateSource
    {
        public Task<FeedMessage> ReadAsync(CancellationToken cancellation = default) => read(cancellation);
    }

    private sealed class SessionHttpHandler(byte[] bytes) : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
