namespace AlgorithmComparison.Core.Models;

public sealed class TransitNetwork
{
    public required IReadOnlyDictionary<string, Stop> Stops { get; init; }
    public required IReadOnlyDictionary<string, Route> Routes { get; init; }
    public required IReadOnlyDictionary<string, Trip> Trips { get; init; }
    public required IReadOnlyList<Connection> Connections { get; init; }
    public required IReadOnlyDictionary<string, Connection> ConnectionDict { get; init; }
    public required DateOnly ServiceDate { get; init; }
}
