using System.Reflection;
using AlgorithmComparison.Algorithms.Raptor;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;
using AlgorithmComparison.RaptorRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Fact]
    public void RealtimeInheritsTheOriginalRoundSearchWithoutOverridingIt()
    {
        Assert.True(typeof(RealtimeRaptorAlgorithm).IsSubclassOf(typeof(RaptorAlgorithm)));
        var method = typeof(RealtimeRaptorAlgorithm).GetMethod("RunCore", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(typeof(RaptorAlgorithm), method!.DeclaringType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedSearchMatchesStaticResultsAndUsesUpdatesThroughEveryEntryPoint(bool pareto)
    {
        var net = Network(Trip("first", ("A", 100), ("B", 150)),
            Trip("second", ("B", 160), ("C", 200)), Trip("direct", ("A", 110), ("C", 300)));
        var graph = Paths(net);
        var original = new RaptorAlgorithm(graph);
        var realtime = Router(net);
        var query = Query(pareto: pareto);
        Assert.Equal(Criteria(original.Route(query, net)), Criteria(realtime.RouteDetailed(query).Routing));
        realtime.ApplyFeed(Feed(Delay("first", 0, 30)));
        Assert.Equal(new[] { (300, 0) }, Criteria(realtime.RouteDetailed(query).Routing));
        RaptorAlgorithm asBase = realtime;
        IRoutingAlgorithm asInterface = realtime;
        Assert.Equal(new[] { (300, 0) }, Criteria(asBase.Route(query, net)));
        Assert.Equal(new[] { (300, 0) }, Criteria(asInterface.Route(query, net)));
        Assert.Equal(200, original.Route(Query(), net).BestJourney!.ArrivalTime);
        realtime.ApplyFeed(Feed());
        Assert.Equal(Criteria(original.Route(query, net)), Criteria(asBase.Route(query, net)));
    }

    [Fact]
    public void CanceledSharedSearchCanBeStartedAgainWithoutKeepingQueryState()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = Router(net);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => router.RouteDetailed(Query(), canceled.Token));
        Assert.Equal(200, router.RouteDetailed(Query()).Routing.BestJourney!.ArrivalTime);
        Assert.NotNull(router.LastMetrics);
        Assert.Equal(router.Name, router.LastMetrics!.AlgorithmName);
    }

    [Fact]
    public void SharedSearchCapsHugeTransferBudgetAndHandlesOnlyWalkingAfterCancellation()
    {
        var net = Network(Trip("t", ("A", 100), ("C", 200)));
        var router = new RealtimeRaptorAlgorithm(net, Paths(net, ("A", "C", 400)), time: new Clock(Now));
        Assert.Equal(200, router.RouteDetailed(Query(maxTransfers: int.MaxValue)).Routing.BestJourney!.ArrivalTime);
        var canceled = Update("t");
        canceled.Trip.ScheduleRelationship = TransitRealtime.TripDescriptor.Types.ScheduleRelationship.Canceled;
        router.ApplyFeed(Feed(canceled));
        Assert.Equal(400, router.RouteDetailed(Query(maxTransfers: int.MaxValue)).Routing.BestJourney!.ArrivalTime);
    }

    private static (int Arrival, int Transfers)[] Criteria(RoutingResult result) =>
        (result.BestJourney is { } best ? new[] { best } : result.ParetoJourneys.Journeys)
            .Select(j => (j.ArrivalTime, j.NumberOfTransfers)).OrderBy(j => j.ArrivalTime).ToArray();
}
