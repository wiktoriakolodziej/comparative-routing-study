using AlgorithmComparison.Core.Walking;

namespace AlgorithmComparison.Algorithms.Raptor;

public partial class RaptorAlgorithm
{
    public WalkingPropagationResult<int> PropagateWalkingWithService(RaptorInput input,
        WalkingTransferService service, int round, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(service);
        if (round < 0 || input.T.Values.Any(times => round >= times.Length))
            throw new ArgumentOutOfRangeException(nameof(round));
        if (service.StopIds.Any(id => !input.StopIdToIndex.TryGetValue(id, out var index)
            || !input.T.ContainsKey(index) || !input.TStar.ContainsKey(index)))
            throw new ArgumentException("Walking graph does not match initialized round state.", nameof(service));
        // Snapshot ALL finite times in this round. They are competing sources; this
        // preserves valid parent chains when a walk reaches an already known stop.
        var seeds = input.T.Where(p => p.Value[round] != Infinity).Select(p =>
            new WalkingSeed<int>(input.StopIndexToId[p.Key], p.Value[round], round, p.Key)).ToArray();
        var result = service.PropagateTimes(seeds, cancellation: cancellation, accept: walked =>
        {
            if (!walked.HasWalk) return true;
            var stop = input.StopIdToIndex[walked.StopId];
            if (walked.Time >= input.T[stop][round]) return false;
            var last = walked.Step!;
            input.T[stop][round] = walked.Time;
            input.TStar[stop] = Math.Min(input.TStar[stop], walked.Time);
            input.Parents[(stop, round)] = new RaptorParent(RaptorParentKind.Walk,
                input.StopIdToIndex[last.FromStopId], round,
                checked(walked.Time - last.DurationSeconds), walked.Time);
            input.MarkedStops.Add(stop);
            return true;
        });
        return result;
    }
}
