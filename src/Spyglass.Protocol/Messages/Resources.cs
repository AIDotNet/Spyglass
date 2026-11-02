namespace Spyglass.Protocol.Messages;

// resources/* (design/protocol/01-methods.md §13). Provider ids are session-scoped node ids (the same id space as tree nodes):
// an element provider's id IS the element's id.

/// <summary>
/// One resource provider. <see cref="Kind"/>: application | element | dictionary (merged / included) | themeDictionary |
/// styles | style. <see cref="Variant"/> is set on theme dictionaries ("Light", "Dark", "Default"…).
/// </summary>
public sealed record ResourceProviderDto
{
    public required int Id { get; init; }
    public required string Kind { get; init; }
    /// <summary>Display name: "App", "Window#Main", "FluentTheme", "Style Button.primary", "Controls.axaml"…</summary>
    public required string Name { get; init; }
    /// <summary>Declaring XAML / included file when known (Debug builds).</summary>
    public SourceInfo? Source { get; init; }
    /// <summary>Keys defined directly in this provider (not in children).</summary>
    public int KeyCount { get; init; }
    public string? Variant { get; init; }
    public IReadOnlyList<ResourceProviderDto>? Children { get; init; }
}

// ---- resources/getProviders ----
/// <param name="ElementId">When set, also returns the elements from this one up the logical tree that declare resources (nearest first).</param>
public sealed record ResourcesGetProvidersParams(int? ElementId = null);
/// <param name="Providers">The Application first, then element providers (nearest first).</param>
/// <param name="ActiveVariant">The theme variant in effect (the element's, or the Application's).</param>
public sealed record ResourcesGetProvidersResult(IReadOnlyList<ResourceProviderDto> Providers, string ActiveVariant, IReadOnlyList<string> Variants);

// ---- resources/getEntries ----
/// <param name="ThemeVariant">Also include the keys of this variant's theme dictionary directly under the provider (each flagged with its variant).</param>
/// <param name="Query">Case-insensitive substring of the key or the displayed value.</param>
public sealed record ResourcesGetEntriesParams(int ProviderId, string? ThemeVariant = null, string? Query = null, int Limit = 3000);

public sealed record ResourceEntryDto
{
    /// <summary>Display text; Type keys ({x:Type Button}) show the type name.</summary>
    public required string Key { get; init; }
    /// <summary>"string" or "type" (other key objects are shown with ToString()).</summary>
    public string KeyType { get; init; } = "string";
    public required ValueDto Value { get; init; }
    /// <summary>Markup as written when the stored value is a DynamicResource / StaticResource reference ("{DynamicResource X}"); <see cref="Value"/> is then the resolved value.</summary>
    public string? Reference { get; init; }
    public SourceInfo? Source { get; init; }
    /// <summary>Theme variant when the entry comes from a theme dictionary.</summary>
    public string? Variant { get; init; }
    /// <summary>The value can be changed with resources/set.</summary>
    public bool Editable { get; init; }
    public bool IsModifiedBySpyglass { get; init; }
}

public sealed record ResourcesGetEntriesResult(ResourceProviderDto Provider, IReadOnlyList<ResourceEntryDto> Entries, bool Truncated);

// ---- resources/lookup ----
/// <param name="ElementId">Start here (as TryFindResource would); null = start at the Application.</param>
/// <param name="ThemeVariant">"Light" | "Dark" | "Default"; null = the active variant.</param>
public sealed record ResourcesLookupParams(string Key, int? ElementId = null, string? ThemeVariant = null);

/// <summary>One provider the lookup visited, in precedence order.</summary>
public sealed record ResourceStepDto
{
    public required ResourceProviderDto Provider { get; init; }
    /// <summary>Provider path, e.g. "Application › Styles › FluentTheme › ThemeDictionaries[Dark]".</summary>
    public required string Path { get; init; }
    public bool Hit { get; init; }
    /// <summary>The first hit: the one that is used.</summary>
    public bool Winner { get; init; }
    /// <summary>A later hit that is hidden by the winner.</summary>
    public bool Shadowed { get; init; }
    public ValueDto? Value { get; init; }
    public SourceInfo? Source { get; init; }
}

public sealed record ResourcesLookupResult
{
    public required string Key { get; init; }
    public required string Variant { get; init; }
    public bool Found { get; init; }
    /// <summary>What TryFindResource really returned (authoritative); the chain explains where it came from.</summary>
    public ValueDto? Value { get; init; }
    public IReadOnlyList<ResourceStepDto> Chain { get; init; } = [];
    public bool Truncated { get; init; }
    public string? Note { get; init; }
}

// ---- resources/find (app-wide key search; used by MCP resources_find) ----
/// <param name="Query">Exact key if one exists, otherwise a case-insensitive substring.</param>
public sealed record ResourcesFindParams(string Query, int? ElementId = null, string? ThemeVariant = null, int Limit = 10);
/// <param name="Lookups">One per theme variant (Light and Dark), each with its provider chain.</param>
public sealed record ResourceFindKey(string Key, IReadOnlyList<ResourcesLookupResult> Lookups);
public sealed record ResourcesFindResult(IReadOnlyList<ResourceFindKey> Keys, bool Exact, bool Truncated, string ActiveVariant = "");

// ---- resources/set / resources/reset ----
/// <summary>Changes the value of an EXISTING key (never adds or removes keys). Theme dictionaries are providers of their own: pass their id.</summary>
public sealed record ResourcesSetParams(int ProviderId, string Key, ValueDto Value);
public sealed record ResourcesSetResult(ResourceEntryDto Entry);
public sealed record ResourcesResetParams(int? ProviderId = null, string? Key = null, bool All = false);
