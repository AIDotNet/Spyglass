using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Spyglass.Agent;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.Avalonia11.Tests;

public sealed class SmokeApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}

/// <summary>One headless Avalonia 11 app for the whole class (Avalonia can be set up once per process).</summary>
public sealed class SmokeFixture : IAsyncLifetime
{
    internal SpyglassHub Hub = null!;
    internal AgentSession Session = null!;
    internal Window Window = null!;
    readonly CancellationTokenSource _stop = new();

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("SPYGLASS_HOME", Directory.CreateTempSubdirectory("spyglass-a11-").FullName);
        Hub = new SpyglassHub(new SpyglassHubOptions { Port = 0 });
        await Hub.StartAsync();
        var ready = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SmokeApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .UseSpyglass(o => { o.Port = Hub.Port; o.ApplicationName = "Avalonia 11 smoke"; o.LaunchClient = false; })
                    .SetupWithoutStarting();
                var w = new Window
                {
                    Width = 400, Height = 300,
                    Content = new StackPanel
                    {
                        Name = "Root",
                        Children =
                        {
                            new TextBlock { Name = "Heading", Text = "Hello 11" },
                            new TextBox { Name = "NameBox", Watermark = "your name" },
                            new Button { Name = "SaveButton", Content = "Save" },
                        },
                    },
                };
                w.Show();
                ready.SetResult(w);
                Dispatcher.UIThread.MainLoop(_stop.Token);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true }.Start();
        Window = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Session = await Hub.WaitForSessionAsync(TimeSpan.FromSeconds(20));
    }

    public async ValueTask DisposeAsync()
    {
        SpyglassAgent.Stop();
        await Hub.DisposeAsync();
        _stop.Cancel();
    }

}

/// <summary>The agent compiled for Avalonia 11.2: connects, walks the tree, reads properties and layout, takes a screenshot.</summary>
public sealed class Avalonia11Tests(SmokeFixture f) : IClassFixture<SmokeFixture>
{
    readonly SmokeFixture _f = f;

    [Fact]
    public void Agent_says_it_is_basic_and_runs_against_avalonia_11()
    {
        Assert.StartsWith("11.", _f.Session.App.AvaloniaVersion);
        Assert.Equal(AgentFlavor.Basic, _f.Session.Agent.Flavor);
    }

    async Task<int> IdOf(string selector) => (await _f.Session.SearchAsync(new SearchParams(selector))).Matches.Single().Id;

    [Fact]
    public async Task Tree_search_describe_properties_and_layout_work()
    {
        var roots = await _f.Session.GetRootsAsync();
        Assert.Contains(roots.Roots, r => r.Kind == RootKind.Window);

        var id = await IdOf("#SaveButton");
        var d = await _f.Session.DescribeAsync(id);
        Assert.Contains("Button", d.Selector);
        Assert.Null(d.Source);   // Avalonia 11 has no public XAML source info

        var props = await _f.Session.GetPropertiesAsync(id);
        Assert.Contains(props, p => p.Name == "Content");

        var layout = await _f.Session.GetLayoutAsync(id);
        Assert.True(layout.Bounds.W > 0 && layout.Bounds.H > 0);

        var box = (await _f.Session.GetTreeAsync(await IdOf("#NameBox"))).Placeholder;
        Assert.Equal("your name", box);   // Watermark is reported as the placeholder
    }

    [Fact]
    public async Task Property_edit_round_trips()
    {
        var id = await IdOf("#Heading");
        await _f.Session.SetPropertyAsync(id, "Text", "TextBlock", new ValueDto(ValueKind.String, "Changed"));
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("Changed", await Dispatcher.UIThread.InvokeAsync(() => ((TextBlock)((StackPanel)_f.Window.Content!).Children[0]).Text));
    }

    [Fact]
    public async Task Screenshot_is_a_png()
    {
        var shot = await _f.Session.ScreenshotAsync(await IdOf("#SaveButton"));
        Assert.Equal("png", shot.Format);
        var bytes = Convert.FromBase64String(shot.Base64);
        Assert.Equal(0x89, bytes[0]); Assert.Equal((byte)'P', bytes[1]);
    }
}
