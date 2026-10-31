using AlgorithmComparison.Core.Walking;

namespace AlgorithmComparison.Algorithms.TimeDependentDijkstra;

public partial class TimeDependentDijkstraAlgorithm
{
    public WalkingPropagationResult<Label> PropagateWalkingWithService(TDDijkstraInput input,
        WalkingTransferService service, IEnumerable<Label> sources, int targetStop,
        CancellationToken cancellation = default, bool validateGraph = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(sources);
        if (!input.Graph.OriginalStopIds.ContainsKey(targetStop)) throw new ArgumentException("Unknown target stop.", nameof(targetStop));
        if (validateGraph && service.StopIds.Any(id => !input.Graph.StopIdToIndex.ContainsKey(id)))
            throw new ArgumentException("Walking graph contains stops absent from the algorithm.", nameof(service));
        var seeds = sources.Where(label => label.Active).Select(label => new WalkingSeed<Label>(
            input.Graph.OriginalStopIds[label.StopId], label.ArrivalTime, label.Boardings, label)).ToArray();
        // Even the current single-result Dijkstra uses Pareto labels internally.
        var native = new Dictionary<WalkingLabel<Label>, Label>();
        var result = service.PropagatePareto(seeds, cancellation: cancellation, accept: walked =>
        {
            if (!walked.HasWalk)
            {
                native[walked] = walked.Origin.Context;
                return true;
            }
            var previous = native[walked.Parent!];
            var accepted = TryAddLabel(input, previous, input.Graph.StopIdToIndex[walked.StopId], walked.Time,
                walked.Boardings, null, null, null, targetStop);
            if (accepted is null) return false;
            native[walked] = accepted;
            return true;
        });
        return result; // Accepted walking states, already committed to the native frontier.
    }
}

