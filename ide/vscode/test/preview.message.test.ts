import test from "node:test";
import assert from "node:assert/strict";
import { errorSummary, firstLine } from "../src/preview/message";

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

const tie = "System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.\n" +
  " ---> System.ArgumentException: -5 is not a valid value for 'MinWidth'.\n   at Avalonia.Layout.Layoutable.ValidateMinimumWidth()\n" +
  "--- End of stack trace from previous location ---\n   --- End of inner exception stack trace ---\n   at System.Reflection.MethodBaseInvoker.Invoke()";
test("summary: a TargetInvocationException shows its inner cause", () => {
  assert.equal(errorSummary(tie), "System.ArgumentException: -5 is not a valid value for 'MinWidth'.");
});
test("summary: a non-wrapper outer exception is kept, wrapper-only chains fall back to the last one", () => {
  const parse = "XamlX.XamlParseException: The input string '1.0.0' was not in a correct format. Line 1, position 9.\n ---> System.FormatException: bad\n   --- End of inner exception stack trace ---";
  assert.equal(errorSummary(parse), "XamlX.XamlParseException: The input string '1.0.0' was not in a correct format. Line 1, position 9.");
  assert.equal(errorSummary("System.Reflection.TargetInvocationException: x\n   at Foo()"), "System.Reflection.TargetInvocationException: x");
  assert.equal(errorSummary("\n System.Xml.XmlException: bad\n   at Foo()"), "System.Xml.XmlException: bad");
  assert.equal(errorSummary(""), "");
});
