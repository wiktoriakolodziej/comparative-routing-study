namespace AlgorithmComparison.CLI;

public sealed record BenchmarkDataset(
    string CityName,
    string TransferCategory,
    string DirectoryPath,
    IReadOnlyList<string> ValidationIssues)
{
    public bool IsValid => ValidationIssues.Count == 0;
}

public sealed record BenchmarkDatasetDiscoveryResult(
    string DataRoot,
    IReadOnlyList<BenchmarkDataset> Datasets,
    IReadOnlyList<string> DiscoveryIssues);
public static class BenchmarkDatasetDiscovery
{

    private static readonly string[] RequiredGtfsFiles =
    [
        "stops.txt",
        "routes.txt",
        "trips.txt",
        "stop_times.txt"
    ];

    public static BenchmarkDatasetDiscoveryResult Discover(string? applicationDirectory = null)
    {
        var dataRoot = FindDataRoot(applicationDirectory ?? AppContext.BaseDirectory);
        var datasets = new List<BenchmarkDataset>();
        var discoveryIssues = new List<string>();

        if (!Directory.Exists(dataRoot))
        {
            discoveryIssues.Add("The Data directory does not exist.");
            return new BenchmarkDatasetDiscoveryResult(dataRoot, datasets, discoveryIssues);
        }

        // Keep existing category/city identities, and also accept Data/City GTFS
        // folders. New datasets appear in the menu without changing source code.
        foreach (var folder in Directory.EnumerateDirectories(dataRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var children = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (RequiredGtfsFiles.Any(file => File.Exists(Path.Combine(folder, file))) || children.Length == 0)
            {
                datasets.Add(new BenchmarkDataset(Path.GetFileName(folder), "Direct", folder, ValidateFeed(folder)));
                continue;
            }
            foreach (var cityPath in children)
                datasets.Add(new BenchmarkDataset(Path.GetFileName(cityPath), Path.GetFileName(folder), cityPath, ValidateFeed(cityPath)));
        }

        if (datasets.Count == 0)
            discoveryIssues.Add("No city directories were found in Data.");

        return new BenchmarkDatasetDiscoveryResult(dataRoot, datasets, discoveryIssues);
    }

    private static string FindDataRoot(string applicationDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Data");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        return Path.Combine(Path.GetFullPath(applicationDirectory), "Data");
    }

    private static IReadOnlyList<string> ValidateFeed(
        string cityPath)
    {
        var issues = new List<string>();
        foreach (var fileName in RequiredGtfsFiles)
        {
            if (!File.Exists(Path.Combine(cityPath, fileName)))
                issues.Add($"Missing required file {fileName}.");
        }

        if (!File.Exists(Path.Combine(cityPath, "calendar.txt"))
            && !File.Exists(Path.Combine(cityPath, "calendar_dates.txt")))
        {
            issues.Add("Missing both calendar.txt and calendar_dates.txt.");
        }

        return issues;
    }
}

