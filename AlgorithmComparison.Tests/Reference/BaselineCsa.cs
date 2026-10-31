namespace AlgorithmComparison.Tools.CsaBaseline;

using AlgorithmComparison.Algorithms.Common;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using System.Diagnostics;
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

public class ConnectionScanAlgorithm : AlgorithmBase, IBenchmarkPreparableRoutingAlgorithm
{
    private const int DefaultMaxLegs = 8;
    private const int Infinity = int.MaxValue;

    private sealed record ParetoPointer(
        Connection EnterConnection,
        Connection ExitConnection,
        Footpath? InitialFootpath,
        Footpath? TransferFootpath,
        Footpath? FinalFootpath,
        ParetoPointer? Next);

    private sealed record ParetoProfileEntry(
        int DepartureTime,
        int[] ArrivalByMaxLegs,
        ParetoPointer?[] Pointers);

    private readonly IFootpathGraph footpathGraph;

    private readonly IExtractingPreprocessor<CsaInput> preprocessor;

    public override string Name => "CSA";

    public ConnectionScanAlgorithm(IFootpathGraph footpathGraph)
    {
        ArgumentNullException.ThrowIfNull(footpathGraph);
        this.footpathGraph = footpathGraph;

        this.preprocessor = new CsaPreprocessor();
    }

    public TimeSpan PreprocessingTime => preprocessor.PreprocessingTime;

    public void Prepare(TransitNetwork network) => preprocessor.Preprocess(network);

    protected override RoutingResult RunCore(RoutingQuery query, TransitNetwork network)
    {
        if (query.MultiCriteria)
            return RunPareto(query, network);

        Prepare(network);
        var input = this.preprocessor.GetValue();

        var s = query.SourceStopId;
        var t = query.TargetStopId;
        var departureTime = query.DepartureTime;

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

    /// <summary>
    /// Profile CSA from the target backwards. Each vector component stores the
    /// earliest arrival using at most that many vehicle legs, as in paper §4.5.
    /// </summary>
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
                new(Infinity, FilledVector(maxLegs, Infinity), new ParetoPointer?[maxLegs])
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

        var walkingToTarget = network.Stops.Keys.ToDictionary(
            stopId => stopId,
            _ => (Duration: Infinity, Path: (Footpath?)null));
        walkingToTarget[targetStopId] = (0, null);
        WalkingExpansion.Run(footpathGraph, targetStopId, true, f => f, footpath =>
        {
            if (footpath.DurationSeconds < walkingToTarget[footpath.FromStopId].Duration)
                walkingToTarget[footpath.FromStopId] = (footpath.DurationSeconds, footpath);
        });

        for (var i = network.Connections.Count - 1; i >= 0; i--)
        {
            var connection = network.Connections[i];
            TripsOrConnectionsScanned++;

            var directWalk = walkingToTarget[connection.ArrivalStopId];
            var directArrival = directWalk.Duration == Infinity
                ? Infinity
                : SaturatingAdd(connection.ArrivalTime, directWalk.Duration);
            var tau1 = FilledVector(maxLegs, directArrival);

            var tau2 = tripArrivals[connection.TripId];
            var suffix = EvaluateProfile(
                profiles[connection.ArrivalStopId], connection.ArrivalTime);
            var tau3 = Shift(suffix.ArrivalByMaxLegs);
            var tau3Pointers = ShiftPointers(suffix.Pointers);

            var arrivals = FilledVector(maxLegs, Infinity);
            var pointers = new ParetoPointer?[maxLegs];
            for (var legLimit = 0; legLimit < maxLegs; legLimit++)
            {
                ParetoPointer? directPointer = directArrival == Infinity
                    ? null
                    : new ParetoPointer(connection, connection, null, null, directWalk.Path, null);

                var stayPointer = tripPointers[connection.TripId][legLimit] is { } tripPointer
                    ? tripPointer with { EnterConnection = connection }
                    : null;

                var transferPointer = tau3Pointers[legLimit] is { } suffixPointer
                    ? new ParetoPointer(
                        connection,
                        connection,
                        null,
                        suffixPointer.InitialFootpath,
                        null,
                        suffixPointer with { InitialFootpath = null })
                    : null;

                ChooseBest(
                    (tau1[legLimit], directPointer),
                    (tau2[legLimit], stayPointer),
                    (tau3[legLimit], transferPointer),
                    out arrivals[legLimit],
                    out pointers[legLimit]);
            }

            // T[c.trip] is needed even when limited walking lets us skip S updates.
            tripArrivals[connection.TripId] = arrivals;
            tripPointers[connection.TripId] = pointers;

            var departureProfile = EvaluateProfile(
                profiles[connection.DepartureStopId],
                connection.DepartureTime);
            if (!Improves(arrivals, departureProfile.ArrivalByMaxLegs))
                continue;

            // One worker owns one stop's profile; the dictionary, vectors and
            // pointers are read-only. Complete this connection before scanning the next.
            WalkingExpansion.ForEach(footpathGraph, connection.DepartureStopId, true, footpath =>
                InsertIntoProfile(profiles[footpath.FromStopId], connection.DepartureTime - footpath.DurationSeconds,
                    arrivals, pointers, footpath.FromStopId == footpath.ToStopId ? null : footpath));
        }

        var result = new ParetoSet();
        var sourceProfile = EvaluateProfile(profiles[sourceStopId], query.DepartureTime);
        for (var legLimit = 0; legLimit < maxLegs; legLimit++)
        {
            if (sourceProfile.ArrivalByMaxLegs[legLimit] == Infinity
                || sourceProfile.Pointers[legLimit] is not { } pointer)
                continue;

            result.TryAdd(ReconstructParetoJourney(pointer));
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

    private static ParetoProfileEntry EvaluateProfile(
        List<ParetoProfileEntry> profile,
        int earliestDeparture)
    {
        var lo = 0;
        var hi = profile.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (profile[mid].DepartureTime < earliestDeparture)
                lo = mid + 1;
            else
                hi = mid;
        }
        return profile[Math.Min(lo, profile.Count - 1)];
    }

    private static void InsertIntoProfile(
        List<ParetoProfileEntry> profile,
        int departureTime,
        int[] arrivals,
        ParetoPointer?[] pointers,
        Footpath? initialFootpath)
    {
        var index = profile.BinarySearch(
            new ParetoProfileEntry(departureTime, [], []),
            Comparer<ParetoProfileEntry>.Create(
                (left, right) => left.DepartureTime.CompareTo(right.DepartureTime)));
        if (index < 0)
            index = ~index;

        var existing = profile[index];
        if (!Improves(arrivals, existing.ArrivalByMaxLegs))
            return;

        var candidatePointers = pointers
            .Select(pointer => pointer is null
                ? null
                : pointer with { InitialFootpath = initialFootpath })
            .ToArray();

        // Each profile entry already includes the best later departure option.
        var entry = MergeEntries(
            existing with { DepartureTime = departureTime },
            arrivals,
            candidatePointers);

        if (existing.DepartureTime == departureTime)
            profile[index] = entry;
        else
            profile.Insert(index, entry);

        // The candidate is available to every earlier departure threshold.
        for (var i = index - 1; i >= 0; i--)
        {
            var previous = profile[i];
            var updated = MergeEntries(previous, arrivals, candidatePointers);
            profile[i] = updated;

            if (updated.ArrivalByMaxLegs.SequenceEqual(
                    profile[i + 1].ArrivalByMaxLegs))
            {
                profile.RemoveAt(i);
            }
            else if (ReferenceEquals(previous, updated))
            {
                break;
            }
        }
    }

    private static bool Improves(int[] candidate, int[] current)
    {
        for (var i = 0; i < candidate.Length; i++)
        {
            if (candidate[i] < current[i])
                return true;
        }
        return false;
    }

    private static ParetoProfileEntry MergeEntries(
        ParetoProfileEntry existing,
        int[] arrivals,
        ParetoPointer?[] pointers)
    {
        int[]? mergedArrivals = null;
        ParetoPointer?[]? mergedPointers = null;
        for (var i = 0; i < existing.ArrivalByMaxLegs.Length; i++)
        {
            if (arrivals[i] < existing.ArrivalByMaxLegs[i])
            {
                mergedArrivals ??= (int[])existing.ArrivalByMaxLegs.Clone();
                mergedPointers ??= (ParetoPointer?[])existing.Pointers.Clone();
                mergedArrivals[i] = arrivals[i];
                mergedPointers[i] = pointers[i];
            }
        }

        if (mergedArrivals is null)
            return existing;

        return existing with
        {
            ArrivalByMaxLegs = mergedArrivals,
            Pointers = mergedPointers!
        };
    }

    private static void ChooseBest(
        (int Arrival, ParetoPointer? Pointer) first,
        (int Arrival, ParetoPointer? Pointer) second,
        (int Arrival, ParetoPointer? Pointer) third,
        out int arrival,
        out ParetoPointer? pointer)
    {
        var best = first;
        if (second.Arrival < best.Arrival
            || (second.Arrival == best.Arrival && PreferPointer(second.Pointer, best.Pointer)))
            best = second;
        if (third.Arrival < best.Arrival
            || (third.Arrival == best.Arrival && PreferPointer(third.Pointer, best.Pointer)))
            best = third;
        arrival = best.Arrival;
        pointer = best.Pointer;
    }

    private static bool PreferPointer(ParetoPointer? candidate, ParetoPointer? current)
    {
        if (candidate is null) return false;
        if (current is null) return true;
        return CountTransitLegs(candidate) < CountTransitLegs(current);
    }

    private static int CountTransitLegs(ParetoPointer pointer)
    {
        var count = 0;
        for (var current = pointer; current is not null; current = current.Next)
            count++;
        return count;
    }

    private static int[] Shift(int[] vector)
    {
        var shifted = FilledVector(vector.Length, Infinity);
        for (var i = 1; i < vector.Length; i++)
            shifted[i] = vector[i - 1];
        return shifted;
    }

    private static ParetoPointer?[] ShiftPointers(ParetoPointer?[] pointers)
    {
        var shifted = new ParetoPointer?[pointers.Length];
        for (var i = 1; i < pointers.Length; i++)
            shifted[i] = pointers[i - 1];
        return shifted;
    }

    private static int[] FilledVector(int length, int value)
    {
        var vector = new int[length];
        Array.Fill(vector, value);
        return vector;
    }

    private static int SaturatingAdd(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;

    private static Journey ReconstructParetoJourney(ParetoPointer pointer)
    {
        var legs = new List<JourneyLeg>();
        if (pointer.InitialFootpath is { } initial)
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
