using System.Diagnostics;
using System.Reflection;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.StartupHook.Tests;

/// <summary>The app never references Spyglass: DOTNET_STARTUP_HOOKS alone makes it connect.</summary>
public sealed class StartupHookTests
{
    static string Meta(string key) => typeof(StartupHookTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    static Process Launch(int port, string? attach = null)
    {
        var p = new ProcessStartInfo("dotnet", $"\"{Meta("TestAppDll")}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        p.Environment["DOTNET_STARTUP_HOOKS"] = Meta("HookDll");
        p.Environment["SPYGLASS_PORT"] = port.ToString();
        p.Environment["TESTAPP_SECONDS"] = "60";
        if (attach is not null) p.Environment["SPYGLASS_ATTACH"] = attach;
        return Process.Start(p)!;
    }

    static async Task<SpyglassHub> StartHub()
    {
        Environment.SetEnvironmentVariable("SPYGLASS_HOME", Directory.CreateTempSubdirectory("spyglass-hook-").FullName);
        var hub = new SpyglassHub(new SpyglassHubOptions { Port = 0 });
        await hub.StartAsync();
        return hub;
    }

    [Fact]
    public async Task An_app_without_the_agent_connects_and_can_be_inspected()
    {
        await using var hub = await StartHub();
        using var app = Launch(hub.Port);
        try
        {
            var session = await hub.WaitForSessionAsync(TimeSpan.FromSeconds(40));
            Assert.Equal("Spyglass.StartupHook.TestApp", session.App.Name);
            Assert.Equal(AgentFlavor.Basic, session.Agent.Flavor);

            var id = (await session.SearchAsync(new SearchParams("#HookedButton"))).Matches.Single().Id;
            Assert.Contains("Button", (await session.DescribeAsync(id)).Selector);
            var shot = await session.ScreenshotAsync(id);
            Assert.Equal("png", shot.Format);
        }
        finally { app.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task SPYGLASS_ATTACH_0_turns_the_hook_off()
    {
        await using var hub = await StartHub();
        using var app = Launch(hub.Port, attach: "0");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.Empty(hub.Sessions);
            Assert.False(app.HasExited);
        }
        finally { app.Kill(entireProcessTree: true); }
    }
}
