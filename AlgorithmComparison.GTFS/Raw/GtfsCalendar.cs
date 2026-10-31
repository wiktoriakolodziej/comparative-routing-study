namespace AlgorithmComparison.GTFS.Raw;

internal sealed class GtfsCalendar
{
    public required string ServiceId { get; init; }

    // Day-of-week flags (1 = service runs, 0 = service does not run).
    public required int Monday { get; init; }
    public required int Tuesday { get; init; }
    public required int Wednesday { get; init; }
    public required int Thursday { get; init; }
    public required int Friday { get; init; }
    public required int Saturday { get; init; }
    public required int Sunday { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public bool RunsOn(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Monday == 1,
        DayOfWeek.Tuesday => Tuesday == 1,
        DayOfWeek.Wednesday => Wednesday == 1,
        DayOfWeek.Thursday => Thursday == 1,
        DayOfWeek.Friday => Friday == 1,
        DayOfWeek.Saturday => Saturday == 1,
        DayOfWeek.Sunday => Sunday == 1,
        _ => false,
    };
}
