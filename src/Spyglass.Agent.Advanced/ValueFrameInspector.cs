using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Diagnostics;
using Avalonia.Markup.Xaml.Diagnostics;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Spyglass.Agent.Internal;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Advanced;

/// <summary>
/// styles/getFrames and bindings/getDetails on Avalonia [PrivateApi] diagnostics (spike S1, A1/A2). UI-thread only.
/// </summary>
internal sealed class ValueFrameInspector(NodeRegistry registry, ValueCodec codec)
{
    public FramesResult Frames(int id)
    {
        var ao = registry.Get<AvaloniaObject>(id);
        var frames = ao.GetValueStoreDiagnostic().AppliedFrames.ToList();

        // For each property, find the frame whose value is in effect; every other active frame setting it is overridden.
        var winners = new Dictionary<AvaloniaProperty, IValueFrameDiagnostic>();
        foreach (var prop in frames.SelectMany(f => f.Values.Select(v => v.Property)).Distinct())
        {
            var effective = ao.GetDiagnostic(prop);
            var candidates = frames.Where(f => f.IsActive && f.Priority == effective.Priority && f.Values.Any(v => v.Property == prop)).ToList();
            var winner = candidates.Count <= 1
                ? candidates.FirstOrDefault()
                : candidates.LastOrDefault(f => Equals(f.Values.First(v => v.Property == prop).Value, effective.Value)) ?? candidates.Last();
            if (winner is not null) winners[prop] = winner;
        }

        return new FramesResult(frames.Select(f => new ValueFrameDto(
            f.Type.ToString(),
            f.Priority.ToString(),
            f.IsActive,
            SourceOf(f),
            f.Values
                .Where(v => v.Property.Name != "XamlSourceInfo")
                .Select(v => new SetterDto(v.Property.Name, Encode(ao, v.Value),
                    f.IsActive && !(winners.TryGetValue(v.Property, out var w) && ReferenceEquals(w, f))))
                .ToList())).ToList());
    }

    public BindingDetailsResult Binding(int id, string property)
    {
        var ao = registry.Get<AvaloniaObject>(id);
        var prop = AvaloniaPropertyRegistry.Instance.GetRegistered(ao.GetType()).FirstOrDefault(p => p.Name == property)
                   ?? throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"{ao.GetType().Name} has no property '{property}'.");
        if (BindingOperations.GetBindingExpressionBase(ao, prop) is not UntypedBindingExpressionBase expr)
            throw new SpyglassProtocolException(ErrorCodes.InvalidParams, $"{property} is not bound.");
        return new BindingDetailsResult(
            expr.Description ?? "",
            Mode: null,
            Priority: expr.Priority.ToString(),
            ErrorType: expr.ErrorType == BindingErrorType.None ? null : expr.ErrorType.ToString(),
            Converter: null,
            FallbackValue: null,
            Value: codec.Encode(ao.GetValue(prop)));
    }

    static FrameSource SourceOf(IValueFrameDiagnostic f) => f.Source switch
    {
        Style s => new FrameSource(FrameSourceKind.Style, Selector: s.Selector?.ToString(), Location: Location(s)),
        ControlTheme t => new FrameSource(FrameSourceKind.ControlTheme, TargetType: t.TargetType?.Name, Location: Location(t)),
        _ when f.Type.ToString() == "Local" => new FrameSource(FrameSourceKind.Local),
        _ when f.Type.ToString() == "Template" => new FrameSource(FrameSourceKind.Template, TargetType: f.Source?.GetType().Name),
        _ => new FrameSource(FrameSourceKind.Other, TargetType: f.Source?.GetType().Name),
    };

    static SourceInfo? Location(object o) =>
        XamlSourceInfo.GetXamlSourceInfo(o) is { SourceUri: { } uri } si ? new SourceInfo(uri.ToString(), XamlLines.ToOneBased(si.LineNumber, o), si.LinePosition) : null;

    /// <summary>Setter values: show markup extensions the way they were written, with the resolved resource value.</summary>
    ValueDto Encode(AvaloniaObject owner, object? value)
    {
        switch (value)
        {
            case DynamicResourceExtension d:
                var text = $"{{DynamicResource {d.ResourceKey}}}";
                if (owner is StyledElement se && d.ResourceKey is { } key && se.TryFindResource(key, se.ActualThemeVariant, out var resolved))
                    text += " = " + codec.Encode(resolved).Text;
                return new ValueDto(ValueKind.String, text);
            case BindingBase:
                return new ValueDto(ValueKind.String, "{Binding}");
            case ITemplate:
                return new ValueDto(ValueKind.Object, "{" + value.GetType().Name + "}");
            default:
                return codec.Encode(value);
        }
    }
}
