// Avalonia XAML IntelliSense in VS Code (design/xaml-language/README.md): completion, documentation and hover for .axaml,
// answered by the Spyglass hub from the file's project (Avalonia, the component libraries it references, its own build).
import * as vscode from "vscode";
import { HubClient, XamlCompletionItem } from "./hubClient";

export const AXAML_SELECTOR: vscode.DocumentSelector = [
  { language: "axaml" },
  { scheme: "file", pattern: "**/*.axaml" },   // also when the user maps .axaml to another language (e.g. xml)
];

const KINDS: Record<string, vscode.CompletionItemKind> = {
  element: vscode.CompletionItemKind.Class,
  type: vscode.CompletionItemKind.Class,
  property: vscode.CompletionItemKind.Property,
  attachedProperty: vscode.CompletionItemKind.Property,
  event: vscode.CompletionItemKind.Event,
  value: vscode.CompletionItemKind.EnumMember,
  markupExtension: vscode.CompletionItemKind.Function,
  namespace: vscode.CompletionItemKind.Module,
  directive: vscode.CompletionItemKind.Keyword,
  resource: vscode.CompletionItemKind.Constant,
  name: vscode.CompletionItemKind.Variable,
  closingTag: vscode.CompletionItemKind.Class,
  owner: vscode.CompletionItemKind.Class,
  method: vscode.CompletionItemKind.Method,
};

class AxamlCompletionItem extends vscode.CompletionItem {
  constructor(readonly source: XamlCompletionItem, readonly file: string) {
    super(source.label, KINDS[source.kind] ?? vscode.CompletionItemKind.Text);
  }
}

/** Characters after which VS Code asks for completions without Ctrl+Space. */
export const TRIGGERS = ["<", " ", ":", ".", "{", "\"", "'", "=", ",", "/"];

export function registerXamlLanguage(client: HubClient, log: (line: string) => void): vscode.Disposable {
  let lastMessage: string | undefined;

  const completion: vscode.CompletionItemProvider<AxamlCompletionItem> = {
    async provideCompletionItems(doc, pos) {
      const offset = doc.offsetAt(pos);
      const r = await client.xamlCompletion(doc.fileName, doc.getText(), offset);
      if (!r) return undefined;
      if (r.message && r.message !== lastMessage) { lastMessage = r.message; log(`IntelliSense: ${r.message}`); }
      let start = doc.positionAt(Math.min(r.from, offset));
      if (start.line !== pos.line) start = new vscode.Position(pos.line, 0);   // VS Code needs a single-line range
      const range = new vscode.Range(start, pos);
      const items = r.items.map((i) => {
        const item = new AxamlCompletionItem(i, doc.fileName);
        item.range = range;
        item.detail = i.detail;
        const text = i.insertText ?? i.label;
        item.insertText = i.snippet ? new vscode.SnippetString(text) : text;
        if (i.sortText) item.sortText = i.sortText + i.label;
        if (i.deprecated) item.tags = [vscode.CompletionItemTag.Deprecated];
        if (i.retrigger) item.command = { command: "editor.action.triggerSuggest", title: "Suggest" };
        if (i.additionalEdit) {
          const e = i.additionalEdit;
          item.additionalTextEdits = [vscode.TextEdit.replace(new vscode.Range(doc.positionAt(e.offset), doc.positionAt(e.offset + e.length)), e.text)];
        }
        return item;
      });
      return new vscode.CompletionList(items, r.incomplete === true);
    },
    async resolveCompletionItem(item) {
      if (item.source.docId) {
        const md = await client.xamlDocumentation(item.file, item.source.docId);
        if (md) item.documentation = new vscode.MarkdownString(md);
      }
      return item;
    },
  };

  const hover: vscode.HoverProvider = {
    async provideHover(doc, pos) {
      const r = await client.xamlHover(doc.fileName, doc.getText(), doc.offsetAt(pos));
      if (!r?.markdown) return undefined;
      return new vscode.Hover(new vscode.MarkdownString(r.markdown), new vscode.Range(doc.positionAt(r.from), doc.positionAt(r.to)));
    },
  };

  const isAxaml = (d: vscode.TextDocument) => d.uri.scheme === "file" && /\.axaml$/i.test(d.fileName);
  const prepareOpen = () => vscode.workspace.textDocuments.filter(isAxaml).forEach((d) => client.xamlPrepare(d.fileName));
  client.on("connected", prepareOpen);

  return vscode.Disposable.from(
    vscode.languages.registerCompletionItemProvider(AXAML_SELECTOR, completion, ...TRIGGERS),
    vscode.languages.registerHoverProvider(AXAML_SELECTOR, hover),
    vscode.workspace.onDidOpenTextDocument((d) => { if (isAxaml(d)) client.xamlPrepare(d.fileName); }),
    { dispose: () => client.off("connected", prepareOpen) },
  );
}
