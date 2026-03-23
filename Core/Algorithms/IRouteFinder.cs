using RouteAnalyzer.Core.Model.GraphModel;

namespace RouteAnalyzer.Core.Algorithms;

public interface IRouteFinder
{
    public Result FindShortestPath(int startStop, int targetStop, int startTime);
}
public class PathResult
{
    public List<Vertex> Path { get; set; } = [];
    public int TotalTime { get; set; }
}

public abstract record Result;

public record ErrorResult(string ErrorMessage) : Result;
public record NoPathResult() : ErrorResult("No path found");
public record InvalidInputResult(string Details) : ErrorResult($"Invalid input: {Details}");

public record SuccessResult<T>(T Result) : Result;

