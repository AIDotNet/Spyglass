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
    static SpyglassExtension() => HostAssemblies.Install();

    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "AIDotNet.Spyglass.bb67bdd2-3341-4592-8f74-a09c107e98b4",   // permanent: the Marketplace identity of the extension
            version: ExtensionAssemblyVersion,
            publisherName: "AIDotNet",
            displayName: "Spyglass for Avalonia",
            description: "XAML hot reload for running Avalonia apps and a design-time .axaml preview: edit and see it update as you type. Free and open source.")
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
        serviceCollection.AddSingleton<SpyglassPreviewService>();
    }
}
