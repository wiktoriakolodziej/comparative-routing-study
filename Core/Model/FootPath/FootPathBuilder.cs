using RouteAnalyzer.Core.Model.GtfsModel;

namespace RouteAnalyzer.Core.Model.FootPath;

/// <summary>
/// Buduje graf połączeń pieszych spełniający dwa wymagania CSA:
///
///   1. Domknięcie przechodnie  – jeśli A→B i B→C istnieją,
///      to A→C też musi istnieć (z czasem ≤ czas(A→B) + czas(B→C)).
///
///   2. Nierówność trójkąta    – czas(A→C) ≤ czas(A→B) + czas(B→C)
///      dla każdej trójki przystanków.
///
/// Oba warunki zapewnia algorytm Floyda-Warshalla.
///
/// ─── Skąd wziąć wagi krawędzi? ──────────────────────────────────────────────
///
/// Ponieważ zestaw GTFS nie zawiera transfers.txt, szacujemy czas przejścia
/// pieszego ze wzoru haversine (odległość w linii prostej) podzielonej przez
/// prędkość pieszego.  To podejście ma dwa parametry, które warto dostroić:
///
///   • MaxWalkingDistanceMeters  – próg: przystanki dalej od siebie NIE są
///     połączone w grafie (krawędź startowa nie istnieje).
///     Typowe wartości: 300–600 m.
///
///   • WalkingSpeedMps  – prędkość pieszego w m/s.
///     Przyjmujemy 1,2 m/s ≈ 4,3 km/h (standard planowania transportu).
///
/// ─── Dlaczego Floyd-Warshall, a nie Dijkstra? ────────────────────────────────
///
/// CSA wymaga WSZYSTKICH najkrótszych ścieżek pieszych między WSZYSTKIMI parami
/// przystanków (w obrębie progu odległości).  Floyd-Warshall daje to jednym
/// przebiegiem O(n³), gdzie n to liczba przystanków – dla typowych miast
/// kilkaset do kilku tysięcy węzłów, co jest szybkie.
/// </summary>
public static class FootPathBuilder
{
    // ── Parametry ─────────────────────────────────────────────────────────────

    /// Maksymalna odległość [m], dla której tworzymy krawędź startową w grafie.
    public static double MaxWalkingDistanceMeters { get; set; } = 500.0;

    /// Prędkość pieszego [m/s].
    public static double WalkingSpeedMps { get; set; } = 1.2;

    /// Minimalny czas przesiadki na tym samym przystanku [s].
    /// Gwarantuje, że T(A→A) = MinTransferSeconds (a nie 0).
    public static int MinTransferSeconds { get; set; } = 120;

    // ── Budowanie grafu ───────────────────────────────────────────────────────

    public static Dictionary<int, List<FootPath>> Build(
        Dictionary<int, Stop> stops)
    {
        List<Stop> stopList = stops.Values.ToList();
        int n = stopList.Count;

        // Mapowanie stopId → indeks w macierzy
        var idx = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++)
            idx[stopList[i].Id] = i;

        // ── Krok 1: Inicjalizacja macierzy odległości ─────────────────────────
        // dist[i,j] = czas pieszego [s] lub int.MaxValue/2 jeśli brak krawędzi.
        // Używamy int.MaxValue/2 żeby uniknąć overflow przy sumowaniu.

        const int INF = int.MaxValue / 2;
        var dist = new int[n, n];

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                dist[i, j] = (i == j) ? MinTransferSeconds : INF;

        // ── Krok 2: Krawędzie startowe (haversine ≤ próg) ────────────────────

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double meters = HaversineMeters(stopList[i], stopList[j]);

                if (meters <= MaxWalkingDistanceMeters)
                {
                    int seconds = (int)Math.Ceiling(meters / WalkingSpeedMps);

                    // Graf nieważony kierunkowo (spacer w obie strony tyle samo)
                    dist[i, j] = seconds;
                    dist[j, i] = seconds;
                }
            }
        }

        // ── Krok 3: Floyd-Warshall ────────────────────────────────────────────
        // Po tym kroku dist[i,j] = NAJKRÓTSZY czas pieszy z i do j,
        // z uwzględnieniem przesiadek przez pośrednie przystanki.
        // Gwarantuje domknięcie przechodnie i nierówność trójkąta.

        for (int k = 0; k < n; k++)
            for (int i = 0; i < n; i++)
            {
                if (dist[i, k] == INF) continue;   // optymalizacja

                for (int j = 0; j < n; j++)
                {
                    int via = dist[i, k] + dist[k, j];
                    if (via < dist[i, j])
                        dist[i, j] = via;
                }
            }

        // ── Krok 4: Budowanie słownika FootPath ───────────────────────────────
        // Przechowujemy tylko krawędzie, które faktycznie istnieją (dist < INF).
        // Klucz = stopId źródłowego przystanku.

        var result = new Dictionary<int, List<FootPath>>();

        // Inicjalizuj puste listy dla każdego przystanku
        foreach (var stop in stopList)
            result[stop.Id] = new List<FootPath>();

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (dist[i, j] == INF) continue;

                result[stopList[i].Id].Add(new FootPath(
                    DepartureStopId: stopList[i].Id,
                    ArrivalStopId: stopList[j].Id,
                    DurationSeconds: dist[i, j]));
            }
        }

        return result;
    }

    // ── Odległość haversine ───────────────────────────────────────────────────

    private static double HaversineMeters(Stop a, Stop b)
    {
        const double R = 6_371_000; // promień Ziemi [m]

        double dLat = ToRad(b.Latitude - a.Latitude);
        double dLon = ToRad(b.Longitude - a.Longitude);

        double sinLat = Math.Sin(dLat / 2);
        double sinLon = Math.Sin(dLon / 2);

        double h = sinLat * sinLat
                 + Math.Cos(ToRad(a.Latitude)) * Math.Cos(ToRad(b.Latitude))
                 * sinLon * sinLon;

        return 2 * R * Math.Asin(Math.Sqrt(h));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

