namespace AlgorithmComparison.Core.Contracts;

public interface IExtractingPreprocessor<T> : IPreprocessor
{
    T GetValue();
}
