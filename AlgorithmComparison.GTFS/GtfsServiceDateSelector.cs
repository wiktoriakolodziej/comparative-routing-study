using AlgorithmComparison.GTFS.Raw;

namespace AlgorithmComparison.GTFS;

public static class GtfsServiceDateSelector
{
    public static DateOnly SelectRepresentativeDate(string gtfsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gtfsDirectory);

        var calendarPath = Path.Combine(gtfsDirectory, "calendar.txt");
        var calendarDatesPath = Path.Combine(gtfsDirectory, "calendar_dates.txt");
        var tripsPath = Path.Combine(gtfsDirectory, "trips.txt");

        var calendars = File.Exists(calendarPath)
            ? Parse(GtfsParser.ParseCalendar, calendarPath)
            : Array.Empty<GtfsCalendar>();
        var calendarDates = File.Exists(calendarDatesPath)
            ? Parse(GtfsParser.ParseCalendarDates, calendarDatesPath)
            : Array.Empty<GtfsCalendarDate>();

        if (calendars.Count == 0 && calendarDates.Count == 0)
            throw new InvalidDataException("GTFS feed contains neither calendar.txt nor calendar_dates.txt.");

        var tripServiceIds = Parse(GtfsParser.ParseTrips, tripsPath)
            .Select(trip => trip.ServiceId)
            .ToHashSet(StringComparer.Ordinal);

        var candidateDates = calendarDates
            .Select(exception => exception.Date)
            .ToHashSet();

        if (calendars.Count > 0)
        {
            var firstDate = calendars.Min(calendar => calendar.StartDate);
            var lastDate = calendars.Max(calendar => calendar.EndDate);
            for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
                candidateDates.Add(date);
        }

        var middleDay = candidateDates.Min(date => date.DayNumber)
            + (candidateDates.Max(date => date.DayNumber) - candidateDates.Min(date => date.DayNumber)) / 2;

        foreach (var date in candidateDates
                     .OrderBy(date => Math.Abs(date.DayNumber - middleDay)
                         + WeekdayPreference(date.DayOfWeek)))
        {
            var activeServices = calendars.Count > 0
                ? ServiceCalendarResolver.GetActiveServiceIds(calendars, calendarDates, date)
                : ServiceCalendarResolver.GetActiveServiceIds(calendarDates, date);

            if (activeServices.Overlaps(tripServiceIds))
                return date;
        }

        throw new InvalidDataException("GTFS calendar contains no date with an active service used by trips.");
    }

    private static int WeekdayPreference(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Tuesday => 0,
        DayOfWeek.Wednesday => 1,
        DayOfWeek.Thursday => 2,
        DayOfWeek.Monday => 3,
        DayOfWeek.Friday => 4,
        DayOfWeek.Saturday => 8,
        DayOfWeek.Sunday => 12,
        _ => 12
    };

    private static T Parse<T>(Func<TextReader, T> parser, string filePath)
    {
        using var reader = new StreamReader(filePath);
        return parser(reader);
    }
}
