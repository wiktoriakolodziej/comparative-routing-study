using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Contracts;

public interface IFootpathGraph
{
    bool TryGetDuration(string fromStopId, string toStopId, out int durationSeconds);
    IEnumerable<Footpath> GetOutgoingFootpaths(string stopId);
    IEnumerable<Footpath> GetIncomingFootpaths(string stopId);
}
