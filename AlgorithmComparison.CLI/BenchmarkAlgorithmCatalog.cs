using AlgorithmComparison.Algorithms.Csa;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Algorithms.TimeDependentDijkstra;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;

namespace AlgorithmComparison.CLI;

public sealed record BenchmarkVariant(string Algorithm, string Mode)
{
    public string Id => $"{Algorithm}-{(Mode == "earliest" ? "single" : Mode)}";
    public string Label => $"{BenchmarkAlgorithmCatalog.DisplayName(Algorithm)} - {(Mode == "earliest" ? "single" : "Pareto")}";
}

// One registry supplies the menu, argument validation and worker factories.
// Realtime has a separate command because it additionally needs matching snapshots.
public static class BenchmarkAlgorithmCatalog
{
    private sealed record Definition(string Key, string Name,
        Func<IFootpathGraph, IRoutingAlgorithm> Create);
    private static readonly Definition[] Definitions =
    [
        new("raptor", "RAPTOR Mine", graph => new RaptorAlgorithm(graph)),
        new("csa", "CSA", graph => new ConnectionScanAlgorithm(graph)),
        new("dijkstra", "Time Dependent Dijkstra", graph => new TimeDependentDijkstraAlgorithm(graph))
    ];
    public static IReadOnlyList<BenchmarkVariant> Variants { get; } = Array.AsReadOnly(
        Definitions.SelectMany(d => new[] { new BenchmarkVariant(d.Key, "earliest"), new BenchmarkVariant(d.Key, "pareto") }).ToArray());
    public static string DisplayName(string key) => Definitions.First(d => d.Key == key).Name;
    public static IRoutingAlgorithm Create(string key, IFootpathGraph graph) =>
        (Definitions.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown benchmark algorithm '{key}'.")).Create(graph);
    public static BenchmarkVariant? Find(string id) => Variants.FirstOrDefault(v => v.Id.Equals(
        id.Replace("-earliest", "-single", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase));
}
