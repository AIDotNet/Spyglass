using System.Diagnostics;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.ProjectSystem.Query;
using Spyglass.VisualStudio.Core;

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
            MenuChild.Command<OpenPreviewCommand>(),
            MenuChild.Command<ShowSpyglassCommand>(),
            MenuChild.Command<ToggleHotReloadCommand>(),
            MenuChild.Command<ReconnectCommand>(),
            MenuChild.Separator,
            MenuChild.Command<RunWithDotnetWatchCommand>(),
        ],
    };
}

/// <summary>
/// Opens the design-time preview window (floating, owned by Visual Studio) and shows the active .axaml in it; when the
/// window is already open it is brought to the front. Extensions › Spyglass and View › Other Windows (the keyboard shortcut
/// and the editor context menu are separate commands, see below).
/// </summary>
[VisualStudioContribution]
internal sealed class OpenPreviewCommand(SpyglassPreviewService preview) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.OpenPreview%")
    {
        Icon = new(ImageMoniker.KnownValues.Image, IconSettings.IconAndText),
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        OpenPreviewHelper.RunAsync(context, preview, cancellationToken);
}

/// <summary>
/// The same command in the code editor's right-click menu, visible only in .axaml/.xaml files. The parent is the code
/// window context menu's text-editing group of the shell command set (vsshlids.h IDG_VS_CODEWIN_TEXTEDIT); a group that
/// does not exist just leaves the command out of the menu.
/// </summary>
[VisualStudioContribution]
internal sealed class OpenPreviewFromEditorCommand(SpyglassPreviewService preview) : Command
{
    static readonly Guid ShellMainMenu = new("d309f791-903f-11d0-9efc-00a0c911004f");

    public override CommandConfiguration CommandConfiguration => new("%Spyglass.OpenPreviewFromEditor%")
    {
        Icon = new(ImageMoniker.KnownValues.Image, IconSettings.IconAndText),
        Placements = [CommandPlacement.VsctParent(ShellMainMenu, id: 0x0200, priority: 0x0600)],
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @".+\.(axaml|xaml)$"),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        OpenPreviewHelper.RunAsync(context, preview, cancellationToken);
}

/// <summary>
/// Ctrl+Shift+Alt+P, only while an .axaml/.xaml editor is active (a disabled command's shortcut does nothing). No menu
/// entry: it is the keyboard route to <see cref="OpenPreviewCommand"/>. Same chord as the VS Code and Rider extensions;
/// no default Visual Studio binding is known for it (Ctrl+Alt+P is Attach to Process, Ctrl+Shift+P is unbound), but a
/// user's own keyboard mapping may use it (Tools › Options › Environment › Keyboard).
/// </summary>
[VisualStudioContribution]
internal sealed class OpenPreviewShortcutCommand(SpyglassPreviewService preview) : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Spyglass.OpenPreview%")
    {
        Shortcuts = [new CommandShortcutConfiguration(ModifierKey.ControlShiftLeftAlt, Key.P)],
        EnabledWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @".+\.(axaml|xaml)$"),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken) =>
        OpenPreviewHelper.RunAsync(context, preview, cancellationToken);
}

internal static class OpenPreviewHelper
{
    public static async Task RunAsync(IClientContext context, SpyglassPreviewService preview, CancellationToken ct)
    {
        var view = await context.GetActiveTextViewAsync(ct);
        var path = view?.Document.Uri is { IsFile: true } uri ? uri.LocalPath : null;
        await preview.OpenAsync(path, ct);
    }
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
            Arguments = DotnetWatchCommand.CmdArguments(path),
        };
        Process.Start(psi)?.Dispose();
    }
}
