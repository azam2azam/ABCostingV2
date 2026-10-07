#!/usr/bin/env bash
# Builds the API into %TEMP% and starts it detached on http://localhost:5230, retrying when
# Windows Smart App Control refuses a freshly built assembly. Logs go to %TEMP%/verita-api.log.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PORT="${VERITA_API_PORT:-5230}"
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*Verita.Api.dll*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }" 2>/dev/null
for attempt in $(seq 1 12); do
  OUT="${TEMP:-/tmp}/verita-api-$RANDOM"
  dotnet build "$ROOT/src/Verita.Api" -o "$OUT" -p:Deterministic=false >/dev/null || { echo "build failed" >&2; exit 1; }
  LOG="${TEMP:-/tmp}/verita-api.log"
  WOUT=$(cygpath -w "$OUT"); WLOG=$(cygpath -w "$LOG")
  powershell -NoProfile -Command "\$env:ASPNETCORE_URLS='http://localhost:$PORT'; \$env:ASPNETCORE_ENVIRONMENT='Development'; Start-Process -FilePath dotnet -ArgumentList 'Verita.Api.dll' -WorkingDirectory '$WOUT' -RedirectStandardOutput '$WLOG' -RedirectStandardError '$WLOG.err' -WindowStyle Hidden"
  for i in $(seq 1 30); do
    sleep 1
    if curl -s "http://localhost:$PORT/health" | grep -q ok; then echo "API running on http://localhost:$PORT (from $OUT)"; exit 0; fi
    if grep -q "Application Control policy" "$LOG.err" 2>/dev/null; then break; fi
  done
  echo "attempt $attempt did not start (application control or startup error); retrying" >&2
  tail -3 "$LOG.err" >&2 2>/dev/null
done
echo "API did not start" >&2; exit 1
