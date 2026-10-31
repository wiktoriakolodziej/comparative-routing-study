namespace AlgorithmComparison.CLI;

public interface IInteractiveConsole : ICheckboxConsole
{
    string? ReadLine();
}

public sealed class InteractiveConsole : IInteractiveConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected || Console.IsOutputRedirected;
    public void Clear() => Console.Clear();
    public void WriteLine(string line) => Console.WriteLine(line);
    public ConsoleKey ReadKey() => Console.ReadKey(intercept: true).Key;
    public string? ReadLine() => Console.ReadLine();
}

public static class ChoiceMenu
{
    public static int Select(string title, IReadOnlyList<string> labels, ICheckboxConsole terminal)
    {
        if (terminal.IsInputRedirected)
            throw new ArgumentException("Menu wymaga interaktywnej konsoli. W skryptach użyj benchmark lub raptor-realtime z parametrami.");
        if (labels.Count == 0) throw new ArgumentException("Brak dostępnych pozycji.", nameof(labels));
        var cursor = 0;
        while (true)
        {
            terminal.Clear();
            terminal.WriteLine(title);
            terminal.WriteLine("");
            for (var i = 0; i < labels.Count; i++)
                terminal.WriteLine($"{(i == cursor ? ">" : " ")} {labels[i]}");
            terminal.WriteLine("");
            terminal.WriteLine("Strzałki: wybór  Enter: zatwierdź  Esc: anuluj");
            switch (terminal.ReadKey())
            {
                case ConsoleKey.UpArrow: cursor = (cursor + labels.Count - 1) % labels.Count; break;
                case ConsoleKey.DownArrow: cursor = (cursor + 1) % labels.Count; break;
                case ConsoleKey.Home: cursor = 0; break;
                case ConsoleKey.End: cursor = labels.Count - 1; break;
                case ConsoleKey.Enter: return cursor;
                case ConsoleKey.Escape: throw new OperationCanceledException("Anulowano wybór.");
            }
        }
    }
}

public enum ApplicationMode { StaticBenchmarks, Realtime }

public static class ApplicationMenu
{
    public static ApplicationMode Select(ICheckboxConsole? terminal = null)
        => (ApplicationMode)ChoiceMenu.Select("Wybierz tryb pracy", new[]
        {
            "Porównanie algorytmów — rozkład statyczny (single i Pareto)",
            "RAPTOR Realtime — badania na żywo WYMAGAJĄ INTERNETU; dostępne także odtwarzanie offline"
        }, terminal ?? new InteractiveConsole());
}
