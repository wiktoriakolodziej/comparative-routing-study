using RouteAnalyzer.Core.GtfsParser;
using RouteAnalyzer.Core.Model.GraphModel;
using RouteAnalyzer.Core.Model.GtfsModel;
using Xunit;

namespace RouteAnalyzer.Test;

public class GraphBuilderTests
{
    [Fact]
    public void BuildGraph_CreatesVerticesAndEdgesCorrectly()
    {
        // 1. Przygotowanie danych testowych (GtfsFeed)
        var gtfsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), @"Test\TestData");
        GtfsFeed feed = GtfsLoader.LoadGtfsFeed(gtfsFolderPath);

        var builder = new GraphBuilder(feed);

        // 2. Budowanie grafu
        Graph graph = builder.Build();

        // 3. Sprawdzenie liczby wierzchołków
        Assert.Equal(5, graph.Vertices.Count);

        // 4. Sprawdzenie travel edges
        var edge = graph.GetEdges(graph.Vertices[0].Id).FirstOrDefault();
        Assert.NotNull(edge);
        Assert.Equal(graph.Vertices[0].Id, edge.From);
        Assert.Equal(graph.Vertices[1].Id, edge.To);
        Assert.Equal(300, edge.Weight); // 08:05 - 08:00 = 300 sekund

        // 5. Sprawdzenie waiting edges (na tym samym przystanku)
        var waitingEdges = graph.GetWaitingEdges(graph.Vertices[0].Id);
        Assert.Equal(1, waitingEdges.Count);
    }
}

