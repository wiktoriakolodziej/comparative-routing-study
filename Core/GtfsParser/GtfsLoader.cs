using RouteAnalyzer.Core.Model.GtfsModel;

namespace RouteAnalyzer.Core.GtfsParser;

internal class GtfsLoader
{

    public static GtfsFeed LoadGtfsFeed(string gtfsFolderPath)
    {
        var feed = new GtfsFeed();
        var stopsFilePath = Path.Combine(gtfsFolderPath, "stops.txt");
        var tripsFilePath = Path.Combine(gtfsFolderPath, "trips.txt");
        var stopTimesFilePath = Path.Combine(gtfsFolderPath, "stop_times.txt");
        LoadStops(stopsFilePath, feed);
        LoadTrips(tripsFilePath, feed);
        LoadStopTimes(stopTimesFilePath, feed);
        return feed;
    }

    private static void LoadStops(string stopsFilePath, GtfsFeed feed)
    {
        var lines = File.ReadAllLines(stopsFilePath);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            var stop = new Stop
            {
                Id = int.Parse(parts[0]),
                Name = parts[2],
                Latitude = double.Parse(parts[3]),
                Longitude = double.Parse(parts[4])
            };
            feed.Stops.Add(stop.Id, stop);
        }
    }

    private static void LoadTrips(string tripsFilePath, GtfsFeed feed)
    {
        var lines = File.ReadAllLines(tripsFilePath);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            var trip = new Trip
            {
                Id = int.Parse(parts[2]),
                RouteId = int.Parse(parts[0]),
                ServiceId = int.Parse(parts[1])
            };
            feed.Trips.Add(trip.Id, trip);
        }
    }

    private static void LoadStopTimes(string stopTimesFilePath, GtfsFeed feed)
    {
        var lines = File.ReadAllLines(stopTimesFilePath);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            var stopTime = new StopTime
            {
                TripId = int.Parse(parts[0]),
                StopId = int.Parse(parts[3]),
                ArrivalTime = ParseTimeToSeconds(parts[1]),
                DepartureTime = ParseTimeToSeconds(parts[2]),
                StopSequence = int.Parse(parts[4])
            };
            feed.StopTimes.Add(stopTime);

            feed.StopTimesByStopId[stopTime.StopId] =
                feed.StopTimesByStopId.GetValueOrDefault(stopTime.StopId) ?? [];
            feed.StopTimesByStopId[stopTime.StopId].Add(stopTime);

            feed.StopTimesByTripId[stopTime.TripId] = 
                feed.StopTimesByTripId.GetValueOrDefault(stopTime.TripId) ?? [];
            feed.StopTimesByTripId[stopTime.TripId].Add(stopTime);
        }
    }

    //remember that GTFS times can exceed 24:00:00, e.g. 25:30:00 means 1:30 AM next day
    private static int ParseTimeToSeconds(string timeStr)
    {
        var parts = timeStr.Split(':');
        int hours = int.Parse(parts[0]);
        int minutes = int.Parse(parts[1]);
        int seconds = int.Parse(parts[2]);
        return hours * 3600 + minutes * 60 + seconds;
    }
}

