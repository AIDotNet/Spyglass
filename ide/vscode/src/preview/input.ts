// Browser input → Avalonia protocol values (design/preview/README.md §4, "交互").
import { KEY, PHYSICAL_KEY } from "./keys";

/** KeyboardEvent.code → PhysicalKey value (names match except KeyA → A). */
export function physicalKeyFor(code: string): number {
  const name = /^Key[A-Z]$/.test(code) ? code.slice(3) : code;
  return PHYSICAL_KEY[name] ?? PHYSICAL_KEY.None ?? 0;
}

const NAMED: Record<string, string> = {
  Backspace: "Back", Tab: "Tab", Enter: "Return", Escape: "Escape", " ": "Space", ArrowLeft: "Left", ArrowUp: "Up",
  ArrowRight: "Right", ArrowDown: "Down", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown",
  Delete: "Delete", Insert: "Insert", CapsLock: "CapsLock", Shift: "LeftShift", Control: "LeftCtrl", Alt: "LeftAlt", Meta: "LWin",
  ";": "OemSemicolon", "=": "OemPlus", "+": "OemPlus", ",": "OemComma", "-": "OemMinus", ".": "OemPeriod", "/": "OemQuestion",
  "`": "OemTilde", "[": "OemOpenBrackets", "\\": "OemPipe", "]": "OemCloseBrackets", "'": "OemQuotes",
};

/** KeyboardEvent.key (+ code for digits) → logical Key value; 0 (Key.None) when unknown. */
export function logicalKeyFor(key: string, code: string): number {
  if (/^F([1-9]|1[0-2])$/.test(key)) return KEY[key];
  if (/^Digit[0-9]$/.test(code)) return KEY["D" + code.slice(5)];
  if (key.length === 1 && /[a-z]/i.test(key)) return KEY[key.toUpperCase()];
  const n = NAMED[key];
  return n && KEY[n] !== undefined ? KEY[n] : 0;
}
