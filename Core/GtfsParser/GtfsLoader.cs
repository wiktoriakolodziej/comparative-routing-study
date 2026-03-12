using RouteAnalyzer.Core.Model.GtfsModel;

namespace RouteAnalyzer.Core.GtfsParser;

internal class GtfsLoader
{
    private readonly GtfsFeed feed = new();

    public GtfsFeed LoadGtfsFeed(string gtfsFolderPath)
    {
        var stopsFilePath = Path.Combine(gtfsFolderPath, "stops.txt");
        var tripsFilePath = Path.Combine(gtfsFolderPath, "trips.txt");
        var stopTimesFilePath = Path.Combine(gtfsFolderPath, "stop_times.txt");
        LoadStops(stopsFilePath);
        LoadTrips(tripsFilePath);
        LoadStopTimes(stopTimesFilePath);
        return feed;
    }

    private void LoadStops(string stopsFilePath)
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

    private void LoadTrips(string tripsFilePath)
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

    private void LoadStopTimes(string stopTimesFilePath)
    {
        var lines = File.ReadAllLines(stopTimesFilePath);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            var stopTime = new StopTime
            {
                TripId = int.Parse(parts[0]),
                StopId = int.Parse(parts[3]),
                ArrivalTime = int.Parse(parts[1]),
                DepartureTime = int.Parse(parts[2]),
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
}

