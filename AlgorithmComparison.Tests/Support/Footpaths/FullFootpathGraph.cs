using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Footpaths;

/// <summary>Allows walking between every pair of stops, without a distance limit.</summary>
internal sealed class FullFootpathGraph : GroupedFootpathGraph
{
    public FullFootpathGraph(IEnumerable<Stop> stops)
        : base(stops, _ => string.Empty)
    {
    }
}
