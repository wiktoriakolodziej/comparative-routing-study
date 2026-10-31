using System.Collections.ObjectModel;
using AlgorithmComparison.Core.Models;

namespace AlgorithmComparison.Core.Walking;

public enum WalkingDirection { ForwardEarliestArrival, BackwardLatestDeparture }
public sealed record WalkingSeed<TContext>(string StopId, int Time, int Boardings, TContext Context);
public sealed class WalkingLabel<TContext>
{
    public string StopId { get; }
    public int Time { get; }
    public int Boardings => Origin.Boardings;
    public WalkingSeed<TContext> Origin { get; }
    public bool HasWalk => Step is not null;
    public WalkingDirection Direction { get; }
    public WalkingLabel<TContext>? Parent { get; }
    public Footpath? Step { get; }

    internal WalkingLabel(string stopId, int time, WalkingSeed<TContext> origin,
        WalkingDirection direction, WalkingLabel<TContext>? parent = null, Footpath? step = null)
    {
        StopId = stopId; Time = time; Origin = origin; Direction = direction; Parent = parent; Step = step;
    }
    public IReadOnlyList<Footpath> GetPath()
    {
        var edges = new List<Footpath>();
        for (var label = this; label.Step is not null; label = label.Parent!) edges.Add(label.Step);
        if (Direction == WalkingDirection.ForwardEarliestArrival) edges.Reverse();
        return edges.AsReadOnly();
    }
}
public sealed class WalkingPropagationResult<TContext>
{
    public WalkingDirection Direction { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<WalkingLabel<TContext>>> LabelsByStop { get; }
    public IReadOnlyDictionary<string, int> TimesByStop { get; }

    internal WalkingPropagationResult(WalkingDirection direction,
        Dictionary<string, List<WalkingLabel<TContext>>> labels)
    {
        Direction = direction;
        LabelsByStop = new ReadOnlyDictionary<string, IReadOnlyList<WalkingLabel<TContext>>>(
            labels.ToDictionary(p => p.Key, p => (IReadOnlyList<WalkingLabel<TContext>>)Array.AsReadOnly(
                (direction == WalkingDirection.ForwardEarliestArrival
                    ? p.Value.OrderBy(label => label.Time).ThenBy(label => label.Boardings)
                    : p.Value.OrderByDescending(label => label.Time).ThenBy(label => label.Boardings)).ToArray()), StringComparer.Ordinal));
        TimesByStop = new ReadOnlyDictionary<string, int>(LabelsByStop.ToDictionary(p => p.Key, p => p.Value[0].Time, StringComparer.Ordinal));
    }
}
