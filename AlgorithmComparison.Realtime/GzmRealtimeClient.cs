using Google.Protobuf;
using TransitRealtime;

namespace AlgorithmComparison.Realtime;

public sealed record DownloadedRealtimeFeed(
    RealtimeFeedKind Kind, Uri SourceUrl, DateTimeOffset DownloadStartedAt,
    DateTimeOffset ReceivedAt, DateTimeOffset GeneratedAt, string? ContentType,
    byte[] Protobuf, FeedMessage Feed);
public sealed class GzmRealtimeClient
{
    private readonly HttpClient http;
    private readonly GzmRealtimeOptions options;
    private readonly TimeProvider time;

    public GzmRealtimeClient(HttpClient http, GzmRealtimeOptions options, TimeProvider? time = null)
    {
        options.Validate();
        this.http = http;
        this.options = options;
        this.time = time ?? TimeProvider.System;
    }

    public async Task<DownloadedRealtimeFeed> DownloadAsync(
        RealtimeFeedKind kind, CancellationToken cancellationToken = default)
    {
        var url = new Uri(options.BaseUri, GzmRealtimeOptions.FeedName(kind));
        var started = time.GetUtcNow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AlgorithmComparison.Realtime/1.0");
            request.Headers.Accept.ParseAdd("application/x-protobuf, application/octet-stream;q=0.9");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > options.MaxResponseBytes)
                throw new InvalidDataException("GTFS-RT response exceeds the configured size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(block, timeout.Token)) != 0)
            {
                if (buffer.Length + read > options.MaxResponseBytes)
                    throw new InvalidDataException("GTFS-RT response exceeds the configured size limit.");
                buffer.Write(block, 0, read);
            }

            var bytes = buffer.ToArray();
            FeedMessage feed;
            try { feed = FeedMessage.Parser.ParseFrom(bytes); }
            catch (InvalidProtocolBufferException ex)
            { throw new InvalidDataException("Response is not a valid GTFS-RT Protobuf message.", ex); }
            var received = time.GetUtcNow();
            var generated = ValidateFeed(feed, kind, received, options.FutureTimestampTolerance);
            return new(kind, url, started, received, generated,
                response.Content.Headers.ContentType?.ToString(), bytes, feed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GTFS-RT request timed out: {url}", ex);
        }
    }

    private static DateTimeOffset ValidateFeed(FeedMessage feed, RealtimeFeedKind kind,
        DateTimeOffset received, TimeSpan futureTolerance)
    {
        if (feed.Header is not { HasTimestamp: true } header || header.GtfsRealtimeVersion != "2.0")
            throw new InvalidDataException("A GTFS-RT 2.0 header with a source timestamp is required.");
        if (header.Incrementality != FeedHeader.Types.Incrementality.FullDataset)
            throw new InvalidDataException("DIFFERENTIAL feeds are unsupported; the last full snapshot is retained.");
        if (header.Timestamp > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new InvalidDataException("Source timestamp is outside the supported range.");
        var generated = DateTimeOffset.FromUnixTimeSeconds((long)header.Timestamp);
        if (generated > received + futureTolerance)
            throw new InvalidDataException("Source timestamp is too far in the future.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in feed.Entity)
        {
            if (string.IsNullOrWhiteSpace(entity.Id) || !ids.Add(entity.Id) || entity.IsDeleted)
                throw new InvalidDataException("A full feed must contain unique entity IDs and no deletion records.");
            if (kind == RealtimeFeedKind.TripUpdates && entity.TripUpdate?.Trip is null)
                throw new InvalidDataException("Trip updates endpoint returned an entity without a trip update/descriptor.");
            if (kind == RealtimeFeedKind.VehiclePositions && entity.Vehicle is null)
                throw new InvalidDataException("Vehicle positions endpoint returned an entity without a vehicle position.");
        }
        return generated;
    }
}
