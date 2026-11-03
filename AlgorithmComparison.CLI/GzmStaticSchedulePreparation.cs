using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AlgorithmComparison.CLI;

public sealed record GtfsPreparationProcessResult(int ExitCode, string Output, string Error);

public static class GzmStaticSchedulePreparation
{
    private const string ResultPrefix = "GTFS_RESULT=";

    public static string Download(DateOnly day, string dataRoot, IInteractiveConsole terminal,
        Func<ProcessStartInfo, GtfsPreparationProcessResult>? run = null)
    {
        var packageRoot = Directory.GetParent(Path.GetFullPath(dataRoot))?.Parent?.FullName
            ?? throw new IOException("Nie można ustalić katalogu aplikacji dla pobrania GTFS.");
        var module = new[]
        {
            Path.Combine(packageRoot, "AlgorithmComparison.CLI", "GzmData.psm1"),
            Path.Combine(AppContext.BaseDirectory, "GzmData.psm1"),
            Path.Combine(packageRoot, "tools", "GzmData.psm1"),
            Path.Combine(packageRoot, "tools", "PortableRealtime", "GzmData.psm1")
        }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Brak modułu pobierania GTFS. Uruchom aplikację z całej rozpakowanej paczki.");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $ProgressPreference = 'SilentlyContinue'; " +
            "$ErrorActionPreference = 'Stop'; try { Import-Module " + Quote(module) + " -Force; " +
            "$result = Save-GzmGtfs -PackageRoot " + Quote(packageRoot) + " -ServiceDate " +
            Quote(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + "; " +
            "Write-Output ('" + ResultPrefix + "' + ($result | ConvertTo-Json -Compress)); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
        {
            WorkingDirectory = packageRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Remove("PSModulePath");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass",
            "-Command", script })
            start.ArgumentList.Add(argument);
        var result = (run ?? Run)(start);
        string? manifest = null;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimEnd('\r');
            if (value.StartsWith(ResultPrefix, StringComparison.Ordinal)) manifest = value[ResultPrefix.Length..];
            else terminal.WriteLine(value);
        }
        if (result.ExitCode != 0)
            throw new IOException("Nie udało się pobrać aktualnego rozkładu GTFS. Pomiar nie został uruchomiony. " + result.Error.Trim());
        if (manifest is null) throw new InvalidDataException("Pobieranie GTFS nie zwróciło informacji o przygotowanym rozkładzie.");
        using var json = JsonDocument.Parse(manifest);
        if (!json.RootElement.TryGetProperty("ServiceDate", out var date)
            || date.GetString() != day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            || !json.RootElement.TryGetProperty("GtfsDirectory", out var folder)
            || string.IsNullOrWhiteSpace(folder.GetString()))
            throw new InvalidDataException("Pobrany rozkład GTFS nie jest przygotowany na bieżący dzień.");
        var path = Path.GetFullPath(Path.Combine(packageRoot, folder.GetString()!));
        var staticRoot = Path.GetFullPath(Path.Combine(dataRoot, "static")) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(staticRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !new[] { "stops.txt", "routes.txt", "trips.txt", "stop_times.txt" }.All(name => File.Exists(Path.Combine(path, name))))
            throw new InvalidDataException("Pobieranie GTFS nie przygotowało kompletnego rozkładu w katalogu danych aplikacji.");
        return path;
    }

    private static GtfsPreparationProcessResult Run(ProcessStartInfo start)
    {
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new IOException("Nie można uruchomić PowerShell do pobrania GTFS.", ex); }
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10 * 60 * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("Przekroczono czas pobierania GTFS. Pomiar nie został uruchomiony.");
        }
        return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
