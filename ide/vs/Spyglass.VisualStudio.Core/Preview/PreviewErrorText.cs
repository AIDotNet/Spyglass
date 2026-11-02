using System.Text.RegularExpressions;

namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>Turns the previewer host's error text (<c>ex.ToString()</c>, stack trace included) into one line for the error bar and the Error List.</summary>
public static class PreviewErrorText
{
    /// <summary>Exceptions that only wrap the real cause (the previewer loads XAML through reflection, so runtime errors arrive wrapped).</summary>
    static readonly Regex Wrappers = new(@"^(System\.Reflection\.TargetInvocationException|System\.TypeInitializationException|System\.AggregateException)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The first exception in the chain (outer, then each <c> ---&gt; </c> inner) that is not a mere wrapper, so
    /// "TargetInvocationException: Exception has been thrown by the target of an invocation." shows its actual cause.
    /// </summary>
    public static string Summary(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var headers = new List<string>();
        var first = lines.FirstOrDefault(l => l.Length > 0);
        if (first is not null) headers.Add(first);
        headers.AddRange(lines.Where(l => l.StartsWith("--->", StringComparison.Ordinal)).Select(l => l.Substring(4).Trim()));
        var pick = headers.FirstOrDefault(h => !Wrappers.IsMatch(h)) ?? headers.LastOrDefault() ?? text.Trim();
        return pick.Length > 300 ? pick.Substring(0, 297) + "…" : pick;
    }
}
