using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AlgorithmComparison.Core.Queries;
using AlgorithmComparison.RaptorRealtime;

namespace AlgorithmComparison.CLI;

public sealed record RealtimeSessionRecord(int Cycle, DateTimeOffset EvaluatedAt, bool Replay,
    RoutingQuery Query, double WalkingRadiusMeters, string? AcquisitionError, RealtimeUpdateReport? LatestUpdateAttempt,
    RealtimeRoutingResult Result, RaptorBenchmarkCycle? Comparison, RealtimeAcquisitionMetrics? AcquisitionMetrics,
    RealtimeSourceSnapshot? SourceSnapshot);

public sealed class RealtimeSessionReportWriter
{
    private sealed record CycleFile(int Cycle, string File, string Sha256);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const string CsvHeader = "cycle,evaluated_at,used_realtime,version,acquisition_error,update_accepted,source_snapshot,source_sha256,best_arrival,journeys";
    private readonly string directory;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private int recordedCycles;
    private int requestedCycles;
    private int realtimeCycles;
    private int acquisitionErrors;
    private int rejectedUpdates;
    private bool started;
    private bool finished;

    public RealtimeSessionReportWriter(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        if (Directory.EnumerateFileSystemEntries(this.directory).Any())
            throw new IOException("Katalog dziennika sesji musi być pusty.");
    }

    public async Task StartAsync(object configuration, int requestedCycles, CancellationToken cancellation = default)
    {
        if (started || requestedCycles < 1) throw new InvalidOperationException("Invalid session start.");
        this.requestedCycles = requestedCycles;
        await WriteAtomicAsync("configuration.json", JsonSerializer.Serialize(configuration, Json), cancellation);
        await RealtimeReportFiles.AppendAsync(new[]
        {
            (Path.Combine(directory, "cycles.csv"), CsvHeader, ""),
            (Path.Combine(directory, "cycles.jsonl"), "", "")
        }, cancellation);
        started = true;
        await ManifestAsync("running", null, cancellation);
    }

    public async Task WriteAsync(RealtimeSessionRecord record, CancellationToken cancellation = default)
    {
        if (!started || finished || record.Cycle != recordedCycles + 1)
            throw new InvalidOperationException("Session cycles must be written once in consecutive order.");
        var name = $"cycle-{record.Cycle:000000}.json";
        var content = JsonSerializer.Serialize(record, Json);
        await WriteAtomicAsync(name, content, cancellation);
        var entry = new CycleFile(record.Cycle, name,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
        var journeys = record.Result.Routing.BestJourney is { } best ? new[] { best }
            : record.Result.Routing.ParetoJourneys.Journeys.ToArray();
        var row = string.Join(',', new object?[] { record.Cycle, record.EvaluatedAt.ToString("O"), record.Result.UsedRealtime,
            record.Result.Version, record.AcquisitionError, record.LatestUpdateAttempt?.Accepted, record.SourceSnapshot?.FilePath,
            record.SourceSnapshot?.Sha256, journeys.Select(j => (int?)j.ArrivalTime).Min(), journeys.Length }
            .Select(value => "\"" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\""));
        await RealtimeReportFiles.AppendAsync(new[]
        {
            (Path.Combine(directory, "cycles.csv"), CsvHeader, row + Environment.NewLine),
            (Path.Combine(directory, "cycles.jsonl"), "", JsonSerializer.Serialize(entry) + Environment.NewLine)
        }, cancellation);
        recordedCycles++;
        if (record.Result.UsedRealtime) realtimeCycles++;
        if (record.AcquisitionError is not null) acquisitionErrors++;
        if (record.LatestUpdateAttempt is { Accepted: false }) rejectedUpdates++;
        await ManifestAsync("running", null, cancellation);
    }

    public async Task FinishAsync(string status, string? error = null)
    {
        if (!started || finished) return;
        if (status is not ("completed" or "completed-with-errors" or "canceled" or "failed"))
            throw new ArgumentException("Invalid completion status.", nameof(status));
        await ManifestAsync(status, error, CancellationToken.None);
        finished = true;
    }

    private async Task ManifestAsync(string status, string? error, CancellationToken cancellation)
    {
        List<CycleFile>? completedFiles = null;
        if (status != "running")
        {
            completedFiles = new();
            using var input = new StreamReader(Path.Combine(directory, "cycles.jsonl"));
            while (await input.ReadLineAsync(cancellation) is { } line)
                completedFiles.Add(JsonSerializer.Deserialize<CycleFile>(line)
                    ?? throw new InvalidDataException("Empty cycle index entry."));
            if (completedFiles.Count != recordedCycles) throw new InvalidDataException("Cycle index count does not match the session.");
        }
        await WriteAtomicAsync("session.json", JsonSerializer.Serialize(new
        {
            SchemaVersion = 2, StartedAt = startedAt, FinishedAt = status == "running" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow,
            Status = status, Error = error, RequestedCycles = requestedCycles, RecordedCycles = recordedCycles,
            RealtimeCycles = realtimeCycles, AcquisitionErrors = acquisitionErrors, RejectedUpdates = rejectedUpdates,
            CycleIndexFile = "cycles.jsonl", Cycles = completedFiles
        }, Json), cancellation);
    }

    private Task WriteAtomicAsync(string name, string content, CancellationToken cancellation) =>
        RealtimeReportFiles.WriteAtomicAsync(Path.Combine(directory, name), content, cancellation);
}
