using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlgorithmComparison.CLI;

namespace AlgorithmComparison.Tests;

public sealed class GzmStaticSchedulePreparationTests
{
    private static readonly DateOnly Day = new(2026, 11, 3);

    [Fact]
    public void PreparationUsesExistingDownloaderAndSafelyQuotesPathsWithSpacesAndApostrophes()
    {
        using var files = new Files();
        var console = new Terminal();
        var path = GzmStaticSchedulePreparation.Download(Day, files.DataRoot, console, start =>
        {
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
            var script = start.ArgumentList[^1];
            Assert.False(start.Environment.ContainsKey("PSModulePath"));
            Assert.Contains("Save-GzmGtfs", script);
            Assert.Contains("-ServiceDate '2026-11-03'", script);
            Assert.Contains(files.Root.Replace("'", "''"), script);
            return new(0, "Postęp pobierania\n" + files.Manifest(), "");
        });
        Assert.Equal(files.Schedule, path);
        Assert.Contains("Postęp pobierania", console.Lines);
        Assert.DoesNotContain(console.Lines, line => line.StartsWith("GTFS_RESULT="));
    }

    [Fact]
    public void FailedDownloadCannotFallBackToAnOldDirectory()
    {
        using var files = new Files();
        var exception = Assert.Throws<IOException>(() => GzmStaticSchedulePreparation.Download(Day, files.DataRoot,
            new Terminal(), _ => new(1, "", "API niedostępne")));
        Assert.Contains("Pomiar nie został uruchomiony", exception.Message);
    }

    [Theory]
    [InlineData("2026-11-02", "DataRealTime/Slask/static/current")]
    [InlineData("2026-11-03", "outside")]
    [InlineData("2026-11-03", "DataRealTime/Slask/static/missing")]
    public void PreparationRejectsWrongDayOutsidePathAndIncompleteSchedule(string day, string folder)
    {
        using var files = new Files();
        Assert.Throws<InvalidDataException>(() => GzmStaticSchedulePreparation.Download(Day, files.DataRoot,
            new Terminal(), _ => new(0, "GTFS_RESULT=" + JsonSerializer.Serialize(new { ServiceDate = day, GtfsDirectory = folder }), "")));
    }

    [Fact]
    public void PreparationRequiresADownloadManifest()
    {
        using var files = new Files();
        Assert.Throws<InvalidDataException>(() => GzmStaticSchedulePreparation.Download(Day, files.DataRoot,
            new Terminal(), _ => new(0, "No result", "")));
    }

    [Fact]
    public void RealPowerShellBridgeWorksWithAnOfflineModuleAndUnicodePaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new Files();
        File.WriteAllText(files.Module, """
            function Save-GzmGtfs {
                param([string]$PackageRoot, [string]$ServiceDate)
                $null = Get-FileHash -LiteralPath (Join-Path $PackageRoot 'DataRealTime/Slask/static/current/stops.txt')
                Write-Host 'Pobieranie rozkładu testowego'
                [pscustomobject]@{ ServiceDate = $ServiceDate; GtfsDirectory = 'DataRealTime/Slask/static/current' }
            }
            Export-ModuleMember -Function Save-GzmGtfs
            """, new UTF8Encoding(true));
        var console = new Terminal();
        Assert.Equal(files.Schedule, GzmStaticSchedulePreparation.Download(Day, files.DataRoot, console));
        Assert.Contains("Pobieranie rozkładu testowego", console.Lines);
    }

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "GZM próba O'Brien-" + Guid.NewGuid().ToString("N"));
        public string DataRoot => Path.Combine(Root, "DataRealTime", "Slask");
        public string Schedule => Path.Combine(DataRoot, "static", "current");
        public string Module => Path.Combine(Root, "AlgorithmComparison.CLI", "GzmData.psm1");
        public Files()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Module)!);
            File.WriteAllText(Module, "");
            Directory.CreateDirectory(Schedule);
            foreach (var name in new[] { "stops.txt", "routes.txt", "trips.txt", "stop_times.txt" })
                File.WriteAllText(Path.Combine(Schedule, name), "");
        }
        public string Manifest() => "GTFS_RESULT=" + JsonSerializer.Serialize(new
        { ServiceDate = "2026-11-03", GtfsDirectory = "DataRealTime/Slask/static/current" });
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Terminal : IInteractiveConsole
    {
        public List<string> Lines { get; } = new();
        public bool IsInputRedirected => false;
        public void Clear() { }
        public void WriteLine(string line) => Lines.Add(line);
        public ConsoleKey ReadKey() => throw new InvalidOperationException();
        public string? ReadLine() => throw new InvalidOperationException();
    }
}
