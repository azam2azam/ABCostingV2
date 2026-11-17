# HIS consumption quantities — spec

Item-level quantities and volumes per procedure case, from HISGENX: **consumables, implants, medicine, and staff time (nurses, OR team)**.

**This spec covers quantities only.** Every row has an item or staff key, so cost can be joined later from outside HIS:

| Cost input (later, not in this spec) | Source |
|---|---|
| Medicine purchase price | Pharmacy DB |
| Consumable / implant unit cost | ERP inventory (or `HIS_FloorConsumption.UnitPrice` if confirmed as cost — see §9) |
| Nurse / doctor / technician rate | ERP payroll |
| Ward nursing minutes per patient-day | Nursing management (standard, §6.3) |

Related: [`HIS-COSTING-DATA-MAPPING.md`](HIS-COSTING-DATA-MAPPING.md), [`HIS-EXTRACT-PLAN.md`](HIS-EXTRACT-PLAN.md).

## Implementation status (2026-11-17)

| Part | Where | State |
|---|---|---|
| Daily facts packs (`HIS-{setup}-{project}-yyyy-MM-dd-facts.zip`) | `tools/HisExtract/DaySlices.cs`, `Program.cs` | Done. Import reads the day from the manifest (or the file name) |
| `39_ConsumptionItem.csv`, `40_StaffTime.csv` | `tools/HisExtract/ConsumptionExtract.cs` | Done; **not yet run against live HIS** |
| VERITA tables `ConsumptionItem`, `StaffTime` | `Transactions.cs`, `EntitySpecs.cs`, migration `ConsumptionAndStaffTime` | Done. Encounter link optional: rows keep `hisEncounterNo` and link when the encounter loads |
| Period facts screen counts / deletes both tables | `PeriodFactsService.cs` | Done |

Scope per daily pack: the pack's encounters (admitted / visited that day, or invoiced that day); every item and minute of those encounters is written whatever its own date. `sourceRef` makes re-sent rows updates.

Implemented vs this spec:

- Link rules: `ORDER_LINE`, `ORDER`, `OT_RESERVATION`, `BILLED_LINE`, `NONE`. Not yet: `OT_WINDOW`, `PACKAGE`.
- Anchor lines are counted lines only (line status 2, invoice type 1 / 75).
- Consumables: `HIS_FloorConsumption` (with `BBraun_ItemIntegrationMapping` Oracle code) and `INP_PatientConsumedItem` (skipped when a floor row has the same order). Returns (`TransType`, `RefundableQty`, `INP_ConsumableItemLog`) are **not netted yet** — codes unknown (§9 q7).
- Implants: `INP_ImplantDetail`, item from a floor row with the same barcode on the admission, else barcode, else description.
- Medicine: OP `HIS_ProgNoteMedicine` (dispensed qty, prescribed doses kept); every other billed medicine line (IP included) at billed quantity. IP orders wait for the pharmacy feed. Billed consumable charge codes without an issue row are not written (§9 q6).
- Staff time: surgeon + every `INP_OTMedicalStaffDetail` member (role `OT_STAFF`, raw `staffType`) at surgery minutes; anaesthesia minutes as `ANESTHETIST` (no id); OP doctor; ER doctor + nurse at the patient's stay; ward nursing = patient-days × `--ward-nurse-min` (default 260, `STANDARD`). Recovery nurse not written.

---

## 1. Why a new extract

`tools/HisExtract/ActivityTxnExtract.cs` already reads some of these sources into `35_ActivityTransaction.csv`, but it cannot support item costing:

| Today | Problem |
|---|---|
| Every floor and IP consumable is written as resource `CON-PPE`, activity `WOUND-CARE` | Item identity lost — no way to join a purchase price |
| `serviceRef` is always empty | Consumption is not tied to the procedure invoice line |
| IP medicine not extracted; OP medicine written as daily **dose** | Dose is not the unit a purchase price is quoted in (pack / unit) |
| Implants not extracted | — |
| OR minutes written for surgeon, anaesthetist, room only | No scrub / circulating / recovery nurse time |
| Ward nurse minutes = fixed `260 min × days` for every ward | One constant for ICU and general ward |

`35_ActivityTransaction.csv` stays as it is (the engine reads it). This spec adds two **item-grain** pack files; a later step can derive `ActivityTransaction` from them once item → resource mapping and costs exist.

---

## 2. Grain and anchor

- **Case anchor** = a procedure invoice line `(TransactionType, InvoiceNo, LineItemNo)` on `HIS_InvoiceDetail`, `Status = 2`, `TransactionType` in (1, 75). Package child lines are not anchors (the parent line is — same rule as revenue).
- **Encounter** = OP appointment / IP admission / ER assessment, coded as today (`Codes.OpEncounter`, `IpEncounter`, `ErEncounter`).
- Every consumption row carries the encounter. It carries the anchor `serviceRef` **when a link rule finds one** (§7); otherwise it stays on the encounter.
- Period: same as the current extractor — encounter admit month, plus encounters invoiced in the month that started earlier.

---

## 3. Output files

### 3.1 `39_ConsumptionItem.csv` — consumables, implants, medicine

| Column | Type | Notes |
|---|---|---|
| `encounterNo` | text | Required |
| `serviceRef` | text | Anchor invoice line `{TransactionType}-{InvoiceNo}-{LineItemNo}`; empty if unattributed |
| `linkMethod` | enum | How `serviceRef` was found (§7): `ORDER_LINE`, `ORDER`, `OT_RESERVATION`, `OT_WINDOW`, `PACKAGE`, `BILLED_LINE`, `NONE` |
| `otReservationId` | int | When the item belongs to a surgery |
| `category` | enum | `CONSUMABLE`, `IMPLANT`, `MEDICINE` |
| `itemKey` | text | Item identity as HIS holds it (see `itemKeySystem`) |
| `itemKeySystem` | enum | `INV_ITEM` (`ItemId` in inventory), `HIS_PROC` (charge code `ProcedureID`), `HIS_MED` (`MedicineCode` / `ItemID`), `BARCODE`, `TEXT` (implant description only) |
| `oracleItemCode` | text | From `HIS_Procedure.OracleItemCode`, `BBraun_ItemIntegrationMapping`, `HIS_ItemMaterialMapping.LinkedOracleItemCode` where available — **the main join key for ERP cost** |
| `barcode` | text | Floor consumption / implants |
| `description` | text | Item / charge name |
| `quantity` | decimal(19,4) | Net of returns (§8) |
| `uom` | text | As issued: `UNIT`, `PACK`, `DOSE`, … |
| `conversionFactor` | decimal | `HIS_FloorConsumption.ConversionFactor` when present (issue unit → stock unit) |
| `prescribedQty` | decimal | Medicine only: ordered/prescribed quantity, for checking against dispensed |
| `costCentreCode` | text | Nursing station / OT / clinic → VERITA cost centre; `UnmappedCc` if unknown |
| `locationId` | text | `InventoryLocationID` / `NursingStationID` / OT id |
| `billedAmount` | decimal | Charge on the linked line, if billed (informational — **not** cost) |
| `hisUnitPrice` | decimal | `HIS_FloorConsumption.UnitPrice` raw, kept until §9 confirms what it is |
| `occurredAt` | datetime | Issue / dispense / implant time |
| `sourceTable` | text | HIS table the row came from |
| `sourceRef` | text | Unique, prefix per source (§4–§5) |

### 3.2 `40_StaffTime.csv` — nurse and OR team time

| Column | Type | Notes |
|---|---|---|
| `encounterNo`, `serviceRef`, `linkMethod`, `otReservationId` | | As above |
| `staffRole` | enum | `SURGEON`, `ASSISTANT`, `ANESTHETIST`, `ANES_TECH`, `SCRUB_NURSE`, `CIRC_NURSE`, `RECOVERY_NURSE`, `WARD_NURSE`, `ER_NURSE`, `TECHNICIAN` |
| `staffId` | text | HIS doctor / employee id when known (join to payroll later); empty for standard-based rows |
| `minutes` | decimal | |
| `basis` | enum | `MEASURED` (start/end in HIS) or `STANDARD` (volume × standard, §6.3) |
| `volume` / `volumeUom` | decimal / text | For `STANDARD`: e.g. 3.5 `PATIENT_DAY` |
| `costCentreCode`, `occurredAt`, `sourceTable`, `sourceRef` | | |

---

## 4. Consumables and implants

### 4.1 Sources

| Source | Rows (all years) | Item key | Quantity | Encounter | Link fields |
|---|---:|---|---|---|---|
| `HIS_FloorConsumption` | 4.2M | `ItemId` (`INV_ITEM`), `Barcode`, `ItemCategoryId` | `UnitQty`, `ConversionFactor`, `RefundableQty` | `EncounterType` + `EncounterNo` | `OrderNo`, `OrderLineItemNo`, `NursingStationID`, `InventoryLocationID` |
| `INP_PatientConsumedItem` | 2.2M | `ProcedureID` (`HIS_PROC`) | `Quantity` | `AdmissionNo` | `OrderNo` |
| `INP_ConsumableItemLog` | 2.9M | `ProcedureID` | `Quantity` by `TransType` (issue / return) | `AdmissionNo` | `OrderNo`, `LineItemNo` |
| `INP_ImplantDetail` | ? | `Barcode`, `Description` (`TEXT`) | `Quantity` | via `OTReservationID` → `INP_OTReservation.AdmissionNo` | `OTReservationID`, nurses |
| `INP_OTBookingRequestMaterial` | ? | `ProcedureMaterialLinkID` / `ItemMaterialLinkID` | `Quantity` | via `OTBookingRequestNo` | **Planned** materials — extract to a check column, not as consumption |
| `HIS_InvoiceDetail` (consumable charge codes) | — | `ProcedureID` | `Quantity` | invoice header | Billed consumables with no issue record |

Out: `HIS_ProcedureMaterialMapping` (8 rows), `INP_ConsumableItemPreset*` (templates, not use).

### 4.2 Rules

- **Category.** `CONSUMABLE` by default; `IMPLANT` for every `INP_ImplantDetail` row and for any item whose category / charge-code group is implant (category list to be confirmed, §9).
- **`sourceRef`.** `FLR-{SQL_ID}`, `CON-{AdmissionNo}-{OrderNo}-{ProcedureID}`, `IMP-{OTReservationID}-{LineItemNo}`, `BIL-{TransactionType}-{InvoiceNo}-{LineItemNo}`.
- **Implant item key.** `INP_ImplantDetail` has no item code. Try in order: (1) `Barcode` = `HIS_FloorConsumption.Barcode` on the same encounter → take its `ItemId`; (2) implant charge line on the same admission → its `ProcedureID` / `OracleItemCode`; (3) keep `TEXT` description and flag for manual mapping.
- **Overlap between sources** — see §8. The same physical item can appear as a floor issue, an IP consumed item and a billed line.

---

## 5. Medicine

### 5.1 Sources

| Source | Setting | Item key | Quantity |
|---|---|---|---|
| `HIS_ProgNoteMedicine` | OP | `MedicineCode` | `DispensedQuantity` (pack/unit), `DoseDailyQuantity × DoseDurationDays` (prescribed doses) |
| `INP_ProgNoteMedicineOrder` | IP | `ItemID`, `ItemUom` | Ordered: dose × frequency × days — **no dispensed qty in HIS** |
| `INP_Prescription` | IP | `ItemID`, `Dose` | Prescription lines (verify use vs order table) |
| `INP_MedDispensationLogs` | IP | `RxNo` | Administration events (count), no qty |
| `HIS_InvoiceDetail` where `HIS_Procedure.IsMedicine = 1` | OP / IP | `ProcedureID`, `MedicineId`, `OracleItemCode` | Billed `Quantity` |
| `HIS_InvoiceMain.IsPharmacyInvoice / PharmacyInvoiceNo` | OP | — | Link to the pharmacy sale |
| `HIS_ClaimMedicine` | claimed | `ItemID`, `DispenseHISInvoiceNo` | Claimed item per invoice line |

### 5.2 Rules

- **Costing quantity = dispensed quantity in the pharmacy's selling unit**, because the purchase price will come per that unit. Doses (`DoseDailyQuantity`) are kept as `prescribedQty` only. This fixes the current pack-vs-dose mismatch noted in `MedsAsync`.
- **OP:** `quantity = DispensedQuantity`; if 0, fall back to the billed medicine line quantity; else leave the row with `quantity = 0` and `prescribedQty` filled (flag).
- **IP:** HIS has orders, not dispenses. Use the billed medicine line quantity as `quantity`, order quantity as `prescribedQty`. When the pharmacy feed arrives, its dispensed lines replace both (join on `PharmacyInvoiceNo` / `RxNo` / admission + item + date).
- **Item key:** prefer `OracleItemCode`, then `MedicineId`, then `ProcedureID`. **Confirm HIS item codes equal pharmacy item codes** before any cost join (§9).
- **`sourceRef`:** `MED-OP-{EpisodeID}-{AppointmentNo}-{MedicineCode}`, `MED-IP-{OrderNo}-{LineItemNo}`, `MED-BIL-{TransactionType}-{InvoiceNo}-{LineItemNo}`.

---

## 6. Staff time

### 6.1 OR (measured)

| Item | Source |
|---|---|
| Surgery minutes | `INP_OTReservation.ActualSugeryStartTime → ActualSurgeryEndTime` (`INP_ORLogInfo` is empty) |
| Anaesthesia minutes | `INP_OTReservation.AnesthesiaStartTime → AnesthesiaEndTime` |
| Team per surgery | `INP_OTMedicalStaffDetail (OTReservationID, MedicalStaffID, MedicalStaffType)`; surgeon also `INP_OTReservation.PrimaryDoctorID` |
| Nurses, fallback | `INP_ImplantDetail.ScrubNurse / CirculatingNurse` (same reservation); `HIS_EncounterNursingOperativeReport` (`ScrubNurseID`, `CirculatingNurseID`, `RecoveryNurseId`, `ORNurseId`) — keyed by encounter, so use only when the admission has one surgery |
| Day procedures / IVF | `HIS_NursingProcedureNotes` — `RoomTimeIn/Out`, `ProcedureStart/End`, `AnesthesiaStart/End`, nurse and technician IDs |

One row per `(OTReservationID, staffRole, staffId)`. Surgical roles get surgery minutes; anaesthesia roles get anaesthesia minutes (or surgery minutes if blank). Recovery-nurse minutes are not in HIS → `STANDARD` (e.g. 60 min per case, from nursing).

`sourceRef`: `OT-{OTReservationID}-{staffRole}-{staffId}`.

### 6.2 ER / OP (measured)

Keep current time logic (`INP_ERAssessment` sign-in/out, appointment actual start/end) and emit `ER_NURSE` rows. OP nurse time is not recorded — `STANDARD` minutes per visit by clinic, if nursing provides them.

### 6.3 Ward (volume × standard)

- **Volume from HIS:** patient-days per ward from `INP_Transfer.Duration` (fallback admission LOS), as `BedsAsync` does today.
- **Standard from nursing management (new input):** `WardNursingStandard (hospitalCode, nursingStationId or costCentreCode, minutesPerPatientDay, effectiveFrom)`. Replaces the hard-coded `260`.
- Row: `staffRole = WARD_NURSE`, `basis = STANDARD`, `volume = patient-days`, `minutes = volume × standard`. Stays on the encounter (`linkMethod = NONE`) — ward nursing is a case cost, not a procedure-line cost.

---

## 7. Linking consumption to the procedure line

Applied in this order; first match wins; the rule used goes into `linkMethod`.

| # | Rule | Applies to |
|---|---|---|
| 1 `ORDER_LINE` | Item `OrderNo + OrderLineItemNo` = invoice line `OrderNo + OrderLineItemNo` | Floor consumption, billed lines |
| 2 `ORDER` | Item `OrderNo` = invoice line `OrderNo`, and the order has a single procedure line | IP consumed items, IP medicine orders |
| 3 `OT_RESERVATION` | Item belongs to an `OTReservationID`; anchor = invoice line on that admission whose `ProcedureID` is in `INP_OTProcedureMapping` for the reservation (first `LineItemNo` if several) | Implants, OR staff |
| 4 `OT_WINDOW` | IP item issued on the OT location / during the surgery window ± 2 h | Floor consumption in theatre |
| 5 `PACKAGE` | Item's line is a package child (`HIS_InvoicePackageItem.ChildInvoiceNo`) → anchor = package parent line | All |
| 6 `BILLED_LINE` | The item is itself a billed consumable / medicine line → anchor = the nearest procedure line on the same invoice | Billed-only items |
| 7 `NONE` | No rule matched — row stays on the encounter | Ward floor stock, ward nursing, leftovers |

No spreading of `NONE` rows in the extract. Spreading across procedures (e.g. by billed value) is a VERITA-side decision, so it can be changed without re-extracting.

---

## 8. Overlap and returns

- **Returns:** `INP_ConsumableItemLog` issue minus return by `(AdmissionNo, OrderNo, ProcedureID)`; floor consumption `TransType` returns and `RefundableQty` subtract from `UnitQty`. Drop rows that net to ≤ 0.
- **Same item in several tables** — precedence, keep one:
  1. `HIS_FloorConsumption` (real stock issue, has inventory `ItemId`)
  2. `INP_PatientConsumedItem` / `INP_ConsumableItemLog`
  3. Billed line (`HIS_InvoiceDetail`) — only when no issue row matched it on `OrderNo + OrderLineItemNo` (or admission + `ProcedureID` + date)
- Implants: `INP_ImplantDetail` wins; drop a floor / billed row it was matched to by barcode, after copying the item key across.
- Medicine: pharmacy dispense (later) > OP `DispensedQuantity` > billed line > order.

The probe (§10) measures how much each pair overlaps before the precedence is locked.

---

## 9. Open questions (live HIS)

1. **`HIS_FloorConsumption.UnitPrice`** — inventory cost or price? Compare with `SellingPrice`, `MarkUp`, `BillingPrice` on a sample. If cost, it is the consumable cost for floor items.
2. Row counts and fill rate for `INP_ImplantDetail`, `INP_OTMedicalStaffDetail`, `HIS_EncounterNursingOperativeReport`, `HIS_NursingProcedureNotes`, `INP_OTBookingRequestMaterial`.
3. `MedicalStaffType` codes (`STP_Parameter`) → `staffRole`.
4. How many OT reservations have actual surgery start/end filled.
5. Item master for `HIS_FloorConsumption.ItemId` — inventory DB name; is `OracleItemCode` reachable for it (`BBraun_ItemIntegrationMapping`?).
6. Which `HIS_Procedure` categories / groups are consumables and implants (charge-code side).
7. `INP_ConsumableItemLog.TransType` and `HIS_FloorConsumption.TransType` values (issue / return).
8. Are HIS `MedicineCode` / `ItemID` / `MedicineId` the same codes the pharmacy uses?
9. Overlap: % of `INP_PatientConsumedItem` rows also in `HIS_FloorConsumption`, and % of consumable invoice lines with an issue row.

---

## 10. Build steps

1. **Probe** (`HisExtract probe`): add counts and fill-rate checks for §9 items 1–9 for one month.
2. Lock precedence (§8) and categories from the probe.
3. **Extract** `39_ConsumptionItem.csv` and `40_StaffTime.csv` in the facts pack (new `ConsumptionExtract.cs`), month-sliced like the other facts.
4. **Import** into two VERITA staging tables with the same columns; DQ rules: unknown item key, `linkMethod = NONE` share, quantity ≤ 0, duplicate `sourceRef`.
5. **Reports (quantities only):** per procedure — items and quantities, implants, staff minutes by role; per item — volume by month and department.
6. **Later (cost):** add `ItemCost (itemKey / oracleItemCode, uom, unitCost, effectiveFrom, source)` from pharmacy and ERP, and `StaffRate (role or staffId, ratePerMinute, effectiveFrom)` from payroll. Cost = quantity × unit cost; minutes × rate. Then derive `ActivityTransaction` rows from these instead of `CON-PPE`.

### Reconciliation checks per month

- Consumption rows with a `serviceRef` / all rows (target: rising as link rules improve).
- Billed consumable / medicine quantity vs extracted quantity, by item.
- Implants per surgery vs implant charge lines.
- OR staff minutes per surgery = surgery minutes × team size.
- Ward patient-days = `INP_Transfer` / LOS total.
