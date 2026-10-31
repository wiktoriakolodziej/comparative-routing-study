using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;

namespace AlgorithmComparison.Core.Contracts;

public interface IRoutingAlgorithm
{
    string Name { get; }
    RoutingResult Route(RoutingQuery query, TransitNetwork network);
    AlgorithmMetrics? LastMetrics { get; }
}
