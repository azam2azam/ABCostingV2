# HIS → VERITA extract plan

How we will pull HISGENX data for **one hospital** and load it into this costing app through the existing **Data import** zip/CSV path.

Companion: [`HIS-COSTING-DATA-MAPPING.md`](HIS-COSTING-DATA-MAPPING.md) (schema mapping). This file is the **operational plan**, updated with live table row counts.

---

## 1. Target tenant

| Side | Value |
|---|---|
| HIS organisation | `SetupID = '010266'` |
| HIS hospital / branch | `ProjectID = 15` |
| Costing hospital | `Hospital.Code = RYN-UAT` |
| HIS SQL Server | `RAY-VIDA-UAT` |
| HIS database | `HISGENX` |
| HIS login | `HmgDbUser` / `HmgDbUser123` |

Default connection string (Enter to accept; override at the prompt or with `HIS_CONNECTION`):

```
Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123;TrustServerCertificate=True;Application Name=VeritaHisExtract
```

Every HIS query is `WHERE SetupID = @SetupId AND ProjectID = @ProjectId` (tables that have no `ProjectID` — e.g. `HIS_CostCenterSetup`, `HIS_ICD10_AM_DISEASE` — filter `SetupID` only).

The published row-count file is **the whole HIS database** (all 8 projects). `STP_Project` has 8 rows, `STP_Setup` has 1. Project 15 is one branch of that organisation. Until the extractor’s **probe** runs on the HIS machine, we do not know Project 15’s share of the 48 million invoice lines.

---

## 2. What the costing app will accept

The app already imports the way we need. Do not write a second loader.

| Piece | Behaviour |
|---|---|
| Screen | **Data import** (`web` → `/api/v1/data/import`) |
| File | One CSV, or a **zip of CSVs** processed in `EntitySpecs` order |
| Names | `NN_TableName.csv` (e.g. `01_Hospital.csv`, `33_Encounter.csv`). The number is stripped; the table name must match. |
| Encoding | UTF-8, optional BOM |
| Header | Exact camelCase column names from the template |
| Guidance row | Optional second row starting with `#` — skipped |
| Dates | `yyyy-MM-dd` or `yyyy-MM-dd HH:mm` |
| Numbers | Dot decimal, no thousands separators |
| Bools | `true` / `false` |
| Zip limit | **200 MB** (`RequestSizeLimit(200_000_000)`) |
| Bad rows | Rejected individually; the rest still load |
| Validate | `dryRun=true` runs the real load in a transaction and rolls back |

Empty files (header only) are skipped. Leave out tables we are not filling.

Templates zip is downloadable from the same screen (`templates.zip`). The extractor **must emit the same headers**, not HIS column names.

Import order (from `EntitySpecs.All`) — only emit files we actually fill:

```
01 Hospital
02 Facility
03 ServiceLine
04 Department
05 CostCenter
14 Provider
18 Procedure
19 ProcedureRate
24 Package
25 PackageComponent
26 Drg
27 Payer
32 Patient
33 Encounter
34 EncounterService
35 ActivityTransaction
36 RevenueTransaction
```

Tables **not** produced by HIS (costing model / ERP) are filled later in the app or from Oracle: `GLAccount`, `Period`, `CostDriver`, `CostPool`, `GLMapping`, `Resource`, `ResourceRate`, `ResourceCapacity`, `Activity` + recipes, `CostingVersion`, `AllocationRule*`, `GLPosting`, `DriverVolume`, `User`.

---

## 3. Row counts: what is real, empty, or too big

Counts from `Database/Vida3/table row count.csv` (whole DB, not Project 15).

### 3.1 Masters we will extract (small / safe)

| HIS table | Rows | Use in costing |
|---|---:|---|
| `STP_Setup` | 1 | Confirm org |
| `STP_Project` | 8 | Pick Project 15 → `Hospital` |
| `STP_Company` / `STP_Region` / `STP_City` | 3 / 2 / 4,115 | Hospital city / name |
| `HIS_Clinic` | 351 | `Department` |
| `HIS_ClinicGroup` | 130 | `ServiceLine` seed |
| `HIS_CostCenterSetup` | 212 | `CostCenter` seed (Oracle tree) |
| `HIS_RuleCostCenterNew` | 33 | Clinic / class → cost centre (**current** rules) |
| `HIS_RuleCostCenter` | 309,971 | History — **do not dump**; use New only |
| `INP_NursingStation` | 101 | Ward departments |
| `INP_OTMain` | 24 | Theatre departments |
| `INP_Building` / `INP_Floor` | 8 / 10 | `Facility` |
| `HIS_Building` / `HIS_Floor` / `HIS_Room` | 11 / 10 / 15 | OPD space |
| `INP_Room` / `INP_RoomCategory` / `INP_Bed` | 905 / 36 / 2,665 | Rooms; bed count |
| `HIS_Procedure` | 50,399 | Charge master → `Procedure` (filter Project 15) |
| `HIS_ProcedureCategory` / `Group` / `SubGroup` | 80 / 284 / 511 | `procType` / category |
| `HIS_ProcedurePrice` | 252,812 | `ProcedureRate` |
| `HIS_ProcedureCpt` | 16,393 | `catalogCode` |
| `HIS_ProcedurePackageDetail` | 98,269 | `PackageComponent` |
| `HIS_PPCMain` / `HIS_PPCDetail` | 29 / 87 | Package catalogue (thin) |
| `HIS_Company` / `HIS_CompanyGroup` | 68,074 / 865 | `Payer` — filter to this project + used in period |
| `HIS_CompanySubCategory` | 194,779 | Plan grain — **do not** make one payer per plan |
| `HIS_DRGSpecification` | 602 | `Drg` |
| `HIS_Doctor` | 8,430 | `Provider` |
| `HIS_DoctorClinic` | 15,341 | Provider → department |
| `HIS_DoctorSpeciality` | 4,437 | `specialty` |
| `LAB_TestMain` | 13,585 | Mark procedures as `LAB` |
| `LAB_Division` / `Section` / `Site` | 81 / 179 / 97 | Lab departments |
| `STP_LinkClinicDepartment` | 296 | Extra clinic → dept hint |
| `STP_Parameter` | 49,343 | Decode enums (needed on HIS box) |
| `INP_Tariff` / `INP_TariffProcedureDetail` | 26 / 68 | Fallback IP prices |

### 3.2 Facts we will extract — **only for a date range**

These are too large to copy in full. The extractor always takes `@From` / `@To` (one costing month to start).

| HIS table | Rows (all projects, all years) | Role |
|---|---:|---|
| `HIS_InvoiceDetail` | **48,562,048** | `EncounterService` + `RevenueTransaction` |
| `HIS_InvoiceMain` | 17,738,387 | Encounter bridge, payer, dates |
| `HIS_Appointment` | 20,556,938 | OP `Encounter` |
| `HIS_Patient` | 10,668,173 | `Patient` — **only patients in the period’s encounters** |
| `HIS_ProgNoteOrder` | 16,881,898 | Unbilled OP services (optional, later) |
| `INP_ProgNoteOrder` | 13,952,511 | Unbilled IP services (optional, later) |
| `LAB_OrderDetail` / `LAB_OrderMain` | 12,221,544 / 3,518,653 | Lab performed (optional actuals) |
| `HIS_ProgNoteMedicine` | 11,756,484 | Drug quantities (optional actuals) |
| `HIS_InvoicePackageItem` | 11,185,522 | Explode packages |
| `HIS_FloorConsumption` | 4,167,125 | Consumable `ActivityTransaction` |
| `INP_ConsumableItemLog` | 2,887,982 | Same |
| `INP_PatientConsumedItem` | 2,239,562 | Same |
| `INP_ERAssessment` | 1,301,821 | ER `Encounter` |
| `HIS_RadReport` | 1,557,481 | Imaging times |
| `INP_Transfer` | 516,934 | Bed-days by ward |
| `INP_Admission` | 285,050 | IP `Encounter` |
| `INP_DischargeSummary` | 291,509 | LOS |
| `INP_OTReservation` | 216,169 | Surgery flag + OR minutes |
| `INP_OTProcedureMapping` | 156,197 | Surgery procedures |
| `HIS_ReturnDetail` / `HIS_RefundDetail` | 2,228,459 / 571,933 | Negative revenue |
| `HIS_InvoiceCreditDetail` | 1,508 | Credit notes (small) |

### 3.3 Empty or unusable — drop from the previous mapping

| HIS table | Rows | Consequence |
|---|---:|---|
| `GLD_Account` and all `GLD_Journal*` | **0** | Local HIS GL is dead. `GLPosting` comes from **Oracle only**. |
| `STP_Department` / `STP_CostCenter` | **0** | Unused. Departments = clinics + nursing stations. |
| `INP_ORLogInfo` / `INP_ORLogHeader` | **0** | No intra-op log. Use `INP_OTReservation` times (`ActualSugeryStartTime` / `EndTime`). |
| `PHR_Medicine*` | **1** | Pharmacy item master is not in this DB. Drugs are `HIS_Procedure` where `IsMedicine = 1`. Dispense cost still needs the pharmacy database later. |
| `HIS_ProcedureDuration` | **0** | No duration master. Seed activities from `HIS_Procedure.ProcedureTime` or measured times. |
| `HIS_ProcedureMaterialMapping` | 8 | BOM is empty. Do not build recipes from it. |
| `INP_BedCensus` | 414 | Not a daily census. Bed-days = admission LOS and `INP_Transfer`. |
| `INP_AdmissionDRGList` | **1** | DRG almost never stored on the admission. Keep the DRG **catalogue**; leave `Encounter.drgCode` empty until another source appears. |
| `HIS_SpecialPackageInfo` | 0 | Use `HIS_Procedure.IsPackage` + `HIS_ProcedurePackageDetail` / `HIS_PPCMain`. |
| `HIS_ROTADetails` | 2,228 | Roster too thin for capacity. `ResourceCapacity` stays a workshop/HR input. |

### 3.4 Do not extract (noise)

Nursing notes, education sessions, vitals, claims settlement, SMS, audit, EMR flowsheets, dental charting, IVF worksheets. Biggest examples: `HIS_PatientEduSessionDetail` 159M, `EHR_DynamicFlowSheet` 66M, `HIS_RuleCompany` 66M (coverage rules — we only need companies actually billed).

---

## 4. Approach: a console extractor on the HIS machine

HIS is not reachable from this workstation. We build a small **.NET 8 console** here, copy a self-contained folder to the HIS PC, run it there (read-only SQL), and bring the zip(s) back for Data import.

```
HIS SQL Server  --read-only-->  HisExtract.exe  -->  RYN-UAT-catalogue.zip
                                                -->  RYN-UAT-2026-09-facts.zip
                         (USB / fileshare)
VERITA Data import  <-- zip <--  this workstation
```

### 4.1 Why a console, not a linked server

- No network path from here to HIS.
- UAT connection defaults are baked in (see §1); the prompt still offers an override for another server.
- Streaming readers: we must not load 48 million invoice lines into memory.
- Output is already the costing app’s contract (numbered CSVs).

### 4.2 Project

Implemented at `tools/HisExtract/`. Standalone .NET 8 console, **no reference** to `Verita.Infrastructure` / EF. Publish as one folder:

```bash
dotnet publish tools/HisExtract -c Release -r win-x64 --self-contained -o ./publish/HisExtract
```

Dependencies: `Microsoft.Data.SqlClient` only.

Layout:

```
tools/HisExtract/
  HisExtract.csproj
  Program.cs              prompt + commands
  Settings.cs             SetupId, ProjectId, HospitalCode, UAT connection
  HisConnection.cs        read-uncommitted SQL
  Probe.cs
  CsvZipWriter.cs
  Codes.cs / Maps.cs
  CatalogueExtract.cs
  FactsExtract.cs
```

Commands:

```text
HisExtract
  1) Connection string (Enter = UAT default below, or HIS_CONNECTION env var)
  2) Confirm SetupId / ProjectId / HospitalCode (defaults below)
  3) Command:
       probe              print Project-15 counts for key tables
       catalogue          write RYN-UAT-catalogue.zip
       facts              write RYN-UAT-{period}-facts.zip  (asks From/To)
       all                catalogue then facts
```

Defaults baked in (overridable):

```
Server        = RAY-VIDA-UAT
Database      = HISGENX
User Id       = HmgDbUser
Password      = HmgDbUser123
SetupId       = 010266
ProjectId     = 15
HospitalCode  = RYN-UAT

Server=RAY-VIDA-UAT;Database=HISGENX;User Id=HmgDbUser;Password=HmgDbUser123;TrustServerCertificate=True;Application Name=VeritaHisExtract
```

### 4.3 Safety on the HIS box

- `Application Name=VeritaHisExtract;Pooling=true;`
- `ApplicationIntent=ReadOnly` if the listener supports it
- `SELECT` only — never INSERT/UPDATE
- `READ UNCOMMITTED` so we do not block billing
- Command timeout 0 (or 600s) on large invoice queries
- Write CSV to disk incrementally; zip at the end
- If a facts zip would exceed ~180 MB, split by week (see §7)

---

## 5. What each pack contains

### Pack A — `RYN-UAT-catalogue.zip` (once)

Identity and charge master. No period filter except “active”.

| File | HIS origin | Transform (short) |
|---|---|---|
| `01_Hospital.csv` | `STP_Project` Project 15 | `code=RYN-UAT`, name/city/currency from project, `bedCount` = count of `INP_Bed` |
| `02_Facility.csv` | `INP_Building` | `{code=RYN-UAT.B-{id}}`, `squareMetres=0` until FM supplies it |
| `03_ServiceLine.csv` | Distinct `HIS_ClinicGroup` for this project | `SL-{GroupID}`, name = `GroupDescription`, `category` from name heuristics |
| `04_Department.csv` | `HIS_Clinic` ∪ `INP_NursingStation` ∪ `INP_OTMain` ∪ `LAB_Division` | See codes below |
| `05_CostCenter.csv` | `HIS_CostCenterSetup` + rule map | Oracle CC code; `ccType` default `PRODUCTION`; `stepDownSeq=100` |
| `14_Provider.csv` | `HIS_Doctor` (this project, active) | Needs a **role resource** later — see gap in §8 |
| `18_Procedure.csv` | `HIS_Procedure` where not medicine | `procType` from category/flags |
| `19_ProcedureRate.csv` | `HIS_Procedure.PublishedPrice` (fallback `Price`) + `HIS_ProcedurePrice` cash/default | `effectiveFrom` = 2000-01-01 or price `EffectiveDate` |
| `24_Package.csv` | `HIS_Procedure` where `IsPackage=1` | |
| `25_PackageComponent.csv` | `HIS_ProcedurePackageDetail` | |
| `26_Drg.csv` | `HIS_DRGSpecification` | Shared catalogue (no ProjectID) |
| `27_Payer.csv` | `HIS_Company` used by this project | Plus synthetic `PAY-SELF` |

**Department codes (stable):**

| Source | `code` | `catalogCode` | `deptType` |
|---|---|---|---|
| Clinic | `RYN-UAT.CLN-{ClinicID}` | `HIS_Clinic.MappingCode` or `CLN-{ClinicID}` | `CLINICAL` |
| Nursing station | `RYN-UAT.NS-{NursingStationID}` | `MappingCode` or `NS-{id}` | `CLINICAL` |
| OT | `RYN-UAT.OT-{OTID}` | `OT-{id}` | `CLINICAL` |
| Lab division | `RYN-UAT.LAB-{DivisionID}` | `LAB-{id}` | `ANCILLARY` |

**Procedure codes:** `RYN-UAT.{ProcedureID}`  
**catalogCode:** CPT from `HIS_ProcedureCpt` if present, else `OracleItemCode`, else raw `ProcedureID`.  
**Provider codes:** `RYN-UAT.DOC-{DoctorID}`  
**Payer codes:** `PAY-{CompanyID}` (zero-padded if needed), `PAY-SELF` for cash.

Skip medicines (`IsMedicine = 1`) in `Procedure.csv`. They become resources in a later workshop, not billable procedures.

**`Provider` problem:** VERITA requires `resourceCode`. Catalogue pack cannot invent payroll roles. Two options (pick one before coding):

1. **Defer `14_Provider.csv`** until `Resource` exists; still write doctors to a sidecar `his-doctors.csv` for the workshop.
2. **Emit one placeholder resource** in a tiny extra file `11_Resource.csv` (`RYN-UAT.RES-PHYS`, `LABOR_PHYSICIAN`) and point every provider at it. Ugly but lets encounters reference a doctor on day one.

Recommendation: **option 2 for the first import**, replace with real grades later.

**`EncounterService` needs `costCentreCode`.** If `HIS_InvoiceDetail.CostCenterID` is empty, map performing clinic → department → a default cost centre `RYN-UAT.CC-{dept}`. Catalogue pack should create **one cost centre per department** when Oracle CC is missing, using `HIS_RuleCostCenterNew` where it hits.

### Pack B — costing model (not HIS)

Created in the app (or a hand-filled zip): Period, GL accounts, pools, mappings, resources/rates/capacity, activities, recipes, allocation rules, `CostingVersion`. Without this, a run will fail data-quality checks even if Pack A+C load cleanly.

### Pack C — `RYN-UAT-{yyyy-MM}-facts.zip` (each month)

Only rows whose **clinical/invoice date** falls in `[From, To)`.

| File | Build rule |
|---|---|
| `32_Patient.csv` | Distinct patients on the period’s OP + IP + ER encounters. `mrn = RYN-UAT-{PatientType}-{PatientID}` (PatientType is in the HIS PK). `birthYear` only, national ID masked. |
| `33_Encounter.csv` | Union: closed OP appointments, IP admissions **admitted** in the month (VERITA rule: month of admit), ER assessments. |
| `34_EncounterService.csv` | Invoice detail lines in the month whose header has AppointmentNo or AdmissionNo; skip medicine procedures; explode package components if header is a package. `sourceRef = INV-{TransactionType}-{InvoiceNo}-{LineItemNo}` |
| `36_RevenueTransaction.csv` | Same lines: gross / discount / net from price × qty and shares. `sourceRef = REV-{TransactionType}-{InvoiceNo}-{LineItemNo}`. Add refund/return/credit as negative lines. |
| `35_ActivityTransaction.csv` | **Phase 2 of facts.** First month can omit this file (standard costing still works if recipes exist). When included: OR minutes from `INP_OTReservation`, bed-day rows from IP LOS / transfers, floor consumption quantities. |

**Encounter numbers:**

| Type | `encounterNo` | `encType` |
|---|---|---|
| OP | `RYN-UAT-OP-{AppointmentNo}` | `OP` |
| IP | `RYN-UAT-IP-{AdmissionNo}` | `IP` |
| ER | `RYN-UAT-ER-{AssessmentNo}` | `ER` |

`payerCode`: invoice/admission `CompanyID` → `PAY-{id}`, else `PAY-SELF`.  
`departmentCode`: clinic / admitting clinic / ER clinic as in the mapping doc.  
`isSurgical`: IP with an `INP_OTReservation` in the stay.  
`lengthOfStay`: discharge days or datediff; OP/ER = 0.

Filter OP to visits that happened (`Status` not cancelled; prefer `IsVisitClosed = 1` or actual visit times — confirm with `probe` + a sample). Filter invoices to non-cancelled `Status`.

---

## 6. Probe (first thing we run on HIS)

Before extracting, `probe` prints **Project 15** counts and a one-month sample. That is what we use to size zips and indexes.

Example output we want:

```
SetupID=010266  ProjectID=15  Hospital=RYN-UAT
STP_Project.Name = ...
HIS_Clinic              n
HIS_Procedure           n   (IsMedicine=0 / =1 / IsPackage=1)
HIS_Doctor              n
HIS_Company (this proj) n
HIS_Appointment  {month} n
INP_Admission    {month} n
INP_ERAssessment {month} n
HIS_InvoiceMain  {month} n
HIS_InvoiceDetail{month} n
HIS_Patient distinct in those encounters n
```

If month invoice lines > ~400k, we split facts by week. If procedures for Project 15 are still tens of thousands, we still extract them (catalogue zip will be small; 20k procedure rows is fine).

Also dump distinct `STP_Parameter` groups we care about (`PatientType`, gender, invoice `TransactionType`, `AdmissionType`, `CompanyType`) into `probe-parameters.csv` so we can finish enum maps without guessing.

---

## 7. Size and chunking

| Constraint | Limit |
|---|---|
| Upload zip | 200 MB |
| Importer | Loads the **whole target table** into memory for upsert |
| First hospital-month | Keep facts well under both |

Practical rules:

1. Catalogue zip: one file, no chunking.
2. Facts: **one calendar month** per zip.
3. If `probe` says the month’s `HIS_InvoiceDetail` for Project 15 exceeds ~300,000 lines, emit `RYN-UAT-2026-09-w1-facts.zip` … `w4` (same patients/encounters in week 1, services/revenue split by invoice date). Encounters must be imported **before** services — so week-1 zip includes **all** month encounters + week-1 lines; later weeks are services/revenue only.
4. Do not extract all-history patients. Only patients referenced by the period’s encounters.

CSV UTF-8 of ~100k invoice-like rows is typically 15–40 MB. One month for one branch should fit once filtered.

---

## 8. What HIS will not give us (still true after counts)

| Costing table | Why |
|---|---|
| `GLAccount` / `GLPosting` / `GLMapping` | `GLD_*` is empty. Oracle trial balance by cost centre. |
| `ResourceRate` / true `ResourceCapacity.annualCost` | No payroll, no inventory cost. `PHR_Medicine` empty. |
| `CostPool` / `AllocationRule` / `Activity` recipes | Costing design. Procedure BOM is empty (8 rows). |
| Equipment | `LAB_Equipment` is interface config (182 rows), not an asset register. |
| Floor area | Not in HIS. |

A published costing **run** needs Pack B + Oracle GL in addition to these zips. Pack A+C alone populate Data entry (patients, encounters, billed services, revenue) and are the right first milestone.

---

## 9. Work sequence

```
1. Build HisExtract (this repo), publish win-x64 self-contained.
2. Copy folder to HIS PC. Run probe with SetupId/ProjectId.
3. Freeze enum maps from probe-parameters.csv (TransactionType, gender, …).
4. Run catalogue → bring RYN-UAT-catalogue.zip here.
5. Data import: Validate, then Import. Fix rejects (unknown procType, missing CC).
6. Pick one closed month. Run facts. Validate/import.
7. Workshop Pack B (pools, resources, recipes) + Oracle GL.
8. Cost the month. Expand ActivityTransaction and more months.
```

On Data import: use a user whose `hospitalScope` includes `RYN-UAT` (or `*`). Shared tables (`Payer`, `Drg`, `ServiceLine`) require a **group-wide** user (`hospitalScope = *`).

---

## 10. Extractor coding rules (when we implement)

1. Parameterize `@SetupId`, `@ProjectId`, `@From`, `@To`. Never concatenate SQL.
2. Stream `SqlDataReader` → `StreamWriter`. No `DataTable`.
3. Escape CSV as RFC 4180 (same as `Verita.Infrastructure.DataHub.Csv`: quote if comma/quote/newline; double quotes).
4. Money: HIS `float` → round to 4 decimal places invariant culture.
5. `smalldatetime` / varchar times → `yyyy-MM-dd HH:mm`; if time is unparsable, use date at 00:00.
6. Skip deleted/cancelled rows (`IsActive`, `Status`) once probe shows the codes.
7. Idempotent codes: same HIS key always yields the same VERITA code so re-import updates rather than duplicates.
8. Log row counts written per file to `extract-log.txt` inside the zip.
9. Include `README.txt` in the zip: SetupId, ProjectId, HospitalCode, period, UTC extract time, source server (name only).

---

## 11. First SQL sketches (Project 15)

Hospital:

```sql
SELECT p.SetupID, p.ProjectID, p.Name, p.Alias, p.OraganisationCode,
       p.IsActive, p.DefaultCurrencyID, p.ISOCityID
FROM dbo.STP_Project p
WHERE p.SetupID = @SetupId AND p.ProjectID = @ProjectId;
```

Clinics → departments:

```sql
SELECT c.ClinicID, c.ClinicDescription, c.GroupID, c.MappingCode, c.IsPrimaryClinic
FROM dbo.HIS_Clinic c
WHERE c.SetupID = @SetupId AND c.ProjectID = @ProjectId;
```

Procedures (non-drug):

```sql
SELECT ProcedureID, ProcedureName, CategoryID, GroupID, SubGroupID,
       Price, PublishedPrice, IsPackage, IsMedicine, IsSurgeryProcedure,
       ProcedureTime, OracleItemCode, IsActive
FROM dbo.HIS_Procedure
WHERE SetupID = @SetupId AND ProjectID = @ProjectId
  AND ISNULL(IsMedicine, 0) = 0;
```

One-month invoices (facts spine):

```sql
SELECT m.TransactionType, m.InvoiceNo, m.InvoiceDate, m.AppointmentNo, m.AdmissionNo,
       m.PatientType, m.PatientID, m.ClinicID, m.DoctorID, m.CompanyID,
       d.LineItemNo, d.ProcedureID, d.Price, d.Quantity, d.DiscountAmount,
       d.PatientShare, d.CompanyShare, d.CostCenterID, d.PrimaryDoctorID, d.PerformDate
FROM dbo.HIS_InvoiceMain m
JOIN dbo.HIS_InvoiceDetail d
  ON  d.SetupID = m.SetupID AND d.ProjectID = m.ProjectID
  AND d.TransactionType = m.TransactionType AND d.InvoiceNo = m.InvoiceNo
WHERE m.SetupID = @SetupId AND m.ProjectID = @ProjectId
  AND m.InvoiceDate >= @From AND m.InvoiceDate < @To;
```

Add `WITH (NOLOCK)` only if the HIS DBA agrees; otherwise `READ UNCOMMITTED` at session level is enough.

---

## 12. Success criteria for the first import

- `Hospital` row `RYN-UAT` exists.
- Departments ≈ Project 15 clinics + wards + OTs.
- Procedures load with `procType` in the allowed list (`PROCEDURE|SURGERY|CONSULT|LAB|IMAGING|BED_DAY|ER_VISIT|THERAPY`).
- One month: patients, encounters (OP/IP/ER), services, revenue, with `sourceRef` unique.
- Data import Validate reports **0 rejects** (or only documented skips).
- Re-importing the same zip is idempotent (unchanged counts, no duplicates).

Not in scope for that first import: a reconciling costing run, activity transactions, GL, resource rates.

---

## 13. Open items before writing `HisExtract`

1. Confirm Project 15’s name/city on `STP_Project` (probe).
2. Distinct `TransactionType` values actually used for OP vs IP invoices.
3. Appointment `Status` / `IsVisitClosed` for “real visit”.
4. Whether `HIS_InvoiceDetail.CostCenterID` is populated for Project 15 (if not, clinic fallback is mandatory).
5. How many `HIS_Procedure` rows belong to Project 15 (50k is all hospitals).
6. Placeholder physician resource (option 2) vs deferring `Provider`.
)
