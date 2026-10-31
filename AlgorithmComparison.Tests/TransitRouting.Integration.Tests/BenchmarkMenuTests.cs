using AlgorithmComparison.CLI;
using AlgorithmComparison.CLI.Commands;
using AlgorithmComparison.Core.Results;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class BenchmarkMenuTests
{
    private sealed class FakeConsole(params ConsoleKey[] keys) : ICheckboxConsole
    {
        private readonly Queue<ConsoleKey> pending = new(keys);
        public List<string> Lines { get; } = new();
        public bool IsInputRedirected => false;
        public void Clear() { }
        public void WriteLine(string line) => Lines.Add(line);
        public ConsoleKey ReadKey() => pending.Dequeue();
    }
    [Fact]
    public void MenuSupportsSpaceNavigationSelectAllNoneAndRejectsEmptyConfirmation()
    {
        var terminal = new FakeConsole(ConsoleKey.N, ConsoleKey.Enter, ConsoleKey.End, ConsoleKey.Spacebar, ConsoleKey.Enter);
        Assert.Equal(new[] { 2 }, CheckboxMenu.Select("menu", new[] { "a", "b", "c" }, new[] { true, true, true }, terminal));
        Assert.Contains(terminal.Lines, line => line.Contains("[ ]"));
        Assert.Contains(terminal.Lines, line => line.Contains("[x]"));
        Assert.Contains(terminal.Lines, line => line.Contains("Zaznacz co najmniej"));
        var state = new CheckboxSelectionState(new[] { false, false });
        state.Apply(ConsoleKey.UpArrow); Assert.Equal(1, state.Cursor);
        state.Apply(ConsoleKey.A); Assert.All(state.Selected, Assert.True);
        state.Apply(ConsoleKey.N); Assert.All(state.Selected, Assert.False);
        Assert.Equal(CheckboxAction.Cancel, state.Apply(ConsoleKey.Escape));
    }
    [Fact]
    public void EscapeCancelsWithoutConfirmingSelection()
    {
        Assert.Throws<OperationCanceledException>(() => CheckboxMenu.Select("menu", new[] { "a" }, new[] { true }, new FakeConsole(ConsoleKey.Escape)));
    }
    [Fact]
    public void OptionsSelectIndependentModesAndUseCommonTransferLimit()
    {
        Assert.True(BenchmarkOptions.TryParse(new[] { "3", "--variants", "csa-single,raptor-pareto", "--datasets", "Slask,Warsaw", "--max-transfers", "0" }, out var options, out var error), error);
        Assert.Equal(0, options!.MaxTransfers);
        Assert.Equal(new[] { new BenchmarkVariant("csa", "earliest"), new BenchmarkVariant("raptor", "pareto") }, options.GetVariants());
        Assert.Equal(new[] { "Slask", "Warsaw" }, options.DatasetIds);
        Assert.True(BenchmarkOptions.TryParse(new[] { "1" }, out var defaults, out _));
        Assert.Equal(7, defaults!.MaxTransfers); Assert.Equal(6, defaults.GetVariants().Count);
    }
    [Theory]
    [InlineData("--walking-threads", "4")]
    [InlineData("--variants", "csa-wrong")]
    [InlineData("--variants", "")]
    [InlineData("--datasets", "")]
    [InlineData("--max-transfers", "-1")]
    [InlineData("--max-transfers", "2147483647")]
    public void InvalidSelectionsAndLimitsAreRejected(string option, string value)
    { Assert.False(BenchmarkOptions.TryParse(new[] { "1", option, value }, out _, out _)); }

    [Fact]
    public void DatasetSelectionDoesNotRequireRemovingOtherFolders()
    {
        var a = new BenchmarkDataset("Slask", "CommunityTransfer", "a", Array.Empty<string>());
        var b = new BenchmarkDataset("Warsaw", "ZoneTransfer", "b", Array.Empty<string>());
        var invalid = new BenchmarkDataset("broken", "Other", "x", new[] { "no stops" });
        var d = new BenchmarkDatasetDiscoveryResult("root", new[] { a, b, invalid }, Array.Empty<string>());
        Assert.Equal(new[] { b }, BenchmarkSelection.ResolveDatasets(d, new[] { "Warsaw" }));
        Assert.Equal(new[] { a, b }, BenchmarkSelection.ResolveDatasets(d, null));
        Assert.Throws<ArgumentException>(() => BenchmarkSelection.ResolveDatasets(d, new[] { "missing" }));
        Assert.Throws<ArgumentException>(() => BenchmarkSelection.ResolveDatasets(d, new[] { "broken" }));
        var duplicate = d with { Datasets = new[] { a, a with { TransferCategory = "Other" } } };
        Assert.Throws<ArgumentException>(() => BenchmarkSelection.ResolveDatasets(duplicate, new[] { "Slask" }));
        Assert.Single(BenchmarkSelection.ResolveDatasets(duplicate, new[] { "Other/Slask" }));
    }
    [Fact]
    public void ConfirmedMenuChoicesAreRemembered()
    {
        var root = Path.Combine(Path.GetTempPath(), "benchmark-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var d = new BenchmarkDatasetDiscoveryResult(Path.Combine(root, "Data"), new[]{
                new BenchmarkDataset("Slask","CommunityTransfer","a",Array.Empty<string>()),
                new BenchmarkDataset("Warsaw","ZoneTransfer","b",Array.Empty<string>()),
                new BenchmarkDataset("New York","ZoneTransfer","c",Array.Empty<string>())}, Array.Empty<string>());
            var terminal = new FakeConsole(ConsoleKey.N, ConsoleKey.Spacebar, ConsoleKey.Enter,
                ConsoleKey.N, ConsoleKey.End, ConsoleKey.Spacebar, ConsoleKey.Enter);
            var chosen = BenchmarkSelection.Interactive(new BenchmarkOptions(1), d, terminal);
            Assert.Equal(new[] { "raptor-single" }, chosen.VariantIds);
            Assert.Equal(new[] { "ZoneTransfer/New York" }, chosen.DatasetIds);
            var remembered = BenchmarkSelection.Interactive(new BenchmarkOptions(1), d, new FakeConsole(ConsoleKey.Enter, ConsoleKey.Enter));
            Assert.Equal(chosen.VariantIds, remembered.VariantIds); Assert.Equal(chosen.DatasetIds, remembered.DatasetIds);
        }
        finally { Directory.Delete(root, true); }
    }
    private static BenchmarkWorkerResult Result(string alg, string mode, bool failure = false) => new()
    {
        CityName = "Warsaw",
        TransferCategory = "ZoneTransfer",
        ServiceDate = "2026-08-04",
        FootpathGraph = "radius",
        Algorithm = alg,
        Mode = mode,
        MasterSeed = 42,
        DatasetSeed = 1,
        QueryCount = 1,
        WarmupCount = 0,
        Repetitions = 1,
        MaxTransfers = 7,
        Samples = new[]{new BenchmarkWorkerSample { QueryIndex=1,TimeWindow="all-day",FromStopId="S",ToStopId="Z",DepartureTime=0,
            Repetition=1,IsWarmup=false,Signature=failure?null:"100|0|",Error=failure?"test error":null,
            Metrics=failure?null:new AlgorithmMetrics {AlgorithmName=alg,QueryTimeMs=10,AllocatedBytes=100,StopsSettled=1,TripsOrConnectionsScanned=1}}}
    };
    [Fact]
    public void AsymmetricModeSelectionIsNotReportedAsMissingAlgorithms()
    {
        var root = Path.Combine(Path.GetTempPath(), "benchmark-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var results = new[] { Result("raptor", "earliest"), Result("csa", "pareto") };
            var selected = new[] { new BenchmarkVariant("raptor", "earliest"), new BenchmarkVariant("csa", "pareto") };
            Assert.False(BenchmarkWorkerReportAggregator.WriteReports(results, selected, root, "radius-500m"));
            var comparisons = File.ReadAllText(Path.Combine(root, "comparisons-radius-500m.csv"));
            Assert.Equal(2, comparisons.Split("not_compared").Length - 1); Assert.DoesNotContain(",error,", comparisons);
            var a = BenchmarkTextReportWriter.Write(results[0], root);
            var b = BenchmarkTextReportWriter.Write(results[0] with { Mode = "pareto" }, root);
            Assert.NotEqual(a, b); Assert.Equal("Warsaw-raptor-single.txt", Path.GetFileName(a));
            Assert.Contains("Maksymalna liczba przesiadek: 7", File.ReadAllText(a));
            Assert.Contains("zapytanie\tpowtorzenie", File.ReadAllText(a));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void SelectedWorkerFailureRemainsAnError()
    {
        var root = Path.Combine(Path.GetTempPath(), "benchmark-error-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(BenchmarkWorkerReportAggregator.WriteReports(new[] { Result("csa", "earliest", true) },
                new[] { new BenchmarkVariant("csa", "earliest") }, root, "radius"));
            Assert.Contains(",error,", File.ReadAllText(Path.Combine(root, "comparisons-radius.csv")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void DiscoveryAcceptsExistingLayoutAndNewDirectGtfsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "benchmark-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "Data");
            foreach (var path in new[] { Path.Combine(data, "CommunityTransfer", "Slask"), Path.Combine(data, "NewCity") })
            {
                Directory.CreateDirectory(path);
                foreach (var file in new[] { "stops.txt", "routes.txt", "trips.txt", "stop_times.txt", "calendar_dates.txt" })
                    File.WriteAllText(Path.Combine(path, file), "header");
            }
            Directory.CreateDirectory(Path.Combine(data, "Broken"));
            var result = BenchmarkDatasetDiscovery.Discover(root);
            Assert.Equal(3, result.Datasets.Count);
            Assert.Equal(2, result.Datasets.Count(d => d.IsValid));
            Assert.Single(BenchmarkSelection.ResolveDatasets(result, new[] { "Direct/NewCity" }));
            Assert.Single(BenchmarkSelection.ResolveDatasets(result, new[] { "CommunityTransfer/Slask" }));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void TextReportExcludesWarmupFromStatisticsAndDoesNotOverwriteModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "benchmark-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = Result("csa", "earliest");
            var sample = r.Samples[0];
            var noisy = sample with { IsWarmup = true, Repetition = 0, Metrics = new AlgorithmMetrics { AlgorithmName = "csa", QueryTimeMs = 9999, AllocatedBytes = 1, StopsSettled = 0, TripsOrConnectionsScanned = 0 } };
            var path = BenchmarkTextReportWriter.Write(r with { Samples = new[] { noisy, sample } }, root);
            var text = File.ReadAllText(path);
            Assert.Contains("mediana [ms]: 10;", text);
            Assert.Contains("9999", text);
            Assert.Contains("Pomiarow: 1;", text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
