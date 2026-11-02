#!/usr/bin/env python3
"""HR3 end-to-end (design/hot-reload §16): the sample app under `dotnet watch`, the hub with --no-watch (so saved XAML can
only arrive as a stamp inside a .NET Hot Reload delta). Edits and saves real files, checks the running app through the
hub's MCP endpoint, restores the files. Run after `dotnet build Spyglass.slnx`:  python3 scripts/hot_reload_watch_e2e.py"""
import json, os, shutil, signal, subprocess, sys, tempfile, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PORT = 29514
XAML = os.path.join(ROOT, "samples/Spyglass.Sample/MainWindow.axaml")
VM = os.path.join(ROOT, "samples/Spyglass.Sample/App.axaml.cs")
home = tempfile.mkdtemp(prefix="spyglass-watch-e2e-")
backups = {p: open(p, encoding="utf-8").read() for p in (XAML, VM)}
procs, failed = [], []


def mcp(tool, args):
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/mcp", method="POST",
        data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": tool, "arguments": args}}).encode(),
        headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    body = urllib.request.urlopen(req, timeout=15).read().decode()
    data = next((l[6:] for l in body.splitlines() if l.startswith("data: ")), body)
    return "\n".join(c.get("text", "") for c in json.loads(data)["result"]["content"])


def text_count(text):
    try:
        out = mcp("ui_find", {"query": f"text={text}"})
        return sum(1 for l in out.splitlines() if l.lstrip().startswith("TextBlock"))
    except Exception:
        return 0


def expect(what, seconds, cond):
    end = time.time() + seconds
    while time.time() < end:
        if cond():
            print(f"PASS {what}", flush=True); return True
        time.sleep(0.5)
    print(f"FAIL {what}", flush=True); failed.append(what); return False


def edit(path, old, new):
    s = open(path, encoding="utf-8").read()
    assert old in s, f"{old!r} not in {path}"
    open(path, "w", encoding="utf-8").write(s.replace(old, new, 1))


try:
    env = dict(os.environ, SPYGLASS_HOME=home, SPYGLASS_PORT=str(PORT), DOTNET_WATCH_SUPPRESS_EMOJIS="1")
    serve_log = open(os.path.join(home, "serve.log"), "w")
    watch_log = open(os.path.join(home, "watch.log"), "w")
    procs.append(subprocess.Popen(["dotnet", os.path.join(ROOT, "src/Spyglass.App/bin/Debug/net10.0/Spyglass.dll"),
                                   "serve", "--port", str(PORT), "--no-watch"], env=env, stdout=serve_log, stderr=serve_log))
    time.sleep(2)
    procs.append(subprocess.Popen(["dotnet", "watch", "run", "--project", os.path.join(ROOT, "samples/Spyglass.Sample"),
                                   "--non-interactive", "--property:UseAppHost=false"],
                                  env=env, stdout=watch_log, stderr=watch_log, stdin=subprocess.DEVNULL, cwd=ROOT))
    expect("app connected with hot reload", 180, lambda: "hot reload active" in mcp("hot_reload_status", {}))
    time.sleep(2)

    # 1. Saved XAML reaches the app through the stamp (the hub does not watch files).
    edit(XAML, '<TextBox x:Name="NameBox"', '<TextBlock Text="Saved under dotnet watch" />\n    <TextBox x:Name="NameBox"')
    expect("XAML save applied via the stamp channel", 30, lambda: text_count("Saved under dotnet watch") >= 1)

    # 2. A new x:Name, then a NEW window. With Avalonia's InitializeComponent the window would throw (spike S7, R1).
    edit(XAML, '<TextBlock Text="Saved under dotnet watch" />', '<TextBlock x:Name="Fresh" Text="Named under dotnet watch" />')
    expect("x:Name edit applied", 30, lambda: text_count("Named under dotnet watch") >= 1)
    print("   ", mcp("ui_interact", {"element": "Button#NewWindowButton", "action": "invoke"}).splitlines()[0])
    expect("new window built from the new XAML", 15, lambda: text_count("Named under dotnet watch") >= 2)

    # 3. A C# getter change: bindings re-read without PropertyChanged.
    edit(VM, 'public string Title => "Spyglass Sample";', 'public string Title => "Spyglass Sample, C# hot reload";')
    expect("C# change refreshed the binding", 30, lambda: text_count("C# hot reload") >= 1)

    status = mcp("hot_reload_status", {})
    print("--- hot_reload_status\n" + status)
    restarted = "Restart is needed" in open(os.path.join(home, "watch.log")).read()
    print("--- dotnet watch restarted the app: " + ("YES (results above may come from a fresh process)" if restarted else "no"))
finally:
    for p, text in backups.items():
        open(p, "w", encoding="utf-8").write(text)
    for p in reversed(procs):
        p.send_signal(signal.SIGTERM)
    time.sleep(1)
    subprocess.run(["pkill", "-f", "Spyglass.Sample.dll"])
    print(f"logs: {home}")

sys.exit(1 if failed else 0)
