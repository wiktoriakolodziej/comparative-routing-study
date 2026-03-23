namespace RouteAnalyzer.Core.Model.GraphModel;

public class Edge
{
    public required int From { get; init; }
    public required int To { get; init; }
    public required int Weight { get; set; }
    public required EdgeType Type { get; init; }
}

public enum EdgeType
{
    Travel,
    Wait
}

