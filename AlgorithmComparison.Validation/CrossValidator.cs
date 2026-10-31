using AlgorithmComparison.Core.Contracts;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;

namespace AlgorithmComparison.Validation;

public sealed class CrossValidator
{
    private readonly IReadOnlyList<IRoutingAlgorithm> _algorithms;
    public CrossValidator(IReadOnlyList<IRoutingAlgorithm> algorithms)
    {
        if (algorithms.Count < 2)
            throw new ArgumentException("At least two algorithms are required for cross-validation.", nameof(algorithms));
        _algorithms = algorithms;
    }
    public ValidationReport Validate(QueryWorkload workload, TransitNetwork network)
    {
        var failures = new List<ValidationFailure>();
        int passed = 0;
        int counter = 1;

        foreach (var query in workload.Queries)
        {
            var arrivalByAlgorithm = new Dictionary<string, int>();
            var resultByAlgorithm = new Dictionary<string, Core.Results.RoutingResult>();

            foreach (var algorithm in _algorithms)
            {
                var result = algorithm.Route(query, network);
                int arrival = ArrivalTimeOf(query, result);
                arrivalByAlgorithm[algorithm.Name] = arrival;
                resultByAlgorithm[algorithm.Name] = result;
            }
            Console.WriteLine($"Query: {counter}, Arrival Times: {string.Join(", ", arrivalByAlgorithm)}");
            counter++;

            int first = arrivalByAlgorithm.Values.First();
            bool agree = arrivalByAlgorithm.Values.All(a => a == first);

            if (agree)
            {
                passed++;
            }
            else
            {
                string description = first == int.MaxValue || arrivalByAlgorithm.Values.Any(a => a == int.MaxValue)
                    ? "Algorithms disagree on reachability."
                    : "Algorithms disagree on earliest arrival time.";

                failures.Add(new ValidationFailure
                {
                    Query = query,
                    ArrivalTimeByAlgorithm = arrivalByAlgorithm,
                    ResultByAlgorithm = resultByAlgorithm,
                    Description = description,
                });
            }
        }

        return new ValidationReport
        {
            TotalQueries = workload.Queries.Count,
            PassedQueries = passed,
            Failures = failures,
        };
    }
    private static int ArrivalTimeOf(RoutingQuery query, Core.Results.RoutingResult result)
    {
        if (!query.MultiCriteria)
            return result.BestJourney?.ArrivalTime ?? int.MaxValue;

        return result.ParetoJourneys.Count == 0
            ? int.MaxValue
            : result.ParetoJourneys.Journeys.Min(j => j.ArrivalTime);
    }
}
