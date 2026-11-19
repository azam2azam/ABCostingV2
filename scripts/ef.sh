#!/usr/bin/env bash
# Runs dotnet-ef against a published copy of the tools. On machines with Windows Smart App
# Control, freshly built DLLs are sometimes blocked from loading (0x800711C7) depending on the
# file's hash; a fresh non-deterministic publish usually loads within a few attempts.
# There are no EF migrations: the schema is deploy/iis/sql/01-schema.sql. The one use left is to print the DDL
# the EF model implies, as a reference when writing a schema change by hand:
#   scripts/ef.sh dbcontext script -o /tmp/model.sql
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EF=$(find ~/.dotnet/tools/.store/dotnet-ef -name ef.dll -path "*netcoreapp2.0*" | head -1)
for attempt in $(seq 1 12); do
  OUT="${TEMP:-/tmp}/verita-ef-$RANDOM"
  dotnet build "$ROOT/tools/Verita.Tools" -o "$OUT" -p:Deterministic=false >/dev/null
  LOG=$(cd "$ROOT/src/Verita.Infrastructure" && dotnet exec --depsfile "$OUT/Verita.Tools.deps.json" \
    --runtimeconfig "$OUT/Verita.Tools.runtimeconfig.json" "$EF" "$@" \
    --assembly "$OUT/Verita.Infrastructure.dll" --startup-assembly "$OUT/Verita.Tools.dll" \
    --project-dir . --root-namespace Verita.Infrastructure 2>&1)
  if echo "$LOG" | grep -qE "Could not load assembly|Application Control policy"; then echo "attempt $attempt blocked by application control; retrying" >&2; continue; fi
  echo "$LOG"; exit 0
done
echo "dotnet-ef could not load the assemblies after 12 attempts" >&2; exit 1
