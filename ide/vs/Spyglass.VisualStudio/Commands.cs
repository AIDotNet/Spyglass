using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace Spyglass.VisualStudio;

/// <summary>Extensions › Spyglass.</summary>
internal static class SpyglassMenu
{
    [VisualStudioContribution]
    public static MenuConfiguration Menu => new("%Spyglass.Menu%")
    {
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Children =
        [
            MenuChild.Command<ShowSpyglassCommand>(),
            MenuChild.Command<ToggleHotReloadCommand>(),
            MenuChild.Command<ReconnectCommand>(),
            MenuChild.Separator,
            MenuChild.Command<RunWithDotnetWatchCommand>(),
        ],
    };
}

/// <summary>Opens the Spyglass tool window: connection, apps, the last reload, and the log ("open output").</summary>
[VisualStudioContribution]
internal sealed class ShowSpyglassCommand(SpyglassService spyglass) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.Show%")
    {
        Icon = new(ImageMoniker.KnownValues.StatusInformation, IconSettings.IconAndText),
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await spyglass.EnsureStartedAsync(cancellationToken);
        await Extensibility.Shell().ShowToolWindowAsync<SpyglassToolWindow>(activate: true, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class ToggleHotReloadCommand(SpyglassService spyglass) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.ToggleHotReload%")
    {
        Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await spyglass.EnsureStartedAsync(cancellationToken);
        await spyglass.SetEnabledAsync(!spyglass.HotReloadEnabled, cancellationToken);
    }
}

[VisualStudioContribution]
internal sealed class ReconnectCommand(SpyglassService spyglass) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.Reconnect%");

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await spyglass.EnsureStartedAsync(cancellationToken);
        spyglass.Log("Reconnecting…");
        spyglass.Client.Reconnect();
    }
}

/// <summary>Runs a project of the solution with <c>dotnet watch</c> in a console window, so C# edits apply too.</summary>
[VisualStudioContribution]
internal sealed class RunWithDotnetWatchCommand(SpyglassService spyglass) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.RunWithDotnetWatch%")
    {
        Icon = new(ImageMoniker.KnownValues.Run, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await spyglass.EnsureStartedAsync(cancellationToken);
        var projects = (await Extensibility.Workspaces().QueryProjectsAsync(p => p.With(x => x.Name).With(x => x.Path), cancellationToken))
            .Where(p => p.Path?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projects.Count == 0)
        {
            await Extensibility.Shell().ShowPromptAsync("No .csproj in this solution.", PromptOptions.OK, cancellationToken);
            return;
        }

        var path = projects[0].Path!;
        if (projects.Count > 1)
        {
            var options = new PromptOptions<int> { DismissedReturns = -1, Title = "Spyglass" };
            for (var i = 0; i < projects.Count; i++) options.Choices.Add(projects[i].Name ?? Path.GetFileNameWithoutExtension(projects[i].Path!), i);
            var pick = await Extensibility.Shell().ShowPromptAsync(
                "Project to run with dotnet watch (C# edits apply without restarting):", options, cancellationToken);
            if (pick < 0) return;
            path = projects[pick].Path!;
        }

        spyglass.Log($"dotnet watch run --project \"{path}\"");
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = true,   // its own console window, which the user closes to stop the app
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add($"dotnet watch run --project \"{path}\"");
        Process.Start(psi)?.Dispose();
    }
}
