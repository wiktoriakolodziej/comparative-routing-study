namespace RouteAnalyzer.Test;

using Core.Model.Connection;
using Core.Model.GtfsModel;
using Xunit;


public class ConnectionBuilderTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static StopTime ST(int tripId, int stopId, int dep, int arr, int seq) =>
        new()
        {
            TripId = tripId,
            StopId = stopId,
            DepartureTime = dep,
            ArrivalTime = arr,
            StopSequence = seq
        };

    // ── podstawowe przypadki ──────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmptyArray()
    {
        var result = ConnectionBuilder.Build([]);

        Assert.Empty(result);
    }

    [Fact]
    public void SingleStopTime_ProducesNoConnections()
    {
        // Jeden przystanek w kursie → brak pary sąsiednich → brak Connection.
        var result = ConnectionBuilder.Build([ST(1, 10, 3600, 3600, 1)]);

        Assert.Empty(result);
    }

    [Fact]
    public void TwoStopTimes_ProducesOneConnection()
    {
        var input = new List<StopTime>
        {
            ST(1, 10, dep: 3600, arr: 3600, seq: 1),
            ST(1, 20, dep: 4200, arr: 4200, seq: 2),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Single(result);
        var c = result[0];
        Assert.Equal(1, c.TripId);
        Assert.Equal(10, c.DepartureStopId);
        Assert.Equal(20, c.ArrivalStopId);
        Assert.Equal(3600, c.DepartureTime);
        Assert.Equal(4200, c.ArrivalTime);
    }

    [Fact]
    public void ThreeStopTimes_ProducesTwoConnections()
    {
        var input = new List<StopTime>
        {
            ST(1, 10, 3600, 3600, 1),
            ST(1, 20, 4200, 4200, 2),
            ST(1, 30, 4800, 4800, 3),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Equal(2, result.Length);
        Assert.Equal(10, result[0].DepartureStopId);
        Assert.Equal(20, result[0].ArrivalStopId);
        Assert.Equal(20, result[1].DepartureStopId);
        Assert.Equal(30, result[1].ArrivalStopId);
    }

    // ── sortowanie ────────────────────────────────────────────────────────────

    [Fact]
    public void Output_IsSortedByDepartureTimeAscending()
    {
        // Dwa kursy z odjazdami w odwrotnej kolejności – po budowaniu muszą być
        // posortowane, bo tego wymaga CSA.
        var input = new List<StopTime>
        {
            ST(tripId: 2, stopId: 10, dep: 7200, arr: 7200, seq: 1),
            ST(tripId: 2, stopId: 20, dep: 7800, arr: 7800, seq: 2),

            ST(tripId: 1, stopId: 30, dep: 3600, arr: 3600, seq: 1),
            ST(tripId: 1, stopId: 40, dep: 4200, arr: 4200, seq: 2),
        };

        var result = ConnectionBuilder.Build(input);

        for (int i = 1; i < result.Length; i++)
            Assert.True(result[i - 1].DepartureTime <= result[i].DepartureTime,
                $"Połączenie [{i - 1}] ma DepartureTime {result[i - 1].DepartureTime} " +
                $"> [{i}] {result[i].DepartureTime} – tablica nie jest posortowana.");
    }

    [Fact]
    public void MultipleTrips_ConnectionsFromBothTripsPresent()
    {
        var input = new List<StopTime>
        {
            ST(1, 10, 3600, 3600, 1),
            ST(1, 20, 4200, 4200, 2),

            ST(2, 30, 5000, 5000, 1),
            ST(2, 40, 5600, 5600, 2),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Equal(2, result.Length);
        Assert.Contains(result, c => c.TripId == 1);
        Assert.Contains(result, c => c.TripId == 2);
    }

    // ── stop_sequence nie musi być podana w kolejności ────────────────────────

    [Fact]
    public void StopTimesOutOfSequenceOrder_AreReorderedCorrectly()
    {
        // stop_times.txt w pliku GTFS nie musi być posortowane po stop_sequence.
        var input = new List<StopTime>
        {
            ST(1, 20, dep: 4200, arr: 4200, seq: 2),   // celowo jako pierwsza
            ST(1, 10, dep: 3600, arr: 3600, seq: 1),
            ST(1, 30, dep: 4800, arr: 4800, seq: 3),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Equal(2, result.Length);
        // Pierwsza Connection musi wychodzić z przystanku seq=1, nie seq=2.
        Assert.Equal(10, result[0].DepartureStopId);
        Assert.Equal(20, result[0].ArrivalStopId);
    }

    // ── filtrowanie błędnych danych ───────────────────────────────────────────

    [Fact]
    public void ConnectionWhereDepAfterArr_IsSkipped()
    {
        // Błąd w danych: odjazd po przyjeździe następnego przystanku – pomijamy.
        var input = new List<StopTime>
        {
            ST(1, 10, dep: 5000, arr: 5000, seq: 1),
            ST(1, 20, dep: 4000, arr: 4000, seq: 2),   // arr < dep poprzedniego
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Empty(result);
    }

    [Fact]
    public void ValidAndInvalidConnections_OnlyValidOnesKept()
    {
        var input = new List<StopTime>
        {
            ST(1, 10, dep: 3600, arr: 3600, seq: 1),
            ST(1, 20, dep: 4200, arr: 4200, seq: 2),   // OK: 3600 → 4200
            ST(1, 30, dep: 4000, arr: 4000, seq: 3),   // błąd: dep(4000) < arr(4200)
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Single(result);
        Assert.Equal(10, result[0].DepartureStopId);
        Assert.Equal(20, result[0].ArrivalStopId);
    }

    // ── kursy nocne (GTFS dopuszcza czas > 24:00) ────────────────────────────

    [Fact]
    public void NightTrip_TimesAbove24Hours_HandledCorrectly()
    {
        // "25:30:00" = 91800 s, "26:00:00" = 93600 s
        var input = new List<StopTime>
        {
            ST(1, 10, dep: 91800, arr: 91800, seq: 1),
            ST(1, 20, dep: 93600, arr: 93600, seq: 2),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.Single(result);
        Assert.Equal(91800, result[0].DepartureTime);
        Assert.Equal(93600, result[0].ArrivalTime);
    }

    // ── TripId jest prawidłowo przepisywany ───────────────────────────────────

    [Fact]
    public void TripId_IsCopiedToEachConnection()
    {
        var input = new List<StopTime>
        {
            ST(tripId: 99, stopId: 1, dep: 1000, arr: 1000, seq: 1),
            ST(tripId: 99, stopId: 2, dep: 2000, arr: 2000, seq: 2),
            ST(tripId: 99, stopId: 3, dep: 3000, arr: 3000, seq: 3),
        };

        var result = ConnectionBuilder.Build(input);

        Assert.All(result, c => Assert.Equal(99, c.TripId));
    }
}
