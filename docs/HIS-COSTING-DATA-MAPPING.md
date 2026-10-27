# HISGENX → VERITA data mapping

How the existing Vida3 / HISGENX hospital information system can feed VERITA (this costing platform), table by table.

This document is a **schema mapping**, not an ETL spec. Live row counts and the extract/import plan (console util → zip → Data import) are in [`HIS-EXTRACT-PLAN.md`](HIS-EXTRACT-PLAN.md).

The HIS script (`Database/Vida3/HISGENX_script.sql`) is structure only. Column meanings below are inferred from names, foreign keys and stored-procedure usage. Row counts later showed several mapped tables are empty (local GL, OR log, pharmacy master, department setup); trust the extract plan for what to actually pull.

---

## 1. What this is about

VERITA is a hospital **activity-based costing** engine. It does not replace HIS. It takes:

1. **A costing model** the hospital designs (cost centres, pools, resources, recipes, allocation rules).
2. **Monthly facts** from source systems (ledger, HIS encounters, billed services, measured consumption, revenue).

HISGENX is a **clinical + billing** system. It is rich on patients, encounters, charge master, invoices, payers, labs, OR, rooms and doctors. It is **not** a cost ledger: it does not hold payroll cost, inventory unit cost, depreciation, or GL spend by cost centre.

**Practical consequence:** most of the *volume and catalogue* side of VERITA can be extracted from HIS. Most of the *money and model* side cannot. Those come from Oracle ERP / payroll / a costing workshop.

---

## 2. VERITA costing model (what we load into)

Loadable tables are declared in `src/Verita.Infrastructure/DataHub/EntitySpecs.cs`. The engine (`src/Verita.Engine`) consumes them in this order of ideas:

```
Organisation  →  Financial structure  →  Resources  →  Service catalogue
        ↓
Monthly facts: GL postings, driver volumes, patients, encounters,
               services delivered, activity transactions, revenue
        ↓
Eleven-stage run: validate → rates → build pools from GL → capacity
               → allocate support → allocate to activities
               → standard cost → actual / patient cost
               → roll-up → variance → reconcile to GL
```

### 2.1 Loadable tables

| Group | Table | What it is | Versioned? |
|---|---|---|---|
| Organisation | `Hospital` | Reporting entity and access scope | |
| Organisation | `Facility` | Building; floor area drives facility overhead | |
| Organisation | `ServiceLine` | Commercial roll-up shared across hospitals | |
| Organisation | `Department` | Clinical or support unit (`catalogCode` joins the same dept across hospitals) | |
| Organisation | `CostCenter` | Where ledger money lands (`stepDownSeq` = close order) | |
| Financial | `GLAccount` | Group chart of accounts | |
| Financial | `Period` | Accounting month | |
| Financial | `CostDriver` | Measurable cause of cost | yes |
| Financial | `CostPool` | Homogeneous bundle of cost at a centre | yes |
| Financial | `GLMapping` | Account + centre → pool (weights must total 1) | yes |
| Resources | `Resource` | Role, asset or supply item that costs money | |
| Resources | `ResourceRate` | Effective-dated unit cost | yes |
| Resources | `ResourceCapacity` | Practical minutes and annual cost (TDABC) | yes |
| Resources | `Provider` | Named physician pointing at a role resource | |
| Catalogue | `Activity` | Unit of work with standard duration | yes |
| Catalogue | `ActivityResource` | Standard recipe: what one activity consumes | |
| Catalogue | `ActivityDriver` | Driver an activity is measured by | |
| Catalogue | `Procedure` | Billable service (`catalogCode` joins across hospitals) | |
| Catalogue | `ProcedureRate` | List price | yes |
| Catalogue | `CostingVersion` | Named set of recipes | |
| Catalogue | `CostingDefinition` | Procedure bound to a version | |
| Catalogue | `ProcedureActivityLine` | Procedure recipe: activities | |
| Catalogue | `ProcedureResourceLine` | Procedure recipe: direct resources | |
| Catalogue | `Package` | Bundle of procedures at a contracted price | |
| Catalogue | `PackageComponent` | Procedure inside a package | |
| Catalogue | `Drg` | Diagnosis-related group | |
| Catalogue | `Payer` | Insurer / funder (`contractRate` as fraction of list) | |
| Catalogue | `AllocationRule` | How a pool is distributed (reciprocal / step-down / ABC) | yes |
| Catalogue | `AllocationRuleTarget` | Percentage targets for a rule | |
| Monthly | `GLPosting` | Trial balance by account, centre, month | |
| Monthly | `DriverVolume` | Measured driver quantity by centre and month | |
| Monthly | `Patient` | Patient master (HIS) | |
| Monthly | `Encounter` | Episode of care (month = admit month) | |
| Monthly | `EncounterService` | Service delivered on an encounter | |
| Monthly | `ActivityTransaction` | Measured resource consumption (actual-cost source of truth) | |
| Monthly | `RevenueTransaction` | Billed line | |
| Access | `User` | Sign-in (not from HIS) | |

Engine outputs (`EncounterCost`, procedure costs, residuals, etc.) are **calculated**, never imported from HIS.

### 2.2 Enumerations that matter for mapping

From `src/Verita.Domain/Catalogue.cs`:

| Field | Allowed values |
|---|---|
| `Department.deptType` | `CLINICAL`, `ANCILLARY`, `SUPPORT`, `ADMIN` |
| `CostCenter.ccType` | `PRODUCTION`, `ANCILLARY`, `SUPPORT`, `OVERHEAD` |
| `Procedure.procType` | `PROCEDURE`, `SURGERY`, `CONSULT`, `LAB`, `IMAGING`, `BED_DAY`, `ER_VISIT`, `THERAPY` |
| `Encounter.encType` | `IP`, `OP`, `ER`, `DAYCASE` |
| `Payer.payerType` | `GOVERNMENT`, `INSURANCE`, `CORPORATE`, `SELF_PAY` |
| `Resource.resourceType` | `LABOR_PHYSICIAN`, `LABOR_NURSE`, `LABOR_TECH`, `LABOR_ALLIED`, `DRUG`, `CONSUMABLE`, `IMPLANT`, `BLOOD`, `EQUIPMENT`, `ROOM`, `UTILITY`, `IT_SERVICE`, `FACILITY` |
| `Patient.segment` | `PEDIATRIC`, `ADULT`, `GERIATRIC`, `MATERNITY` |

Default HIS source-system labels already in the domain: `VIDA-HIS` (services), `CYCLUS-RCM` (revenue), `ERP-GL` (ledger).

### 2.3 What a costing run actually needs from HIS vs elsewhere

| Engine input | HIS can supply? |
|---|---|
| Hospital, departments, a first cut of cost centres | Yes, with design |
| Patient, encounter, billed procedure, payer, DRG, package | Yes |
| List prices and billed revenue | Yes |
| Procedure catalogue | Yes |
| Measured consumption (`ActivityTransaction`) | Partially — times and quantities exist; they must be **shaped** into TDABC rows |
| Driver volumes | Derived from HIS facts |
| GL trial balance by cost centre | **No** — Oracle ERP |
| Resource unit costs and capacity annual cost | **No** — payroll / ERP / inventory |
| Cost pools, GL mapping, allocation rules, recipes | **No** — costing model design |

---

## 3. HISGENX structure (what we extract from)

| | |
|---|---|
| Script | `Database/Vida3/HISGENX_script.sql` |
| Size | ~43 MB, ~505k lines, **UTF-16LE** |
| Objects | 1,455 tables (all `dbo`), 74 views, 2,717 procedures, 1,637 functions, 998 FKs |
| Data | None in this script |
| Tenancy | Almost every table is keyed by `SetupID char(6)` (organisation) + `ProjectID smallint` (hospital / branch) |

### 3.1 Module prefixes

| Prefix | ~tables | Module |
|---|---|---|
| `HIS_` / `His_` | ~720 | Outpatient, billing, payer, doctor, EMR |
| `INP_` / `Inp_` | ~260 | Inpatient, ER, OR, beds |
| `LAB_` / `Lab_` | ~110 | Laboratory and blood bank |
| `STP_` | ~95 | Setup and lookup |
| `EMR_` / `EHR_` / `RRT_` | ~75 | Clinical forms |
| `IVF_` | 36 | IVF |
| `HHC_` | 21 | Home health |
| `DEN_` | 13 | Dental |
| `GLD_` | 9 | Local general ledger (not the real ERP) |
| `PHR_` | 5 | Pharmacy **master only** |
| `NPHIES_*` | — | Saudi e-claims |
| `ERP_*` | — | Oracle interface procedures |

### 3.2 Conventions that affect every extract

- **Hospital identity is `(SetupID, ProjectID)`.** Every join must carry both. Map this pair to VERITA `Hospital.Code` once and keep it stable.
- **Catalogues are per hospital.** `HIS_Procedure`, `HIS_Clinic`, `HIS_Doctor` all include `ProjectID`. The same `ProcedureID` can mean different things in different hospitals. VERITA `catalogCode` is the cross-hospital join key — populate it from a group-level code (CPT, Oracle item, mapping code) where one exists.
- **Money is `float`.** Convert to `DECIMAL(19,4)` on load; expect noise.
- **Dates are mostly `smalldatetime`.** Appointment and OR times are often `varchar`.
- **Enumerations live in `STP_Parameter`**, which has no data in this script. Codes such as `PatientType`, `TransactionType`, `OrderType`, `ProcClassType` need the live database or a data dictionary.
- **Pharmacy sales/stock tables are not in this database.** Procedures reference `PHR_SalesMain`, `PHR_SalesDetail`, `PHR_Item`, `PHR_InPatientCharges`, `PHR_InventoryStock` — they live elsewhere.
- **The real GL is Oracle**, not `GLD_*`. Many HIS tables have `IsPosted2GL` and `Oracle*` mapping columns.

### 3.3 Encounter types (inferred from procedures)

| HIS object | Encounter type code (inferred) | VERITA `encType` |
|---|---|---|
| `HIS_Appointment` | 1 | `OP` (or `DAYCASE` when admission type says so) |
| `INP_Admission` | 2 | `IP` |
| `INP_ERAssessment` | 3 | `ER` |

Invoice `TransactionType` (inferred): **2 = outpatient invoice**, **75 = inpatient invoice**. Confirm on live data.

### 3.4 Patient → encounter → charge spine

```
HIS_Patient (SetupID, ProjectID, PatientType, PatientID)
  ├─ OP:  HIS_Appointment (AppointmentNo)
  │         └─ HIS_ProgNoteEpisode / HIS_ProgNoteOrder
  ├─ ER:  INP_ERAssessment (AssessmentNo)
  └─ IP:  INP_AdmissionRequest → INP_Admission (AdmissionNo)
            ├─ INP_Transfer / INP_NursingTransfer
            ├─ INP_ProgNoteOrder
            ├─ INP_OTReservation → INP_ORLogInfo
            └─ INP_DischargeSummary / INP_AdmissionDRGList

HIS_InvoiceMain (TransactionType, InvoiceNo;
                 AppointmentNo or AdmissionNo;
                 ClinicID, DoctorID, CompanyID)
  └─ HIS_InvoiceDetail (LineItemNo, ProcedureID, CostCenterID,
                        Quantity, Price, shares, OrderNo)
        └─ HIS_Procedure (Category / Group / SubGroup)
```

`HIS_InvoiceDetail` is the **central fact table** for billed activity and revenue. Cost-centre on the line is filled by `fn_GetCostCenterNew` from `HIS_RuleCostCenter*`.

---

## 4. How to read the mapping tables

| Extractability | Meaning |
|---|---|
| **Direct** | One HIS table (or a thin union) → one VERITA table. Light transforms only. |
| **Derived** | Join / aggregate HIS facts into VERITA shape. Logic required, no new design. |
| **Seed** | HIS can populate a first catalogue; costing still decides codes, types and recipes. |
| **Design** | VERITA construct. HIS has no equivalent. Workshop + finance. |
| **External** | Required for costing, but **not in this HIS database** (Oracle, HR, pharmacy DB). |
| **Out of scope** | Not an input to costing (users, audit, clinical notes). |

PHI: `Patient.fullName` and `nationalIdMasked` exist in VERITA. Mask national ID on extract. Do not bring addresses, phones, or clinical-note text.

---

## 5. Organisation

### 5.1 `Hospital` — Direct

| VERITA column | HIS source | Notes |
|---|---|---|
| `code` | `STP_Project.OraganisationCode` or `Alias`, else `SetupID`-`ProjectID` | Must be unique and stable |
| `name` | `STP_Project.Name` | |
| `city` | `STP_City` via `STP_Project.ISOCityID` | |
| `currency` | `STP_Currency` via `DefaultCurrencyID` | Default `SAR` |
| `bedCount` | `COUNT(*)` of `INP_Bed` where active | Or a constant from ops |
| `isActive` | `STP_Project.IsActive` | Filter `CloseDate` |

`HIS_Hospital` is a **partner / referral hospital** list (`HospitalID` on `HIS_Procedure`), not the reporting entity. Do not use it as `Hospital`.

One VERITA hospital = one `(SetupID, ProjectID)`. If several projects are one legal hospital, collapse them here and keep a crosswalk.

### 5.2 `Facility` — Seed

| VERITA column | HIS source | Notes |
|---|---|---|
| `hospitalCode` | Project → Hospital | |
| `code` / `name` | `INP_Building` (and `HIS_Building` for OPD) | One row per building |
| `facilityType` | Infer from name / `INP_Building` flags | e.g. MAIN, ANNEX |
| `squareMetres` | **Not in HIS** | Needed for facility overhead driver — survey or FM system |

### 5.3 `ServiceLine` — Seed (group-level)

HIS has no commercial service-line master. Candidates:

| Candidate | Table | Use as |
|---|---|---|
| Clinic group | `HIS_ClinicGroup.GroupDescription` | Operational roll-up |
| Procedure category | `HIS_ProcedureCategory` | Charging hierarchy (Lab, Radiology, Surgery…) |
| Department group | `STP_DepartmentGroup` | Setup-level, not per hospital |

**Recommendation:** define VERITA `ServiceLine` once for the group (e.g. `SL-IP`, `SL-OP`, `SL-ER`, `SL-SURG`, `SL-DIAG`, `SL-PHARM`) and map `HIS_Clinic.GroupID` / procedure category onto it. Do not explode one service line per clinic.

### 5.4 `Department` — Seed from clinics + inpatient units

HIS “department” is split across several masters. VERITA wants one department per clinical/support unit per hospital.

| HIS table | Maps to department of | Suggested `deptType` |
|---|---|---|
| `HIS_Clinic` + `HIS_ClinicGroup` | Outpatient / admitting specialty | `CLINICAL` |
| `INP_NursingStation` | Ward / ICU / HDU | `CLINICAL` |
| `INP_OTMain` | Operating theatre | `CLINICAL` |
| `LAB_Division` / `LAB_Section` | Laboratory | `ANCILLARY` |
| Radiology | **No RIS master** — use clinics / procedure categories named radiology | `ANCILLARY` |
| Admin / finance / HR | Not in HIS | `ADMIN` / `SUPPORT` — add manually |

| VERITA column | HIS source |
|---|---|
| `code` | `{HospitalCode}.{ClinicID}` or `{HospitalCode}.NS-{NursingStationID}` |
| `catalogCode` | `HIS_Clinic.MappingCode` if filled; else clinic/group name slug |
| `name` | `ClinicDescription` / `INP_NursingStation.Description` |
| `specialty` | `HIS_DoctorSpeciality` of the HOD, or clinic name |
| `serviceLineCode` | Map from clinic group (see 5.3) |

`STP_Department` exists but has **no `ProjectID`** and no FK to clinics. Treat it as a setup dictionary, not the operational department list.

### 5.5 `CostCenter` — Seed + design (two HIS trees)

HIS already has cost centres, but they are not a clean ABC structure.

| HIS object | What it is |
|---|---|
| `STP_CostCenter` | Setup-level, linked to `STP_Department`. No `ProjectID`. Not linked to clinics. |
| `HIS_CostCenterSetup` | Hierarchical ERP tree (`CostCenterID varchar(50)`, `ParentCostCenter`). Appears to be the **Oracle** cost-centre list. |
| `HIS_RuleCostCenter` / `HIS_RuleCostCenterNew` | Maps `(RuleType OPD/IP, ClinicID, ProcClass C/G/S/P, ProcClassID)` → `CostCenterID`. Used when posting invoice lines. |
| `HIS_InvoiceDetail.CostCenterID` | The **applied** cost centre on each billed line — gold for revenue by centre. |
| `INP_NursingStation.OracleUnitID` / `MappingCode` | Inpatient ward as ERP unit. |
| `HIS_Clinic.CreditAccountID` / `DebitAccountID` | GL accounts on the clinic, not a cost centre. |

**Recommendation:**

1. Start VERITA `CostCenter` from `HIS_CostCenterSetup` (Oracle codes), so GL postings can land later without a second crosswalk.
2. Attach each centre to a VERITA `Department` using `HIS_RuleCostCenterNew` (clinic / nursing station / procedure class).
3. Classify `ccType` and `stepDownSeq` in a workshop (`PRODUCTION` clinical, `ANCILLARY` lab/rad/pharmacy, `SUPPORT` / `OVERHEAD` for admin). HIS does not store ABC close order.
4. Add centres HIS does not have (facilities, utilities, IT) by hand.

---

## 6. Financial structure (mostly not HIS)

### 6.1 `GLAccount` — External (Oracle), weak fallback in HIS

| Source | Use |
|---|---|
| Oracle chart of accounts | **Authoritative.** VERITA `naturalClass` (`SALARY`, `DRUG`, `CONSUMABLE`, …) and `costClass` (`DIRECT`/`INDIRECT`) are costing tags — assign them in finance. |
| `GLD_Account` | Local HIS chart (`AccountKey`, `AccountName`, parent, `STP_AccountType`). Usable only if this hospital actually journals in HIS. Procedures show posting goes to Oracle. |
| `HIS_ProcedureSubGroup.GLAccountIDCash` / `GLAccountIDCredit` | **Revenue** accounts by procedure subgroup, not expense. Useful later for revenue GL, not for `GLPosting`. |

### 6.2 `Period` — Design / External

Create calendar months (`2026-09`, start/end/days). Optionally align to `GLD_AccountingPeriod` if that table is used. Status `OPEN` / closed is a costing operations field, not HIS.

### 6.3 `CostDriver` — Design; HIS feeds volumes later

Drivers are a costing construct (`DRV-HEAD`, `DRV-SQM`, `DRV-OR-MIN`, `DRV-BEDDAY`, `DRV-WAU`). HIS does not define them.

HIS **can** supply the quantities that become `DriverVolume` (section 10.2). Set `sourceSystem` / `sourceField` / `formula` on each driver to the HIS table used, so volumes are auditable (FR-1.12).

Examples:

| Driver | Unit | HIS source field |
|---|---|---|
| Bed days | `DAY` | `INP_BedCensus` or `INP_Transfer.Duration` |
| OR minutes | `MIN` | `INP_ORLogInfo` / `INP_OTReservation` actual surgery times |
| Clinic visits | `TXN` | `HIS_Appointment` where visit closed |
| Lab tests | `TEST` | `LAB_OrderDetail` performed |
| Headcount | `HEADCOUNT` | `HIS_Employee` or roster — weak; prefer HR |
| Floor area | `SQM` | Not in HIS |

### 6.4 `CostPool` / `GLMapping` / `AllocationRule*` — Design

No HIS equivalent. These are the ABC model:

- Pools sit on a cost centre (`DIRECT` staff vs `INDIRECT` overhead).
- `GLMapping` sends each (account, centre) posting into pools (weights = 1).
- `AllocationRule` moves support pools (stage 1 reciprocal, 2 step-down, 3 onto activities).

Do not invent these from HIS clinics. Build them with finance against the Oracle trial balance.

---

## 7. Resources and providers

### 7.1 `Resource` — Seed from several HIS masters + design

VERITA resources are **what money is spent on and what patients consume**. HIS has people, rooms, drugs and items, but not a unified resource file.

| `resourceType` | HIS seed | Capacity-driven? |
|---|---|---|
| `LABOR_PHYSICIAN` | One resource per **role/grade**, not per doctor. Seed grades from `HIS_Doctor.EmploymentType` / specialty. Named doctors go to `Provider`. | Yes |
| `LABOR_NURSE` / `LABOR_TECH` / `LABOR_ALLIED` | `HIS_Employee` (`PositionTitle`, `JobFamily`, `DepartmentName`, `CostCenter`) + `HIS_ROTAShift`. Incomplete — prefer HR. | Yes |
| `ROOM` | `INP_RoomCategory` (ward/ICU bed-day), `INP_OTMain` (OR minute), `HIS_Room` (consult room) | Yes |
| `EQUIPMENT` | `LAB_Equipment` is **interface config**, not an asset register. No modality master for radiology. **External (FA / CMMS).** | Yes |
| `DRUG` | `PHR_Medicine` (+ `PHR_MedicineCategory/Group/SubGroup`). `HIS_Procedure` rows with `IsMedicine = 1` / `MedicineId`. | No |
| `CONSUMABLE` | `HIS_ProcedureMaterialMapping` → `HIS_ItemMaterialMapping`; `INP_PatientConsumedItem`; `HIS_FloorConsumption.ItemId` | No |
| `IMPLANT` | `INP_ImplantDetail` (description, qty) — item master is thin | No |
| `BLOOD` | `LAB_Blood*` / blood-bank product list | No |
| `UTILITY` / `IT_SERVICE` / `FACILITY` | Not in HIS | Yes / No |

`HIS_Doctor.BelongToPayroll`, `IsExternalDoctor`, and truncated salary-like columns exist — confirm on live data before treating them as cost.

### 7.2 `ResourceRate` — External

Unit cost is payroll loaded rate, drug purchase price, or `annualCost / practicalMinutes`. **Not in HIS.** Closest false friends:

- `HIS_Procedure.Price` — **charge**, not cost
- `HIS_Doctor.ConsultationFee` — fee, not salary
- `HIS_IncomeShareDoctor` — revenue share to the doctor, not employment cost

### 7.3 `ResourceCapacity` — Partial seed

| Field | HIS | Gap |
|---|---|---|
| Theoretical minutes | `HIS_DoctorBasicSchedule` / `HIS_DoctorCalendarSchedule`; `HIS_ROTADetails`; bed count × period days × 24 × 60 for `ROOM` | Need practical ratio (default 0.85) |
| Annual cost | **External** (payroll, depreciation) | Without this, TDABC rates cannot be computed from capacity |
| Equipment minutes | Not in HIS | |

OR staff mix on `INP_ORLogInfo` (`PrimaryDoctorID`, `AnesthetistID`, `ScurbNurseID`, `CirculatingNurseID`) is useful to **check** utilisation, not to set capacity.

### 7.4 `Provider` — Direct

| VERITA column | HIS source |
|---|---|
| `code` | `{HospitalCode}.DOC-{DoctorID}` |
| `name` | `HIS_Doctor.DoctorName` |
| `specialty` | `HIS_DoctorSpeciality.SpecialityID` (decode via `STP_Parameter` on live DB) |
| `grade` | `EmploymentType` or payroll grade if present |
| `resourceCode` | Map doctor → **role resource** (consultant, specialist, resident…) |
| `departmentCode` | Primary clinic from `HIS_DoctorClinic` (`IsPrimary` / first clinic) or `HIS_Doctor` home clinic |

Skip `IsExternalDoctor = 1` from capacity, or map them to a separate locum resource.

---

## 8. Service catalogue

### 8.1 `Procedure` — Direct (filter the charge master)

`HIS_Procedure` is a **unified charge master**: consults, labs, imaging, surgeries, room-and-board, medicines, packages. PK `(SetupID, ProjectID, ProcedureID varchar(12))`.

| VERITA column | HIS source | Transform |
|---|---|---|
| `code` | `{HospitalCode}.{ProcedureID}` | Per-hospital unique |
| `catalogCode` | `HIS_ProcedureCpt.CPTCodeID`, `HIS_ProcedureCodeTypeMapping.CodeID`, or `OracleItemCode` | Required to join the same service across hospitals |
| `name` | `ProcedureName` (Arabic twin `ProcedureNameN` — ignore or store elsewhere) | |
| `category` | `HIS_ProcedureCategory` / Group / SubGroup | |
| `procType` | Infer from category, flags | See table below |
| `departmentCode` | Performing clinic / `HIS_ProcedureClinicMapping` / lab section | |
| `serviceLineCode` | From category map (5.3) | |
| `complexity` | `SurgerySeverity` if surgery, else default `3` | 1–5 |
| `isActive` | `IsActive` if present, else not cancelled | |

**`procType` inference (confirm on live category names):**

| HIS signal | `procType` |
|---|---|
| `IsSurgeryProcedure = 1` | `SURGERY` |
| Category / name like consult / clinic visit | `CONSULT` |
| `LAB_TestMain.ProcedureID` match | `LAB` |
| Radiology category, or row in `HIS_RadReport` | `IMAGING` |
| `INP_Room.ProcedureID` / room-and-board items | `BED_DAY` |
| ER clinic / triage visit procedure | `ER_VISIT` |
| `IsMedicine = 1` | Do **not** load as a procedure — map to `Resource` type `DRUG` |
| `IsPackage = 1` | Load as `Package`, not `Procedure` |
| Therapy / physio category | `THERAPY` |
| Else | `PROCEDURE` |

Related masters to keep as crosswalks (not VERITA tables): `HIS_ProcedureIcd10`, `HIS_ProcedureLoinc`, `HIS_ProcedureDRGMapping`, `HIS_ProcedureCodeTypeDRGMapping`.

### 8.2 `ProcedureRate` — Direct

| Priority | HIS table | Columns |
|---|---|---|
| 1 (cash / default list) | `HIS_Procedure.Price` / `PublishedPrice` / `GrossPrice` | Use `PublishedPrice` as list if populated |
| 2 (payer-group list) | `HIS_ProcedurePrice` | PK `(CompanyGroupID, ProcedureID)`, `Price`, `EffectiveDate`, `EndDate` |
| 3 (inpatient tariff) | `INP_TariffProcedureDetail` | `Price`, `PublishedPrice`, `GrossPrice`, `SpecialPrice` by `TariffID` |
| 4 (MOH) | `HIS_MOHProcedurePrice` | If used |

VERITA stores **one list price per procedure per hospital**, effective-dated. Payer discounts belong on `Payer.contractRate` and on `RevenueTransaction`, not as extra procedure rates.

`EffectiveFrom` = `HIS_ProcedurePrice.EffectiveDate` (or a convention such as contract start). Close the previous version the day before.

### 8.3 `Package` / `PackageComponent` — Direct

| VERITA | HIS |
|---|---|
| `Package` | `HIS_Procedure` where `IsPackage = 1`, plus `HIS_SpecialPackageInfo` / `HIS_PPCMain` if those are the commercial packages |
| `code` / `name` / `price` | ProcedureID, name, package price |
| `coveredLOS` | `HIS_Procedure.LOS` |
| `PackageComponent` | `HIS_ProcedurePackageDetail` (`ProcedureID` parent → component `ProcedureID`, qty) |
| Component prices | `HIS_ProcedurePackageDetailPrice` (`ActualPrice`, `DiscountedPrice`) — informational; VERITA costs components via recipes |

`HIS_InvoicePackageItem` is the **transactional** breakdown (goes toward `EncounterService` / revenue), not the catalogue.

### 8.4 `Drg` — Direct

| VERITA | HIS |
|---|---|
| `code` | `HIS_DRGSpecification.DRGCode` (PK also has `DRGCodeType`) |
| `name` | Specification description if present |
| `relativeWt` | `DRGWeight` |
| `expectedLOS` | `AverageLOS` |
| `baseRate` | `DRGBaseRate` |
| `mdc` | Not obvious — may sit on ICD chapter or a type column |

Per-admission assigned DRG: `INP_AdmissionDRGList` (`DRGCode`, `DRGWeight`, `ALOS`, `DRGTotalPayment`) → `Encounter.drgCode`. Grouper log: `HIS_DRGLog`.

### 8.5 `Payer` — Direct

HIS hierarchy is **Group → Company → SubCategory (plan)**. VERITA has a single `Payer`.

| Choice | When |
|---|---|
| One payer per `HIS_Company` | Default. `code` = company code / `TPACode`. |
| One payer per `HIS_CompanyGroup` | If costing only cares about pricing scheme. |
| One payer per `HIS_CompanySubCategory` | Only if plans have very different `contractRate`. Usually too grainy. |

| VERITA column | HIS |
|---|---|
| `name` | `HIS_Company` name |
| `payerType` | Map `CompanyType` / name: MOH / NPHIES government → `GOVERNMENT`; insurer → `INSURANCE`; cash / self → `SELF_PAY`; employer → `CORPORATE` |
| `contractRate` | From `HIS_RuleCompany` / `HIS_RuleCompanyGroup` (`CompanyShare` as fraction of price), or compute from invoices: `SUM(CompanyShare)/SUM(Price)` |

Cash patients: introduce a synthetic `PAY-SELF` and use it when `CompanyID` is null or patient pay type is cash.

`HIS_TPACompany.PayerId` is the TPA identifier — useful as `catalog`-style code, not a second payer dimension.

### 8.6 `Activity` / recipes / `CostingVersion` — Design, HIS can seed durations

VERITA activities are **work steps** (e.g. “plain X-ray”, “OR minute”, “ward nursing day”), not charge codes. Recipes (`ActivityResource`, `ProcedureActivityLine`, `ProcedureResourceLine`) are the costing model.

HIS duration and BOM hints to **seed**, not to publish blindly:

| Hint | Table | Use |
|---|---|---|
| Procedure time | `HIS_Procedure.ProcedureTime`, `MinTimeRequired` | `Activity.stdDuration` / `minDuration` |
| Duration | `HIS_ProcedureDuration` (`DurationDays`, `DurationHours`) | LOS-type activities |
| Bill of materials | `HIS_ProcedureMaterialMapping` + `HIS_ItemMaterialMapping` (`MaterialID`, `Quantity`) | `ProcedureResourceLine` for consumables |
| Dental kits | `HIS_DentalCaseKit*` | Same idea |
| Nursing services | `HIS_NursingService` | Possible nursing activities |
| Lab TAT | `LAB_TestMain.AverageTATMinutes` | Not the same as tech minutes, but a starting number |

`CostingVersion` (e.g. `CV-2026-H2`) is created in VERITA Governance. HIS has no versioned recipe.

**Suggested first recipes (to get a run standing):**

- CONSULT / ER_VISIT → activity “visit” consuming physician + nurse minutes
- LAB → activity “perform test” consuming tech minutes (+ reagent resource if known)
- IMAGING → activity “exam” consuming tech + room/equipment minutes
- SURGERY → activities “OR time”, “anaesthesia”, “recovery” from OT log durations
- BED_DAY → activity “ward day” consuming nurse minutes + room day
- Drugs / implants → `ProcedureResourceLine` (or encounter-level `ActivityTransaction`) with quantity from the charge line

Until recipes exist, `DQ-PROC-UNMAPPED` will fire for every delivered procedure.

---

## 9. Monthly clinical facts (the HIS gold)

These are the tables VERITA already describes as coming from HIS / RCM.

### 9.1 `Patient` — Direct

| VERITA | HIS `HIS_Patient` | Transform |
|---|---|---|
| `mrn` | `PatientUID` if globally unique, else `{HospitalCode}-{PatientID}` | PK also includes `PatientType` — include it in the key if the same ID is reused |
| `hospitalCode` | Project map | Patient is **per hospital** |
| `fullName` | Concatenated name columns | PHI |
| `birthYear` | `YEAR(DateofBirth)` | Do not store full DOB |
| `sex` | `Gender` → `M` / `F` | |
| `segment` | Age at period end + maternity flag | `PEDIATRIC` / `ADULT` / `GERIATRIC` / `MATERNITY` |
| `nationalIdMasked` | `PatientIdentificationNo` masked | e.g. first + last digit, rest bullets |

Do not load `HIS_PatientDemographic` addresses, phones, or ID scans.

Payer on the patient (`HIS_PatientCompanyInfo`) is coverage, not the encounter payer — prefer the invoice / admission company.

### 9.2 `Encounter` — Derived (union of three spines)

Build **one encounter row per episode**. Suggested `encounterNo` (must be unique per hospital):

| Spine | `encounterNo` | `encType` | `admitAt` / `dischargeAt` |
|---|---|---|---|
| OP | `{Hosp}-OP-{AppointmentNo}` | `OP` | `ActualVisitStartTime` / `EndTime`, else appointment date + `StartTime`/`EndTime` |
| ER | `{Hosp}-ER-{AssessmentNo}` | `ER` | `ArrivalTime` / `DischargeTime` or `SignIn`/`SignOut` |
| IP | `{Hosp}-IP-{AdmissionNo}` | `IP` | `AdmissionDate` / `DischargeDate` |
| Day case | Same as IP or OP depending on `AdmissionType` | `DAYCASE` | Confirm flag on live data |

| VERITA column | HIS |
|---|---|
| `patientMrn` | Patient map |
| `departmentCode` | OP: `HIS_Appointment.ClinicID`; IP: `AdmittingClinicID` or current `INP_NursingStation`; ER: `INP_ERAssessment.ClinicID` |
| `serviceLineCode` | From that department |
| `payerCode` | IP: `INP_Admission.CompanyId`; OP: `HIS_Appointment.CompanyID`; else invoice `CompanyID`; else `PAY-SELF` |
| `drgCode` | `INP_AdmissionDRGList.DRGCode` (IP only) |
| `packageCode` | If admission / invoice is a package procedure |
| `providerCode` | Attending / admitting `DoctorID` |
| `lengthOfStay` | IP: `INP_DischargeSummary.DaysStayed` or datediff; OP/ER: `0` |
| `isSurgical` | True if any `INP_OTReservation` / surgery procedure on the stay |
| `acuity` | ER triage (`HIS_EncounterTriageAssessment` / `His_ErPatientTriage*`) → map to `ROUTINE` or a hospital scale; default `ROUTINE` |

Filter: `IsVisitClosed` / discharged / not cancelled. Encounter **belongs to the month of admit** (VERITA rule), including stays that discharge next month.

`HIS_ProgNoteEpisode` (`EpisodeID`, `AppointmentNo`, `AdmissionNo`) can stitch OP → IP conversions. Prefer **admission** as the surviving encounter and keep the OP appointment as services on it, or emit two encounters — decide once and document. Recommendation: **separate encounters** (OP then IP) unless the business treats conversion as one episode.

### 9.3 `EncounterService` — Derived (prefer invoice lines)

**Primary source: `HIS_InvoiceDetail`** joined to `HIS_InvoiceMain`.

| VERITA | HIS |
|---|---|
| `encounterNo` | From `AppointmentNo` or `AdmissionNo` on `HIS_InvoiceMain` (ER via `INP_ERAssessment.InvoiceNo`) |
| `procedureCode` | `ProcedureID` → VERITA procedure (skip medicines / packages — see 8.1) |
| `costCentreCode` | `HIS_InvoiceDetail.CostCenterID` → VERITA cost centre. Fallback: performing clinic / nursing station rule |
| `providerCode` | `PrimaryDoctorID` |
| `quantity` | `Quantity` |
| `performedAt` | `PerformDate`, else invoice date |
| `sourceSystem` | `VIDA-HIS` |
| `sourceRef` | `{TransactionType}-{InvoiceNo}-{LineItemNo}` (unique; DQ-TXN-DUPLICATE) |

**Also pull unbilled but performed work** (otherwise cost without a service, or missing clinical activity):

| Source | When |
|---|---|
| `HIS_ProgNoteOrder` / `INP_ProgNoteOrder` where `PerformedStatus` done and `IsInvoiced = 0` | Performed, not yet billed |
| `LAB_OrderDetail` performed | Lab; `LAB_TestMain.ProcedureID` is the charge code |
| `HIS_RadReport` | Imaging; keyed by invoice line |
| `INP_OTProcedureMapping` | Surgery procedures on the reservation |
| Bed days | Synthesize `BED_DAY` quantity = LOS (or census days) on the ward cost centre |

Deduplicate on `sourceRef`. If the same order is invoiced, keep the **invoice** line and drop the order copy.

Include `HIS_InvoicePackageItem` exploded components if the header is a package — VERITA costs components, not the bundle (bundle sits on `Encounter.packageCode`).

### 9.4 `RevenueTransaction` — Direct from invoice lines

| VERITA | HIS |
|---|---|
| `encounterNo` | Same bridge as 9.3 |
| `procedureCode` | `ProcedureID` (optional on VERITA — keep it) |
| `payerCode` | `HIS_InvoiceMain.CompanyID` |
| `grossAmount` | `Price * Quantity` (or line gross) |
| `discount` | `DiscountAmount` + waived + (gross − patient − company) reconciling items |
| `netAmount` | `PatientShare + CompanyShare` (or `InvoiceNet` allocated to the line) |
| `postedAt` | `InvoiceDate` |
| `sourceSystem` | `VIDA-HIS` (or `CYCLUS-RCM` if a downstream RCM is still in play — here HIS **is** the bill) |
| `sourceRef` | Same as invoice line, prefix `REV-` if you also store the service with the raw key |

Credit notes / refunds: `HIS_InvoiceCreditDetail`, `HIS_RefundDetail`, `HIS_ReturnDetail` as **negative** revenue with their own `sourceRef`. Do not silently net them unless finance says so.

Claims (`HIS_Claim*`, `HIS_Settlement*`) are **cash collection**, not the revenue fact VERITA needs for profitability. Use settlement later for a collections view, not for `RevenueTransaction.netAmount`.

### 9.5 `ActivityTransaction` — Derived (this is the actual-cost feed)

VERITA actual costing reads **measured consumption**, not the invoice. HIS rarely stores “activity X consumed resource Y”. We have to **manufacture** TDABC rows from clinical events.

Every row needs: encounter, optional service `sourceRef`, `activityCode`, `resourceCode`, `costCentreCode`, `quantity`, `uom`, `occurredAt`, `sourceSystem`, `sourceRef` **including the activity** (CR-10).

| Clinical event | Quantity / UOM | Resource to attach | `sourceRef` idea |
|---|---|---|---|
| OP visit with actual start/end | Minutes | Physician role (+ nurse if known) | `APPT-{AppointmentNo}-CONSULT-PHYS` |
| ER assessment in/out | Minutes | ER physician / nurse | `ER-{AssessmentNo}-TRIAGE-NURSE` |
| `INP_ORLogInfo` surgery start/end | Minutes | OR room + surgeon + anaesthetist + scrub + circulating | `OR-{ReservationID}-SURG-{role}` |
| Anaesthesia start/end | Minutes | Anaesthetist resource | `OR-{ReservationID}-ANES` |
| `INP_BedCensus` / transfer duration | Days or minutes | Ward `ROOM` + nurse resource | `BED-{AdmissionNo}-{Ward}-{Date}` |
| `HIS_FloorConsumption` | `UnitQty` | Consumable resource | `FLR-{Encounter}-{ItemId}-{line}` |
| `INP_PatientConsumedItem` / `INP_ConsumableItemLog` | `Quantity` | Consumable | `CON-{AdmissionNo}-{ProcedureID}` |
| `INP_ImplantDetail` | `Quantity` | Implant | `IMP-{ReservationID}-{Barcode}` |
| `HIS_ProgNoteMedicine` / `INP_Prescription` dispensed | `DispensedQuantity` | Drug resource | `MED-{OrderNo}-{MedicineCode}` |
| `HIS_ProgNoteMedicineAdministrationLog` wastage | Wasted qty | Drug | `WST-…` |
| Lab performed | 1 `TEST` or TAT minutes | Lab tech (+ reagent if mapped) | `LAB-{OrderNo}-{Line}` |
| Radiology `StudyConductedOn`–`StudyEndTime` | Minutes | Radiographer + room | `RAD-{InvoiceNo}-{Line}` |

If time stamps are missing, **fall back to standard duration** from `Activity.stdDuration` so a run can complete — tag `sourceSystem` as `VIDA-HIS-STD` to distinguish measured vs standard.

Pharmacy **dispense cost** needs `PHR_Sales*` / inventory (external DB). Without it, drug actuals are quantity × `ResourceRate` from ERP purchase price.

---

## 10. Monthly financial facts

### 10.1 `GLPosting` — External (Oracle)

VERITA needs trial balance: **account × cost centre × month × amount**.

| HIS table | Why it is not enough |
|---|---|
| `GLD_JournalDetail` | Debit/credit by `AccountID` only — **no cost centre** |
| `GLD_AccountBudget` | Budget vs actual by account and period — still no centre |
| `HIS_InvoiceMain.IsPosted2GL` | Flag that revenue was sent to Oracle, not the expense TB |

Extract from Oracle (HIS already has `Oracle_ORA_ORG_Code`, `OracleItemCode`, `OracleUnitID` as hooks). After Oracle land, map Oracle CC → VERITA `CostCenter` using `HIS_CostCenterSetup`.

### 10.2 `DriverVolume` — Derived from HIS (and HR / FM)

Aggregate HIS facts for the period, by VERITA cost centre:

| Driver | Sketch |
|---|---|
| Visits | Count closed `HIS_Appointment` by clinic → cost centre |
| Bed days | Sum `INP_BedCensus` occupied, or sum transfer `Duration`, by nursing station |
| OR minutes | Sum actual surgery minutes by `INP_OTMain` |
| Lab tests | Count performed `LAB_OrderDetail` by `LAB_Section` |
| Imaging exams | Count `HIS_RadReport` / imaging invoice lines |
| Meals | Count `INP_MealOrder` if catering is a pool |
| Headcount | **HR**; `HIS_ROTADetails` unique `MemberID` as fallback |
| Square metres | **FM**, not HIS |

`activityCode` on `DriverVolume` is optional — fill it when the driver is activity-specific.

---

## 11. Tables by extractability (cheat sheet)

### 11.1 Extract from HIS into VERITA

| VERITA table | Extractability | Principal HIS tables |
|---|---|---|
| `Hospital` | Direct | `STP_Project` |
| `Facility` | Seed | `INP_Building`, `HIS_Building` |
| `Department` | Seed | `HIS_Clinic`, `HIS_ClinicGroup`, `INP_NursingStation`, `INP_OTMain`, `LAB_Division` |
| `CostCenter` | Seed | `HIS_CostCenterSetup`, `HIS_RuleCostCenterNew`, `HIS_InvoiceDetail.CostCenterID` |
| `Provider` | Direct | `HIS_Doctor`, `HIS_DoctorSpeciality`, `HIS_DoctorClinic` |
| `Procedure` | Direct | `HIS_Procedure` (+ category/group/subgroup, CPT/Oracle codes) |
| `ProcedureRate` | Direct | `HIS_ProcedurePrice`, `HIS_Procedure.Price`, `INP_TariffProcedureDetail` |
| `Package` | Direct | `HIS_Procedure` (`IsPackage`), `HIS_ProcedurePackageDetail` |
| `PackageComponent` | Direct | `HIS_ProcedurePackageDetail` |
| `Drg` | Direct | `HIS_DRGSpecification` |
| `Payer` | Direct | `HIS_Company`, `HIS_CompanyGroup`, `HIS_RuleCompany*` |
| `Patient` | Direct | `HIS_Patient` |
| `Encounter` | Derived | `HIS_Appointment`, `INP_Admission`, `INP_ERAssessment` |
| `EncounterService` | Derived | `HIS_InvoiceDetail`, orders, lab, OT, bed days |
| `RevenueTransaction` | Direct | `HIS_InvoiceDetail` + credit/refund |
| `ActivityTransaction` | Derived | OT log, census, floor consumption, meds, lab, rad |
| `DriverVolume` | Derived | Same facts, aggregated |

### 11.2 Seed from HIS, finish in a costing workshop

| VERITA table | HIS hint |
|---|---|
| `ServiceLine` | `HIS_ClinicGroup`, procedure categories |
| `Resource` | Doctors-as-roles, rooms, `PHR_Medicine`, materials, implants |
| `ResourceCapacity` | Rosters, doctor schedules, bed count, OR count |
| `Activity` | `HIS_Procedure.ProcedureTime`, `HIS_ProcedureDuration` |
| `ActivityResource` / `ProcedureResourceLine` | `HIS_ProcedureMaterialMapping` |
| `ProcedureActivityLine` | One default activity per `procType` to start |

### 11.3 Not in this HIS database

| VERITA table | Real source |
|---|---|
| `GLAccount`, `GLPosting`, `GLMapping` | Oracle ERP trial balance + finance mapping |
| `ResourceRate` | Payroll (loaded cost), inventory average cost, asset depreciation |
| `CostPool`, `CostDriver` (master), `AllocationRule*` | Costing design |
| `CostingVersion`, `CostingDefinition` | VERITA governance |
| Drug actual cost / stock | Pharmacy DB (`PHR_Sales*`, `PHR_InventoryStock`) |
| Equipment register | Fixed assets / CMMS |
| Floor area | Facility management |
| `User` | VERITA / Entra ID — not `SEC_*` (those tables are not even in this script) |
| Period calendar | Finance |

### 11.4 HIS domains we should **not** load as costing facts

Clinical documentation (`EMR_*`, `EHR_*`, progress notes, consents), IVF worksheets, dental charting, NPHIES wire tables, dashboard snapshots (`DSH_*`), user security, growth charts, temp/migration tables. Diagnoses stay as **attributes** (`Encounter.drgCode`) or future analysis — VERITA has no ICD table.

Useful **later / satellite** extracts (not required for the first published run):

- `HIS_IncomeShareDoctor` — physician profitability vs employment cost
- `HIS_Claim*` / `HIS_Settlement*` — collected vs billed
- `HIS_EstimatedCostMain` — estimate vs actual
- `INP_MealOrder` — catering driver
- `HIS_ROTA*` — utilisation QA

---

## 12. Recommended load order

Follow `EntitySpecs` load order. For a first hospital-month:

**Phase A — identity and catalogue (once)**  
`Hospital` → `Facility` → `ServiceLine` → `Department` → `CostCenter` → `Payer` → `Drg` → `Procedure` → `ProcedureRate` → `Package` / `PackageComponent` → `Provider`

**Phase B — costing model (workshop, not HIS)**  
`GLAccount` → `Period` → `CostDriver` → `CostPool` → `GLMapping` → `Resource` → `ResourceRate` → `ResourceCapacity` → `Activity` → recipes → `AllocationRule` → `CostingVersion`

**Phase C — monthly HIS facts**  
`Patient` → `Encounter` → `EncounterService` → `RevenueTransaction` → `ActivityTransaction` → `DriverVolume`

**Phase D — monthly ERP**  
`GLPosting` from Oracle, after cost-centre codes match Phase A.

A run will not reconcile until A+B+C+D are present. A+C alone can populate patient/service/revenue screens but will fail `DQ-RATE-MISSING`, `DQ-GL-UNMAPPED`, `DQ-PROC-UNMAPPED`.

---

## 13. Crosswalks to keep (even if not VERITA tables)

Store these as ETL reference tables; they prevent silent code collisions.

| Crosswalk | From | To |
|---|---|---|
| Hospital | `(SetupID, ProjectID)` | `Hospital.Code` |
| Clinic | `(SetupID, ProjectID, ClinicID)` | `Department.Code` |
| Nursing station / OT / lab section | HIS unit id | `Department` / `CostCenter` |
| Oracle CC | `HIS_CostCenterSetup.CostCenterID` | `CostCenter.Code` |
| Procedure | `(SetupID, ProjectID, ProcedureID)` | `Procedure.Code` + `catalogCode` |
| Doctor | `(SetupID, ProjectID, DoctorID)` | `Provider.Code` + role `Resource` |
| Company | `CompanyID` | `Payer.Code` |
| Encounter | Appointment / Admission / Assessment no. | `Encounter.encounterNo` |
| Invoice line | `(TransactionType, InvoiceNo, LineItemNo)` | `EncounterService.sourceRef` / `RevenueTransaction.sourceRef` |
| Medicine | `PHR_Medicine` / `MedicineId` | `Resource.Code` |
| Parameter codes | `STP_Parameter` | Enum maps (`encType`, gender, company type, …) |

---

## 14. Open questions (need live HIS or finance)

1. Confirm `STP_Parameter` values for `PatientType`, `TransactionType`, `AdmissionType`, `CompanyType`, `ProcClassType`, specialty.
2. Confirm whether `GLD_*` is used operationally or is a dead local ledger.
3. Location of `PHR_Sales*` / `PHR_Item` / inventory (database name, link on `PharmacyInvoiceNo`).
4. Whether `HIS_Doctor` salary-like columns are populated and are loaded cost or contract fee.
5. How `HIS_CostCenterSetup` relates to Oracle vs `STP_CostCenter`.
6. Day-case rule: which admission types map to `DAYCASE` vs `IP`.
7. OP→IP conversion: one encounter or two.
8. Which price is contractual list (`Price` vs `PublishedPrice` vs `HIS_ProcedurePrice`).
9. Radiology: is there an external RIS, or is the charge master the only exam list.
10. Multi-project: one legal hospital with several `ProjectID`s, or one VERITA hospital each.

---

## 15. Short conclusion

HISGENX can populate the **who / what / how many / how much was billed** side of VERITA: hospitals, clinics, doctors, charge master, prices, payers, DRGs, patients, OP/IP/ER encounters, invoiced services, and revenue.

It cannot populate the **how much it cost** side: GL by cost centre, payroll rates, inventory cost, equipment cost, cost pools, or allocation rules. Those come from Oracle / HR and from the costing model.

The important design work before any extract is:

1. Map `(SetupID, ProjectID)` → one `Hospital`.
2. Build a single `CostCenter` list aligned to Oracle, using `HIS_CostCenterSetup` + invoice-line `CostCenterID`.
3. Filter `HIS_Procedure` into procedures vs drugs vs packages.
4. Unify Appointment / Admission / ER into `Encounter`.
5. Turn OT times, bed census, meds and consumables into `ActivityTransaction` rows.
6. Bring Oracle trial balance and resource rates in from outside HIS.
)