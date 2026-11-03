using System.IO.Pipelines;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Nerdbank.Streams;
using Spyglass.VisualStudio.Core.Language;

namespace Spyglass.VisualStudio;

#pragma warning disable VSEXTPREVIEW_LSP // VisualStudio.Extensibility's LSP support is a preview API (as in Microsoft's samples).

/// <summary>
/// Avalonia XAML IntelliSense in Visual Studio (design/xaml-language/README.md): Visual Studio's LSP client talks to
/// <see cref="AxamlLanguageServer"/>, which runs here in the extension (an in-memory pipe, no extra process) and asks the
/// Spyglass hub, the same engine the VS Code extension uses: elements, properties, events, attached properties, values,
/// markup extensions, binding paths from x:DataType, resource keys and component libraries from the project's references.
/// </summary>
[VisualStudioContribution]
internal sealed class AxamlLanguageServerProvider : LanguageServerProvider
{
    readonly SpyglassService _spyglass;

    public AxamlLanguageServerProvider(Extension extension, VisualStudioExtensibility extensibility, SpyglassService spyglass)
        : base(extension, extensibility) => _spyglass = spyglass;

    /// <summary>.axaml files: syntax highlighting (<see cref="AxamlClassificationTaggerProvider"/>) and this language server.</summary>
    [VisualStudioContribution]
    public static DocumentTypeConfiguration AxamlDocumentType => new("axaml")
    {
        FileExtensions = [".axaml"],
        BaseDocumentType = LanguageServerBaseDocumentType,
    };

    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration => new(
        "%Spyglass.AxamlLanguageServer.DisplayName%",
        [DocumentFilter.FromDocumentType(AxamlDocumentType)]);

    public override async Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        await _spyglass.EnsureStartedAsync(cancellationToken);   // starts the hub connection (and `spyglass serve` if needed)
        var (forVisualStudio, forServer) = FullDuplexStream.CreatePipePair();
        var server = new AxamlLanguageServer(_spyglass.Client, line => _spyglass.Log($"IntelliSense: {line}"));
        _ = Task.Run(async () =>
        {
            try { await server.RunAsync(forServer.Input.AsStream(), forServer.Output.AsStream()); }
            catch (Exception ex) { _spyglass.Log($"IntelliSense stopped: {ex.Message}"); }
            finally
            {
                await forServer.Input.CompleteAsync();
                await forServer.Output.CompleteAsync();
            }
        });
        return forVisualStudio;
    }

    public override Task OnServerInitializationResultAsync(ServerInitializationResult serverInitializationResult,
        LanguageServerInitializationFailureInfo? initializationFailureInfo, CancellationToken cancellationToken)
    {
        if (serverInitializationResult == ServerInitializationResult.Failed)
            _spyglass.Log($"IntelliSense could not start: {initializationFailureInfo?.StatusMessage}");
        return base.OnServerInitializationResultAsync(serverInitializationResult, initializationFailureInfo, cancellationToken);
    }
}
