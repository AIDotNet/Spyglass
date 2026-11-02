using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Markup.Xaml.Diagnostics;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal;

/// <summary>
/// Element queries for tree/search (design/mcp/README.md §5):
/// selector subset <c>Type#Name.class:pseudo:nth(n)</c> with descendant (' ') and child ('>') combinators,
/// <c>text=…</c> (visible text, case-insensitive substring), <c>xaml=File.axaml:line</c>.
/// Type matching accepts the exact type or any base type name. Template parts are skipped unless requested.
/// </summary>
internal sealed class SelectorQuery
{
    enum Combinator { Descendant, Child }

    sealed record Compound(string? Type, string? Name, IReadOnlyList<string> Classes, IReadOnlyList<string> Pseudo, int? Nth);

    readonly List<(Combinator Combinator, Compound Compound)> _chain = new();
    readonly string? _text;
    readonly (string File, int Line)? _xaml;

    public SelectorQuery(string query)
    {
        query = query.Trim();
        if (query.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
        {
            _text = query.Substring(5).Trim().Trim('"', '\'');
            return;
        }
        if (query.StartsWith("xaml=", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(query.Substring(5).Trim(), @"^(.+?):(\d+)$");
            if (!m.Success) throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "xaml= expects File.axaml:line");
            _xaml = (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            return;
        }
        Parse(query);
    }

    void Parse(string q)
    {
        var tokens = Regex.Split(Regex.Replace(q, @"\s*>\s*", " > "), @"\s+").Where(t => t.Length > 0).ToList();
        var next = Combinator.Descendant;
        foreach (var t in tokens)
        {
            if (t == ">") { next = Combinator.Child; continue; }
            _chain.Add((next, ParseCompound(t)));
            next = Combinator.Descendant;
        }
        if (_chain.Count == 0) throw new SpyglassProtocolException(ErrorCodes.InvalidParams, "empty selector");
    }

    static Compound ParseCompound(string s)
    {
        var m = Regex.Match(s, @"^(?<type>\*|[A-Za-z_][\w]*)?(?<rest>.*)$");
        string? type = m.Groups["type"].Success && m.Groups["type"].Value is { Length: > 0 } tv && tv != "*" ? tv : null;
        string? name = null;
        int? nth = null;
        var classes = new List<string>();
        var pseudo = new List<string>();
        foreach (Match part in Regex.Matches(m.Groups["rest"].Value, @"(?<k>[#.:])(?<v>[\w-]+)(\((?<a>\d+)\))?"))
        {
            var v = part.Groups["v"].Value;
            switch (part.Groups["k"].Value)
            {
                case "#": name = v; break;
                case ".": classes.Add(v); break;
                case ":" when v == "nth" && part.Groups["a"].Success: nth = int.Parse(part.Groups["a"].Value); break;
                case ":": pseudo.Add(":" + v); break;
            }
        }
        return new Compound(type, name, classes, pseudo, nth);
    }

    public IEnumerable<object> Run(TreeWalker walker, IEnumerable<object> roots, TreeKind tree, bool includeTemplate)
    {
        var candidates = roots.SelectMany(r => walker.DescendantsAndSelf(r, tree))
            .Where(o => includeTemplate || !IsTemplatePart(o));

        if (_text is not null)
        {
            // Exact matches first, then substring matches; text in template parts (e.g. a Button's inner TextBlock)
            // is attributed to the owning control via TextOf on the control itself.
            var list = candidates.Select(o => (o, t: TreeWalker.TextOf(o)))
                .Where(x => x.t is not null && x.t.IndexOf(_text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            // A Button with Content="Save" also generates an inner AccessText/TextBlock "Save": keep the outermost.
            var set = new HashSet<object>(list.Select(x => x.o), ReferenceEqualityComparer.Instance);
            list = list.Where(x => !HasAncestorIn(x.o, set, tree)).ToList();
            return list.Where(x => string.Equals(x.t!.Trim(), _text, StringComparison.OrdinalIgnoreCase)).Select(x => x.o)
                .Concat(list.Where(x => !string.Equals(x.t!.Trim(), _text, StringComparison.OrdinalIgnoreCase)).Select(x => x.o));
        }

        if (_xaml is { } xaml)
            return candidates.Where(o => XamlSourceInfo.GetXamlSourceInfo(o) is { } si
                                         && XamlLines.ToOneBased(si.LineNumber, o) == xaml.Line
                                         && si.SourceUri?.ToString().EndsWith(xaml.File, StringComparison.OrdinalIgnoreCase) == true);

        return candidates.Where(o => MatchesChain(o, _chain.Count - 1, tree, includeTemplate));
    }

    bool MatchesChain(object o, int index, TreeKind tree, bool includeTemplate)
    {
        if (!MatchesCompound(o, _chain[index].Compound, tree, includeTemplate)) return false;
        if (index == 0) return true;
        var combinator = _chain[index].Combinator;
        for (var p = ParentOf(o, tree, includeTemplate); p is not null; p = ParentOf(p, tree, includeTemplate))
        {
            if (MatchesChain(p, index - 1, tree, includeTemplate)) return true;
            if (combinator == Combinator.Child) return false;
        }
        return false;
    }

    static bool HasAncestorIn(object o, HashSet<object> set, TreeKind tree)
    {
        for (var p = TreeWalker.Parent(o, tree); p is not null; p = TreeWalker.Parent(p, tree))
            if (set.Contains(p)) return true;
        return false;
    }

    static object? ParentOf(object o, TreeKind tree, bool includeTemplate)
    {
        var p = TreeWalker.Parent(o, tree);
        while (!includeTemplate && p is not null && IsTemplatePart(p)) p = TreeWalker.Parent(p, tree);
        return p;
    }

    static bool MatchesCompound(object o, Compound c, TreeKind tree, bool includeTemplate)
    {
        if (c.Type is { } type && !IsOfType(o, type)) return false;
        if (o is not StyledElement se) return c.Name is null && c.Classes.Count == 0 && c.Pseudo.Count == 0 && c.Nth is null;
        if (c.Name is not null && se.Name != c.Name) return false;
        if (c.Classes.Any(cl => !se.Classes.Contains(cl))) return false;
        if (c.Pseudo.Any(pc => !se.Classes.Contains(pc))) return false;
        if (c.Nth is { } nth)
        {
            var parent = ParentOf(o, tree, includeTemplate);
            if (parent is null) return nth == 1;
            var siblings = TreeWalker.Children(parent, tree)
                .Where(s => MatchesCompound(s, c with { Nth = null }, tree, includeTemplate)).ToList();
            return siblings.IndexOf(o) == nth - 1;
        }
        return true;
    }

    static bool IsOfType(object o, string name)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsTemplatePart(object o) => o is StyledElement { TemplatedParent: not null };
}
