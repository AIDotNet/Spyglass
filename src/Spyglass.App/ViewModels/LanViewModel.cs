using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Spyglass.App.Core.Lan;
using Spyglass.App.Localization;
using Spyglass.App.Core.Settings;
using Spyglass.Protocol;

namespace Spyglass.App.ViewModels;

/// <summary>
/// LAN connections (design/protocol §6): the setting, whether it is active in this run, the pairing code and the
/// addresses to give the agent. Shared by the Settings and Connections pages.
/// </summary>
public sealed class LanViewModel : ObservableObject
{
    readonly LanGate _gate;
    readonly SettingsService _settings;
    readonly Func<int> _port;
    IReadOnlyList<LanAddress> _addresses;

    public LanViewModel(LanGate gate, SettingsService settings, Func<int> port)
    {
        _gate = gate;
        _settings = settings;
        _port = port;
        _addresses = gate.AllowLan ? LanAddresses.Get() : [];
        gate.Guard.CodeChanged += () => Dispatcher.UIThread.Post(() => { Raise(nameof(Code)); Raise(nameof(Snippet)); });
        settings.Changed += _ => { Raise(nameof(AllowLan)); Raise(nameof(RestartNote)); Raise(nameof(RestartPending)); };
        WatchLanguage();
        RegenerateCommand = new RelayCommand(() => _gate.Guard.Regenerate());
        CopyCodeCommand = new RelayCommand(() => CopyAsync(Code));
        CopyHostCommand = new RelayCommand(() => CopyAsync(Host));
        RefreshAddressesCommand = new RelayCommand(RefreshAddresses);
    }

    /// <summary>The hub is listening on all interfaces in this run.</summary>
    public bool IsActive => _gate.AllowLan;

    /// <summary>The persisted setting; takes effect at the next start (Kestrel cannot change its bindings while running).</summary>
    public bool AllowLan { get => _settings.Current.AllowLan; set => _settings.Update(s => s.AllowLan = value); }

    public bool RestartPending => AllowLan != IsActive;
    public string RestartNote => !RestartPending ? "" : AllowLan
        ? Loc.T("Lan.RestartToStart")
        : Loc.T("Lan.RestartToStop");

    public string Code => _gate.Guard.Code;

    public IReadOnlyList<LanAddress> Addresses => _addresses;
    public string Host => _addresses.FirstOrDefault()?.Address ?? "<this machine's address>";
    public string AddressesText => _addresses.Count == 0
        ? Loc.T("Lan.NoAddress")
        : string.Join("   ", _addresses.Select(a => $"{a.Address}  ({a.Interface})"));

    public string Snippet => $$"""
        // On the other device (plain ws://, trusted networks only):
        .UseSpyglass(o =>
        {
            o.Host = "{{Host}}";
            o.PairingCode = "{{Code}}";   // or SPYGLASS_HOST / SPYGLASS_PAIRING_CODE
        })
        """;

    public string PortText => Loc.F("Lan.Port", _port());

    public RelayCommand RegenerateCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand CopyHostCommand { get; }
    public RelayCommand RefreshAddressesCommand { get; }

    public void RefreshAddresses()
    {
        _addresses = _gate.AllowLan ? LanAddresses.Get() : [];
        Raise(nameof(Addresses)); Raise(nameof(Host)); Raise(nameof(AddressesText)); Raise(nameof(Snippet));
    }

    static async Task CopyAsync(string text)
    {
        var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
