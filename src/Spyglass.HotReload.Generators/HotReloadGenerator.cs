using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Spyglass.HotReload.Generators;

/// <summary>
/// For every .axaml that Avalonia compiles (AdditionalFiles with SourceItemGroup=AvaloniaXaml, injected by Avalonia's own
/// targets) emits a "stamp" type holding the document's path, avares URI, x:Class and text, plus a module initializer that
/// registers them with Spyglass.Agent.HotReload (design/hot-reload/README.md §3.1; the stamps become the hot reload delta
/// channel in HR3, §6.1). Only runs when the Spyglass.HotReload targets enabled it (Debug builds).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HotReloadGenerator : IIncrementalGenerator
{
    const string RuntimeType = "Spyglass.Agent.HotReload.SpyglassHotReload";

    sealed record Doc(string Path, string Uri, string? Class, string Text, string Id);
    sealed record Settings(bool Enabled, string ProjectDir, bool CompiledBindings, bool OwnsInitializeComponent);
    sealed record View(string Namespace, string Name, string Id);

    public void Initialize(IncrementalGeneratorInitializationContext ctx)
    {
        var settings = ctx.AnalyzerConfigOptionsProvider.Select((o, _) =>
        {
            var g = o.GlobalOptions;
            return new Settings(
                Flag(g, "build_property.SpyglassHotReload"),
                Value(g, "build_property.SpyglassHotReloadProjectDir") ?? "",
                Flag(g, "build_property.AvaloniaUseCompiledBindingsByDefault"),
                // Set by Spyglass.HotReload.targets when it switched Avalonia's name generator to OnlyProperties (design §5.1).
                Flag(g, "build_property.SpyglassHotReloadOwnsInitializeComponent")
                    && Value(g, "build_property.AvaloniaNameGeneratorBehavior") == "OnlyProperties");
        });
        var hasRuntime = ctx.CompilationProvider.Select((c, _) => (c.AssemblyName ?? "", c.GetTypeByMetadataName(RuntimeType) is not null));

        var docs = ctx.AdditionalTextsProvider
            .Combine(ctx.AnalyzerConfigOptionsProvider)
            .Where(p => Value(p.Right.GetOptions(p.Left), "build_metadata.AdditionalFiles.SourceItemGroup") == "AvaloniaXaml")
            .Select((p, ct) => (p.Left.Path, Text: p.Left.GetText(ct)?.ToString() ?? "", Link: Value(p.Right.GetOptions(p.Left), "build_metadata.AdditionalFiles.Link")))
            .Combine(settings)
            .Combine(hasRuntime)
            .Select((x, _) =>
            {
                var ((file, s), (asm, _)) = x;
                var rel = !string.IsNullOrEmpty(file.Link) ? file.Link! : Relative(s.ProjectDir, file.Path);
                var cls = Regex.Match(file.Text, "x:Class\\s*=\\s*\"([^\"]+)\"");
                return new Doc(file.Path, $"avares://{asm}/{rel.Replace('\\', '/').TrimStart('/')}", cls.Success ? cls.Groups[1].Value : null, file.Text, Id(file.Path));
            });

        // One source per document: an edit regenerates only that stamp (smallest possible delta for HR3).
        ctx.RegisterSourceOutput(docs.Combine(settings).Combine(hasRuntime), (spc, x) =>
        {
            var ((doc, s), (_, runtime)) = x;
            if (!s.Enabled || !runtime) return;
            spc.AddSource($"SpyglassXamlStamp_{doc.Id}.g.cs", Stamp(doc));
        });

        // InitializeComponent for x:Class views without a hand-written one (design §5.1, spike S7).
        var views = docs.Where(d => d.Class is not null)
            .Combine(ctx.CompilationProvider)
            .Select((x, _) =>
            {
                var (doc, compilation) = x;
                var type = compilation.GetTypeByMetadataName(doc.Class!);
                if (type is null || type.GetMembers("InitializeComponent").Length > 0) return null;   // hand-written one wins
                if (!IsStyledElement(type)) return null;   // Application/Styles: no InitializeComponent convention
                var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
                return new View(ns, type.Name, doc.Id);
            });
        ctx.RegisterSourceOutput(views.Combine(settings).Combine(hasRuntime), (spc, x) =>
        {
            var ((view, s), (_, runtime)) = x;
            if (view is null || !s.Enabled || !runtime || !s.OwnsInitializeComponent) return;
            spc.AddSource($"SpyglassInitializeComponent_{view.Id}.g.cs", InitializeComponent(view));
        });

        ctx.RegisterSourceOutput(docs.Collect().Combine(settings).Combine(hasRuntime), (spc, x) =>
        {
            var ((all, s), (_, runtime)) = x;
            if (!s.Enabled || !runtime || all.IsEmpty) return;
            spc.AddSource("SpyglassHotReload.ModuleInitializer.g.cs", Initializer(all, s.CompiledBindings));
        });
    }

    static string Stamp(Doc d) => $$"""
        // <auto-generated/> Spyglass.HotReload: {{d.Path}}
        #nullable enable
        namespace Spyglass.Generated
        {
            // Recreated (not edited) by .NET Hot Reload when the .axaml changes, so the delta's updated types include it.
            [global::System.Runtime.CompilerServices.CreateNewOnMetadataUpdate]
            [global::System.CodeDom.Compiler.GeneratedCode("Spyglass.HotReload.Generators", "1.0")]
            internal static class SpyglassXamlStamp_{{d.Id}}
            {
                public const string Path = {{Literal(d.Path)}};
                public const string Uri = {{Literal(d.Uri)}};
                public const string? Class = {{(d.Class is null ? "null" : Literal(d.Class))}};
                public static string Text() => {{Literal(d.Text)}};
            }
        }
        """;

    static bool IsStyledElement(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "Avalonia.StyledElement") return true;
        return false;
    }

    static string InitializeComponent(View v)
    {
        var cls = $$"""
            partial class {{v.Name}}
            {
                /// <summary>Wires up the component and loads its XAML (Spyglass.HotReload, Debug builds; design/hot-reload §5.1).</summary>
                /// <param name="loadXaml">Should the XAML be loaded into the component.</param>
                [global::System.CodeDom.Compiler.GeneratedCode("Spyglass.HotReload.Generators", "1.0")]
                [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
                public void InitializeComponent(bool loadXaml = true)
                {
                    // Through XamlIl's trampoline by reflection: stays valid when a Hot Reload delta recompiles this method,
                    // and honours the hot reload override. The XAML compiler requires the Load(this) call (AVLN3000); it is
                    // also the fallback.
                    if (loadXaml && !global::Spyglass.Agent.HotReload.SpyglassHotReload.Populate(this))
                        global::Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
                }
            }
            """;
        var head = "// <auto-generated/> Spyglass.HotReload\n#nullable enable\n";
        return v.Namespace.Length == 0 ? head + cls : $"{head}namespace {v.Namespace}\n{{\n{cls}\n}}\n";
    }

    static string Initializer(ImmutableArray<Doc> docs, bool compiledBindings)
    {
        var sb = new StringBuilder();
        foreach (var d in docs.OrderBy(d => d.Path, StringComparer.Ordinal))
            sb.Append($"                new global::Spyglass.Agent.HotReload.SpyglassXamlDocument(SpyglassXamlStamp_{d.Id}.Path, SpyglassXamlStamp_{d.Id}.Uri, SpyglassXamlStamp_{d.Id}.Class, SpyglassXamlStamp_{d.Id}.Text),\n");
        return $$"""
            // <auto-generated/> Spyglass.HotReload
            #nullable enable
            #pragma warning disable CA2255 // ModuleInitializer in a library: intended, registers this assembly's XAML for hot reload
            namespace Spyglass.Generated
            {
                [global::System.CodeDom.Compiler.GeneratedCode("Spyglass.HotReload.Generators", "1.0")]
                internal static class SpyglassHotReloadModule
                {
                    [global::System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Register() => global::Spyglass.Agent.HotReload.SpyglassHotReload.Register(
                        typeof(SpyglassHotReloadModule).Assembly, {{(compiledBindings ? "true" : "false")}},
            {{sb.ToString().TrimEnd('\n', ',')}});
                }
            }
            """;
    }

    static bool Flag(AnalyzerConfigOptions o, string key) => string.Equals(Value(o, key), "true", StringComparison.OrdinalIgnoreCase);
    static string? Value(AnalyzerConfigOptions o, string key) => o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    static string Relative(string dir, string path)
    {
        if (string.IsNullOrEmpty(dir)) return Path.GetFileName(path);
        var d = dir.Replace('\\', '/').TrimEnd('/') + "/";
        var p = path.Replace('\\', '/');
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) ? p.Substring(d.Length) : Path.GetFileName(path);
    }

    static string Literal(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";

    static string Id(string path)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(path.Replace('\\', '/')));
        return string.Concat(h.Take(6).Select(b => b.ToString("x2")));
    }
}
