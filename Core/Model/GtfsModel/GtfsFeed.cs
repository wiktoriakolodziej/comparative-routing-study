namespace RouteAnalyzer.Core.Model.GtfsModel;

public class GtfsFeed
{
    public Dictionary<int, Stop> Stops { get; set; } = new ();
    public Dictionary<int, Trip> Trips { get; set; } = new ();
    public List<StopTime> StopTimes { get; set; } = [];
    public Dictionary<int, List<StopTime>> StopTimesByStopId { get; set; } = new ();
    public Dictionary<int, List<StopTime>> StopTimesByTripId { get; set; } = new ();
}

