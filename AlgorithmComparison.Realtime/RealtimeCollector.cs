using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AlgorithmComparison.Realtime;

public sealed record RealtimeCollectionResult(RealtimeFeedKind Kind, RealtimeSnapshotMetadata? Snapshot, string? Error)
{
    public bool Success => Error is null;
}

public sealed class RealtimeCollector
{
    private readonly GzmRealtimeClient client;
    private readonly RealtimeSnapshotStore store;
    private readonly GzmRealtimeOptions options;
    private readonly TimeProvider time;

    public RealtimeCollector(GzmRealtimeClient client, RealtimeSnapshotStore store,
        GzmRealtimeOptions options, TimeProvider? time = null)
    {
        options.Validate();
        this.client = client;
        this.store = store;
        this.options = options;
        this.time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RealtimeCollectionResult>> CollectOnceAsync(CancellationToken cancellationToken = default)
    {
        var kinds = options.IncludeVehiclePositions
            ? new[] { RealtimeFeedKind.TripUpdates, RealtimeFeedKind.VehiclePositions }
            : new[] { RealtimeFeedKind.TripUpdates };
        var results = new List<RealtimeCollectionResult>();
        foreach (var kind in kinds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var download = await client.DownloadAsync(kind, cancellationToken);
                var snapshot = await store.SaveAsync(download, options.MaxFeedAge, cancellationToken);
                results.Add(new(kind, snapshot, null));
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or JsonException)
            {
                // Do not let a vehicle position outage discard a successful trip update.
                results.Add(new(kind, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        return results;
    }

    public async IAsyncEnumerable<IReadOnlyList<RealtimeCollectionResult>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            yield return await CollectOnceAsync(cancellationToken);
            // Fixed delay after a batch: no overlapping downloads or catch-up request bursts.
            await Task.Delay(options.PollInterval, time, cancellationToken);
        }
    }
}
