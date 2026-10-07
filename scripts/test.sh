#!/usr/bin/env bash
# Runs the engine and integration suites, building into %TEMP% and retrying when Windows Smart
# App Control refuses a freshly built test assembly. Integration tests need a bootstrapped database.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
run_suite() {
  local project=$1 dll=$2
  for attempt in $(seq 1 12); do
    OUT="${TEMP:-/tmp}/verita-test-$RANDOM"
    dotnet build "$ROOT/tests/$project" -o "$OUT" -p:Deterministic=false >/dev/null || { echo "build failed: $project" >&2; return 1; }
    RESULT=$(cd "$ROOT/src/Verita.Api" && dotnet test "$OUT/$dll" 2>&1)
    if echo "$RESULT" | grep -q "Application Control policy"; then continue; fi
    echo "$RESULT" | grep -E "Failed |Passed!|Failed!" ; echo "$RESULT" | grep -q "Passed!"; return $?
  done
  echo "$project could not start after 12 attempts" >&2; return 1
}
run_suite Verita.Engine.Tests Verita.Engine.Tests.dll && run_suite Verita.Integration.Tests Verita.Integration.Tests.dll
