namespace RouteAnalyzer.Core.Model.GraphModel;

public class Vertex
{
    public required int Id { get; init; }
    public required int StopId { get; set; }
    public required int Time { get; set; } //time in seconds since midnight
    public required int TripId { get; set; }
}

