namespace HisExtract;

public static class FactsExtract
{
    public static async Task RunAsync(HisConnection db, Settings s, CsvZipWriter pack)
    {
        var c = new Codes(s);
        var asOf = s.To ?? DateTime.Today;
        pack.WriteManifest("facts", s.From, s.To);
        pack.Note($"Facts {s.From:yyyy-MM-dd} .. {s.To:yyyy-MM-dd} (SQL end exclusive)");

        await PatientsAsync(db, s, c, pack, asOf);
        await EncountersAsync(db, s, c, pack);
        await BillingAsync(db, s, c, pack);
        await ActivityTxnExtract.RunAsync(db, s, c, pack);
        pack.Note("Facts extract finished");
    }

    private static async Task PatientsAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack, DateTime asOf)
    {
        using var csv = pack.Create("32_Patient.csv",
            "hospitalCode", "mrn", "fullName", "birthYear", "sex", "segment", "nationalIdMasked");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT p.PatientType, p.PatientID, p.FirstName, p.MiddleName, p.LastName,
                   p.Gender, p.DateofBirth, p.PatientIdentificationNo, p.IsPregnant
            FROM dbo.HIS_Patient p WITH (NOLOCK)
            INNER JOIN (
                SELECT PatientType, PatientID FROM dbo.HIS_Appointment WITH (NOLOCK)
                WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
                  AND AppointmentDate >= @From AND AppointmentDate < @To
                UNION
                SELECT PatientType, PatientID FROM dbo.INP_Admission WITH (NOLOCK)
                WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
                  AND AdmissionDate >= @From AND AdmissionDate < @To
                UNION
                SELECT PatientType, PatientID FROM dbo.INP_ERAssessment WITH (NOLOCK)
                WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
                  AND AssessmentDate >= @From AND AssessmentDate < @To
                UNION
                SELECT PatientType, PatientID FROM dbo.HIS_InvoiceMain WITH (NOLOCK)
                WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
                  AND InvoiceDate >= @From AND InvoiceDate < @To AND CancelledOn IS NULL
            ) x ON x.PatientType = p.PatientType AND x.PatientID = p.PatientID
            WHERE p.SetupID=@SetupId AND p.ProjectID=@ProjectId
            """, s.BindPeriod, r =>
        {
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0 || !seen.Add(mrn)) return;
            var dob = HisConnection.Dt(r, "DateofBirth");
            var year = dob?.Year ?? 1900;
            csv.Write(c.Hospital, mrn,
                Maps.FullName(HisConnection.S(r, "FirstName"), HisConnection.S(r, "MiddleName"), HisConnection.S(r, "LastName")),
                Maps.Int(year), Maps.Sex(HisConnection.I(r, "Gender")),
                Maps.Segment(year, HisConnection.B(r, "IsPregnant"), asOf),
                Maps.MaskId(HisConnection.S(r, "PatientIdentificationNo")));
        });
    }

    private static async Task EncountersAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("33_Encounter.csv",
            "patientMrn", "hospitalCode", "departmentCode", "serviceLineCode", "payerCode",
            "drgCode", "packageCode", "providerCode", "encounterNo", "encType",
            "admitAt", "dischargeAt", "lengthOfStay", "isSurgical", "acuity");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Mapping §9.2: service line comes from the performing clinic's HIS_ClinicGroup, same as Department.
        var slByClinic = new Dictionary<int, string>();
        await db.QueryAsync("""
            SELECT ClinicID, GroupID
            FROM dbo.HIS_Clinic WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId
            """, s.BindTenant, r =>
        {
            var id = HisConnection.I(r, "ClinicID");
            var g = HisConnection.Ni(r, "GroupID");
            slByClinic[id] = g is null ? c.UnmappedSl : c.ServiceLine(g.Value);
        });
        string SlFromClinic(int? clinicId) =>
            clinicId is { } id && slByClinic.TryGetValue(id, out var sl) ? sl : c.UnmappedSl;

        await db.QueryAsync("""
            SELECT a.AppointmentNo, a.PatientType, a.PatientID, a.ClinicID, a.DoctorID, a.CompanyID,
                   a.AppointmentDate, a.StartTime, a.EndTime, a.ActualVisitStartTime, a.ActualVisitEndTime,
                   a.EndDate
            FROM dbo.HIS_Appointment a WITH (NOLOCK)
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
            UNION ALL
            SELECT a.AppointmentNo, a.PatientType, a.PatientID, a.ClinicID, a.DoctorID, a.CompanyID,
                   a.AppointmentDate, a.StartTime, a.EndTime, a.ActualVisitStartTime, a.ActualVisitEndTime,
                   a.EndDate
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.HIS_Appointment a WITH (NOLOCK)
              ON a.SetupID=m.SetupID AND a.ProjectID=m.ProjectID AND a.AppointmentNo=m.AppointmentNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (a.AppointmentDate < @From OR a.AppointmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0) return;
            var no = c.OpEncounter(HisConnection.I(r, "AppointmentNo"));
            if (!seen.Add(no)) return;
            var day = HisConnection.Dt(r, "AppointmentDate") ?? s.From!.Value;
            var start = Maps.Combine(day, FirstTime(HisConnection.S(r, "ActualVisitStartTime"), HisConnection.S(r, "StartTime")));
            var endSrc = HisConnection.Dt(r, "EndDate");
            var end = Maps.Combine(endSrc ?? day, FirstTime(HisConnection.S(r, "ActualVisitEndTime"), HisConnection.S(r, "EndTime")));
            csv.Write(
                mrn,
                c.Hospital, c.DeptFromClinic(HisConnection.Ni(r, "ClinicID")), SlFromClinic(HisConnection.Ni(r, "ClinicID")),
                c.PayerOrSelf(HisConnection.Ni(r, "CompanyID")),
                "", "", c.ProviderOrEmpty(HisConnection.Ni(r, "DoctorID")),
                no, "OP", Maps.DateTime(start), Maps.DateTime(end), "0", "false", "ROUTINE");
        });

        await db.QueryAsync("""
            SELECT i.AdmissionNo, i.PatientType, i.PatientID, i.ClinicID, i.AdmittingClinicID, i.DoctorID,
                   i.AdmittingDoctorID, i.CompanyId, i.AdmissionDate, i.DischargeDate,
                   ds.DaysStayed,
                   CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.INP_OTReservation ot WITH (NOLOCK)
                        WHERE ot.SetupID=i.SetupID AND ot.ProjectID=i.ProjectID AND ot.AdmissionNo=i.AdmissionNo)
                     THEN 1 ELSE 0 END AS bit) AS IsSurgical
            FROM dbo.INP_Admission i WITH (NOLOCK)
            LEFT JOIN dbo.INP_DischargeSummary ds WITH (NOLOCK)
              ON ds.SetupID=i.SetupID AND ds.ProjectID=i.ProjectID AND ds.AdmissionNo=i.AdmissionNo
            WHERE i.SetupID=@SetupId AND i.ProjectID=@ProjectId AND i.PatientID > 0
              AND i.AdmissionDate >= @From AND i.AdmissionDate < @To
            UNION ALL
            SELECT i.AdmissionNo, i.PatientType, i.PatientID, i.ClinicID, i.AdmittingClinicID, i.DoctorID,
                   i.AdmittingDoctorID, i.CompanyId, i.AdmissionDate, i.DischargeDate,
                   ds.DaysStayed,
                   CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM dbo.INP_OTReservation ot WITH (NOLOCK)
                        WHERE ot.SetupID=i.SetupID AND ot.ProjectID=i.ProjectID AND ot.AdmissionNo=i.AdmissionNo)
                     THEN 1 ELSE 0 END AS bit) AS IsSurgical
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
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0) return;
            var no = c.IpEncounter(HisConnection.I(r, "AdmissionNo"));
            if (!seen.Add(no)) return;
            var admit = HisConnection.Dt(r, "AdmissionDate") ?? s.From!.Value;
            var disch = HisConnection.Dt(r, "DischargeDate");
            var los = HisConnection.I(r, "DaysStayed");
            if (los <= 0 && disch is not null) los = Math.Max(0, (int)(disch.Value.Date - admit.Date).TotalDays);
            var clinic = HisConnection.Ni(r, "AdmittingClinicID") ?? HisConnection.Ni(r, "ClinicID");
            var doc = HisConnection.Ni(r, "AdmittingDoctorID") ?? HisConnection.Ni(r, "DoctorID");
            csv.Write(
                mrn,
                c.Hospital, c.DeptFromClinic(clinic), SlFromClinic(clinic),
                c.PayerOrSelf(HisConnection.Ni(r, "CompanyId")),
                "", "", c.ProviderOrEmpty(doc),
                no, "IP", Maps.DateTime(admit), disch is null ? "" : Maps.DateTime(disch.Value),
                Maps.Int(los), Maps.Bool(HisConnection.B(r, "IsSurgical")), "ROUTINE");
        });

        await db.QueryAsync("""
            SELECT e.AssessmentNo, e.PatientType, e.PatientID, e.ClinicID, e.DoctorID, e.AssessmentDate,
                   e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0
              AND e.AssessmentDate >= @From AND e.AssessmentDate < @To
            UNION ALL
            SELECT e.AssessmentNo, e.PatientType, e.PatientID, e.ClinicID, e.DoctorID, e.AssessmentDate,
                   e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK)
              ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND e.InvoiceNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (e.AssessmentDate < @From OR e.AssessmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0) return;
            var no = c.ErEncounter(HisConnection.I(r, "AssessmentNo"));
            if (!seen.Add(no)) return;
            var day = HisConnection.Dt(r, "AssessmentDate") ?? s.From!.Value;
            var start = HisConnection.Dt(r, "SignInTime") ?? Maps.Combine(day, HisConnection.S(r, "ArrivalTime"));
            var end = HisConnection.Dt(r, "SignOutTime") ?? HisConnection.Dt(r, "DischargeTime") ?? start;
            csv.Write(
                mrn,
                c.Hospital, c.DeptFromClinic(HisConnection.Ni(r, "ClinicID")), SlFromClinic(HisConnection.Ni(r, "ClinicID")),
                c.SelfPayer, "", "", c.ProviderOrEmpty(HisConnection.Ni(r, "DoctorID")),
                no, "ER", Maps.DateTime(start), Maps.DateTime(end), "0", "false", "ROUTINE");
        });
    }

    private static async Task BillingAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var svc = pack.Create("34_EncounterService.csv",
            "encounterNo", "procedureCode", "costCentreCode", "providerCode", "quantity", "performedAt", "sourceSystem", "sourceRef");
        using var rev = pack.Create("36_RevenueTransaction.csv",
            "encounterNo", "procedureCode", "payerCode", "grossAmount", "discount", "netAmount", "postedAt", "sourceSystem", "sourceRef");

        await db.QueryAsync("""
            SELECT m.TransactionType, m.InvoiceNo, m.InvoiceDate, m.AppointmentNo, m.AdmissionNo,
                   m.ClinicID, m.CompanyID, m.PerformedServiceClinicID,
                   d.LineItemNo, d.ProcedureID, d.Price, d.Quantity, d.DiscountAmount, d.WaivedAmount,
                   d.PatientShare, d.CompanyShare, d.CostCenterID, d.PrimaryDoctorID, d.PrimaryClinicID,
                   d.PerformDate, hp.IsMedicine, er.AssessmentNo
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.HIS_InvoiceDetail d WITH (NOLOCK)
              ON d.SetupID=m.SetupID AND d.ProjectID=m.ProjectID
             AND d.TransactionType=m.TransactionType AND d.InvoiceNo=m.InvoiceNo
            LEFT JOIN dbo.HIS_Procedure hp WITH (NOLOCK)
              ON hp.SetupID=d.SetupID AND hp.ProjectID=d.ProjectID AND hp.ProcedureID=d.ProcedureID
            LEFT JOIN dbo.INP_ERAssessment er WITH (NOLOCK)
              ON er.SetupID=m.SetupID AND er.ProjectID=m.ProjectID AND er.InvoiceNo=m.InvoiceNo
             AND ISNULL(m.AppointmentNo,0)=0 AND ISNULL(m.AdmissionNo,0)=0
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To
              AND m.CancelledOn IS NULL
            """, s.BindPeriod, r =>
        {
            var enc = EncounterOf(c, HisConnection.Ni(r, "AdmissionNo"), HisConnection.Ni(r, "AppointmentNo"), HisConnection.Ni(r, "AssessmentNo"));
            if (enc is null) return;
            var posted = HisConnection.Dt(r, "InvoiceDate") ?? s.From!.Value;
            var performed = HisConnection.Dt(r, "PerformDate") ?? posted;
            var qty = Math.Max(1, HisConnection.I(r, "Quantity"));
            var price = Maps.Round4(HisConnection.F(r, "Price"));
            var gross = decimal.Round(price * qty, 4, MidpointRounding.AwayFromZero);
            var discount = Maps.Round4(HisConnection.F(r, "DiscountAmount") + HisConnection.F(r, "WaivedAmount"));
            var net = Maps.Round4(HisConnection.F(r, "PatientShare") + HisConnection.F(r, "CompanyShare"));
            if (net == 0) net = decimal.Round(gross - discount, 4, MidpointRounding.AwayFromZero);
            var procId = HisConnection.S(r, "ProcedureID");
            var medicine = HisConnection.B(r, "IsMedicine");
            var procCode = medicine || procId.Length == 0 ? "" : c.Procedure(procId);
            var clinic = HisConnection.Ni(r, "PrimaryClinicID") ?? HisConnection.Ni(r, "PerformedServiceClinicID") ?? HisConnection.Ni(r, "ClinicID");
            var tt = HisConnection.I(r, "TransactionType");
            var inv = HisConnection.I(r, "InvoiceNo");
            var line = HisConnection.I(r, "LineItemNo");
            var payer = c.PayerOrSelf(HisConnection.Ni(r, "CompanyID"));

            if (procCode.Length > 0)
            {
                svc.Write(enc, procCode, c.CcFromInvoice(HisConnection.S(r, "CostCenterID"), clinic),
                    c.ProviderOrEmpty(HisConnection.Ni(r, "PrimaryDoctorID")),
                    Maps.Dec(qty), Maps.DateTime(performed), "VIDA-HIS", $"INV-{tt}-{inv}-{line}");
            }

            rev.Write(enc, procCode, payer, Maps.Money(gross), Maps.Money(discount), Maps.Money(net),
                Maps.DateTime(posted), "VIDA-HIS", $"REV-{tt}-{inv}-{line}");
        });

        await db.QueryAsync("""
            SELECT r.RefundNo, r.RefundDate, r.OriginalTransactionType, r.OriginalTransactionRef,
                   d.InvoiceLineItemNo, d.ProcedureID, d.RefundAmount, d.PatientShare, d.CompanyShare,
                   m.AppointmentNo, m.AdmissionNo, m.CompanyID
            FROM dbo.HIS_RefundMain r WITH (NOLOCK)
            INNER JOIN dbo.HIS_RefundDetail d WITH (NOLOCK)
              ON d.SetupID=r.SetupID AND d.ProjectID=r.ProjectID AND d.RefundNo=r.RefundNo
            LEFT JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
              ON m.SetupID=r.SetupID AND m.ProjectID=r.ProjectID
             AND m.TransactionType=r.OriginalTransactionType AND m.InvoiceNo=r.OriginalTransactionRef
            WHERE r.SetupID=@SetupId AND r.ProjectID=@ProjectId
              AND r.RefundDate >= @From AND r.RefundDate < @To
              AND r.CancelledOn IS NULL
            """, s.BindPeriod, r =>
        {
            var enc = EncounterOf(c, HisConnection.Ni(r, "AdmissionNo"), HisConnection.Ni(r, "AppointmentNo"), null);
            if (enc is null) return;
            var amt = Maps.Round4(HisConnection.F(r, "RefundAmount"));
            if (amt == 0) amt = Maps.Round4(HisConnection.F(r, "PatientShare") + HisConnection.F(r, "CompanyShare"));
            var procId = HisConnection.S(r, "ProcedureID");
            var posted = HisConnection.Dt(r, "RefundDate") ?? s.From!.Value;
            rev.Write(enc, procId.Length == 0 ? "" : c.Procedure(procId),
                c.PayerOrSelf(HisConnection.Ni(r, "CompanyID")),
                Maps.Money(-amt), "0", Maps.Money(-amt), Maps.DateTime(posted), "VIDA-HIS",
                $"REF-{HisConnection.I(r, "RefundNo")}-{HisConnection.I(r, "InvoiceLineItemNo")}");
        });
    }

    private static string? EncounterOf(Codes c, int? admissionNo, int? appointmentNo, int? assessmentNo)
    {
        if (admissionNo is > 0) return c.IpEncounter(admissionNo.Value);
        if (appointmentNo is > 0) return c.OpEncounter(appointmentNo.Value);
        if (assessmentNo is > 0) return c.ErEncounter(assessmentNo.Value);
        return null;
    }

    private static string FirstTime(params string[] times)
    {
        foreach (var t in times)
            if (!string.IsNullOrWhiteSpace(t)) return t;
        return "";
    }
}
