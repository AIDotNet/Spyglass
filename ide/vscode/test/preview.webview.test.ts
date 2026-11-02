import test from "node:test";
import assert from "node:assert/strict";
import * as vm from "node:vm";
import { previewHtml } from "../src/preview/webview";

test("webview: the page script is syntactically valid (a bad escape in the template literal silently kills the whole panel)", () => {
  const html = previewHtml("n0nce", "vscode-resource:");
  const m = /<script nonce="n0nce">([\s\S]*)<\/script>/.exec(html);
  assert.ok(m, "script block present");
  assert.doesNotThrow(() => new vm.Script(m![1]), "page script compiles");
});
