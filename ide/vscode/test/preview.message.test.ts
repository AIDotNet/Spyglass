import test from "node:test";
import assert from "node:assert/strict";
import { firstLine } from "../src/preview/message";

test("message: a stack trace is reduced to its first line", () => {
  const e = "System.Xml.XmlException: The 'Window' start tag does not match.\r\n   at System.Xml.Throw()\n   at Foo.Bar()";
  assert.equal(firstLine(e), "System.Xml.XmlException: The 'Window' start tag does not match.");
});
test("message: leading blank lines are skipped, long lines are capped", () => {
  assert.equal(firstLine("\n\n  boom \nnext"), "boom");
  const long = firstLine("x".repeat(500));
  assert.equal(long.length, 298);
  assert.ok(long.endsWith("…"));
  assert.equal(firstLine(""), "");
});
