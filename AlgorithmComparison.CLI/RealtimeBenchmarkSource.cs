using AlgorithmComparison.RaptorRealtime;
using System.Security.Cryptography;
using TransitRealtime;

namespace AlgorithmComparison.CLI;

public sealed class RealtimeBenchmarkClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class RealtimeBenchmarkSource(ITripUpdateSource source, RealtimeBenchmarkClock clock, bool replay) : ITripUpdateSource, ITripUpdateSourceMetadata
{
    public RealtimeSourceSnapshot? LastSnapshot => (source as ITripUpdateSourceMetadata)?.LastSnapshot;
    public async Task<FeedMessage> ReadAsync(CancellationToken cancellation = default)
    {
        try
        {
            var feed = await source.ReadAsync(cancellation);
            if (replay)
            {
                if (feed.Header is not { HasTimestamp: true }) throw new InvalidDataException("Replay requires a timestamped feed.");
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(checked((long)feed.Header.Timestamp));
            }
            return feed;
        }
        finally { if (!replay) clock.Now = DateTimeOffset.UtcNow; }
    }
}

public sealed class RealtimeSnapshotSequence(IReadOnlyList<string> paths) : ITripUpdateSource, ITripUpdateSourceMetadata
{
    private int index;
    public RealtimeSourceSnapshot? LastSnapshot { get; private set; }
    public async Task<FeedMessage> ReadAsync(CancellationToken cancellation = default)
    {
        LastSnapshot = null;
        if (index >= paths.Count) throw new InvalidOperationException("No more snapshots in the replay sequence.");
        var path = Path.GetFullPath(paths[index++]);
        var bytes = await File.ReadAllBytesAsync(path, cancellation);
        var feed = FeedMessage.Parser.ParseFrom(bytes);
        LastSnapshot = new("snapshot-file", path, Convert.ToHexString(SHA256.HashData(bytes)));
        return feed;
    }
}
