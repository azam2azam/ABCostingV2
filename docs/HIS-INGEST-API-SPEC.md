# HIS ingest API — spec

Replace the CSV/zip pack import with batched pushes from `HisExtract` to a VERITA ingest API. The API receives, a background worker applies, and every batch is tracked, retryable and safe to resend.

Status: **proposed, not started.**

Related: [`HIS-EXTRACT-PLAN.md`](HIS-EXTRACT-PLAN.md), [`HIS-COSTING-DATA-MAPPING.md`](HIS-COSTING-DATA-MAPPING.md), [`HIS-CONSUMPTION-QUANTITIES-SPEC.md`](HIS-CONSUMPTION-QUANTITIES-SPEC.md).

---

## 1. Problem

The import application crashes on monthly HIS packs (one facts month ≈ 66 MB of CSV, over 1M rows).

| Cause | Where | Effect |
|---|---|---|
| Each CSV read with `ReadToEnd()`; every file in the zip parsed into `List<Dictionary<string,string>>` before any save | `HisImportPack.Read`, `Csv.Parse` | Several GB of memory; IIS worker runs out of memory |
| One SQL transaction for the whole pack | `DataHubService.ImportSetAsync` | Long locks, log growth; one late failure rolls back everything |
| All work inside the HTTP request | `DataController.Import` | IIS timeout, app-pool recycle or closed browser kills the import; nothing to resume |
| EF change tracking on 50k-row pages; per-row fallback on any constraint error | `SaveOf<T>`, `SaveOneByOneAsync` | One bad row → thousands of single-row saves |
| Reference lookups reloaded per page | `SaveOf<T>` | Repeated full-table reads |
| Post-steps over whole tables after each file | `MarkChildInvoiceLines`, `MapPayerParties`, refund removal, ER merge | Slower as tables grow |

## 2. Goals

- Memory and transaction size bounded per batch, not per pack.
- No work depends on an HTTP request staying open.
- Any failure affects one batch, is visible, and can be retried.
- Resending the same data is always safe (idempotent).
- Incremental extracts: send only what changed in HIS.
- One pipeline for the extractor and manual uploads.

Out of scope: costing-model tables (blocked from import today, stays so), engine changes.

---

## 3. Architecture

```
HisExtract (HIS box)                         VERITA
─────────────────────                        ───────────────────────────────────
read HIS in slices ──► batch (5–10k rows)
   │                    gzip JSON
   │  outbox folder  ──► PUT batch ─────────► Ingest API: store batch, return 202
   │  (checkpoint)        retry/backoff               │
   │                                                  ▼
   └──► POST complete ──────────────────────► IngestBatch queue (DB)
                                                      │
                                              Background worker (hosted service)
                                                apply batch → own transaction
                                                validate, upsert, write rejects
                                                      │
                                              Finalize run (once)
```

Rule: **the API only receives; the worker applies.** A batch is durable in the database before the API returns. After a recycle the worker resumes from the queue.

---

## 4. API

Base: `/api/ingest`. JSON. Batches are `Content-Encoding: gzip`.

### 4.1 Start a run

`POST /api/ingest/runs`

```json
{
  "hospital": "HSP-01",
  "hisSetupId": "010266",
  "hisProjectId": 17,
  "pack": "facts",              // catalogue | facts
  "from": "2026-10-01",
  "toInclusive": "2026-10-31",
  "mode": "full",               // full | incremental
  "extractorVersion": "1.4.0",
  "clientRunKey": "010266-17-facts-2026-10-20261117T0930Z"
}
```

→ `201 { "runId": "…", "status": "RECEIVING" }`

- Validates the tenant once: hospital in the client's scope, bound to the same `SetupID/ProjectID`, or unbound (binds it, as the import does today).
- `clientRunKey` is unique: the same key returns the existing run (lets the extractor resume after a crash before it saved the run id).
- Only one open run per `(hospital, pack, month)`; another one returns 409 with the open run id.

### 4.2 Send a batch

`PUT /api/ingest/runs/{runId}/batches/{entity}/{seq}`

Headers: `X-Content-SHA256` (of the uncompressed body), `X-Row-Count`.

```json
{ "rows": [ { "encounterNo": "…", "procedureCode": "…", "sourceRef": "…", "…": "…" } ] }
```

- `entity` = an `EntitySpecs` name (`Encounter`, `EncounterService`, `RevenueTransaction`, …).
- Row columns = the same names as the CSV templates, so existing validation is reused unchanged.
- Max 10,000 rows or 8 MB uncompressed per batch.
- Responses:
  - `202` stored (status `RECEIVED`)
  - `200` same `(runId, entity, seq)` and same hash already stored — no-op
  - `409` same key, different hash
  - `400` unknown entity / columns / row count mismatch (batch not stored)
  - `423` run is not `RECEIVING`

### 4.3 Complete a run

`POST /api/ingest/runs/{runId}/complete`

```json
{ "expected": { "Patient": 18210, "Encounter": 41210, "EncounterService": 402118, "RevenueTransaction": 512300 },
  "batches":  { "Patient": 2, "Encounter": 5, "EncounterService": 41, "RevenueTransaction": 52 } }
```

- Server checks that received batch and row counts match. If they don't → `409` listing the missing `(entity, seq)`. The extractor resends those.
- On match → status `COMPLETE`; the worker starts applying.

### 4.4 Status, rejects, retry

| Endpoint | Returns |
|---|---|
| `GET /api/ingest/runs?hospital=&status=` | Recent runs |
| `GET /api/ingest/runs/{runId}` | Run status; per entity: batches received / applied / deferred / failed, rows created / updated / unchanged / rejected |
| `GET /api/ingest/runs/{runId}/rejects?entity=&page=` | Rejected rows: natural key, reasons, raw row |
| `GET /api/ingest/runs/{runId}/rejects.csv?entity=` | Same, as CSV |
| `POST /api/ingest/runs/{runId}/retry` | Re-queue `FAILED` batches (body optional: list of batch ids) |
| `POST /api/ingest/runs/{runId}/cancel` | Stop applying; applied batches stay |
| `GET /api/ingest/watermarks?hospital=` | Last pushed point per HIS table (§7) |
| `PUT /api/ingest/watermarks` | Extractor reports new watermarks — stored only when the run reaches `DONE` |

### 4.5 Authentication

- Machine client, not a user JWT: API key (hashed in DB) or OAuth client credentials.
- New permission `ingest.write`; client bound to one or more hospitals.
- UI users read status with `masterdata.view` as today.
- HTTPS only. Log client id on every run and batch.

---

## 5. Data model

### `IngestRun`

| Column | Notes |
|---|---|
| `Id` | Sequential GUID |
| `ClientRunKey` | Unique |
| `ClientId` | Ingest client |
| `HospitalId`, `HisSetupId`, `HisProjectId` | |
| `Pack`, `Mode`, `From`, `ToInclusive` | |
| `Status` | `RECEIVING → COMPLETE → APPLYING → FINALIZING → DONE`; `FAILED`, `CANCELLED` |
| `ExpectedCounts`, `ReceivedCounts` | JSON per entity |
| `ExtractorVersion` | |
| `StartedAt`, `CompletedAt`, `FinishedAt`, `LastError` | |

### `IngestBatch`

| Column | Notes |
|---|---|
| `Id` | |
| `RunId`, `Entity`, `Seq` | Unique together |
| `RowCount`, `Sha256` | |
| `Payload` | `varbinary(max)`, gzip as received |
| `Status` | `RECEIVED → APPLYING → APPLIED`; `DEFERRED`, `FAILED` |
| `Attempts`, `NextAttemptAt`, `LastError` | |
| `Created`, `Updated`, `Unchanged`, `Rejected` | Counts after apply |
| `ReceivedAt`, `AppliedAt` | |

Payloads of `DONE` runs are purged after a retention period (e.g. 30 days); counts stay.

### `IngestReject`

`BatchId`, `RowIndex`, `NaturalKey`, `Reasons` (JSON), `Row` (JSON).

### `IngestWatermark`

`HospitalId`, `HisTable`, `LastRowVer` (`binary(8)`), `LastEditedOn`, `RunId`, `UpdatedAt`.

### Existing tables

- `ImportBatch` stays for history. One row per `(run, entity)`, `SourceSystem = HIS-{SetupID}-{ProjectID}`.
- Fact tables (`Encounter`, `EncounterService`, `ActivityTransaction`, `RevenueTransaction`, `RefundTransaction`, `InvoicePackageItem`, and the new consumption tables) get a **`RowHash`** column (`binary(32)`, SHA-256 of normalised incoming columns).

---

## 6. Apply worker

Hosted service in `Verita.Api` (or a separate worker process if IIS recycling is a problem).

### 6.1 Picking work

- Picks batches of `COMPLETE` / `APPLYING` runs, `UPDLOCK, READPAST`, so several workers can run safely.
- **Order:** `EntitySpecs` load order within a run; all batches of an entity before the next entity. Runs for the same hospital are applied one at a time.
- Concurrency: one run per hospital at a time; batches of the same entity may run in parallel (setting, default 1).

### 6.2 Applying one batch

1. Decompress and parse the payload (one batch in memory only).
2. Remap codes to the run's hospital (`HisImportPack.BindHospital`), local encounter numbers.
3. Validate with existing `DataHubService` rules (refs, enums, numbers, dates).
4. Write in **one transaction for the batch**:
   - Catalogue / small tables: existing EF path (`SaveOf<T>`), using a **run-level lookup cache** (hospitals, procedures, cost centres, payers, providers) instead of reloading per page.
   - Fact tables: `SqlBulkCopy` into a session temp table, then one `MERGE` on the natural key; skip rows whose `RowHash` matches (`Unchanged`). Uses `BulkWriter`.
5. Constraint errors: retry that batch by halving (binary split) to isolate bad rows, not row by row.
6. Rejected rows → `IngestReject`. Rejects never fail the batch.
7. Mark `APPLIED` with counts.

### 6.3 Failures

| Case | Handling |
|---|---|
| Parent missing (service row before its encounter, unknown encounter) | Batch → `DEFERRED`; retried after the parent entity's batches are applied. Still missing after the parent entity is done → those rows rejected `PARENT_MISSING` |
| Unknown doctor | Create the "Not assigned" placeholder provider (today's `UnknownDoctors` rule) and load |
| Transient (deadlock, timeout, connection) | Retry with backoff: 30 s, 2 min, 10 min, 30 min, 2 h |
| Same error after 5 attempts | `FAILED`; visible on the run page with **Retry** |
| Worker / app-pool stops mid-batch | Batch transaction rolls back; `APPLYING` older than lease timeout (10 min) goes back to `RECEIVED` |

### 6.4 Finalize (once per run)

After all batches are `APPLIED` (or rejected), scoped to the run's **hospital and months**, not whole tables:

| Step | Today |
|---|---|
| Mark child invoice lines | `MarkChildInvoiceLinesSql` |
| Payer → insurer / administrator | `MapPayerPartiesAsync` (catalogue runs) |
| Remove refunds loaded as revenue | `RemoveRefundsFromRevenueAsync` |
| Merge replaced ER encounters | `MergeReplacedErEncountersAsync` |
| Ensure OPEN periods for the months | `EnsureFactPeriodAsync` |
| Invalidate assistant vocabulary | `AssistantVocabulary.Invalidate()` |
| Audit `CREATE ImportBatch` | `IAuditWriter` |
| Store watermarks from the run | §7 |

Run → `DONE`. While a run for a hospital and month is not `DONE`, **calculation runs for that hospital and month are blocked** (data is partial).

---

## 7. Tracking changes in HIS

### 7.1 Natural keys (already in place)

Upsert on existing keys: `Encounter.encounterNo`, fact `sourceRef` (e.g. `{TransactionType}-{InvoiceNo}-{LineItemNo}`), catalogue `code`. Resending any row is safe.

### 7.2 RowHash

Unchanged rows (same hash) are counted, not written. A re-pushed month with few changes is cheap.

### 7.3 Incremental extract (phase 2)

- Most HIS tables have `RowVer` (SQL `rowversion`) and `EditedOn`.
- Per HIS table, the extractor reads `GET /watermarks`, pulls `WHERE RowVer > @last`, and reports the new max at the end of the run.
- When a child row changes (invoice line, refund, consumption), resend its **parent encounter and all its rows** so derived fields stay right.
- Cancellations are status changes (`Status = 4`) and arrive as updates.
- **Hard deletes are not seen.** A monthly **reconcile run**: the extractor sends per-month counts and checksums per entity; VERITA compares; differing months are re-pushed in `full` mode, and rows not present in the full push are marked removed (not hard-deleted).

Start with `full` month slices over the API (same data as today), then switch to `incremental`.

---

## 8. Extractor changes (`tools/HisExtract`)

| Today | New |
|---|---|
| `CsvZipWriter` writes `NN_Entity.csv` parts into a zip | `BatchSink` writes 5–10k-row batches to an **outbox** folder (`outbox/{clientRunKey}/{entity}/{seq}.json.gz`) |
| Manual upload of the zip | `Pusher` sends outbox batches, deletes each after 2xx |
| — | `checkpoint.json` per run: run id, batches sent, counts — restart resumes |
| `SkipUnbilledEncounters` on the server | Done in the extractor: only billed encounters are sent |
| Settings: HIS connection | Plus: VERITA base URL, client id / secret, batch size, `--mode full|incremental`, `--csv` (keep the old zip as a fallback output) |

Push rules:

- Send entities in load order; parallel batches within an entity (default 4).
- Retry on 5xx, 408, 429 and network errors with backoff (1 s → 5 min, max 10 tries); honour `Retry-After`.
- Stop the run on 4xx other than 408/429/409-duplicate and print the server message.
- If VERITA is unreachable, the extract still finishes into the outbox; `HisExtract push` sends it later.
- End of run: `POST complete`; on 409 resend the listed batches.
- Command `HisExtract status {runId}` prints the server status.

---

## 9. Manual uploads on the same pipeline

- `POST /api/data/import` keeps its contract for small single-table CSV uploads.
- HIS packs and any file over a threshold (e.g. 5 MB): stream-parse the CSV (no `ReadToEnd`), cut batches while reading, write `IngestRun` + `IngestBatch`, return the run id at once.
- The Data Import screen polls the run and shows progress, rejects and retry.
- Dry run: the worker applies batches inside a transaction that is rolled back, per batch; the report notes that cross-entity checks are limited to rows already in VERITA.

---

## 10. UI

Data Import → **Ingest runs**:

- List: hospital, pack, months, mode, status, started, duration, rows, rejects.
- Run page: progress bar per entity (batches applied / total), counts, deferred / failed batches with errors, **Retry failed**, **Cancel**, rejects download.
- Banner on Runs: "Facts for HSP-01 2026-10 are still loading" while a run is not `DONE`.

---

## 11. Settings

| Setting | Default |
|---|---|
| `Ingest:MaxBatchRows` | 10,000 |
| `Ingest:MaxBatchBytes` | 8 MB |
| `Ingest:WorkerConcurrency` | 1 |
| `Ingest:LeaseMinutes` | 10 |
| `Ingest:MaxAttempts` | 5 |
| `Ingest:PayloadRetentionDays` | 30 |
| `Ingest:CommandTimeoutSeconds` | 600 (as `ImportCommandTimeoutSeconds`) |

IIS: site `AlwaysRunning` + preload so the worker starts without a request. `RequestSizeLimit(200 MB)` on import endpoints can drop once packs go through batches.

---

## 12. Rollout

| Phase | Work | Outcome |
|---|---|---|
| 1 | Shared `Verita.Ingest.Contracts` (run / batch DTOs, entity names) used by API and extractor | One contract |
| 2 | Tables (§5), endpoints (§4), worker reusing current validation (§6), finalize (§6.4), client auth | Server ready |
| 3 | Extractor `BatchSink` + outbox + `Pusher` + checkpoint; `--csv` kept | **Crashes stop** (bounded memory and transactions) |
| 4 | Ingest runs UI | Visible progress and retry |
| 5 | Bulk copy + `MERGE` + `RowHash` for fact tables | Speed |
| 6 | `RowVer` watermarks, incremental mode, monthly reconcile | Only changes sent |
| 7 | Manual uploads through the pipeline; retire big-zip endpoint | One pipeline |

## 13. Tests

- Batch idempotency: same batch twice → one write; different hash → 409.
- Resume: kill the worker mid-batch → batch re-applied once, counts right.
- Deferred parent: services before encounters → applied after encounters.
- Complete with missing batches → 409 with the list.
- Finalize runs once, scoped to the run's hospital and months.
- Memory: a 1M-row facts month applies within a fixed memory ceiling (e.g. < 500 MB worker growth).
- Parity: the same month through the old zip import and the new pipeline gives identical row counts and totals per entity.

## 14. Open questions

1. Network path from the HIS server to VERITA (firewall, proxy, TLS certificate).
2. API key vs OAuth client credentials for the extractor.
3. Worker inside the IIS site vs a separate Windows service.
4. Retention of payloads and rejects.
5. Whether manual dry run is still needed for HIS packs once runs can be retried.
6. Reconcile frequency and which months (current + previous only?).
