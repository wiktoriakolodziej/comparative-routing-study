using System.Globalization;
using System.Text.Json;
using AlgorithmComparison.Core.Models;
using AlgorithmComparison.GTFS;
using AlgorithmComparison.RaptorRealtime;
using TransitRealtime;

namespace AlgorithmComparison.CLI;

public sealed record RealtimeMenuSelection(string[] Arguments, TransitNetwork Network);

public static class RealtimeRaptorMenu
{
    public static RealtimeMenuSelection Read(IInteractiveConsole? terminal = null,
        Func<string, DateOnly, TransitNetwork>? loader = null, string? dataRoot = null, TimeProvider? clock = null,
        Func<DateOnly, string>? prepareLiveGtfs = null)
    {
        terminal ??= new InteractiveConsole();
        loader ??= (path, day) => new GtfsLoader().Load(path, day);
        clock ??= TimeProvider.System;
        dataRoot ??= FindDataRoot();
        var replay = ChoiceMenu.Select("RAPTOR Realtime — skąd pobierać informacje o opóźnieniach?", new[]
        {
            "Online — bieżące opóźnienia z API GZM (wymaga internetu)",
            "Offline — odtworzenie wcześniej zapisanej aktualizacji"
        }, terminal,
            "Online: program pobierze aktualny rozkład GTFS i będzie sprawdzać bieżące opóźnienia.\n" +
            "Offline: ponownie obliczy podróż na podstawie zapisanego pliku aktualizacji (.pb).\n" +
            "Tryb offline nie pobiera nowych danych i wymaga rozkładu z tej samej sesji co aktualizacja.") == 1;

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
        var evaluationTime = clock.GetUtcNow();
        string? snapshot = null;
        string gtfs;
        DateOnly date;
        if (replay)
        {
            snapshot = Path.GetFullPath(ReadText("Ścieżka do zapisanej aktualizacji opóźnień (.pb)", null, terminal));
            var feed = FeedMessage.Parser.ParseFrom(File.ReadAllBytes(snapshot));
            if (feed.Header is not { HasTimestamp: true })
                throw new InvalidDataException("Zapisana aktualizacja musi zawierać datę i czas utworzenia.");
            evaluationTime = DateTimeOffset.FromUnixTimeSeconds(checked((long)feed.Header.Timestamp));
            var defaultDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evaluationTime, zone).DateTime);
            terminal.WriteLine($"Aktualizacja została utworzona: {TimeZoneInfo.ConvertTime(evaluationTime, zone):dd.MM.yyyy HH:mm:ss} (czas w Polsce).");
            while (!DateOnly.TryParseExact(ReadText("Dzień rozkładu (yyyy-MM-dd)", defaultDate.ToString("yyyy-MM-dd"), terminal),
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                terminal.WriteLine("Podaj poprawną datę.");
            var directories = Directory.Exists(Path.Combine(dataRoot, "static"))
                ? Directory.GetDirectories(Path.Combine(dataRoot, "static")).OrderDescending(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            var selected = directories.Length == 0 ? 0 : ChoiceMenu.Select("Wybierz zapisany rozkład do odtworzenia pomiaru",
                directories.Select(ScheduleLabel).Append("Wskaż inny folder z rozkładem GTFS…").ToArray(), terminal,
                "To zapisane kopie rozkładów, a nie różne warianty algorytmu.\n" +
                "Data opisuje dzień przygotowania kopii. Końcówka nazwy odróżnia kolejne pobrania.\n" +
                "Wybierz rozkład użyty przy zapisie aktualizacji; sama zgodność dat nie gwarantuje zgodności kursów.");
            gtfs = Path.GetFullPath(selected < directories.Length ? directories[selected] : ReadText("Folder z rozkładem GTFS", null, terminal));
        }
        else
        {
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evaluationTime, zone).DateTime);
            terminal.WriteLine($"Pobieranie aktualnego rozkładu GZM na dzień {date:dd.MM.yyyy}. Wymagane połączenie z internetem.");
            gtfs = Path.GetFullPath((prepareLiveGtfs ?? (day => GzmStaticSchedulePreparation.Download(day, dataRoot, terminal)))(date));
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime) != date)
                throw new InvalidDataException("Podczas przygotowania zmienił się dzień. Uruchom konfigurację ponownie, aby pobrać właściwy rozkład.");
            terminal.WriteLine($"Wybrano automatycznie pobrany rozkład: {gtfs}");
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
        terminal.WriteLine("Wpisz godzinę wyjazdu albo now, aby w każdym pomiarze rozpoczynać podróż o aktualnej godzinie.");
        string departure;
        while (true)
        {
            departure = ReadText("Godzina wyjazdu (HH:mm:ss lub now; godziny po północy mogą przekraczać 23)", defaultDeparture, terminal);
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
        terminal.WriteLine("Aktualność danych jest sprawdzana według czasu ich utworzenia.");
        terminal.WriteLine("Przeterminowane prognozy nie są używane - przy braku aktualnych danych program korzysta z godzin rozkładowych.");
        var maxAge = ReadNumber("Jak długo uznawać informacje o opóźnieniach za aktualne? (sekundy)", 300, 1, int.MaxValue, terminal);
        var interval = 30;
        var count = 1;
        if (!replay)
        {
            interval = ReadNumber("Czas oczekiwania przed kolejnym pomiarem (sekundy)", 30, 1, 86400, terminal);
            var continuous = ChoiceMenu.Select("Kiedy zakończyć badanie?", new[]
            {
                "Po wykonaniu określonej liczby pomiarów",
                "Dopiero po ręcznym zatrzymaniu skrótem Ctrl+C"
            }, terminal, "Jeden pomiar obejmuje pobranie aktualizacji i porównanie wariantów RAPTOR-a.") == 1;
            count = continuous ? int.MaxValue : ReadNumber("Liczba kolejnych pomiarów", 10, 1, int.MaxValue, terminal);
        }
        var output = Path.GetFullPath(ReadText("Ścieżka do zapisu raportu wynikowego",
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
        else args.AddRange(new[] { "--watch", "--count", count.ToString(CultureInfo.InvariantCulture),
            "--interval-seconds", interval.ToString(CultureInfo.InvariantCulture), "--archive", Path.Combine(dataRoot, "live") });
        terminal.WriteLine(replay ? "Odtwarzanie zapisanej aktualizacji — internet nie jest wymagany." : "Badanie na żywo — WYMAGANE POŁĄCZENIE Z INTERNETEM.");
        terminal.WriteLine($"GTFS: {gtfs}; dzień: {date:yyyy-MM-dd}; {from} → {to}; {(pareto ? "Pareto" : "single")}; wyjazd: {departure}.");
        terminal.WriteLine("Porównanie zwykłego RAPTOR-a, kontrolnego rozkładu bazowego i realtime: 1 rozgrzewka + 5 powtórzeń każdego wariantu.");
        terminal.WriteLine($"Limit przesiadek: {transfers}; promień: {radius} m; prognozy ważne przez najwyżej {maxAge} s od ich utworzenia.");
        if (!replay)
        {
            terminal.WriteLine($"Oczekiwanie po każdym pomiarze: {interval} s; zakończenie: {(count == int.MaxValue ? "ręcznie, skrótem Ctrl+C" : "po " + count + " pomiarach")}.");
            terminal.WriteLine($"Pobrane aktualizacje: {Path.Combine(dataRoot, "live")}.");
        }
        terminal.WriteLine($"Ścieżka do zapisu raportu wynikowego: {output}.");
        _ = ReadText("Enter: rozpocznij; wpisz anuluj, aby zakończyć", "start", terminal);
        if (!replay && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime) != date)
            throw new InvalidDataException("Zmienił się dzień. Uruchom konfigurację ponownie, aby pobrać rozkład na bieżący dzień.");
        return new(args.ToArray(), network);
    }

    private static string ScheduleLabel(string directory)
    {
        var name = Path.GetFileName(directory);
        DateOnly? day = name.StartsWith("gtfs-", StringComparison.Ordinal) && name.Length >= 13
            && DateOnly.TryParseExact(name.Substring(5, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        var manifest = Path.Combine(directory, "download-manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                if (json.RootElement.TryGetProperty("DownloadedAt", out var at)
                    && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var downloaded))
                    return $"{(day.HasValue ? day.Value.ToString("dd.MM.yyyy") : name)} — pobrano {TimeZoneInfo.ConvertTime(downloaded, TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")):dd.MM.yyyy HH:mm:ss} ({name})";
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return day.HasValue ? $"{day.Value:dd.MM.yyyy} — zapisany rozkład ({name})" : name;
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
