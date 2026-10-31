using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Realtime;
using TransitRealtime;

namespace AlgorithmComparison.RaptorRealtime;

public interface ITripUpdateSource
{
    Task<FeedMessage> ReadAsync(CancellationToken cancellation = default);
}

public sealed record RealtimeSourceSnapshot(string Kind, string? FilePath, string Sha256);
public interface ITripUpdateSourceMetadata
{
    RealtimeSourceSnapshot? LastSnapshot { get; }
}

public sealed class GzmTripUpdateSource(RealtimeCollector collector, RealtimeSnapshotStore store) : ITripUpdateSource, ITripUpdateSourceMetadata
{
    public RealtimeSourceSnapshot? LastSnapshot { get; private set; }
    public async Task<FeedMessage> ReadAsync(CancellationToken cancellation = default)
    {
        LastSnapshot = null;
        var results = await collector.CollectOnceAsync(cancellation);
        var update = results.Single(result => result.Kind == RealtimeFeedKind.TripUpdates);
        if (!update.Success)
            throw new IOException(update.Error);
        var path = Path.Combine(store.RootDirectory, update.Snapshot!.SnapshotDirectory, "tripUpdates.pb");
        var feed = FeedMessage.Parser.ParseFrom(await File.ReadAllBytesAsync(path, cancellation));
        LastSnapshot = new("gzm-api", path, update.Snapshot.Sha256);
        return feed;
    }
}

public sealed class SnapshotTripUpdateSource : ITripUpdateSource
{
    private readonly FeedMessage snapshot;

    public SnapshotTripUpdateSource(FeedMessage snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.snapshot = snapshot.Clone();
    }

    public Task<FeedMessage> ReadAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot.Clone());
    }
}

public sealed record RealtimeSessionCycle(DateTimeOffset EvaluatedAt, RoutingQuery Query,
    string? AcquisitionError, RealtimeUpdateReport? LatestUpdateAttempt, RealtimeRoutingResult Result)
{
    public RealtimeAcquisitionMetrics? AcquisitionMetrics { get; init; }
    public RealtimeSourceSnapshot? SourceSnapshot { get; init; }
    public bool Success => AcquisitionError is null && LatestUpdateAttempt is not { Accepted: false };
}

public sealed class RealtimeRoutingSession : IDisposable
{
    private readonly RealtimeRaptorAlgorithm router;
    private readonly ITripUpdateSource source;
    private readonly TimeSpan pollInterval;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public RealtimeRoutingSession(RealtimeRaptorAlgorithm router, ITripUpdateSource source,
        TimeSpan? pollInterval = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(source);
        this.router = router;
        this.source = source;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        if (this.pollInterval < TimeSpan.FromSeconds(1) || this.pollInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        this.time = time ?? TimeProvider.System;
    }

    public RealtimeRoutingResult Route(RoutingQuery query, CancellationToken cancellation = default)
        => router.RouteDetailed(query, cancellation);

    public async Task<RealtimeSessionCycle> RefreshAndRouteAsync(
        Func<DateTimeOffset, RoutingQuery> queryFactory, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        await refreshGate.WaitAsync(cancellation);
        try
        {
            string? error = null;
            RealtimeUpdateReport? update = null;
            var acquisitionWatch = Stopwatch.StartNew();
            var acquisitionBytes = GC.GetTotalAllocatedBytes(precise: true);
            RealtimeAcquisitionMetrics? acquisitionMetrics = null;
            try
            {
                var feed = await source.ReadAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                acquisitionMetrics = new(acquisitionWatch.Elapsed.TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(precise: true) - acquisitionBytes);
                update = router.ApplyFeed(feed);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException
                or InvalidDataException or UnauthorizedAccessException or JsonException
                or Google.Protobuf.InvalidProtocolBufferException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                acquisitionMetrics ??= new(acquisitionWatch.Elapsed.TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(precise: true) - acquisitionBytes);
            }
            cancellation.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            var query = queryFactory(now);
            return new(now, query, error, update, router.RouteDetailed(query, cancellation)) { AcquisitionMetrics = acquisitionMetrics, SourceSnapshot = (source as ITripUpdateSourceMetadata)?.LastSnapshot };
        }
        finally { refreshGate.Release(); }
    }

    public async IAsyncEnumerable<RealtimeSessionCycle> RunAsync(
        Func<DateTimeOffset, RoutingQuery> queryFactory, int count = 1,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        for (var cycle = 0; cycle < count; cycle++)
        {
            yield return await RefreshAndRouteAsync(queryFactory, cancellation);
            if (cycle + 1 < count)
                await Task.Delay(pollInterval, time, cancellation);
        }
    }

    public void Dispose() => refreshGate.Dispose();
}
