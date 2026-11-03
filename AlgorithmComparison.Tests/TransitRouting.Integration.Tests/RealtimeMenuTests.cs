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
    public void MenuHandlesCancellationBeforeDownloadingAndRejectsRedirectedInput()
    {
        Assert.Throws<OperationCanceledException>(() => ApplicationMenu.Select(new MenuConsole(new[] { ConsoleKey.Escape })));
        Assert.Throws<ArgumentException>(() => ApplicationMenu.Select(new MenuConsole(Array.Empty<ConsoleKey>()) { IsInputRedirected = true }));
        Assert.Throws<OperationCanceledException>(() => RealtimeRaptorMenu.Read(new MenuConsole(new[] { ConsoleKey.Escape }),
            (_, _) => throw new Exception("Must not load"), Root(), new MenuClock(), _ => throw new Exception("Must not download")));
    }

    [Fact]
    public void LiveMenuDownloadsTodaysScheduleBeforeLoadingItAndBuildsWatchConfiguration()
    {
        var root = Root();
        var console = new MenuConsole(new[] { ConsoleKey.Enter, ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter }, new[]
        {
            "missing", "A", "C", "17:30", "-1", "2", "500,5", "", "0", "15", "0", "3", "", ""
        });
        var downloaded = false;
        var calls = 0;
        var result = RealtimeRaptorMenu.Read(console, (path, day) =>
        {
            Assert.True(downloaded); calls++; Assert.Equal(root, path); Assert.Equal(Day, day); return Network();
        }, root, new MenuClock(), day =>
        {
            Assert.Equal(Day, day); Assert.False(downloaded); downloaded = true; return root;
        });
        Assert.Equal(1, calls);
        Assert.Contains("--compare", result.Arguments);
        Assert.Contains("--watch", result.Arguments);
        Assert.Contains("--pareto", result.Arguments);
        Assert.DoesNotContain("--snapshot", result.Arguments);
        Assert.Equal(root, Value(result, "--gtfs"));
        Assert.Equal("2026-10-18", Value(result, "--date"));
        Assert.Equal("17:30:00", Value(result, "--departure"));
        Assert.Equal("2", Value(result, "--max-transfers"));
        Assert.Equal("500.5", Value(result, "--radius-metres"));
        Assert.Equal("15", Value(result, "--interval-seconds"));
        Assert.Equal("3", Value(result, "--count"));
        Assert.False(Directory.Exists(root));
        Assert.Contains(console.Lines, line => line.Contains("WYMAGANE POŁĄCZENIE Z INTERNETEM"));
        Assert.Contains(console.Lines, line => line.Contains("automatycznie pobrany rozkład"));
        Assert.Contains(console.Lines, line => line.Contains("Nie znaleziono"));
        Assert.Contains(console.Lines, line => line == "Aktualność danych jest sprawdzana według czasu ich utworzenia.");
        Assert.Contains(console.Lines, line => line.Contains("Czas oczekiwania przed kolejnym pomiarem"));
        Assert.Contains(console.Lines, line => line.Contains("Ścieżka do zapisu raportu wynikowego"));
        Assert.DoesNotContain(console.Lines, line => line.Contains("0 = do Ctrl+C"));
    }

    [Fact]
    public void ReplayMenuUsesSnapshotDateExplainsSavedSchedulesAndNeverDownloads()
    {
        var root = Root();
        Directory.CreateDirectory(Path.Combine(root, "static", "gtfs-20261018"));
        var snapshot = Path.Combine(root, "tripUpdates.pb");
        File.WriteAllBytes(snapshot, new FeedMessage { Header = new FeedHeader
        { GtfsRealtimeVersion = "2.0", Timestamp = (ulong)Now.ToUnixTimeSeconds() } }.ToByteArray());
        try
        {
            var console = new MenuConsole(new[] { ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter,
                ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Enter },
                new[] { snapshot, "", "Centrum", "C", "", "", "", "", "", "" });
            var result = RealtimeRaptorMenu.Read(console, (_, day) => { Assert.Equal(Day, day); return Network(); },
                root, new MenuClock(), _ => throw new Exception("Offline must not download"));
            Assert.Contains("--replay", result.Arguments);
            Assert.DoesNotContain("--watch", result.Arguments);
            Assert.DoesNotContain("--pareto", result.Arguments);
            Assert.Equal(snapshot, Value(result, "--snapshot"));
            Assert.Equal("B", Value(result, "--from"));
            Assert.Equal("12:00:00", Value(result, "--departure"));
            Assert.Contains(console.Lines, line => line.Contains("Centrum [B]"));
            Assert.Contains(console.Lines, line => line.Contains("18.10.2026 — zapisany rozkład"));
            Assert.Contains(console.Lines, line => line.Contains("To zapisane kopie rozkładów"));
            Assert.Contains(console.Lines, line => line.Contains("nie pobiera nowych danych"));
            Assert.DoesNotContain(console.Lines, line => line.Contains("Kiedy zakończyć"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LiveContinuousModeIsAnExplicitChoiceRatherThanZeroCount()
    {
        var root = Root();
        var console = new MenuConsole(new[] { ConsoleKey.Enter, ConsoleKey.Enter, ConsoleKey.DownArrow, ConsoleKey.Enter },
            new[] { "A", "C", "", "", "", "", "", "", "" });
        var result = RealtimeRaptorMenu.Read(console, (_, _) => Network(), root, new MenuClock(), _ => root);
        Assert.Equal(int.MaxValue.ToString(), Value(result, "--count"));
        Assert.Equal("now", Value(result, "--departure"));
        Assert.Contains(console.Lines, line => line.Contains("Dopiero po ręcznym zatrzymaniu"));
        Assert.Contains(console.Lines, line => line.Contains("zakończenie: ręcznie, skrótem Ctrl+C"));
    }

    [Fact]
    public void FailedDownloadStopsBeforeLoadingOrSearching()
    {
        Assert.Throws<IOException>(() => RealtimeRaptorMenu.Read(new MenuConsole(new[] { ConsoleKey.Enter }),
            (_, _) => throw new Exception("Must not load stale schedule"), Root(), new MenuClock(),
            _ => throw new IOException("API unavailable")));
    }

    [Fact]
    public void MidnightDuringDownloadStopsBeforeLoadingYesterdaysSchedule()
    {
        var now = Now;
        var clock = new MutableClock(() => now);
        Assert.Throws<InvalidDataException>(() => RealtimeRaptorMenu.Read(new MenuConsole(new[] { ConsoleKey.Enter }),
            (_, _) => throw new Exception("Must not load yesterday's schedule"), Root(), clock,
            _ => { now = now.AddDays(1); return Root(); }));
    }

    [Fact]
    public void MenuRejectsNetworkWithoutActiveTrips()
    {
        var root = Root();
        Assert.Throws<InvalidDataException>(() => RealtimeRaptorMenu.Read(new MenuConsole(new[] { ConsoleKey.Enter }),
            (_, _) => Network(false), root, new MenuClock(), _ => root));
    }

    private static string Root() => Path.Combine(Path.GetTempPath(), "menu-" + Guid.NewGuid().ToString("N"));
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
    private sealed class MutableClock(Func<DateTimeOffset> now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now(); }
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
