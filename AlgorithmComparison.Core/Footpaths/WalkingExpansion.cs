using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Footpaths;

public static class WalkingExpansion
{
    public static void ForEach(IFootpathGraph graph, string stop, bool incoming, Action<Footpath> visit)
    {
        foreach (var path in incoming ? graph.GetIncomingFootpaths(stop) : graph.GetOutgoingFootpaths(stop))
            visit(path);
    }

    public static void Run<T>(IFootpathGraph graph, string stop, bool incoming,
        Func<Footpath, T> prepare, Action<T> commit, CancellationToken cancellation = default)
    {
        foreach (var path in incoming ? graph.GetIncomingFootpaths(stop) : graph.GetOutgoingFootpaths(stop))
        {
            cancellation.ThrowIfCancellationRequested();
            commit(prepare(path));
        }
    }

    public static void Run<TItem, TResult>(IReadOnlyList<TItem> items,
        Func<TItem, TResult> prepare, Action<TResult> commit, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            commit(prepare(item));
        }
    }
}
