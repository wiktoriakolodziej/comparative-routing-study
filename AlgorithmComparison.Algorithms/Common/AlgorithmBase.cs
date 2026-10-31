using System.Diagnostics;
using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.Core.Results;

namespace AlgorithmComparison.Algorithms.Common;

public abstract class AlgorithmBase : IRoutingAlgorithm
{
    private readonly Stopwatch _stopwatch = new();
    private long _allocBefore;

    public abstract string Name { get; }
    public AlgorithmMetrics? LastMetrics { get; private set; }

    // Counter fields set by each subclass during RunCore.
    protected int StopsSettled;
    protected int TripsOrConnectionsScanned;
    protected int Relaxations;
    protected int RaptorRounds;
    public virtual RoutingResult Route(RoutingQuery query, TransitNetwork network)
    {
        StopsSettled = 0;
        TripsOrConnectionsScanned = 0;
        Relaxations = 0;
        RaptorRounds = 0;

        _allocBefore = GC.GetTotalAllocatedBytes(precise: false);
        _stopwatch.Restart();

        var inner = RunCore(query, network);

        _stopwatch.Stop();
        var elapsed = _stopwatch.Elapsed;
        long allocAfter = GC.GetTotalAllocatedBytes(precise: false);

        LastMetrics = new AlgorithmMetrics
        {
            AlgorithmName = Name,
            QueryTimeMs = elapsed.TotalMilliseconds,
            AllocatedBytes = allocAfter - _allocBefore,
            StopsSettled = StopsSettled,
            TripsOrConnectionsScanned = TripsOrConnectionsScanned,
            Relaxations = Relaxations,
            RaptorRounds = RaptorRounds,
        };

        // Wrap result with the measured duration so callers see it in RoutingResult too.
        return new RoutingResult
        {
            BestJourney = inner.BestJourney,
            ParetoJourneys = inner.ParetoJourneys,
            QueryDuration = elapsed,
        };
    }
    protected abstract RoutingResult RunCore(RoutingQuery query, TransitNetwork network);
}
