using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Contracts;

public interface IBenchmarkPreparableRoutingAlgorithm : IRoutingAlgorithm
{
    void Prepare(TransitNetwork network);
    TimeSpan PreprocessingTime { get; }
}
