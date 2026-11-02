// Zoom and theme logic of the preview, pure functions (same rules as the Visual Studio implementation,
// design/preview/README.md §4). All sizes are device-independent pixels.

export interface PreviewZoom { fit: boolean; percent: number }

export const FIT: PreviewZoom = { fit: true, percent: 100 };
export const ZOOM_STEPS = [25, 50, 75, 100, 125, 150, 200, 300, 400];

/** "fit", "150" or "150%" → zoom; anything else (or out of 10–800) → Fit. */
export function parseZoom(text: string | undefined | null): PreviewZoom {
  const t = (text ?? "").trim().replace(/%$/, "");
  if (!t || t.toLowerCase() === "fit") return FIT;
  const p = Number(t);
  return Number.isFinite(p) && p >= 10 && p <= 800 ? { fit: false, percent: p } : FIT;
}

export const zoomToString = (z: PreviewZoom): string => (z.fit ? "fit" : `${Math.round(z.percent * 100) / 100}%`);

/** Scale factor for a picture of logical size frame in a pane: Fit shrinks (0.1–1, never enlarges), otherwise the percentage. */
export function zoomScale(z: PreviewZoom, frameW: number, frameH: number, paneW: number, paneH: number): number {
  if (!z.fit) return z.percent / 100;
  if (frameW <= 0 || frameH <= 0 || paneW <= 0 || paneH <= 0) return 1;
  return Math.min(1, Math.max(0.1, Math.min(paneW / frameW, paneH / frameH)));
}

/** The next larger step above the current look (also when it came from Fit); stays at the top step. */
export function zoomIn(currentScale: number): PreviewZoom {
  const pct = currentScale * 100;
  return { fit: false, percent: ZOOM_STEPS.find((s) => s > pct + 0.5) ?? ZOOM_STEPS[ZOOM_STEPS.length - 1] };
}

export function zoomOut(currentScale: number): PreviewZoom {
  const pct = currentScale * 100;
  const below = [...ZOOM_STEPS].reverse().find((s) => s < pct - 0.5);
  return { fit: false, percent: below ?? ZOOM_STEPS[0] };
}

/** Viewport (logical size) to tell the host: the pane undone by the zoom; Fit asks for the whole pane. */
export function viewportFor(z: PreviewZoom, paneW: number, paneH: number): { width: number; height: number } {
  const scale = z.fit ? 1 : z.percent / 100;
  return { width: Math.max(1, Math.floor(paneW / scale)), height: Math.max(1, Math.floor(paneH / scale)) };
}

/** Pane DPI for a devicePixelRatio, clamped to 0.5–4× of 96. */
export const dpiFor = (devicePixelRatio: number): number => 96 * Math.min(4, Math.max(0.5, devicePixelRatio > 0 ? devicePixelRatio : 1));

// ---------- theme ----------

export type PreviewTheme = "default" | "light" | "dark";
export const parseTheme = (t: string | undefined | null): PreviewTheme => (t === "light" || t === "dark" ? t : "default");

/** An attribute inserted at (line, column), 1-based, `length` characters long, in the XAML sent to the host. */
export interface XamlEdit { line: number; column: number; length: number }

/** Maps a host error position in the edited XAML back to the editor's text. */
export function mapBack(edit: XamlEdit | undefined, line?: number, column?: number): { line?: number; column?: number } {
  if (!edit || edit.length === 0 || line !== edit.line || column === undefined) return { line, column };
  return { line, column: column >= edit.column + edit.length ? column - edit.length : column < edit.column ? column : edit.column };
}

/**
 * Adds RequestedThemeVariant="Light|Dark" to a Window root (the host has no theme message). The previewer renders a
 * Window without its theme's default background, so the variant would be invisible: when the root sets no Background
 * itself, `Background="{DynamicResource SystemRegionBrush}"` (the Fluent window background) is added too. A root that
 * already sets RequestedThemeVariant, a non-Window root (RequestedThemeVariant exists only on Window; the host rejects
 * it elsewhere) and the default theme are sent unchanged.
 */
export function applyTheme(xaml: string, theme: PreviewTheme): { xaml: string; edit?: XamlEdit } {
  if (theme === "default") return { xaml };
  const nameEnd = rootNameEnd(xaml);
  if (nameEnd < 0) return { xaml };
  const nameStart = xaml.lastIndexOf("<", nameEnd - 1) + 1;
  const name = xaml.slice(nameStart, nameEnd);
  if (name.slice(name.lastIndexOf(":") + 1) !== "Window") return { xaml };
  const tagEnd = xaml.indexOf(">", nameEnd);
  if (tagEnd < 0 || xaml.slice(nameEnd, tagEnd).includes("RequestedThemeVariant")) return { xaml };
  const tag = xaml.slice(nameEnd, tagEnd);
  const hasBackground = /\sBackground\s*=/.test(tag) || /<(\w+:)?Window\.Background[\s>]/.test(xaml);
  const insert = ` RequestedThemeVariant="${theme === "light" ? "Light" : "Dark"}"` + (hasBackground ? "" : ' Background="{DynamicResource SystemRegionBrush}"');
  const before = xaml.slice(0, nameEnd);
  const line = before.split("\n").length;
  const column = nameEnd - (before.lastIndexOf("\n") + 1) + 1;
  return { xaml: xaml.slice(0, nameEnd) + insert + xaml.slice(nameEnd), edit: { line, column, length: insert.length } };
}

/** Index just after the root element's name, skipping the XML declaration, comments and DOCTYPE; -1 if none. */
function rootNameEnd(s: string): number {
  let i = 0;
  while (i < s.length) {
    const lt = s.indexOf("<", i);
    if (lt < 0 || lt + 1 >= s.length) return -1;
    if (s.startsWith("<?", lt)) { const e = s.indexOf("?>", lt); if (e < 0) return -1; i = e + 2; continue; }
    if (s.startsWith("<!--", lt)) { const e = s.indexOf("-->", lt); if (e < 0) return -1; i = e + 3; continue; }
    if (s.startsWith("<!", lt)) { const e = s.indexOf(">", lt); if (e < 0) return -1; i = e + 1; continue; }
    let j = lt + 1;
    while (j < s.length && !/\s/.test(s[j]) && s[j] !== ">" && s[j] !== "/") j++;
    return j > lt + 1 ? j : -1;
  }
  return -1;
}
