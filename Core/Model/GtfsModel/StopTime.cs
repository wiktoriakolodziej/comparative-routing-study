namespace RouteAnalyzer.Core.Model.GtfsModel;

public class StopTime
{
    public required int TripId { get; init; }
    public required int StopId { get; init; }
    public required int ArrivalTime { get; set; }
    public required int DepartureTime { get; set; }
    public required int StopSequence { get; set; }
}

