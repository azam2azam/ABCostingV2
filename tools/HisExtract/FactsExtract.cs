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
                SELECT a.PatientType, a.PatientID FROM dbo.HIS_Appointment a WITH (NOLOCK)
                WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
                  AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
                  AND EXISTS (SELECT 1 FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
                              WHERE m.SetupID=a.SetupID AND m.ProjectID=a.ProjectID AND m.AppointmentNo=a.AppointmentNo AND m.CancelledOn IS NULL)
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

        var stubs = 0;
        await db.QueryAsync("""
            SELECT a.PatientID FROM dbo.HIS_Appointment a WITH (NOLOCK)
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
              AND EXISTS (SELECT 1 FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
                          WHERE m.SetupID=a.SetupID AND m.ProjectID=a.ProjectID AND m.AppointmentNo=a.AppointmentNo AND m.CancelledOn IS NULL)
            UNION
            SELECT PatientID FROM dbo.INP_Admission WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
              AND AdmissionDate >= @From AND AdmissionDate < @To
            UNION
            SELECT PatientID FROM dbo.INP_ERAssessment WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
              AND AssessmentDate >= @From AND AssessmentDate < @To
            UNION
            SELECT PatientID FROM dbo.HIS_InvoiceMain WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND PatientID > 0
              AND InvoiceDate >= @From AND InvoiceDate < @To AND CancelledOn IS NULL
            """, s.BindPeriod, r =>
        {
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0 || !seen.Add(mrn)) return;
            stubs++;
            csv.Write(c.Hospital, mrn, "Unknown patient", "1900", "M", "ADULT", "");
        });
        if (stubs > 0) pack.Note($"Patient: {stubs} encounter PatientIDs had no HIS_Patient row and were added as stubs.");
    }

    private static async Task EncountersAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("33_Encounter.csv",
            "patientMrn", "hospitalCode", "departmentCode", "serviceLineCode", "payerCode",
            "drgCode", "packageCode", "providerCode", "encounterNo", "encType",
            "admitAt", "dischargeAt", "lengthOfStay", "isSurgical", "acuity", "sourceAssessmentNo",
            "hisStatus", "hisErStatus", "hisVisitClosed", "hisArrivedAt");
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

        var ipDrg = await DrgExtract.ByAdmissionAsync(db, s, pack);
        var opDrg = await DrgExtract.ByAppointmentAsync(db, s, pack);

        await db.QueryAsync("""
            SELECT i.AdmissionNo, i.PatientType, i.PatientID, i.ClinicID, i.AdmittingClinicID, i.DoctorID,
                   i.AdmittingDoctorID, i.CompanyId, i.AdmissionDate, i.DischargeDate, i.Status,
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
                   i.AdmittingDoctorID, i.CompanyId, i.AdmissionDate, i.DischargeDate, i.Status,
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
                DrgExtract.Lookup(ipDrg, HisConnection.I(r, "AdmissionNo")), "", c.ProviderOrEmpty(doc),
                no, "IP", Maps.DateTime(admit), disch is null ? "" : Maps.DateTime(disch.Value),
                Maps.Int(los), Maps.Bool(HisConnection.B(r, "IsSurgical")), "ROUTINE", "",
                HisConnection.S(r, "Status"), "", "", "");
        });

        // ER before OP: an assessment linked to an appointment claims that encounter number, so the
        // appointment is not written again as OP. Its invoice lines already point at that number.
        // Packs are one month each and a visit can span two, so links are read for the months around
        // this one. HIS_InvoiceMain has no key on InvoiceNo alone: invoices are read by date and hashed,
        // never looked up per assessment.
        const string ErNearby = "e.AssessmentDate >= DATEADD(month,-3,@From) AND e.AssessmentDate < DATEADD(month,3,@To)";
        const string ErColumns = """
            e.AssessmentNo, e.PatientType, e.PatientID, e.ClinicID, e.DoctorID, e.AssessmentDate,
            e.ArrivalTime, e.DischargeTime, e.SignInTime, e.SignOutTime,
            e.Status, e.ERStatus, ap.IsVisitClosed, ap.ArrivedOn,
            ap.AppointmentNo AS LinkedAppointment, ap.CompanyID AS AppointmentCompany,
            CAST(CASE WHEN e.AppointmentID > 0 AND ap.AppointmentNo IS NULL THEN 1 ELSE 0 END AS bit) AS AppointmentRefused
            """;
        const string ErAppointment = """
            LEFT JOIN dbo.HIS_Appointment ap WITH (NOLOCK)
              ON ap.SetupID=e.SetupID AND ap.ProjectID=e.ProjectID AND ap.AppointmentNo=e.AppointmentID AND e.AppointmentID > 0
              AND ap.PatientID=e.PatientID
            """;
        var invoiceLinks = new Dictionary<int, (int Appointment, int? Company)>();
        await db.QueryAsync("""
            SELECT e.AssessmentNo, m.AppointmentNo, m.CompanyID
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK)
              ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo AND e.PatientID=m.PatientID
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= DATEADD(month,-3,@From) AND m.InvoiceDate < DATEADD(month,3,@To)
              AND m.CancelledOn IS NULL AND e.InvoiceNo > 0
            OPTION (HASH JOIN)
            """, s.BindPeriod, r =>
        {
            var assessment = HisConnection.I(r, "AssessmentNo");
            var appointment = HisConnection.I(r, "AppointmentNo");
            invoiceLinks[assessment] = (appointment, HisConnection.Ni(r, "CompanyID"));
            c.LinkErToAppointment(assessment, appointment);
        });
        pack.Note($"ER links by InvoiceNo: {invoiceLinks.Count}.");
        var appointmentLinks = 0;
        await db.QueryAsync($"""
            SELECT e.AssessmentNo, ap.AppointmentNo
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            INNER JOIN dbo.HIS_Appointment ap WITH (NOLOCK)
              ON ap.SetupID=e.SetupID AND ap.ProjectID=e.ProjectID AND ap.AppointmentNo=e.AppointmentID AND ap.PatientID=e.PatientID
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0 AND e.AppointmentID > 0 AND {ErNearby}
            """, s.BindPeriod, r =>
        {
            appointmentLinks++;
            c.LinkErToAppointment(HisConnection.I(r, "AssessmentNo"), HisConnection.I(r, "AppointmentNo"));
        });
        pack.Note($"ER links by AppointmentID: {appointmentLinks}.");

        int erByAppointment = 0, erByInvoice = 0, erStandalone = 0, erRefused = 0, erSharing = 0;
        void WriteEr(Microsoft.Data.SqlClient.SqlDataReader r)
        {
            var mrn = c.Mrn(HisConnection.I(r, "PatientID"));
            if (mrn.Length == 0) return;
            var assessment = HisConnection.I(r, "AssessmentNo");
            var byAppointment = HisConnection.Ni(r, "LinkedAppointment");
            int? byInvoice = invoiceLinks.TryGetValue(assessment, out var invoiced) ? invoiced.Appointment : null;
            var appointment = byAppointment ?? byInvoice;
            if (appointment is { } linked) c.LinkErToAppointment(assessment, linked);
            var no = c.ErEncounter(assessment);
            if (HisConnection.B(r, "AppointmentRefused")) erRefused++;
            if (!seen.Add(no))
            {
                if (appointment is not null) erSharing++;
                return;
            }
            if (byAppointment is not null) erByAppointment++;
            else if (byInvoice is not null) erByInvoice++;
            else erStandalone++;
            var day = HisConnection.Dt(r, "AssessmentDate") ?? s.From!.Value;
            var start = HisConnection.Dt(r, "SignInTime") ?? Maps.Combine(day, HisConnection.S(r, "ArrivalTime"));
            var end = HisConnection.Dt(r, "SignOutTime") ?? HisConnection.Dt(r, "DischargeTime") ?? start;
            var company = HisConnection.Ni(r, "AppointmentCompany") ?? invoiced.Company;
            var drg = appointment is { } a ? DrgExtract.Lookup(opDrg, a) : "";
            csv.Write(
                mrn,
                c.Hospital, c.DeptFromClinic(HisConnection.Ni(r, "ClinicID")), SlFromClinic(HisConnection.Ni(r, "ClinicID")),
                c.PayerOrSelf(company), drg, "", c.ProviderOrEmpty(HisConnection.Ni(r, "DoctorID")),
                no, "ER", Maps.DateTime(start), Maps.DateTime(end), "0", "false", "ROUTINE", $"{assessment}",
                HisConnection.S(r, "Status"), HisConnection.S(r, "ERStatus"), Flag(r, "IsVisitClosed"), When(HisConnection.Dt(r, "ArrivedOn")));
        }
        await db.QueryAsync($"""
            SELECT {ErColumns}
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            {ErAppointment}
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0
              AND e.AssessmentDate >= @From AND e.AssessmentDate < @To
            UNION ALL
            SELECT {ErColumns}
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.INP_ERAssessment e WITH (NOLOCK)
              ON e.SetupID=m.SetupID AND e.ProjectID=m.ProjectID AND e.InvoiceNo=m.InvoiceNo
            {ErAppointment}
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND e.InvoiceNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
              AND (e.AssessmentDate < @From OR e.AssessmentDate >= @To)
            """, s.BindPeriod, WriteEr);

        // An assessment dated outside this month still belongs here when its appointment is in this pack.
        var inPack = new HashSet<int>();
        await db.QueryAsync("""
            SELECT a.AppointmentNo FROM dbo.HIS_Appointment a WITH (NOLOCK)
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
            UNION
            SELECT m.AppointmentNo FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo > 0
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To AND m.CancelledOn IS NULL
            """, s.BindPeriod, r => inPack.Add(HisConnection.I(r, "AppointmentNo")));
        await db.QueryAsync($"""
            SELECT {ErColumns}
            FROM dbo.INP_ERAssessment e WITH (NOLOCK)
            {ErAppointment}
            WHERE e.SetupID=@SetupId AND e.ProjectID=@ProjectId AND e.PatientID > 0 AND {ErNearby}
              AND (e.AssessmentDate < @From OR e.AssessmentDate >= @To)
            """, s.BindPeriod, r =>
        {
            var assessment = HisConnection.I(r, "AssessmentNo");
            var linked = HisConnection.Ni(r, "LinkedAppointment") ?? (invoiceLinks.TryGetValue(assessment, out var invoiced) ? invoiced.Appointment : null);
            if (linked is { } appointment && inPack.Contains(appointment)) WriteEr(r);
        });
        pack.Note($"ER encounters: {erByAppointment} linked by AppointmentID, {erByInvoice} linked by InvoiceNo, {erStandalone} with no appointment. {erRefused} AppointmentID values had no appointment for the same patient; {erSharing} assessments shared an appointment with another.");

        await db.QueryAsync("""
            SELECT a.AppointmentNo, a.PatientType, a.PatientID, a.ClinicID, a.DoctorID, a.CompanyID,
                   a.AppointmentDate, a.StartTime, a.EndTime, a.ActualVisitStartTime, a.ActualVisitEndTime,
                   a.EndDate, a.Status, a.IsVisitClosed, a.ArrivedOn
            FROM dbo.HIS_Appointment a WITH (NOLOCK)
            WHERE a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.PatientID > 0
              AND a.AppointmentDate >= @From AND a.AppointmentDate < @To
              -- An appointment with no invoice is not an encounter.
              AND EXISTS (SELECT 1 FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
                          WHERE m.SetupID=a.SetupID AND m.ProjectID=a.ProjectID AND m.AppointmentNo=a.AppointmentNo AND m.CancelledOn IS NULL)
            UNION ALL
            SELECT a.AppointmentNo, a.PatientType, a.PatientID, a.ClinicID, a.DoctorID, a.CompanyID,
                   a.AppointmentDate, a.StartTime, a.EndTime, a.ActualVisitStartTime, a.ActualVisitEndTime,
                   a.EndDate, a.Status, a.IsVisitClosed, a.ArrivedOn
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
                DrgExtract.Lookup(opDrg, HisConnection.I(r, "AppointmentNo")), "", c.ProviderOrEmpty(HisConnection.Ni(r, "DoctorID")),
                no, "OP", Maps.DateTime(start), Maps.DateTime(end), "0", "false", "ROUTINE", "",
                HisConnection.S(r, "Status"), "", Flag(r, "IsVisitClosed"), When(HisConnection.Dt(r, "ArrivedOn")));
        });
    }

    private static async Task BillingAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var svc = pack.Create("34_EncounterService.csv",
            "encounterNo", "procedureCode", "costCentreCode", "providerCode", "quantity", "performedAt", "sourceSystem", "sourceRef", "isChildInvoice");
        using var rev = pack.Create("36_RevenueTransaction.csv",
            "encounterNo", "procedureCode", "payerCode", "grossAmount", "discount", "netAmount", "postedAt", "sourceSystem", "sourceRef",
            "hisLineStatus", "isPerformed", "performType", "isReturned", "isRefunded", "refundedAmount",
            "hisUnitPrice", "hisQuantity", "discountType", "hisDiscountAmount", "waivedAmount", "waiverId", "patientShare", "companyShare",
            "taxAmount", "patientTaxAmount", "companyTaxAmount", "outlierPayment", "claimedAmount", "settledAmount", "rejectedAmount",
            "isZeroPrice", "isPackageSelected", "packageProcedureId", "packageSequenceNo", "isPackageInvoice", "billType", "isAcceptedWithDrg",
            "hisInvoiceType", "hisMainInvoiceNo", "isChildInvoice");
        var noEncounter = 0;

        await db.QueryAsync("""
            SELECT m.TransactionType, m.InvoiceNo, m.InvoiceDate, m.AppointmentNo, m.AdmissionNo,
                   m.ClinicID, m.CompanyID, m.PerformedServiceClinicID,
                   d.LineItemNo, d.ProcedureID, d.Price, d.Quantity, d.DiscountAmount, d.WaivedAmount,
                   d.PatientShare, d.CompanyShare, d.CostCenterID, d.PrimaryDoctorID, d.PrimaryClinicID,
                   d.PerformDate, d.Status, d.IsPerformed, d.PerformType, d.IsReturned, d.IsRefunded, d.RefundedAmount,
                   d.DiscountType, d.WaiverID, d.TaxAmount, d.PatientTaxAmount, d.CompanyTaxAmount, d.OutlierPayment,
                   d.ClaimedAmount, d.SettledAmount, d.RejectedAmount, d.IsZeroPrice, d.IsPackageSelected,
                   d.PackageProcedureID, d.PackageSequenceNo, m.IsPackageInvoice, m.BillType, m.IsAcceptedWithDRG, m.InvoiceType, m.MainInvoiceNo,
                   hp.IsMedicine, er.AssessmentNo
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
            if (enc is null)
            {
                noEncounter++;
                return;
            }
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
            // InvoiceType 2 is a child invoice: the parent line carries the package's money and cost.
            var child = Maps.Bool(HisConnection.I(r, "InvoiceType") == 2);

            if (procCode.Length > 0)
            {
                svc.Write(enc, procCode, c.CcFromInvoice(HisConnection.S(r, "CostCenterID"), clinic),
                    c.ProviderOrEmpty(HisConnection.Ni(r, "PrimaryDoctorID")),
                    Maps.Dec(qty), Maps.DateTime(performed), "VIDA-HIS", $"INV-{tt}-{inv}-{line}", child);
            }

            rev.Write(enc, procCode, payer, Maps.Money(gross), Maps.Money(discount), Maps.Money(net),
                Maps.DateTime(posted), "VIDA-HIS", $"REV-{tt}-{inv}-{line}",
                HisConnection.S(r, "Status"), Flag(r, "IsPerformed"), HisConnection.S(r, "PerformType"), Flag(r, "IsReturned"), Flag(r, "IsRefunded"),
                Maps.Money(Maps.Round4(HisConnection.F(r, "RefundedAmount"))),
                // As HIS holds them, for reference: gross, discount and net above stay what they are.
                Maps.Money(price), HisConnection.S(r, "Quantity"), HisConnection.S(r, "DiscountType"),
                Stored(r, "DiscountAmount"), Stored(r, "WaivedAmount"), HisConnection.S(r, "WaiverID"),
                Stored(r, "PatientShare"), Stored(r, "CompanyShare"), Stored(r, "TaxAmount"), Stored(r, "PatientTaxAmount"), Stored(r, "CompanyTaxAmount"),
                Stored(r, "OutlierPayment"), Stored(r, "ClaimedAmount"), Stored(r, "SettledAmount"), Stored(r, "RejectedAmount"),
                Flag(r, "IsZeroPrice"), Flag(r, "IsPackageSelected"), HisConnection.S(r, "PackageProcedureID"), HisConnection.S(r, "PackageSequenceNo"),
                Flag(r, "IsPackageInvoice"), HisConnection.S(r, "BillType"), Flag(r, "IsAcceptedWithDRG"),
                HisConnection.S(r, "InvoiceType"), HisConnection.S(r, "MainInvoiceNo"), child);
        });
        pack.Note($"Invoice lines with no appointment, admission or ER assessment, not loaded: {noEncounter}.");
        await RefundsAsync(db, s, c, pack);
        await PackageItemsAsync(db, s, c, pack);
    }

    /// <summary>Package components on the period's invoices, as HIS holds them. Not services and not revenue.</summary>
    private static async Task PackageItemsAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("38_InvoicePackageItem.csv",
            "hospitalCode", "encounterNo", "hisEncounterNo", "procedureCode", "hisProcedureId", "packageProcedureId", "packageLineItemNo", "packageSequence",
            "transactionType", "invoiceNo", "lineItemNo", "invoiceDate", "price", "discountedCharge", "patientShare", "patientPaid", "companyShare", "companyPaid",
            "childInvoiceNo", "childInvoiceLineItemNo", "isCompleted", "isRefunded", "sourceSystem", "sourceRef");
        int lines = 0, noEncounter = 0;
        await db.QueryAsync("""
            SELECT p.TransactionType, p.InvoiceNo, p.LineItemNo, p.ProcedureID, p.ProcedurePackageID, p.ProcedurePackageLineItemNo, p.ProcedurePackageSequence,
                   p.Price, p.DiscountedCharge, p.PatientShare, p.PatientPaid, p.CompanyShare, p.CompanyPaid,
                   p.ChildInvoiceNo, p.ChildInvoiceLineItemNo, p.IsCompleted, p.IsRefunded,
                   m.InvoiceDate, m.AppointmentNo, m.AdmissionNo, er.AssessmentNo
            FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
            INNER JOIN dbo.HIS_InvoicePackageItem p WITH (NOLOCK)
              ON p.SetupID=m.SetupID AND p.ProjectID=m.ProjectID
             AND p.TransactionType=m.TransactionType AND p.InvoiceNo=m.InvoiceNo
            LEFT JOIN dbo.INP_ERAssessment er WITH (NOLOCK)
              ON er.SetupID=m.SetupID AND er.ProjectID=m.ProjectID AND er.InvoiceNo=m.InvoiceNo
             AND ISNULL(m.AppointmentNo,0)=0 AND ISNULL(m.AdmissionNo,0)=0
            WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId
              AND m.InvoiceDate >= @From AND m.InvoiceDate < @To
              AND m.CancelledOn IS NULL
            """, s.BindPeriod, r =>
        {
            var enc = EncounterOf(c, HisConnection.Ni(r, "AdmissionNo"), HisConnection.Ni(r, "AppointmentNo"), HisConnection.Ni(r, "AssessmentNo")) ?? "";
            var procId = HisConnection.S(r, "ProcedureID");
            var tt = HisConnection.I(r, "TransactionType");
            var inv = HisConnection.I(r, "InvoiceNo");
            var line = HisConnection.I(r, "LineItemNo");
            lines++;
            if (enc.Length == 0) noEncounter++;
            csv.Write(
                c.Hospital, enc, enc, procId.Length == 0 ? "" : c.Procedure(procId), procId, HisConnection.S(r, "ProcedurePackageID"),
                HisConnection.S(r, "ProcedurePackageLineItemNo"), HisConnection.S(r, "ProcedurePackageSequence"),
                $"{tt}", $"{inv}", Maps.Int(line), When(HisConnection.Dt(r, "InvoiceDate")),
                Stored(r, "Price"), Stored(r, "DiscountedCharge"), Stored(r, "PatientShare"), Stored(r, "PatientPaid"), Stored(r, "CompanyShare"), Stored(r, "CompanyPaid"),
                HisConnection.S(r, "ChildInvoiceNo"), HisConnection.S(r, "ChildInvoiceLineItemNo"), Flag(r, "IsCompleted"), Flag(r, "IsRefunded"),
                "VIDA-HIS", $"PKG-{tt}-{inv}-{line}");
        });
        pack.Note($"InvoicePackageItem {lines} lines: {noEncounter} with no encounter. Not written as services or revenue.");
    }

    /// <summary>Every refund in the period as HIS holds it, with its status. These are not revenue lines.</summary>
    private static async Task RefundsAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("37_RefundTransaction.csv",
            "hospitalCode", "encounterNo", "hisEncounterNo", "patientMrn", "procedureCode", "hisProcedureId", "payerCode",
            "refundNo", "refundType", "refundDate", "originalTransactionType", "originalInvoiceNo", "originalInvoiceDate", "invoiceLineNo", "invoiceCancelledAt",
            "refundAmount", "patientShare", "companyShare", "patientPaid", "taxAmount", "headerRefundAmount", "headerAmountPaid",
            "status", "approvalStatus", "approvedAt", "cancelledAt", "reasonId", "sourceSystem", "sourceRef");
        int lines = 0, cancelled = 0, noEncounter = 0;
        var written = new HashSet<(int RefundNo, int Line)>();
        await db.QueryAsync("""
            SELECT r.RefundNo, r.RefundType, r.RefundDate, r.PatientID, r.RefundAmount AS HeaderRefundAmount, r.AmountPaid,
                   r.OriginalTransactionType, r.OriginalTransactionRef, r.OriginalTransactionDate, r.ReasonID,
                   r.Status, r.ApprovalStatus, r.ApprovedDate, r.CancelledOn,
                   d.InvoiceLineItemNo, d.ProcedureID, d.RefundAmount, d.PatientShare, d.CompanyShare, d.PatientPaid, d.TaxAmount,
                   m.AppointmentNo, m.AdmissionNo, m.CompanyID, m.CancelledOn AS InvoiceCancelledOn, er.AssessmentNo
            FROM dbo.HIS_RefundMain r WITH (NOLOCK)
            LEFT JOIN dbo.HIS_RefundDetail d WITH (NOLOCK)
              ON d.SetupID=r.SetupID AND d.ProjectID=r.ProjectID AND d.RefundNo=r.RefundNo
            LEFT JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
              ON m.SetupID=r.SetupID AND m.ProjectID=r.ProjectID
             AND m.TransactionType=r.OriginalTransactionType AND m.InvoiceNo=r.OriginalTransactionRef
            LEFT JOIN dbo.INP_ERAssessment er WITH (NOLOCK)
              ON er.SetupID=m.SetupID AND er.ProjectID=m.ProjectID AND er.InvoiceNo=m.InvoiceNo
             AND ISNULL(m.AppointmentNo,0)=0 AND ISNULL(m.AdmissionNo,0)=0
            WHERE r.SetupID=@SetupId AND r.ProjectID=@ProjectId
              AND r.RefundDate >= @From AND r.RefundDate < @To
            """, s.BindPeriod, r =>
        {
            var enc = EncounterOf(c, HisConnection.Ni(r, "AdmissionNo"), HisConnection.Ni(r, "AppointmentNo"), HisConnection.Ni(r, "AssessmentNo")) ?? "";
            var refundNo = HisConnection.I(r, "RefundNo");
            var line = HisConnection.I(r, "InvoiceLineItemNo");
            if (!written.Add((refundNo, line))) return;
            var procId = HisConnection.S(r, "ProcedureID");
            static string Money(double v) => Maps.Money(Maps.Round4(v));
            var cancelledOn = HisConnection.Dt(r, "CancelledOn");
            lines++;
            if (cancelledOn is not null) cancelled++;
            if (enc.Length == 0) noEncounter++;
            csv.Write(
                c.Hospital, enc, enc, c.Mrn(HisConnection.I(r, "PatientID")),
                procId.Length == 0 ? "" : c.Procedure(procId), procId, c.PayerOrSelf(HisConnection.Ni(r, "CompanyID")),
                $"{refundNo}", HisConnection.S(r, "RefundType"), When(HisConnection.Dt(r, "RefundDate")),
                HisConnection.S(r, "OriginalTransactionType"), $"{HisConnection.I(r, "OriginalTransactionRef")}", When(HisConnection.Dt(r, "OriginalTransactionDate")),
                Maps.Int(line), When(HisConnection.Dt(r, "InvoiceCancelledOn")),
                Money(HisConnection.F(r, "RefundAmount")), Money(HisConnection.F(r, "PatientShare")), Money(HisConnection.F(r, "CompanyShare")),
                Money(HisConnection.F(r, "PatientPaid")), Money(HisConnection.F(r, "TaxAmount")),
                Money(HisConnection.F(r, "HeaderRefundAmount")), Money(HisConnection.F(r, "AmountPaid")),
                HisConnection.S(r, "Status"), HisConnection.S(r, "ApprovalStatus"), When(HisConnection.Dt(r, "ApprovedDate")), When(cancelledOn),
                HisConnection.S(r, "ReasonID"), "VIDA-HIS", $"REF-{refundNo}-{line}");
        });
        pack.Note($"RefundTransaction {lines} lines: {cancelled} on cancelled refunds, {noEncounter} with no encounter. Not written as revenue.");
    }

    private static string Stored(Microsoft.Data.SqlClient.SqlDataReader r, string col) => Maps.Money(Maps.Round4(HisConnection.F(r, col)));

    private static string When(DateTime? d) => d is null ? "" : Maps.DateTime(d.Value);

    /// <summary>A HIS yes/no as stored: blank when HIS holds nothing, which is not the same as no.</summary>
    private static string Flag(Microsoft.Data.SqlClient.SqlDataReader r, string col) =>
        HisConnection.S(r, col).Length == 0 ? "" : Maps.Bool(HisConnection.B(r, col));

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
