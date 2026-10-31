using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Footpaths;

/// <summary>Allows walking only between stops with exactly the same name (ordinal comparison).</summary>
internal sealed class SameNameFootpathGraph : GroupedFootpathGraph
{
    public SameNameFootpathGraph(IEnumerable<Stop> stops)
        : base(stops, stop => stop.Name)
    {
    }
}
