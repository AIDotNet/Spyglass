using Avalonia;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.Mcp;
using Xunit;

namespace Spyglass.IntegrationTests;

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;

/// <summary>
/// Starts the hub, then a headless Avalonia app on its own UI thread running a real dispatcher loop
/// (like a real app, so the agent's UI-thread work runs without the test pumping it).
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public static int Port;

    readonly CancellationTokenSource _uiStop = new();
    Thread? _uiThread;

    public SpyglassHub Hub { get; private set; } = null!;
    public AgentSession Session { get; private set; } = null!;
    public TestWindow Window { get; private set; } = null!;


    public async ValueTask InitializeAsync()
    {
        Hub = new SpyglassHub(new SpyglassHubOptions { Port = 0 }.WithMcp());
        await Hub.StartAsync();
        Port = Hub.Port;

        var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiThread = new Thread(() =>
        {
            try
            {
                TestApp.BuildAvaloniaApp().SetupWithoutStarting();
                var w = new TestWindow { DataContext = new TestViewModel() };
                w.Show();
                ready.SetResult(w);
                Dispatcher.UIThread.MainLoop(_uiStop.Token);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Avalonia UI (test)" };
        _uiThread.Start();

        Window = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Session = await Hub.WaitForSessionAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Runs on the UI thread, then lets queued layout/render work run.</summary>
    public async Task<T> OnUi<T>(Func<T> f)
    {
        var r = await Dispatcher.UIThread.InvokeAsync(f);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        Spyglass.Agent.SpyglassAgent.Stop();
        await Hub.DisposeAsync();
        _uiStop.Cancel();
        _uiThread?.Join(TimeSpan.FromSeconds(5));
    }
}
