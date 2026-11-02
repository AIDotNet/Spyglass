using System.Diagnostics;
using System.Net.Sockets;

namespace Spyglass.Agent.Internal;

/// <summary>Finds and starts the Spyglass client when the open/inspect hotkey is pressed and it is not running.</summary>
internal static class ClientLauncher
{
    public static bool IsListening(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch { return false; }
    }

    public static bool IsLoopback(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Resolution order: explicit option → SPYGLASS_CLIENT → `spyglass` on PATH → usual install location.</summary>
    public static string? Resolve(string? explicitPath, Func<string, string?>? env = null, string? pathVariable = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (var candidate in new[] { explicitPath, env("SPYGLASS_CLIENT") })
            if (!string.IsNullOrWhiteSpace(candidate) && (File.Exists(candidate) || Directory.Exists(candidate)))
                return candidate;

        var exe = OperatingSystem.IsWindows() ? "spyglass.exe" : "spyglass";
        foreach (var dir in (pathVariable ?? env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }

        // dotnet global tools live in ~/.dotnet/tools, which GUI apps on macOS often lack on PATH.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tool = Path.Combine(home, ".dotnet", "tools", exe);
        if (File.Exists(tool)) return tool;

        if (OperatingSystem.IsMacOS() && Directory.Exists("/Applications/Spyglass.app")) return "/Applications/Spyglass.app";
        if (OperatingSystem.IsWindows())
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Spyglass", "Spyglass.exe");
            if (File.Exists(local)) return local;
        }
        return null;
    }

    public static ProcessStartInfo StartInfo(string path)
    {
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo("open", ["-a", path]) { UseShellExecute = false };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo("dotnet", [path]) { UseShellExecute = false };
        return new ProcessStartInfo(path) { UseShellExecute = false };
    }

    public static bool TryStart(string path, out string? error)
    {
        try
        {
            using var p = Process.Start(StartInfo(path));
            error = null;
            return p is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
