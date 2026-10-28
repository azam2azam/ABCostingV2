namespace HisExtract;

/// <summary>
/// Facts pack phase 2: measured HIS events as ActivityTransaction.
/// Quantity only — never invoice or floor UnitPrice as cost. Recipe explosion stays in VERITA seed.
/// </summary>
public static class ActivityTxnExtract
{
    public static async Task RunAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("35_ActivityTransaction.csv",
            "encounterNo", "serviceRef", "activityCode", "resourceCode", "costCentreCode",
            "quantity", "uom", "occurredAt", "sourceSystem", "sourceRef");
        var o = new Writer(csv, c);

        await Safe(pack, "OP visit minutes", () => Counted(pack, "OP visit minutes", o, () => OpAsync(db, s, c, o)));
        await Safe(pack, "ER stay minutes", () => Counted(pack, "ER stay minutes", o, () => ErAsync(db, s, c, o)));
        await Safe(pack, "OR / anaesthesia minutes", () => Counted(pack, "OR / anaesthesia minutes", o, () => OtAsync(db, s, c, o)));
        await Safe(pack, "IP bed-days", () => Counted(pack, "IP bed-days", o, () => BedsAsync(db, s, c, o)));
        await Safe(pack, "Floor consumption qty", () => Counted(pack, "Floor consumption qty", o, () => FloorAsync(db, s, c, o)));
        await Safe(pack, "IP consumed items qty", () => Counted(pack, "IP consumed items qty", o, () => ConsumedAsync(db, s, c, o)));
        await Safe(pack, "OP dispensed meds qty", () => Counted(pack, "OP dispensed meds qty", o, () => MedsAsync(db, s, c, o)));
        pack.Note($"ActivityTransaction {o.Written} measured rows (VIDA-HIS). UnitPrice / invoice price is not written.");
    }

    private static async Task Safe(CsvZipWriter pack, string label, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            pack.Note($"{label} skipped: {ex.Message}");
        }
    }

    private static async Task Counted(CsvZipWriter pack, string label, Writer o, Func<Task> body)
    {
        var before = o.Written;
        await body();
        pack.Note($"{label}: {o.Written - before} rows");
    }

    private static async Task OpAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT a.AppointmentNo, a.ClinicID, a.AppointmentDate,
                   a.ActualVisitStartTime, a.ActualVisitEndTime, g.GroupID
            FROM dbo.HIS_Appointment a WITH (NOLOCK)
            LEFT JOIN dbo.HIS_Clinic g WITH (NOLOCK)
              ON g.SetupID=a.SetupID AND g.ProjectID=a.ProjectID AND g.ClinicID=a.ClinicID
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
            UNION
            SELECT a.AppointmentNo, a.ClinicID, a.AppointmentDate,
                   a.ActualVisitStartTime, a.ActualVisitEndTime, g.GroupID
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.HIS_Appointment a WITH (NOLOCK)
              ON a.SetupID=m.SetupID AND a.ProjectID=m.ProjectID AND a.AppointmentNo=m.AppointmentNo
            LEFT JOIN dbo.HIS_Clinic g WITH (NOLOCK)
              ON g.SetupID=a.SetupID AND g.ProjectID=a.ProjectID AND g.ClinicID=a.ClinicID
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (a.AppointmentDate < @From OR a.AppointmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var startRaw = HisConnection.S(r, "ActualVisitStartTime");
            var endRaw = HisConnection.S(r, "ActualVisitEndTime");
            if (startRaw.Length == 0 || endRaw.Length == 0) return;
            var day = HisConnection.Dt(r, "AppointmentDate") ?? s.From!.Value;
            var start = Maps.Combine(day, startRaw);
            var min = Maps.ClockMinutes(start, Maps.Combine(day, endRaw));
            if (min is null) return;
            var no = HisConnection.I(r, "AppointmentNo");
            var dental = HisConnection.Ni(r, "GroupID") == 17;
            var act = dental ? "DENT-VISIT" : "CONSULT";
            var res = dental ? "MD-DENT" : "MD-MED";
            o.Add(c.OpEncounter(no), act, res, c.CcFromClinic(HisConnection.Ni(r, "ClinicID")),
                min.Value, "MIN", start, $"APPT-{no}-{act}-{res}");
        });
    }

    private static async Task ErAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT e.AssessmentNo, e.ClinicID, e.AssessmentDate,
                   e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0
              AND e.AssessmentDate >= @From AND e.AssessmentDate < @To
            UNION
            SELECT e.AssessmentNo, e.ClinicID, e.AssessmentDate,
                   e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK)
              ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND e.InvoiceNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (e.AssessmentDate < @From OR e.AssessmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var day = HisConnection.Dt(r, "AssessmentDate") ?? s.From!.Value;
            var start = HisConnection.Dt(r, "SignInTime") ?? Maps.Combine(day, HisConnection.S(r, "ArrivalTime"));
            var end = HisConnection.Dt(r, "SignOutTime") ?? HisConnection.Dt(r, "DischargeTime");
            if (end is null) return;
            var min = Maps.ClockMinutes(start, end.Value, 72 * 60);
            if (min is null) return;
            var no = HisConnection.I(r, "AssessmentNo");
            var enc = c.ErEncounter(no);
            var cc = c.CcFromClinic(HisConnection.Ni(r, "ClinicID"));
            o.Add(enc, "ER-ASSESS", "MD-ER", cc, min.Value, "MIN", start, $"ER-{no}-ER-ASSESS-MD-ER");
            o.Add(enc, "NURSE-ER", "RN-ER", cc, min.Value, "MIN", start, $"ER-{no}-NURSE-ER-RN-ER");
            o.Add(enc, "ER-ASSESS", "RM-ER", cc, min.Value, "MIN", start, $"ER-{no}-ER-ASSESS-RM-ER");
        });
    }

    private static async Task OtAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT ot.OTReservationID, ot.AdmissionNo, ot.OTID, ot.ClinicID, ot.OperationDate,
                   ot.ActualSugeryStartTime, ot.ActualSurgeryEndTime,
                   ot.AnesthesiaStartTime, ot.AnesthesiaEndTime
            FROM dbo.INP_OTReservation ot WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=ot.SetupID AND i.ProjectID=ot.ProjectID AND i.AdmissionNo=ot.AdmissionNo
            WHERE ot.SetupID=@SetupId AND ot.ProjectID=@ProjectId AND ot.AdmissionNo > 0
              AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
            UNION
            SELECT ot.OTReservationID, ot.AdmissionNo, ot.OTID, ot.ClinicID, ot.OperationDate,
                   ot.ActualSugeryStartTime, ot.ActualSurgeryEndTime,
                   ot.AnesthesiaStartTime, ot.AnesthesiaEndTime
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=m.SetupID AND i.ProjectID=m.ProjectID AND i.AdmissionNo=m.AdmissionNo
            INNER JOIN dbo.INP_OTReservation ot WITH (NOLOCK)
              ON ot.SetupID=i.SetupID AND ot.ProjectID=i.ProjectID AND ot.AdmissionNo=i.AdmissionNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (i.AdmissionDate < @From OR i.AdmissionDate >= @To)
            """, s.BindPeriod, r =>
        {
            var day = HisConnection.Dt(r, "OperationDate") ?? s.From!.Value;
            var id = HisConnection.I(r, "OTReservationID");
            var enc = c.IpEncounter(HisConnection.I(r, "AdmissionNo"));
            var cc = HisConnection.Ni(r, "OTID") is { } ot
                ? c.OtCc(ot)
                : c.CcFromClinic(HisConnection.Ni(r, "ClinicID"));
            var surgS = HisConnection.S(r, "ActualSugeryStartTime");
            var surgE = HisConnection.S(r, "ActualSurgeryEndTime");
            if (surgS.Length > 0 && surgE.Length > 0)
            {
                var start = Maps.Combine(day, surgS);
                var min = Maps.ClockMinutes(start, Maps.Combine(day, surgE));
                if (min is not null)
                {
                    o.Add(enc, "SURGERY", "MD-SURG", cc, min.Value, "MIN", start, $"OR-{id}-SURGERY-MD-SURG");
                    o.Add(enc, "SURGERY", "RM-OR", cc, min.Value, "MIN", start, $"OR-{id}-SURGERY-RM-OR");
                }
            }
            var anS = HisConnection.S(r, "AnesthesiaStartTime");
            var anE = HisConnection.S(r, "AnesthesiaEndTime");
            if (anS.Length > 0 && anE.Length > 0)
            {
                var start = Maps.Combine(day, anS);
                var min = Maps.ClockMinutes(start, Maps.Combine(day, anE));
                if (min is not null)
                {
                    o.Add(enc, "ANESTHESIA", "MD-ANES", cc, min.Value, "MIN", start, $"OR-{id}-ANESTHESIA-MD-ANES");
                    o.Add(enc, "ANESTHESIA", "EQ-ANESMC", cc, min.Value, "MIN", start, $"OR-{id}-ANESTHESIA-EQ-ANESMC");
                }
            }
        });
    }

    private static async Task BedsAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        var withTransfer = new HashSet<int>();
        await db.QueryAsync("""
            SELECT t.TransferNo, t.AdmissionNo, t.TransferDate, t.Duration, t.StartTime, t.EndTime, t.ClinicID
            FROM dbo.INP_Transfer t WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=t.SetupID AND i.ProjectID=t.ProjectID AND i.AdmissionNo=t.AdmissionNo
            WHERE t.SetupID=@SetupId AND t.ProjectID=@ProjectId
              AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
            UNION
            SELECT t.TransferNo, t.AdmissionNo, t.TransferDate, t.Duration, t.StartTime, t.EndTime, t.ClinicID
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=m.SetupID AND i.ProjectID=m.ProjectID AND i.AdmissionNo=m.AdmissionNo
            INNER JOIN dbo.INP_Transfer t WITH (NOLOCK)
              ON t.SetupID=i.SetupID AND t.ProjectID=i.ProjectID AND t.AdmissionNo=i.AdmissionNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (i.AdmissionDate < @From OR i.AdmissionDate >= @To)
            """, s.BindPeriod, r =>
        {
            var adm = HisConnection.I(r, "AdmissionNo");
            var day = HisConnection.Dt(r, "TransferDate") ?? s.From!.Value;
            var min = Maps.TransferMinutes(HisConnection.I(r, "Duration"), day,
                HisConnection.S(r, "StartTime"), HisConnection.S(r, "EndTime"));
            if (min is null) return;
            withTransfer.Add(adm);
            var enc = c.IpEncounter(adm);
            var cc = c.CcFromClinic(HisConnection.Ni(r, "ClinicID"));
            var no = HisConnection.I(r, "TransferNo");
            o.Add(enc, "NURSE-WARD", "RM-WARD", cc, min.Value, "MIN", day, $"BED-{adm}-TRN-{no}-RM-WARD");
            var nurse = Math.Round(min.Value * (260m / 1440m), 4, MidpointRounding.AwayFromZero);
            if (nurse > 0)
                o.Add(enc, "NURSE-WARD", "RN-WARD", cc, nurse, "MIN", day, $"BED-{adm}-TRN-{no}-RN-WARD");
        });

        await db.QueryAsync("""
            SELECT i.AdmissionNo, i.AdmissionDate, i.DischargeDate, i.AdmittingClinicID, i.ClinicID, ds.DaysStayed
            FROM dbo.INP_Admission i WITH (NOLOCK)
            LEFT JOIN dbo.INP_DischargeSummary ds WITH (NOLOCK)
              ON ds.SetupID=i.SetupID AND ds.ProjectID=i.ProjectID AND ds.AdmissionNo=i.AdmissionNo
            WHERE i.SetupID=@SetupId AND i.ProjectID=@ProjectId AND i.PatientID > 0
              AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
            UNION
            SELECT i.AdmissionNo, i.AdmissionDate, i.DischargeDate, i.AdmittingClinicID, i.ClinicID, ds.DaysStayed
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=m.SetupID AND i.ProjectID=m.ProjectID AND i.AdmissionNo=m.AdmissionNo
            LEFT JOIN dbo.INP_DischargeSummary ds WITH (NOLOCK)
              ON ds.SetupID=i.SetupID AND ds.ProjectID=i.ProjectID AND ds.AdmissionNo=i.AdmissionNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (i.AdmissionDate < @From OR i.AdmissionDate >= @To)
            """, s.BindPeriod, r =>
        {
            var adm = HisConnection.I(r, "AdmissionNo");
            if (withTransfer.Contains(adm)) return;
            var admit = HisConnection.Dt(r, "AdmissionDate") ?? s.From!.Value;
            var disch = HisConnection.Dt(r, "DischargeDate");
            var los = HisConnection.I(r, "DaysStayed");
            if (los <= 0 && disch is not null) los = Math.Max(0, (int)(disch.Value.Date - admit.Date).TotalDays);
            if (los <= 0) los = 1;
            var enc = c.IpEncounter(adm);
            var cc = c.CcFromClinic(HisConnection.Ni(r, "AdmittingClinicID") ?? HisConnection.Ni(r, "ClinicID"));
            o.Add(enc, "NURSE-WARD", "RM-WARD", cc, los * 1440m, "MIN", admit, $"BED-{adm}-LOS-RM-WARD");
            o.Add(enc, "NURSE-WARD", "RN-WARD", cc, los * 260m, "MIN", admit, $"BED-{adm}-LOS-RN-WARD");
        });
    }

    private static async Task FloorAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT f.SQL_ID, f.EncounterType, f.EncounterNo, f.NursingStationID, f.UnitQty, f.CreatedOn
            FROM dbo.HIS_FloorConsumption f WITH (NOLOCK)
            WHERE f.SetupID=@SetupId AND f.ProjectID=@ProjectId
              AND f.CreatedOn >= @From AND f.CreatedOn < @To
              AND f.UnitQty > 0
              AND (
                    (f.EncounterType=1 AND EXISTS (
                        SELECT 1 FROM dbo.HIS_Appointment a WITH (NOLOCK)
                        WHERE a.SetupID=f.SetupID AND a.ProjectID=f.ProjectID AND a.AppointmentNo=f.EncounterNo
                          AND a.AppointmentDate >= @From AND a.AppointmentDate < @To))
                 OR (f.EncounterType=2 AND EXISTS (
                        SELECT 1 FROM dbo.INP_Admission i WITH (NOLOCK)
                        WHERE i.SetupID=f.SetupID AND i.ProjectID=f.ProjectID AND i.AdmissionNo=f.EncounterNo
                          AND i.AdmissionDate >= @From AND i.AdmissionDate < @To))
                 OR (f.EncounterType=3 AND EXISTS (
                        SELECT 1 FROM dbo.INP_ERAssessment e WITH (NOLOCK)
                        WHERE e.SetupID=f.SetupID AND e.ProjectID=f.ProjectID AND e.AssessmentNo=f.EncounterNo
                          AND e.AssessmentDate >= @From AND e.AssessmentDate < @To))
                  )
            """, s.BindPeriod, r =>
        {
            var enc = EncounterOf(c, HisConnection.I(r, "EncounterType"), HisConnection.I(r, "EncounterNo"));
            if (enc is null) return;
            var qty = Maps.Round4(HisConnection.F(r, "UnitQty"));
            if (qty <= 0) return;
            var id = Convert.ToInt64(r.GetValue(r.GetOrdinal("SQL_ID")), System.Globalization.CultureInfo.InvariantCulture);
            var ns = HisConnection.Ni(r, "NursingStationID");
            var cc = ns is null ? c.UnmappedCc : c.WardCc(ns.Value);
            var at = HisConnection.Dt(r, "CreatedOn") ?? s.From!.Value;
            o.Add(enc, "WOUND-CARE", "CON-PPE", cc, qty, "UNIT", at, $"FLR-{id}-WOUND-CARE-CON-PPE");
        });
    }

    private static async Task ConsumedAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT x.AdmissionNo, x.OrderNo, x.ProcedureID, x.Quantity, x.CreatedOn
            FROM dbo.INP_PatientConsumedItem x WITH (NOLOCK)
            INNER JOIN dbo.INP_Admission i WITH (NOLOCK)
              ON i.SetupID=x.SetupID AND i.ProjectID=x.ProjectID AND i.AdmissionNo=x.AdmissionNo
            WHERE x.SetupID=@SetupId AND x.ProjectID=@ProjectId
              AND x.CreatedOn >= @From AND x.CreatedOn < @To
              AND x.Quantity > 0
              AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
            """, s.BindPeriod, r =>
        {
            var adm = HisConnection.I(r, "AdmissionNo");
            if (adm <= 0) return;
            var qty = HisConnection.I(r, "Quantity");
            var at = HisConnection.Dt(r, "CreatedOn") ?? s.From!.Value;
            var proc = HisConnection.S(r, "ProcedureID");
            var order = HisConnection.I(r, "OrderNo");
            o.Add(c.IpEncounter(adm), "WOUND-CARE", "CON-PPE", c.UnmappedCc, qty, "UNIT", at,
                $"CON-{adm}-{order}-{proc}-WOUND-CARE-CON-PPE");
        });
    }

    private static async Task MedsAsync(HisConnection db, Settings s, Codes c, Writer o)
    {
        await db.QueryAsync("""
            SELECT m.EpisodeID, m.AppointmentNo, m.MedicineCode, a.ClinicID, m.StartDate,
                   m.DispensedQuantity, m.Quantity, m.Qty
            FROM dbo.HIS_ProgNoteMedicine m WITH (NOLOCK)
            INNER JOIN dbo.HIS_Appointment a WITH (NOLOCK)
              ON a.SetupID=m.SetupID AND a.ProjectID=m.ProjectID AND a.AppointmentNo=m.AppointmentNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
            """, s.BindPeriod, r =>
        {
            var qty = HisConnection.I(r, "DispensedQuantity");
            if (qty <= 0) qty = HisConnection.I(r, "Quantity");
            if (qty <= 0) qty = HisConnection.I(r, "Qty");
            if (qty <= 0) return;
            var appt = HisConnection.I(r, "AppointmentNo");
            var at = HisConnection.Dt(r, "StartDate") ?? s.From!.Value;
            var ep = HisConnection.I(r, "EpisodeID");
            var med = HisConnection.I(r, "MedicineCode");
            o.Add(c.OpEncounter(appt), "PHARM-DISPENSE", "DRG-IP", c.CcFromClinic(HisConnection.Ni(r, "ClinicID")),
                qty, "DOSE", at, $"MED-{ep}-{appt}-{med}-PHARM-DISPENSE-DRG-IP");
        });
    }

    private static string? EncounterOf(Codes c, int encounterType, int encounterNo)
    {
        if (encounterNo <= 0) return null;
        return encounterType switch
        {
            1 => c.OpEncounter(encounterNo),
            2 => c.IpEncounter(encounterNo),
            3 => c.ErEncounter(encounterNo),
            _ => null,
        };
    }

    private sealed class Writer(CsvZipWriter.CsvFile csv, Codes c)
    {
        private readonly HashSet<string> _refs = new(StringComparer.Ordinal);
        public int Written { get; private set; }

        public void Add(string enc, string act, string res, string cc, decimal qty, string uom, DateTime at, string sourceRef)
        {
            if (enc.Length == 0 || qty <= 0 || cc.Length == 0) return;
            if (!_refs.Add(sourceRef)) return;
            csv.Write(enc, "", c.Activity(act), c.Resource(res), cc,
                Maps.Dec(qty), uom, Maps.DateTime(at), "VIDA-HIS", sourceRef);
            Written++;
        }
    }
}
