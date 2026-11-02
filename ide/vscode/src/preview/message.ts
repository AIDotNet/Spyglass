/** First non-empty line of a (possibly multi-line, stack-trace) error message, capped for the error bar and the Problems list. */
export function firstLine(text: string): string {
  const line = text.split(/\r?\n/).map((s) => s.trim()).find((s) => s.length > 0) ?? text.trim();
  return line.length > 300 ? line.slice(0, 297) + "…" : line;
}

/** Exceptions that only wrap the real cause (the previewer loads XAML through reflection, so runtime errors arrive as TargetInvocationException). */
const WRAPPERS = /^(System\.Reflection\.TargetInvocationException|System\.TypeInitializationException|System\.AggregateException)\b/;

/**
 * One-line summary of the host's `ex.ToString()`: the first exception in the chain (outer, then each ` ---> ` inner)
 * that is not a mere wrapper, so "TargetInvocationException: Exception has been thrown…" shows its actual cause.
 */
export function errorSummary(text: string): string {
  const headers = text.split(/\r?\n/).map((s) => s.trim()).filter((s, i, all) =>
    s.startsWith("--->") || (s.length > 0 && all.slice(0, i).every((p) => p.length === 0)))
    .map((s) => s.replace(/^--->\s*/, ""));
  const pick = headers.find((h) => !WRAPPERS.test(h)) ?? headers[headers.length - 1];
  return firstLine(pick ?? text);
}
