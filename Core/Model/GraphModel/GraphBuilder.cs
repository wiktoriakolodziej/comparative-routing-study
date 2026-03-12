using RouteAnalyzer.Core.Model.GtfsModel;

namespace RouteAnalyzer.Core.Model.GraphModel;

public class GraphBuilder(GtfsFeed feed)
{
    private readonly GtfsFeed feed = feed;
    private readonly Graph graph = new Graph();
    public Graph Build()
    {
       BuildVertices();
       BuildRideEdges();
       BuildWaitingEdges();
       return graph;
    }

    private void BuildVertices()
    {
        var vertexIdentifier = 0;
        foreach (var stopTime in feed.StopTimes)
        {
            graph.Vertices.Add(new Vertex()
            {
                Id = vertexIdentifier,
                StopId = stopTime.StopId,
                Time = stopTime.ArrivalTime,
                TripId = stopTime.TripId
            });
            graph.Adjacency[vertexIdentifier] = [];
            vertexIdentifier++;
        }
    }

    private void BuildRideEdges()
    {
        foreach (var stopTimes in feed.StopTimesByTripId.Values)
        {
            stopTimes.Sort((a, b) => a.StopSequence.CompareTo((b.StopSequence)));
            for (var i = 0; i < stopTimes.Count - 1; i++)
            {
                var fromVertexId = graph.Vertices.First(x => x.StopId == stopTimes[i].StopId && x.TripId == stopTimes[i].TripId).Id;
                var toVertexId = graph.Vertices.First(x => x.StopId == stopTimes[i + 1].StopId && x.TripId == stopTimes[i + 1].TripId).Id;
                var weight = stopTimes[i + 1].ArrivalTime - stopTimes[i].ArrivalTime;
                graph.Adjacency[fromVertexId].Add(new Edge()
                {
                    From = fromVertexId,
                    To = toVertexId,
                    Weight = weight
                });
            }
        }
    }

    private void BuildWaitingEdges()
    {
        foreach (var stopTimes in feed.StopTimesByStopId.Values)
        {
            stopTimes.Sort((a,b) => a.ArrivalTime.CompareTo(b.ArrivalTime));
            for (var i = 0; i < stopTimes.Count - 1; i++)
            {
                var fromVertexId = graph.Vertices.First(x => x.StopId == stopTimes[i].StopId && x.TripId == stopTimes[i].TripId).Id;
                var weight = stopTimes[i + 1].ArrivalTime - stopTimes[i].ArrivalTime;
                graph.Adjacency[fromVertexId].Add(new Edge()
                {
                    From = fromVertexId,
                    To = fromVertexId,
                    Weight = weight
                });
            }
        }
    }
}
