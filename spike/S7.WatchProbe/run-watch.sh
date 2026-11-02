#!/bin/bash
# Spike S7 driver: run the app under `dotnet watch`, apply scripted edits, print the log.
# Usage: ./run-watch.sh <stamps:true|false> <log>
set -u
STAMPS=$1; LOG=$2; cd "$(dirname "$0")/App"
cp CardView.axaml /tmp/s7.CardView.axaml.bak; cp CardView.axaml.cs /tmp/s7.CardView.cs.bak; cp Probe.cs /tmp/s7.Probe.cs.bak
export DOTNET_WATCH_SUPPRESS_EMOJIS=1 DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true
dotnet watch run --non-interactive --property:SpyglassHotReloadStamps=$STAMPS < /dev/null > "$LOG" 2>&1 &
W=$!
wait_for() { for i in $(seq 1 120); do grep -q "$1" "$LOG" && return 0; sleep 1; done; echo "TIMEOUT waiting for $1" >> "$LOG"; }
mark() { echo "===== $1 =====" >> "$LOG"; }
wait_for "started;"; sleep 2
mark "E1: Probe.cs code-v1 -> code-v2";            sed -i '' 's/code-v1/code-v2/' Probe.cs; sleep 8
mark "E2: CardView.axaml Text v1 -> v2";            sed -i '' 's/Text="v1"/Text="v2"/' CardView.axaml; sleep 10
mark "E3: CardView.axaml add x:Name=Sub (R1)";      sed -i '' 's#<TextBlock x:Name="Title" Text="v2" />#<TextBlock x:Name="Title" Text="v3" /><TextBlock x:Name="Sub" Text="sub" />#' CardView.axaml
  [ "${OWNINIT:-0}" = 1 ] && python3 -c "
p='CardView.axaml.cs'; s=open(p).read()
s=s.replace('internal TextBlock? Title;','internal TextBlock? Title;\n    internal TextBlock? Sub;')
s=s.replace('Title = this.FindNameScope()?.Find<TextBlock>(\"Title\");','Title = this.FindNameScope()?.Find<TextBlock>(\"Title\");\n        Sub = this.FindNameScope()?.Find<TextBlock>(\"Sub\");')
open(p,'w').write(s)"
  sleep 15
mark "E4: Probe.cs code-v2 -> code-v3 (after E3)";  sed -i '' 's/code-v2/code-v3/' Probe.cs; sleep 10
mark "END"
kill $W; sleep 1; pkill -f "S7.App" ; pkill -f "dotnet-watch" 
cp /tmp/s7.CardView.axaml.bak CardView.axaml; cp /tmp/s7.CardView.cs.bak CardView.axaml.cs; cp /tmp/s7.Probe.cs.bak Probe.cs
