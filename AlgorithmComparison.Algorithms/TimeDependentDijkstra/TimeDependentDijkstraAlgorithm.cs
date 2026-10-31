using AlgorithmComparison.Algorithms.Common;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.Core.Walking;
using Stop = AlgorithmComparison.Core.Models.Stop;

namespace AlgorithmComparison.Algorithms.TimeDependentDijkstra;

public class TDDijkstraInput
{
    public Graph Graph { get; set; } = new();
    public int? MaxTransfers { get; set; }
    public int? BestZeroTransferArrival { get; set; }
    public Dictionary<(int StopId, int? TripId), List<Label>> LabelsByState { get; } = new();
    public List<Label> TargetLabels { get; } = new();
    public PriorityQueue<Label, (int ArrivalTime, int Boardings)> PriorityQueue { get; set; } = new();
    public Dictionary<int, List<(int ArrivalTime, int Boardings)>> WalkingExpansionFrontiers { get; } = new();
}
public class Edge
{
    public int FromStop { get; set; }
    public int ToStop { get; set; }
    public List<Connection> Connections { get; set; } = new();
}
public class Connection
{
    public int DepartureTime { get; set; } // in seconds since midnight
    public int ArrivalTime { get; set; } // in seconds since midnight
    public int TripId { get; set; }
}

public class Graph
{
    public List<Stop> Stops { get; set; } = new();
    public Dictionary<int, List<Edge>> OutgoingEdges { get; set; } = new();
    public Dictionary<int, string> OriginalStopIds { get; } = new();
    public Dictionary<string, int> StopIdToIndex { get; } = new(StringComparer.Ordinal);
}

public class Label
{
    public int StopId { get; init; }
    public int ArrivalTime { get; init; }
    public int Boardings { get; init; }

    public int? TripId { get; init; }

    public Label? PreviousLabel { get; init; }
    public PreviousStep? PreviousStep { get; init; }

    public bool Active { get; set; } = true;

    public int Transfers => Math.Max(0, Boardings - 1);
}

public record PreviousStep(int CurrentStop, Edge? Edge, Connection? Connection);

public partial class TimeDependentDijkstraAlgorithm : AlgorithmBase, IBenchmarkPreparableRoutingAlgorithm
{
    public override string Name => "Time Dependent Dijkstra";
    private readonly IExtractingPreprocessor<TDDijkstraInput> preprocessor;
    private readonly IFootpathGraph footpathGraph;

    private readonly WalkingTransferService? walkingService;

    public TimeDependentDijkstraAlgorithm(IFootpathGraph footpathGraph)
    {
        ArgumentNullException.ThrowIfNull(footpathGraph);
        this.footpathGraph = footpathGraph;
        walkingService = footpathGraph is RadiusFootpathGraph radius ? new WalkingTransferService(radius) : null;
        this.preprocessor = new DijkstraExtractingPreprocessor();

    }

    public TimeSpan PreprocessingTime => preprocessor.PreprocessingTime;

    public void Prepare(TransitNetwork network)
    {
        if (!preprocessor.IsPreprocessed)
            preprocessor.Preprocess(network);
    }

    protected override RoutingResult RunCore(RoutingQuery query, TransitNetwork network)
    {
        if (query.MaxTransfers is < 0)
            throw new ArgumentOutOfRangeException(nameof(query.MaxTransfers), "MaxTransfers cannot be negative.");

        var input = Preprocess(network, query);

        var targetStop = StopIdToInt(input.Graph, query.TargetStopId);

        Label? settledSingleTarget = null;
        while (input.PriorityQueue.TryDequeue(out var current, out _))
        {
            if (!current.Active)
                continue;

            // No pending label can arrive earlier than this one. With a known
            // zero-transfer journey, every remaining label is dominated at the target.
            if (query.MultiCriteria && input.BestZeroTransferArrival is int zeroTransferArrival
                && current.ArrivalTime >= zeroTransferArrival)
                break;

            StopsSettled++;
            if (current.StopId == targetStop)
            {
                if (!query.MultiCriteria)
                {
                    settledSingleTarget = current;
                    break; // Stop on dequeue, not when the first target is discovered.
                }
                continue;
            }

            var dominatedByTarget = input.TargetLabels.Any(target =>
                target.ArrivalTime <= current.ArrivalTime &&
                target.Transfers <= current.Transfers);

            if (dominatedByTarget)
                continue;

            // Komunikację rozwijamy także po dojściu pieszo.
            RelaxTransitEdges(input, current, targetStop);

            if ((current.PreviousLabel is null || current.TripId is not null) && ShouldExpandWalking(input, current))
            {
                if (walkingService is not null)
                    PropagateWalkingWithService(input, walkingService, new[] { current }, targetStop, validateGraph: false);
                else
                    RelaxWalkingEdges(input, current, targetStop);
            }

        }

        var targetLabels = settledSingleTarget is null
            ? input.TargetLabels : new List<Label> { settledSingleTarget };

        var paretoLabels = targetLabels
            .Where(label => !targetLabels.Any(other =>
                other.ArrivalTime <= label.ArrivalTime
                && other.Transfers <= label.Transfers
                && (other.ArrivalTime < label.ArrivalTime
                    || other.Transfers < label.Transfers)))
            .GroupBy(label => (label.ArrivalTime, label.Transfers))
            .Select(group => group.First())
            .ToList();

        var journeys = paretoLabels
            .Select(label => ReconstructJourney(label, input))
            .OrderBy(journey => journey.ArrivalTime)
            .ThenBy(journey => journey.NumberOfTransfers)
            .ToList();

        var paretoSet = new ParetoSet();
        if (query.MultiCriteria)
        {
            foreach (var journey in journeys)
            {
                if (query.MaxTransfers is null
                    || journey.NumberOfTransfers <= query.MaxTransfers.Value)
                    paretoSet.TryAdd(journey);
            }
        }

        return new RoutingResult
        {
            BestJourney = query.MultiCriteria ? null : journeys.FirstOrDefault(),
            ParetoJourneys = paretoSet,
            QueryDuration = TimeSpan.Zero,
        };

    }
    private TDDijkstraInput Preprocess(TransitNetwork network, RoutingQuery query)
    {
        if (!preprocessor.IsPreprocessed)
        {
            preprocessor.Preprocess(network);
        }

        var input = preprocessor.GetValue();

        Prepare(network);
        var sourceStopId = StopIdToInt(preprocessor.GetValue().Graph, query.SourceStopId);
        var targetStopId = StopIdToInt(preprocessor.GetValue().Graph, query.TargetStopId);

        if (!input.Graph.OriginalStopIds.ContainsKey(sourceStopId))
        {
            throw new InvalidOperationException(
                $"Source stop '{query.SourceStopId}' is absent from the graph.");
        }

        if (!input.Graph.OriginalStopIds.ContainsKey(targetStopId))
        {
            throw new InvalidOperationException(
                $"Target stop '{query.TargetStopId}' is absent from the graph.");
        }

        input.MaxTransfers = query.MaxTransfers;
        input.BestZeroTransferArrival = null;
        input.PriorityQueue.Clear();
        input.LabelsByState.Clear();
        input.TargetLabels.Clear();
        input.WalkingExpansionFrontiers.Clear();

        var start = new Label
        {
            StopId = sourceStopId,
            ArrivalTime = query.DepartureTime,
            Boardings = 0,
            TripId = null
        };

        input.LabelsByState.Add(
            (sourceStopId, null),
            new List<Label> { start });

        if (sourceStopId == targetStopId)
        {
            input.TargetLabels.Add(start);
            input.BestZeroTransferArrival = start.ArrivalTime;
        }

        input.PriorityQueue.Enqueue(
            start,
            (start.ArrivalTime, start.Boardings));

        return input;
    }

    private static int StopIdToInt(Graph graph, string stopId)
    {
        if (!graph.StopIdToIndex.TryGetValue(stopId, out var index))
            throw new ArgumentException($"Unknown stop ID: '{stopId}'.", nameof(stopId));
        return index;
    }
    private static bool ShouldExpandWalking(
    TDDijkstraInput input,
    Label current)
    {
        if (!input.WalkingExpansionFrontiers.TryGetValue(
                current.StopId, out var frontier))
        {
            frontier = new List<(int ArrivalTime, int Boardings)>();

            input.WalkingExpansionFrontiers.Add(
                current.StopId,
                frontier);
        }

        // Wcześniej rozwinęliśmy już przejścia piesze
        // z co najmniej równie dobrego stanu.
        foreach (var previous in frontier)
        {
            if (previous.ArrivalTime <= current.ArrivalTime &&
                previous.Boardings <= current.Boardings)
            {
                return false;
            }
        }

        // Nowe rozwinięcie zastępuje gorsze punkty frontu.
        for (var i = frontier.Count - 1; i >= 0; i--)
        {
            var previous = frontier[i];

            if (current.ArrivalTime <= previous.ArrivalTime &&
                current.Boardings <= previous.Boardings)
            {
                frontier.RemoveAt(i);
            }
        }

        frontier.Add((
            current.ArrivalTime,
            current.Boardings));

        return true;
    }

    private void RelaxTransitEdges(
    TDDijkstraInput input,
    Label current,
    int targetStop)
    {
        if (!input.Graph.OutgoingEdges.TryGetValue(
                current.StopId, out var edges))
        {
            return;
        }

        foreach (var edge in edges)
        {
            var first = FirstAvailableConnection(
                edge.Connections,
                current.ArrivalTime);

            for (var i = first; i < edge.Connections.Count; i++)
            {
                TripsOrConnectionsScanned++;
                var connection = edge.Connections[i];

                var staysOnTrip =
                    current.TripId == connection.TripId;

                // At the limit we may stay on this trip, but cannot board another.
                // Do not break: a later connection in this scan may be on our trip.
                if (!staysOnTrip && input.MaxTransfers is int maxTransfers
                    && current.Boardings > maxTransfers)
                    continue;

                TryAddLabel(
                    input: input,
                    previousLabel: current,
                    stopId: edge.ToStop,
                    arrivalTime: connection.ArrivalTime,
                    boardings: staysOnTrip ? current.Boardings : checked(current.Boardings + 1),
                    tripId: connection.TripId,
                    edge: edge,
                    connection: connection,
                    targetStop: targetStop);
            }
        }
    }

    private void RelaxWalkingEdges(
        TDDijkstraInput input,
        Label current,
        int targetStop)
    {
        var originalStopId =
            input.Graph.OriginalStopIds[current.StopId];

        WalkingExpansion.Run(footpathGraph, originalStopId, false, footpath =>
        {
            if (!input.Graph.StopIdToIndex.TryGetValue(footpath.ToStopId, out var toStopId))
                throw new InvalidOperationException($"Stop '{footpath.ToStopId}' is absent from the graph.");
            var arrival = current.ArrivalTime + footpath.DurationSeconds;
            // Read-only rejection is safe before ordered commits. Survivors are
            // checked again because earlier commits may dominate them.
            var dominated = false;
            foreach (var target in input.TargetLabels)
            {
                if (target.ArrivalTime <= arrival && target.Transfers <= current.Transfers)
                { dominated = true; break; }
            }
            if (!dominated && input.LabelsByState.TryGetValue((toStopId, null), out var bag))
                foreach (var label in bag)
                {
                    if (label.ArrivalTime <= arrival && label.Boardings <= current.Boardings)
                    { dominated = true; break; }
                }
            return (Stop: toStopId, Arrival: arrival, Dominated: dominated);
        }, candidate =>
        {

            if (!candidate.Dominated)
                TryAddLabel(input, current, candidate.Stop, candidate.Arrival, current.Boardings,
                    null, null, null, targetStop);
        });
    }
    private static int FirstAvailableConnection(
    List<Connection> connections,
    int arrivalTime)
    {
        var left = 0;
        var right = connections.Count;

        while (left < right)
        {
            var middle = left + (right - left) / 2;

            if (connections[middle].DepartureTime < arrivalTime)
                left = middle + 1;
            else
                right = middle;
        }

        return left;
    }

    private Label? TryAddLabel(
        TDDijkstraInput input,
        Label previousLabel,
        int stopId,
        int arrivalTime,
        int boardings,
        int? tripId,
        Edge? edge,
        Connection? connection,
        int targetStop)
    {
        var transfers = Math.Max(0, boardings - 1);
        if (input.MaxTransfers is int maxTransfers && transfers > maxTransfers)
            return null;

        // Sprawdzamy wartości kandydata przed utworzeniem obiektów.
        foreach (var target in input.TargetLabels)
        {
            if (target.ArrivalTime <= arrivalTime &&
                target.Transfers <= transfers)
            {
                return null;
            }
        }

        var key = (StopId: stopId, TripId: tripId);

        if (input.LabelsByState.TryGetValue(key, out var bag))
        {
            // Wszystkie etykiety w tej grupie mają ten sam TripId.
            foreach (var existing in bag)
            {
                if (existing.ArrivalTime <= arrivalTime &&
                    existing.Boardings <= boardings)
                {
                    return null;
                }
            }
        }
        else
        {
            bag = new List<Label>();
            input.LabelsByState.Add(key, bag);
        }

        // Dopiero zaakceptowany kandydat wymaga alokacji.
        var candidate = new Label
        {
            StopId = stopId,
            ArrivalTime = arrivalTime,
            Boardings = boardings,
            TripId = tripId,
            PreviousLabel = previousLabel,
            PreviousStep = new PreviousStep(
                previousLabel.StopId,
                edge,
                connection)
        };

        // Usuwamy etykiety zdominowane w obrębie tego samego stanu.
        for (var i = bag.Count - 1; i >= 0; i--)
        {
            var existing = bag[i];

            if (arrivalTime <= existing.ArrivalTime &&
                boardings <= existing.Boardings)
            {
                existing.Active = false;
                bag.RemoveAt(i);

                if (stopId == targetStop)
                {
                    input.TargetLabels.Remove(existing);
                }
            }
        }

        bag.Add(candidate);
        Relaxations++;

        if (stopId == targetStop)
        {
            input.TargetLabels.Add(candidate);
            if (transfers == 0)
                input.BestZeroTransferArrival = input.BestZeroTransferArrival is int best
                    ? Math.Min(best, arrivalTime) : arrivalTime;
        }

        input.PriorityQueue.Enqueue(
            candidate,
            (arrivalTime, boardings));
        return candidate;
    }

    private static Journey ReconstructJourney(Label target, TDDijkstraInput input)
    {
        if (target.PreviousLabel is null)
        {
            var stopId = input.Graph.OriginalStopIds[target.StopId];

            return new Journey
            {
                Legs =
                [
                    new JourneyLeg
                    {
                        FromStopId = stopId,
                        ToStopId = stopId,
                        DepartureTime = target.ArrivalTime,
                        ArrivalTime = target.ArrivalTime,
                        TripId = null,
                        RouteId = null
                    }
                ]
            };
        }

        var steps = new List<Label>();

        for (var label = target;
             label.PreviousLabel is not null;
             label = label.PreviousLabel!)
        {
            steps.Add(label);
        }

        steps.Reverse();

        var legs = new List<JourneyLeg>();

        foreach (var label in steps)
        {
            var previous = label.PreviousLabel!;
            var step = label.PreviousStep!;
            var connection = step.Connection;

            var leg = new JourneyLeg
            {
                FromStopId = input.Graph.OriginalStopIds[previous.StopId],
                ToStopId = input.Graph.OriginalStopIds[label.StopId],
                DepartureTime = connection?.DepartureTime
                    ?? previous.ArrivalTime,
                ArrivalTime = label.ArrivalTime,
                TripId = connection?.TripId.ToString()
            };

            // Kontynuowanie jazdy tym samym kursem:
            // wydłuż istniejący odcinek zamiast tworzyć nowy.
            if (legs.Count > 0
                && !leg.IsWalk
                && !legs[^1].IsWalk
                && legs[^1].TripId == leg.TripId
                && legs[^1].ToStopId == leg.FromStopId)
            {
                var last = legs[^1];

                legs[^1] = new JourneyLeg
                {
                    FromStopId = last.FromStopId,
                    ToStopId = leg.ToStopId,
                    DepartureTime = last.DepartureTime,
                    ArrivalTime = leg.ArrivalTime,
                    TripId = last.TripId,
                    RouteId = last.RouteId
                };
            }
            else
            {
                legs.Add(leg);
            }
        }

        return new Journey { Legs = legs };
    }
}

