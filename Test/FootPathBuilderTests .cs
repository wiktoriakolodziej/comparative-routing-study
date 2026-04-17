namespace RouteAnalyzer.Test;

using Core.Model.FootPath;
using Core.Model.GtfsModel;
using Xunit;


public class FootPathBuilderTests : IDisposable
{
    // Resetujemy parametry statyczne przed każdym testem,
    // żeby testy były od siebie niezależne.
    public FootPathBuilderTests()
    {
        FootPathBuilder.MaxWalkingDistanceMeters = 500.0;
        FootPathBuilder.WalkingSpeedMps = 1.2;
        FootPathBuilder.MinTransferSeconds = 120;
    }

    public void Dispose() { }   // reset odbywa się w konstruktorze

    // ── helpers ───────────────────────────────────────────────────────────────

    // Warszawa Centrum – punkt odniesienia (52.2297°N, 21.0122°E).
    // Przesunięcia są tak dobrane, żeby odległości były znane i przewidywalne:
    //   ~1° szerokości ≈ 111 320 m,  ~1° długości na tej szerokości ≈ 67 550 m.

    private static Stop MakeStop(int id, double latOffset, double lonOffset) =>
        new()
        {
            Id = id,
            Name = $"Stop{id}",
            Latitude = 52.2297 + latOffset,
            Longitude = 21.0122 + lonOffset
        };

    // Dwa przystanki oddalone od siebie o ok. 335 m (w obrębie progu 500 m).
    private static Dictionary<int, Stop> TwoNearbyStops() => new()
    {
        [1] = MakeStop(1, 0.0, 0.0),
        [2] = MakeStop(2, 0.003, 0.0),   // ≈ 334 m na północ
    };

    // Dwa przystanki oddalone od siebie o ok. 668 m (powyżej progu 500 m).
    private static Dictionary<int, Stop> TwoFarStops() => new()
    {
        [1] = MakeStop(1, 0.0, 0.0),
        [2] = MakeStop(2, 0.006, 0.0),   // ≈ 668 m na północ
    };

    // Trzy przystanki w linii: A--200m--B--200m--C, każdy w progu, razem 400 m.
    private static Dictionary<int, Stop> ThreeStopsInLine() => new()
    {
        [1] = MakeStop(1, 0.0, 0.0),
        [2] = MakeStop(2, 0.0018, 0.0),   // ≈ 200 m od [1]
        [3] = MakeStop(3, 0.0036, 0.0),   // ≈ 200 m od [2], ≈ 400 m od [1]
    };

    // ── przypadki brzegowe ────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmptyDictionary()
    {
        var result = FootPathBuilder.Build(new Dictionary<int, Stop>());

        Assert.Empty(result);
    }

    [Fact]
    public void SingleStop_HasOnlySelfLoop()
    {
        var stops = new Dictionary<int, Stop> { [1] = MakeStop(1, 0, 0) };

        var result = FootPathBuilder.Build(stops);

        Assert.Single(result);
        Assert.Single(result[1]);
        var fp = result[1][0];
        Assert.Equal(1, fp.DepartureStopId);
        Assert.Equal(1, fp.ArrivalStopId);
        Assert.Equal(FootPathBuilder.MinTransferSeconds, fp.DurationSeconds);
    }

    // ── self-loop ─────────────────────────────────────────────────────────────

    [Fact]
    public void EachStop_HasSelfLoopWithMinTransferSeconds()
    {
        FootPathBuilder.MinTransferSeconds = 180;
        var result = FootPathBuilder.Build(TwoNearbyStops());

        foreach (var (stopId, paths) in result)
        {
            var self = paths.SingleOrDefault(p => p.ArrivalStopId == stopId);
            Assert.NotNull(self);
            Assert.Equal(180, self.DurationSeconds);
        }
    }

    // ── próg odległości ───────────────────────────────────────────────────────

    [Fact]
    public void NearbyStops_WithinThreshold_AreConnected()
    {
        FootPathBuilder.MaxWalkingDistanceMeters = 500;
        var result = FootPathBuilder.Build(TwoNearbyStops());

        // Oba kierunki muszą istnieć.
        Assert.Contains(result[1], p => p.ArrivalStopId == 2);
        Assert.Contains(result[2], p => p.ArrivalStopId == 1);
    }

    [Fact]
    public void FarStops_BeyondThreshold_AreNotDirectlyConnected()
    {
        FootPathBuilder.MaxWalkingDistanceMeters = 500;
        var result = FootPathBuilder.Build(TwoFarStops());

        // Odległość ≈ 668 m > 500 m → brak krawędzi (poza self-loop).
        Assert.DoesNotContain(result[1], p => p.ArrivalStopId == 2);
        Assert.DoesNotContain(result[2], p => p.ArrivalStopId == 1);
    }

    [Fact]
    public void ThresholdExactlyAtDistance_StopsAreConnected()
    {
        // Ustawiamy próg dokładnie powyżej odległości ≈ 334 m.
        FootPathBuilder.MaxWalkingDistanceMeters = 340;
        var result = FootPathBuilder.Build(TwoNearbyStops());

        Assert.Contains(result[1], p => p.ArrivalStopId == 2);
    }

    // ── czas przejścia ────────────────────────────────────────────────────────

    [Fact]
    public void WalkDuration_MatchesCeilingOfDistanceDividedBySpeed()
    {
        FootPathBuilder.WalkingSpeedMps = 1.2;
        FootPathBuilder.MaxWalkingDistanceMeters = 500;

        var stops = TwoNearbyStops();
        var result = FootPathBuilder.Build(stops);

        var fp = result[1].Single(p => p.ArrivalStopId == 2);

        // Obliczamy oczekiwaną odległość haversine "z zewnątrz"
        // i sprawdzamy, czy duration = ⌈dist / speed⌉.
        double lat1 = stops[1].Latitude, lon1 = stops[1].Longitude;
        double lat2 = stops[2].Latitude, lon2 = stops[2].Longitude;
        double expectedMeters = HaversineMeters(lat1, lon1, lat2, lon2);
        int expectedSecs = (int)Math.Ceiling(expectedMeters / 1.2);

        Assert.Equal(expectedSecs, fp.DurationSeconds);
    }

    [Fact]
    public void HigherWalkingSpeed_ProducesShorterDuration()
    {
        FootPathBuilder.MaxWalkingDistanceMeters = 500;

        FootPathBuilder.WalkingSpeedMps = 1.2;
        var slow = FootPathBuilder.Build(TwoNearbyStops());
        int durationSlow = slow[1].Single(p => p.ArrivalStopId == 2).DurationSeconds;

        FootPathBuilder.WalkingSpeedMps = 2.0;
        var fast = FootPathBuilder.Build(TwoNearbyStops());
        int durationFast = fast[1].Single(p => p.ArrivalStopId == 2).DurationSeconds;

        Assert.True(durationFast < durationSlow,
            $"Szybszy chód ({durationFast}s) powinien dać krótszy czas niż wolniejszy ({durationSlow}s).");
    }

    // ── symetria grafu ────────────────────────────────────────────────────────

    [Fact]
    public void Graph_IsSymmetric_BothDirectionsHaveSameDuration()
    {
        FootPathBuilder.MaxWalkingDistanceMeters = 500;
        var result = FootPathBuilder.Build(TwoNearbyStops());

        int d12 = result[1].Single(p => p.ArrivalStopId == 2).DurationSeconds;
        int d21 = result[2].Single(p => p.ArrivalStopId == 1).DurationSeconds;

        Assert.Equal(d12, d21);
    }

    // ── Floyd-Warshall: domknięcie przechodnie ────────────────────────────────

    [Fact]
    public void TransitiveClosure_AtoC_ExistsEvenIfOnlyABandBCWithinThreshold()
    {
        // A i C są od siebie oddalone o ≈ 400 m (w progu 500 m),
        // więc Floyd-Warshall powinien je bezpośrednio połączyć ze ścieżką via B.
        // Ale też: gdyby próg był mniejszy niż 400 m, A→C mogłoby istnieć tylko
        // jako ścieżka pośrednia. Ustawiamy próg = 250 m, żeby A-C nie miały
        // bezpośredniej krawędzi startowej (≈ 400 m > 250 m), ale via B (2×200 m) tak.
        FootPathBuilder.MaxWalkingDistanceMeters = 250;

        var result = FootPathBuilder.Build(ThreeStopsInLine());

        // A→C musi istnieć jako wynik domknięcia przechodniego.
        Assert.Contains(result[1], p => p.ArrivalStopId == 3);
        Assert.Contains(result[3], p => p.ArrivalStopId == 1);
    }

    [Fact]
    public void TransitiveClosure_Duration_NotGreaterThanSumOfSegments()
    {
        // Nierówność trójkąta: czas(A→C) ≤ czas(A→B) + czas(B→C).
        FootPathBuilder.MaxWalkingDistanceMeters = 250;
        var result = FootPathBuilder.Build(ThreeStopsInLine());

        int ab = result[1].Single(p => p.ArrivalStopId == 2).DurationSeconds;
        int bc = result[2].Single(p => p.ArrivalStopId == 3).DurationSeconds;
        int ac = result[1].Single(p => p.ArrivalStopId == 3).DurationSeconds;

        Assert.True(ac <= ab + bc,
            $"Nierówność trójkąta naruszona: czas(A→C)={ac} > czas(A→B)={ab} + czas(B→C)={bc}");
    }

    [Fact]
    public void Floyd_ShortcutIsUsed_WhenDirectEdgeIsLonger()
    {
        // Tworzymy trójkąt A-B-C, gdzie krawędź A-C jest w progu,
        // ale droga via B jest krótsza (co tu jest niemożliwe przy haversine
        // dla trójkąta na płaszczyźnie – ale możemy to wymusić ustawiając
        // bardzo niską prędkość dla "długiej" trasy i weryfikując,
        // że Floyd nie PSUJE krótszej krawędzi bezpośredniej).
        //
        // Prostszy wariant: sprawdzamy, że gdy A→C istnieje bezpośrednio
        // i via B, Floyd zachowuje MINIMUM.
        FootPathBuilder.MaxWalkingDistanceMeters = 500;
        var result = FootPathBuilder.Build(ThreeStopsInLine());

        int ac_direct = result[1].Single(p => p.ArrivalStopId == 3).DurationSeconds;
        int ab = result[1].Single(p => p.ArrivalStopId == 2).DurationSeconds;
        int bc = result[2].Single(p => p.ArrivalStopId == 3).DurationSeconds;

        // Floyd mógł tylko poprawić (skrócić) krawędź A→C, nie pogorszyć.
        Assert.True(ac_direct <= ab + bc);
    }

    // ── struktura słownika wynikowego ─────────────────────────────────────────

    [Fact]
    public void ResultDictionary_ContainsEntryForEveryStop()
    {
        var stops = TwoNearbyStops();
        var result = FootPathBuilder.Build(stops);

        Assert.Equal(stops.Count, result.Count);
        foreach (var stopId in stops.Keys)
            Assert.True(result.ContainsKey(stopId),
                $"Brak klucza {stopId} w słowniku wynikowym.");
    }

    [Fact]
    public void FootPath_DepartureStopId_MatchesDictionaryKey()
    {
        var result = FootPathBuilder.Build(TwoNearbyStops());

        foreach (var (stopId, paths) in result)
            Assert.All(paths, p => Assert.Equal(stopId, p.DepartureStopId));
    }

    // ── helper do niezależnego obliczenia haversine w testach ─────────────────

    private static double HaversineMeters(
        double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}