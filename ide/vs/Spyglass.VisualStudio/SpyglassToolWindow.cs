using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;
using Spyglass.VisualStudio.Core;

namespace Spyglass.VisualStudio;

/// <summary>
/// The Spyglass tool window. VisualStudio.Extensibility has no status bar API, so this is where the VS Code status bar
/// item's information lives: connection, apps that can hot-reload, the newest reload result, and the log.
/// </summary>
[VisualStudioContribution]
internal sealed class SpyglassToolWindow : ToolWindow
{
    readonly SpyglassService _spyglass;

    public SpyglassToolWindow(SpyglassService spyglass)
    {
        _spyglass = spyglass;
        Title = "Spyglass";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        DockDirection = Dock.Bottom,
        AllowAutoCreation = true,
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken) => await _spyglass.EnsureStartedAsync(cancellationToken);

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IRemoteUserControl>(new SpyglassStatusControl(_spyglass.Status));
}

/// <summary>Remote UI control; its XAML is the embedded SpyglassStatusControl.xaml.</summary>
internal sealed class SpyglassStatusControl(object dataContext) : RemoteUserControl(dataContext);

[DataContract]
internal sealed class SpyglassStatusViewModel : NotifyPropertyChangedObject
{
    const int MaxLogLines = 300;
    readonly LinkedList<string> _log = new();
    string _title = "Spyglass: starting…";
    string _details = "";
    string _kind = nameof(HubStatusKind.Busy);
    string _logText = "";
    string _toggleText = "Turn hot reload off";

    public SpyglassStatusViewModel()
    {
        ReconnectCommand = new AsyncCommand((_, _) => { Reconnect?.Invoke(); return Task.CompletedTask; });
        ToggleCommand = new AsyncCommand((_, _) => Toggle?.Invoke() ?? Task.CompletedTask);
    }

    internal Action? Reconnect { get; set; }
    internal Func<Task>? Toggle { get; set; }

    [DataMember] public string Title { get => _title; set => SetProperty(ref _title, value); }
    [DataMember] public string Details { get => _details; set => SetProperty(ref _details, value); }
    /// <summary>Off, Busy, Disconnected, Idle, Live or Error (the XAML picks an icon and color).</summary>
    [DataMember] public string Kind { get => _kind; set => SetProperty(ref _kind, value); }
    [DataMember] public string Log { get => _logText; set => SetProperty(ref _logText, value); }
    [DataMember] public string ToggleText { get => _toggleText; set => SetProperty(ref _toggleText, value); }
    [DataMember] public IAsyncCommand ReconnectCommand { get; }
    [DataMember] public IAsyncCommand ToggleCommand { get; }

    internal void Update(HubStatusView view, bool enabled)
    {
        Title = view.Text;
        Details = string.Join(Environment.NewLine, view.Details);
        Kind = view.Kind.ToString();
        ToggleText = enabled ? "Turn hot reload off" : "Turn hot reload on";
    }

    internal void AppendLog(string line)
    {
        lock (_log)
        {
            _log.AddLast(line);
            while (_log.Count > MaxLogLines) _log.RemoveFirst();
            Log = string.Join(Environment.NewLine, _log);
        }
    }
}
