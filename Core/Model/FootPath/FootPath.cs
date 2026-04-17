namespace RouteAnalyzer.Core.Model.FootPath;

/// <summary>
/// Połączenie piesze między dwoma przystankami (krawędź grafu pieszego).
/// Graf musi być domknięty przechodnio i spełniać nierówność trójkąta,
/// dlatego budujemy go algorytmem Floyda-Warshalla.
/// </summary>
public sealed record FootPath(
    int DepartureStopId,
    int ArrivalStopId,
    int DurationSeconds);

