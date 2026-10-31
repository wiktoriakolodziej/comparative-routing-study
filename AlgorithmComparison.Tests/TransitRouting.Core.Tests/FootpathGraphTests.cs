using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Tests.TransitRouting.Core.Tests;

public class FootpathGraphTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void DirectedTransfer_HasTheSameDurationInBothEnumerations(int duration)
    {
        IFootpathGraph graph = CreateGraph(new Footpath
        {
            FromStopId = "A",
            ToStopId = "B",
            DurationSeconds = duration
        });

        Assert.True(graph.TryGetDuration("A", "B", out var actual));
        Assert.Equal(duration, actual);
        Assert.Equal(duration, Assert.Single(graph.GetOutgoingFootpaths("A"), f => f.ToStopId == "B").DurationSeconds);
        Assert.Equal(duration, Assert.Single(graph.GetIncomingFootpaths("B"), f => f.FromStopId == "A").DurationSeconds);
        Assert.False(graph.TryGetDuration("B", "A", out _));
        Assert.DoesNotContain(graph.GetOutgoingFootpaths("B"), f => f.ToStopId == "A");
        Assert.DoesNotContain(graph.GetIncomingFootpaths("A"), f => f.FromStopId == "B");
    }

    [Fact]
    public void IsolatedStop_HasOnlyAZeroDurationSelfTransfer()
    {
        IFootpathGraph graph = CreateGraph();

        Assert.True(graph.TryGetDuration("A", "A", out var duration));
        Assert.Equal(0, duration);
        var outgoing = Assert.Single(graph.GetOutgoingFootpaths("A"));
        var incoming = Assert.Single(graph.GetIncomingFootpaths("A"));
        Assert.Equal(("A", "A", 0), (outgoing.FromStopId, outgoing.ToStopId, outgoing.DurationSeconds));
        Assert.Equal(("A", "A", 0), (incoming.FromStopId, incoming.ToStopId, incoming.DurationSeconds));
        Assert.False(graph.TryGetDuration("A", "B", out duration));
        Assert.Equal(0, duration);
    }

    [Fact]
    public void UnknownStop_HasNoTransferIncludingToItself()
    {
        IFootpathGraph graph = CreateGraph();

        Assert.False(graph.TryGetDuration("missing", "missing", out _));
        Assert.False(graph.TryGetDuration("A", "missing", out _));
        Assert.False(graph.TryGetDuration("missing", "A", out _));
        Assert.Empty(graph.GetOutgoingFootpaths("missing"));
        Assert.Empty(graph.GetIncomingFootpaths("missing"));
    }

    [Fact]
    public void StoredSelfTransfer_IsNotReturnedTwice()
    {
        IFootpathGraph graph = CreateGraph(new Footpath
        {
            FromStopId = "A",
            ToStopId = "A",
            DurationSeconds = 0
        });

        Assert.Single(graph.GetOutgoingFootpaths("A"));
        Assert.Single(graph.GetIncomingFootpaths("A"));
    }

    private static FootpathGraph CreateGraph(params Footpath[] edges) => new()
    {
        Nodes = new[] { "A", "B" }.ToDictionary(id => id, id => new Node { StopId = id }),
        Edges = edges,
        OutgoingEdges = edges.GroupBy(f => f.FromStopId).ToDictionary(g => g.Key, g => g.ToHashSet()),
        IncomingEdges = edges.GroupBy(f => f.ToStopId).ToDictionary(g => g.Key, g => g.ToHashSet())
    };
}
