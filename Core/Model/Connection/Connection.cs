namespace RouteAnalyzer.Core.Model.Connection;

/// <summary>
/// Pojedyncze połączenie (jeden odcinek kursu między dwoma kolejnymi przystankami).
/// Lista wszystkich Connection jest GŁÓWNĄ strukturą CSA – musi być posortowana
/// rosnąco po DepartureTime.
/// </summary>
public sealed record Connection(
    int TripId,
    int DepartureStopId,
    int ArrivalStopId,
    int DepartureTime,   // sekundy od północy
    int ArrivalTime);

