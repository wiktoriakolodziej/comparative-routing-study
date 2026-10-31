using AlgorithmComparison.Algorithms.Common;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using System.Diagnostics;
using AlgorithmComparison.Core.Walking;

namespace AlgorithmComparison.Algorithms.Raptor;

public record Stop(int Id);
public record Route(int Id, int NoOfStops, int NoOfTrips);
public record Trip(int Id);
public record Transfer(int Id, int ArrivalStopId, int Duration);

public enum RaptorParentKind
{
    Ride,
    Walk
}

public sealed record RaptorParent(
    RaptorParentKind Kind,
    int PreviousStopId,
    int PreviousRound,
    int DepartureTime,
    int ArrivalTime,
    int? RouteId = null,
    int? TripId = null,
    int? TransferId = null);

public class RaptorInput
{
    public Dictionary<int, Dictionary<int, List<(int ArrivalTime, int DepartureTime)>>> StopTimes { get; set; } = new Dictionary<int, Dictionary<int, List<(int ArrivalTime, int DepartureTime)>>>();
    public Dictionary<int, List<int>> RouteTrips { get; set; } = new Dictionary<int, List<int>>(); //Dictionary<routeId, List<tripId>>, trips are sorted by departure time
    public Dictionary<int, (bool CanBoard, bool CanAlight)[]> RoutePermissions { get; } = new();
    public Dictionary<int, List<int>> RouteStops { get; set; } = new Dictionary<int, List<int>>(); //Dictionary<routeId, List<stopId>>, stops are sorted by stop sequence
    public Dictionary<int, Route> Routes { get; set; } = new Dictionary<int, Route>();

    public Dictionary<int, List<int>> StopTransfers = new Dictionary<int, List<int>>();
    public Dictionary<int, List<int>> StopRoutes = new Dictionary<int, List<int>>(); //Dictionary<stopId, List<routeId>>
    public Dictionary<int, Stop> Stops { get; set; } = new Dictionary<int, Stop>();
    public Dictionary<int, Trip> Trips { get; set; } = new Dictionary<int, Trip>();
    public Dictionary<int, Transfer> Transfers { get; set; } = new Dictionary<int, Transfer>();

    public Dictionary<string, int> StopIdToIndex { get; set; } = new();
    public Dictionary<int, string> StopIndexToId { get; set; } = new();
    public Dictionary<string, int> TripIdToIndex { get; set; } = new();
    public Dictionary<int, string> TripIndexToId { get; set; } = new();

    public Dictionary<int, int> Q { get; set; } = new Dictionary<int, int>(); //<routeId, stopSequence> -- NOT STOP ID
    public HashSet<int> MarkedStops { get; set; } = new HashSet<int>(); //stopId
    public Dictionary<int, int> TStar { get; set; } = new Dictionary<int, int>(); //local pruning
    public Dictionary<int, int[]> T { get; set; } = new Dictionary<int, int[]>();

    public Dictionary<(int StopId, int Round), RaptorParent> Parents { get; set; } = new();
    public Dictionary<int, string> RouteIndexToGtfsRouteId { get; set; } = new();
}
public partial class RaptorAlgorithm : AlgorithmBase, IBenchmarkPreparableRoutingAlgorithm
{
    public override string Name => "RAPTOR Mine";

    private readonly IExtractingPreprocessor<RaptorInput> preprocessor;
    private const int Infinity = int.MaxValue;

    private readonly WalkingTransferService? walkingService;
    public RaptorAlgorithm(IFootpathGraph footpathGraph) : this(footpathGraph, null) { }

    protected RaptorAlgorithm(IFootpathGraph footpathGraph, IEnumerable<string>? walkingStops)
    {
        ArgumentNullException.ThrowIfNull(footpathGraph);
        walkingService = footpathGraph is RadiusFootpathGraph radius ? new WalkingTransferService(radius)
            : walkingStops is not null ? new WalkingTransferService(footpathGraph, walkingStops) : null;

        this.preprocessor = new RaptorExtractingPreprocessor(footpathGraph);
    }

    public virtual TimeSpan PreprocessingTime => preprocessor.PreprocessingTime;

    public virtual void Prepare(TransitNetwork network)
    {
        if (!preprocessor.IsPreprocessed)
            preprocessor.Preprocess(network);
    }

    protected virtual RaptorInput GetInput(TransitNetwork network)
    {
        Prepare(network);
        return preprocessor.GetValue();
    }

    protected virtual CancellationToken RoutingCancellation => default;

    protected override RoutingResult RunCore(RoutingQuery query, TransitNetwork network)
    {
        var cancellation = RoutingCancellation;
        cancellation.ThrowIfCancellationRequested();
        var input = GetInput(network);
        Preprocess(query, input);

        var stopwatch = Stopwatch.StartNew();
        var targetStopId = input.StopIdToIndex[query.TargetStopId];

        var rounds = query.MaxTransfers.HasValue
            ? checked(query.MaxTransfers.Value + 1)
            : input.Trips.Count;

        for (int k = 1; k <= rounds; k++) //zaczynamy od 1 rundy, czyli gdzie możemy dojechać jednym trip
        {
            cancellation.ThrowIfCancellationRequested();
            input.Q.Clear();

            foreach (var stopId in input.MarkedStops)
            {
                if (!input.StopRoutes.TryGetValue(stopId, out var routeIds))
                {
                    continue;
                }

                foreach (var routeId in routeIds)
                {
                    var stopSequence = input.RouteStops[routeId].IndexOf(stopId); //to jest do poprawy bo czas O(n)
                    if (input.Q.TryGetValue(routeId, out var existingStopSequence))
                    {
                        if (stopSequence < existingStopSequence)
                        {
                            input.Q[routeId] = stopSequence;
                        }
                    }
                    else
                    {
                        input.Q[routeId] = stopSequence;
                    }
                }
            }
            input.MarkedStops.Clear();

            //traverse each route
            foreach (var (routeId, stopSequence) in input.Q)
            {
                cancellation.ThrowIfCancellationRequested();
                var permissions = input.RoutePermissions.GetValueOrDefault(routeId);
                int? tripId = null; //current trip index
                var boardingStopId = -1;
                var boardingStopSequence = -1;

                for (var index = stopSequence; index < input.Routes[routeId].NoOfStops; index++)
                {
                    var stopId = input.RouteStops[routeId][index];
                    if (tripId.HasValue && (permissions is null || permissions[index].CanAlight))
                    {
                        var currentTripId = tripId.Value;
                        var arrivalTime = input.StopTimes[routeId][tripId.Value][index].ArrivalTime;
                        var limit = Math.Min(input.TStar[stopId], input.TStar[targetStopId]);
                        if (arrivalTime < limit)
                        {
                            input.T[stopId][k] = arrivalTime;
                            input.TStar[stopId] = arrivalTime;
                            var departureTime = input.StopTimes[routeId][currentTripId][boardingStopSequence].DepartureTime;

                            input.Parents[(stopId, k)] = new RaptorParent(
                                Kind: RaptorParentKind.Ride,
                                PreviousStopId: boardingStopId,
                                PreviousRound: k - 1,
                                DepartureTime: departureTime,
                                ArrivalTime: arrivalTime,
                                RouteId: routeId,
                                TripId: currentTripId);

                            input.MarkedStops.Add(stopId);
                        }

                    }

                    var previousRoundTime = input.T[stopId][k - 1];
                    if ((permissions is null || permissions[index].CanBoard) &&
                        (!tripId.HasValue || previousRoundTime <= input.StopTimes[routeId][tripId.Value][index].DepartureTime))
                    {
                        foreach (var scannedTripId in input.RouteTrips[routeId])
                        {
                            var departureTime = input.StopTimes[routeId][scannedTripId][index].DepartureTime;
                            if (departureTime >= previousRoundTime)
                            {
                                tripId = scannedTripId;
                                boardingStopId = stopId;
                                boardingStopSequence = index;
                                break;
                            }
                        }
                    }
                }
            }

            if (walkingService is not null)
                PropagateWalkingWithService(input, walkingService, k, cancellation);
            else
            {
                foreach (var stopId in input.MarkedStops.ToList())
                {
                    if (!input.StopTransfers.TryGetValue(stopId, out var footpathIds))
                    {
                        continue;
                    }

                    var walkingDeparture = input.T[stopId][k];
                    WalkingExpansion.Run(footpathIds, footpathIndex =>
                    {
                        var transfer = input.Transfers[footpathIndex];
                        return (Index: footpathIndex, Stop: transfer.ArrivalStopId, Arrival: walkingDeparture + transfer.Duration);
                    }, candidate =>
                    {
                        var footpathIndex = candidate.Index;
                        var arrivalStopId = candidate.Stop;
                        var candidateTime = candidate.Arrival;

                        if (candidateTime < input.T[arrivalStopId][k])
                        {
                            input.T[arrivalStopId][k] = candidateTime;
                            input.TStar[arrivalStopId] = Math.Min(input.TStar[arrivalStopId], candidateTime);

                            input.Parents[(arrivalStopId, k)] = new RaptorParent(
                                Kind: RaptorParentKind.Walk,
                                PreviousStopId: stopId,
                                PreviousRound: k,
                                DepartureTime: input.T[stopId][k],
                                ArrivalTime: candidateTime,
                                TransferId: footpathIndex);

                            input.MarkedStops.Add(arrivalStopId);
                        }
                    });
                }

            }

            if (input.MarkedStops.Count == 0)
                break;
        }

        stopwatch.Stop();

        return ExtractRoutingResult(
            input,
            query,
            rounds,
            stopwatch.Elapsed);
    }

    private void Preprocess(RoutingQuery query, RaptorInput input)
    {

        if (!input.StopIdToIndex.TryGetValue(query.SourceStopId, out var sourceStopId))
        {
            throw new ArgumentException(
                $"Unknown source stop: '{query.SourceStopId}'.",
                nameof(query));
        }

        if (!input.StopIdToIndex.ContainsKey(query.TargetStopId))
        {
            throw new ArgumentException(
                $"Unknown target stop: '{query.TargetStopId}'.",
                nameof(query));
        }

        if (query.MaxTransfers is < 0)
        {
            throw new ArgumentException(
                "MaxTransfers cannot be negative.",
                nameof(query));
        }

        // k oznacza liczbę wykorzystanych tripów.
        // Dla MaxTransfers = 0 potrzebujemy k = 0 i k = 1.
        var maxTrips = query.MaxTransfers.HasValue
            ? checked(query.MaxTransfers.Value + 1)
            : input.Trips.Count;

        input.Q.Clear();
        input.MarkedStops.Clear();
        input.T.Clear();
        input.TStar.Clear();
        input.Parents.Clear();

        foreach (var stopId in input.Stops.Keys)
        {
            var labels = new int[checked(maxTrips + 1)];
            Array.Fill(labels, Infinity);

            input.T.Add(stopId, labels);
            input.TStar.Add(stopId, Infinity);
        }

        // Stan początkowy: jesteśmy na źródle o zadanej godzinie.
        input.T[sourceStopId][0] = query.DepartureTime;
        input.TStar[sourceStopId] = query.DepartureTime;
        input.MarkedStops.Add(sourceStopId);

        if (walkingService is not null)
        {
            PropagateWalkingWithService(input, walkingService, 0, RoutingCancellation);
            return;
        }

        // Opcjonalnie: dojście pieszo przed pierwszym boardingiem.
        // Dzięki tranzytywnemu domknięciu footpathów wystarczy jeden przebieg.
        if (input.StopTransfers.TryGetValue(sourceStopId, out var transferIds))
        {
            WalkingExpansion.Run(transferIds, transferId =>
            {
                var transfer = input.Transfers[transferId];
                return (Transfer: transfer, Arrival: query.DepartureTime + transfer.Duration);
            }, candidate =>
            {
                var transfer = candidate.Transfer;
                var transferId = transfer.Id;
                var candidateTime = candidate.Arrival;

                if (candidateTime >= input.T[transfer.ArrivalStopId][0])
                {
                    return;
                }

                input.T[transfer.ArrivalStopId][0] = candidateTime;
                input.TStar[transfer.ArrivalStopId] = candidateTime;
                input.Parents[(transfer.ArrivalStopId, 0)] = new RaptorParent(
                    Kind: RaptorParentKind.Walk,
                    PreviousStopId: sourceStopId,
                    PreviousRound: 0,
                    DepartureTime: query.DepartureTime,
                    ArrivalTime: candidateTime,
                    TransferId: transferId);
                input.MarkedStops.Add(transfer.ArrivalStopId);
            });
        }
    }

    private static RoutingResult ExtractRoutingResult(
    RaptorInput input,
    RoutingQuery query,
    int maxTrips,
    TimeSpan queryDuration)
    {
        var sourceStopId = input.StopIdToIndex[query.SourceStopId];
        var targetStopId = input.StopIdToIndex[query.TargetStopId];

        var paretoSet = new ParetoSet();

        for (var k = 0; k <= maxTrips; k++)
        {
            if (input.T[targetStopId][k] == Infinity)
            {
                continue;
            }

            var journey = ExtractJourney(
                input,
                sourceStopId,
                targetStopId,
                k);

            paretoSet.TryAdd(journey);
        }

        if (query.MultiCriteria)
        {
            return new RoutingResult
            {
                BestJourney = null,
                ParetoJourneys = paretoSet,
                QueryDuration = queryDuration
            };
        }

        return new RoutingResult
        {
            // ParetoSet jest posortowany po czasie przyjazdu rosnąco.
            BestJourney = paretoSet.Journeys.FirstOrDefault(),
            ParetoJourneys = new ParetoSet(),
            QueryDuration = queryDuration
        };
    }

    private static Journey ExtractJourney(
    RaptorInput input,
    int sourceStopId,
    int targetStopId,
    int targetRound)
    {
        // Specjalny przypadek: źródło jest celem.
        // Journey wymaga przynajmniej jednego lega.
        if (sourceStopId == targetStopId && targetRound == 0)
        {
            var time = input.T[sourceStopId][0];
            var gtfsStopId = input.StopIndexToId[sourceStopId];

            return new Journey
            {
                Legs =
                [
                    new JourneyLeg
                {
                    FromStopId = gtfsStopId,
                    ToStopId = gtfsStopId,
                    DepartureTime = time,
                    ArrivalTime = time,
                    TripId = null,
                    RouteId = null
                }
                ]
            };
        }

        var reversedLegs = new List<JourneyLeg>();

        var currentStopId = targetStopId;
        var currentRound = targetRound;

        while (currentStopId != sourceStopId || currentRound != 0)
        {
            if (!input.Parents.TryGetValue(
                    (currentStopId, currentRound),
                    out var parent))
            {
                throw new InvalidOperationException(
                    "Cannot reconstruct journey: parent pointer is missing.");
            }

            var tripId = parent.TripId is int internalTripId
                ? input.TripIndexToId[internalTripId]
                : null;

            var routeId = parent.RouteId is int internalRouteId
                ? input.RouteIndexToGtfsRouteId[internalRouteId]
                : null;

            reversedLegs.Add(new JourneyLeg
            {
                FromStopId = input.StopIndexToId[parent.PreviousStopId],
                ToStopId = input.StopIndexToId[currentStopId],
                DepartureTime = parent.DepartureTime,
                ArrivalTime = parent.ArrivalTime,
                TripId = tripId,
                RouteId = routeId
            });

            currentStopId = parent.PreviousStopId;
            currentRound = parent.PreviousRound;
        }

        reversedLegs.Reverse();

        return new Journey
        {
            Legs = reversedLegs
        };
    }
}

