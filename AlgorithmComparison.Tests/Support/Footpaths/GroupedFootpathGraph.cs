using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Footpaths;

/// <summary>
/// Allows walking between every pair of stops in the same group.
/// Stores group membership and computes walking durations on demand.
/// </summary>
internal class GroupedFootpathGraph : IFootpathGraph
{
    private readonly Dictionary<string, Stop> _stops;
    private readonly Dictionary<string, string> _groupByStop;
    private readonly Dictionary<string, Stop[]> _stopsByGroup;

    /// <summary>Uses an explicit group ID for every supplied stop.</summary>
    public GroupedFootpathGraph(
        IEnumerable<Stop> stops,
        IReadOnlyDictionary<string, string> groupByStop)
        : this(stops, stop => groupByStop.TryGetValue(stop.Id, out var groupId)
            ? groupId
            : throw new ArgumentException($"Missing group for stop '{stop.Id}'.", nameof(groupByStop)))
    {
    }

    protected GroupedFootpathGraph(IEnumerable<Stop> stops, Func<Stop, string> groupSelector)
    {
        _stops = stops.ToDictionary(stop => stop.Id, StringComparer.Ordinal);
        _groupByStop = _stops.Values.ToDictionary(stop => stop.Id, groupSelector, StringComparer.Ordinal);
        _stopsByGroup = _stops.Values.GroupBy(stop => _groupByStop[stop.Id], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    public bool TryGetDuration(string fromStopId, string toStopId, out int durationSeconds)
    {
        durationSeconds = 0;
        if (!_stops.TryGetValue(fromStopId, out var from)
            || !_stops.TryGetValue(toStopId, out var to)
            || _groupByStop[fromStopId] != _groupByStop[toStopId])
            return false;

        durationSeconds = WalkingDuration(from, to);
        return true;
    }

    public IEnumerable<Footpath> GetOutgoingFootpaths(string stopId)
    {
        if (!_stops.TryGetValue(stopId, out var from)) yield break;

        foreach (var to in _stopsByGroup[_groupByStop[stopId]])
            yield return CreateFootpath(from, to);
    }

    public IEnumerable<Footpath> GetIncomingFootpaths(string stopId)
    {
        if (!_stops.TryGetValue(stopId, out var to)) yield break;

        foreach (var from in _stopsByGroup[_groupByStop[stopId]])
            yield return CreateFootpath(from, to);
    }

    public IReadOnlyList<Footpath> GetOutgoingBatch(string stopId) =>
        _stops.TryGetValue(stopId, out var stop) ? new Batch(stop, _stopsByGroup[_groupByStop[stopId]], false) : Array.Empty<Footpath>();

    public IReadOnlyList<Footpath> GetIncomingBatch(string stopId) =>
        _stops.TryGetValue(stopId, out var stop) ? new Batch(stop, _stopsByGroup[_groupByStop[stopId]], true) : Array.Empty<Footpath>();

    private sealed class Batch(Stop stop, Stop[] group, bool incoming) : IReadOnlyList<Footpath>
    {
        public int Count => group.Length;
        public Footpath this[int index] => incoming ? CreateFootpath(group[index], stop) : CreateFootpath(stop, group[index]);
        public IEnumerator<Footpath> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static Footpath CreateFootpath(Stop from, Stop to) => new()
    {
        FromStopId = from.Id,
        ToStopId = to.Id,
        DurationSeconds = WalkingDuration(from, to)
    };

    private static int WalkingDuration(Stop from, Stop to)
    {
        if (from.Id == to.Id) return 0;

        // Same Haversine distance, speed and rounding as the existing GTFS footpath generator.
        const double earthRadiusMetres = 6_371_000.0;
        const double walkingSpeedMetresPerSecond = 1.11;
        var dLat = (to.Latitude - from.Latitude) * Math.PI / 180.0;
        var dLon = (to.Longitude - from.Longitude) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(from.Latitude * Math.PI / 180.0) * Math.Cos(to.Latitude * Math.PI / 180.0)
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var distance = earthRadiusMetres * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (int)Math.Ceiling(distance / walkingSpeedMetresPerSecond);
    }

}
