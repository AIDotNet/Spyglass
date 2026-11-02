using Spyglass.Mcp;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>`spyglass run`: argument parsing and the environment the app is started with.</summary>
public class RunCliTests
{
    [Fact]
    public void Command_and_options_are_split_at_the_first_non_option_or_double_dash()
    {
        var o = RunCli.Parse(["--port", "29600", "--advanced", "dotnet", "run", "--project", "App"]);
        Assert.Null(o.Error);
        Assert.Equal(29600, o.Port);
        Assert.True(o.Advanced);
        Assert.Equal(["dotnet", "run", "--project", "App"], o.Command);   // options after the command belong to the command

        var dd = RunCli.Parse(["--", "./app", "--port", "1"]);
        Assert.Equal(["./app", "--port", "1"], dd.Command);
        Assert.Equal(29500, dd.Port);
    }

    [Fact]
    public void Bad_input_is_an_error_not_an_exception()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["--port", "70000", "app"], ["--port"], ["--hook"], ["--bogus", "app"] })
            Assert.NotNull(RunCli.Parse(args).Error);
    }

    [Fact]
    public void Help_is_recognised() => Assert.True(RunCli.Parse(["--help"]).Help);

    [Fact]
    public void Start_info_sets_hook_port_and_mode_and_keeps_existing_hooks()
    {
        var old = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
            var psi = RunCli.BuildStartInfo(RunCli.Parse(["--port", "29601", "--advanced", "my app.exe", "a b", "c"]), "/h/Spyglass.StartupHook.dll");
            Assert.Equal("my app.exe", psi.FileName);
            Assert.Equal(["a b", "c"], psi.ArgumentList);   // arguments are never re-split or passed through a shell
            Assert.False(psi.UseShellExecute);
            Assert.Equal("/h/Spyglass.StartupHook.dll", psi.Environment["DOTNET_STARTUP_HOOKS"]);
            Assert.Equal("29601", psi.Environment["SPYGLASS_PORT"]);
            Assert.Equal("advanced", psi.Environment["SPYGLASS_ATTACH"]);

            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "/x/other.dll");
            var psi2 = RunCli.BuildStartInfo(RunCli.Parse(["app"]), "/h/hook.dll");
            Assert.Equal("/h/hook.dll" + Path.PathSeparator + "/x/other.dll", psi2.Environment["DOTNET_STARTUP_HOOKS"]);
            Assert.False(psi2.Environment.ContainsKey("SPYGLASS_ATTACH") && psi2.Environment["SPYGLASS_ATTACH"] == "advanced");
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", old); }
    }

    [Fact]
    public async Task Missing_hook_and_missing_command_have_distinct_exit_codes()
    {
        Assert.Equal(2, await RunCli.RunAsync([]));
        Assert.Equal(3, await RunCli.RunAsync(["--hook", "/nope/Spyglass.StartupHook.dll", "dotnet"]));
        var hook = Path.GetTempFileName();
        try { Assert.Equal(127, await RunCli.RunAsync(["--hook", hook, "/no/such/program-xyz"])); }
        finally { File.Delete(hook); }
    }
}
