using RouteAnalyzer.Core.Model.GraphModel;

namespace RouteAnalyzer.Core.Algorithms;

public class Dijkstra(Graph graph) : IRouteFinder
{
    private readonly Dictionary<int, int> _weights = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _previousVertices = new Dictionary<int, int>();
    private readonly PriorityQueue<(int VertexId, int Weight), int> _queue = new PriorityQueue<(int VertexId, int Weight), int>();
    private readonly Graph _graph = graph;

    public Result FindShortestPath(int startStop, int targetStop, int startTime)
    {
        if (!Initialize(startStop, startTime))
            return new InvalidInputResult($"No connections from start stop {startStop} after start time {startTime}");

        var (currentVertexId, currentVertexWeight) = _queue.Dequeue();
        var currentVertex = _graph.GetVertex(currentVertexId);
        while (currentVertex.StopId != targetStop)
        {
            if (_weights[currentVertexId] != currentVertexWeight)
            {
                if(_queue.Count == 0)
                    return new NoPathResult();

                (currentVertexId, currentVertexWeight) = _queue.Dequeue();
                currentVertex = _graph.GetVertex(currentVertexId);
                continue;
            }
            
            RelaxEdges(currentVertexId);

            if(_queue.Count == 0)
                return new NoPathResult();
            (currentVertexId, currentVertexWeight) = _queue.Dequeue();
            currentVertex = _graph.GetVertex(currentVertexId);
        }

        return new SuccessResult<PathResult>(BuildPath(currentVertexId));
    }

    private bool TryFindStartingVertex(int startStop, int startTime)
    {
        var candidates = _graph.Vertices.Where(v => v.StopId == startStop && v.Time >= startTime).ToList();
        var startingVertex = candidates.MinBy(v => v.Time);
        if (startingVertex is null)
            return false;

        _weights[startingVertex.Id] = startingVertex.Time - startTime;
        _queue.Enqueue((startingVertex.Id, _weights[startingVertex.Id]), _weights[startingVertex.Id]);
        return true;
    }

    private bool Initialize(int startStop, int startTime)
    {
        _weights.Clear();
        _previousVertices.Clear();
        _queue.Clear();

        foreach (var vertex in _graph.Vertices)
        {
            _weights[vertex.Id] = int.MaxValue;
        }

        return TryFindStartingVertex(startStop, startTime);
    }

    private void RelaxEdges(int currentVertexId)
    {
        var edges = _graph.GetEdges(currentVertexId);
        var currentWeight = _weights[currentVertexId];
        foreach (var edge in edges)
        {
            var newWeight = currentWeight + edge.Weight;
            if(newWeight >= _weights[edge.To])
                continue;
           
            _weights[edge.To] = newWeight;
            _previousVertices[edge.To] = currentVertexId;
            _queue.Enqueue((edge.To, newWeight), newWeight);
        }
    }

    private PathResult BuildPath(int targetVertexId)
    {
        var path = new List<Vertex>{ _graph.GetVertex(targetVertexId) };
        var totalTime = _weights[targetVertexId];
        while (_previousVertices.TryGetValue(targetVertexId, out var previousVertexId))
        {
            path.Add(_graph.GetVertex(previousVertexId));
            targetVertexId = previousVertexId;
        }
        path.Reverse();

        return new PathResult
        {
            Path = path,
            TotalTime = totalTime
        };
    }
}

