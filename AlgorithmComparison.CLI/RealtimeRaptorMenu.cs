using System.Globalization;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.GTFS;
using AlgorithmComparison.RaptorRealtime;
using TransitRealtime;

namespace AlgorithmComparison.CLI;

public sealed record RealtimeMenuSelection(string[] Arguments, TransitNetwork Network);

public static class RealtimeRaptorMenu
{
    public static RealtimeMenuSelection Read(IInteractiveConsole? terminal = null,
        Func<string, DateOnly, TransitNetwork>? loader = null, string? dataRoot = null, TimeProvider? clock = null)
    {
        terminal ??= new InteractiveConsole();
        loader ??= (path, day) => new GtfsLoader().Load(path, day);
        clock ??= TimeProvider.System;
        dataRoot ??= FindDataRoot();
        var replay = ChoiceMenu.Select("RAPTOR Realtime — źródło aktualizacji", new[]
        {
            "Na żywo: API GZM — WYMAGANE POŁĄCZENIE Z INTERNETEM",
            "Offline: odtworzenie zapisanej migawki TripUpdates (.pb)"
        }, terminal) == 1;

        var directories = Directory.Exists(Path.Combine(dataRoot, "static"))
            ? Directory.GetDirectories(Path.Combine(dataRoot, "static")).OrderDescending(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var selected = directories.Length == 0 ? 0 : ChoiceMenu.Select(
            "Wybierz rozkład GTFS zgodny z dniem i identyfikatorami aktualizacji",
            directories.Select(Path.GetFileName).Select(n => n!).Append("Inny folder…").ToArray(), terminal);
        var gtfs = selected < directories.Length ? directories[selected] : ReadText("Folder GTFS", null, terminal);
        gtfs = Path.GetFullPath(gtfs);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var evaluationTime = clock.GetUtcNow();
        string? snapshot = null;
        if (replay)
        {
            snapshot = Path.GetFullPath(ReadText("Plik migawki tripUpdates.pb", null, terminal));
            var feed = FeedMessage.Parser.ParseFrom(File.ReadAllBytes(snapshot));
            if (feed.Header is not { HasTimestamp: true })
                throw new InvalidDataException("Migawka do odtworzenia musi zawierać timestamp nagłówka.");
            evaluationTime = DateTimeOffset.FromUnixTimeSeconds(checked((long)feed.Header.Timestamp));
        }
        var defaultDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evaluationTime, zone).DateTime);
        DateOnly date;
        while (true)
        {
            var text = ReadText("Dzień rozkładu (yyyy-MM-dd)", defaultDate.ToString("yyyy-MM-dd"), terminal);
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                && (replay || date == defaultDate)) break;
            terminal.WriteLine(replay ? "Podaj poprawną datę." : "Tryb na żywo wymaga rozkładu na bieżący dzień w Polsce.");
        }
        terminal.WriteLine("Wczytywanie GTFS…");
        var network = loader(gtfs, date);
        if (network.ServiceDate != date || network.Trips.Count == 0)
            throw new InvalidDataException("Brak aktywnych kursów na wybrany dzień lub niezgodny dzień rozkładu.");
        terminal.WriteLine($"Wczytano {network.Stops.Count} przystanków i {network.Trips.Count} kursów.");
        var from = ReadStop("Przystanek początkowy", network, terminal);
        var to = ReadStop("Przystanek końcowy", network, terminal);
        var pareto = ChoiceMenu.Select("Wariant wyszukiwania", new[]
        { "Single — najwcześniejszy przyjazd", "Pareto — czas przyjazdu i liczba przesiadek" }, terminal) == 1;
        var defaultDeparture = replay ? FormatTime(ServiceDayTime.ToSeconds(evaluationTime.ToUnixTimeSeconds(), date, zone)) : "now";
        string departure;
        while (true)
        {
            departure = ReadText("Godzina wyjazdu (HH:mm:ss lub now; dopuszczalne godziny > 23)", defaultDeparture, terminal);
            if (departure == "now") break;
            var parts = departure.Split(':');
            if (parts.Length == 2) departure += ":00";
            parts = departure.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out var h) && h >= 0 && h <= (int.MaxValue - 3599) / 3600
                && int.TryParse(parts[1], out var m) && m is >= 0 and < 60
                && int.TryParse(parts[2], out var s) && s is >= 0 and < 60) break;
            terminal.WriteLine("Niepoprawny czas. Podaj np. 17:30:00 albo now.");
        }
        var transfers = ReadNumber("Maksymalna liczba przesiadek", 7, 0, int.MaxValue - 1, terminal);
        double radius;
        while (true)
        {
            var text = ReadText("Promień pojedynczego przejścia pieszego w metrach", "500", terminal).Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) && double.IsFinite(radius) && radius >= 0) break;
            terminal.WriteLine("Podaj nieujemną, skończoną liczbę.");
        }
        var maxAge = ReadNumber("Maksymalny wiek danych w sekundach", 300, 1, int.MaxValue, terminal);
        var interval = replay ? 30 : ReadNumber("Przerwa między cyklami w sekundach", 30, 1, 86400, terminal);
        var count = replay ? 1 : ReadNumber("Liczba cykli (0 = do Ctrl+C)", 10, 0, int.MaxValue, terminal);
        var output = Path.GetFullPath(ReadText("Raport JSON (wynik ostatniego cyklu)",
            Path.Combine(dataRoot, "validation", $"session-{(replay ? "replay" : "live")}-{clock.GetUtcNow():yyyyMMddTHHmmssfffZ}.json"), terminal));
        var args = new List<string>
        {
            "--compare", "--gtfs", gtfs, "--date", date.ToString("yyyy-MM-dd"), "--from", from, "--to", to,
            "--departure", departure, "--max-transfers", transfers.ToString(CultureInfo.InvariantCulture),
            "--radius-metres", radius.ToString(CultureInfo.InvariantCulture),
            "--max-age-seconds", maxAge.ToString(CultureInfo.InvariantCulture), "--out", output
        };
        if (pareto) args.Add("--pareto");
        if (replay) args.AddRange(new[] { "--snapshot", snapshot!, "--replay" });
        else args.AddRange(new[] { "--watch", "--count", (count == 0 ? int.MaxValue : count).ToString(CultureInfo.InvariantCulture),
            "--interval-seconds", interval.ToString(CultureInfo.InvariantCulture), "--archive", Path.Combine(dataRoot, "live") });
        terminal.WriteLine( replay ? "Odtwarzanie offline — internet nie jest wymagany." : "Badanie na żywo — WYMAGANE POŁĄCZENIE Z INTERNETEM.");
        terminal.WriteLine($"GTFS: {gtfs}; dzień: {date:yyyy-MM-dd}; {from} → {to}; {(pareto ? "Pareto" : "single")}; wyjazd: {departure}.");
        terminal.WriteLine("Benchmark porówna zwykły RAPTOR, kontrolny rozkład bazowy i realtime: 1 rozgrzewka + 5 powtórzeń każdego wariantu.");
        terminal.WriteLine($"Limit przesiadek: {transfers}; promień: {radius} m; maksymalny wiek danych: {maxAge} s.");
        if (!replay) terminal.WriteLine($"Przerwa: {interval} s; cykle: {(count == 0 ? "do Ctrl+C" : count.ToString())}.");
        terminal.WriteLine($"Raport ostatniego cyklu: {output}. Surowe migawki online: {Path.Combine(dataRoot, "live")}.");
        _ = ReadText("Enter: rozpocznij; wpisz anuluj, aby zakończyć", "start", terminal);
        return new(args.ToArray(), network);
    }

    private static string ReadStop(string prompt, TransitNetwork network, IInteractiveConsole terminal)
    {
        while (true)
        {
            var text = ReadText(prompt + " (ID lub fragment nazwy)", null, terminal);
            if (network.Stops.ContainsKey(text)) return text;
            var matches = network.Stops.Values.Where(s => s.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).Take(21).ToArray();
            if (matches.Length == 1) { terminal.WriteLine($"Wybrano {matches[0].Name} [{matches[0].Id}]."); return matches[0].Id; }
            if (matches.Length is > 1 and <= 20)
                return matches[ChoiceMenu.Select(prompt, matches.Select(s => $"{s.Name} [{s.Id}]").ToArray(), terminal)].Id;
            terminal.WriteLine(matches.Length == 0 ? "Nie znaleziono przystanku." : "Ponad 20 wyników — podaj dokładniejszą nazwę lub ID.");
        }
    }

    private static int ReadNumber(string prompt, int fallback, int min, int max, IInteractiveConsole terminal)
    {
        while (true)
        {
            if (int.TryParse(ReadText(prompt, fallback.ToString(CultureInfo.InvariantCulture), terminal), out var n) && n >= min && n <= max) return n;
            terminal.WriteLine($"Podaj liczbę całkowitą od {min} do {max}.");
        }
    }

    private static string ReadText(string prompt, string? fallback, IInteractiveConsole terminal)
    {
        while (true)
        {
            terminal.WriteLine(fallback is null ? prompt + ":" : $"{prompt} [{fallback}]:");
            var line = terminal.ReadLine()?.Trim().Trim('"') ?? throw new OperationCanceledException("Zamknięto wejście konsoli.");
            if (line.Equals("anuluj", StringComparison.OrdinalIgnoreCase) || line.Equals("q", StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("Anulowano konfigurację.");
            if (line.Length != 0) return line;
            if (fallback is not null) return fallback;
        }
    }

    private static string FormatTime(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";

    public static string FindDataRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "DataRealTime", "Slask");
                if (Directory.Exists(path)) return path;
            }
        return Path.GetFullPath(Path.Combine("DataRealTime", "Slask"));
    }
}
