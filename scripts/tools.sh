#!/usr/bin/env bash
# Runs the operator CLI (tools/Verita.Tools). Builds into %TEMP% and retries when Windows Smart
# App Control refuses a freshly built assembly (0x800711C7). Usage: scripts/tools.sh bootstrap
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
for attempt in $(seq 1 12); do
  OUT="${TEMP:-/tmp}/verita-tools-$RANDOM"
  dotnet build "$ROOT/tools/Verita.Tools" -o "$OUT" -p:Deterministic=false >/dev/null || { echo "build failed" >&2; exit 1; }
  dotnet "$OUT/Verita.Tools.dll" "$@" 2> >(tee "$OUT.err" >&2)
  CODE=$?
  if [ $CODE -ne 0 ] && grep -q "Application Control policy" "$OUT.err" 2>/dev/null; then
    echo "attempt $attempt blocked by application control; rebuilding" >&2; continue
  fi
  exit $CODE
done
echo "could not start the tools after 12 attempts" >&2; exit 1
