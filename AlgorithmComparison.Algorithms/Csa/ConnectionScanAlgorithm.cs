namespace AlgorithmComparison.Algorithms.Csa;

using AlgorithmComparison.Algorithms.Common;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using System.Diagnostics;
using AlgorithmComparison.Core.Walking;
public class CsaInput
{
    public Dictionary<string, int> S { get; set; } = new Dictionary<string, int>();
    public Dictionary<string, Connection?> T { get; set; } = new Dictionary<string, Connection?>();
    public Dictionary<string, (Connection? EnterConnection, Connection? ExitConnection, Footpath? Footpath)> J { get; set; } = new Dictionary<string, (Connection? EnterConnection, Connection? ExitConnection, Footpath? Footpath)>();
    public IReadOnlyList<Connection> Connections { get; set; } = new List<Connection>();
}

public class CsaPreprocessor : IExtractingPreprocessor<CsaInput>
{
    private CsaInput csaInput = new CsaInput();
    public bool IsPreprocessed { get; private set; } = false;
    public TimeSpan PreprocessingTime { get; private set; }

    public CsaInput GetValue()
    {
        if (!IsPreprocessed)
            throw new InvalidOperationException("Preprocessing has not been performed yet.");
        return csaInput;
    }

    public void Preprocess(TransitNetwork network)
    {
        if (!IsPreprocessed)
        {
            var stopwatch = Stopwatch.StartNew();
            OneTimePreprocessing(network);
            stopwatch.Stop();
            PreprocessingTime = stopwatch.Elapsed;
            IsPreprocessed = true;
        }

        csaInput.S.Clear();
        csaInput.J.Clear();
        csaInput.T.Clear();

        foreach (var stop in network.Stops)
        {
            csaInput.S[stop.Key] = int.MaxValue;
            csaInput.J[stop.Key] = (null, null, null);
        }
        foreach (var trip in network.Trips)
        {
            csaInput.T[trip.Key] = null;
        }
    }

    private void OneTimePreprocessing(TransitNetwork network)
    {
        csaInput.Connections = network.Connections;
    }
}

public partial class ConnectionScanAlgorithm : AlgorithmBase, IBenchmarkPreparableRoutingAlgorithm
{
    private const int DefaultMaxLegs = 8;
    private const int Infinity = int.MaxValue;

    // Journey chains are immutable and shared between budgets and stop profiles.
    // Initial walking belongs to the profile entry, not to a cloned journey chain.
    private sealed record ParetoPointer(
        Connection EnterConnection,
        Connection ExitConnection,
        Footpath? TransferFootpath,
        Footpath? FinalFootpath,
        ParetoPointer? Next)
    {
        public int LegCount { get; } = 1 + (Next?.LegCount ?? 0);
    }

    private readonly record struct ProfilePointer(ParetoPointer? Journey, Footpath? InitialFootpath);

    // Each entry exclusively owns both arrays. Updates may mutate these arrays;
    // immutable journey nodes can be shared, but arrays must never be shared.
    private sealed record ParetoProfileEntry(
        int DepartureTime,
        int[] ArrivalByMaxLegs,
        ProfilePointer[] Pointers);

    private readonly IFootpathGraph footpathGraph;
    private readonly WalkingTransferService? walkingService;

    private readonly IExtractingPreprocessor<CsaInput> preprocessor;

    public override string Name => "CSA";

    public ConnectionScanAlgorithm(IFootpathGraph footpathGraph)
    {
        ArgumentNullException.ThrowIfNull(footpathGraph);
        this.footpathGraph = footpathGraph;
        walkingService = footpathGraph is RadiusFootpathGraph radius ? new WalkingTransferService(radius) : null;

        this.preprocessor = new CsaPreprocessor();
    }

    public TimeSpan PreprocessingTime => preprocessor.PreprocessingTime;

    public void Prepare(TransitNetwork network) => preprocessor.Preprocess(network);

    protected override RoutingResult RunCore(RoutingQuery query, TransitNetwork network)
    {
        if (query.MultiCriteria)
            return RunPareto(query, network);
        if (query.MaxTransfers.HasValue)
            return RunBoundedSingle(query, network);

        Prepare(network);
        var input = this.preprocessor.GetValue();

        var s = query.SourceStopId;
        var t = query.TargetStopId;
        var departureTime = query.DepartureTime;

        if (walkingService is not null)
            PropagateWalkingWithService(input, walkingService,
                new[] { new WalkingSeed<CsaWalkingOrigin>(s, departureTime, 0, new(null, null)) }, validateGraph: false);
        else
            WalkingExpansion.Run(footpathGraph, s, false, f => (Path: f, Arrival: departureTime + f.DurationSeconds),
                candidate => input.S[candidate.Path.ToStopId] = candidate.Arrival);

        for (var i = FindFirstConnectionAtOrAfter(input.Connections, departureTime); i < input.Connections.Count; i++)
        {
            var c = input.Connections[i];
            if (input.S[t] <= c.DepartureTime)
                break;

            if (input.T[c.TripId] is not null || input.S[c.DepartureStopId] <= c.DepartureTime)
            {
                if (input.T[c.TripId] is null)
                    input.T[c.TripId] = c;

                if (c.ArrivalTime < input.S[c.ArrivalStopId])
                {
                    if (walkingService is not null)
                        PropagateWalkingWithService(input, walkingService, new[] {
                            new WalkingSeed<CsaWalkingOrigin>(c.ArrivalStopId, c.ArrivalTime, 0, new(input.T[c.TripId], c)) }, validateGraph: false);
                    else
                        WalkingExpansion.Run(footpathGraph, c.ArrivalStopId, false, f => (Path: f, Arrival: c.ArrivalTime + f.DurationSeconds), candidate =>
                            {
                                var f = candidate.Path;
                                if (candidate.Arrival < input.S[f.ToStopId])
                                {
                                    input.S[f.ToStopId] = candidate.Arrival;
                                    input.J[f.ToStopId] = new(input.T[c.TripId], c, f);
                                }
                            });
                }
            }
        }
        return ExtractJourney(t, s, departureTime, input);
    }
    private RoutingResult RunPareto(RoutingQuery query, TransitNetwork network)
    {
        var maxLegs = query.MaxTransfers is int maxTransfers
            ? checked(maxTransfers + 1)
            : DefaultMaxLegs;
        if (maxLegs < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "MaxTransfers cannot be negative.");

        var profiles = network.Stops.Keys.ToDictionary(
            stopId => stopId,
            _ => new List<ParetoProfileEntry>
            {
                new(Infinity, FilledVector(maxLegs, Infinity), new ProfilePointer[maxLegs])
            });
        var tripArrivals = network.Trips.Keys.ToDictionary(
            tripId => tripId,
            _ => FilledVector(maxLegs, Infinity));
        var tripPointers = network.Trips.Keys.ToDictionary(
            tripId => tripId,
            _ => new ParetoPointer?[maxLegs]);

        var sourceStopId = query.SourceStopId;
        var targetStopId = query.TargetStopId;
        if (!profiles.ContainsKey(sourceStopId) || !profiles.ContainsKey(targetStopId))
            throw new ArgumentException("The query source and target must exist in the network.", nameof(query));

        Dictionary<string, (int Duration, Footpath? Path)> walkingToTarget;
        if (walkingService is not null)
            walkingToTarget = PrepareWalkingToTargetWithService(walkingService, targetStopId);
        else
        {
            walkingToTarget = network.Stops.Keys.ToDictionary(
                stopId => stopId,
                _ => (Duration: Infinity, Path: (Footpath?)null));
            walkingToTarget[targetStopId] = (0, null);
            WalkingExpansion.Run(footpathGraph, targetStopId, true, f => f, footpath =>
            {
                if (footpath.DurationSeconds < walkingToTarget[footpath.FromStopId].Duration)
                    walkingToTarget[footpath.FromStopId] = (footpath.DurationSeconds, footpath);
            });

        }

        for (var i = network.Connections.Count - 1; i >= 0; i--)
        {
            var connection = network.Connections[i];
            // No journey starting at query time can use an earlier departure.
            // Each query builds its own profiles; earlier profile entries are not reused.
            if (connection.DepartureTime < query.DepartureTime) break;
            TripsOrConnectionsScanned++;

            var directWalk = walkingToTarget[connection.ArrivalStopId];
            var directArrival = directWalk.Duration == Infinity
                ? Infinity
                : SaturatingAdd(connection.ArrivalTime, directWalk.Duration);
            var suffix = EvaluateProfile(profiles[connection.ArrivalStopId], connection.ArrivalTime);
            // Reuse per-trip vectors: profiles copy their values and retain immutable
            // journey references, so they never alias these mutable scratch arrays.
            var arrivals = tripArrivals[connection.TripId];
            var pointers = tripPointers[connection.TripId];
            ParetoPointer? directPointer = null;
            for (var legLimit = 0; legLimit < maxLegs; legLimit++)
            {
                var bestArrival = directArrival;
                var bestCount = directArrival == Infinity ? int.MaxValue : 1;
                var choice = 0; // direct walk, stay on trip, transfer
                var stay = pointers[legLimit];
                var stayArrival = arrivals[legLimit];
                if (stayArrival < bestArrival || (stayArrival == bestArrival && stay is not null && stay.LegCount < bestCount))
                {
                    bestArrival = stayArrival;
                    bestCount = stay?.LegCount ?? int.MaxValue;
                    choice = 1;
                }
                var transfer = legLimit == 0 ? default : suffix.Pointers[legLimit - 1];
                var transferArrival = legLimit == 0 ? Infinity : suffix.ArrivalByMaxLegs[legLimit - 1];
                var transferCount = transfer.Journey is null ? int.MaxValue : transfer.Journey.LegCount + 1;
                if (transferArrival < bestArrival || (transferArrival == bestArrival && transfer.Journey is not null && transferCount < bestCount))
                {
                    bestArrival = transferArrival;
                    choice = 2;
                }
                arrivals[legLimit] = bestArrival;
                // Allocate only the winning alternative. Equal alternatives retain
                // the original preference order: direct, then stay, then transfer.
                pointers[legLimit] = choice switch
                {
                    0 => directArrival == Infinity ? null
                        : directPointer ??= new ParetoPointer(connection, connection, null, directWalk.Path, null),
                    1 => stay is null ? null : stay with { EnterConnection = connection },
                    _ => transfer.Journey is null ? null : new ParetoPointer(connection, connection,
                        transfer.InitialFootpath, null, transfer.Journey)
                };
            }

            // Per-trip state is updated even when limited walking skips all profiles.
            var departureProfile = EvaluateProfile(
                profiles[connection.DepartureStopId],
                connection.DepartureTime);
            if (!Improves(arrivals, departureProfile.ArrivalByMaxLegs))
                continue;

            // One worker owns one stop's profile; the dictionary, vectors and
            // pointers are read-only. Complete this connection before scanning the next.
            if (walkingService is not null)
                PropagateWalkingProfilesWithService(walkingService, profiles, connection.DepartureStopId,
                    connection.DepartureTime, arrivals, pointers);
            else
                WalkingExpansion.ForEach(footpathGraph, connection.DepartureStopId, true, footpath =>
                    InsertIntoProfile(profiles[footpath.FromStopId], connection.DepartureTime - footpath.DurationSeconds,
                        arrivals, pointers, footpath.FromStopId == footpath.ToStopId ? null : footpath));
        }

        var result = new ParetoSet();
        var sourceProfile = EvaluateProfile(profiles[sourceStopId], query.DepartureTime);
        for (var legLimit = 0; legLimit < maxLegs; legLimit++)
        {
            if (sourceProfile.ArrivalByMaxLegs[legLimit] == Infinity
                || sourceProfile.Pointers[legLimit].Journey is null)
                continue;

            result.TryAdd(ReconstructParetoJourney(sourceProfile.Pointers[legLimit]));
        }

        // The paper focuses on journeys containing a vehicle leg. The routing
        // contract also permits a direct walking journey, so include it explicitly.
        var sourceWalk = walkingToTarget[sourceStopId];
        if (sourceWalk.Duration != Infinity)
        {
            result.TryAdd(new Journey
            {
                Legs = sourceWalk.Path is null
                    ? [new JourneyLeg
                    {
                        FromStopId = sourceStopId,
                        ToStopId = targetStopId,
                        DepartureTime = query.DepartureTime,
                        ArrivalTime = query.DepartureTime,
                        TripId = null,
                        RouteId = null
                    }]
                    : [new JourneyLeg
                    {
                        FromStopId = sourceWalk.Path.FromStopId,
                        ToStopId = sourceWalk.Path.ToStopId,
                        DepartureTime = query.DepartureTime,
                        ArrivalTime = SaturatingAdd(query.DepartureTime, sourceWalk.Duration),
                        TripId = null,
                        RouteId = null
                    }]
            });
        }

        return new RoutingResult
        {
            BestJourney = null,
            ParetoJourneys = result,
            QueryDuration = TimeSpan.Zero
        };
    }

    private static int ProfileIndex(List<ParetoProfileEntry> profile, int departure)
    {
        // A frequent case during a backward scan; walking may also require insertion
        // in the middle, so retain a proper lower-bound search for that case.
        if (departure <= profile[0].DepartureTime) return 0;
        var lo = 1;
        var hi = profile.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (profile[mid].DepartureTime < departure) lo = mid + 1;
            else hi = mid;
        }
        return Math.Min(lo, profile.Count - 1); // Infinity sentinel.
    }

    private static ParetoProfileEntry EvaluateProfile(List<ParetoProfileEntry> profile, int earliestDeparture)
        => profile[ProfileIndex(profile, earliestDeparture)];

    private static void InsertIntoProfile(List<ParetoProfileEntry> profile, int departureTime,
        int[] arrivals, ParetoPointer?[] pointers, Footpath? initialFootpath)
    {
        var index = ProfileIndex(profile, departureTime);
        var existing = profile[index];
        if (!Improves(arrivals, existing.ArrivalByMaxLegs)) return;

        if (existing.DepartureTime != departureTime)
        {
            // Only a genuinely new breakpoint needs new arrays. Clone before writing
            // to keep existing breakpoints and other stop profiles independent.
            existing = new ParetoProfileEntry(departureTime, (int[])existing.ArrivalByMaxLegs.Clone(),
                (ProfilePointer[])existing.Pointers.Clone());
            profile.Insert(index, existing);
        }
        MergeInto(existing, arrivals, pointers, initialFootpath);

        for (var i = index - 1; i >= 0; i--)
        {
            var previous = profile[i];
            var changed = MergeInto(previous, arrivals, pointers, initialFootpath);
            if (previous.ArrivalByMaxLegs.AsSpan().SequenceEqual(profile[i + 1].ArrivalByMaxLegs))
                profile.RemoveAt(i);
            else if (!changed)
                break;
        }
    }

    private static bool Improves(int[] candidate, int[] current)
    {
        for (var i = 0; i < candidate.Length; i++)
            if (candidate[i] < current[i]) return true;
        return false;
    }

    private static bool MergeInto(ParetoProfileEntry entry, int[] arrivals,
        ParetoPointer?[] pointers, Footpath? initialFootpath)
    {
        var changed = false;
        for (var i = 0; i < arrivals.Length; i++)
        {
            if (arrivals[i] >= entry.ArrivalByMaxLegs[i]) continue;
            entry.ArrivalByMaxLegs[i] = arrivals[i];
            entry.Pointers[i] = new ProfilePointer(pointers[i], initialFootpath);
            changed = true;
        }
        return changed;
    }
    private static int[] FilledVector(int length, int value)
    {
        var vector = new int[length];
        Array.Fill(vector, value);
        return vector;
    }

    private static int SaturatingAdd(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;

    private static Journey ReconstructParetoJourney(ProfilePointer profilePointer)
    {
        var pointer = profilePointer.Journey!;
        var legs = new List<JourneyLeg>();
        if (profilePointer.InitialFootpath is { } initial)
        {
            legs.Add(new JourneyLeg
            {
                FromStopId = initial.FromStopId,
                ToStopId = initial.ToStopId,
                DepartureTime = pointer.EnterConnection.DepartureTime
                    - initial.DurationSeconds,
                ArrivalTime = pointer.EnterConnection.DepartureTime,
                TripId = null,
                RouteId = null
            });
        }

        for (var current = pointer; current is not null; current = current.Next)
        {
            legs.Add(new JourneyLeg
            {
                FromStopId = current.EnterConnection.DepartureStopId,
                ToStopId = current.ExitConnection.ArrivalStopId,
                DepartureTime = current.EnterConnection.DepartureTime,
                ArrivalTime = current.ExitConnection.ArrivalTime,
                TripId = current.EnterConnection.TripId,
                RouteId = current.EnterConnection.RouteId
            });

            if (current.TransferFootpath is { } transfer)
            {
                legs.Add(new JourneyLeg
                {
                    FromStopId = transfer.FromStopId,
                    ToStopId = transfer.ToStopId,
                    DepartureTime = current.ExitConnection.ArrivalTime,
                    ArrivalTime = SaturatingAdd(current.ExitConnection.ArrivalTime, transfer.DurationSeconds),
                    TripId = null,
                    RouteId = null
                });
            }
        }

        var exit = pointer;
        while (exit.Next is not null) exit = exit.Next;

        if (exit.FinalFootpath is { } final)
        {
            legs.Add(new JourneyLeg
            {
                FromStopId = final.FromStopId,
                ToStopId = final.ToStopId,
                DepartureTime = exit.ExitConnection.ArrivalTime,
                ArrivalTime = SaturatingAdd(exit.ExitConnection.ArrivalTime, final.DurationSeconds),
                TripId = null,
                RouteId = null
            });
        }

        return new Journey { Legs = legs };
    }

    private RoutingResult ExtractJourney(string t, string s, int departureTime, CsaInput input)
    {
        if (input.S[t] == Infinity)
        {
            return new RoutingResult
            {
                BestJourney = null,
                ParetoJourneys = new ParetoSet(),
                QueryDuration = TimeSpan.Zero
            };
        }

        var legs = new Stack<JourneyLeg>();
        while (input.J[t] is { EnterConnection: { } enter, ExitConnection: { } exit, Footpath: { } footpath })
        {
            legs.Push(new JourneyLeg
            {
                FromStopId = footpath.FromStopId,
                ToStopId = footpath.ToStopId,
                DepartureTime = exit.ArrivalTime,
                ArrivalTime = exit.ArrivalTime + footpath.DurationSeconds,
                TripId = null,
                RouteId = null
            });
            legs.Push(new JourneyLeg
            {
                FromStopId = enter.DepartureStopId,
                ToStopId = exit.ArrivalStopId,
                DepartureTime = enter.DepartureTime,
                ArrivalTime = exit.ArrivalTime,
                TripId = enter.TripId,
                RouteId = enter.RouteId
            });
            t = enter.DepartureStopId;
        }
        legs.Push(new JourneyLeg
        {
            FromStopId = s,
            ToStopId = t,
            DepartureTime = departureTime,
            ArrivalTime = input.S[t],
            TripId = null,
            RouteId = null
        });
        return new RoutingResult
        {
            BestJourney = new Journey
            {
                Legs = legs.ToList()
            },
            ParetoJourneys = new ParetoSet(),
            QueryDuration = TimeSpan.Zero
        };

    }

    private static int FindFirstConnectionAtOrAfter(IReadOnlyList<Connection> connections, int departureTime)
    {
        int lo = 0, hi = connections.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (connections[mid].DepartureTime < departureTime)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

