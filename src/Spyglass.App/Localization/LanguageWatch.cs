using Spyglass.App.ViewModels;

namespace Spyglass.App.Localization;

/// <summary>
/// View models that build user-visible text in C# call <c>WatchLanguage()</c>; when the language changes they raise
/// PropertyChanged for everything so bindings re-read their texts. References are weak, so rows can be discarded freely.
/// </summary>
internal static class LanguageWatch
{
    static readonly List<WeakReference<ObservableObject>> Watchers = new();
    static bool _subscribed;
    static int _pruneAt = 256;

    public static void Add(ObservableObject o)
    {
        lock (Watchers)
        {
            if (!_subscribed) { _subscribed = true; Loc.Changed += Notify; }
            if (Watchers.Count >= _pruneAt)
            {
                Watchers.RemoveAll(w => !w.TryGetTarget(out _));
                _pruneAt = Math.Max(256, Watchers.Count * 2);
            }
            Watchers.Add(new WeakReference<ObservableObject>(o));
        }
    }

    static void Notify()
    {
        List<ObservableObject> alive = new();
        lock (Watchers)
        {
            Watchers.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Watchers) if (w.TryGetTarget(out var o)) alive.Add(o);
        }
        foreach (var o in alive) o.OnLanguageChanged();
    }
}
