using AlgorithmComparison.CLI;
using AlgorithmComparison.Core.Models;
using Google.Protobuf;
using TransitRealtime;
using Stop = AlgorithmComparison.Core.Models.Stop;
using Trip = AlgorithmComparison.Core.Models.Trip;

namespace AlgorithmComparison.Tests;

public sealed class RealtimeMenuTests
{
    private static readonly DateOnly Day = new(2026, 10, 18);
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainMenuSelectsBothBranchesAndExplainsInternetRequirement(bool realtime)
    {
        var console = new MenuConsole(realtime ? new[] { ConsoleKey.DownArrow, ConsoleKey.Enter } : new[] { ConsoleKey.Enter });
        Assert.Equal(realtime ? ApplicationMode.Realtime : ApplicationMode.StaticBenchmarks, ApplicationMenu.Select(console));
        Assert.Contains(console.Lines, line => line.Contains("WYMAGAJĄ INTERNETU"));
    }

    [Fact]
    public void MenuHandlesCancellationAndRejectsRedirectedInput()
    {
        Assert.Throws<OperationCanceledException>(() => ApplicationMenu.Select(new MenuConsole(new[] { ConsoleKey.Escape })));
        Assert.Throws<ArgumentException>(() => ApplicationMenu.Select(new MenuConsole(Array.Empty<ConsoleKey>()) { IsInputRedirected = true }));
        var console = new MenuConsole(new[] { ConsoleKey.Enter }, new[] { "anuluj" });
        Assert.Throws<OperationCanceledException>(() => RealtimeRaptorMenu.Read(console, (_, _) => throw new Exception("Must not load"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), new MenuClock()));
    }

    [Fact]
    public void LiveMenuBuildsWatchConfigurationAndLoadsNetworkOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "menu-" + Guid.NewGuid().ToString("N"));
        var console = new MenuConsole(new[] { ConsoleKey.Enter, ConsoleKey.DownArrow, ConsoleKey.Enter }, new[]
        {
            root, "2026-10-17", "", "missing", "A", "C", "17:30", "-1", "2", "500,5", "", "0", "15", "3", "", ""
        });
        var calls = 0;
        var result = RealtimeRaptorMenu.Read(console, (path, day) =>
        {
            calls++; Assert.Equal(root, path); Assert.Equal(Day, day); return Network();
        }, root, new MenuClock());
        Assert.Equal(1, calls);
        Assert.Contains("--compare", result.Arguments);
        Assert.Contains("--watch", result.Arguments);
        Assert.Contains("--pareto", result.Arguments);
        Assert.DoesNotContain("--snapshot", result.Arguments);
        Assert.Equal("17:30:00", Value(result, "--departure"));
        Assert.Equal("2", Value(result, "--max-transfers"));
        Assert.Equal("500.5", Value(result, "--radius-metres"));
        Assert.Equal("15", Value(result, "--interval-seconds"));
        Assert.Equal("3", Value(result, "--count"));
        Assert.False(Directory.Exists(root));
        Assert.Contains(console.Lines, line => line.Contains("WYMAGANE POŁĄCZENIE Z INTERNETEM"));
        Assert.Contains(console.Lines, line => line.Contains("bieżący dzień"));
        Assert.Contains(console.Lines, line => line.Contains("Nie znaleziono"));
    }

    [Fact]
    public void ReplayMenuUsesSnapshotDateAndResolvesAmbiguousStopNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "static", "gtfs-20261018"));
        var snapshot = Path.Combine(root, "tripUpdates.pb");
        File.WriteAllBytes(snapshot, new FeedMessage { Header = new FeedHeader
        { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)Now.ToUnixTimeSeconds() } }.ToByteArray());
        try
        {
            var console = new MenuConsole(new[] { ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter,
                ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter },
                new[] { snapshot, "", "Centrum", "C", "", "", "", "", "", "" });
            var result = RealtimeRaptorMenu.Read(console, (_, day) => { Assert.Equal(Day, day); return Network(); }, root, new MenuClock());
            Assert.Contains("--replay", result.Arguments);
            Assert.DoesNotContain("--watch", result.Arguments);
            Assert.DoesNotContain("--pareto", result.Arguments);
            Assert.Equal(snapshot, Value(result, "--snapshot"));
            Assert.Equal("B", Value(result, "--from"));
            Assert.Equal("12:00:00", Value(result, "--departure"));
            Assert.Contains(console.Lines, line => line.Contains("Centrum [B]"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LiveZeroCyclesMeansUntilCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var console = new MenuConsole(new[] { ConsoleKey.Enter, ConsoleKey.Enter },
            new[] { root, "", "A", "C", "", "", "", "", "", "0", "", "" });
        var result = RealtimeRaptorMenu.Read(console, (_, _) => Network(), root, new MenuClock());
        Assert.Equal(int.MaxValue.ToString(), Value(result, "--count"));
        Assert.Equal("now", Value(result, "--departure"));
    }

    [Fact]
    public void MenuRejectsNetworkWithoutActiveTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var console = new MenuConsole(new[] { ConsoleKey.Enter }, new[] { root, "" });
        Assert.Throws<InvalidDataException>(() => RealtimeRaptorMenu.Read(console, (_, _) => Network(false), root, new MenuClock()));
    }

    private static string Value(RealtimeMenuSelection result, string name) => result.Arguments[Array.IndexOf(result.Arguments, name) + 1];

    private static TransitNetwork Network(bool active = true) => new()
    {
        ServiceDate = Day,
        Stops = new[] { "A", "B", "C" }.ToDictionary(id => id, id => new Stop
        { Id = id, Name = id == "C" ? "Cel" : "Centrum", Latitude = 50, Longitude = 19 }),
        Trips = active ? new Dictionary<string, Trip> { ["t"] = new Trip { Id = "t", RouteId = "r", ServiceId = "s", StopTimes = Array.Empty<StopTime>() } } : new Dictionary<string, Trip>(),
        Routes = new Dictionary<string, Route>(), Connections = Array.Empty<Connection>(), ConnectionDict = new Dictionary<string, Connection>()
    };

    private sealed class MenuClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class MenuConsole(IEnumerable<ConsoleKey> keys, IEnumerable<string>? lines = null) : IInteractiveConsole
    {
        private readonly Queue<ConsoleKey> keys = new(keys);
        private readonly Queue<string> input = new(lines ?? Array.Empty<string>());
        public List<string> Lines { get; } = new();
        public bool IsInputRedirected { get; init; }
        public void Clear() { }
        public void WriteLine(string line) => Lines.Add(line);
        public ConsoleKey ReadKey() => keys.Dequeue();
        public string? ReadLine() => input.Count == 0 ? throw new InvalidOperationException("Unexpected input request") : input.Dequeue();
    }
}
