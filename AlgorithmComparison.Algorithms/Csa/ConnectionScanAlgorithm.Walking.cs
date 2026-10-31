using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Walking;

namespace AlgorithmComparison.Algorithms.Csa;

public sealed record CsaWalkingOrigin(Connection? EnterConnection, Connection? ExitConnection);

public partial class ConnectionScanAlgorithm
{
    public WalkingPropagationResult<CsaWalkingOrigin> PropagateWalkingWithService(CsaInput input,
        WalkingTransferService service, IEnumerable<WalkingSeed<CsaWalkingOrigin>> sources,
        CancellationToken cancellation = default, bool validateGraph = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(sources);
        if (validateGraph && service.StopIds.Any(id => !input.S.ContainsKey(id) || !input.J.ContainsKey(id)))
            throw new ArgumentException("Walking graph does not match initialized CSA state.", nameof(service));
        var seeds = sources.ToArray();
        foreach (var seed in seeds)
        {
            var origin = seed.Context ?? throw new ArgumentException("Missing walking context.", nameof(sources));
            if ((origin.EnterConnection is null) != (origin.ExitConnection is null))
                throw new ArgumentException("Both vehicle pointers must be supplied together.", nameof(sources));
            if (origin.ExitConnection is { } exit && (exit.ArrivalStopId != seed.StopId
                || exit.ArrivalTime != seed.Time || origin.EnterConnection!.TripId != exit.TripId))
                throw new ArgumentException("Seed must match the preceding vehicle arrival.", nameof(sources));
        }
        var result = service.PropagateTimes(seeds, cancellation: cancellation, accept: walked =>
        {
            if (walked.Time >= input.S[walked.StopId]) return !walked.HasWalk;
            input.S[walked.StopId] = walked.Time;
            var origin = walked.Origin.Context;
            if (origin.ExitConnection is null)
                input.J[walked.StopId] = (null!, null!, null!);
            else
                input.J[walked.StopId] = (origin.EnterConnection!, origin.ExitConnection,
                    new Footpath
                    {
                        FromStopId = walked.Origin.StopId,
                        ToStopId = walked.StopId,
                        DurationSeconds = checked(walked.Time - walked.Origin.Time)
                    });
            return true;
        });
        return result;
    }
    private static WalkingPropagationResult<string> PropagateWalkingProfilesWithService(
        WalkingTransferService service, Dictionary<string, List<ParetoProfileEntry>> profiles,
        string departureStop, int departureTime, int[] arrivals, ParetoPointer?[] pointers,
        CancellationToken cancellation = default)
    {
        if (arrivals.Length != pointers.Length)
            throw new ArgumentException("Inconsistent profile vectors.");
        var result = service.PropagateTimes(new[] { new WalkingSeed<string>(departureStop, departureTime, 0, departureStop) },
            WalkingDirection.BackwardLatestDeparture, cancellation, accept: walked =>
        {
            var profile = profiles[walked.StopId];
            if (!Improves(arrivals, EvaluateProfile(profile, walked.Time).ArrivalByMaxLegs)) return false;
            var initial = walked.StopId == departureStop ? null : new Footpath
            {
                FromStopId = walked.StopId,
                ToStopId = departureStop,
                DurationSeconds = checked(departureTime - walked.Time)
            };
            InsertIntoProfile(profile, walked.Time, arrivals, pointers, initial);
            return true;
        });
        return result;
    }
    private static Dictionary<string, (int Duration, Footpath? Path)> PrepareWalkingToTargetWithService(
        WalkingTransferService service, string targetStop, CancellationToken cancellation = default)
    {
        var reached = service.PropagateTimes(new Dictionary<string, int> { [targetStop] = 0 },
            WalkingDirection.BackwardLatestDeparture, cancellation);
        return service.StopIds.ToDictionary(id => id, id => !reached.TimesByStop.TryGetValue(id, out var time)
            ? (Infinity, (Footpath?)null)
            : (checked(-time), id == targetStop ? null : new Footpath
            { FromStopId = id, ToStopId = targetStop, DurationSeconds = checked(-time) }));
    }
}
