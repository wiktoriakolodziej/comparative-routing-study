using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Footpaths;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Tests.TransitRouting.Core.Tests;

public class FootpathGraphModelsTests
{
    private static readonly Stop[] Stops =
    [
        new() { Id = "A", Name = "Centrum", Latitude = 0, Longitude = 0 },
        new() { Id = "B", Name = "Centrum", Latitude = 0, Longitude = 0.001 },
        new() { Id = "C", Name = "Dworzec", Latitude = 0, Longitude = 0.002 },
        new() { Id = "D", Name = "centrum", Latitude = 0, Longitude = 0.003 }
    ];

    private static readonly Dictionary<string, string> Groups = new()
    {
        ["A"] = "first",
        ["B"] = "first",
        ["C"] = "first",
        ["D"] = "second"
    };

    [Theory]
    [InlineData("same-name", 2)]
    [InlineData("grouped", 3)]
    [InlineData("full", 4)]
    public void Models_ExposeOnlyAllowedTransfers_WithConsistentDurations(string model, int expectedCount)
    {
        var graph = CreateGraph(model);
        var outgoing = graph.GetOutgoingFootpaths("A").ToArray();
        var incoming = graph.GetIncomingFootpaths("A").ToArray();

        Assert.Equal(expectedCount, outgoing.Length);
        Assert.Equal(expectedCount, incoming.Length);
        foreach (var stop in Stops)
        {
            var expected = model == "full"
                || (model == "same-name" ? stop.Name == "Centrum" : Groups[stop.Id] == "first");
            Assert.Equal(expected, graph.TryGetDuration("A", stop.Id, out var duration));
            Assert.Equal(expected, outgoing.Any(f => f.ToStopId == stop.Id));
            Assert.Equal(expected, incoming.Any(f => f.FromStopId == stop.Id));
            if (!expected)
            {
                Assert.Equal(0, duration);
                continue;
            }

            Assert.Equal(duration, outgoing.Single(f => f.ToStopId == stop.Id).DurationSeconds);
            Assert.Equal(duration, incoming.Single(f => f.FromStopId == stop.Id).DurationSeconds);
            Assert.True(graph.TryGetDuration(stop.Id, "A", out var reverseDuration));
            Assert.Equal(duration, reverseDuration);
        }

        Assert.True(graph.TryGetDuration("A", "B", out var walkingTime));
        Assert.Equal(101, walkingTime); // About 111.2 metres at 1.11 m/s, rounded up.
    }

    [Theory]
    [InlineData("same-name")]
    [InlineData("grouped")]
    [InlineData("full")]
    public void Models_IncludeSelfTransfers_AndRejectUnknownStops(string model)
    {
        var graph = CreateGraph(model);
        foreach (var stop in Stops)
        {
            Assert.True(graph.TryGetDuration(stop.Id, stop.Id, out var duration));
            Assert.Equal(0, duration);
            Assert.Equal(0, Assert.Single(graph.GetOutgoingFootpaths(stop.Id), f => f.ToStopId == stop.Id).DurationSeconds);
            Assert.Equal(0, Assert.Single(graph.GetIncomingFootpaths(stop.Id), f => f.FromStopId == stop.Id).DurationSeconds);
        }

        Assert.False(graph.TryGetDuration("missing", "missing", out _));
        Assert.False(graph.TryGetDuration("A", "missing", out _));
        Assert.False(graph.TryGetDuration("missing", "A", out _));
        Assert.Empty(graph.GetOutgoingFootpaths("missing"));
        Assert.Empty(graph.GetIncomingFootpaths("missing"));
    }

    [Theory]
    [InlineData("same-name")]
    [InlineData("grouped")]
    [InlineData("full")]
    public void Models_PreserveTransitiveClosureAndTriangleInequality(string model)
    {
        var graph = CreateGraph(model);
        foreach (var from in Stops)
            foreach (var via in Stops)
                foreach (var to in Stops)
                {
                    if (!graph.TryGetDuration(from.Id, via.Id, out var first)
                        || !graph.TryGetDuration(via.Id, to.Id, out var second)) continue;

                    Assert.True(graph.TryGetDuration(from.Id, to.Id, out var direct));
                    Assert.True(direct <= first + second);
                }
    }

    [Fact]
    public void GroupedGraph_RequiresAGroupForEveryStop()
    {
        Assert.Throws<ArgumentException>(() => new GroupedFootpathGraph(Stops, new Dictionary<string, string>()));
    }

    [Fact]
    public void GroupedGraph_SnapshotsGroupAssignments()
    {
        var assignments = new Dictionary<string, string>(Groups);
        var graph = new GroupedFootpathGraph(Stops, assignments);
        assignments["D"] = "first";

        Assert.False(graph.TryGetDuration("A", "D", out _));
    }

    [Fact]
    public void FullGraph_AllowsZeroDurationBetweenDistinctStopsAtTheSamePosition()
    {
        var graph = new FullFootpathGraph(
        [
            Stops[0],
            new Stop { Id = "other", Name = "Other", Latitude = 0, Longitude = 0 }
        ]);

        Assert.True(graph.TryGetDuration("A", "other", out var duration));
        Assert.Equal(0, duration);
    }

    private static IFootpathGraph CreateGraph(string model) => model switch
    {
        "same-name" => new SameNameFootpathGraph(Stops),
        "grouped" => new GroupedFootpathGraph(Stops, Groups),
        "full" => new FullFootpathGraph(Stops),
        _ => throw new ArgumentException("Unknown test model.", nameof(model))
    };
}
