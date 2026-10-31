using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Queries;

public sealed class QueryWorkload
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required IReadOnlyList<RoutingQuery> Queries { get; init; }
    public static QueryWorkload CreateRandom(TransitNetwork network, int count, int seed = 42)
        => CreateFromTimeWindow(network, count, seed, minSeconds: 0, maxSeconds: 24 * 3600, "Random");
    public static QueryWorkload CreateMorningPeak(TransitNetwork network, int count, int seed = 42)
        => CreateFromTimeWindow(network, count, seed, minSeconds: 7 * 3600, maxSeconds: 9 * 3600, "MorningPeak");
    public static QueryWorkload CreateOffPeak(TransitNetwork network, int count, int seed = 42)
        => CreateFromTimeWindow(network, count, seed, minSeconds: 22 * 3600, maxSeconds: 24 * 3600, "OffPeak");
    private static QueryWorkload CreateFromTimeWindow(
        TransitNetwork network, int count, int seed, int minSeconds, int maxSeconds, string label)
    {
        var stopIds = GetActiveStopIds(network);
        if (stopIds.Count < 2)
            throw new InvalidOperationException("Network has fewer than 2 stops with active connections — cannot generate queries.");

        var rng = new Random(seed);
        var queries = new List<RoutingQuery>(count);
        for (int i = 0; i < count; i++)
        {
            string source = stopIds[rng.Next(stopIds.Count)];
            string target;
            do { target = stopIds[rng.Next(stopIds.Count)]; }
            while (target == source);

            int departureTime = minSeconds + rng.Next(maxSeconds - minSeconds);

            queries.Add(new RoutingQuery
            {
                SourceStopId = source,
                TargetStopId = target,
                DepartureTime = departureTime,
            });
        }

        return new QueryWorkload
        {
            Name = $"{label}-{count}",
            Description = $"{count} random queries, seed {seed}, departure window [{minSeconds}s, {maxSeconds}s)",
            Queries = queries,
        };
    }
    private static IReadOnlyList<string> GetActiveStopIds(TransitNetwork network)
    {
        var ids = new HashSet<string>();
        foreach (var c in network.Connections)
        {
            ids.Add(c.DepartureStopId);
            ids.Add(c.ArrivalStopId);
        }
        return [.. ids];
    }
}
