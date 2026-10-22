#!/usr/bin/env bash
# Rebuild the UI image from the current tree and recreate the local container.
# Does not start or touch SQL. The UI calls http://localhost:5230.
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

echo "Rebuilding UI from ${ROOT}..."
docker compose build web
docker compose up -d --force-recreate --no-deps web

echo
echo "Waiting for http://localhost:4230 ..."
ok=0
for i in $(seq 1 40); do
  if curl -fsS -o /dev/null http://localhost:4230/ >/dev/null 2>&1; then
    ok=1
    break
  fi
  sleep 1
done
if [[ "$ok" -ne 1 ]]; then
  echo "UI container did not become healthy. Last logs:" >&2
  docker compose logs --tail=80 web >&2
  exit 1
fi

echo "UI  http://localhost:4230"
echo "login  cfo@verita.health / Demo!Pass123"
open "http://localhost:4230" >/dev/null 2>&1 || true
