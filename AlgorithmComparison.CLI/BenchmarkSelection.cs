using System.Text.Json;

namespace AlgorithmComparison.CLI;

public static class BenchmarkSelection
{
    private sealed record Preferences(string[] Variants, string[] Datasets);
    public static string DatasetId(BenchmarkDataset dataset) => $"{dataset.TransferCategory}/{dataset.CityName}";
    public static IReadOnlyList<BenchmarkDataset> ResolveDatasets(BenchmarkDatasetDiscoveryResult discovery,
        IReadOnlyList<string>? selected)
    {
        if (selected is null) return discovery.Datasets.Where(d => d.IsValid).ToArray();
        var result = new List<BenchmarkDataset>();
        foreach (var key in selected)
        {
            var matches = discovery.Datasets.Where(d => DatasetId(d).Equals(key, StringComparison.OrdinalIgnoreCase)
                || d.CityName.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new ArgumentException($"Unknown or ambiguous dataset '{key}'. Use category/city.");
            if (!matches[0].IsValid) throw new ArgumentException($"Invalid dataset '{key}': {string.Join("; ", matches[0].ValidationIssues)}");
            if (!result.Contains(matches[0])) result.Add(matches[0]);
        }
        if (result.Count == 0) throw new ArgumentException("Select at least one dataset.");
        return result;
    }
    public static BenchmarkOptions Interactive(BenchmarkOptions options, BenchmarkDatasetDiscoveryResult discovery,
        ICheckboxConsole? terminal = null)
    {
        var available = discovery.Datasets.Where(d => d.IsValid).ToArray();
        if (available.Length == 0) throw new ArgumentException("Brak poprawnych zbiorow GTFS w katalogu Data.");
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(discovery.DataRoot))!, "benchmark-selection.json");
        Preferences? saved = null;
        try { if (File.Exists(path)) saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { Console.WriteLine($"Nie udalo sie odczytac poprzedniego wyboru: {e.Message}"); }
        var variants = BenchmarkAlgorithmCatalog.Variants;
        var preVariants = options.VariantIds ?? saved?.Variants;
        var chosenVariants = CheckboxMenu.Select("Wybierz algorytmy i warianty", variants.Select(v => v.Label).ToArray(),
            variants.Select(v => preVariants is null || preVariants.Contains(v.Id, StringComparer.OrdinalIgnoreCase)), terminal);
        var preDatasets = options.DatasetIds ?? saved?.Datasets;
        var chosenDatasets = CheckboxMenu.Select("Wybierz zbiory danych", available.Select(d => available.Count(other => other.CityName.Equals(d.CityName, StringComparison.OrdinalIgnoreCase)) > 1
                ? $"{d.CityName} ({d.TransferCategory})" : d.CityName).ToArray(),
            available.Select(d => preDatasets is null ? !d.CityName.Equals("New York", StringComparison.OrdinalIgnoreCase)
                : preDatasets.Contains(DatasetId(d), StringComparer.OrdinalIgnoreCase) || preDatasets.Contains(d.CityName, StringComparer.OrdinalIgnoreCase)), terminal);
        var result = options with
        {
            VariantIds = chosenVariants.Select(i => variants[i].Id).ToArray(),
            DatasetIds = chosenDatasets.Select(i => DatasetId(available[i])).ToArray()
        };
        try { File.WriteAllText(path, JsonSerializer.Serialize(new Preferences(result.VariantIds.ToArray(), result.DatasetIds.ToArray()), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"Nie udalo sie zapisac wyboru: {e.Message}"); }
        return result;
    }
}
