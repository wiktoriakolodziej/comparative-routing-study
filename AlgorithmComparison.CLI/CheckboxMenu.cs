namespace AlgorithmComparison.CLI;

public enum CheckboxAction { Continue, Empty, Confirm, Cancel }

public sealed class CheckboxSelectionState
{
    public bool[] Selected { get; }
    public int Cursor { get; private set; }
    public CheckboxSelectionState(IEnumerable<bool> selected)
    {
        Selected = selected.ToArray();
        if (Selected.Length == 0) throw new ArgumentException("Menu has no choices.");
    }
    public CheckboxAction Apply(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow: Cursor = (Cursor + Selected.Length - 1) % Selected.Length; break;
            case ConsoleKey.DownArrow: Cursor = (Cursor + 1) % Selected.Length; break;
            case ConsoleKey.Home: Cursor = 0; break;
            case ConsoleKey.End: Cursor = Selected.Length - 1; break;
            case ConsoleKey.Spacebar: Selected[Cursor] = !Selected[Cursor]; break;
            case ConsoleKey.A: Array.Fill(Selected, true); break;
            case ConsoleKey.N: Array.Fill(Selected, false); break;
            case ConsoleKey.Enter: return Selected.Any(x => x) ? CheckboxAction.Confirm : CheckboxAction.Empty;
            case ConsoleKey.Escape: return CheckboxAction.Cancel;
        }
        return CheckboxAction.Continue;
    }
}

public interface ICheckboxConsole
{
    bool IsInputRedirected { get; }
    void Clear();
    void WriteLine(string line);
    ConsoleKey ReadKey();
}

public static class CheckboxMenu
{
    private sealed class Terminal : ICheckboxConsole
    {
        public bool IsInputRedirected => Console.IsInputRedirected || Console.IsOutputRedirected;
        public void Clear() => Console.Clear();
        public void WriteLine(string line) => Console.WriteLine(line);
        public ConsoleKey ReadKey() => Console.ReadKey(intercept: true).Key;
    }
    public static int[] Select(string title, IReadOnlyList<string> labels, IEnumerable<bool> selected,
        ICheckboxConsole? terminal = null)
    {
        terminal ??= new Terminal();
        if (terminal.IsInputRedirected)
            throw new ArgumentException("Menu wymaga interaktywnej konsoli. Do skryptow uzyj benchmark --variants ... --datasets ...");
        var state = new CheckboxSelectionState(selected);
        if (state.Selected.Length != labels.Count) throw new ArgumentException("Mismatched menu choices.");
        var message = "";
        while (true)
        {
            terminal.Clear(); terminal.WriteLine(title); terminal.WriteLine("");
            for (var i = 0; i < labels.Count; i++)
                terminal.WriteLine($"{(i == state.Cursor ? ">" : " ")} [{(state.Selected[i] ? "x" : " ")}] {labels[i]}");
            terminal.WriteLine("");
            terminal.WriteLine("Strzalki: wybor  Spacja: zaznacz  A: wszystkie  N: zadne");
            terminal.WriteLine("Enter: zatwierdz  Esc: anuluj"); terminal.WriteLine(message);
            switch (state.Apply(terminal.ReadKey()))
            {
                case CheckboxAction.Confirm:
                    return Enumerable.Range(0, labels.Count).Where(i => state.Selected[i]).ToArray();
                case CheckboxAction.Cancel: throw new OperationCanceledException("Anulowano benchmark.");
                case CheckboxAction.Empty: message = "Zaznacz co najmniej jedna pozycje."; break;
                default: message = ""; break;
            }
        }
    }
}
