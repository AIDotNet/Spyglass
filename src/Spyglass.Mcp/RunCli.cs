using System.Diagnostics;
using Spyglass.Protocol;

namespace Spyglass.Mcp;

/// <summary>
/// <c>spyglass run [options] [--] command [args…]</c>: starts your app with the Spyglass startup hook attached
/// (DOTNET_STARTUP_HOOKS), so it connects to Spyglass without any code change (design/startup-hook/README.md).
/// Standard streams are inherited; the exit code is the app's.
/// </summary>
public static class RunCli
{
    const string Usage = """
        spyglass run [--port N] [--advanced] [--hook PATH] [--] <command> [args...]

          --port N      port Spyglass listens on (default: SPYGLASS_PORT or 29500)
          --advanced    also start the Advanced agent (style sources, binding details; Avalonia 12.1.4 only)
          --hook PATH   Spyglass.StartupHook.dll to use (default: SPYGLASS_HOOK, else the copy shipped with Spyglass)

        Examples:
          spyglass run dotnet run --project src/MyApp
          spyglass run ./bin/Debug/net8.0/MyApp
          spyglass run --advanced -- dotnet MyApp.dll

        The app connects to the Spyglass that is running on the port (the GUI, or `spyglass serve`).
        Note: `dotnet run` builds first; the hook is ignored by the build tools.
        """;

    public sealed record Options(int Port, bool Advanced, string? Hook, IReadOnlyList<string> Command, bool Help, string? Error);

    public static Options Parse(IReadOnlyList<string> args)
    {
        var port = SpyglassProtocol.Port;
        var advanced = false;
        string? hook = null, error = null;
        var i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (!a.StartsWith('-')) break;   // first non-option: the command
            switch (a)
            {
                case "--port" when i + 1 < args.Count && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536: port = p; i++; break;
                case "--port": error = "--port needs a number between 1 and 65535."; break;
                case "--advanced": advanced = true; break;
                case "--hook" when i + 1 < args.Count: hook = args[++i]; break;
                case "--hook": error = "--hook needs a path."; break;
                case "-h" or "--help": return new Options(port, advanced, hook, [], true, null);
                default: error = $"Unknown option '{a}'. Put options before the command, or separate them with --."; break;
            }
        }
        var command = args.Skip(i).ToList();
        if (error is null && command.Count == 0) error = "Nothing to run: give the command that starts your app.";
        return new Options(port, advanced, hook, command, false, error);
    }

    /// <summary>The hook to use: --hook, SPYGLASS_HOOK, or the copy next to the Spyglass executable. Null when none exists.</summary>
    public static string? FindHook(string? explicitPath)
    {
        foreach (var candidate in new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable("SPYGLASS_HOOK"),
            Path.Combine(AppContext.BaseDirectory, "hook", "Spyglass.StartupHook.dll"),
        })
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    /// <summary>How the app is started. An existing DOTNET_STARTUP_HOOKS is kept (ours first).</summary>
    public static ProcessStartInfo BuildStartInfo(Options o, string hookPath)
    {
        var psi = new ProcessStartInfo(o.Command[0]) { UseShellExecute = false };
        foreach (var arg in o.Command.Skip(1)) psi.ArgumentList.Add(arg);
        var existing = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        psi.Environment["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(existing) ? hookPath : hookPath + Path.PathSeparator + existing;
        psi.Environment["SPYGLASS_PORT"] = o.Port.ToString();
        if (o.Advanced) psi.Environment["SPYGLASS_ATTACH"] = "advanced";
        return psi;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var o = Parse(args);
        if (o.Help) { await Console.Error.WriteLineAsync(Usage); return 0; }
        if (o.Error is not null) { await Console.Error.WriteLineAsync($"{o.Error}\n\n{Usage}"); return 2; }
        var hook = FindHook(o.Hook);
        if (hook is null)
        {
            await Console.Error.WriteLineAsync("Could not find Spyglass.StartupHook.dll. Pass --hook PATH or set SPYGLASS_HOOK (see design/startup-hook/README.md).");
            return 3;
        }

        Process process;
        try { process = Process.Start(BuildStartInfo(o, hook))!; }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Could not start '{o.Command[0]}': {ex.Message}");
            return 127;
        }
        using (process)
        {
            await Console.Error.WriteLineAsync($"[spyglass] started '{o.Command[0]}' with the startup hook; it connects to port {o.Port}.");
            // Ctrl+C reaches the app through the terminal's process group; we just wait for it instead of dying first.
            ConsoleCancelEventHandler ignore = (_, e) => e.Cancel = true;
            Console.CancelKeyPress += ignore;
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return 130;
            }
            finally { Console.CancelKeyPress -= ignore; }
        }
    }
}
