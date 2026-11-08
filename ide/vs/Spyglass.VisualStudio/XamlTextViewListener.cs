using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Spyglass.VisualStudio;

/// <summary>
/// .axaml/.xaml editors → hub. Visual Studio calls this out of process for every text view of a matching file; the
/// snapshot's text is read only when the debounced edit is actually sent (and only for files a running app compiled).
/// </summary>
[VisualStudioContribution]
internal sealed class XamlTextViewListener : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener
{
    readonly SpyglassService _spyglass;
    readonly SpyglassPreviewService _preview;

    public XamlTextViewListener(Extension extension, VisualStudioExtensibility extensibility, SpyglassService spyglass, SpyglassPreviewService preview)
        : base(extension, extensibility)
    {
        _spyglass = spyglass;
        _preview = preview;
    }

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo =
        [
            DocumentFilter.FromGlobPattern("**/*.axaml", relativePath: false),
            DocumentFilter.FromGlobPattern("**/*.xaml", relativePath: false),
        ],
    };

    public async Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        await _spyglass.EnsureStartedAsync(cancellationToken);
        if (PathOf(textView) is not { } path) return;
        var document = textView.Document;
        _spyglass.ViewOpened(path, () => document.Text.CopyToString(), document.IsDirty);
        _preview.ViewOpened(path, () => document.Text.CopyToString());
    }

    public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        if (args.Edits.Count == 0) return;   // caret or selection moved
        await _spyglass.EnsureStartedAsync(cancellationToken);
        if (PathOf(args.AfterTextView) is not { } path) return;
        var document = args.AfterTextView.Document;
        _spyglass.ViewChanged(path, () => document.Text.CopyToString(), document.IsDirty);
        _preview.ViewChanged(path, () => document.Text.CopyToString());
    }

    public async Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        await _spyglass.EnsureStartedAsync(cancellationToken);
        if (PathOf(textView) is { } path && _spyglass.ViewClosed(path)) _preview.ViewClosed(path);
    }

    static string? PathOf(ITextViewSnapshot view) =>
        view.Document.Uri is { IsFile: true } uri ? uri.LocalPath : view.FilePath;
}
