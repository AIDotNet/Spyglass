// Pure zoom/theme logic of the preview.
import assert from "node:assert/strict";
import { test } from "node:test";
import { applyTheme, dpiFor, FIT, mapBack, parseTheme, parseZoom, viewportFor, zoomIn, zoomOut, zoomScale, zoomToString } from "../src/preview/zoom";

test("zoom: parse and format", () => {
  assert.deepEqual(parseZoom("fit"), FIT);
  assert.deepEqual(parseZoom(undefined), FIT);
  assert.deepEqual(parseZoom("150%"), { fit: false, percent: 150 });
  assert.deepEqual(parseZoom("75"), { fit: false, percent: 75 });
  assert.deepEqual(parseZoom("5%"), FIT);
  assert.deepEqual(parseZoom("banana"), FIT);
  assert.equal(zoomToString(FIT), "fit");
  assert.equal(zoomToString({ fit: false, percent: 125 }), "125%");
});

test("zoom: Fit shrinks but never enlarges; fixed zoom is the percentage", () => {
  assert.equal(zoomScale(FIT, 800, 600, 400, 600), 0.5);
  assert.equal(zoomScale(FIT, 200, 100, 800, 600), 1);
  assert.equal(zoomScale(FIT, 10000, 10000, 100, 100), 0.1);
  assert.equal(zoomScale(FIT, 0, 0, 100, 100), 1);
  assert.equal(zoomScale({ fit: false, percent: 200 }, 100, 100, 50, 50), 2);
});

test("zoom: steps go from the current look and clamp at 25–400%", () => {
  assert.equal(zoomIn(1).percent, 125);
  assert.equal(zoomIn(0.6).percent, 75);   // from a Fit look of 60%
  assert.equal(zoomIn(4).percent, 400);
  assert.equal(zoomOut(1).percent, 75);
  assert.equal(zoomOut(0.3).percent, 25);
  assert.equal(zoomOut(0.25).percent, 25);
});

test("zoom: the host viewport is the pane undone by the zoom; DPI is clamped", () => {
  assert.deepEqual(viewportFor(FIT, 800, 600), { width: 800, height: 600 });
  assert.deepEqual(viewportFor({ fit: false, percent: 200 }, 800, 600), { width: 400, height: 300 });
  assert.deepEqual(viewportFor({ fit: false, percent: 50 }, 801, 600), { width: 1602, height: 1200 });
  assert.deepEqual(viewportFor(FIT, 0, 0), { width: 1, height: 1 });
  assert.equal(dpiFor(2), 192);
  assert.equal(dpiFor(0), 96);
  assert.equal(dpiFor(10), 384);
});

const win = `<?xml version="1.0"?>
<!-- c -->
<Window xmlns="https://github.com/avaloniaui" Title="T">
  <Button/>
</Window>`;

test("theme: injects RequestedThemeVariant on a Window root and maps error columns back", () => {
  assert.equal(parseTheme("dark"), "dark");
  assert.equal(parseTheme("x"), "default");
  const r = applyTheme(win, "dark");
  const insert = ' RequestedThemeVariant="Dark" Background="{DynamicResource SystemRegionBrush}"';
  assert.ok(r.xaml.includes(`<Window${insert} xmlns=`));
  assert.deepEqual(r.edit, { line: 3, column: 8, length: insert.length });
  // before the insertion: unchanged; inside: clamped to the insertion; after: shifted back; other line: unchanged.
  assert.deepEqual(mapBack(r.edit, 3, 3), { line: 3, column: 3 });
  assert.deepEqual(mapBack(r.edit, 3, 12), { line: 3, column: 8 });
  assert.deepEqual(mapBack(r.edit, 3, r.edit!.column + r.edit!.length + 5), { line: 3, column: 13 });
  assert.deepEqual(mapBack(r.edit, 4, 9), { line: 4, column: 9 });
  assert.deepEqual(mapBack(undefined, 4, 9), { line: 4, column: 9 });
});

test("theme: an own Background (attribute or property element) is kept, not duplicated", () => {
  assert.equal(applyTheme(`<Window xmlns="x" Background="Red"/>`, "dark").xaml, `<Window RequestedThemeVariant="Dark" xmlns="x" Background="Red"/>`);
  const el = `<Window xmlns="x"><Window.Background><SolidColorBrush/></Window.Background></Window>`;
  assert.ok(!applyTheme(el, "light").xaml.includes("SystemRegionBrush"));
  assert.ok(applyTheme(el, "light").xaml.startsWith('<Window RequestedThemeVariant="Light" xmlns'));
});

test("theme: Light, prefixed Window, and the cases that must stay untouched", () => {
  assert.ok(applyTheme(`<w:Window xmlns:w="x"/>`, "light").xaml.startsWith('<w:Window RequestedThemeVariant="Light" Background='));
  assert.equal(applyTheme(win, "default").xaml, win);
  assert.equal(applyTheme(win, "default").edit, undefined);
  const uc = `<UserControl xmlns="x"><Button/></UserControl>`;
  assert.deepEqual(applyTheme(uc, "dark"), { xaml: uc });   // a UserControl root rejects the property
  const already = `<Window xmlns="x" RequestedThemeVariant="Light"/>`;
  assert.deepEqual(applyTheme(already, "dark"), { xaml: already });
  assert.deepEqual(applyTheme("not xaml", "dark"), { xaml: "not xaml" });
  assert.deepEqual(applyTheme("<Window", "dark"), { xaml: "<Window" });
});
