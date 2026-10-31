using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Contracts;

public interface IPreprocessor
{
    void Preprocess(TransitNetwork network);
    bool IsPreprocessed { get; }
    TimeSpan PreprocessingTime { get; }
}
