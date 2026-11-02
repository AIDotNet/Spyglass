using System.Diagnostics;
using System.Reflection;
using System.Text;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.StartupHook.Tests;

/// <summary>The apps never reference Spyglass (except TestAppSelf, which calls UseSpyglass itself): DOTNET_STARTUP_HOOKS alone makes them connect.</summary>
public sealed class StartupHookTests
{
    static string Meta(string key) => typeof(StartupHookTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>A launched app with its stderr collected.</summary>
    sealed class App : IDisposable
    {
        readonly Process _process;
        readonly StringBuilder _err = new();
        public App(Process process)
        {
            _process = process;
            process.ErrorDataReceived += (_, e) => { lock (_err) _err.AppendLine(e.Data); };
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        public bool HasExited => _process.HasExited;
        public string Stderr { get { lock (_err) return _err.ToString(); } }
        public void Dispose() { try { _process.Kill(entireProcessTree: true); } catch { } _process.Dispose(); }
    }

    static App Launch(string app, int port, string? attach = null)
    {
        var p = new ProcessStartInfo("dotnet", $"\"{Meta(app + "Dll")}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        p.Environment["DOTNET_STARTUP_HOOKS"] = Meta("HookDll");
        p.Environment["SPYGLASS_PORT"] = port.ToString();
        p.Environment["TESTAPP_SECONDS"] = "60";
        if (attach is not null) p.Environment["SPYGLASS_ATTACH"] = attach;
        return new App(Process.Start(p)!);
    }

    static async Task<SpyglassHub> StartHub()
    {
        Environment.SetEnvironmentVariable("SPYGLASS_HOME", Directory.CreateTempSubdirectory("spyglass-hook-").FullName);
        var hub = new SpyglassHub(new SpyglassHubOptions { Port = 0 });
        await hub.StartAsync();
        return hub;
    }

    static async Task AssertInspectable(AgentSession session)
    {
        var id = (await session.SearchAsync(new SearchParams("#HookedButton"))).Matches.Single().Id;
        Assert.Contains("Button", (await session.DescribeAsync(id)).Selector);
        var shot = await session.ScreenshotAsync(id);
        Assert.Equal("png", shot.Format);
    }

    [Fact]
    public async Task An_app_without_the_agent_connects_and_can_be_inspected()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestApp", hub.Port);
        var session = await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("Spyglass.StartupHook.TestApp", session.App.Name);
        Assert.Equal(AgentFlavor.Basic, session.Agent.Flavor);
        await AssertInspectable(session);
    }

    [Fact]
    public async Task An_Avalonia_11_app_connects_with_the_Avalonia_11_build_and_can_be_inspected()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestApp11", hub.Port);
        var session = await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
        Assert.Equal("Spyglass.StartupHook.TestApp11", session.App.Name);
        Assert.Equal(AgentFlavor.Basic, session.Agent.Flavor);
        await AssertInspectable(session);
        Assert.DoesNotContain("[spyglass]", app.Stderr);
    }

    [Fact]
    public async Task SPYGLASS_ATTACH_advanced_starts_the_Advanced_agent()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestApp", hub.Port, attach: "advanced");
        var session = await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(AgentFlavor.Advanced, session.Agent.Flavor);
        await AssertInspectable(session);
        var id = (await session.SearchAsync(new SearchParams("#HookedButton"))).Matches.Single().Id;
        Assert.NotEmpty((await session.GetValueFramesAsync(id)).Frames);   // really runs on Avalonia's private API
        Assert.DoesNotContain("[spyglass]", app.Stderr);
    }

    [Fact]
    public async Task Advanced_on_Avalonia_11_degrades_to_Basic_with_one_note()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestApp11", hub.Port, attach: "advanced");
        var session = await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
        Assert.Equal(AgentFlavor.Basic, session.Agent.Flavor);
        await AssertInspectable(session);
        Assert.Single(app.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("[spyglass]") && l.Contains("Advanced"));
    }

    [Fact]
    public async Task An_unsupported_Avalonia_version_is_left_alone_with_one_note()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestAppOld", hub.Port);
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Empty(hub.Sessions);
        Assert.False(app.HasExited);
        Assert.Single(app.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("[spyglass]") && l.Contains("11.0.13"));
    }

    [Fact]
    public async Task An_app_that_already_calls_UseSpyglass_gets_exactly_one_session()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestAppSelf", hub.Port);
        await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
        await Task.Delay(TimeSpan.FromSeconds(8));   // a second agent from the hook would have connected by now
        Assert.Single(hub.Sessions);
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task SPYGLASS_ATTACH_0_turns_the_hook_off()
    {
        await using var hub = await StartHub();
        using var app = Launch("TestApp", hub.Port, attach: "0");
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Empty(hub.Sessions);
        Assert.False(app.HasExited);
    }
}
