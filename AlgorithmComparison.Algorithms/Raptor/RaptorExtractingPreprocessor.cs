using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Contracts;
using System.Diagnostics;

namespace AlgorithmComparison.Algorithms.Raptor;

public sealed class RaptorExtractingPreprocessor : IExtractingPreprocessor<RaptorInput>
{
    private RaptorInput? value;
    private readonly IFootpathGraph footpathGraph;

    public RaptorExtractingPreprocessor(IFootpathGraph footpathGraph)
    {
        ArgumentNullException.ThrowIfNull(footpathGraph);
        this.footpathGraph = footpathGraph;
    }

    public bool IsPreprocessed => value is not null;

    public TimeSpan PreprocessingTime { get; private set; }

    public RaptorInput GetValue()
    {
        return value ?? throw new InvalidOperationException(
            "Raptor preprocessor has not been run yet.");
    }

    public void Preprocess(TransitNetwork network)
    {
        if (network is null)
        {
            throw new ArgumentNullException(nameof(network));
        }

        if (IsPreprocessed)
        {
            throw new InvalidOperationException(
                "Raptor preprocessor can only be run once.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = new RaptorInput();

            BuildStopMappings(network, result);
            BuildTripMappings(network, result);
            BuildRoutesAndStopTimes(network, result);
            BuildTransfers(network, result);

            value = result;
        }
        finally
        {
            stopwatch.Stop();
            PreprocessingTime = stopwatch.Elapsed;
        }
    }

    private static void BuildStopMappings(
        TransitNetwork network,
        RaptorInput result)
    {
        var nextStopIndex = 0;

        foreach (var gtfsStop in network.Stops.Values
                     .OrderBy(stop => stop.Id, StringComparer.Ordinal))
        {
            result.StopIdToIndex.Add(gtfsStop.Id, nextStopIndex);
            result.StopIndexToId.Add(nextStopIndex, gtfsStop.Id);
            result.Stops.Add(nextStopIndex, new Stop(nextStopIndex));

            nextStopIndex++;
        }
    }

    private static void BuildTripMappings(
        TransitNetwork network,
        RaptorInput result)
    {
        var nextTripIndex = 0;

        foreach (var gtfsTrip in network.Trips.Values
                     .OrderBy(trip => trip.Id, StringComparer.Ordinal))
        {
            result.TripIdToIndex.Add(gtfsTrip.Id, nextTripIndex);
            result.TripIndexToId.Add(nextTripIndex, gtfsTrip.Id);
            result.Trips.Add(nextTripIndex, new Trip(nextTripIndex));

            nextTripIndex++;
        }
    }

    private static void BuildRoutesAndStopTimes(
        TransitNetwork network,
        RaptorInput result)
    {
        var tripsWithStopTimes = network.Trips.Values
            .Select(trip => new TripWithStopTimes(
                trip,
                trip.StopTimes
                    .OrderBy(stopTime => stopTime.StopSequence)
                    .ToList()))
            .ToList();

        // RAPTOR route = GTFS route + dokładna sekwencja przystanków.
        var routePatterns = tripsWithStopTimes
            .GroupBy(item => new RoutePatternKey(
                item.Trip.RouteId,
                CreateStopsSignature(item.StopTimes)))
            .OrderBy(group => group.Key.GtfsRouteId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.StopsSignature, StringComparer.Ordinal)
            .ToList();

        var nextRouteIndex = 0;

        foreach (var pattern in routePatterns)
        {
            var representative = pattern.First();

            if (representative.StopTimes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Trip '{representative.Trip.Id}' has no stop times.");
            }

            var routeId = nextRouteIndex++;
            result.RouteIndexToGtfsRouteId.Add(routeId, pattern.Key.GtfsRouteId);
            var routeStops = representative.StopTimes
                .Select(stopTime => GetStopIndex(result, stopTime.StopId))
                .ToList();

            var orderedTrips = pattern
                .OrderBy(item => item.StopTimes[0].DepartureTime)
                .ThenBy(item => item.Trip.Id, StringComparer.Ordinal)
                .ToList();

            result.Routes.Add(
                routeId,
                new Route(routeId, routeStops.Count, orderedTrips.Count));

            result.RouteStops.Add(routeId, routeStops);
            result.RouteTrips.Add(routeId, new List<int>());
            result.StopTimes.Add(
                routeId,
                new Dictionary<int, List<(int ArrivalTime, int DepartureTime)>>());

            // Każdy route ma się pojawić tylko raz przy danym przystanku.
            foreach (var stopId in routeStops.Distinct())
            {
                if (!result.StopRoutes.TryGetValue(stopId, out var routeIds))
                {
                    routeIds = new List<int>();
                    result.StopRoutes.Add(stopId, routeIds);
                }

                routeIds.Add(routeId);
            }

            foreach (var item in orderedTrips)
            {
                ValidateStopPattern(item, representative);

                var tripId = result.TripIdToIndex[item.Trip.Id];

                var times = item.StopTimes
                    .Select(stopTime => (
                        ArrivalTime: stopTime.ArrivalTime,
                        DepartureTime: stopTime.DepartureTime))
                    .ToList();

                result.RouteTrips[routeId].Add(tripId);
                result.StopTimes[routeId].Add(tripId, times);
            }
        }
    }

    private void BuildTransfers(
        TransitNetwork network,
        RaptorInput result)
    {
        var nextTransferIndex = 0;

        foreach (var fromGtfsStopId in network.Stops.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            var fromStopId = GetStopIndex(result, fromGtfsStopId);

            foreach (var footpath in footpathGraph.GetOutgoingFootpaths(fromGtfsStopId)
                         .OrderBy(path => path.ToStopId, StringComparer.Ordinal)
                         .ThenBy(path => path.DurationSeconds))
            {
                if (footpath.FromStopId != fromGtfsStopId)
                {
                    throw new InvalidOperationException(
                        "Footpath dictionary key does not match Footpath.FromStopId.");
                }

                var toStopId = GetStopIndex(result, footpath.ToStopId);

                var transfer = new Transfer(
                    nextTransferIndex,
                    toStopId,
                    footpath.DurationSeconds);

                result.Transfers.Add(nextTransferIndex, transfer);

                if (!result.StopTransfers.TryGetValue(fromStopId, out var transferIds))
                {
                    transferIds = new List<int>();
                    result.StopTransfers.Add(fromStopId, transferIds);
                }

                transferIds.Add(nextTransferIndex);
                nextTransferIndex++;
            }
        }
    }

    private static int GetStopIndex(RaptorInput result, string gtfsStopId)
    {
        if (!result.StopIdToIndex.TryGetValue(gtfsStopId, out var stopIndex))
        {
            throw new InvalidOperationException(
                $"Stop '{gtfsStopId}' is referenced but absent from TransitNetwork.Stops.");
        }

        return stopIndex;
    }

    private static string CreateStopsSignature(IReadOnlyList<StopTime> stopTimes)
    {
        // Prefiks długości usuwa dwuznaczność w przypadku nietypowych stop_id.
        return string.Join(
            "|",
            stopTimes.Select(stopTime =>
                $"{stopTime.StopId.Length}:{stopTime.StopId}"));
    }

    private static void ValidateStopPattern(
        TripWithStopTimes item,
        TripWithStopTimes representative)
    {
        if (item.StopTimes.Count != representative.StopTimes.Count)
        {
            throw new InvalidOperationException(
                $"Trip '{item.Trip.Id}' has an inconsistent stop pattern.");
        }

        for (var i = 0; i < item.StopTimes.Count; i++)
        {
            if (item.StopTimes[i].StopId != representative.StopTimes[i].StopId)
            {
                throw new InvalidOperationException(
                    $"Trip '{item.Trip.Id}' has an inconsistent stop pattern.");
            }
        }
    }

    private sealed record RoutePatternKey(
        string GtfsRouteId,
        string StopsSignature);

    private sealed record TripWithStopTimes(
        AlgorithmComparison.Core.Models.Trip Trip,
        IReadOnlyList<StopTime> StopTimes);
}
