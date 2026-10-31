using System.Globalization;

namespace AlgorithmComparison.CLI;

public static class BenchmarkSeedMenu
{
    public static int Read(int? seed = null, IInteractiveConsole? terminal = null, TimeProvider? clock = null)
    {
        if (seed.HasValue) return seed.Value;
        terminal ??= new InteractiveConsole();
        if (terminal.IsInputRedirected)
            throw new ArgumentException("Menu wymaga interaktywnej konsoli. Użyj benchmark z opcjonalnym --seed.");
        while (true)
        {
            terminal.WriteLine("Ziarno generatora [Enter: automatyczne; lub wpisz własną liczbę]:");
            var text = terminal.ReadLine()?.Trim()
                ?? throw new OperationCanceledException("Zamknięto wejście konsoli.");
            if (text.Length == 0) return BenchmarkQueryWorkloadFactory.CreateRunSeed(clock);
            if (text.Equals("q", StringComparison.OrdinalIgnoreCase) || text.Equals("anuluj", StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("Anulowano konfigurację.");
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            terminal.WriteLine("Podaj liczbę całkowitą od -2147483648 do 2147483647 albo pozostaw puste pole.");
        }
    }
}
