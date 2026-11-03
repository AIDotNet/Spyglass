// Tokenizes .axaml with the shipped grammars on top of VS Code's built-in XML grammar (test/fixtures/xml.tmLanguage.json,
// MIT, converted by VS Code from atom/language-xml) and checks the scopes of the XAML constructs.
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { test } from "node:test";
import * as oniguruma from "vscode-oniguruma";
import * as tm from "vscode-textmate";

const root = path.join(__dirname, "..", "..");
const pkg = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const contributed: { scopeName: string; path: string; injectTo?: string[] }[] = pkg.contributes.grammars;

const registry = (base: "text.xml.axaml" | "source.axaml") => new tm.Registry({
  onigLib: (async () => {
    await oniguruma.loadWASM(fs.readFileSync(require.resolve("vscode-oniguruma/release/onig.wasm")).buffer);
    return { createOnigScanner: (p: string[]) => new oniguruma.OnigScanner(p), createOnigString: (s: string) => new oniguruma.OnigString(s) };
  })(),
  loadGrammar: async (scope) => {
    if (scope === "text.xml") return tm.parseRawGrammar(fs.readFileSync(path.join(root, "test", "fixtures", "xml.tmLanguage.json"), "utf8"), "xml.json");
    // Avalonia's own extension: a copy of the XML grammar under another scope name.
    if (scope === "source.axaml") return tm.parseRawGrammar(JSON.stringify({ scopeName: "source.axaml", patterns: [{ include: "text.xml" }] }), "a.json");
    const g = contributed.find((c) => c.scopeName === scope);
    return g ? tm.parseRawGrammar(fs.readFileSync(path.join(root, g.path), "utf8"), g.path) : null;
  },
  getInjections: (scope) => contributed.filter((c) => c.injectTo?.includes(scope)).map((c) => c.scopeName),
});

async function tokenize(text: string, base: "text.xml.axaml" | "source.axaml" = "text.xml.axaml") {
  const grammar = (await registry(base).loadGrammar(base))!;
  const out: { text: string; scopes: string[] }[] = [];
  let stack = tm.INITIAL;
  for (const line of text.split("\n")) {
    const r = grammar.tokenizeLine(line, stack);
    for (const t of r.tokens) out.push({ text: line.substring(t.startIndex, t.endIndex), scopes: t.scopes });
    stack = r.ruleStack;
  }
  return out;
}

/** Scopes of the first token whose text is exactly `text` (after the `nth` match). */
function scopesOf(tokens: { text: string; scopes: string[] }[], text: string, nth = 0): string[] {
  const hits = tokens.filter((t) => t.text === text);
  assert.ok(hits.length > nth, `no token "${text}" in: ${tokens.map((t) => JSON.stringify(t.text)).join(" ")}`);
  return hits[nth].scopes;
}

const has = (scopes: string[], s: string) => assert.ok(scopes.includes(s), `expected ${s} in ${scopes.join(" ")}`);
const lacks = (scopes: string[], s: string) => assert.ok(!scopes.some((x) => x.startsWith(s)), `unexpected ${s} in ${scopes.join(" ")}`);

test("grammar: plain XML scopes are kept for tags, attributes, strings and comments", async () => {
  const t = await tokenize(`<Window xmlns="https://github.com/avaloniaui" Title="Hi"><!-- note --></Window>`);
  has(scopesOf(t, "Window"), "entity.name.tag.localname.xml");
  has(scopesOf(t, "Title"), "entity.other.attribute-name.localname.xml");
  has(scopesOf(t, "Hi"), "string.quoted.double.xml");
  has(scopesOf(t, " note "), "comment.block.xml");
});

test("grammar: markup extensions with arguments, nesting and a quoted StringFormat", async () => {
  const t = await tokenize(`<TextBlock Text="{Binding Price, Mode=OneWay, Converter={StaticResource Money}, StringFormat='{}{0:N2}'}" />`);
  has(scopesOf(t, "Binding"), "support.function.markup-extension.axaml");
  has(scopesOf(t, "{"), "punctuation.section.embedded.begin.axaml");
  has(scopesOf(t, "Mode"), "variable.parameter.markup-extension.axaml");
  has(scopesOf(t, "StaticResource"), "support.function.markup-extension.axaml");
  has(scopesOf(t, "StaticResource"), "meta.embedded.markup-extension.axaml");
  has(scopesOf(t, "{}{0:N2}"), "string.quoted.single.axaml");
  // The attribute string closes normally and the tag ends: nothing leaks.
  const last = t[t.length - 1];
  assert.equal(last.text, "/>");
  lacks(last.scopes, "meta.embedded.markup-extension");
});

test("grammar: an unclosed markup extension ends at the attribute's quote", async () => {
  const t = await tokenize(`<TextBlock Text="{Binding Na" Foreground="Red" />`);
  lacks(scopesOf(t, "Foreground"), "meta.embedded.markup-extension");
  has(scopesOf(t, "Foreground"), "entity.other.attribute-name.localname.xml");
  const sq = await tokenize(`<TextBlock Text='{Binding Na' Foreground="Red" />`);
  lacks(scopesOf(sq, "Foreground"), "meta.embedded.markup-extension");
});

test("grammar: prefixed extensions, types, $parent and #name", async () => {
  const t = await tokenize(`<Button Command="{Binding $parent[Window].DataContext.Save}" Content="{x:Static local:Strings.Save}" Tag="{Binding #Input.Text}" />`);
  has(scopesOf(t, "x"), "entity.name.namespace.axaml");
  has(scopesOf(t, "Static"), "support.function.markup-extension.axaml");
  has(scopesOf(t, "Strings.Save"), "entity.name.type.axaml");
  has(scopesOf(t, "parent"), "variable.language.axaml");
  has(scopesOf(t, "Input"), "variable.other.element-name.axaml");
});

test("grammar: {} escape is a literal, not an extension", async () => {
  const t = await tokenize(`<TextBlock Text="{}{literal}" />`);
  has(scopesOf(t, "{}"), "constant.character.escape.markup-extension.axaml");
  assert.ok(!t.some((x) => x.scopes.includes("support.function.markup-extension.axaml")));
});

test("grammar: property elements and attached properties", async () => {
  const t = await tokenize(`<Grid.RowDefinitions>\n<Button Grid.Row="1" local:Ext.Hint="x" />\n</Grid.RowDefinitions>`);
  has(scopesOf(t, "Grid"), "entity.name.tag.localname.xml");
  has(scopesOf(t, "RowDefinitions"), "support.type.property-name.axaml");
  has(scopesOf(t, "RowDefinitions", 1), "support.type.property-name.axaml");   // closing tag
  has(scopesOf(t, "Grid", 1), "entity.name.type.attached.axaml");
  has(scopesOf(t, "Row"), "entity.other.attribute-name.attached.axaml");
  has(scopesOf(t, "Ext"), "entity.name.type.attached.axaml");
  has(scopesOf(t, "local"), "entity.other.attribute-name.namespace.xml");
});

test("grammar: x: directives and using:/clr-namespace: URIs", async () => {
  const t = await tokenize(`<UserControl x:Class="App.Main" x:DataType="vm:MainVm" xmlns:vm="using:App.ViewModels" xmlns:c="clr-namespace:Lib.Controls;assembly=Lib" />`);
  has(scopesOf(t, "Class"), "keyword.other.directive.axaml");
  has(scopesOf(t, "DataType"), "keyword.other.directive.axaml");
  has(scopesOf(t, "using"), "keyword.other.namespace-scheme.axaml");
  has(scopesOf(t, "App.ViewModels"), "entity.name.namespace.axaml");
  has(scopesOf(t, "clr-namespace"), "keyword.other.namespace-scheme.axaml");
  has(scopesOf(t, "Lib"), "entity.name.namespace.axaml");
});

test("grammar: nothing XAML-specific inside comments or text content", async () => {
  const t = await tokenize(`<!-- <Grid.Row> Text="{Binding}" -->\n<TextBlock>Grid.Row="1" {Binding}</TextBlock>`);
  assert.ok(!t.some((x) => x.scopes.some((s) => s.endsWith(".axaml") && s !== "text.xml.axaml")), t.filter((x) => x.scopes.some((s) => s.endsWith(".axaml") && s !== "text.xml.axaml")).map((x) => x.text).join("|"));
});

test("grammar: the rules also apply on top of Avalonia's own source.axaml grammar", async () => {
  const t = await tokenize(`<Button Grid.Row="1" Content="{Binding Name}" />`, "source.axaml");
  has(scopesOf(t, "Binding"), "support.function.markup-extension.axaml");
  has(scopesOf(t, "Row"), "entity.other.attribute-name.attached.axaml");
});
