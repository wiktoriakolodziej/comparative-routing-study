using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;

namespace AlgorithmComparison.Realtime;

public sealed record RealtimeSnapshotMetadata(
    string FeedName, string SourceUrl, DateTimeOffset DownloadStartedAt,
    DateTimeOffset ReceivedAt, DateTimeOffset GeneratedAt, string? ContentType,
    int EntityCount, int Bytes, string Sha256, string SnapshotDirectory,
    double SourceAgeSecondsAtReceipt, bool WasStaleAtReceipt)
{
    // Freshness is evaluated at use time: a successful HTTP request does not imply fresh data.
    public bool IsStale(DateTimeOffset now, TimeSpan maxAge) => now - GeneratedAt > maxAge;
}
public sealed class RealtimeSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string RootDirectory { get; }

    public RealtimeSnapshotStore(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<RealtimeSnapshotMetadata?> ReadLatestAsync(
        RealtimeFeedKind kind, CancellationToken cancellationToken = default)
    {
        var path = LatestPath(kind);
        if (!File.Exists(path)) return null;
        // FileShare.Delete permits an atomic replacement by the writer on Windows.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<RealtimeSnapshotMetadata>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Latest snapshot metadata is empty.");
    }

    public async Task<RealtimeSnapshotMetadata> SaveAsync(DownloadedRealtimeFeed download,
        TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var name = GzmRealtimeOptions.FeedName(download.Kind);
        // Serializes publication even when two collector processes target the same directory.
        await using var writerLock = new FileStream(Path.Combine(RootDirectory, $".{name}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = await ReadLatestAsync(download.Kind, cancellationToken);
        if (previous is not null && download.GeneratedAt < previous.GeneratedAt)
            throw new InvalidDataException("Source timestamp regressed; the last snapshot is retained.");

        var id = $"{download.ReceivedAt:yyyyMMddTHHmmssfffffffZ}-{name}-{Guid.NewGuid():N}";
        var directory = Path.Combine(RootDirectory, id);
        var pending = directory + ".pending";
        var latestTemp = Path.Combine(RootDirectory, $".{id}.json.tmp");
        var metadata = new RealtimeSnapshotMetadata(name, download.SourceUrl.AbsoluteUri,
            download.DownloadStartedAt, download.ReceivedAt, download.GeneratedAt, download.ContentType,
            download.Feed.Entity.Count, download.Protobuf.Length,
            Convert.ToHexString(SHA256.HashData(download.Protobuf)), id,
            (download.ReceivedAt - download.GeneratedAt).TotalSeconds,
            download.ReceivedAt - download.GeneratedAt > maxAge);
        try
        {
            Directory.CreateDirectory(pending);
            await File.WriteAllBytesAsync(Path.Combine(pending, name + ".pb"), download.Protobuf, cancellationToken);
            // Protobuf's formatter preserves field presence (missing delay is not delay=0).
            using var json = JsonDocument.Parse(JsonFormatter.Default.Format(download.Feed));
            await File.WriteAllTextAsync(Path.Combine(pending, name + ".json"),
                JsonSerializer.Serialize(json.RootElement, JsonOptions), Encoding.UTF8, cancellationToken);
            var manifest = JsonSerializer.Serialize(metadata, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(pending, "metadata.json"), manifest, Encoding.UTF8, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, directory);
            await File.WriteAllTextAsync(latestTemp, manifest, Encoding.UTF8, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(latestTemp, LatestPath(download.Kind), overwrite: true);
            return metadata;
        }
        finally
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            if (File.Exists(latestTemp)) File.Delete(latestTemp);
        }
    }

    private string LatestPath(RealtimeFeedKind kind) =>
        Path.Combine(RootDirectory, $"latest-{GzmRealtimeOptions.FeedName(kind)}.json");
}
