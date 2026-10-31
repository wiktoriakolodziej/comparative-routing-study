using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.Core.Walking;
using AlgorithmComparison.Core.Footpaths;

namespace AlgorithmComparison.Algorithms.Csa;

public partial class ConnectionScanAlgorithm
{
    private sealed record SingleLabel(string Stop, int Time, int Boardings,
        SingleLabel? Previous = null, JourneyLeg? Step = null);
    private sealed record SingleTrip(SingleLabel Origin, Connection Enter);

    // Forward CSA, one chronological connection scan per boarding budget. The
    // previous layer is immutable during a scan: an earlier arrival using more
    // vehicles cannot erase a later arrival needed under a smaller budget.
    private RoutingResult RunBoundedSingle(RoutingQuery query, TransitNetwork network)
    {
        if (query.MaxTransfers is < 0)
            throw new ArgumentOutOfRangeException(nameof(query.MaxTransfers));
        var maxBoardings = checked(query.MaxTransfers!.Value + 1);
        Prepare(network);
        if (!network.Stops.ContainsKey(query.SourceStopId) || !network.Stops.ContainsKey(query.TargetStopId))
            throw new ArgumentException("Unknown source or target stop.", nameof(query));
        var previous = network.Stops.Keys.ToDictionary(id => id, _ => (SingleLabel?)null);
        var initial = new SingleLabel(query.SourceStopId, query.DepartureTime, 0);
        previous[initial.Stop] = initial;
        Propagate(initial, previous);
        var connections = preprocessor.GetValue().Connections;
        var first = FindFirstConnectionAtOrAfter(connections, query.DepartureTime);
        for (var budget = 1; budget <= maxBoardings; budget++)
        {
            var current = new Dictionary<string, SingleLabel?>(previous);
            var trips = new Dictionary<string, SingleTrip>();
            var improved = false;
            for (var i = first; i < connections.Count; i++)
            {
                var c = connections[i];
                if (current[query.TargetStopId] is { } target && target.Time < c.DepartureTime) break;
                TripsOrConnectionsScanned++;
                if (!trips.TryGetValue(c.TripId, out var trip))
                {
                    if (previous[c.DepartureStopId] is not { } origin || origin.Time > c.DepartureTime) continue;
                    trip = new SingleTrip(origin, c);
                    trips.Add(c.TripId, trip);
                }
                var label = new SingleLabel(c.ArrivalStopId, c.ArrivalTime, trip.Origin.Boardings + 1,
                    trip.Origin, new JourneyLeg
                    {
                        FromStopId = trip.Enter.DepartureStopId,
                        ToStopId = c.ArrivalStopId,
                        DepartureTime = trip.Enter.DepartureTime,
                        ArrivalTime = c.ArrivalTime,
                        TripId = c.TripId,
                        RouteId = c.RouteId
                    });
                if (!Offer(current, label)) continue;
                improved = true;
                Propagate(label, current);
            }
            previous = current;
            if (!improved) break;
        }
        var best = previous[query.TargetStopId];
        var legs = new List<JourneyLeg>();
        for (var label = best; label?.Previous is not null; label = label.Previous)
            legs.Add(label.Step!);
        legs.Reverse();
        if (best is not null && (legs.Count == 0 || legs[0].DepartureTime > query.DepartureTime))
            legs.Insert(0, new JourneyLeg
            {
                FromStopId = query.SourceStopId,
                ToStopId = query.SourceStopId,
                DepartureTime = query.DepartureTime,
                ArrivalTime = query.DepartureTime
            });
        return new RoutingResult
        {
            BestJourney = best is null ? null : new Journey { Legs = legs },
            ParetoJourneys = new ParetoSet(),
            QueryDuration = TimeSpan.Zero
        };

        bool Offer(Dictionary<string, SingleLabel?> times, SingleLabel label)
        {
            if (times[label.Stop] is { } old && (old.Time < label.Time ||
                old.Time == label.Time && old.Boardings <= label.Boardings)) return false;
            times[label.Stop] = label;
            StopsSettled++; Relaxations++;
            return true;
        }
        void Propagate(SingleLabel origin, Dictionary<string, SingleLabel?> times)
        {
            SingleLabel Walk(string to, int arrival) => new(to, arrival, origin.Boardings, origin,
                new JourneyLeg
                {
                    FromStopId = origin.Stop,
                    ToStopId = to,
                    DepartureTime = origin.Time,
                    ArrivalTime = arrival
                });
            if (walkingService is not null)
                walkingService.PropagateTimes(new[] { new WalkingSeed<SingleLabel>(origin.Stop,
                    origin.Time, origin.Boardings, origin) }, accept: label =>
                        !label.HasWalk || Offer(times, Walk(label.StopId, label.Time)));
            else
                // Historical graphs are transitively closed; radius graphs above
                // always use the shared multi-hop service.
                WalkingExpansion.Run(footpathGraph, origin.Stop, false, f => (f.ToStopId, Time: checked(origin.Time + f.DurationSeconds)),
                    p => { if (p.ToStopId != origin.Stop) Offer(times, Walk(p.ToStopId, p.Time)); });
        }
    }
}
