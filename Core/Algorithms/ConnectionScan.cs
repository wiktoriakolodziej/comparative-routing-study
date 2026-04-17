using RouteAnalyzer.Core.Model.Connection;
using RouteAnalyzer.Core.Model.FootPath;
using RouteAnalyzer.Core.Model.GtfsModel;
using static System.Formats.Asn1.AsnWriter;

namespace RouteAnalyzer.Core.Algorithms;

//todo: implementacja ścieżki, która będzie zwracana w Result
//todo: implementacja GetNearestConnectionIndex (np. binarne wyszukiwanie po _connections)
public class ConnectionScan : IRouteFinder
{
    private readonly Connection[] _connections;
    private readonly Dictionary<int, List<FootPath>> _footPathsByStopId;
    private int[] S;
    private Connection?[] T;
    private (Connection? enterConnection, Connection? endConnection, FootPath? footpath)[] J;
    private Action<int, int> _algorithm;
    public ConnectionScan(List<StopTime> stopTimes, Dictionary<int, Stop> stops, int tripCount)
    {
        _connections = ConnectionBuilder.Build(stopTimes);
        _footPathsByStopId = FootPathBuilder.Build(stops);
        S = Enumerable.Repeat(int.MaxValue, stops.Count).ToArray();
        T = Enumerable.Repeat((Connection?)null, tripCount).ToArray();
        J = Enumerable.Repeat(((Connection?)null, (Connection?)null, (FootPath?)null), stops.Count).ToArray();
        _algorithm = RunUnoptimized;
    }
    public void SetUnoptimized() => _algorithm = RunUnoptimized;
    public void SetOptimized() => _algorithm = RunOptimized;
    public Result FindShortestPath(int startStop, int targetStop, int startTime)
    {
        Reset();
        Initialize(startStop, startTime);
        _algorithm(targetStop, startTime);
        var journey = GetJourney(targetStop);
        return new SuccessResult<(Connection? enterConnection, Connection? endConnection, FootPath? footpath)[]>(journey);
    }

    private void RunUnoptimized(int targetStop, int startTime)
    {
        foreach (var c in _connections)
        {
            if (T[c.TripId] is null && S[c.DepartureStopId] > c.DepartureTime)
                continue;

            if (T[c.TripId] is null)
                T[c.TripId] = c;

            foreach (var footPath in _footPathsByStopId[c.DepartureStopId])
            {
                if (S[footPath.ArrivalStopId] <= c.ArrivalTime + footPath.DurationSeconds)
                    continue;

                S[footPath.ArrivalStopId] = c.ArrivalTime + footPath.DurationSeconds;
                J[footPath.ArrivalStopId] = (T[c.TripId], c, footPath);
            }
        }
    }

    private void RunOptimized(int targetStop, int startTime)
    {
        for (int i = GetNearestConnectionIndex(startTime); i < _connections.Length; i++)
        {
            var c = _connections[i];
            if (S[targetStop] <= c.DepartureTime)
                break;

            if (T[c.TripId] is null && S[c.DepartureStopId] > c.DepartureTime)
                continue;

            if (T[c.TripId] is null)
                T[c.TripId] = c;

            if (S[c.ArrivalStopId] <= c.ArrivalTime)
                continue;

            foreach (var footPath in _footPathsByStopId[c.DepartureStopId])
            {
                if (S[footPath.ArrivalStopId] <= c.ArrivalTime + footPath.DurationSeconds)
                    continue;

                S[footPath.ArrivalStopId] = c.ArrivalTime + footPath.DurationSeconds;
                J[footPath.ArrivalStopId] = (T[c.TripId], c, footPath);
            }
        }
    }

    private int GetNearestConnectionIndex(int startTime)
    {
        var startIndex = 0;
        var endIndex = _connections.Length - 1;
        var middleIndex = (startIndex + endIndex) / 2;
        var finishPredicate = new Func<int, bool>(index =>
            (index == _connections.Length - 1 ||
            (_connections[index].DepartureTime < startTime && _connections[index + 1].DepartureTime >= startTime)));
        
        while (!finishPredicate(middleIndex))
        {
            if (_connections[middleIndex].DepartureTime < startTime)
                startIndex = middleIndex + 1;
            else
                endIndex = middleIndex - 1;

            middleIndex = (startIndex + endIndex) / 2;
        }
        return middleIndex;
    } 

    private void Initialize(int startStop, int startTime)
    {
        S[startStop] = startTime;
        foreach (var footPath in _footPathsByStopId[startStop])
        {
            S[footPath.ArrivalStopId] = startTime + footPath.DurationSeconds;
        }
    }

    private void Reset()
    {
        S = Enumerable.Repeat(int.MaxValue, S.Length).ToArray();
        T = Enumerable.Repeat((Connection?)null, T.Length).ToArray();
        J = Enumerable.Repeat(((Connection?)null, (Connection?)null, (FootPath?)null), J.Length).ToArray();
    }

    private (Connection? enterConnection, Connection? endConnection, FootPath? footpath)[] GetJourney(int targetStop)
    {
        var journey = new Stack<(Connection? enterConnection, Connection? endConnection, FootPath? footpath)>();
        var t = targetStop;
        while (t != -1 && J[t] != (null, null, null))
        {
            journey.Push(J[t]);
            t = J[t].enterConnection?.DepartureStopId ?? -1;
        }
        return journey.ToArray();
    }
}
