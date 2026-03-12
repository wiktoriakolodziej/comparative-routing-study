namespace RouteAnalyzer.Core.Model.GtfsModel;

//this class represents a stop in the GTFS data. It contains the stop's ID, name, latitude, and longitude.
public class Stop
{
    public required int Id { get; init; }
    public required string Name { get; set; }
    public required double Latitude { get; set; }
    public required double Longitude { get; set; }
}
