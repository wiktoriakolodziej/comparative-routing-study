using RouteAnalyzer.Core.Model.GtfsModel;

namespace RouteAnalyzer.Core.Model.Connection;


/// <summary>
/// Buduje posortowaną listę połączeń (Connection[]) z surowych danych GTFS.
///
/// Każdy kurs (trip) w stop_times.txt to sekwencja przystanków.
/// Z każdej pary sąsiednich przystanków w kursie tworzymy jedno Connection.
/// Na koniec sortujemy całość po czasie odjazdu – tego wymaga CSA.
/// </summary>
public static class ConnectionBuilder
{
    public static Connection[] Build(List<StopTime> stopTimes)
    {
        // Krok 1: zgrupuj czasy przystanków według kursu i posortuj po stop_sequence.
        var byTrip = stopTimes
            .GroupBy(st => st.TripId)
            .Select(g => g.OrderBy(st => st.StopSequence).ToList());

        var connections = new List<Connection>();

        // Krok 2: dla każdego kursu zamień sąsiednie pary przystanków na Connection.
        foreach (var tripStops in byTrip)
        {
            for (int i = 0; i < tripStops.Count - 1; i++)
            {
                var from = tripStops[i];
                var to = tripStops[i + 1];

                // Pomijamy ewentualne błędy danych (czas odjazdu > przyjazdu).
                if (from.DepartureTime > to.ArrivalTime) continue;

                connections.Add(new Connection(
                    TripId: from.TripId,
                    DepartureStopId: from.StopId,
                    ArrivalStopId: to.StopId,
                    DepartureTime: from.DepartureTime,
                    ArrivalTime: to.ArrivalTime));
            }
        }

        // Krok 3: sortuj rosnąco po DepartureTime – KLUCZOWY wymóg CSA.
        connections.Sort((a, b) => a.DepartureTime.CompareTo(b.DepartureTime));

        return connections.ToArray();
    }
}
