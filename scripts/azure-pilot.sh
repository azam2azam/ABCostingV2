#!/usr/bin/env bash
# Creates the Azure pilot resources for VERITA (SQL + App Service + Static Web App).
# Location order: Middle East first, then Europe.
# Prerequisites: Azure CLI and `az login`.
set -euo pipefail

PREFIX="${VERITA_PREFIX:-verita-pilot}"
RG="${VERITA_RG:-rg-${PREFIX}}"
SQL_SERVER="${VERITA_SQL_SERVER:-sql${PREFIX//-/}$(date +%H%M%S)}"
SQL_DB="${VERITA_SQL_DB:-Verita}"
SQL_ADMIN="${VERITA_SQL_ADMIN:-veritaadmin}"
PLAN="${VERITA_PLAN:-plan-${PREFIX}}"
WEBAPP="${VERITA_WEBAPP:-app-${PREFIX}-api}"
SWA="${VERITA_SWA:-stapp-${PREFIX}-web}"
JWT_ISSUER="${VERITA_JWT_ISSUER:-verita}"
SWA_LOCS="${VERITA_SWA_LOCATIONS:-westeurope eastasia eastus2 westus2 centralus}"
ME_LOCS="${VERITA_ME_LOCATIONS:-uaenorth qatarcentral israelcentral uaecentral}"
EU_LOCS="${VERITA_EU_LOCATIONS:-northeurope uksouth swedencentral francecentral germanywestcentral switzerlandnorth westeurope}"

if ! command -v az >/dev/null; then
  echo "Azure CLI is not installed. On this Mac: brew install azure-cli && az login" >&2
  exit 1
fi
az account show >/dev/null || { echo "Run: az login" >&2; exit 1; }

SQL_PASSWORD="${VERITA_SQL_PASSWORD:-}"
if [ -z "$SQL_PASSWORD" ]; then
  SQL_PASSWORD="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-20)!aA1"
fi
JWT_KEY="${VERITA_JWT_KEY:-$(openssl rand -base64 48)}"

q() { printf "%s" "$1" | sed "s/'/'\\\\''/g"; }

try_create() {
  local desc=$1; shift
  if "$@" >/tmp/verita-az.out 2>/tmp/verita-az.err; then
    return 0
  fi
  echo "  failed: ${desc}" >&2
  sed -n 's/.*Message: /  /p' /tmp/verita-az.err | head -2 >&2 || true
  return 1
}

echo "Subscription: $(az account show --query name -o tsv)"

RG_LOC="${VERITA_LOCATION:-uaenorth}"
echo "Creating resource group ${RG} in ${RG_LOC} (metadata only)..."
az group create -n "$RG" -l "$RG_LOC" -o none

LOC=""
echo "Creating Azure SQL ${SQL_SERVER} / ${SQL_DB} (Middle East, then Europe)..."
for candidate in $ME_LOCS $EU_LOCS; do
  echo "  trying ${candidate}..."
  if try_create "SQL in ${candidate}" az sql server create -g "$RG" -n "$SQL_SERVER" -l "$candidate" -u "$SQL_ADMIN" -p "$SQL_PASSWORD" -o none; then
    LOC=$candidate
    echo "  SQL server created in ${LOC}"
    break
  fi
done
if [ -z "$LOC" ]; then
  echo "Could not create Azure SQL in any Middle East or Europe region." >&2
  exit 1
fi

az sql server firewall-rule create -g "$RG" -s "$SQL_SERVER" -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
az sql db create -g "$RG" -s "$SQL_SERVER" -n "$SQL_DB" --service-objective Basic -o none
SQL_FQDN=$(az sql server show -g "$RG" -n "$SQL_SERVER" --query fullyQualifiedDomainName -o tsv)
CS="Server=tcp:${SQL_FQDN},1433;Initial Catalog=${SQL_DB};User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False"

APP_LOC=""
echo "Creating App Service ${WEBAPP} (F1 then B1; SQL region first)..."
for candidate in $LOC $ME_LOCS $EU_LOCS; do
  for sku in F1 B1; do
    echo "  trying ${sku} in ${candidate}..."
    if try_create "plan ${sku} ${candidate}" az appservice plan create -g "$RG" -n "$PLAN" --sku "$sku" --is-linux -l "$candidate" -o none; then
      APP_LOC=$candidate
      echo "  plan created: ${sku} in ${APP_LOC}"
      break 2
    fi
  done
done
if [ -z "$APP_LOC" ]; then
  echo "Could not create an App Service plan (quota or region)." >&2
  exit 1
fi

az webapp create -g "$RG" -p "$PLAN" -n "$WEBAPP" --runtime "DOTNETCORE:8.0" -o none
az webapp config set -g "$RG" -n "$WEBAPP" --startup-file "dotnet Verita.Api.dll" -o none
az webapp config appsettings set -g "$RG" -n "$WEBAPP" --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  SCM_DO_BUILD_DURING_DEPLOYMENT=false \
  Jwt__Issuer="$JWT_ISSUER" \
  Jwt__Key="$JWT_KEY" \
  ConnectionStrings__Verita="$CS" \
  -o none
az webapp update -g "$RG" -n "$WEBAPP" --https-only true -o none

SWA_LOC=""
echo "Creating Static Web App ${SWA} (Standard, so /api can be linked)..."
for candidate in $SWA_LOCS; do
  echo "  trying ${candidate}..."
  if try_create "SWA in ${candidate}" az staticwebapp create -g "$RG" -n "$SWA" -l "$candidate" --sku Standard -o none; then
    SWA_LOC=$candidate
    echo "  Static Web App created in ${SWA_LOC}"
    break
  fi
done
if [ -z "$SWA_LOC" ]; then
  echo "Could not create a Static Web App in a supported region." >&2
  exit 1
fi

API_ID=$(az webapp show -g "$RG" -n "$WEBAPP" --query id -o tsv)
az staticwebapp backends link -n "$SWA" -g "$RG" --backend-resource-id "$API_ID" --backend-region "$APP_LOC" -o none
echo "  linked /api -> ${WEBAPP}"

SWA_TOKEN=$(az staticwebapp secrets list -n "$SWA" -g "$RG" --query properties.apiKey -o tsv)
SWA_URL=$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)
API_URL=$(az webapp show -g "$RG" -n "$WEBAPP" --query defaultHostName -o tsv)
SUB_ID=$(az account show --query id -o tsv)

ENV_FILE="$(cd "$(dirname "$0")/.." && pwd)/.env.azure-pilot"
cat > "$ENV_FILE" <<EOF
AZURE_RESOURCE_GROUP='$(q "$RG")'
AZURE_LOCATION='$(q "$LOC")'
AZURE_APP_LOCATION='$(q "$APP_LOC")'
AZURE_SWA_LOCATION='$(q "$SWA_LOC")'
AZURE_WEBAPP_NAME='$(q "$WEBAPP")'
AZURE_SWA_NAME='$(q "$SWA")'
AZURE_SQL_SERVER='$(q "$SQL_FQDN")'
AZURE_SQL_ADMIN='$(q "$SQL_ADMIN")'
AZURE_SQL_PASSWORD='$(q "$SQL_PASSWORD")'
AZURE_SUBSCRIPTION_ID='$(q "$SUB_ID")'
AZURE_STATIC_WEB_APPS_API_TOKEN='$(q "$SWA_TOKEN")'
ConnectionStrings__Verita='$(q "$CS")'
Jwt__Issuer='$(q "$JWT_ISSUER")'
Jwt__Key='$(q "$JWT_KEY")'
EOF

echo
echo "========== Save these =========="
echo "Resource group:     $RG"
echo "SQL region:         $LOC"
echo "API region:         $APP_LOC"
echo "Static Web region:  $SWA_LOC   (SWA has no Middle East region)"
echo "API:                https://$API_URL"
echo "Web:                https://$SWA_URL"
echo "SQL server:         $SQL_FQDN"
echo "SQL admin:          $SQL_ADMIN"
echo "SQL password:       $SQL_PASSWORD"
echo "Wrote secrets to    $ENV_FILE  (gitignored)"
echo
echo "GitHub secrets: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID,"
echo "  AZURE_WEBAPP_NAME=$WEBAPP, AZURE_STATIC_WEB_APPS_API_TOKEN (printed above)"
echo "================================"
