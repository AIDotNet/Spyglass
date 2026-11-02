using System.Reflection;
using System.Xml;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.HotReload;

/// <summary>Turns runtime XAML compiler/loader exceptions into protocol diagnostics with 1-based positions.</summary>
internal static class XamlErrors
{
    public static IEnumerable<XamlDiagnostic> From(Exception ex)
    {
        var leaves = Flatten(ex).ToList();
        var seen = new HashSet<string>();
        foreach (var e in leaves)
        {
            var message = FirstLine(e.Message);
            if (!seen.Add(message)) continue;
            yield return e is XmlException xe && xe.LineNumber > 0
                ? new XamlDiagnostic(XamlDiagnosticSeverity.Error, StripPosition(message), xe.LineNumber, xe.LinePosition, e.GetType().Name)
                : new XamlDiagnostic(XamlDiagnosticSeverity.Error, message, Code: e.GetType().Name);
        }
    }

    static IEnumerable<Exception> Flatten(Exception ex) => ex switch
    {
        AggregateException ae => ae.InnerExceptions.SelectMany(Flatten),
        TargetInvocationException { InnerException: { } inner } => Flatten(inner),
        _ => [ex],
    };

    static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>XamlX appends "Line N, position M." to messages; the position is reported separately.</summary>
    static string StripPosition(string s)
    {
        var i = s.LastIndexOf(" Line ", StringComparison.Ordinal);
        return i > 0 && s.IndexOf("position", i, StringComparison.Ordinal) > 0 ? s[..i].TrimEnd() : s;
    }
}
