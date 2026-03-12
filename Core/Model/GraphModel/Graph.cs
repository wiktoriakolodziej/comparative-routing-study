namespace RouteAnalyzer.Core.Model.GraphModel;

public class Graph
{
    public Dictionary<int, List<Edge>> Adjacency { get; set; } = new Dictionary<int, List<Edge>>();
    public List<Vertex> Vertices { get; set; } = [];

    public List<Edge> GetEdges(int vertexId)
    {
        return Adjacency.TryGetValue(vertexId, out var edges) ? edges : [];
    }

    public List<Edge> GetWaitingEdges(int vertexId)
    {
        var waitingEdges = new List<Edge>();
        Adjacency.TryGetValue(vertexId, out var edges);
        if(edges == null) return waitingEdges;

        waitingEdges.AddRange(edges.Where(x => x.To.Equals(x.From)));

        return waitingEdges;
    }
}

