using System.Collections.ObjectModel;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.Mcp;
using Spyglass.Protocol;

namespace Spyglass.App.ViewModels;

/// <summary>Main frame (wireframe W0): navigation, connected apps, status bar.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly SpyglassHub _hub;
    string _page = "connections";
    object? _pageContent;
    SessionViewModel? _current;
    string _status = "Starting…";
    string? _hubError;

    public MainViewModel(SpyglassHub hub)
    {
        _hub = hub;
        Connections = new ConnectionsViewModel(this);
        Settings = new SettingsViewModel(Spyglass.App.Core.Settings.SettingsService.Shared, () => Port);
        Lan = new LanViewModel(hub.Lan, Spyglass.App.Core.Settings.SettingsService.Shared, () => Port);
        Settings.Lan = Lan;
        _pageContent = Connections;
        hub.SessionConnected += s => Dispatcher.UIThread.Post(() => OnConnected(s));
        hub.SessionDisconnected += s => Dispatcher.UIThread.Post(() => OnDisconnected(s));
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = new();

    /// <summary>Client settings (not tied to a connected app).</summary>
    public SettingsViewModel Settings { get; private set; } = null!;

    /// <summary>LAN connections: setting, pairing code, addresses (Settings and Connections pages).</summary>
    public LanViewModel Lan { get; }

    /// <summary>The window should come to the front (hotkey pressed or element picked in the app).</summary>
    public event Action? BringToFrontRequested;
    public ConnectionsViewModel Connections { get; }

    public int Port => _hub.Port == 0 ? SpyglassProtocol.DefaultPort : _hub.Port;
    public string McpUrl => $"http://127.0.0.1:{Port}{SpyglassMcp.Path}";

    public string? HubError { get => _hubError; set { if (Set(ref _hubError, value)) UpdateStatus(); } }

    public SessionViewModel? Current
    {
        get => _current;
        set
        {
            var old = _current;
            if (!Set(ref _current, value)) return;
            UpdateStatus();
            Raise(nameof(BindingsBadge));
            Raise(nameof(LogsBadge));
            Raise(nameof(WindowTitle));
            if (old is not null && old != value) old.Performance.SetActive(false);
            if (_page == "performance") ShowPerformance();
            if (_page == "elements") PageContent = (object?)value?.Elements ?? Connections;
            if (_page == "events") ShowEvents();
            if (_page == "resources") PageContent = value is null ? Connections : ShowResources();
        }
    }

    /// <summary>Sidebar key.</summary>
    public string Page
    {
        get => _page;
        set
        {
            var previous = _page;
            if (!Set(ref _page, value)) return;
            if (previous == "performance") Current?.Performance.SetActive(false);
            if (value == "events") { ShowEvents(); return; }
            if (value == "performance") { ShowPerformance(); return; }
            PageContent = value switch
            {
                "connections" => Connections,
                "settings" => Settings,
                "elements" when Current is not null => Current.Elements,
                "bindings" when Current is not null => Current.Bindings,
                "network" when Current is not null => Current.Network,
                "logs" when Current is not null => Current.Logs,
                "resources" when Current is not null => ShowResources(),
                "elements" or "bindings" or "network" or "logs" or "resources" => Connections,
                _ => new PlaceholderViewModel(value),
            };
        }
    }

    public object? PageContent { get => _pageContent; private set => Set(ref _pageContent, value); }

    /// <summary>Resources page: reloads the providers on every visit so the selected element's resources are current.</summary>
    object ShowResources()
    {
        Current!.Resources.NotifySelectionChanged();
        _ = Current.Resources.LoadAsync();
        return Current.Resources;
    }

    /// <summary>Performance page: metrics run only while it is shown.</summary>
    void ShowPerformance()
    {
        if (Current is null) { PageContent = Connections; return; }
        PageContent = Current.Performance;
        Current.Performance.SetActive(true);
    }

    /// <summary>Events page: tracing starts only when the user first opens it (like Network).</summary>
    void ShowEvents()
    {
        if (Current is null) { PageContent = Connections; return; }
        PageContent = Current.Events;
        _ = Current.Events.EnsureStartedAsync();
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string WindowTitle => Current is { } c ? $"Spyglass — {c.Session.App.Name}" : "Spyglass";
    public bool IsConnected => Current is not null;

    public void HubStarted()
    {
        Raise(nameof(Port));
        Raise(nameof(McpUrl));
        Connections.Refresh();
        UpdateStatus();
    }

    void OnConnected(AgentSession s)
    {
        var vm = new SessionViewModel(s, id => Dispatcher.UIThread.Post(() => ShowElement(id)));
        vm.Bindings.PropertyChanged += (_, _) => { if (vm == Current) Raise(nameof(BindingsBadge)); };
        vm.Logs.PropertyChanged += (_, e) => { if (vm == Current && e.PropertyName == nameof(LogsViewModel.ProblemCount)) Raise(nameof(LogsBadge)); };
        s.ActivateRequested += p => Dispatcher.UIThread.Post(() => Activate(vm, p));
        Sessions.Add(vm);
        Current = vm;
        Connections.Refresh();
        Page = "elements";
        PageContent = vm.Elements;
        _ = vm.Elements.LoadAsync();
        _ = vm.Bindings.LoadAsync();
        _ = vm.Logs.LoadAsync();
    }

    /// <summary>Sidebar badge on "Bindings": error count of the current app, empty when none.</summary>
    public string? BindingsBadge => Current?.Bindings.Count is > 0 and var n ? n.ToString() : null;

    /// <summary>Sidebar badge on "Logs": warnings and errors received from the current app, empty when none.</summary>
    public string? LogsBadge => Current?.Logs.ProblemCount is > 0 and var n ? n.ToString() : null;

    void ShowElement(int id)
    {
        if (Current is null) return;
        Page = "elements";
        PageContent = Current.Elements;
        Current.Elements.SelectById(id);
    }

    void Activate(SessionViewModel vm, Protocol.Messages.ActivateParams p)
    {
        Current = vm;
        Page = "elements";
        PageContent = vm.Elements;
        if (p.NodeId is { } id) vm.Elements.SelectById(id);
        BringToFrontRequested?.Invoke();
    }

    void OnDisconnected(AgentSession s)
    {
        var vm = Sessions.FirstOrDefault(x => x.Session == s);
        if (vm is null) return;
        Sessions.Remove(vm);
        if (Current == vm) Current = Sessions.LastOrDefault();
        if (Current is null && Page is "elements" or "events") PageContent = Connections;
        Connections.Refresh();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        Raise(nameof(IsConnected));
        Status = HubError is not null ? HubError
            : Current is { } c ? $"Connected   {c.Session.App.Name} · PID {c.Session.App.Pid} · Avalonia {c.Session.App.AvaloniaVersion} · " +
                                 $"Agent {c.Session.Agent.Version} ({c.Session.Agent.Flavor.ToString().ToLowerInvariant()}){(Sessions.Count > 1 ? $"   ·   {Sessions.Count} apps" : "")}"
            : $"Waiting for an app · listening on 127.0.0.1:{Port} · MCP {McpUrl}";
    }
}

public sealed class SessionViewModel(AgentSession session, Action<int> showElement)
{
    public AgentSession Session { get; } = session;
    public ElementsViewModel Elements { get; } = new(session);
    public BindingsViewModel Bindings { get; } = new(session, showElement);
    public NetworkViewModel Network { get; } = new(session);
    public LogsViewModel Logs { get; } = new(session, showElement);
    public EventsViewModel Events { get; } = new(session, showElement);
    public ResourcesViewModel Resources => _resources ??= new(Session, Elements);
    ResourcesViewModel? _resources;
    public PerformanceViewModel Performance { get; } = new(session, showElement);
    public string Title => Session.App.Name;
    public string Details => $"PID {Session.App.Pid} · {Session.App.Os} {Session.App.Arch} · Avalonia {Session.App.AvaloniaVersion} · " +
                             $"{(Session.App.IsDebugBuild ? "Debug" : "Release")} · Agent {Session.Agent.Version} ({Session.Agent.Flavor.ToString().ToLowerInvariant()})";
}

public sealed class ConnectionsViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;
    public bool HasSessions => Main.Sessions.Count > 0;
    public bool NoSessions => !HasSessions;
    public LanViewModel Lan => Main.Lan;
    public string ListenText => Main.HubError ?? $"Listening on {(Main.Lan.IsActive ? "0.0.0.0" : "127.0.0.1")}:{Main.Port} · protocol {SpyglassProtocol.Version}";

    public string AgentSnippet => """
        dotnet add package Spyglass.Agent

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
        #if DEBUG
            .UseSpyglass()
        #endif
            ;
        """;

    public string McpSnippet => $$"""
        # Claude Code
        claude mcp add --transport http spyglass {{Main.McpUrl}}

        # Other MCP clients (Streamable HTTP)
        { "spyglass": { "type": "http", "url": "{{Main.McpUrl}}" } }
        """;

    public void Refresh()
    {
        Raise(nameof(HasSessions));
        Raise(nameof(NoSessions));
        Raise(nameof(ListenText));
        Raise(nameof(McpSnippet));
    }
}

public sealed class PlaceholderViewModel(string page)
{
    public string Title => char.ToUpperInvariant(page[0]) + page[1..];
    public string Description => page switch
    {
        "performance" => "Coming in v0.3.",
        _ => "Coming soon.",
    };
}
