using AlgorithmComparison.Core.Models;
using AlgorithmComparison.GTFS.Raw;

namespace AlgorithmComparison.GTFS;

internal static class NetworkBuilder
{
    public static TransitNetwork Build(
        IReadOnlyList<Stop> stops,
        IReadOnlyList<GtfsRoute> rawRoutes,
        IReadOnlyList<GtfsTrip> rawTrips,
        IReadOnlyList<GtfsStopTime> rawStopTimes,
        IReadOnlySet<string> activeServiceIds,
        DateOnly serviceDate)
    {
        var stopDict = stops.ToDictionary(s => s.Id);

        // Map route_type integer → domain enum.
        var routeDict = rawRoutes.ToDictionary(
            r => r.RouteId,
            r => new Route
            {
                Id = r.RouteId,
                ShortName = r.RouteShortName,
                LongName = r.RouteLongName,
                RouteType = MapRouteType(r.RouteType),
            });

        // Filter trips to active service IDs.
        var activeTrips = rawTrips
            .Where(t => activeServiceIds.Contains(t.ServiceId))
            .ToDictionary(t => t.TripId);

        // Group stop_times by trip, keeping only active trips.
        var stopTimesByTrip = new Dictionary<string, List<GtfsStopTime>>();
        foreach (var st in rawStopTimes)
        {
            if (!activeTrips.ContainsKey(st.TripId)) continue;
            if (!stopTimesByTrip.TryGetValue(st.TripId, out var list))
                stopTimesByTrip[st.TripId] = list = [];
            list.Add(st);
        }

        var domainTrips = new Dictionary<string, Trip>();
        var connections = new List<Connection>();

        foreach (var (tripId, rawTimes) in stopTimesByTrip)
        {
            if (rawTimes.Count < 2) continue; // single-stop trips have no connections

            rawTimes.Sort((a, b) => a.StopSequence.CompareTo(b.StopSequence));

            // Skip trips that reference stops absent from the stops file.
            if (rawTimes.Any(st => !stopDict.ContainsKey(st.StopId))) continue;

            var stopTimes = rawTimes.Select(st => new StopTime
            {
                TripId = tripId,
                StopId = st.StopId,
                StopSequence = st.StopSequence,
                ArrivalTime = st.ArrivalTime,
                DepartureTime = st.DepartureTime,
            }).ToList();

            var gtfsTrip = activeTrips[tripId];
            domainTrips[tripId] = new Trip
            {
                Id = tripId,
                RouteId = gtfsTrip.RouteId,
                ServiceId = gtfsTrip.ServiceId,
                Headsign = gtfsTrip.TripHeadsign,
                StopTimes = stopTimes,
            };

            // Build one Connection per consecutive stop-time pair.
            for (int i = 0; i < stopTimes.Count - 1; i++)
            {
                connections.Add(new Connection
                {
                    Id = connections.Count.ToString(),
                    DepartureStopId = stopTimes[i].StopId,
                    ArrivalStopId = stopTimes[i + 1].StopId,
                    DepartureTime = stopTimes[i].DepartureTime,
                    ArrivalTime = stopTimes[i + 1].ArrivalTime,
                    TripId = tripId,
                    RouteId = gtfsTrip.RouteId,
                });
            }
        }

        // CSA requires connections sorted by departure time.
        connections.Sort((a, b) => a.DepartureTime.CompareTo(b.DepartureTime));

        return new TransitNetwork
        {
            Stops = stopDict,
            Routes = routeDict,
            Trips = domainTrips,
            Connections = connections,
            ConnectionDict = connections.ToDictionary(c => c.Id),
            ServiceDate = serviceDate,
        };
    }

    private static RouteType MapRouteType(int gtfsType) => gtfsType switch
    {
        0 => RouteType.Tram,
        1 => RouteType.Subway,
        2 => RouteType.Rail,
        3 => RouteType.Bus,
        4 => RouteType.Ferry,
        5 => RouteType.CableCar,
        6 => RouteType.Gondola,
        7 => RouteType.Funicular,
        _ => RouteType.Other,  // Extended GTFS types 100-1700 (e.g. 11 = trolleybus)
    };
}
