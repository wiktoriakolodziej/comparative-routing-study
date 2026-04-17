using RouteAnalyzer.Core.Algorithms;
using RouteAnalyzer.Core.GtfsParser;
using RouteAnalyzer.Core.Model.GraphModel;
using Xunit;

namespace RouteAnalyzer.Test;

public class DijkstraTests
{
    private Graph BuildGraphFromGtfs()
    {
        var gtfsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), @"Test\TestData");

        var feed = GtfsLoader.LoadGtfsFeed(gtfsFolderPath);

        var builder = new GraphBuilder(feed);

        return builder.Build();
    }

    [Fact]
    public void Should_Find_Path_From_StopA_To_StopC()
    {
        var graph = BuildGraphFromGtfs();
        var dijkstra = new Dijkstra(graph);

        // StopId = 1 → StopA
        // StopId = 3 → StopC
        var result = dijkstra.FindShortestPath(startStop: 1, targetStop: 3, startTime: 0);

        var success = Assert.IsType<SuccessResult<PathResult>>(result);

        Assert.NotNull(success.Result.Path);
        Assert.True(success.Result.Path.Count >= 2);

        Assert.Equal(1, success.Result.Path.First().StopId);
        Assert.Equal(3, success.Result.Path.Last().StopId);
    }

    [Fact]
    public void Should_Return_Correct_TotalTime()
    {
        var graph = BuildGraphFromGtfs();
        var dijkstra = new Dijkstra(graph);

        var result = dijkstra.FindShortestPath(1, 3, 0);

        var success = Assert.IsType<SuccessResult<PathResult>>(result);

        Assert.Equal(29160, success.Result.TotalTime);
    }

    [Fact]
    public void Should_Pick_Earliest_Departure()
    {
        var graph = BuildGraphFromGtfs();
        var dijkstra = new Dijkstra(graph);

        var result = dijkstra.FindShortestPath(1, 3, 0);

        var success = Assert.IsType<SuccessResult<PathResult>>(result);

        var path = success.Result.Path;

        // pierwszy vertex powinien mieć najwcześniejszy Time
        Assert.Equal(28800, path.First().Time);
    }

    [Fact]
    public void Should_Respect_StartTime_And_Choose_Later_Departure()
    {
        var graph = BuildGraphFromGtfs();
        var dijkstra = new Dijkstra(graph);

        // startTime pomiędzy departure z tripów
        var result = dijkstra.FindShortestPath(1, 3, 28850);

        var success = Assert.IsType<SuccessResult<PathResult>>(result);

        var path = success.Result.Path;

        // powinien wybrać trip 1001 (bo 1000 już za wcześnie)
        Assert.Equal(28860, path.First().Time);
    }
}