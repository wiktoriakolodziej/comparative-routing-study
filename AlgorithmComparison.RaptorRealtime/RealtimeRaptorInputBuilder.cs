using AlgorithmComparison.Algorithms.Raptor;

namespace AlgorithmComparison.RaptorRealtime;

internal static class RealtimeRaptorInputBuilder
{
    public static RaptorInput Build(PreparedTimetable timetable, IReadOnlyDictionary<string, int> stopIndex)
    {
        var input = new RaptorInput();
        foreach (var (id, index) in stopIndex)
        {
            input.StopIdToIndex.Add(id, index);
            input.StopIndexToId.Add(index, id);
            input.Stops.Add(index, new Stop(index));
            input.StopRoutes.Add(index, []);
        }
        foreach (var trip in timetable.Trips.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var index = input.Trips.Count;
            input.TripIdToIndex.Add(trip.Id, index);
            input.TripIndexToId.Add(index, trip.Id);
            input.Trips.Add(index, new Trip(index));
        }
        for (var routeId = 0; routeId < timetable.Routes.Length; routeId++)
        {
            var route = timetable.Routes[routeId];
            input.Routes.Add(routeId, new Route(routeId, route.Stops.Length, route.Trips.Length));
            input.RouteStops.Add(routeId, route.Stops.ToList());
            input.RouteTrips.Add(routeId, route.Trips.Select(t => input.TripIdToIndex[t.Id]).ToList());
            input.RouteIndexToGtfsRouteId.Add(routeId, route.Trips[0].RouteId);
            input.StopTimes.Add(routeId, route.Trips.ToDictionary(t => input.TripIdToIndex[t.Id],
                t => t.Stops.Select(s => (s.Arrival, s.Departure)).ToList()));
            var permissions = route.Trips[0].Stops.Select(s => (s.CanBoard, s.CanAlight)).ToArray();
            if (permissions.Any(p => !p.CanBoard || !p.CanAlight))
                input.RoutePermissions.Add(routeId, permissions);
            foreach (var stop in route.Stops.Distinct())
                input.StopRoutes[stop].Add(routeId);
        }
        return input;
    }
}
