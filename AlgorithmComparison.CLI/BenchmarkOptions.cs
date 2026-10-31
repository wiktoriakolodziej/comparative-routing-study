using System.Globalization;
namespace AlgorithmComparison.CLI;

public sealed record BenchmarkOptions(int QueryCount)
{
    public const int DefaultWarmupCount = 3;
    public const int DefaultRepetitions = 5;
    public const int DefaultMaxTransfers = 7;
    public double WalkingRadiusMeters { get; init; } = 500;
    public int WarmupCount { get; init; } = DefaultWarmupCount;
    public int Repetitions { get; init; } = DefaultRepetitions;
    public int MaxTransfers { get; init; } = DefaultMaxTransfers;
    public int? Seed { get; init; }
    public IReadOnlyList<string>? VariantIds { get; init; }
    public IReadOnlyList<string>? DatasetIds { get; init; }
    public IReadOnlyList<BenchmarkVariant> GetVariants() => VariantIds is null
        ? BenchmarkAlgorithmCatalog.Variants
        : VariantIds.Select(id => BenchmarkAlgorithmCatalog.Find(id) ?? throw new ArgumentException($"Unknown variant '{id}'.")).ToArray();
    public static bool TryParse(IReadOnlyList<string> args, out BenchmarkOptions? options, out string? error)
    {
        options = null; error = null;
        if (args.Count < 1 || args.Count % 2 != 1)
        { error = "Usage: benchmark <query-count> [--radius-metres 500] [--seed N] [--warmups 3] [--repetitions 5] [--max-transfers 7] [--variants raptor-single,csa-pareto] [--datasets Slask,Warsaw]."; return false; }
        if (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
        { error = "Query count must be positive."; return false; }
        var result = new BenchmarkOptions(count);
        var seen = new HashSet<string>();
        for (var i = 1; i < args.Count; i += 2)
        {
            var key = args[i]; var value = args[i + 1];
            if (!seen.Add(key)) { error = $"Repeated option: {key}."; return false; }
            if (key is "--variants" or "--datasets")
            {
                var ids = value.Split(',', StringSplitOptions.TrimEntries);
                if (ids.Any(string.IsNullOrEmpty)) { error = $"Empty selection for {key}."; return false; }
                if (key == "--variants")
                {
                    var variants = ids.Select(BenchmarkAlgorithmCatalog.Find).ToArray();
                    if (variants.Any(v => v is null)) { error = $"Unknown variant. Available: {string.Join(", ", BenchmarkAlgorithmCatalog.Variants.Select(v => v.Id))}."; return false; }
                    result = result with { VariantIds = variants.Select(v => v!.Id).Distinct().ToArray() };
                }
                else result = result with { DatasetIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                continue;
            }
            if (key == "--radius-metres")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius) || !double.IsFinite(radius) || radius < 0)
                { error = "Walking radius must be finite and nonnegative."; return false; }
                result = result with { WalkingRadiusMeters = radius }; continue;
            }
            if (key is not ("--seed" or "--warmups" or "--repetitions" or "--max-transfers"))
            { error = $"Unknown option: {key}."; return false; }
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                || (key != "--seed" && n < (key == "--repetitions" ? 1 : 0)))
            { error = $"Invalid value for {key}."; return false; }
            if (key == "--max-transfers" && n > int.MaxValue - 2)
            { error = "Transfer limit is too large."; return false; }
            result = key switch
            {
                "--seed" => result with { Seed = n },
                "--warmups" => result with { WarmupCount = n },
                "--repetitions" => result with { Repetitions = n },
                _ => result with { MaxTransfers = n }
            };
        }
        options = result; return true;
    }
}
