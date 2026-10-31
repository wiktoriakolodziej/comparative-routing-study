using AlgorithmComparison.CLI;

namespace AlgorithmComparison.Tests.TransitRouting.Integration.Tests;

public sealed class BenchmarkSeedMenuTests
{
    private sealed class Input(params string?[] values) : IInteractiveConsole
    {
        private readonly Queue<string?> pending = new(values);
        public int Reads { get; private set; }
        public List<string> Lines { get; } = [];
        public bool IsInputRedirected => false;
        public void Clear() { }
        public void WriteLine(string line) => Lines.Add(line);
        public ConsoleKey ReadKey() => throw new InvalidOperationException();
        public string? ReadLine() { Reads++; return pending.Dequeue(); }
    }
    private sealed class Clock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ManualSeedIsPreserved(int seed)
    {
        var terminal = new Input(seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(seed, BenchmarkSeedMenu.Read(terminal: terminal));
    }
    [Fact]
    public void EmptyAnswerUsesClockAndGeneratesAnAcceptedSeed()
    {
        var clock = new Clock();
        var seed = BenchmarkSeedMenu.Read(terminal: new Input(""), clock: clock);
        Assert.Equal(1, clock.Reads);
        Assert.InRange(seed, 1, int.MaxValue);
        Assert.True(BenchmarkOptions.TryParse(new[] { "1", "--seed", seed.ToString() }, out var options, out _));
        Assert.Equal(seed, options!.Seed);
    }
    [Fact]
    public void InvalidSeedIsRetriedAndCommandLineSeedSkipsTheQuestion()
    {
        var terminal = new Input("bad", "2147483648", "-12");
        Assert.Equal(-12, BenchmarkSeedMenu.Read(terminal: terminal));
        Assert.Equal(3, terminal.Reads);
        var noAnswers = new Input();
        Assert.Equal(0, BenchmarkSeedMenu.Read(0, noAnswers));
        Assert.Equal(0, noAnswers.Reads);
    }
    [Theory]
    [InlineData("q")]
    [InlineData("anuluj")]
    [InlineData(null)]
    public void CancellationDoesNotStartMeasurements(string? answer)
    {
        Assert.Throws<OperationCanceledException>(() => BenchmarkSeedMenu.Read(terminal: new Input(answer)));
    }
}
