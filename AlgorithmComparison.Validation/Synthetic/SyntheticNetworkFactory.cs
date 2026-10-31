using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Validation.Synthetic;

public static class SyntheticNetworkFactory
{
    private static readonly DateOnly TestDate = new(2024, 9, 2);

    public static TransitNetwork CreateLinear(int stopCount = 5)
    {
        var stops = Enumerable.Range(0, stopCount)
            .Select(i => new Stop
            {
                Id = $"S{i}",
                Name = $"Stop {i}",
                Latitude = 52.0,
                Longitude = 21.0 + i * 0.01,
            })
            .ToList();

        var route = new Route { Id = "R0", ShortName = "L0", RouteType = RouteType.Bus };

        var trips = new List<Trip>();
        var connections = new List<Connection>();
        int tripIndex = 0;

        // One trip per hour, 06:00 – 22:00.
        for (int hour = 6; hour <= 22; hour++)
        {
            int baseTime = hour * 3600;
            var stopTimes = stops.Select((s, i) => new StopTime
            {
                TripId = $"T{tripIndex}",
                StopId = s.Id,
                StopSequence = i + 1,
                ArrivalTime = baseTime + i * 120,
                DepartureTime = baseTime + i * 120,
            }).ToList();

            trips.Add(new Trip
            {
                Id = $"T{tripIndex}",
                RouteId = "R0",
                ServiceId = "SVC",
                StopTimes = stopTimes,
            });

            for (int i = 0; i < stops.Count - 1; i++)
                connections.Add(new Connection
                {
                    Id = connections.Count.ToString(),
                    DepartureStopId = stops[i].Id,
                    ArrivalStopId = stops[i + 1].Id,
                    DepartureTime = stopTimes[i].DepartureTime,
                    ArrivalTime = stopTimes[i + 1].ArrivalTime,
                    TripId = $"T{tripIndex}",
                    RouteId = "R0",
                });

            tripIndex++;
        }

        return BuildNetwork(stops, [route], trips, connections);
    }

    public static TransitNetwork CreateWithTransfer()
    {
        var stops = new List<Stop>
        {
            new() { Id = "A", Name = "Stop A", Latitude = 52.0, Longitude = 21.00 },
            new() { Id = "B", Name = "Stop B", Latitude = 52.0, Longitude = 21.01 },
            new() { Id = "C", Name = "Stop C", Latitude = 52.0, Longitude = 21.02 },
        };

        var routes = new List<Route>
        {
            new() { Id = "R0", ShortName = "Line1", RouteType = RouteType.Bus },
            new() { Id = "R1", ShortName = "Line2", RouteType = RouteType.Bus },
        };

        // Trip T0: A 08:00 → B 08:10
        var t0 = MakeTrip("T0", "R0", [("A", 8 * 3600), ("B", 8 * 3600 + 600)]);
        // Trip T1: B 08:20 → C 08:30
        var t1 = MakeTrip("T1", "R1", [("B", 8 * 3600 + 1200), ("C", 8 * 3600 + 1800)]);

        var connections = new List<Connection>
        {
            new() { Id = 0.ToString(), DepartureStopId = "A", ArrivalStopId = "B", DepartureTime = 8*3600, ArrivalTime = 8*3600+600, TripId = "T0", RouteId = "R0" },
            new() { Id = 1.ToString(), DepartureStopId = "B", ArrivalStopId = "C", DepartureTime = 8*3600+1200, ArrivalTime = 8*3600+1800, TripId = "T1", RouteId = "R1" },
        };

        return BuildNetwork(stops, routes, [t0, t1], connections);
    }

    private static Trip MakeTrip(string tripId, string routeId, (string stopId, int time)[] stops)
    {
        var stopTimes = stops.Select((s, i) => new StopTime
        {
            TripId = tripId,
            StopId = s.stopId,
            StopSequence = i + 1,
            ArrivalTime = s.time,
            DepartureTime = s.time,
        }).ToList();

        return new Trip { Id = tripId, RouteId = routeId, ServiceId = "SVC", StopTimes = stopTimes };
    }

    private static TransitNetwork BuildNetwork(
        List<Stop> stops,
        List<Route> routes,
        List<Trip> trips,
        List<Connection> connections)
    {
        connections.Sort((a, b) => a.DepartureTime.CompareTo(b.DepartureTime));

        return new TransitNetwork
        {
            Stops = stops.ToDictionary(s => s.Id),
            Routes = routes.ToDictionary(r => r.Id),
            Trips = trips.ToDictionary(t => t.Id),
            Connections = connections,
            ConnectionDict = connections.ToDictionary(c => c.Id),

            ServiceDate = TestDate,

        };
    }
}
