# Azure + GitHub CI/CD (pilot)

Source: [azam2azam/ABCostingV2](https://github.com/azam2azam/ABCostingV2).

Deploy by pushing a branch (or **Actions → Run workflow**):

| Branch | Workflow |
|---|---|
| `deploy-api/*` | API → App Service |
| `deploy-ui/*` | Angular → Static Web App |
| `deploy/*` | Both API and UI |

Example: `git checkout -b deploy/pilot && git push -u origin HEAD`. Path filters are not used, so creating that branch is enough to deploy.

The Angular app calls same-origin `/api/v1`. The Static Web App is **Standard** and linked to App Service `app-verita-pilot-api`, so `/api/*` is proxied. No CORS change is required.

## Live resources (`rg-verita-pilot`)

| Piece | Name | Region |
|---|---|---|
| Resource group | `rg-verita-pilot` | UAE North |
| Azure SQL | `sqlverita113512.database.windows.net` / `Verita` | UAE North |
| App Service | `app-verita-pilot-api` | Israel Central (F1) |
| Static Web App | `stapp-verita-pilot-web` | East Asia |

- API: https://app-verita-pilot-api.azurewebsites.net
- Web: https://black-grass-01a44c800.3.azurestaticapps.net
- Connection string, JWT key, and SQL password live in App Service settings and local `.env.azure-pilot` (gitignored). Do not commit them.

## Workflows

- `.github/workflows/api.yml` — engine tests, publish `Verita.Api`, deploy App Service (OIDC)
- `.github/workflows/web.yml` — `ng build`, upload Static Web App

GitHub secrets (already on the repo):

| Secret | Used by |
|---|---|
| `AZURE_CLIENT_ID` | API workflow OIDC |
| `AZURE_TENANT_ID` | API workflow OIDC |
| `AZURE_SUBSCRIPTION_ID` | API workflow OIDC |
| `AZURE_WEBAPP_NAME` | `app-verita-pilot-api` |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Web workflow |

Entra app `github-verita-cicd` uses a federated credential for GitHub environment `azure`. GitHub’s OIDC subject includes owner/repo IDs:

`repo:azam2azam@67052343/ABCostingV2@1407141690:environment:azure`

The API workflow sets `environment: azure`.

## Local SQL (not Azure)

```bash
docker compose up -d
ConnectionStrings__Verita='Server=localhost,1433;Database=Verita;User Id=sa;Password=Verita!Dev123;TrustServerCertificate=True;Encrypt=False' \
  dotnet run --project src/Verita.Api --launch-profile http
```

## Recreate Azure resources

```bash
az login
scripts/azure-pilot.sh
```

Location order is Middle East first, then Europe. Static Web Apps have no Middle East region. The script writes `.env.azure-pilot` and, when it creates a new SWA, uses **Standard** so the App Service backend can be linked.

Sign in on the web URL with `cfo@verita.health` / `Demo!Pass123` after bootstrap.
