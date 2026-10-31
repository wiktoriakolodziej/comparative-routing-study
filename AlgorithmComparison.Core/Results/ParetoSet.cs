using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Results;

public sealed class ParetoSet
{
    private readonly List<Journey> _journeys = [];
    public static readonly ParetoSet Empty = new();
    public IReadOnlyList<Journey> Journeys => _journeys;
    public int Count => _journeys.Count;
    public bool TryAdd(Journey candidate)
    {
        if (IsDominated(candidate))
        {
            return false;
        }

        _journeys.RemoveAll(existing =>
            candidate.ArrivalTime <= existing.ArrivalTime &&
            candidate.NumberOfTransfers <= existing.NumberOfTransfers);

        _journeys.Add(candidate);

        _journeys.Sort((left, right) =>
            left.ArrivalTime.CompareTo(right.ArrivalTime));

        return true;
    }
    public bool IsDominated(Journey candidate)
    {
        return _journeys.Any(existing =>
            existing.ArrivalTime <= candidate.ArrivalTime &&
            existing.NumberOfTransfers <= candidate.NumberOfTransfers);
    }
}
