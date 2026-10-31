using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.Core.Queries;

namespace AlgorithmComparison.CLI;

public sealed record BenchmarkQueryEntry(
    int Index,
    string TimeWindow,
    RoutingQuery Query);

public sealed record BenchmarkQueryWorkload(
    int MasterSeed,
    int DatasetSeed,
    IReadOnlyList<BenchmarkQueryEntry> Queries,
    string CsvPath);
public static class BenchmarkQueryWorkloadFactory
{
    private const int SecondsPerDay = 24 * 60 * 60;
    private const int MorningStart = 7 * 60 * 60;
    private const int MorningEnd = 9 * 60 * 60;
    private const int EveningStart = 22 * 60 * 60;

    public static int CreateRunSeed(TimeProvider? clock = null)
    {
        Span<byte> material = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(material, (clock ?? TimeProvider.System).GetUtcNow().UtcTicks);
        RandomNumberGenerator.Fill(material[8..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(material)) & int.MaxValue;
        return value == 0 ? 1 : value;
    }

    public static int DeriveDatasetSeed(int masterSeed, string transferCategory, string cityName)
    {
        var seedText = FormattableString.Invariant($"{masterSeed}\n{transferCategory}\n{cityName}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        return BinaryPrimitives.ReadInt32LittleEndian(hash);
    }

    public static BenchmarkQueryWorkload GenerateAndSave(
        TransitNetwork network,
        int queryCount,
        int masterSeed,
        int datasetSeed,
        string transferCategory,
        string cityName,
        string dataRoot,
        DateOnly serviceDate)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cityName);
        if (queryCount < 1)
            throw new ArgumentOutOfRangeException(nameof(queryCount), "Query count must be positive.");

        var stopIds = network.Connections
            .SelectMany(connection => new[] { connection.DepartureStopId, connection.ArrivalStopId })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stopId => stopId, StringComparer.Ordinal)
            .ToArray();
        if (stopIds.Length < 2)
            throw new InvalidOperationException("The selected service date has fewer than two active stops.");

        var morningCount = queryCount / 3;
        var eveningCount = queryCount / 3;
        var allDayCount = queryCount - morningCount - eveningCount;
        var uniqueQueriesPerDay = (long)stopIds.Length * (stopIds.Length - 1) * SecondsPerDay;
        var uniqueQueriesInPeakWindow = (long)stopIds.Length * (stopIds.Length - 1)
            * (MorningEnd - MorningStart);
        if (morningCount > uniqueQueriesInPeakWindow
            || eveningCount > uniqueQueriesInPeakWindow
            || allDayCount > uniqueQueriesPerDay - morningCount - eveningCount)
        {
            throw new InvalidOperationException(
                $"Cannot generate {queryCount} unique queries from {stopIds.Length} active stops " +
                "within the configured time windows.");
        }

        var random = new Random(datasetSeed);
        var usedQueries = new HashSet<(string From, string To, int Departure)>();
        var queries = new List<BenchmarkQueryEntry>(queryCount);

        AddQueries("morning-peak", morningCount, MorningStart, MorningEnd);
        AddQueries("evening-off-peak", eveningCount, EveningStart, SecondsPerDay);
        AddQueries("all-day", allDayCount, 0, SecondsPerDay);

        var reportRoot = Path.GetFullPath(Path.Combine(dataRoot, "..", "Reports", "zapytania"));
        var citySeedDirectory = Path.Combine(
            reportRoot,
            masterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            transferCategory);
        Directory.CreateDirectory(citySeedDirectory);

        var csvPath = Path.Combine(
            citySeedDirectory,
            $"{SanitizeFileName(cityName)}-{queryCount}-{datasetSeed}.csv");
        WriteCsv(csvPath, queries, masterSeed, datasetSeed, serviceDate);

        return new BenchmarkQueryWorkload(masterSeed, datasetSeed, queries, csvPath);

        void AddQueries(string windowName, int count, int minimumTime, int maximumTime)
        {
            for (var i = 0; i < count; i++)
            {
                (string From, string To, int Departure)? selected = null;
                for (var attempt = 0; attempt < 1_000 && selected is null; attempt++)
                {
                    var fromStopId = stopIds[random.Next(stopIds.Length)];
                    string toStopId;
                    do
                    {
                        toStopId = stopIds[random.Next(stopIds.Length)];
                    } while (toStopId == fromStopId);

                    var departureTime = random.Next(minimumTime, maximumTime);
                    var candidate = (fromStopId, toStopId, departureTime);
                    if (usedQueries.Add(candidate))
                        selected = candidate;
                }

                if (selected is null)
                {
                    for (var fromIndex = 0; fromIndex < stopIds.Length && selected is null; fromIndex++)
                        for (var toIndex = 0; toIndex < stopIds.Length && selected is null; toIndex++)
                        {
                            if (fromIndex == toIndex) continue;
                            for (var departureTime = minimumTime;
                                 departureTime < maximumTime && selected is null;
                                 departureTime++)
                            {
                                var candidate = (stopIds[fromIndex], stopIds[toIndex], departureTime);
                                if (usedQueries.Add(candidate))
                                    selected = candidate;
                            }
                        }
                }

                if (selected is not { } query)
                    throw new InvalidOperationException("Could not find another unique benchmark query.");

                queries.Add(new BenchmarkQueryEntry(
                    queries.Count + 1,
                    windowName,
                    new RoutingQuery
                    {
                        SourceStopId = query.From,
                        TargetStopId = query.To,
                        DepartureTime = query.Departure
                    }));
            }
        }
    }

    private static void WriteCsv(
        string path,
        IReadOnlyList<BenchmarkQueryEntry> queries,
        int masterSeed,
        int datasetSeed,
        DateOnly serviceDate)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(
            "query_index,time_window,from_stop_id,to_stop_id,departure_time_seconds," +
            "master_seed,dataset_seed,service_date,footpath_graph");

        foreach (var entry in queries)
        {
            writer.WriteLine(string.Join(",",
                entry.Index,
                Csv(entry.TimeWindow),
                Csv(entry.Query.SourceStopId),
                Csv(entry.Query.TargetStopId),
                entry.Query.DepartureTime,
                masterSeed,
                datasetSeed,
                serviceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                "radius"));
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";
}
