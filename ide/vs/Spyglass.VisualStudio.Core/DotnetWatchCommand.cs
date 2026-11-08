namespace Spyglass.VisualStudio.Core;

/// <summary>"Run Project with dotnet watch" in a console window that stays open.</summary>
public static class DotnetWatchCommand
{
    /// <summary>
    /// The raw command line for cmd.exe. Not built with ProcessStartInfo.ArgumentList: it escapes inner quotes as \", which
    /// cmd.exe does not understand, so dotnet would get a project path with literal quotes. The path is always quoted, for
    /// spaces and for characters such as &amp; that cmd.exe treats specially.
    /// </summary>
    public static string CmdArguments(string projectPath) => $"/k dotnet watch run --project \"{projectPath}\"";
}
