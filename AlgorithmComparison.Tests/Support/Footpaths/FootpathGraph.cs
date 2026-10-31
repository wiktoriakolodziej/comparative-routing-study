using AlgorithmComparison.Core.Contracts;

namespace AlgorithmComparison.Core.Models;

public class FootpathGraph : IFootpathGraph
{
    public required IReadOnlyDictionary<string, Node> Nodes { get; init; }
    public required IReadOnlyList<Footpath> Edges { get; init; }
    public required IReadOnlyDictionary<string, HashSet<Footpath>> OutgoingEdges { get; init; }
    public required IReadOnlyDictionary<string, HashSet<Footpath>> IncomingEdges { get; init; }

    public bool TryGetDuration(string fromStopId, string toStopId, out int durationSeconds)
    {
        durationSeconds = 0;
        if (!Nodes.ContainsKey(fromStopId) || !Nodes.ContainsKey(toStopId))
            return false;

        if (fromStopId == toStopId)
            return true;

        if (OutgoingEdges.TryGetValue(fromStopId, out var footpaths))
        {
            foreach (var footpath in footpaths)
            {
                if (footpath.ToStopId != toStopId) continue;
                durationSeconds = footpath.DurationSeconds;
                return true;
            }
        }

        return false;
    }

    public IEnumerable<Footpath> GetOutgoingFootpaths(string stopId)
    {
        if (!Nodes.ContainsKey(stopId)) yield break;

        yield return new Footpath { FromStopId = stopId, ToStopId = stopId, DurationSeconds = 0 };
        if (OutgoingEdges.TryGetValue(stopId, out var footpaths))
        {
            foreach (var footpath in footpaths)
                if (footpath.ToStopId != stopId) yield return footpath;
        }
    }

    public IEnumerable<Footpath> GetIncomingFootpaths(string stopId)
    {
        if (!Nodes.ContainsKey(stopId)) yield break;

        yield return new Footpath { FromStopId = stopId, ToStopId = stopId, DurationSeconds = 0 };
        if (IncomingEdges.TryGetValue(stopId, out var footpaths))
        {
            foreach (var footpath in footpaths)
                if (footpath.FromStopId != stopId) yield return footpath;
        }
    }
}
public sealed class Node
{
    public required string StopId { get; init; }

}
