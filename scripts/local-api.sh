#!/usr/bin/env bash
# Rebuild the API image from the current tree and recreate the local container.
# Does not start or touch SQL. Expects SQL already listening on localhost:1433.
set -euo pipefail
export PATH="/usr/local/bin:/opt/homebrew/bin:${HOME}/.docker/bin:${PATH}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is not installed or not on PATH. Start Docker Desktop and retry." >&2
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. Start Docker Desktop and retry." >&2
  exit 1
fi

echo "Rebuilding API from ${ROOT}..."
docker compose build api
docker compose up -d --force-recreate --no-deps api

echo
echo "Waiting for http://localhost:5230/health ..."
ok=0
for i in $(seq 1 40); do
  if curl -fsS http://localhost:5230/health >/dev/null 2>&1; then
    ok=1
    break
  fi
  sleep 1
done
if [[ "$ok" -ne 1 ]]; then
  echo "API container did not become healthy. Last logs:" >&2
  docker compose logs --tail=80 api >&2
  exit 1
fi

echo "API  http://localhost:5230"
echo "Swagger  http://localhost:5230/swagger"
open "http://localhost:5230/swagger" >/dev/null 2>&1 || true
