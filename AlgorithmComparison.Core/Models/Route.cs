namespace AlgorithmComparison.Core.Models;

public enum RouteType
{
    Tram = 0,
    Subway = 1,
    Rail = 2,
    Bus = 3,
    Ferry = 4,
    CableCar = 5,
    Gondola = 6,
    Funicular = 7,
    Other = 99,
}
public sealed class Route
{
    public required string Id { get; init; }
    public required string ShortName { get; init; }
    public string LongName { get; init; } = string.Empty;
    public required RouteType RouteType { get; init; }
}
