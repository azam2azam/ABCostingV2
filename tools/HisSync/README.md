# VERITA HIS Sync worker

An ASP.NET Core site with two background workers, so it deploys to IIS the same way as the VERITA API.
It reads the HIS (read-only, `READ UNCOMMITTED`, `NOLOCK`) and writes to the VERITA database through the
same import code as Data Import, so a synced row passes exactly the checks an uploaded one does.

Everything is driven from **System → HIS Sync** in VERITA. The worker only reads what that screen asks for and
writes back its progress and log. It has no UI of its own.

## What it does

**Catalogue worker.** Runs once a day at the time set on HIS Sync (when the daily check is on), or straight
away on **Sync now**. It extracts the HIS catalogue, validates it against VERITA without writing anything
(a dry-run import), and if rows are new or changed it **parks** them. The pack is kept as an import file and
listed under *Catalogue changes* with counts per table. A person chooses **Apply changes** (the pack is queued
on VERITA's import worker) or **Discard**. A newer check replaces an older parked one.

**Facts worker.** Syncs the fact dates added on HIS Sync, oldest first, straight into VERITA (no parking).
Today is never synced. For each day it:

1. lists the day's encounters once (keys only), the same set a whole-day HisExtract pack covers. An ER visit and
   the appointment it is billed through are kept together;
2. syncs them in batches of `BatchSize` encounters (default 100). Each batch extracts those encounters with all
   their rows (patients, encounters, services, revenue, package items, refunds, consumption, staff time,
   transfers, DRG) and imports them;
3. writes the refunds dated that day (any encounter);
4. marks the day done and moves to the next one.

Progress is stored per day and per encounter in VERITA (`HisSyncDay`, `HisSyncEncounter`), so a recycle or crash
only repeats the batch in hand. A batch that fails is retried. After `MaxAttempts` tries it is split in two
until the encounter that fails is found; that encounter is marked failed and listed in the log, and the rest of
the day carries on. A HIS step that times out fails the batch, because syncing only moves forward and a skipped
step would leave rows missing.

Facts wait (the worker shows *Paused* with the reason) while the hospital has no catalogue, or while new catalogue
rows are parked: facts that refer to them would otherwise be rejected.

The worker syncs only hospitals linked to a HIS tenant (SetupID / ProjectID are set on a hospital's first HIS
import).

## Configuration (`appsettings.json` or environment variables)

| Setting | Default | |
|---|---|---|
| `ConnectionStrings:Verita` | — | The VERITA database (same as the API). |
| `His:ConnectionString` | — | HIS (HISGENX) database. A read-only login is enough; it needs to create `#temp` tables. |
| `His:Tenants:{SetupID-ProjectID}:ConnectionString` | — | Optional per-tenant connection, e.g. `His:Tenants:010266-15:ConnectionString`. |
| `His:PharmacyDb` | `PHARMAKON` | Item master database on the HIS server, for item names. |
| `His:QueryTimeoutSeconds` | `1800` | One HIS query's limit. |
| `His:Claims` | `false` | Also extract claim, settlement and NPHIES lines (as `HisExtract --claims`). |
| `HisSync:Enabled` | `true` | Both workers on or off (the site keeps answering). |
| `HisSync:CatalogueEnabled` / `FactsEnabled` | `true` | Each worker on or off. |
| `HisSync:Hospitals` | `[]` | VERITA hospital codes to sync; empty = all linked hospitals. For a staged rollout. |
| `HisSync:BatchSize` | `100` | Encounters per facts batch. |
| `HisSync:PauseBetweenBatchesMs` | `2000` | Pause after each batch, to keep the load on HIS and VERITA light. |
| `HisSync:PollSeconds` | `30` | How often an idle worker looks for work. |
| `HisSync:MaxAttempts` | `3` | Tries before splitting a batch / marking it failed. |
| `HisSync:UtcOffsetHours` | `3` | The hospital's clock, for "today" and the catalogue check time. |
| `HisSync:WorkDir` | system temp | Where a pack is written while it is imported (deleted after). |
| `HisSync:StatusKey` | empty | When set, `/status` needs header `X-Sync-Key`. |

Keep connection strings out of the file in production: use environment variables on the app pool
(`ConnectionStrings__Verita`, `His__ConnectionString`) or `appsettings.Production.json` outside source control.

## Endpoints

- `GET /health` — liveness, no data.
- `GET /status` — per hospital, what each worker is doing (as on HIS Sync).

## Deploy to IIS

```bash
dotnet publish tools/HisSync -c Release -o ./publish/HisSync
```

1. Install the .NET 8 Hosting Bundle and the **Application Initialization** IIS feature.
2. Create a site (or application) pointing at the publish folder, with its own app pool:
   - .NET CLR version: **No Managed Code**
   - **Start Mode: AlwaysRunning**
   - **Idle Time-out (minutes): 0**
   - **Regular Time Interval (minutes): 0** (or a fixed recycle time at night)
3. On the site, **Preload Enabled: True**. `web.config` already asks IIS to request `/health` when the pool
   starts, so the workers start without a visitor.
4. Run **one** site per VERITA database. During an overlapped recycle, a lock in the VERITA database
   (`sp_getapplock`) stops two processes from syncing at the same time.
5. Neither the API nor this worker changes the database schema. Run the release's SQL script (deploy/iis/sql)
   first; until the HIS sync tables exist the worker logs a warning and waits.

## Checking batches against whole days

Before relying on the sync for a hospital, compare a few days on UAT:

```bash
dotnet run --project tools/HisExtract -- batchcheck --from 2026-09-01 --to 2026-09-03 --batch-size 100
```

For each day it extracts the whole-day pack and the worker's batches (plus the day's refunds) and compares every
table row for row. Exit code 0 means they match; 2 means a difference (the first rows that differ are printed).
