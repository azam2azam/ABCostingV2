#!/usr/bin/env bash
# Stand up the Fly.io demo stack. Order: create apps → UI → API → SQL restore
# from deploy/fly/sql/Verita.bak. Existing API / UI apps are only redeployed.
#
#   scripts/fly-pilot.sh
#
# Prerequisites: flyctl (`curl -L https://fly.io/install.sh | sh`), `fly auth login`, python3.
# Closest Fly region to Saudi Arabia is fra (no UAE region).
# SQL stays private (.internal). Secrets go to .env.fly-pilot (gitignored).
# Demo login: cfo@verita.health / Demo!Pass123
set -euo pipefail

export PATH="${HOME}/.fly/bin:${PATH}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$ROOT/.env.fly-pilot"
BAK_FILE="$ROOT/deploy/fly/sql/Verita.bak"
RESTORE_SQL="$ROOT/deploy/fly/sql/restore-verita.sql"
SQL_APP="${VERITA_FLY_SQL:-verita-abc-sql}"
API_APP="${VERITA_FLY_API:-verita-abc-api}"
WEB_APP="${VERITA_FLY_WEB:-verita-abc-web}"
REGION="${VERITA_FLY_REGION:-fra}"
ORG="${VERITA_FLY_ORG:-personal}"

if ! command -v fly >/dev/null 2>&1 && ! command -v flyctl >/dev/null 2>&1; then
  echo "Install flyctl: curl -L https://fly.io/install.sh | sh" >&2
  exit 1
fi
FLY="$(command -v fly || command -v flyctl)"
"$FLY" auth whoami >/dev/null

if [[ ! -f "$BAK_FILE" ]]; then
  echo "Missing SQL backup: ${BAK_FILE}" >&2
  echo "That file is the demo database (costed runs). Commit/copy it before running this script." >&2
  exit 1
fi
if [[ ! -f "$RESTORE_SQL" ]]; then
  echo "Missing restore script: ${RESTORE_SQL}" >&2
  exit 1
fi

q() { printf "%s" "$1" | sed "s/'/'\\\\''/g"; }

if [[ -f "$ENV_FILE" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "$ENV_FILE"
  set +a
fi

SQL_PASSWORD="${VERITA_SQL_PASSWORD:-${SQL_PASSWORD:-}}"
if [[ -z "$SQL_PASSWORD" && -n "${ConnectionStrings__Verita:-}" ]]; then
  SQL_PASSWORD="$(python3 -c "import os,re; m=re.search(r'Password=([^;]+)', os.environ.get('ConnectionStrings__Verita','')); print(m.group(1) if m else '')")"
fi
if [[ -z "$SQL_PASSWORD" ]]; then
  SQL_PASSWORD="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-20)!aA1"
fi
JWT_KEY="${VERITA_JWT_KEY:-${Jwt__Key:-$(openssl rand -base64 48)}}"

app_exists() {
  "$FLY" status -a "$1" >/dev/null 2>&1
}

ensure_app() {
  local app=$1
  if app_exists "$app"; then
    echo "  skip: app $app already exists"
    return 0
  fi
  echo "  creating app $app"
  if "$FLY" apps create "$app" --org "$ORG"; then
    return 0
  fi
  if app_exists "$app"; then
    echo "  skip: app $app already exists"
    return 0
  fi
  echo "  failed to create app $app" >&2
  return 1
}

json_ids() {
  python3 -c '
import json, sys
raw = sys.stdin.read().strip()
if not raw:
    raise SystemExit(0)
data = json.loads(raw)
rows = data if isinstance(data, list) else []
key = sys.argv[1]
want_name = sys.argv[2] if len(sys.argv) > 2 else ""
for row in rows:
    if not isinstance(row, dict):
        continue
    if want_name:
        name = str(row.get("name") or "")
        state = str(row.get("state") or "").lower()
        if name != want_name or state in ("destroyed", "dead"):
            continue
    ident = row.get(key) or row.get("id")
    if ident:
        print(ident)
' "$@"
}

reset_sql_volume() {
  echo "  resetting SQL volume (empty disk; backup restore comes after SQL starts)..."
  local mid vol leftover
  if app_exists "$SQL_APP"; then
    while read -r mid; do
      [[ -z "$mid" ]] && continue
      echo "  destroying SQL machine $mid"
      "$FLY" machine destroy "$mid" -a "$SQL_APP" --force || true
    done < <("$FLY" machines list -a "$SQL_APP" --json 2>/dev/null | json_ids id)
    for i in $(seq 1 12); do
      while read -r vol; do
        [[ -z "$vol" ]] && continue
        echo "  destroying SQL volume $vol"
        "$FLY" volumes destroy "$vol" -a "$SQL_APP" -y || true
      done < <("$FLY" volumes list -a "$SQL_APP" --json 2>/dev/null | json_ids id mssql_data)
      leftover="$("$FLY" volumes list -a "$SQL_APP" --json 2>/dev/null | json_ids id mssql_data || true)"
      [[ -z "$leftover" ]] && break
      sleep 2
    done
    leftover="$("$FLY" volumes list -a "$SQL_APP" --json 2>/dev/null | json_ids id mssql_data || true)"
    if [[ -n "$leftover" ]]; then
      echo "  could not remove old SQL volume(s): $leftover" >&2
      return 1
    fi
  fi
  echo "  creating empty 10GB SQL volume..."
  "$FLY" volumes create mssql_data --snapshot-retention 5 -a "$SQL_APP" -r "$REGION" -s 10 -y
}

wait_sql() {
  echo "  waiting for SQL Server to accept logins..."
  local i
  for i in $(seq 1 36); do
    if "$FLY" ssh console -a "$SQL_APP" -C 'bash -lc "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -Q \"SELECT 1\" -b -h-1 -W"' >/dev/null 2>&1; then
      echo "  SQL is up"
      return 0
    fi
    echo "  SQL not ready yet (attempt ${i}/36)"
    sleep 5
  done
  echo "SQL did not accept connections in time." >&2
  "$FLY" ssh console -a "$SQL_APP" -C 'bash -lc "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -Q \"SELECT 1\" -b"' || true
  return 1
}

restore_sql_from_bak() {
  echo "  uploading ${BAK_FILE}..."
  "$FLY" ssh console -a "$SQL_APP" -C "sh -c 'mkdir -p /var/opt/mssql/backup && chown -R mssql:mssql /var/opt/mssql/backup || true'"
  "$FLY" ssh sftp put "$BAK_FILE" /var/opt/mssql/backup/Verita.bak -a "$SQL_APP"
  "$FLY" ssh sftp put "$RESTORE_SQL" /var/opt/mssql/backup/restore-verita.sql -a "$SQL_APP"
  echo "  restoring Verita database..."
  "$FLY" ssh console -a "$SQL_APP" -C 'bash -lc "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -i /var/opt/mssql/backup/restore-verita.sql -b"'
}

release_public_sql_ips() {
  local ips
  ips="$("$FLY" ips list -a "$SQL_APP" --json 2>/dev/null | python3 -c '
import json, sys
raw = sys.stdin.read().strip()
if not raw:
    raise SystemExit(0)
data = json.loads(raw)
rows = data if isinstance(data, list) else data.get("ips") or data.get("Addresses") or []
for row in rows:
    kind = str(row.get("type") or row.get("Type") or "").lower()
    ip = row.get("address") or row.get("ip") or row.get("IP")
    if ip and "public" in kind:
        print(ip)
' 2>/dev/null || true)"
  if [[ -n "$ips" ]]; then
    echo "  keeping SQL private (releasing public IPs)"
    # shellcheck disable=SC2086
    "$FLY" ips release $ips -a "$SQL_APP" >/dev/null || true
  fi
}

api_existed=0
web_existed=0
app_exists "$API_APP" && api_existed=1
app_exists "$WEB_APP" && web_existed=1

echo "Creating Fly apps in ${REGION} (skip if already present)..."
ensure_app "$SQL_APP"
ensure_app "$API_APP"
ensure_app "$WEB_APP"

CS="Server=${SQL_APP}.internal,1433;Initial Catalog=Verita;User Id=sa;Password=${SQL_PASSWORD};TrustServerCertificate=True;Encrypt=False"

echo "Setting secrets..."
"$FLY" secrets set -a "$SQL_APP" "MSSQL_SA_PASSWORD=${SQL_PASSWORD}" --stage
"$FLY" secrets set -a "$API_APP" \
  "ConnectionStrings__Verita=${CS}" \
  "Jwt__Key=${JWT_KEY}" \
  "Jwt__Issuer=verita" \
  "Cors__Origins__0=https://${WEB_APP}.fly.dev" \
  --stage

if [[ "$web_existed" -eq 1 ]]; then
  echo "UI already exists — redeploying..."
else
  echo "Deploying UI..."
fi
"$FLY" deploy "$ROOT" --config "$ROOT/deploy/fly/web/fly.toml" --dockerfile "$ROOT/Dockerfile.web" --remote-only -a "$WEB_APP" --ha=false

if [[ "$api_existed" -eq 1 ]]; then
  echo "API already exists — redeploying..."
else
  echo "Deploying API..."
fi
"$FLY" deploy "$ROOT" --config "$ROOT/deploy/fly/api/fly.toml" --dockerfile "$ROOT/Dockerfile.api" --remote-only -a "$API_APP" --ha=false

echo "SQL volume (always replaced, then restored from git backup)..."
reset_sql_volume
echo "Deploying SQL..."
"$FLY" deploy "$ROOT" --config "$ROOT/deploy/fly/sql/fly.toml" --remote-only -a "$SQL_APP" --ha=false
release_public_sql_ips
wait_sql
restore_sql_from_bak

cat > "$ENV_FILE" <<EOF
FLY_SQL_APP='$(q "$SQL_APP")'
FLY_API_APP='$(q "$API_APP")'
FLY_WEB_APP='$(q "$WEB_APP")'
FLY_REGION='$(q "$REGION")'
SQL_PASSWORD='$(q "$SQL_PASSWORD")'
ConnectionStrings__Verita='$(q "$CS")'
Jwt__Key='$(q "$JWT_KEY")'
EOF

echo
echo "UI    https://${WEB_APP}.fly.dev"
echo "API   https://${API_APP}.fly.dev/health"
echo "login cfo@verita.health / Demo!Pass123"
echo "SQL restored from ${BAK_FILE} (no migrate/import/cost)."
echo "Secrets written to ${ENV_FILE} (gitignored)."
