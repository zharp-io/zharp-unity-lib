#!/usr/bin/env bash
# Runs the e2e player through each scenario against the stand-in receiver and checks what it got.
# Usage: run-player.sh <player executable> <linux|windows|macos> <out dir>
# Each scenario starts from empty Zharp storage, runs the player with ZHARP_E2E_MODE, then launches
# it again ("after") so what the first run left on disk (a crash, a hang, the outbox) is sent.
set -uo pipefail
PLAYER="$1"; OS="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
case "$OS" in
  linux) DATA="$HOME/.config/unity3d/Zharp/Shop Quest" ;;
  windows) DATA="$USERPROFILE/AppData/LocalLow/Zharp/Shop Quest" ;;
  macos) DATA="$HOME/Library/Application Support/Zharp/Shop Quest" ;;
esac
GUID="$(dirname "$PLAYER")/build-guid.txt"
FAILED=0
# A Linux player of Unity 6 crashes (SIGSEGV) right after the first scene's Awake when there's no X
# display, even with -nographics and with Zharp disabled: run on a virtual one.
DISPLAY_WRAP=()
if [ "$OS" = linux ] && [ -z "${DISPLAY:-}" ] && command -v xvfb-run > /dev/null; then DISPLAY_WRAP=(xvfb-run -a); fi

launch() { # mode, log name, [extra env]
  local mode="$1" name="$2"; shift 2
  env ZHARP_E2E_MODE="$mode" "$@" "${DISPLAY_WRAP[@]}" timeout 90 "$PLAYER" -batchmode -nographics -logFile "$OUT/$name.log" || true
  # Windows: Unity's crash handler, a process of its own, may still be writing the crash folder.
  if [ "$OS" = windows ]; then
    powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$HERE/wait-crash-handler.ps1")" "$(cygpath -w "$(dirname "$PLAYER")")"
  fi
}

scenario() { # name, check, modes…
  local name="$1" check="$2"; shift 2
  rm -rf "$DATA/zharp" "$OUT/$name"; mkdir -p "$OUT/$name"
  python3 "$HERE/receiver.py" "$OUT/$name" 4318 > "$OUT/$name/receiver.log" 2>&1 &
  local rpid=$!; sleep 1
  local i=0
  for mode in "$@"; do i=$((i+1)); launch "$mode" "$name-$i"; done
  kill $rpid 2>/dev/null; wait $rpid 2>/dev/null
  touch "$OUT/$name/received.jsonl" "$OUT/$name/requests.jsonl"
  python3 "$HERE/check.py" "$check" "$OUT/$name/received.jsonl" "$OUT/$name/requests.jsonl" "$GUID" || FAILED=1
}

scenario error error error after
scenario logs-off logs-off logs after
ZHARP_LOGS=true scenario logs-on logs logs after
scenario hang hang hang after
scenario managed managed-crash managed-crash after
if [ "$OS" = windows ]; then
  # Unity's crash handler writes the crash folder; the next launch uploads its minidump.
  rm -rf "$TEMP/Zharp/Shop Quest/Crashes"
  scenario crash minidump crash after
  rm -rf "$TEMP/Zharp/Shop Quest/Crashes"
  scenario hang-crash minidump hang-crash after
else
  scenario crash crash crash after
fi
# Offline: nothing listens while the error happens; it's sent from the outbox on the next launch.
rm -rf "$DATA/zharp" "$OUT/offline"; mkdir -p "$OUT/offline"
launch error offline-1
python3 "$HERE/receiver.py" "$OUT/offline" 4318 > "$OUT/offline/receiver.log" 2>&1 & RPID=$!; sleep 1
launch after offline-2
kill $RPID; wait $RPID 2>/dev/null
touch "$OUT/offline/received.jsonl"
python3 - "$OUT/offline/received.jsonl" <<'EOF' || FAILED=1
import json, sys
errors = [r for line in open(sys.argv[1]) for rl in json.loads(line).get("body", {}).get("resourceLogs", [])
          for s in rl["scopeLogs"] for r in s["logRecords"] if any(a["key"] == "exception.type" for a in r["attributes"])]
types = sorted({a["value"]["stringValue"] for r in errors for a in r["attributes"] if a["key"] == "exception.type"})
print("offline: sent on the next launch:", types)
sys.exit(0 if "NullReferenceException" in types else 1)
EOF
exit $FAILED
