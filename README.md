# VERITA — ABCostingV2

A rebuild of the VERITA hospital activity-based costing platform on **Angular 22, .NET 8 and SQL Server**.
It follows the BRD in [`docs/BRD.docx`](docs/BRD.docx), and the reference implementation at `D:\Claude Projects\verita-abc`
is the parity oracle.

## What is here

| Path | What it is |
|---|---|
| `src/Verita.Domain` | `Money` and the largest-remainder `Distribute` (CR-2), the enum catalogue, roles and permissions, navigation, the data-quality register, and 61 entities |
| `src/Verita.Engine` | The eleven-stage costing engine. It is pure, with no EF, ASP.NET or I/O (ARCH-1), and is tested without a database |
| `src/Verita.Infrastructure` | EF Core schema (DECIMAL money, check constraints, temporal master data, published-only views), SqlBulkCopy writer, Dapper keyset streaming, run, governance, scenario and read services, fixture importer |
| `src/Verita.Api` | REST `/api/v1`. JWT, one policy per permission, typed error envelope, money as strings, a background run queue, OpenAPI |
| `web/` | Angular 22 standalone + signals: all 24 modules, route guard from the same permission map, run held in the URL, types generated from OpenAPI |
| `tools/` | Reference fixture exporter (`fixture/`, 5.7 MB) and the operator CLI (`bootstrap`, `parity`) |
| `tests/` | 22 engine tests and 11 API integration tests (the BRD's AC list); 4 web unit tests |
| `docs/PARITY.md` | Comparison with the reference engine's results |
| `docs/BRD-FINDINGS.md` | Places where the BRD and the reference disagree. **Read this before finalising the BRD.** |

## Run it locally

Prerequisites: .NET 8 SDK, Node 24, SQL Server Express at `localhost\SQLEXPRESS` (Windows authentication).

```bash
scripts/tools.sh bootstrap
```

That one command drops and creates the `Verita` database, loads the fixture, costs all eight hospital-periods, runs the six
scenarios and writes `docs/PARITY.md`. It takes about a minute.

```bash
scripts/run-api.sh
```

That starts the API on http://localhost:5230 (Swagger at `/swagger`). Then start the web app:

```bash
cd web && npx ng serve --port 4230
```

Open http://localhost:4230 and sign in with any seeded persona (`cfo@verita.health`, `costing.manager@verita.health`,
`clinical.director@verita.health`, `auditor@verita.health`, and others). The password for all of them is `Demo!Pass123`.

Tests:

```bash
scripts/test.sh
```

The scripts build into `%TEMP%` and retry, because Windows Smart App Control on this machine sometimes refuses a freshly
built assembly (error 0x800711C7).

## Adding a hospital or a month of data

Two screens under **Data** in the app (needs `masterdata.edit`; users need `admin.manage`):

- **Data Import:** download the templates (one CSV per table, or all of them numbered in a zip), fill them, and upload one CSV or a zip. **Validate** runs the real load inside a transaction and rolls it back, so a zip is checked in load order exactly as it would load. Rows that fail come back one by one with the reason, and the good rows load. Every upload is recorded with accepted and rejected counts, and rejected rows download as a CSV ready to fix.
- **Data Entry:** add and edit any table through a form generated from the same rules. References are picked by code. Rates, capacity, GL mappings and list prices are never edited in place: a change is a new version, and the previous one is closed the day before.

`samples/onboarding-example/` is a complete small hospital (King Fahd Hospital — Dammam, one radiology service, two patients) in template form. Zip the folder and upload it on Data Import, then cost `KFH-DMM` for 2026-09; it reconciles with nothing unexplained. The tables and rules are declared once in `src/Verita.Infrastructure/DataHub/EntitySpecs.cs`.

## Known gaps against the BRD

These are phase-1 gaps in this local build:

- **Sign-in:** OIDC/Entra ID with MFA (NFR-10) is not wired. Local break-glass accounts sign in with a JWT.
- **Not deployed:** there is no TLS, TDE or row-level security, and no containers.
- **Staging:** the fixture loads straight into the target tables rather than through `stg.*` staging (22.2), although each load is recorded in `ImportBatch`.
- **Resume:** stages are idempotent per run, but a failed run re-executes from the start rather than resuming at the failed stage (NFR-3).
- **Long tables:** these render in pages of 200 rather than with CDK virtual scroll (NG-8).
- **Performance:** NFR-1 at one million transactions has not been load-tested. Each fixture run (8–18k transactions) takes 1–2 s.
- **Data entry approvals:** master-data edits are audited but go live immediately. They do not yet route through four-eye approval, which the BRD asks for in phase 2.
