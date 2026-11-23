#!/usr/bin/env bash
# The e2e APK on an emulator: an error, then a native crash sent as an UncleanExit on the next
# launch (until the Android SDK's NDK support reads tombstones). The emulator reaches the host's
# receiver at 10.0.2.2. Usage: run-android.sh <apk> <out dir>
set -uo pipefail
APK="$1"; OUT="$2"; PKG=io.zharp.e2e.shopquest
HERE="$(cd "$(dirname "$0")" && pwd)"
adb install -r "$APK"
ACTIVITY=$(adb shell cmd package resolve-activity --brief "$PKG" | tail -1 | tr -d '\r')
FAILED=0

run() { # out dir, modes…
  local out=$1; shift; mkdir -p "$out"
  python3 "$HERE/receiver.py" "$out" 4318 > "$out/receiver.log" 2>&1 & local r=$!; sleep 1
  for m in "$@"; do
    adb shell am start -W -n "$ACTIVITY" --es mode "$m"
    for i in $(seq 1 60); do grep -q "^$m$" "$out/done.txt" 2>/dev/null && break; [ "$m" = crash ] && [ $i -gt 8 ] && break; sleep 2; done
    adb shell am force-stop "$PKG"
  done
  kill $r; touch "$out/received.jsonl" "$out/requests.jsonl"
}

adb shell pm clear "$PKG"
run "$OUT/error" error
python3 "$HERE/check.py" error "$OUT/error/received.jsonl" "$OUT/error/requests.jsonl" || FAILED=1
adb shell pm clear "$PKG"
run "$OUT/crash" crash after
python3 "$HERE/check.py" crash "$OUT/crash/received.jsonl" || FAILED=1
adb logcat -d > "$OUT/logcat.txt" || true
exit $FAILED
