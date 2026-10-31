using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Results;

public sealed class RoutingResult
{
    public Journey? BestJourney { get; init; }
    public ParetoSet ParetoJourneys { get; init; } = ParetoSet.Empty;
    public bool IsReachable => BestJourney is not null || ParetoJourneys.Count > 0;
    public required TimeSpan QueryDuration { get; init; }
}
