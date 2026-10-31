namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsCalendarDate
{
    public required string ServiceId { get; init; }
    public required DateOnly Date { get; init; }
    public required int ExceptionType { get; init; }
}
