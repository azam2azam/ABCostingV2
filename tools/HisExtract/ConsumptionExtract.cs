using Microsoft.Data.SqlClient;

namespace HisExtract;

/// <summary>
/// Facts pack phase 3 (docs/HIS-CONSUMPTION-QUANTITIES-SPEC.md): item-level consumption and staff time.
/// 39_ConsumptionItem.csv — consumables, implants, medicine by item, tied to the procedure line when a rule finds one.
/// 40_StaffTime.csv — OR team, OP and ER doctors (measured) and ward nursing (patient-days × standard).
/// Quantities only. Cost joins later from pharmacy / ERP on itemKey or oracleItemCode, and from payroll on staffId.
///
/// Scope is the pack's encounters: admitted / visited in the slice, or invoiced in it. For those encounters every
/// item and minute is written whatever its own date, so a daily pack never drops what was used on another day;
/// sourceRef keeps re-sent rows idempotent.
/// </summary>
public static class ConsumptionExtract
{
    // The pack's encounters, by type. Same rule as the Encounter file: in the slice, or invoiced in it.
    private const string IpSet = """
        SELECT i.AdmissionNo AS No FROM dbo.INP_Admission i WITH (NOLOCK)
        WHERE i.SetupID=@SetupId AND i.ProjectID=@ProjectId AND i.PatientID > 0 AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
        UNION
        SELECT m.AdmissionNo FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
        WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo > 0 AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
        """;
    private const string OpSet = """
        SELECT a.AppointmentNo AS No FROM dbo.HIS_Appointment a WITH (NOLOCK)
        WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0 AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
          AND EXISTS (SELECT 1 FROM dbo.HIS_InvoiceMain x WITH (NOLOCK) WHERE x.SetupID=a.SetupID AND x.ProjectID=a.ProjectID AND x.AppointmentNo=a.AppointmentNo AND x.CancelledOn IS NULL)
        UNION
        SELECT m.AppointmentNo FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
        WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0 AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
        """;
    private const string ErSet = """
        SELECT e.AssessmentNo AS No FROM dbo.INP_ERAssessment e WITH (NOLOCK)
        WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0 AND e.AssessmentDate >= @From AND e.AssessmentDate < @To
        UNION
        SELECT e.AssessmentNo FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
        INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK) ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo
        WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND e.InvoiceNo > 0 AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
        """;

    /// <summary>A counted invoice line of one of the pack's encounters: a link target for consumption.</summary>
    private sealed record Line(string Encounter, int Tt, int Inv, int LineNo, string ProcedureId, int? OrderNo, int? OrderLine,
        bool IsMedicine, string? MedicineId, string? OracleItemCode, string Name, decimal Qty, decimal Amount, int? Clinic, DateTime At)
    {
        public string Ref => $"INV-{Tt}-{Inv}-{LineNo}";
    }

    public static async Task RunAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        pack.Step("Consumption: the pack's encounters");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LineIndex lines;
        try
        {
            var (ipN, opN, erN) = await EncounterSetsAsync(db, s);
            pack.Note($"Consumption: {ipN} admissions, {opN} appointments, {erN} ER assessments in scope ({sw.Elapsed.TotalSeconds:N0} s)");
            pack.Step("Consumption: invoice lines to link items to");
            sw.Restart();
            lines = new LineIndex(await LinesAsync(db, s, c));
        }
        catch (SqlException ex)
        {
            // e.g. a timeout, or a login that may not create temp tables: the rest of the pack is still good.
            pack.Warn("Consumption and staff time", ex);
            return;
        }
        pack.Note($"Consumption: {lines.All.Count} counted invoice lines on the pack's encounters to link items to ({sw.Elapsed.TotalSeconds:N0} s)");

        using (var csv = pack.Create("39_ConsumptionItem.csv",
            "hospitalCode", "encounterNo", "hisEncounterNo", "serviceRef", "linkMethod", "otReservationId", "category", "itemKey", "itemKeySystem",
            "oracleItemCode", "barcode", "description", "quantity", "uom", "conversionFactor", "prescribedQty", "costCentreCode", "locationId",
            "billedAmount", "hisUnitPrice", "occurredAt", "sourceTable", "sourceSystem", "sourceRef"))
        {
            var o = new ItemWriter(csv, c);
            await Safe(pack, "Floor consumption", () => Counted(pack, "Floor consumption items", o, () => FloorAsync(db, s, c, o, lines)));
            await Safe(pack, "IP consumed items", () => Counted(pack, "IP consumed items", o, () => ConsumedAsync(db, s, c, o, lines)));
            await Safe(pack, "Implants", () => Counted(pack, "Implants", o, () => ImplantsAsync(db, s, c, o, lines)));
            var dispensedLines = new HashSet<string>(StringComparer.Ordinal);
            await Safe(pack, "OP dispensed medicine", () => Counted(pack, "OP dispensed medicine", o, () => OpMedicineAsync(db, s, c, o, lines, dispensedLines)));
            var before = o.Written;
            BilledMedicine(c, o, lines, dispensedLines);
            pack.Note($"Billed medicine lines: {o.Written - before} rows");
            pack.Note("Consumption: IP medicine is the billed quantity until the pharmacy dispense feed is joined. Billed consumable charge codes without an issue row are not written yet (category list to be confirmed).");
        }

        using (var csv = pack.Create("40_StaffTime.csv",
            "hospitalCode", "encounterNo", "hisEncounterNo", "serviceRef", "linkMethod", "otReservationId", "staffRole", "staffType", "staffId",
            "providerCode", "minutes", "basis", "volume", "volumeUom", "costCentreCode", "occurredAt", "sourceTable", "sourceSystem", "sourceRef"))
        {
            var o = new StaffWriter(csv, c);
            await Safe(pack, "OR staff", () => Counted(pack, "OR staff minutes", o, () => OrStaffAsync(db, s, c, o, lines)));
            await Safe(pack, "OP doctor", () => Counted(pack, "OP doctor minutes", o, () => OpDoctorAsync(db, s, c, o)));
            await Safe(pack, "ER staff", () => Counted(pack, "ER doctor / nurse minutes", o, () => ErStaffAsync(db, s, c, o)));
            await Safe(pack, "Ward nursing", () => Counted(pack, "Ward nursing (standard)", o, () => WardAsync(db, s, c, o)));
            pack.Note($"StaffTime: ward nursing is patient-days × {s.WardNurseMinutesPerDay} min (STANDARD) until nursing supplies a standard per ward. ER minutes are the patient's stay.");
        }
    }

    // ── Link targets ──────────────────────────────────────────────────────────────

    private static async Task<List<Line>> LinesAsync(HisConnection db, Settings s, Codes c)
    {
        var list = new List<Line>();
        // Two branches, each driven by the day's small set of encounters, so HIS_InvoiceMain / HIS_InvoiceDetail are sought, not scanned.
        const string cols = """
            m.TransactionType, m.InvoiceNo, m.AdmissionNo, m.AppointmentNo, m.ClinicID,
            m.InvoiceDate, d.PerformDate, d.LineItemNo, d.ProcedureID, d.OrderNo, d.OrderLineItemNo, d.Price, d.Quantity, d.PrimaryClinicID,
            hp.IsMedicine, hp.MedicineId, hp.OracleItemCode, hp.ProcedureName
            """;
        const string joins = """
            INNER JOIN dbo.HIS_InvoiceDetail d WITH (NOLOCK)
              ON d.SetupID=m.SetupID AND d.ProjectID=m.ProjectID AND d.TransactionType=m.TransactionType AND d.InvoiceNo=m.InvoiceNo
            LEFT JOIN dbo.HIS_Procedure hp WITH (NOLOCK)
              ON hp.SetupID=d.SetupID AND hp.ProjectID=d.ProjectID AND hp.ProcedureID=d.ProcedureID
            """;
        await db.QueryAsync($"""
            SELECT {cols}
            FROM #ip i
            INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK) ON m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo=i.No
            {joins}
            WHERE m.CancelledOn IS NULL AND d.Status = 2 AND m.InvoiceType IN (1, 75)
            UNION ALL
            SELECT {cols}
            FROM #op o
            INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK) ON m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo=o.No
            {joins}
            WHERE m.CancelledOn IS NULL AND d.Status = 2 AND m.InvoiceType IN (1, 75) AND ISNULL(m.AdmissionNo, 0) = 0
            """, s.BindTenant, r =>
        {
            var adm = HisConnection.Ni(r, "AdmissionNo");
            var enc = adm is > 0 ? c.IpEncounter(adm.Value) : c.OpEncounter(HisConnection.I(r, "AppointmentNo"));
            var qty = Math.Max(1, HisConnection.I(r, "Quantity"));
            var price = Maps.Round4(HisConnection.F(r, "Price"));
            list.Add(new Line(enc, HisConnection.I(r, "TransactionType"), HisConnection.I(r, "InvoiceNo"), HisConnection.I(r, "LineItemNo"),
                HisConnection.S(r, "ProcedureID"), HisConnection.Ni(r, "OrderNo"), HisConnection.Ni(r, "OrderLineItemNo"),
                HisConnection.B(r, "IsMedicine"), Nz(HisConnection.S(r, "MedicineId")), Nz(HisConnection.S(r, "OracleItemCode")),
                HisConnection.S(r, "ProcedureName"), qty, decimal.Round(price * qty, 4, MidpointRounding.AwayFromZero),
                HisConnection.Ni(r, "PrimaryClinicID") ?? HisConnection.Ni(r, "ClinicID"),
                HisConnection.Dt(r, "PerformDate") ?? HisConnection.Dt(r, "InvoiceDate") ?? s.From!.Value));
        });
        return list;
    }

    /// <summary>
    /// The pack's encounters into session temp tables (#ip admissions, #op appointments, #er assessments), once per pack.
    /// Every consumption and staff query then starts from these few thousand keys instead of filtering the big HIS tables.
    /// </summary>
    private static async Task<(int Ip, int Op, int Er)> EncounterSetsAsync(HisConnection db, Settings s)
    {
        async Task<int> Fill(string table, string set)
        {
            await db.ExecAsync($"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE {table}; CREATE TABLE {table} (No int NOT NULL PRIMARY KEY);", _ => { });
            return await db.ExecAsync($"INSERT INTO {table} (No) SELECT DISTINCT x.No FROM ({set}) x WHERE x.No > 0", s.BindPeriod);
        }
        return (await Fill("#ip", IpSet), await Fill("#op", OpSet), await Fill("#er", ErSet));
    }

    /// <summary>The link targets, indexed so each item finds its lines without scanning them all.</summary>
    private sealed class LineIndex
    {
        public LineIndex(List<Line> all)
        {
            All = all;
            ByOrder = all.Where(l => l.OrderNo is > 0).ToLookup(l => (l.Encounter, l.OrderNo!.Value));
            ByInvoice = all.ToLookup(l => (l.Tt, l.Inv));
            ByEncounter = all.ToLookup(l => l.Encounter);
        }
        public List<Line> All { get; }
        public ILookup<(string, int), Line> ByOrder { get; }
        public ILookup<(int, int), Line> ByInvoice { get; }
        public ILookup<string, Line> ByEncounter { get; }
    }

    /// <summary>Rules 1–2 of the spec §7: order line, then a single procedure line on the order.</summary>
    private static (string? Ref, string Method, decimal? Billed) ByOrder(LineIndex lines, string enc, int? order, int? orderLine)
    {
        if (order is not > 0) return (null, "NONE", null);
        var onEnc = lines.ByOrder[(enc, order.Value)].ToList();
        if (orderLine is > 0 && onEnc.FirstOrDefault(l => l.OrderLine == orderLine) is { } exact)
            return (exact.Ref, "ORDER_LINE", exact.Amount);
        var procs = onEnc.Where(l => !l.IsMedicine).ToList();
        return procs.Count == 1 ? (procs[0].Ref, "ORDER", null) : (null, "NONE", null);
    }

    /// <summary>Rule 6: a billed item's anchor is the first procedure line on the same invoice.</summary>
    private static string? AnchorOnInvoice(LineIndex lines, Line item) =>
        lines.ByInvoice[(item.Tt, item.Inv)].Where(l => !l.IsMedicine && l.Ref != item.Ref).OrderBy(l => l.LineNo).FirstOrDefault()?.Ref;

    // ── Items ────────────────────────────────────────────────────────────────────

    private static async Task FloorAsync(HisConnection db, Settings s, Codes c, ItemWriter o, LineIndex lines)
    {
        await db.QueryAsync($"""
            WITH f AS (
                SELECT f.* FROM #ip i INNER JOIN dbo.HIS_FloorConsumption f WITH (NOLOCK)
                  ON f.SetupID=@SetupId AND f.ProjectID=@ProjectId AND f.EncounterType=2 AND f.EncounterNo=i.No
                UNION ALL
                SELECT f.* FROM #op o INNER JOIN dbo.HIS_FloorConsumption f WITH (NOLOCK)
                  ON f.SetupID=@SetupId AND f.ProjectID=@ProjectId AND f.EncounterType=1 AND f.EncounterNo=o.No
                UNION ALL
                SELECT f.* FROM #er e INNER JOIN dbo.HIS_FloorConsumption f WITH (NOLOCK)
                  ON f.SetupID=@SetupId AND f.ProjectID=@ProjectId AND f.EncounterType=3 AND f.EncounterNo=e.No
            ), bb AS (
                SELECT b.ItemID, MIN(b.OracleItemCode) AS OracleItemCode FROM dbo.BBraun_ItemIntegrationMapping b WITH (NOLOCK)
                WHERE b.SetupID=@SetupId AND b.ProjectID=@ProjectId AND ISNULL(b.IsActive,1)=1 GROUP BY b.ItemID
            )
            SELECT f.SQL_ID, f.EncounterType, f.EncounterNo, f.NursingStationID, f.InventoryLocationID, f.ItemCategoryId, f.ItemId, f.Barcode,
                   f.UnitQty, f.ConversionFactor, f.RefundableQty, f.UnitPrice, f.GrossAmount, f.OrderNo, f.OrderLineItemNo, f.TransType, f.CreatedOn,
                   bb.OracleItemCode
            FROM f LEFT JOIN bb ON bb.ItemID = f.ItemId
            WHERE f.UnitQty > 0
            """, s.BindTenant, r =>
        {
            var enc = EncounterOf(c, HisConnection.I(r, "EncounterType"), HisConnection.I(r, "EncounterNo"));
            if (enc is null) return;
            var id = Convert.ToInt64(r.GetValue(r.GetOrdinal("SQL_ID")), System.Globalization.CultureInfo.InvariantCulture);
            var (anchor, method, _) = ByOrder(lines, enc, HisConnection.Ni(r, "OrderNo"), HisConnection.Ni(r, "OrderLineItemNo"));
            var ns = HisConnection.Ni(r, "NursingStationID");
            o.Add(enc, anchor, method, null, "CONSUMABLE", HisConnection.S(r, "ItemId"), "INV_ITEM", Nz(HisConnection.S(r, "OracleItemCode")),
                Nz(HisConnection.S(r, "Barcode")), $"Item {HisConnection.S(r, "ItemId")} (category {HisConnection.S(r, "ItemCategoryId")})",
                Maps.Round4(HisConnection.F(r, "UnitQty")), "UNIT", Opt(HisConnection.F(r, "ConversionFactor")), null,
                ns is null ? c.UnmappedCc : c.WardCc(ns), HisConnection.S(r, "InventoryLocationID"),
                Opt(HisConnection.F(r, "GrossAmount")), Opt(HisConnection.F(r, "UnitPrice")),
                HisConnection.Dt(r, "CreatedOn") ?? s.From!.Value, "HIS_FloorConsumption", $"FLR-{id}");
        });
    }

    private static async Task ConsumedAsync(HisConnection db, Settings s, Codes c, ItemWriter o, LineIndex lines)
    {
        // Spec §8: a floor issue wins over the IP consumed record of the same order.
        await db.QueryAsync($"""
            WITH fo AS (
                SELECT DISTINCT f.EncounterNo, f.OrderNo FROM #ip i INNER JOIN dbo.HIS_FloorConsumption f WITH (NOLOCK)
                  ON f.SetupID=@SetupId AND f.ProjectID=@ProjectId AND f.EncounterType=2 AND f.EncounterNo=i.No
                WHERE f.OrderNo IS NOT NULL
            )
            SELECT x.AdmissionNo, x.OrderNo, x.ProcedureID, x.Quantity, x.TotalPrice, x.CreatedOn,
                   hp.ProcedureName, hp.OracleItemCode, hp.IsMedicine
            FROM #ip i
            INNER JOIN dbo.INP_PatientConsumedItem x WITH (NOLOCK)
              ON x.SetupID=@SetupId AND x.ProjectID=@ProjectId AND x.AdmissionNo=i.No
            LEFT JOIN dbo.HIS_Procedure hp WITH (NOLOCK)
              ON hp.SetupID=x.SetupID AND hp.ProjectID=x.ProjectID AND hp.ProcedureID=x.ProcedureID
            LEFT JOIN fo ON fo.EncounterNo = x.AdmissionNo AND fo.OrderNo = x.OrderNo
            WHERE x.Quantity > 0 AND fo.EncounterNo IS NULL
            """, s.BindTenant, r =>
        {
            var adm = HisConnection.I(r, "AdmissionNo");
            if (adm <= 0) return;
            var enc = c.IpEncounter(adm);
            var order = HisConnection.Ni(r, "OrderNo");
            var proc = HisConnection.S(r, "ProcedureID");
            var (anchor, method, _) = ByOrder(lines, enc, order, null);
            o.Add(enc, anchor, method, null, HisConnection.B(r, "IsMedicine") ? "MEDICINE" : "CONSUMABLE", proc, "HIS_PROC",
                Nz(HisConnection.S(r, "OracleItemCode")), null, Nz(HisConnection.S(r, "ProcedureName")),
                HisConnection.I(r, "Quantity"), "UNIT", null, null, c.UnmappedCc, null,
                Opt(HisConnection.F(r, "TotalPrice")), null, HisConnection.Dt(r, "CreatedOn") ?? s.From!.Value,
                "INP_PatientConsumedItem", $"CON-{adm}-{order}-{proc}");
        });
    }

    private static async Task ImplantsAsync(HisConnection db, Settings s, Codes c, ItemWriter o, LineIndex lines)
    {
        var otProcs = await OtProceduresAsync(db, s);
        await db.QueryAsync($"""
            WITH fl AS (
                SELECT f.EncounterNo, f.Barcode, MIN(f.ItemId) AS ItemId FROM #ip i INNER JOIN dbo.HIS_FloorConsumption f WITH (NOLOCK)
                  ON f.SetupID=@SetupId AND f.ProjectID=@ProjectId AND f.EncounterType=2 AND f.EncounterNo=i.No
                WHERE ISNULL(f.Barcode, '') <> '' GROUP BY f.EncounterNo, f.Barcode
            ), bb AS (
                SELECT b.ItemID, MIN(b.OracleItemCode) AS OracleItemCode FROM dbo.BBraun_ItemIntegrationMapping b WITH (NOLOCK)
                WHERE b.SetupID=@SetupId AND b.ProjectID=@ProjectId GROUP BY b.ItemID
            )
            SELECT d.OTReservationID, d.LineItemNo, d.Barcode, d.Description, d.Quantity, d.CreatedOn,
                   ot.AdmissionNo, ot.OTID, ot.OperationDate, fl.ItemId, bb.OracleItemCode
            FROM #ip i
            INNER JOIN dbo.INP_OTReservation ot WITH (NOLOCK) ON ot.SetupID=@SetupId AND ot.ProjectID=@ProjectId AND ot.AdmissionNo=i.No
            INNER JOIN dbo.INP_ImplantDetail d WITH (NOLOCK)
              ON d.SetupID=ot.SetupID AND d.ProjectId=ot.ProjectID AND d.OTReservationID=ot.OTReservationID
            LEFT JOIN fl ON fl.EncounterNo = ot.AdmissionNo AND fl.Barcode = d.Barcode
            LEFT JOIN bb ON bb.ItemID = fl.ItemId
            """, s.BindTenant, r =>
        {
            var adm = HisConnection.I(r, "AdmissionNo");
            var res = HisConnection.I(r, "OTReservationID");
            var enc = c.IpEncounter(adm);
            var anchor = OtAnchor(lines, enc, otProcs.GetValueOrDefault(res));
            var item = HisConnection.S(r, "ItemId");
            var barcode = Nz(HisConnection.S(r, "Barcode"));
            var (key, system) = item.Length > 0 ? (item, "INV_ITEM") : barcode is not null ? (barcode, "BARCODE") : (HisConnection.S(r, "Description"), "TEXT");
            var ot = HisConnection.Ni(r, "OTID");
            o.Add(enc, anchor, anchor is null ? "NONE" : "OT_RESERVATION", res, "IMPLANT", key, system, Nz(HisConnection.S(r, "OracleItemCode")),
                barcode, Nz(HisConnection.S(r, "Description")), Math.Max(1, HisConnection.I(r, "Quantity")), "UNIT", null, null,
                ot is null ? c.UnmappedCc : c.OtCc(ot), ot?.ToString(), null, null,
                HisConnection.Dt(r, "CreatedOn") ?? HisConnection.Dt(r, "OperationDate") ?? s.From!.Value,
                "INP_ImplantDetail", $"IMP-{res}-{HisConnection.I(r, "LineItemNo")}");
        });
    }

    private static async Task OpMedicineAsync(HisConnection db, Settings s, Codes c, ItemWriter o, LineIndex lines, HashSet<string> dispensedLines)
    {
        await db.QueryAsync($"""
            SELECT m.EpisodeID, m.AppointmentNo, m.MedicineCode, m.DispensedQuantity, m.DoseDailyQuantity, m.DoseDurationDays, m.StartDate, m.RxNo
            FROM #op o
            INNER JOIN dbo.HIS_ProgNoteMedicine m WITH (NOLOCK) ON m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo=o.No
            WHERE ISNULL(m.DispensedQuantity, 0) > 0 OR ISNULL(m.DoseDailyQuantity, 0) > 0
            """, s.BindTenant, r =>
        {
            var appt = HisConnection.I(r, "AppointmentNo");
            var enc = c.OpEncounter(appt);
            var med = HisConnection.S(r, "MedicineCode");
            var dispensed = HisConnection.I(r, "DispensedQuantity");
            var daily = HisConnection.F(r, "DoseDailyQuantity");
            var days = Math.Max(1, HisConnection.I(r, "DoseDurationDays"));
            // The billed medicine line of the same appointment and medicine, when there is one.
            var billed = lines.ByEncounter[enc].FirstOrDefault(l => l.IsMedicine && (l.MedicineId == med || l.ProcedureId == med));
            if (billed is not null) dispensedLines.Add(billed.Ref);
            var anchor = billed is null ? null : AnchorOnInvoice(lines, billed) ?? billed.Ref;
            o.Add(enc, anchor, billed is null ? "NONE" : "BILLED_LINE", null, "MEDICINE", med, "HIS_MED", billed?.OracleItemCode, null, billed?.Name,
                dispensed > 0 ? dispensed : billed?.Qty ?? 0, "UNIT", null, daily > 0 ? Maps.Round4(daily * days) : null, c.UnmappedCc, null,
                billed?.Amount, null, HisConnection.Dt(r, "StartDate") ?? s.From!.Value, "HIS_ProgNoteMedicine",
                $"MED-OP-{HisConnection.I(r, "EpisodeID")}-{appt}-{med}");
        });
    }

    /// <summary>Billed medicine lines not already written from an OP dispense: IP medicine and OP medicine with no prescription row.</summary>
    private static void BilledMedicine(Codes c, ItemWriter o, LineIndex lines, HashSet<string> dispensedLines)
    {
        foreach (var l in lines.All.Where(l => l.IsMedicine && !dispensedLines.Contains(l.Ref)))
        {
            var anchor = AnchorOnInvoice(lines, l);
            var (key, system) = l.MedicineId is not null ? (l.MedicineId, "HIS_MED") : (l.ProcedureId, "HIS_PROC");
            o.Add(l.Encounter, anchor, anchor is null ? "NONE" : "BILLED_LINE", null, "MEDICINE", key, system, l.OracleItemCode, null, l.Name,
                l.Qty, "UNIT", null, null, c.UnmappedCc, null, l.Amount, null, l.At, "HIS_InvoiceDetail", $"MED-BIL-{l.Tt}-{l.Inv}-{l.LineNo}");
        }
    }

    // ── Staff time ───────────────────────────────────────────────────────────────

    private static async Task OrStaffAsync(HisConnection db, Settings s, Codes c, StaffWriter o, LineIndex lines)
    {
        var otProcs = await OtProceduresAsync(db, s);
        var staff = new Dictionary<int, List<(string Id, string Type)>>();
        await db.QueryAsync($"""
            SELECT st.OTReservationID, st.MedicalStaffID, st.MedicalStaffType
            FROM #ip i
            INNER JOIN dbo.INP_OTReservation ot WITH (NOLOCK) ON ot.SetupID=@SetupId AND ot.ProjectID=@ProjectId AND ot.AdmissionNo=i.No
            INNER JOIN dbo.INP_OTMedicalStaffDetail st WITH (NOLOCK)
              ON st.SetupID=ot.SetupID AND st.ProjectID=ot.ProjectID AND st.OTReservationID=ot.OTReservationID
            WHERE ISNULL(st.IsActive, 1) = 1
            """, s.BindTenant, r =>
        {
            var res = HisConnection.I(r, "OTReservationID");
            if (!staff.TryGetValue(res, out var list)) staff[res] = list = [];
            list.Add((HisConnection.S(r, "MedicalStaffID"), HisConnection.S(r, "MedicalStaffType")));
        });

        await db.QueryAsync($"""
            SELECT ot.OTReservationID, ot.AdmissionNo, ot.OTID, ot.OperationDate, ot.PrimaryDoctorID,
                   ot.ActualSugeryStartTime, ot.ActualSurgeryEndTime, ot.AnesthesiaStartTime, ot.AnesthesiaEndTime
            FROM #ip i
            INNER JOIN dbo.INP_OTReservation ot WITH (NOLOCK) ON ot.SetupID=@SetupId AND ot.ProjectID=@ProjectId AND ot.AdmissionNo=i.No
            """, s.BindTenant, r =>
        {
            var day = HisConnection.Dt(r, "OperationDate") ?? s.From!.Value;
            var surgS = HisConnection.S(r, "ActualSugeryStartTime");
            var surgE = HisConnection.S(r, "ActualSurgeryEndTime");
            if (surgS.Length == 0 || surgE.Length == 0) return;
            var start = Maps.Combine(day, surgS);
            var surgery = Maps.ClockMinutes(start, Maps.Combine(day, surgE));
            if (surgery is null) return;
            var anS = HisConnection.S(r, "AnesthesiaStartTime");
            var anE = HisConnection.S(r, "AnesthesiaEndTime");
            var anesthesia = anS.Length > 0 && anE.Length > 0 ? Maps.ClockMinutes(Maps.Combine(day, anS), Maps.Combine(day, anE)) : null;
            var res = HisConnection.I(r, "OTReservationID");
            var enc = c.IpEncounter(HisConnection.I(r, "AdmissionNo"));
            var anchor = OtAnchor(lines, enc, otProcs.GetValueOrDefault(res));
            var method = anchor is null ? "NONE" : "OT_RESERVATION";
            var ot = HisConnection.Ni(r, "OTID");
            var cc = ot is null ? c.UnmappedCc : c.OtCc(ot);
            var surgeon = HisConnection.Ni(r, "PrimaryDoctorID");
            if (surgeon is > 0)
                o.Add(enc, anchor, method, res, "SURGEON", null, $"{surgeon}", c.Provider(surgeon.Value), surgery.Value, "MEASURED", null, null, cc, start,
                    "INP_OTReservation", $"OT-{res}-SURGEON-{surgeon}");
            foreach (var (id, type) in staff.GetValueOrDefault(res) ?? [])
            {
                if (id.Length == 0 || id == $"{surgeon}") continue;
                // MedicalStaffType is not decoded yet (STP_Parameter): every listed member gets the surgery minutes.
                o.Add(enc, anchor, method, res, "OT_STAFF", type, id, "", surgery.Value, "MEASURED", null, null, cc, start,
                    "INP_OTMedicalStaffDetail", $"OT-{res}-STAFF-{type}-{id}");
            }
            if (anesthesia is not null)
                o.Add(enc, anchor, method, res, "ANESTHETIST", null, null, "", anesthesia.Value, "MEASURED", null, null, cc, Maps.Combine(day, anS),
                    "INP_OTReservation", $"OT-{res}-ANESTHESIA");
        });
    }

    private static async Task OpDoctorAsync(HisConnection db, Settings s, Codes c, StaffWriter o)
    {
        await db.QueryAsync($"""
            SELECT a.AppointmentNo, a.DoctorID, a.ClinicID, a.AppointmentDate, a.ActualVisitStartTime, a.ActualVisitEndTime
            FROM #op o
            INNER JOIN dbo.HIS_Appointment a WITH (NOLOCK) ON a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.AppointmentNo=o.No
            """, s.BindTenant, r =>
        {
            var no = HisConnection.I(r, "AppointmentNo");
            if (c.IsErAppointment(no)) return; // measured from the ER assessment
            var startRaw = HisConnection.S(r, "ActualVisitStartTime");
            var endRaw = HisConnection.S(r, "ActualVisitEndTime");
            if (startRaw.Length == 0 || endRaw.Length == 0) return;
            var day = HisConnection.Dt(r, "AppointmentDate") ?? s.From!.Value;
            var start = Maps.Combine(day, startRaw);
            var min = Maps.ClockMinutes(start, Maps.Combine(day, endRaw));
            if (min is null) return;
            var doctor = HisConnection.Ni(r, "DoctorID");
            var clinic = HisConnection.Ni(r, "ClinicID");
            o.Add(c.OpEncounter(no), null, "NONE", null, "OP_DOCTOR", null, doctor?.ToString(), doctor is > 0 ? c.Provider(doctor.Value) : "",
                min.Value, "MEASURED", null, null, clinic is null ? c.UnmappedCc : c.ClinicCc(clinic), start, "HIS_Appointment", $"APPT-{no}-OP_DOCTOR");
        });
    }

    private static async Task ErStaffAsync(HisConnection db, Settings s, Codes c, StaffWriter o)
    {
        await db.QueryAsync($"""
            SELECT e.AssessmentNo, e.ClinicID, e.AssessmentDate, e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime,
                   e.SignInDoctorId, e.DoctorID
            FROM #er x
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK) ON e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.AssessmentNo=x.No
            """, s.BindTenant, r =>
        {
            var day = HisConnection.Dt(r, "AssessmentDate") ?? s.From!.Value;
            var start = HisConnection.Dt(r, "SignInTime") ?? Maps.Combine(day, HisConnection.S(r, "ArrivalTime"));
            var end = HisConnection.Dt(r, "SignOutTime") ?? HisConnection.Dt(r, "DischargeTime");
            if (end is null) return;
            var min = Maps.ClockMinutes(start, end.Value, 72 * 60);
            if (min is null) return;
            var no = HisConnection.I(r, "AssessmentNo");
            var enc = c.ErEncounter(no);
            var clinic = HisConnection.Ni(r, "ClinicID");
            var cc = clinic is null ? c.UnmappedCc : c.ClinicCc(clinic);
            var doctor = HisConnection.Ni(r, "SignInDoctorId") ?? HisConnection.Ni(r, "DoctorID");
            o.Add(enc, null, "NONE", null, "ER_DOCTOR", null, doctor?.ToString(), doctor is > 0 ? c.Provider(doctor.Value) : "",
                min.Value, "MEASURED", null, null, cc, start, "INP_ERAssessment", $"ER-{no}-ER_DOCTOR");
            o.Add(enc, null, "NONE", null, "ER_NURSE", null, null, "", min.Value, "MEASURED", null, null, cc, start, "INP_ERAssessment", $"ER-{no}-ER_NURSE");
        });
    }

    private static async Task WardAsync(HisConnection db, Settings s, Codes c, StaffWriter o)
    {
        await db.QueryAsync($"""
            SELECT t.TransferNo, t.AdmissionNo, t.TransferDate, t.Duration, t.StartTime, t.EndTime, t.ClinicID
            FROM #ip i
            INNER JOIN dbo.INP_Transfer t WITH (NOLOCK) ON t.SetupID=@SetupId AND t.ProjectID=@ProjectId AND t.AdmissionNo=i.No
            """, s.BindTenant, r =>
        {
            var day = HisConnection.Dt(r, "TransferDate") ?? s.From!.Value;
            var min = Maps.TransferMinutes(HisConnection.I(r, "Duration"), day, HisConnection.S(r, "StartTime"), HisConnection.S(r, "EndTime"));
            if (min is null or <= 0) return;
            var days = Math.Round(min.Value / 1440m, 4, MidpointRounding.AwayFromZero);
            var adm = HisConnection.I(r, "AdmissionNo");
            var clinic = HisConnection.Ni(r, "ClinicID");
            o.Add(c.IpEncounter(adm), null, "NONE", null, "WARD_NURSE", null, null, "", Math.Round(days * s.WardNurseMinutesPerDay, 4), "STANDARD",
                days, "PATIENT_DAY", clinic is null ? c.UnmappedCc : c.ClinicCc(clinic), day, "INP_Transfer", $"BED-{adm}-TRN-{HisConnection.I(r, "TransferNo")}-WARD_NURSE");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<Dictionary<int, HashSet<string>>> OtProceduresAsync(HisConnection db, Settings s)
    {
        var map = new Dictionary<int, HashSet<string>>();
        await db.QueryAsync($"""
            SELECT pm.OTReservationID, pm.ProcedureID
            FROM #ip i
            INNER JOIN dbo.INP_OTReservation ot WITH (NOLOCK) ON ot.SetupID=@SetupId AND ot.ProjectID=@ProjectId AND ot.AdmissionNo=i.No
            INNER JOIN dbo.INP_OTProcedureMapping pm WITH (NOLOCK)
              ON pm.SetupID=ot.SetupID AND pm.ProjectID=ot.ProjectID AND pm.OTReservationID=ot.OTReservationID
            WHERE ISNULL(pm.IsActive, 1) = 1
            """, s.BindTenant, r =>
        {
            var res = HisConnection.I(r, "OTReservationID");
            if (!map.TryGetValue(res, out var set)) map[res] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(HisConnection.S(r, "ProcedureID").Trim());
        });
        return map;
    }

    /// <summary>Rule 3: the admission's counted line for a procedure mapped to the surgery (first line if several).</summary>
    private static string? OtAnchor(LineIndex lines, string enc, HashSet<string>? procs) =>
        procs is null ? null
            : lines.ByEncounter[enc].Where(l => !l.IsMedicine && procs.Contains(l.ProcedureId.Trim())).OrderBy(l => l.Inv).ThenBy(l => l.LineNo).FirstOrDefault()?.Ref;

    private static string? EncounterOf(Codes c, int type, int no) =>
        no <= 0 ? null : type switch { 1 => c.OpEncounter(no), 2 => c.IpEncounter(no), 3 => c.ErEncounter(no), _ => null };

    private static string? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static decimal? Opt(double v) => v == 0 ? null : Maps.Round4(v);

    private static async Task Safe(CsvZipWriter pack, string label, Func<Task> body)
    {
        try { await body(); }
        catch (SqlException ex) { pack.Warn(label, ex); }
    }

    private static async Task Counted(CsvZipWriter pack, string label, ICounted o, Func<Task> body)
    {
        var before = o.Written;
        pack.Step(label);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await body();
        pack.Note($"{label}: {o.Written - before} rows ({sw.Elapsed.TotalSeconds:N0} s)");
    }

    private interface ICounted { int Written { get; } }

    private sealed class ItemWriter(CsvZipWriter.CsvFile csv, Codes c) : ICounted
    {
        private readonly HashSet<string> _refs = new(StringComparer.Ordinal);
        public int Written { get; private set; }

        public void Add(string enc, string? serviceRef, string linkMethod, int? otReservation, string category, string itemKey, string keySystem,
            string? oracle, string? barcode, string? description, decimal quantity, string uom, decimal? conversion, decimal? prescribed,
            string costCentre, string? location, decimal? billed, decimal? hisUnitPrice, DateTime at, string sourceTable, string sourceRef)
        {
            if (enc.Length == 0 || itemKey.Length == 0 || !_refs.Add(sourceRef)) return;
            csv.Write(c.Hospital, enc, enc, serviceRef ?? "", linkMethod, otReservation?.ToString() ?? "", category, itemKey, keySystem,
                oracle ?? "", barcode ?? "", description ?? "", Maps.Dec(quantity), uom, conversion is null ? "" : Maps.Dec(conversion.Value),
                prescribed is null ? "" : Maps.Dec(prescribed.Value), costCentre, location ?? "", billed is null ? "" : Maps.Money(billed.Value),
                hisUnitPrice is null ? "" : Maps.Dec(hisUnitPrice.Value), Maps.DateTime(at), sourceTable, "VIDA-HIS", sourceRef);
            Written++;
        }
    }

    private sealed class StaffWriter(CsvZipWriter.CsvFile csv, Codes c) : ICounted
    {
        private readonly HashSet<string> _refs = new(StringComparer.Ordinal);
        public int Written { get; private set; }

        public void Add(string enc, string? serviceRef, string linkMethod, int? otReservation, string role, string? staffType, string? staffId,
            string providerCode, decimal minutes, string basis, decimal? volume, string? volumeUom, string costCentre, DateTime at,
            string sourceTable, string sourceRef)
        {
            if (enc.Length == 0 || minutes <= 0 || !_refs.Add(sourceRef)) return;
            csv.Write(c.Hospital, enc, enc, serviceRef ?? "", linkMethod, otReservation?.ToString() ?? "", role, staffType ?? "", staffId ?? "",
                providerCode, Maps.Dec(minutes), basis, volume is null ? "" : Maps.Dec(volume.Value), volumeUom ?? "", costCentre,
                Maps.DateTime(at), sourceTable, "VIDA-HIS", sourceRef);
            Written++;
        }
    }
}
