using System.Collections.ObjectModel;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Walking;

public sealed class RadiusFootpathGraph : IFootpathGraph
{
    private const double EarthRadius = 6_371_000;
    public double RadiusMeters { get; }
    public double SpeedMetersPerSecond { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<Footpath>> Outgoing { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<Footpath>> Incoming { get; }

    public RadiusFootpathGraph(IEnumerable<Stop> stops, double radiusMeters = 500,
        double speedMetersPerSecond = 1.11, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (!double.IsFinite(radiusMeters) || radiusMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        if (!double.IsFinite(speedMetersPerSecond) || speedMetersPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(speedMetersPerSecond));
        RadiusMeters = radiusMeters;
        SpeedMetersPerSecond = speedMetersPerSecond;
        var ordered = stops.OrderBy(s => s.Latitude).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var outgoing = new Dictionary<string, List<Footpath>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<Footpath>>(StringComparer.Ordinal);
        foreach (var stop in ordered)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(stop.Id) || !double.IsFinite(stop.Latitude)
                || !double.IsFinite(stop.Longitude) || Math.Abs(stop.Latitude) > 90 || Math.Abs(stop.Longitude) > 180)
                throw new ArgumentException("Stops require an ID and finite WGS84 coordinates.", nameof(stops));
            if (!outgoing.TryAdd(stop.Id, new List<Footpath>()))
                throw new ArgumentException($"Duplicate stop ID: {stop.Id}", nameof(stops));
            incoming.Add(stop.Id, new List<Footpath>());
            Add(stop.Id, stop.Id, 0);
        }

        // Latitude is a safe lower bound on spherical distance. The sweep avoids
        // comparing pairs outside the latitude strip; worst-case cost remains O(V²).
        var latitudeWindow = radiusMeters / EarthRadius * 180 / Math.PI;
        for (var i = 0; i < ordered.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var j = i + 1; j < ordered.Length; j++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (ordered[j].Latitude - ordered[i].Latitude > latitudeWindow) break;
                var distance = Distance(ordered[i], ordered[j]);
                if (distance > radiusMeters) continue;
                var seconds = Math.Ceiling(distance / speedMetersPerSecond);
                if (seconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(speedMetersPerSecond), "Walking duration exceeds Int32.");
                Add(ordered[i].Id, ordered[j].Id, (int)seconds);
                Add(ordered[j].Id, ordered[i].Id, (int)seconds);
            }
        }
        Outgoing = Freeze(outgoing, false);
        Incoming = Freeze(incoming, true);

        void Add(string from, string to, int duration)
        {
            var edge = new Footpath { FromStopId = from, ToStopId = to, DurationSeconds = duration };
            outgoing[from].Add(edge);
            incoming[to].Add(edge);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<Footpath>> Freeze(
        Dictionary<string, List<Footpath>> source, bool incoming) =>
        new ReadOnlyDictionary<string, IReadOnlyList<Footpath>>(source.ToDictionary(p => p.Key,
            p => (IReadOnlyList<Footpath>)Array.AsReadOnly(p.Value.OrderBy(
                edge => incoming ? edge.FromStopId : edge.ToStopId, StringComparer.Ordinal).ToArray()), StringComparer.Ordinal));

    private static double Distance(Stop a, Stop b)
    {
        var lat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var lon = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Sin(lat / 2) * Math.Sin(lat / 2)
            + Math.Cos(a.Latitude * Math.PI / 180) * Math.Cos(b.Latitude * Math.PI / 180)
            * Math.Sin(lon / 2) * Math.Sin(lon / 2);
        h = Math.Clamp(h, 0, 1);
        return EarthRadius * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }

    public IReadOnlyList<Footpath> GetOutgoingBatch(string stopId) =>
        Outgoing.TryGetValue(stopId, out var edges) ? edges : Array.Empty<Footpath>();
    public IReadOnlyList<Footpath> GetIncomingBatch(string stopId) =>
        Incoming.TryGetValue(stopId, out var edges) ? edges : Array.Empty<Footpath>();
    public IEnumerable<Footpath> GetOutgoingFootpaths(string stopId) => GetOutgoingBatch(stopId);
    public IEnumerable<Footpath> GetIncomingFootpaths(string stopId) => GetIncomingBatch(stopId);
    public bool TryGetDuration(string fromStopId, string toStopId, out int durationSeconds)
    {
        foreach (var edge in GetOutgoingBatch(fromStopId))
            if (edge.ToStopId == toStopId) { durationSeconds = edge.DurationSeconds; return true; }
        durationSeconds = 0;
        return false;
    }
}
