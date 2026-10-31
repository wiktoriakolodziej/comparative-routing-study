using System.Security.Cryptography;
using System.Text.Json;
using AlgorithmComparison.CLI;
using AlgorithmComparison.RaptorRealtime;

namespace AlgorithmComparison.Tests.Realtime;

public sealed partial class RealtimeRaptorTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("completed-with-errors")]
    [InlineData("canceled")]
    [InlineData("failed")]
    public async Task SessionReportRetainsCyclesHashesAndExplicitFinalStatus(string status)
    {
        var directory = Path.Combine(Path.GetTempPath(), "session-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
            var writer = new RealtimeSessionReportWriter(directory);
            await writer.StartAsync(new { ServiceDate = Date, Radius = 500 }, 3);
            using (var running = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "session.json"))))
                Assert.Equal("running", running.RootElement.GetProperty("Status").GetString());
            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var update = router.ApplyFeed(Feed(Delay("t", 0, cycle * 10)));
                var result = router.RouteDetailed(Query());
                await writer.WriteAsync(new(cycle, Now, true, Query(), 500, cycle == 2 ? "API failed" : null,
                    cycle == 2 ? null : update, result, null, null, new("test", "snapshot.pb", "source-hash")));
            }
            await writer.FinishAsync(status, status == "failed" ? "test failure" : null);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "session.json")));
            var root = manifest.RootElement;
            Assert.Equal(status, root.GetProperty("Status").GetString());
            Assert.Equal(2, root.GetProperty("RecordedCycles").GetInt32());
            Assert.Equal(3, root.GetProperty("RequestedCycles").GetInt32());
            Assert.Equal(1, root.GetProperty("AcquisitionErrors").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("FinishedAt").ValueKind);
            foreach (var file in root.GetProperty("Cycles").EnumerateArray())
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file.GetProperty("File").GetString()!));
                Assert.Equal(file.GetProperty("Sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
            }
            Assert.Equal(3, File.ReadAllLines(Path.Combine(directory, "cycles.csv")).Length);
            Assert.Throws<IOException>(() => new RealtimeSessionReportWriter(directory));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(new(3, Now, true, Query(), 500,
                null, null, router.RouteDetailed(Query()), null, null, null)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SessionReportRejectsDuplicateCyclesWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "session-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var router = Router(Network(Trip("t", ("A", 100), ("C", 200))));
            var writer = new RealtimeSessionReportWriter(directory);
            await writer.StartAsync(new { }, 2);
            var record = new RealtimeSessionRecord(1, Now, true, Query(), 500, null, null, router.RouteDetailed(Query()), null, null, null);
            await writer.WriteAsync(record);
            var original = await File.ReadAllTextAsync(Path.Combine(directory, "cycle-000001.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(record));
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(directory, "cycle-000001.json")));
            await writer.FinishAsync("canceled");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReplaySequenceRecordsExactSourceAndClearsItOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "session-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var snapshot = Path.Combine(directory, "first.pb");
            var bytes = Google.Protobuf.MessageExtensions.ToByteArray(Feed());
            await File.WriteAllBytesAsync(snapshot, bytes);
            var source = new RealtimeSnapshotSequence(new[] { snapshot, Path.Combine(directory, "missing.pb") });
            await source.ReadAsync();
            Assert.Equal(snapshot, source.LastSnapshot!.FilePath);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), source.LastSnapshot.Sha256);
            await Assert.ThrowsAsync<FileNotFoundException>(() => source.ReadAsync());
            Assert.Null(source.LastSnapshot);
        }
        finally { Directory.Delete(directory, true); }
    }
}
