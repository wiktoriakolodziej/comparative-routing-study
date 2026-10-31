namespace AlgorithmComparison.Core.Models;

public sealed class Journey
{
    public required IReadOnlyList<JourneyLeg> Legs { get; init; }
    public string DepartureStopId => Legs[0].FromStopId;
    public string ArrivalStopId => Legs[^1].ToStopId;
    public int DepartureTime => Legs[0].DepartureTime;
    public int ArrivalTime => Legs[^1].ArrivalTime;
    public int TotalTime => ArrivalTime - DepartureTime;
    public int NumberOfTransfers => Math.Max(0, Legs.Count(l => !l.IsWalk) - 1);
}
