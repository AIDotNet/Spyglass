using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace Spyglass.VisualStudio;

/// <summary>
/// Spyglass for Avalonia: XAML hot reload from Visual Studio's editor (design/hot-reload/README.md §8). Sends unsaved
/// .axaml buffers to the Spyglass hub, shows reload errors in the Error List, and opens files for "Open in editor".
/// </summary>
[VisualStudioContribution]
internal sealed class SpyglassExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "Spyglass.VisualStudio.7d3f2c1e-5a8b-4c6d-9e0f-1a2b3c4d5e6f",
            version: ExtensionAssemblyVersion,
            publisherName: "Spyglass contributors",
            displayName: "Spyglass for Avalonia",
            description: "XAML hot reload for running Avalonia apps: edit .axaml and the app updates as you type. Free and open source.")
        {
            // Settings (Tools › Options › Spyglass) and the diagnostics API need 17.14; VS 2026 (18.x) runs 17.x extensions.
            InstallationTargetVersion = "[17.14,)",
            Tags = ["Avalonia", "XAML", "hot reload", "axaml"],
        },
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSingleton<SpyglassService>();
    }
}
