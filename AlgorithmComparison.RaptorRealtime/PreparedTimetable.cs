using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.RaptorRealtime;

internal sealed record EffectiveTrip(string Id, string RouteId, EffectiveStopTime[] Stops);
internal sealed record RouteGroup(int[] Stops, EffectiveTrip[] Trips);
internal sealed record PreparedTimetable(string Version, DateTimeOffset? ValidUntil,
    IReadOnlyDictionary<string, EffectiveTrip> Trips, RouteGroup[] Routes,
    List<(int Route, int Position)>[] StopRoutes, RealtimeUpdateReport? Report)
{
    public AlgorithmComparison.Algorithms.Raptor.RaptorInput Input { get; init; } = null!;

    public static Dictionary<string, EffectiveTrip> CopyBase(TransitNetwork network)
    {
        var result = new Dictionary<string, EffectiveTrip>(StringComparer.Ordinal);
        foreach (var trip in network.Trips.Values)
        {
            var stops = trip.StopTimes.OrderBy(s => s.StopSequence).Select(s => new EffectiveStopTime(
                s.StopId, s.StopSequence, s.ArrivalTime, s.DepartureTime, true, true,
                TimeOrigin.Scheduled, TimeOrigin.Scheduled)).ToArray();
            Validate(stops);
            if (stops.Any(s => !network.Stops.ContainsKey(s.StopId)))
                throw new InvalidDataException($"Trip {trip.Id} references an unknown stop.");
            result.Add(trip.Id, new(trip.Id, trip.RouteId, stops));
        }
        return result;
    }

    public static void Validate(EffectiveStopTime[] stops)
    {
        if (stops.Length == 0) throw new InvalidDataException("Trip has no stop times.");
        for (var i = 0; i < stops.Length; i++)
        {
            var s = stops[i];
            if (s.Arrival < 0 || s.Departure < s.Arrival || s.Departure == int.MaxValue ||
                (i > 0 && (stops[i - 1].Departure > s.Arrival || stops[i - 1].StopSequence >= s.StopSequence)))
                throw new InvalidDataException("Inconsistent stop sequence or arrival/departure chronology.");
        }
    }

    public static PreparedTimetable Build(Dictionary<string, EffectiveTrip> trips,
        IReadOnlyDictionary<string, int> stopIndex, string version, DateTimeOffset? validUntil,
        RealtimeUpdateReport? report)
    {
        // Equal stop/permission patterns only. Each chain has no overtaking at ANY stop.
        var groups = new List<RouteGroup>();
        var patterns = trips.Values.GroupBy(trip =>
            $"{trip.RouteId.Length}:{trip.RouteId}|" + string.Join('|', trip.Stops.Select(s =>
                $"{s.StopId.Length}:{s.StopId}:{s.CanBoard}:{s.CanAlight}")));
        foreach (var pattern in patterns)
        {
            var chains = new List<List<EffectiveTrip>>();
            foreach (var trip in pattern.OrderBy(t => t.Stops[0].Departure).ThenBy(t => t.Id, StringComparer.Ordinal))
            {
                var chain = chains.FirstOrDefault(c => DoesNotOvertake(c[^1], trip));
                if (chain is null) chains.Add([trip]);
                else chain.Add(trip);
            }
            foreach (var chain in chains)
                groups.Add(new(chain[0].Stops.Select(s => stopIndex[s.StopId]).ToArray(), chain.ToArray()));
        }
        var stopRoutes = Enumerable.Range(0, stopIndex.Count).Select(_ => new List<(int, int)>()).ToArray();
        for (var r = 0; r < groups.Count; r++)
            for (var p = 0; p < groups[r].Stops.Length; p++)
                stopRoutes[groups[r].Stops[p]].Add((r, p)); // Includes repeated visits on loop routes.
        var prepared = new PreparedTimetable(version, validUntil, trips, groups.ToArray(), stopRoutes,
            report is null ? null : report with { RouteGroups = groups.Count });
        return prepared with { Input = RealtimeRaptorInputBuilder.Build(prepared, stopIndex) };
    }

    private static bool DoesNotOvertake(EffectiveTrip earlier, EffectiveTrip later) =>
        earlier.Stops.Zip(later.Stops).All(pair => pair.First.Arrival <= pair.Second.Arrival &&
                                                    pair.First.Departure <= pair.Second.Departure);
}
