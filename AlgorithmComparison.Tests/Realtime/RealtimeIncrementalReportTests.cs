using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgorithmComparison.CLI;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.RaptorRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Fact]
    public async Task BenchmarkAppendsToSameCsvAndKeepsExactCombinedStatistics()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeBenchmarkReportWriter(directory);
            await writer.WriteAsync(ReportingCycle(1, new[] { 1d, 2d }), new { Radius = 500 });
            var path = Path.Combine(directory, "comparison.csv");
            var prefix = await File.ReadAllTextAsync(path);
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var configuration = new FileStream(Path.Combine(directory, "configuration.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var previousLength = reader.Length;
            await writer.WriteAsync(ReportingCycle(2, new[] { 3d, 4d, 100d }), new { Radius = 500 });
            Assert.True(reader.Length > previousLength);
            Assert.StartsWith(prefix, await File.ReadAllTextAsync(path));
            await writer.WriteAsync(ReportingCycle(3, new[] { 1000d }, "failed sample"), new { Radius = 500 });
            var summary = await File.ReadAllTextAsync(Path.Combine(directory, "summary.txt"));
            Assert.Contains("raptor-original: n=5; średnia 22.000 ms; mediana 3.000 ms; P95 100.000 ms; max 100.000 ms", summary);
            Assert.Contains("błędy 1", summary);
            Assert.Equal(10, File.ReadAllLines(path).Length);
            Assert.Equal(4, File.ReadAllLines(Path.Combine(directory, "updates.csv")).Length);
            Assert.Equal(1, File.ReadAllLines(path).Count(line => line.StartsWith("cycle,query,")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BenchmarkWriterDoesNotRetainSerializedCyclesOrJourneys()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeBenchmarkReportWriter(directory);
            var reference = WriteCycleAndRelease(writer);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reference.IsAlive);
            GC.KeepAlive(writer);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SessionAppendsCsvAndIndexWhileKeepingRunningManifestSmall()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeSessionReportWriter(directory);
            await writer.StartAsync(new { }, 2);
            await writer.WriteAsync(SessionRecord(1));
            using var csv = new FileStream(Path.Combine(directory, "cycles.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var index = new FileStream(Path.Combine(directory, "cycles.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var csvLength = csv.Length;
            var indexLength = index.Length;
            await writer.WriteAsync(SessionRecord(2));
            Assert.True(csv.Length > csvLength);
            Assert.True(index.Length > indexLength);
            using (var running = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "session.json"))))
            {
                Assert.Equal(2, running.RootElement.GetProperty("SchemaVersion").GetInt32());
                Assert.Equal(2, running.RootElement.GetProperty("RecordedCycles").GetInt32());
                Assert.Equal("cycles.jsonl", running.RootElement.GetProperty("CycleIndexFile").GetString());
                Assert.Equal(JsonValueKind.Null, running.RootElement.GetProperty("Cycles").ValueKind);
            }
            await writer.FinishAsync("completed");
            using var completed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "session.json")));
            Assert.Equal(2, completed.RootElement.GetProperty("Cycles").GetArrayLength());
            Assert.Equal(3, File.ReadAllLines(Path.Combine(directory, "cycles.csv")).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SessionAppendFailurePreservesPreviousRowsAndCanBeRetried()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeSessionReportWriter(directory);
            await writer.StartAsync(new { }, 2);
            await writer.WriteAsync(SessionRecord(1));
            var csvPath = Path.Combine(directory, "cycles.csv");
            var indexPath = Path.Combine(directory, "cycles.jsonl");
            var csvBefore = await File.ReadAllTextAsync(csvPath);
            var indexBefore = await File.ReadAllTextAsync(indexPath);
            using (var held = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(SessionRecord(2)));
            Assert.Equal(csvBefore, await File.ReadAllTextAsync(csvPath));
            Assert.Equal(indexBefore, await File.ReadAllTextAsync(indexPath));
            await writer.WriteAsync(SessionRecord(2));
            await writer.FinishAsync("completed");
            Assert.Equal(3, File.ReadAllLines(csvPath).Length);
            Assert.Equal(2, File.ReadAllLines(indexPath).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BenchmarkAppendFailureDoesNotDoubleCountSamplesOnRetry()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeBenchmarkReportWriter(directory);
            await writer.WriteAsync(ReportingCycle(1, new[] { 1d }), new { });
            var csvPath = Path.Combine(directory, "comparison.csv");
            var prefix = await File.ReadAllTextAsync(csvPath);
            using (var held = new FileStream(Path.Combine(directory, "updates.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(ReportingCycle(2, new[] { 3d }), new { }));
            Assert.Equal(prefix, await File.ReadAllTextAsync(csvPath));
            await writer.WriteAsync(ReportingCycle(2, new[] { 3d }), new { });
            Assert.Contains("raptor-original: n=2; średnia 2.000 ms; mediana 2.000 ms", await File.ReadAllTextAsync(Path.Combine(directory, "summary.txt")));
            Assert.Equal(7, File.ReadAllLines(csvPath).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CanceledAppendLeavesPreviousCommittedCycleIntact()
    {
        var directory = ReportDirectory();
        try
        {
            var writer = new RealtimeSessionReportWriter(directory);
            await writer.StartAsync(new { }, 2);
            await writer.WriteAsync(SessionRecord(1));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(SessionRecord(2), canceled.Token));
            await writer.FinishAsync("canceled");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "session.json")));
            Assert.Equal(1, manifest.RootElement.GetProperty("RecordedCycles").GetInt32());
            Assert.Equal("canceled", manifest.RootElement.GetProperty("Status").GetString());
            Assert.Single(File.ReadAllLines(Path.Combine(directory, "cycles.jsonl")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FinalizerReportsBothPrimaryAndSecondaryFailureWithoutThrowing()
    {
        var primary = new IOException("primary routing/session failure").ToString();
        using var errors = new StringWriter();
        var finished = await RealtimeSessionFinalizer.TryFinishAsync(
            () => throw new UnauthorizedAccessException("secondary journal failure"), "failed", primary, errors);
        Assert.False(finished);
        Assert.Contains(primary, errors.ToString());
        Assert.Contains("secondary journal failure", errors.ToString());
        Assert.Contains("UnauthorizedAccessException", errors.ToString());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("canceled")]
    public async Task FinalizerFailureIsReportedEvenWithoutPrimaryError(string status)
    {
        using var errors = new StringWriter();
        Assert.False(await RealtimeSessionFinalizer.TryFinishAsync(() => throw new IOException("journal locked"), status, null, errors));
        Assert.Contains(status, errors.ToString());
        Assert.Contains("journal locked", errors.ToString());
    }

    [Fact]
    public async Task SuccessfulFinalizationDoesNotAddErrorMessages()
    {
        using var errors = new StringWriter();
        Assert.True(await RealtimeSessionFinalizer.TryFinishAsync(() => Task.CompletedTask, "completed", null, errors));
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public void TransferHistoryDoesNotKeepQueriesFromOlderCycles()
    {
        var net = Network(Trip("first", ("A", 100), ("B", 200)), Trip("second", ("B", 210), ("C", 300)));
        var clock = new Clock(Now);
        var walking = Paths(net);
        var router = new RealtimeRaptorAlgorithm(net, walking, time: clock);
        var benchmark = new RealtimeRaptorBenchmark(net, walking, router, clock, 0, 1);
        var update = router.ApplyFeed(Feed(Delay("first", 0, 30)));
        benchmark.Run(1, new(Now, Query(), null, update, router.RouteDetailed(Query())));
        update = router.ApplyFeed(Feed());
        var different = Query(departure: 1);
        benchmark.Run(2, new(Now, different, null, update, router.RouteDetailed(different)));
        var result = Assert.Single(benchmark.Run(3, new(Now, Query(), null, update, router.RouteDetailed(Query()))).Queries);
        Assert.False(result.ComparableToPreviousCycle);
        Assert.Equal(0, result.RecoveredTransfers);
    }

    private static string ReportDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "incremental-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static RealtimeSessionRecord SessionRecord(int cycle)
    {
        var result = Router(Network(Trip("t", ("A", 100), ("C", 200)))).RouteDetailed(Query());
        return new(cycle, Now, true, Query(), 500, null, null, result, null, null, null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WriteCycleAndRelease(RealtimeBenchmarkReportWriter writer)
    {
        var cycle = ReportingCycle(1, new[] { 1d });
        var reference = new WeakReference(cycle);
        writer.WriteAsync(cycle, new { }).GetAwaiter().GetResult();
        return reference;
    }

    private static RaptorBenchmarkCycle ReportingCycle(int cycle, double[] values, string? originalError = null)
    {
        var journey = new Journey { Legs = new[] { new JourneyLeg
        { FromStopId = "A", ToStopId = "C", DepartureTime = 100, ArrivalTime = 200, TripId = "t" } } };
        RaptorBenchmarkVariant Variant(string id) => new(id, new[] { journey }, values.Select(v =>
            new BenchmarkMeasurement(v, checked((long)(v * 1_000_000)))).ToArray(), id == "raptor-original" ? originalError : null);
        var comparison = new RaptorQueryComparison(0, Query(), Variant("raptor-original"), Variant("raptor-scheduled-control"),
            Variant("raptor-realtime"), true, false, 0, 0, 0, Array.Empty<JourneyAssessment>(), Array.Empty<JourneyAssessment>(), 0, 0, false);
        return new(cycle, Now, true, "test-version", null, null, null, null, 0, values.Length, 0, 0, new[] { comparison });
    }
}
