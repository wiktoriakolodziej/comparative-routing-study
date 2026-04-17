using RouteAnalyzer.Core.GtfsParser;
using RouteAnalyzer.Core.Model.GtfsModel;
using Xunit;

namespace RouteAnalyzer.Test;

public class GtfsLoaderAndGraphTests
{
    [Theory]
    [InlineData(@"Test\TestData")]
    public void LoadGtfsAndBuildGraph_CheckVertexAndEdgesCount(string gtfsFolderPath)
    {
        gtfsFolderPath = Path.Combine(Directory.GetCurrentDirectory(), gtfsFolderPath);
        Assert.True(Directory.Exists(gtfsFolderPath), $"Folder {gtfsFolderPath} nie istnieje.");

        // Uruchamiamy loader
        GtfsFeed feed = GtfsLoader.LoadGtfsFeed(gtfsFolderPath);

        // Sprawdzenie podstawowej poprawności wczytania danych

        // Przystanki
        Assert.NotEmpty(feed.Stops);
        foreach (var stop in feed.Stops.Values)
        {
            Assert.False(string.IsNullOrEmpty(stop.Name));
        }

        // Trips
        Assert.NotEmpty(feed.Trips);
        foreach (var trip in feed.Trips.Values)
        {
            Assert.True(trip.RouteId > 0);
            Assert.True(trip.ServiceId > 0);
        }

        // StopTimes
        Assert.NotEmpty(feed.StopTimes);
        foreach (var st in feed.StopTimes)
        {
            Assert.True(feed.Stops.ContainsKey(st.StopId));
            Assert.True(feed.Trips.ContainsKey(st.TripId));
        }

        // Sprawdzenie słowników StopTimesByStopId i StopTimesByTripId
        foreach (var kv in feed.StopTimesByStopId)
        {
            Assert.All(kv.Value, st => Assert.Equal(kv.Key, st.StopId));
        }
        foreach (var kv in feed.StopTimesByTripId)
        {
            Assert.All(kv.Value, st => Assert.Equal(kv.Key, st.TripId));
        }
    }
}
