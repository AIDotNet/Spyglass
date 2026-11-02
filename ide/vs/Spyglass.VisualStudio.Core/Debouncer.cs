namespace Spyglass.VisualStudio.Core;

/// <summary>Runs an action for a key once no new <see cref="Schedule"/> for that key came for the delay.</summary>
public sealed class Debouncer : IDisposable
{
    readonly Action<string> _action;
    readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    readonly object _gate = new();
    bool _disposed;

    public Debouncer(Action<string> action) => _action = action;

    public void Schedule(string key, TimeSpan delay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_timers.Remove(key, out var old)) old.Dispose();
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    // A newer Schedule replaced this timer between firing and here: let that one run.
                    if (!_timers.TryGetValue(key, out var current) || current != timer) return;
                    _timers.Remove(key);
                    current.Dispose();
                }
                _action(key);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timers[key] = timer;
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel(string key)
    {
        lock (_gate) if (_timers.Remove(key, out var t)) t.Dispose();
    }

    public bool IsPending(string key)
    {
        lock (_gate) return _timers.ContainsKey(key);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var t in _timers.Values) t.Dispose();
            _timers.Clear();
        }
    }
}
