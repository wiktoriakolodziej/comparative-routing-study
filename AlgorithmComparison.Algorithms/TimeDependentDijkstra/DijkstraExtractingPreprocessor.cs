using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using System.Diagnostics;

namespace AlgorithmComparison.Algorithms.TimeDependentDijkstra;

public sealed class DijkstraExtractingPreprocessor
    : IExtractingPreprocessor<TDDijkstraInput>
{
    private TDDijkstraInput? value;

    public bool IsPreprocessed => value is not null;

    public TimeSpan PreprocessingTime { get; private set; }

    public TDDijkstraInput GetValue()
    {
        return value ?? throw new InvalidOperationException(
            "Dijkstra preprocessing has not been performed yet.");
    }

    public void Preprocess(TransitNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (IsPreprocessed)
        {
            throw new InvalidOperationException(
                "Dijkstra preprocessor can only be run once.");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var graph = new Graph
            {
                Stops = network.Stops.Values
                    .OrderBy(stop => stop.Id, StringComparer.Ordinal)
                    .ToList()
            };

            BuildStopEntries(graph);
            BuildTransitEdges(network, graph);

            value = new TDDijkstraInput
            {
                Graph = graph
            };
        }
        finally
        {
            stopwatch.Stop();
            PreprocessingTime = stopwatch.Elapsed;
        }
    }

    private static void BuildStopEntries(Graph graph)
    {
        var stopIndex = 0;
        foreach (var stop in graph.Stops)
        {
            graph.StopIdToIndex.Add(stop.Id, stopIndex);
            graph.OutgoingEdges.Add(stopIndex, []);
            graph.OriginalStopIds.Add(stopIndex, stop.Id);
            stopIndex++;
        }
    }

    private static void BuildTransitEdges(
        TransitNetwork network,
        Graph graph)
    {
        var tripIdToIndex = network.Trips.Keys
            .OrderBy(tripId => tripId, StringComparer.Ordinal)
            .Select((tripId, index) => (tripId, index))
            .ToDictionary(pair => pair.tripId, pair => pair.index);

        var connectionGroups = network.Connections
            .GroupBy(connection => new
            {
                FromStop = graph.StopIdToIndex[connection.DepartureStopId],
                ToStop = graph.StopIdToIndex[connection.ArrivalStopId]
            });

        foreach (var group in connectionGroups)
        {
            var connections = group
                .Select(connection => new Connection
                {
                    DepartureTime = connection.DepartureTime,
                    ArrivalTime = connection.ArrivalTime,
                    TripId = tripIdToIndex[connection.TripId]
                })
                .OrderBy(connection => connection.DepartureTime)
                .ThenBy(connection => connection.ArrivalTime)
                .ToList();


            AddEdge(graph, new Edge
            {
                FromStop = group.Key.FromStop,
                ToStop = group.Key.ToStop,
                Connections = connections
            });
        }
    }

    private static void AddEdge(Graph graph, Edge edge)
    {
        if (!graph.OutgoingEdges.TryGetValue(edge.FromStop, out var edges))
        {
            throw new InvalidOperationException(
                $"Stop '{edge.FromStop}' is referenced by an edge but is absent from the graph.");
        }

        edges.Add(edge);
    }

}
