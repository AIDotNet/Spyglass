/** First non-empty line of a (possibly multi-line, stack-trace) error message, capped for the error bar and the Problems list. */
export function firstLine(text: string): string {
  const line = text.split(/\r?\n/).map((s) => s.trim()).find((s) => s.length > 0) ?? text.trim();
  return line.length > 300 ? line.slice(0, 297) + "…" : line;
}
