using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Spyglass.App.Core.Localization;
using Spyglass.App.Core.Settings;

namespace Spyglass.App.Localization;

/// <summary>
/// The GUI's live string source: <c>Loc.Instance["key"]</c> with INotifyPropertyChanged on the indexer, so every
/// binding to it re-evaluates when the language changes. In code use <see cref="T"/> / <see cref="F"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    Loc() => Localizer.Shared.Changed += () =>
    {
        void Raise()
        {
            // Indexer bindings listen for "Item[]" (Avalonia) or "Item" (WPF-style); send both.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        }
        if (Dispatcher.UIThread.CheckAccess()) Raise(); else Dispatcher.UIThread.Post(Raise);
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Localizer.Shared.Get(key);

    /// <summary>Text for a key in the current language.</summary>
    public static string T(string key) => Localizer.Shared.Get(key);

    /// <summary>Text for a key with <c>{0}</c> placeholders filled.</summary>
    public static string F(string key, params object?[] args) => Localizer.Shared.Format(key, args);

    /// <summary>Applies the language chosen in the settings now and whenever the settings change.</summary>
    public static void Attach(SettingsService service)
    {
        Localizer.Shared.Language = Languages.Resolve(service.Current.Language);
        service.Changed += s => Localizer.Shared.Language = Languages.Resolve(s.Language);
    }

    /// <summary>Raised after the language changed (the indexer already notified bindings).</summary>
    public static event Action Changed { add => Localizer.Shared.Changed += value; remove => Localizer.Shared.Changed -= value; }
}

/// <summary>XAML: <c>Text="{loc:T Settings.Theme}"</c>. Expands to a one-way binding on <see cref="Loc"/>'s indexer.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding { Path = $"[{Key}]", Mode = BindingMode.OneWay, Source = Loc.Instance };
}
