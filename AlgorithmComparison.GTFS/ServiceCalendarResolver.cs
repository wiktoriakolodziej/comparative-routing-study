using AlgorithmComparison.GTFS.Raw;

namespace AlgorithmComparison.GTFS;

internal static class ServiceCalendarResolver
{
    public static IReadOnlySet<string> GetActiveServiceIds(
        IReadOnlyList<GtfsCalendarDate> calendarDates,
        DateOnly date)
    {
        var active = new HashSet<string>();
        foreach (var cd in calendarDates)
            if (cd.Date == date && cd.ExceptionType == 1)
                active.Add(cd.ServiceId);
        return active;
    }
    public static IReadOnlySet<string> GetActiveServiceIds(
        IReadOnlyList<GtfsCalendar> calendars,
        IReadOnlyList<GtfsCalendarDate> calendarDates,
        DateOnly date)
    {
        var active = new HashSet<string>();

        // Add services from the regular calendar.
        var weekday = date.DayOfWeek;
        foreach (var cal in calendars)
        {
            if (date >= cal.StartDate && date <= cal.EndDate && cal.RunsOn(weekday))
                active.Add(cal.ServiceId);
        }

        // Apply exceptions: type 1 adds, type 2 removes.
        foreach (var ex in calendarDates)
        {
            if (ex.Date != date) continue;
            if (ex.ExceptionType == 1) active.Add(ex.ServiceId);
            else if (ex.ExceptionType == 2) active.Remove(ex.ServiceId);
        }

        return active;
    }
}
