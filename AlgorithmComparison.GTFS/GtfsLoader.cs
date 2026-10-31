using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.GTFS;

public sealed class GtfsLoader : INetworkLoader
{
    public TransitNetwork Load(string path, DateOnly serviceDate)
    {
        var rawStops = Parse(GtfsParser.ParseStops, Path.Combine(path, "stops.txt"));
        var rawRoutes = Parse(GtfsParser.ParseRoutes, Path.Combine(path, "routes.txt"));
        var rawTrips = Parse(GtfsParser.ParseTrips, Path.Combine(path, "trips.txt"));
        var rawStopTimes = Parse(GtfsParser.ParseStopTimes, Path.Combine(path, "stop_times.txt"));

        var activeServiceIds = ResolveCalendar(path, serviceDate);

        var stops = rawStops.Select(s => new Stop
        {
            Id = s.StopId,
            Name = s.StopName,
            Latitude = s.StopLat,
            Longitude = s.StopLon,
            ParentStopId = s.ParentStation,
        }).ToList();

        return NetworkBuilder.Build(stops, rawRoutes, rawTrips, rawStopTimes,
                                    activeServiceIds, serviceDate);
    }

    private static IReadOnlySet<string> ResolveCalendar(string path, DateOnly date)
    {
        string calPath = Path.Combine(path, "calendar.txt");
        string cdPath = Path.Combine(path, "calendar_dates.txt");
        bool hasCal = File.Exists(calPath);
        bool hasCd = File.Exists(cdPath);

        var calendars = hasCal ? Parse(GtfsParser.ParseCalendar, calPath) : [];
        var calDates = hasCd ? Parse(GtfsParser.ParseCalendarDates, cdPath) : [];

        return calendars.Count > 0
            ? ServiceCalendarResolver.GetActiveServiceIds(calendars, calDates, date)
            : ServiceCalendarResolver.GetActiveServiceIds(calDates, date);
    }

    private static T Parse<T>(Func<TextReader, T> parser, string filePath)
    {
        using var reader = new StreamReader(filePath);
        return parser(reader);
    }
}
