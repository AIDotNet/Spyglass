using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

public readonly record struct OverviewBar(double StartMs, double EndMs);

/// <summary>Bars of the Overview strip on a time axis starting at <see cref="Origin"/> (design/network/02-chrome-parity.md §5).</summary>
public sealed record OverviewModel(DateTimeOffset Origin, double TotalMs, IReadOnlyList<OverviewBar> Bars)
{
    public static OverviewModel Empty { get; } = new(DateTimeOffset.MinValue, 1, []);
    public DateTimeOffset At(double ms) => Origin.AddMilliseconds(ms);
}

/// <summary>Pure logic of the Overview strip and its "filter by time range" (Chrome: drag to select, inclusive).</summary>
public static class NetworkOverview
{
    public static DateTimeOffset EndOf(NetworkRequestDto r, DateTimeOffset now) =>
        NetworkSort.EndTime(r) is { } e && e >= r.StartTime ? e : r.State == NetworkState.Pending ? now : r.StartTime;

    public static OverviewModel Build(IReadOnlyCollection<NetworkRequestDto> requests, DateTimeOffset now)
    {
        if (requests.Count == 0) return OverviewModel.Empty;
        var origin = requests.Min(r => r.StartTime);
        var bars = requests.Select(r => new OverviewBar((r.StartTime - origin).TotalMilliseconds, (EndOf(r, now) - origin).TotalMilliseconds)).ToList();
        return new OverviewModel(origin, Math.Max(1, bars.Max(b => b.EndMs)), bars);
    }

    /// <summary>A request is shown when it was active at any moment of [from, to] (inclusive at both ends). Null bounds = no filter.</summary>
    public static bool InRange(NetworkRequestDto r, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
    {
        if (from is null || to is null) return true;
        var (lo, hi) = from <= to ? (from.Value, to.Value) : (to.Value, from.Value);
        return r.StartTime <= hi && EndOf(r, now) >= lo;
    }

    /// <summary>Bar lanes (greedy packing) so overlapping requests do not hide each other. Returns the lane of each bar.</summary>
    public static int[] Lanes(IReadOnlyList<OverviewBar> bars, int laneCount)
    {
        laneCount = Math.Max(1, laneCount);
        var lanes = new int[bars.Count];
        var ends = new double[laneCount];
        Array.Fill(ends, double.NegativeInfinity);
        foreach (var i in Enumerable.Range(0, bars.Count).OrderBy(i => bars[i].StartMs))
        {
            var lane = Array.FindIndex(ends, e => e <= bars[i].StartMs);
            if (lane < 0) lane = i % laneCount;
            lanes[i] = lane;
            ends[lane] = Math.Max(ends[lane], bars[i].EndMs);
        }
        return lanes;
    }
}
