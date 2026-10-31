using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Results;

namespace AlgorithmComparison.Validation;

public static class ParetoValidator
{
    public static IReadOnlyList<string> Validate(ParetoSet paretoSet)
    {
        ArgumentNullException.ThrowIfNull(paretoSet);

        var violations = new List<string>();
        var journeys = paretoSet.Journeys;
        var seen = new HashSet<(int ArrivalTime, int Transfers)>();

        for (var i = 0; i < journeys.Count; i++)
        {
            var journey = journeys[i];
            var key = (journey.ArrivalTime, journey.NumberOfTransfers);
            if (!seen.Add(key))
                violations.Add($"Duplicate Pareto point ({key.ArrivalTime}, {key.NumberOfTransfers}).");

            for (var j = 0; j < journeys.Count; j++)
            {
                if (i == j) continue;
                var other = journeys[j];
                if (other.ArrivalTime <= journey.ArrivalTime
                    && other.NumberOfTransfers <= journey.NumberOfTransfers
                    && (other.ArrivalTime < journey.ArrivalTime
                        || other.NumberOfTransfers < journey.NumberOfTransfers))
                {
                    violations.Add(
                        $"Pareto point ({key.ArrivalTime}, {key.NumberOfTransfers}) is dominated.");
                    break;
                }
            }

            if (i > 0)
            {
                var previous = journeys[i - 1];
                if (journey.ArrivalTime < previous.ArrivalTime)
                    violations.Add("Pareto journeys are not sorted by arrival time.");
                if (journey.NumberOfTransfers > previous.NumberOfTransfers)
                    violations.Add("Transfer counts increase as arrival times increase.");
            }
        }

        return violations;
    }
    public static bool IsSubsetOf(ParetoSet candidate, ParetoSet reference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(reference);

        return candidate.Journeys.All(journey => reference.Journeys.Any(other =>
            other.ArrivalTime <= journey.ArrivalTime
            && other.NumberOfTransfers <= journey.NumberOfTransfers));
    }
}
