using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Tracks every Window opened in the process, independent of the application lifetime. Covers apps with
/// custom or no lifetime (headless tests, embedding) and windows the lifetime does not list. Installed at
/// startup, before the first window opens.
/// </summary>
internal static class OpenWindows
{
    static readonly List<WeakReference<Window>> Windows = new();
    static readonly object Lock = new();
    static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Window.WindowOpenedEvent.Raised.Subscribe(new Observer(e =>
        {
            if (e.Item1 is not Window w) return;
            lock (Lock) { Prune(); if (!Windows.Any(r => r.TryGetTarget(out var x) && ReferenceEquals(x, w))) Windows.Add(new(w)); }
        }));
        Window.WindowClosedEvent.Raised.Subscribe(new Observer(e =>
        {
            if (e.Item1 is not Window w) return;
            lock (Lock) Windows.RemoveAll(r => !r.TryGetTarget(out var x) || ReferenceEquals(x, w));
        }));
    }

    /// <summary>Open windows in the order they were opened.</summary>
    public static IReadOnlyList<Window> Current
    {
        get
        {
            lock (Lock)
            {
                Prune();
                return Windows.Select(r => r.TryGetTarget(out var w) ? w : null).OfType<Window>().ToList();
            }
        }
    }

    static void Prune() => Windows.RemoveAll(r => !r.TryGetTarget(out _));

    sealed class Observer(Action<(object, RoutedEventArgs)> onNext) : IObserver<(object, RoutedEventArgs)>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext((object, RoutedEventArgs) value) => onNext(value);
    }
}
