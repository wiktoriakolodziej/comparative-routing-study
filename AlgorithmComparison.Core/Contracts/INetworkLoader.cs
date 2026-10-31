using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Contracts;

public interface INetworkLoader
{
    TransitNetwork Load(string path, DateOnly serviceDate);
}
