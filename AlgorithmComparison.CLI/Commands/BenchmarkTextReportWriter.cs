using System.Globalization;
using System.Text;

namespace AlgorithmComparison.CLI.Commands;

public static class BenchmarkTextReportWriter
{
    public static string FileName(string city, string algorithm, string mode)
    {
        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        return $"{Safe(city)}-{Safe(algorithm)}-{(mode == "earliest" ? "single" : "pareto")}.txt";
    }
    public static string Write(BenchmarkWorkerResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(result.CityName, result.Algorithm, result.Mode));
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        string N(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "n/a";
        var samples = result.Samples.Where(s => !s.IsWarmup).ToArray();
        var times = samples.Where(s => s.Error is null && s.Metrics is not null).Select(s => s.Metrics!.QueryTimeMs).Order().ToArray();
        w.WriteLine($"WYNIKI BENCHMARKU - {result.CityName}");
        w.WriteLine($"Algorytm: {BenchmarkAlgorithmCatalog.DisplayName(result.Algorithm)}");
        w.WriteLine($"Wariant: {(result.Mode == "earliest" ? "single" : "Pareto")}");
        w.WriteLine($"Dzien rozkladu: {result.ServiceDate}");
        w.WriteLine($"Maksymalna liczba przesiadek: {result.MaxTransfers} (single i Pareto)");
        w.WriteLine($"Graf pieszy: radius, promien {N(result.WalkingRadiusMeters)} m, multi-hop");
        w.WriteLine("Przejscia piesze: propagacja sekwencyjna (FIFO).");
        w.WriteLine($"Zapytania: {result.QueryCount}; powtorzenia: {result.Repetitions}; rozgrzewka: {result.WarmupCount}");
        w.WriteLine($"Seed: {result.MasterSeed}; seed zbioru: {result.DatasetSeed}");
        w.WriteLine($"Pomiarow: {samples.Length}; bez bledow: {times.Length}; bledy: {samples.Count(s => s.Error is not null)}");
        w.WriteLine($"Ladowanie sieci [ms]: {N(result.NetworkLoadMs)}; graf [ms]: {N(result.FootpathGraphBuildMs)}; Prepare [ms]: {N(result.PrepareWallMs)}");
        w.WriteLine($"Working set po Prepare [B]: {result.WorkingSetAfterPrepareBytes}; peak procesu [B]: {result.PeakWorkingSetBytes}");
        if (times.Length > 0)
        {
            var median = times.Length % 2 == 0 ? (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2 : times[times.Length / 2];
            w.WriteLine($"Srednia [ms]: {N(times.Average())}; mediana [ms]: {N(median)}; P95 [ms]: {N(times[(int)Math.Ceiling(.95 * times.Length) - 1])}");
        }
        w.WriteLine("Sygnatura: przyjazd_w_sekundach|przesiadki|punkty_Pareto; unreachable = brak polaczenia.");
        w.WriteLine("Rozgrzewka jest oznaczona osobno i nie wchodzi do statystyk. Alokacje to suma utworzonych danych, nie stale zajety RAM.");
        w.WriteLine();
        w.WriteLine("zapytanie\tpowtorzenie\trozgrzewka\tokno\tod\tdo\tstart_s\tczas_ms\talokacje_B\twynik_lub_blad");
        foreach (var s in result.Samples)
            w.WriteLine($"{s.QueryIndex}\t{s.Repetition}\t{s.IsWarmup}\t{s.TimeWindow}\t{s.FromStopId}\t{s.ToStopId}\t{s.DepartureTime}\t{N(s.Metrics?.QueryTimeMs)}\t{s.Metrics?.AllocatedBytes}\t{(s.Error ?? s.Signature)?.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')}");
        return path;
    }
}
