namespace AlgorithmComparison.Realtime;

public enum RealtimeFeedKind { TripUpdates, VehiclePositions }

public sealed record GzmRealtimeOptions
{
    public Uri BaseUri { get; init; } = new("https://gtfsrt.transportgzm.pl:5443/gtfsrt/gzm/");
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaxFeedAge { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FutureTimestampTolerance { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public bool IncludeVehiclePositions { get; init; } = true;

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri || BaseUri.Scheme != Uri.UriSchemeHttps ||
            !BaseUri.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(BaseUri.Query))
            throw new ArgumentException("BaseUri must be an absolute HTTPS URL ending with '/'.");
        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxFeedAge <= TimeSpan.Zero || FutureTimestampTolerance < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxFeedAge));
        if (MaxResponseBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
    }

    public static string FeedName(RealtimeFeedKind kind) => kind switch
    {
        RealtimeFeedKind.TripUpdates => "tripUpdates",
        RealtimeFeedKind.VehiclePositions => "vehiclePositions",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
