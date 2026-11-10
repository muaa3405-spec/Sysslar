#!/usr/bin/env bash
# Starts a built Tasky.app against throw-away sample data, lets it open every page, and fails
# when it crashes, logs an error, or never finishes. Used by CI; you can run it locally too:
#
#   ./scripts/smoke-test.sh dist/Sysslar.app/Contents/MacOS/Tasky [screenshot.png]
#
# Your real data is never touched: TASKY_DATA_ROOT points Tasky at a temporary folder.
set -uo pipefail

EXE="$1"
SCREENSHOT="${2:-}"
TIMEOUT_SECONDS=90

ROOT="$(mktemp -d)/tasky-smoke"
export TASKY_DATA_ROOT="$ROOT"
export TASKY_SMOKE_FULL=1
export TASKY_SMOKE_REMINDER=1   # also shows a sample reminder, which should sit at the bottom right
MARKER="$ROOT/Local/Tasky/smoke-test-passed"
LOGS="$ROOT/Local/Tasky/logs"
ERRORS='Fatal error|could not start|Unhandled|Unobserved'   # recovered errors count too: they are bugs

"$EXE" &
PID=$!

for _ in $(seq 1 $((TIMEOUT_SECONDS * 2))); do
  [ -f "$MARKER" ] && break
  kill -0 "$PID" 2>/dev/null || break
  sleep 0.5
done

[ -n "$SCREENSHOT" ] && { screencapture -x "$SCREENSHOT" || true; }
if [ -d "$LOGS" ]; then echo "── Tasky log ──"; cat "$LOGS"/*.log; fi

status=0
if ! kill -0 "$PID" 2>/dev/null; then
  echo "Tasky exited during the smoke test"; status=1
elif [ ! -f "$MARKER" ]; then
  echo "Tasky did not open every page within $TIMEOUT_SECONDS seconds"; status=1
elif [ -d "$LOGS" ] && grep -Eq "$ERRORS" "$LOGS"/*.log; then
  echo "Tasky logged an error during the smoke test"; status=1
else
  echo "Smoke test passed: Tasky opened every page with sample data."
fi

kill "$PID" 2>/dev/null || true
rm -rf "$ROOT"
exit $status
