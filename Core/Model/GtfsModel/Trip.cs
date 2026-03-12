namespace RouteAnalyzer.Core.Model.GtfsModel;

//this class represents a trip in the GTFS data. It contains the trip's ID, route ID, and service ID.
public class Trip
{
    public required int Id { get; init; }
    public required int RouteId { get; set; }
    public required int ServiceId { get; set; }
}

