using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Spyglass.VisualStudio.Core.Language;

namespace Spyglass.VisualStudio;

#pragma warning disable VSEXTPREVIEW_TAGGERS // VisualStudio.Extensibility taggers are a preview API (as in Microsoft's ClassificationSample).

/// <summary>
/// Syntax highlighting for .axaml (design/xaml-language/README.md §2): <see cref="AxamlLexer"/> tokens as Visual Studio's
/// built-in markup classifications, so every theme colors them. Out-of-process extensions cannot define new colors, so
/// XAML constructs map to the closest existing ones (attached-property owners and markup extension names as types,
/// x: directives as keywords).
/// </summary>
[VisualStudioContribution]
internal sealed class AxamlClassificationTaggerProvider : ExtensionPart, ITextViewTaggerProvider<ClassificationTag>, ITextViewChangedListener
{
    readonly object _lock = new();
    readonly Dictionary<Uri, List<AxamlClassificationTagger>> _taggers = [];

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType(AxamlLanguageServerProvider.AxamlDocumentType)],
    };

    public Task<TextViewTagger<ClassificationTag>> CreateTaggerAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        var tagger = new AxamlClassificationTagger(this, textView.Document.Uri);
        lock (_lock)
        {
            if (!_taggers.TryGetValue(textView.Document.Uri, out var list)) _taggers[textView.Document.Uri] = list = [];
            list.Add(tagger);
        }
        return Task.FromResult<TextViewTagger<ClassificationTag>>(tagger);
    }

    public Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        if (args.Edits.Count == 0) return Task.CompletedTask;
        List<AxamlClassificationTagger> taggers;
        lock (_lock) taggers = _taggers.TryGetValue(args.AfterTextView.Document.Uri, out var l) ? [.. l] : [];
        return Task.WhenAll(taggers.Select(t => t.DocumentChangedAsync(args.AfterTextView.Document, cancellationToken)));
    }

    internal void Remove(Uri uri, AxamlClassificationTagger tagger)
    {
        lock (_lock)
            if (_taggers.TryGetValue(uri, out var list) && list.Remove(tagger) && list.Count == 0) _taggers.Remove(uri);
    }

    internal static ClassificationType Classification(AxamlTokenKind kind) => kind switch
    {
        AxamlTokenKind.ElementPrefix or AxamlTokenKind.ElementName => ClassificationType.KnownValues.MarkupNode,
        AxamlTokenKind.AttributePrefix or AxamlTokenKind.AttributeName or AxamlTokenKind.PropertyElement or AxamlTokenKind.MarkupArgument
            => ClassificationType.KnownValues.MarkupAttribute,
        AxamlTokenKind.AttributeValue => ClassificationType.KnownValues.MarkupAttributeValue,
        AxamlTokenKind.AttachedOwner or AxamlTokenKind.MarkupExtension or AxamlTokenKind.ExtensionPrefix => ClassificationType.KnownValues.Type,
        AxamlTokenKind.Directive => ClassificationType.KnownValues.Keyword,
        AxamlTokenKind.Comment => ClassificationType.KnownValues.Comment,
        AxamlTokenKind.CData => ClassificationType.KnownValues.String,
        AxamlTokenKind.Entity or AxamlTokenKind.Escape => ClassificationType.KnownValues.Character,
        AxamlTokenKind.ProcessingInstruction => ClassificationType.KnownValues.PreprocessorKeyword,
        _ => ClassificationType.KnownValues.Punctuation,   // delimiters, markup braces and punctuation
    };
}

/// <summary>
/// Tags of one .axaml view. Comments, tags and attribute values span lines (an edit can recolor everything after it), so
/// each update lexes the whole text (fast: a few ms for large files) and re-sends tags for every range Visual Studio asked for.
/// </summary>
internal sealed class AxamlClassificationTagger(AxamlClassificationTaggerProvider provider, Uri uri) : TextViewTagger<ClassificationTag>
{
    public override void Dispose()
    {
        provider.Remove(uri, this);
        base.Dispose();
    }

    protected override Task RequestTagsAsync(NormalizedTextRangeCollection requestedRanges, bool recalculateAll, CancellationToken cancellationToken) =>
        requestedRanges.Count == 0 || requestedRanges.TextDocumentSnapshot is not { } document
            ? Task.CompletedTask
            : UpdateAsync(document, requestedRanges);

    public async Task DocumentChangedAsync(ITextDocumentSnapshot document, CancellationToken cancellationToken)
    {
        var ranges = await GetAllRequestedRangesAsync(document, cancellationToken);
        if (ranges.Count > 0) await UpdateAsync(document, ranges);
    }

    async Task UpdateAsync(ITextDocumentSnapshot document, IReadOnlyCollection<TextRange> ranges)
    {
        var text = document.Text.CopyToString();
        var tags = new List<TaggedTrackingTextRange<ClassificationTag>>();
        var spans = ranges.Select(r => (r.Start, End: r.Start + r.Length)).ToList();
        foreach (var t in AxamlLexer.Tokenize(text))
        {
            if (!spans.Any(s => t.Start < s.End && t.End > s.Start)) continue;   // only what Visual Studio asked for
            tags.Add(new(new TrackingTextRange(document, t.Start, t.Length, TextRangeTrackingMode.ExtendNone),
                new ClassificationTag(AxamlClassificationTaggerProvider.Classification(t.Kind))));
        }
        await UpdateTagsAsync(ranges, tags, CancellationToken.None);
    }
}
