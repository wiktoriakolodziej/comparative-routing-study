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
        var edges = GetEdges(vertexId);

        return edges.Where(x => x.Type == EdgeType.Wait).ToList();
    }

    public Vertex GetVertex(int id)
    {
        return Vertices[id];
    }
}

