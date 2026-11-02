// HTML for the preview webview: toolbar (zoom, theme, status), a canvas that shows frames and forwards pointer/keyboard
// input, and a clickable error bar (design/preview/README.md §4). Zoom/scale decisions are made by the extension
// (zoom.ts); the page only draws at the scale it is told and divides input positions by it.
export function previewHtml(nonce: string, cspSource: string): string {
  return `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
  :root { --tb: 30px; --bar: 0px; }
  html, body { margin: 0; height: 100%; overflow: hidden; background: var(--vscode-editor-background); color: var(--vscode-foreground); font: var(--vscode-font-size) var(--vscode-font-family); }
  #tb { white-space: nowrap; position: absolute; top: 0; left: 0; right: 0; height: var(--tb); display: flex; align-items: center; gap: 4px; padding: 0 8px; box-sizing: border-box; border-bottom: 1px solid var(--vscode-panel-border, #8884); background: var(--vscode-editorGroupHeader-tabsBackground); }
  #tb .sep { width: 1px; height: 16px; background: var(--vscode-panel-border, #8884); margin: 0 4px; }
  #tb button { background: transparent; color: var(--vscode-foreground); border: 1px solid transparent; border-radius: 3px; padding: 2px 7px; cursor: pointer; font: inherit; min-width: 24px; }
  #tb button:hover { background: var(--vscode-toolbar-hoverBackground, #8883); }
  #tb button[aria-pressed="true"] { background: var(--vscode-toolbar-activeBackground, #8886); }
  #zoomLabel { min-width: 48px; text-align: center; }
  #state { margin-left: auto; display: flex; align-items: center; gap: 6px; opacity: .9; white-space: nowrap; overflow: hidden; min-width: 0; flex: 0 1 auto; }
  #stateText { overflow: hidden; text-overflow: ellipsis; min-width: 0; }
  #theme { flex: none; }
  @media (max-width: 420px) { #theme .lbl { display: none; } }
  #dot { width: 9px; height: 9px; border-radius: 50%; background: #888; flex: none; }
  #dot.starting, #dot.updating { background: var(--vscode-charts-yellow, #cca700); animation: pulse 1s ease-in-out infinite; }
  #dot.ok { background: var(--vscode-charts-green, #388a34); }
  #dot.error { background: var(--vscode-charts-red, #e51400); }
  #dot.exited { background: #888; }
  @keyframes pulse { 50% { opacity: .35; } }
  #stage { position: absolute; top: var(--tb); left: 0; right: 0; bottom: var(--bar); overflow: hidden; }
  canvas { display: block; outline: none; background: repeating-conic-gradient(#8883 0% 25%, transparent 0% 50%) 0 0 / 16px 16px; }
  #msg { position: absolute; top: var(--tb); left: 0; right: 0; bottom: var(--bar); display: none; flex-direction: column; gap: 12px; align-items: center; justify-content: center; text-align: center; padding: 24px; background: var(--vscode-editor-background); }
  #msg.on { display: flex; }
  #bar { position: absolute; left: 0; right: 0; bottom: 0; display: none; padding: 6px 10px; background: var(--vscode-inputValidation-errorBackground); border-top: 1px solid var(--vscode-inputValidation-errorBorder); font-family: var(--vscode-editor-font-family); white-space: pre-wrap; max-height: 25%; overflow: auto; cursor: pointer; }
  #bar.on { display: block; }
  #bar:hover { text-decoration: underline; }
  #msgBtn { background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: 0; padding: 6px 14px; cursor: pointer; }
  #msgBtn:hover { background: var(--vscode-button-hoverBackground); }
</style></head>
<body>
<div id="tb">
  <button id="zOut" title="Zoom out (Ctrl+wheel)">&minus;</button>
  <button id="zoomLabel" title="Actual size (100%)">Fit</button>
  <button id="zIn" title="Zoom in (Ctrl+wheel)">+</button>
  <button id="zFit" title="Fit to pane">&#10530;</button>
  <span class="sep"></span>
  <button id="theme" title="Preview theme: click to cycle Default / Light / Dark">&#9680;<span class="lbl"> Default</span></button>
  <button id="restart" title="Restart the previewer process">&#8635;</button>
  <div id="state"><span id="dot" class="starting"></span><span id="stateText">Starting…</span></div>
</div>
<div id="stage"><canvas id="c" tabindex="0"></canvas></div>
<div id="msg"><div id="msgText"></div><button id="msgBtn" style="display:none"></button></div>
<div id="bar" title="Click to jump to the error in the editor"></div>
<script nonce="${nonce}">
const vscode = acquireVsCodeApi();
const $ = (id) => document.getElementById(id);
const stage = $('stage'), cv = $('c'), ctx = cv.getContext('2d');
const msg = $('msg'), msgText = $('msgText'), msgBtn = $('msgBtn'), bar = $('bar');
let scale = 1, logical = { w: 0, h: 0 };

const mods = (e) => { const m = []; if (e.altKey) m.push(0); if (e.ctrlKey) m.push(1); if (e.shiftKey) m.push(2); if (e.metaKey) m.push(3);
  if (e.buttons & 1) m.push(4); if (e.buttons & 2) m.push(5); if (e.buttons & 4) m.push(6); return m; };
const pos = (e) => { const r = cv.getBoundingClientRect(); return { x: (e.clientX - r.left) / scale, y: (e.clientY - r.top) / scale }; };
const post = (m) => vscode.postMessage(m);

function reportPane() {
  post({ type: 'pane', width: Math.max(1, Math.floor(stage.clientWidth)), height: Math.max(1, Math.floor(stage.clientHeight)), dpr: window.devicePixelRatio || 1 });
}
let vt; const resized = () => { clearTimeout(vt); vt = setTimeout(reportPane, 100); };
new ResizeObserver(resized).observe(stage);
window.addEventListener('resize', resized);

function layoutCanvas() {
  cv.style.width = (logical.w * scale) + 'px';
  cv.style.height = (logical.h * scale) + 'px';
}

function draw(f) {
  const px = new Uint8ClampedArray(f.width * f.height * 4);
  const src = new Uint8Array(f.data);
  for (let y = 0; y < f.height; y++) {
    let s = y * f.stride, d = y * f.width * 4;
    for (let x = 0; x < f.width; x++, s += 4, d += 4) {
      if (f.format === 2) { px[d] = src[s + 2]; px[d + 1] = src[s + 1]; px[d + 2] = src[s]; } else { px[d] = src[s]; px[d + 1] = src[s + 1]; px[d + 2] = src[s + 2]; }
      px[d + 3] = src[s + 3];
    }
  }
  if (cv.width !== f.width || cv.height !== f.height) { cv.width = f.width; cv.height = f.height; }
  logical = { w: f.width * 96 / (f.dpiX || 96), h: f.height * 96 / (f.dpiY || 96) };
  scale = f.scale || 1;
  layoutCanvas();
  ctx.putImageData(new ImageData(px, f.width, f.height), 0, 0);
}

function setMessage(text, button) {
  msg.classList.toggle('on', !!text);
  msgText.textContent = text || '';
  msgBtn.style.display = button ? '' : 'none';
  if (button) { msgBtn.textContent = button.label; msgBtn.onclick = () => post({ type: 'command', command: button.command }); }
}
function setError(text, detail) {
  bar.classList.toggle('on', !!text); bar.textContent = text || '';
  bar.title = text ? 'Click to jump to the error in the editor' + (detail && detail !== text ? '\\n\\n' + detail : '') : '';
  document.documentElement.style.setProperty('--bar', text ? bar.offsetHeight + 'px' : '0px');
  resized();
}

window.addEventListener('message', (ev) => {
  const m = ev.data;
  if (m.type === 'frame') draw(m);
  else if (m.type === 'scale') { scale = m.scale; layoutCanvas(); }
  else if (m.type === 'message') setMessage(m.text, m.button);
  else if (m.type === 'error') setError(m.text, m.detail);
  else if (m.type === 'status') { $('dot').className = m.state; $('stateText').textContent = m.text; $('state').title = m.text; }
  else if (m.type === 'toolbar') { $('zoomLabel').textContent = m.zoom; $('theme').innerHTML = '&#9680;<span class="lbl"> ' + m.theme + '</span>'; $('theme').title = 'Preview theme: ' + m.theme + ' (click to cycle Default / Light / Dark)'; $('zFit').setAttribute('aria-pressed', String(m.fit)); }
});

$('zIn').onclick = () => post({ type: 'zoom', op: 'in' });
$('zOut').onclick = () => post({ type: 'zoom', op: 'out' });
$('zFit').onclick = () => post({ type: 'zoom', op: 'fit' });
$('zoomLabel').onclick = () => post({ type: 'zoom', op: 'actual' });
$('theme').onclick = () => post({ type: 'theme' });
$('restart').onclick = () => post({ type: 'command', command: 'spyglass.preview.restart' });
bar.onclick = () => post({ type: 'reveal' });

let moveTimer = 0, pendingMove = null;
cv.addEventListener('pointermove', (e) => { pendingMove = { type: 'pointer', kind: 'move', ...pos(e), button: 0, mods: mods(e) };
  if (!moveTimer) moveTimer = setTimeout(() => { moveTimer = 0; if (pendingMove) post(pendingMove); pendingMove = null; }, 16); });
cv.addEventListener('pointerdown', (e) => { cv.focus(); cv.setPointerCapture(e.pointerId);
  post({ type: 'pointer', kind: 'down', ...pos(e), button: e.button === 0 ? 1 : e.button === 2 ? 2 : 3, mods: mods(e) }); });
cv.addEventListener('pointerup', (e) => post({ type: 'pointer', kind: 'up', ...pos(e), button: e.button === 0 ? 1 : e.button === 2 ? 2 : 3, mods: mods(e) }));
cv.addEventListener('contextmenu', (e) => e.preventDefault());
window.addEventListener('wheel', (e) => {
  if (e.ctrlKey || e.metaKey) { e.preventDefault(); post({ type: 'zoom', op: e.deltaY < 0 ? 'in' : 'out' }); return; }
  if (e.target !== cv) return;
  e.preventDefault(); const p = pos(e);
  post({ type: 'scroll', ...p, dx: -e.deltaX / 50, dy: -e.deltaY / 50, mods: mods(e) });
}, { passive: false });
const keyMsg = (e, down) => ({ type: 'key', down, code: e.code, key: e.key, mods: mods(e) });
cv.addEventListener('keydown', (e) => { post(keyMsg(e, true));
  if (e.key.length === 1 && !e.ctrlKey && !e.metaKey) post({ type: 'text', text: e.key, mods: mods(e) });
  if (e.key !== 'Escape') e.preventDefault(); });
cv.addEventListener('keyup', (e) => { post(keyMsg(e, false)); e.preventDefault(); });

post({ type: 'ready' });
reportPane();
</script></body></html>`;
}
