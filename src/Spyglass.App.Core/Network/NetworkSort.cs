using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

/// <summary>Request-table columns (design/network/02-chrome-parity.md §3.1).</summary>
public enum NetworkColumn
{
    Name, Id, Method, Status, Type, Protocol, Scheme, Domain, Remote, Connection, Initiator, Cookies, SetCookies, Size, Time, Waterfall,
}

/// <summary>Chrome's Waterfall header menu: Start Time / Response Time / End Time / Total Duration / Latency (§3.2).</summary>
public enum WaterfallSort { StartTime, ResponseTime, EndTime, TotalDuration, Latency }

/// <summary>Current sort. <see cref="Column"/> null (and no <see cref="Custom"/>) = chronological, Chrome's default.</summary>
public sealed record NetworkSortSpec(NetworkColumn? Column = null, bool Descending = false, WaterfallSort Waterfall = WaterfallSort.StartTime, string? Custom = null)
{
    public static NetworkSortSpec Chronological { get; } = new();
    public bool IsChronological => Column is null && Custom is null;
}

/// <summary>Pure cell values shared by the table, sorting and HAR (kept out of the view models so they are testable).</summary>
public static class NetworkCells
{
    public static string Initiator(NetworkRequestDto r) =>
        r.Initiator?.Frame?.Split('.').TakeLast(2) is { } p ? string.Join(".", p) : "Other";

    static Uri? Parse(NetworkRequestDto r) => Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u : null;
    public static string Scheme(NetworkRequestDto r) => Parse(r)?.Scheme ?? "";
    public static string Domain(NetworkRequestDto r) => Parse(r)?.Authority ?? "";
    public static string Protocol(NetworkRequestDto r) => r.HttpVersion ?? "";
    public static string Remote(NetworkRequestDto r) => r.RemoteAddress ?? "";
    public static int CookieCount(NetworkRequestDto r) => NetworkFilter.RequestCookies(r).Count();
    public static int SetCookieCount(NetworkRequestDto r) => NetworkFilter.SetCookies(r).Count();

    /// <summary>Value of a custom response-header column (Chrome: Response Headers › Manage Header Columns).</summary>
    public static string ResponseHeader(NetworkRequestDto r, string name) =>
        string.Join(", ", r.ResponseHeaders.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.Replace('\n', ' ')));
}

/// <summary>Sort orders for the request table. Unknown values (pending requests) always sort after known ones when ascending.</summary>
public static class NetworkSort
{
    public static double? Total(NetworkRequestDto r) =>
        r.Timing is { Total: >= 0 } t ? t.Total : r.EndTime is { } e ? (e - r.StartTime).TotalMilliseconds : null;

    /// <summary>Milliseconds from the start of the request until the first response byte (Chrome's Latency = time to first byte).</summary>
    public static double? Latency(NetworkRequestDto r) => r.Timing is { Wait: >= 0 } t ? t.Wait : null;

    public static DateTimeOffset? ResponseTime(NetworkRequestDto r) =>
        r.Timing is { Total: >= 0 } t ? r.StartTime.AddMilliseconds(t.Total - Math.Max(0, t.Download)) : null;

    public static DateTimeOffset? EndTime(NetworkRequestDto r) =>
        r.EndTime ?? (Total(r) is { } t ? r.StartTime.AddMilliseconds(t) : null);

    public static IComparer<NetworkRequestDto> Comparer(NetworkSortSpec spec)
    {
        Func<NetworkRequestDto, IComparable?> key = KeyOf(spec);
        return Comparer<NetworkRequestDto>.Create((a, b) =>
        {
            var c = CompareKeys(key(a), key(b));
            if (spec.Descending) c = -c;
            return c != 0 ? c : Chronological(a, b);
        });
    }

    public static int Chronological(NetworkRequestDto a, NetworkRequestDto b)
    {
        var c = a.StartTime.CompareTo(b.StartTime);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    public static List<NetworkRequestDto> Sort(IEnumerable<NetworkRequestDto> requests, NetworkSortSpec spec)
    {
        var list = requests.ToList();
        list.Sort(Comparer(spec));
        return list;
    }

    static int CompareKeys(IComparable? x, IComparable? y)
    {
        if (x is null) return y is null ? 0 : 1;      // unknown last
        if (y is null) return -1;
        return x is string sx ? string.Compare(sx, (string)y, StringComparison.OrdinalIgnoreCase) : x.CompareTo(y);
    }

    static Func<NetworkRequestDto, IComparable?> KeyOf(NetworkSortSpec s)
    {
        if (s.Custom is { } name) return r => NetworkCells.ResponseHeader(r, name);
        return s.Column switch
        {
            null => _ => 0,
            NetworkColumn.Name => r => NetworkCopy.Name(r),
            NetworkColumn.Id => r => r.Id,
            NetworkColumn.Method => r => r.Method,
            NetworkColumn.Status => r => r.State == NetworkState.Pending ? null : r.Status ?? 0,
            NetworkColumn.Type => r => NetworkClassifier.TypeLabel(r),
            NetworkColumn.Protocol => r => NetworkCells.Protocol(r),
            NetworkColumn.Scheme => r => NetworkCells.Scheme(r),
            NetworkColumn.Domain => r => NetworkCells.Domain(r),
            NetworkColumn.Remote => r => NetworkCells.Remote(r),
            NetworkColumn.Connection => r => r.ConnectionId,
            NetworkColumn.Initiator => r => NetworkCells.Initiator(r),
            NetworkColumn.Cookies => r => NetworkCells.CookieCount(r),
            NetworkColumn.SetCookies => r => NetworkCells.SetCookieCount(r),
            NetworkColumn.Size => r => r.ResponseSize,
            NetworkColumn.Time => r => Total(r),
            NetworkColumn.Waterfall => s.Waterfall switch
            {
                WaterfallSort.ResponseTime => r => ResponseTime(r),
                WaterfallSort.EndTime => r => EndTime(r),
                WaterfallSort.TotalDuration => r => Total(r),
                WaterfallSort.Latency => r => Latency(r),
                _ => r => r.StartTime,
            },
            _ => _ => 0,
        };
    }
}
