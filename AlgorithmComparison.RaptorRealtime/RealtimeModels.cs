using AlgorithmComparison.Core.Results;

namespace AlgorithmComparison.RaptorRealtime;

public enum TimeOrigin { Scheduled, ReportedDelay, ReportedTime, Propagated, EstimatedArrival, EstimatedDeparture, NoData }

public sealed record EffectiveStopTime(string StopId, int StopSequence, int Arrival, int Departure,
    bool CanBoard, bool CanAlight, TimeOrigin ArrivalOrigin, TimeOrigin DepartureOrigin);

public sealed record RealtimeRaptorOptions
{
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMinutes(2);
    public string TimeZoneId { get; init; } = "Europe/Warsaw";
}

public sealed record UpdateDiagnostic(string Code, string? TripId, string Message);
public sealed record RealtimeUpdateReport(bool Accepted, string Version, DateTimeOffset? GeneratedAt,
    DateTimeOffset? ValidUntil, int AppliedTrips, int CanceledTrips, int ExcludedTrips,
    int RouteGroups, IReadOnlyList<UpdateDiagnostic> Diagnostics)
{
    public RealtimeUpdateMetrics? Metrics { get; init; }
}
public sealed record RealtimeUpdateMetrics(double TotalMs, double RouteGroupsMs, long AllocatedBytes);
public sealed record RealtimeAcquisitionMetrics(double TotalMs, long AllocatedBytes);

public sealed record RealtimeRoutingResult(RoutingResult Routing, bool UsedRealtime, string Version,
    string? FallbackReason, RealtimeUpdateReport? Update, AlgorithmMetrics Metrics);
public static class ServiceDayTime
{
    public static DateTimeOffset Origin(DateOnly date, TimeZoneInfo zone)
    {
        var noon = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(noon, zone)).AddHours(-12);
    }

    public static int ToSeconds(long unixSeconds, DateOnly date, TimeZoneInfo zone) =>
        checked((int)(unixSeconds - Origin(date, zone).ToUnixTimeSeconds()));
}
