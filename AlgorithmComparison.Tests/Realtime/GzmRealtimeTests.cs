using System.Net;
using System.Text.Json;
using AlgorithmComparison.Realtime;
using Google.Protobuf;
using TransitRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed class GzmRealtimeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "algorithmcomparison-rt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GzmRealtimeOptions options = new() { IncludeVehiclePositions = false };

    [Fact]
    public async Task DecodeAndArchivePreserveNegativeZeroAndMissingDelayAndStatuses()
    {
        var feed = TripFeed();
        feed.Entity[0].TripUpdate.StopTimeUpdate.Add(new TripUpdate.Types.StopTimeUpdate
        {
            StopSequence = 8,
            StopId = "stop-8",
            Arrival = new() { Delay = -60 },
            Departure = new() { Delay = 0 },
            ScheduleRelationship = TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship.Skipped
        });
        feed.Entity[0].TripUpdate.StopTimeUpdate.Add(new TripUpdate.Types.StopTimeUpdate
        {
            StopSequence = 9,
            Arrival = new() { Time = Now.ToUnixTimeSeconds() + 600 },
            ScheduleRelationship = TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship.NoData
        });
        feed.Entity[0].TripUpdate.Trip.StartDate = "20261018";
        feed.Entity[0].TripUpdate.Trip.StartTime = "25:05:00";
        feed.Entity[0].TripUpdate.Trip.ScheduleRelationship = TripDescriptor.Types.ScheduleRelationship.Canceled;
        using var http = Http((_, _) => Task.FromResult(Response(feed)));
        var downloaded = await Client(http).DownloadAsync(RealtimeFeedKind.TripUpdates);
        var update = downloaded.Feed.Entity[0].TripUpdate;
        Assert.Equal(-60, update.StopTimeUpdate[0].Arrival.Delay);
        Assert.True(update.StopTimeUpdate[0].Departure.HasDelay);
        Assert.Equal(0, update.StopTimeUpdate[0].Departure.Delay);
        Assert.False(update.StopTimeUpdate[1].Arrival.HasDelay);
        Assert.Equal("20261018", update.Trip.StartDate);
        Assert.Equal("25:05:00", update.Trip.StartTime);
        Assert.Equal(TripDescriptor.Types.ScheduleRelationship.Canceled, update.Trip.ScheduleRelationship);

        var saved = await new RealtimeSnapshotStore(directory).SaveAsync(downloaded, options.MaxFeedAge);
        var folder = Path.Combine(directory, saved.SnapshotDirectory);
        Assert.Equal(feed.ToByteArray(), await File.ReadAllBytesAsync(Path.Combine(folder, "tripUpdates.pb")));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "tripUpdates.json")));
        var stops = json.RootElement.GetProperty("entity")[0].GetProperty("tripUpdate").GetProperty("stopTimeUpdate");
        Assert.Equal(0, stops[0].GetProperty("departure").GetProperty("delay").GetInt32());
        Assert.False(stops[1].GetProperty("arrival").TryGetProperty("delay", out _));
        Assert.Equal(64, saved.Sha256.Length);
    }

    [Theory]
    [InlineData("TripUpdates", "tripUpdates.pb")]
    [InlineData("VehiclePositions", "vehiclePositions.pb")]
    public async Task DecodesArchivedGzmSnapshot(string kindName, string filename)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Realtime", "Fixtures", filename));
        using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(bytes) }));
        var result = await Client(http).DownloadAsync(Enum.Parse<RealtimeFeedKind>(kindName));
        Assert.Equal(672, result.Feed.Entity.Count);
        Assert.Equal("2.0", result.Feed.Header.GtfsRealtimeVersion);
        if (kindName == "TripUpdates")
            Assert.Contains(result.Feed.Entity, entity => entity.TripUpdate.StopTimeUpdate.Any(stop => stop.Arrival is { Delay: < 0 }));
        else
            Assert.All(result.Feed.Entity, entity => Assert.NotNull(entity.Vehicle.Position));
    }

    [Fact]
    public async Task FailedFetchPreservesLatestAndLaterEmptyFullFeedReplacesIt()
    {
        var calls = 0;
        using var http = Http((_, _) => Task.FromResult(++calls switch
        {
            1 => Response(TripFeed()),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Response(EmptyFeed(Now.AddSeconds(1)))
        }));
        var store = new RealtimeSnapshotStore(directory);
        var collector = new RealtimeCollector(Client(http), store, options);
        var first = Assert.Single(await collector.CollectOnceAsync());
        Assert.True(first.Success);
        Assert.False(Assert.Single(await collector.CollectOnceAsync()).Success);
        Assert.Equal(first.Snapshot, await store.ReadLatestAsync(RealtimeFeedKind.TripUpdates));
        Assert.True(Assert.Single(await collector.CollectOnceAsync()).Success);
        var latest = await store.ReadLatestAsync(RealtimeFeedKind.TripUpdates);
        Assert.Equal(0, latest!.EntityCount);
        Assert.NotEqual(first.Snapshot!.SnapshotDirectory, latest.SnapshotDirectory);
    }

    [Fact]
    public async Task VehicleFailureDoesNotDiscardTripUpdates()
    {
        var both = options with { IncludeVehiclePositions = true };
        using var http = Http((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("tripUpdates")
            ? Response(TripFeed()) : new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var collector = new RealtimeCollector(new GzmRealtimeClient(http, both, new FixedTime(Now)),
            new RealtimeSnapshotStore(directory), both);
        var results = await collector.CollectOnceAsync();
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.NotNull(await new RealtimeSnapshotStore(directory).ReadLatestAsync(RealtimeFeedKind.TripUpdates));
        Assert.Null(await new RealtimeSnapshotStore(directory).ReadLatestAsync(RealtimeFeedKind.VehiclePositions));
    }

    [Fact]
    public async Task TimestampRegressionCannotReplaceLatestEvenAfterRestart()
    {
        var calls = 0;
        using var http = Http((_, _) => Task.FromResult(Response(EmptyFeed(++calls == 1 ? Now : Now.AddSeconds(-1)))));
        var firstStore = new RealtimeSnapshotStore(directory);
        var first = await firstStore.SaveAsync(await Client(http).DownloadAsync(RealtimeFeedKind.TripUpdates), options.MaxFeedAge);
        var restartedStore = new RealtimeSnapshotStore(directory);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await restartedStore.SaveAsync(await Client(http).DownloadAsync(RealtimeFeedKind.TripUpdates), options.MaxFeedAge));
        Assert.Equal(first, await restartedStore.ReadLatestAsync(RealtimeFeedKind.TripUpdates));
    }

    [Fact]
    public async Task StalenessUsesSourceTimeAndCanBeReevaluatedLater()
    {
        using var http = Http((_, _) => Task.FromResult(Response(EmptyFeed(Now.AddMinutes(-10)))));
        var downloaded = await Client(http).DownloadAsync(RealtimeFeedKind.TripUpdates);
        var snapshot = await new RealtimeSnapshotStore(directory).SaveAsync(downloaded, options.MaxFeedAge);
        Assert.True(snapshot.WasStaleAtReceipt);
        Assert.Equal(600, snapshot.SourceAgeSecondsAtReceipt);
        Assert.True(snapshot.IsStale(Now.AddMinutes(1), options.MaxFeedAge));
        var originallyFresh = snapshot with { GeneratedAt = Now, WasStaleAtReceipt = false };
        Assert.False(originallyFresh.IsStale(Now, options.MaxFeedAge));
        Assert.True(originallyFresh.IsStale(Now.AddMinutes(6), options.MaxFeedAge));
    }

    [Theory]
    [InlineData("differential")]
    [InlineData("no-header")]
    [InlineData("no-timestamp")]
    [InlineData("wrong-version")]
    [InlineData("future")]
    [InlineData("duplicate-id")]
    [InlineData("wrong-payload")]
    [InlineData("html")]
    public async Task InvalidFeedIsReportedWithoutPublishing(string issue)
    {
        var feed = TripFeed();
        switch (issue)
        {
            case "differential": feed.Header.Incrementality = FeedHeader.Types.Incrementality.Differential; break;
            case "no-header": feed.Header = null; break;
            case "no-timestamp": feed.Header.ClearTimestamp(); break;
            case "wrong-version": feed.Header.GtfsRealtimeVersion = "1.0"; break;
            case "future": feed.Header.Timestamp = (ulong)Now.AddHours(1).ToUnixTimeSeconds(); break;
            case "duplicate-id": feed.Entity.Add(feed.Entity[0].Clone()); break;
            case "wrong-payload": feed.Entity[0].TripUpdate = null; feed.Entity[0].Vehicle = new(); break;
        }
        using var http = Http((_, _) => Task.FromResult(issue == "html"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>server error</html>") }
            : Response(feed)));
        var store = new RealtimeSnapshotStore(directory);
        var result = Assert.Single(await new RealtimeCollector(Client(http), store, options).CollectOnceAsync());
        Assert.False(result.Success);
        Assert.Null(await store.ReadLatestAsync(RealtimeFeedKind.TripUpdates));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var http = Http((_, _) => Task.FromResult(Response(TripFeed())));
        var client = new GzmRealtimeClient(http, options with { MaxResponseBytes = 1 }, new FixedTime(Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(RealtimeFeedKind.TripUpdates));
    }

    [Fact]
    public async Task TimeoutIsReportedButCallerCancellationPropagates()
    {
        using var http = Http(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(EmptyFeed(Now));
        });
        var client = new GzmRealtimeClient(http, options with { RequestTimeout = TimeSpan.FromMilliseconds(30) }, new FixedTime(Now));
        await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(RealtimeFeedKind.TripUpdates));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(RealtimeFeedKind.TripUpdates, cancellation.Token));
    }

    [Fact]
    public async Task WatchRetriesNextCycleAndStopsOnCancellation()
    {
        var calls = 0;
        var watchOptions = options with { PollInterval = TimeSpan.FromSeconds(1) };
        using var http = Http((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(TripFeed())));
        var collector = new RealtimeCollector(new GzmRealtimeClient(http, watchOptions, new FixedTime(Now)),
            new RealtimeSnapshotStore(directory), watchOptions);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = collector.WatchAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.False(Assert.Single(enumerator.Current).Success);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(Assert.Single(enumerator.Current).Success);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal(2, calls);
    }

    private GzmRealtimeClient Client(HttpClient http) => new(http, options, new FixedTime(Now));
    private static FeedMessage EmptyFeed(DateTimeOffset timestamp) => new()
    {
        Header = new FeedHeader { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)timestamp.ToUnixTimeSeconds() }
    };
    private static FeedMessage TripFeed()
    {
        var feed = EmptyFeed(Now);
        feed.Entity.Add(new FeedEntity
        {
            Id = "entity-1",
            TripUpdate = new TripUpdate
            { Trip = new TripDescriptor { TripId = "trip-1" } }
        });
        return feed;
    }
    private static HttpResponseMessage Response(FeedMessage feed) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(feed.ToByteArray()) };
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { Timeout = Timeout.InfiniteTimeSpan };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
