using System.Globalization;
using Microsoft.Data.SqlClient;

namespace HisExtract;

/// <summary>
/// Claims, settlements and NPHIES adjudication / payment on the pack's invoices, every HIS column as stored,
/// so claimed, approved, paid and rejected can be evaluated in VERITA. Scoped to the same invoices billing reads
/// (#ip / #op / #er), with no date filter: a re-extract later picks up settlements posted since, and rows are keyed
/// by sourceRef so they update in place. Claim and settlement lines carry revenueRef = the RevenueTransaction sourceRef.
/// Pharmacy (HIS_ClaimMedicine / HIS_SettlementMedicineDetail) is keyed on dispense invoices that are not in the
/// pack, so it is not read here.
/// </summary>
public static class ClaimsExtract
{
    public static async Task RunAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        pack.Step("Claims: pack invoices");
        try
        {
            var n = await PackInvoicesAsync(db, s);
            pack.Note($"Claims: {n} pack invoices to read claims, settlements and NPHIES responses for (any date, as of now).");
        }
        catch (SqlException ex)
        {
            pack.Warn("Claims and settlements", ex);
            return;
        }
        await Safe(pack, "Claims: HIS columns", () => ColumnsAsync(db, pack));
        await Safe(pack, "Claim lines", () => ClaimLinesAsync(db, s, c, pack));
        await Safe(pack, "Settlement lines", () => SettlementLinesAsync(db, s, c, pack));
        await Safe(pack, "NPHIES claim lines", () => NphiesLinesAsync(db, s, c, pack));
        await Safe(pack, "NPHIES payments", () => NphiesPaymentsAsync(db, s, c, pack));
    }

    private static async Task Safe(CsvZipWriter pack, string label, Func<Task> body)
    {
        try { await body(); }
        catch (SqlException ex) { pack.Warn(label, ex); }
    }

    /// <summary>The pack's invoices with the encounter keys billing uses: #packinv (TransactionType, InvoiceNo).</summary>
    private static async Task<int> PackInvoicesAsync(HisConnection db, Settings s)
    {
        // Created without parameters so the temp table outlives the command (a parameterised batch scopes it to itself).
        await db.ExecAsync("""
            IF OBJECT_ID('tempdb..#packinv') IS NOT NULL DROP TABLE #packinv;
            CREATE TABLE #packinv (TransactionType int NOT NULL, InvoiceNo int NOT NULL, AdmissionNo int NULL, AppointmentNo int NULL,
                AssessmentNo int NULL, PatientID int NULL, CompanyID int NULL, PRIMARY KEY (TransactionType, InvoiceNo));
            """, _ => { });
        await db.ExecAsync("""
            INSERT INTO #packinv (TransactionType, InvoiceNo, AdmissionNo, AppointmentNo, AssessmentNo, PatientID, CompanyID)
            SELECT x.TransactionType, x.InvoiceNo, MAX(x.AdmissionNo), MAX(x.AppointmentNo), MAX(x.AssessmentNo), MAX(x.PatientID), MAX(x.CompanyID)
            FROM (
                SELECT m.TransactionType, m.InvoiceNo, m.AdmissionNo, m.AppointmentNo, CAST(NULL AS int) AS AssessmentNo, m.PatientID, m.CompanyID
                FROM #ip i
                INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
                  ON m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AdmissionNo=i.No
                WHERE m.CancelledOn IS NULL
                UNION ALL
                SELECT m.TransactionType, m.InvoiceNo, m.AdmissionNo, m.AppointmentNo, CAST(NULL AS int), m.PatientID, m.CompanyID
                FROM #op o
                INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
                  ON m.SetupID=@SetupId AND m.ProjectID=@ProjectId AND m.AppointmentNo=o.No
                WHERE m.CancelledOn IS NULL AND ISNULL(m.AdmissionNo, 0) = 0
                UNION ALL
                SELECT m.TransactionType, m.InvoiceNo, m.AdmissionNo, m.AppointmentNo, a.AssessmentNo, m.PatientID, m.CompanyID
                FROM #er e
                INNER JOIN dbo.INP_ERAssessment a WITH (NOLOCK)
                  ON a.SetupID=@SetupId AND a.ProjectID=@ProjectId AND a.AssessmentNo=e.No AND a.InvoiceNo > 0
                INNER JOIN dbo.HIS_InvoiceMain m WITH (NOLOCK)
                  ON m.SetupID=a.SetupID AND m.ProjectID=a.ProjectID AND m.InvoiceNo=a.InvoiceNo AND ISNULL(m.AdmissionNo,0)=0
                WHERE m.CancelledOn IS NULL
            ) x
            GROUP BY x.TransactionType, x.InvoiceNo;
            """, s.BindTenant);
        var n = 0;
        await db.ScalarAsync("SELECT COUNT(*) FROM #packinv", _ => { }, v => n = Convert.ToInt32(v, CultureInfo.InvariantCulture));
        return n;
    }

    // ── Columns: (csv column = VERITA property, HIS expression). Keys the writer needs are prefixed "_" and not written.

    private static readonly (string Col, string Sql)[] ClaimCols =
    [
        ("claimNo", "cp.ClaimNo"), ("claimDate", "cm.ClaimDate"),
        ("invoiceTransactionType", "cp.InvoiceTransactionType"), ("invoiceNo", "cp.InvoiceNo"), ("mainInvoiceNo", "ci.MainInvoiceNo"),
        ("lineItemNo", "cp.LineItemNo"), ("hisPatientType", "cp.PatientType"), ("hisProcedureId", "cp.ProcedureID"),
        ("claimAmount", "cp.ClaimAmount"), ("invoiceAmount", "cp.InvoiceAmount"), ("settlementAmount", "cp.SettlementAmount"),
        ("rejectedAmount", "cp.RejectedAmount"), ("taxAmount", "cp.TaxAmount"),
        ("primaryDoctorId", "cp.PrimaryDoctorID"), ("primaryDoctorShare", "cp.PrimaryDoctorShare"), ("primaryDoctorShareType", "cp.PrimaryDoctorShareType"),
        ("referralClinicId", "cp.ReferralClinicID"), ("referralDoctorId", "cp.ReferralDoctorID"),
        ("referralDoctorShare", "cp.ReferralDoctorShare"), ("referralDoctorShareType", "cp.ReferralDoctorShareType"),
        ("remarks", "cp.Remarks"), ("eClaimStatus", "cp.EClaimStatus"), ("eClaimRejectionReason", "cp.EClaimRejectionReason"),
        ("eClaimRemarks", "cp.EClaimRemarks"), ("lineCreatedOn", "cp.CreatedOn"), ("lineEditedOn", "cp.EditedOn"),
        // HIS_ClaimInvoice: the invoice on the claim, with its NPHIES submission and response.
        ("invInvoiceAmount", "ci.InvoiceAmount"), ("invClaimAmount", "ci.ClaimAmount"), ("invSettlementAmount", "ci.SettlementAmount"),
        ("invTaxAmount", "ci.TaxAmount"), ("invRemarks", "ci.Remarks"), ("invIsPackageInvoice", "ci.IsPackageInvoice"), ("invIsActive", "ci.IsActive"),
        ("invEClaimStatus", "ci.EClaimStatus"), ("invEClaimRefNum", "ci.EClaimRefNum"), ("invEClaimRemarks", "ci.EClaimRemarks"),
        ("nphiesClaimStatus", "ci.NPHIESClaimStatus"), ("nphiesClaimRefNum", "ci.NPHIESClaimRefNum"), ("nphiesClaimRemarks", "ci.NPHIESClaimRemarks"),
        ("nphiesTaxAmount", "ci.NPHIESTaxAmount"), ("nphiesBatchIdentifier", "ci.NPHIES_BatchIdentifier"), ("nphiesBatchNumber", "ci.NPHIES_BatchNumber"),
        ("nphiesClaimRemarks2", "ci.NPHIESClaim_Remarks"), ("nphiesSubmittedOn", "ci.NPHIES_SubmittedOn"), ("nphiesResponseReceivedOn", "ci.NPHIES_ResponseRecievedOn"),
        ("nphiesAmountSubmitted", "ci.NPhies_Amount_Submitted"), ("nphiesAmountApproved", "ci.NPhies_Amount_Approved"),
        ("claimBundleId", "ci.ClaimBundleID"), ("claimBatchBundleId", "ci.ClaimBatchBundleID"), ("isPendedReceived", "ci.IsPendedReceived"),
        ("reconciliationBundleId", "ci.ReconciliationBundleID"), ("resubmissionStatus", "ci.ResubmissionStatus"), ("attachmentStatus", "ci.AttachmentStatus"),
        ("invCreatedOn", "ci.CreatedOn"),
        // HIS_ClaimMain: the claim batch sent to the company.
        ("claimCompanyId", "cm.CompanyID"), ("claimInvoiceFromDate", "cm.InvoiceFromDate"), ("claimInvoiceToDate", "cm.InvoiceToDate"),
        ("claimInvoiceAmount", "cm.InvoiceAmount"), ("claimTotalAmount", "cm.ClaimAmount"), ("claimSettledAmount", "cm.SettledAmount"),
        ("claimNoOfSettlement", "cm.NoOfSettlement"), ("claimLastSettlementNo", "cm.LastSettlementNo"), ("claimLastSettlementDate", "cm.LastSettlementDate"),
        ("claimIsPosted2Gl", "cm.IsPosted2GL"), ("claimIsTransactionClosed", "cm.IsTransactionClosed"), ("claimStatus", "cm.Status"),
        ("claimIsEClaimed", "cm.IsEClaimed"), ("claimEClaimStatus", "cm.EClaimStatus"), ("claimInvoiceTypeId", "cm.ClaimInvoiceTypeID"),
        ("claimTaxAmount", "cm.TaxAmount"), ("claimUpdateDate", "cm.UpdateClaimDate"), ("claimNphiesStatus", "cm.NPHIESClaimStatus"),
        ("claimNphiesUpdateDate", "cm.NPHIESClaimUpdateDate"), ("claimNphiesReference", "cm.NPHIESClaimReference"),
        ("claimEClaimSubmittedOn", "cm.EClaimSubmittedOn"), ("claimNphiesSubmittedOn", "cm.NphiesEClaimSubmittedOn"),
        ("claimNphiesReSubmittedOn", "cm.NphiesEClaimReSubmittedOn"), ("claimIsPostedToZatca", "cm.IsPostedToZatca"),
        ("claimOracleBatchNo", "cm.ORACLE_BATCHNO"), ("claimCreatedOn", "cm.CreatedOn"),
        ("_adm", "p.AdmissionNo"), ("_app", "p.AppointmentNo"), ("_ass", "p.AssessmentNo"), ("_patient", "cp.PatientID"), ("_company", "cm.CompanyID"),
    ];

    private static readonly (string Col, string Sql)[] SettlementCols =
    [
        ("settlementTransactionType", "sp.TransactionType"), ("settlementNo", "sp.SettlementNo"), ("settlementDate", "sm.SettlementDate"),
        ("claimNo", "sm.ClaimNo"), ("invoiceTransactionType", "sp.InvoiceTransactionType"), ("invoiceNo", "sp.InvoiceNo"),
        ("mainInvoiceNo", "si.MainInvoiceNo"), ("lineItemNo", "sp.LineItemNo"), ("hisProcedureId", "sp.ProcedureID"),
        ("claimedAmount", "sp.ClaimedAmount"), ("paidAmount", "sp.PaidAmount"), ("rejectedAmount", "sp.RejectedAmount"),
        ("rejectionReasonId", "sp.RejectionReasonID"),
        ("primaryDoctorId", "sp.PrimaryDoctorID"), ("primaryDoctorShare", "sp.PrimaryDoctorShare"), ("primaryDoctorShareType", "sp.PrimaryDoctorShareType"),
        ("referralClinicId", "sp.ReferralClinicID"), ("referralDoctorId", "sp.ReferralDoctorID"),
        ("referralDoctorShare", "sp.ReferralDoctorShare"), ("referralDoctorShareType", "sp.ReferralDoctorShareType"),
        ("partnerId", "sp.PartnerID"), ("partnerShare", "sp.PartnerShare"), ("partnerShareType", "sp.PartnerShareType"),
        ("remarks", "sp.Remarks"), ("lineCreatedOn", "sp.CreatedOn"), ("lineEditedOn", "sp.EditedOn"),
        // HIS_SettlementInvoice: the invoice in the settlement.
        ("invInvoiceAmount", "si.InvoiceAmount"), ("invClaimedAmount", "si.ClaimedAmount"), ("invSettledAmount", "si.SettledAmount"),
        ("invRejectedAmount", "si.RejectedAmount"), ("invTaxAmount", "si.TaxAmount"), ("invRemarks", "si.Remarks"),
        ("invIsActive", "si.IsActive"), ("invIsPackageInvoice", "si.IsPackageInvoice"),
        // HIS_SettlementMain: the receipt from the company.
        ("settlementCompanyId", "sm.CompanyID"), ("officialReceiptNo", "sm.OfficialRecieptNo"), ("officialReceiptDate", "sm.OfficialReceiptDate"),
        ("officialReceiptReference", "sm.OfficialReceiptReference"), ("receiptAmount", "sm.ReceiptAmount"), ("isIncomeShared", "sm.IsIncomeShared"),
        ("incShareProcessedOn", "sm.IncShareProcessedOn"), ("isPosted2Gl", "sm.IsPosted2GL"), ("isTransactionClosed", "sm.IsTransactionClosed"),
        ("settlementStatus", "sm.Status"), ("settlementTaxAmount", "sm.TaxAmount"), ("erpInterfaceId", "CAST(sm.ERPInterfaceID AS varchar(30))"),
        ("settlementCreatedOn", "sm.CreatedOn"),
        ("_adm", "p.AdmissionNo"), ("_app", "p.AppointmentNo"), ("_ass", "p.AssessmentNo"), ("_patient", "p.PatientID"), ("_company", "sm.CompanyID"),
    ];

    private static readonly (string Col, string Sql)[] NphiesCols =
    [
        ("claimType", "l.ClaimType"), ("invoiceTransactionType", "p.TransactionType"), ("invoiceNo", "l.InvoiceNo"),
        ("claimIdentifier", "l.ClaimIdentifier"), ("sequenceNo", "l.SequenceNo"), ("hisProcedureId", "l.ProcedureID"),
        ("amountSubmitted", "l.AmountSubmitted"), ("amountApproved", "l.AmountApproved"), ("isApproved", "l.IsApproved"),
        ("remarks", "l.Remarks"), ("patientShare", "l.PatientShare"), ("companyTax", "l.CompanyTax"), ("nphiesStatus", "l.NPHIES_Status"),
        ("submittedQuantity", "l.SubmittedQuantity"), ("approvedQuantity", "l.ApprovedQuantity"), ("reasonCode", "l.ReasonCode"),
        ("taxApproved", "l.TaxApproved"), ("claimStatus", "l.ClaimStatus"), ("claimBundleId", "l.ClaimBundleID"),
        ("lineCreatedOn", "l.CreatedOn"), ("lineEditedOn", "l.EditedOn"),
        ("rejectionRemarks", """
            STUFF((SELECT ' | ' + rr.Remarks FROM dbo.HIS_NPHIESClaimRejectionRemarks rr WITH (NOLOCK)
                   WHERE rr.SetupID=l.SetupID AND rr.ProjectID=l.ProjectID AND rr.ClaimIdentifier=l.ClaimIdentifier
                     AND rr.InvoiceNo=l.InvoiceNo AND rr.ProcedureSequence=l.SequenceNo
                   ORDER BY rr.LineItemNo FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 3, '')
            """),
        ("_adm", "p.AdmissionNo"), ("_app", "p.AppointmentNo"), ("_ass", "p.AssessmentNo"), ("_patient", "p.PatientID"), ("_company", "p.CompanyID"),
    ];

    private static readonly (string Col, string Sql)[] PaymentCols =
    [
        ("paymentReconIdentifier", "d.PaymentReconIdentifier"), ("lineItemNo", "CAST(d.LineItemNo AS int)"),
        ("componentPayment", "d.Component_payment"), ("componentEarlyFee", "d.Component_early_fee"), ("componentNphiesFee", "d.Component_nphies_fee"),
        ("identifier", "d.Identifier"), ("predecessor", "d.Predecessor"), ("type", "d.Type"),
        ("claimRequestIdentifier", "d.ClaimRequestidentifier"), ("claimRequestSystem", "d.ClaimRequestsystem"),
        ("claimResponseIdentifier", "d.ClaimResponseidentifier"), ("claimResponseSystem", "d.ClaimResponsesystem"),
        ("payeeOrg", "d.PayeeORG"), ("submitterOrg", "d.SubmitterORG"), ("amount", "d.Amount"),
        ("amountValue", "TRY_CAST(d.Amount AS decimal(19,4))"), ("componentPaymentValue", "TRY_CAST(d.Component_payment AS decimal(19,4))"),
        ("date", "d.Date"), ("lineCreatedOn", "d.CreatedOn"),
        // HIS_NPHIESPaymentReconciliation: the payment notice.
        ("identifierSystem", "h.identifiersystem"), ("paymentIssuer", "h.PaymentIssuer"), ("status", "h.Status"),
        ("startDate", "h.StartDate"), ("endDate", "h.EndDate"), ("paymentCreatedOn", "h.PaymentCreatedOn"), ("outcome", "h.outcome"),
        ("disposition", "h.disposition"), ("paymentDate", "h.paymentDate"), ("paymentAmount", "h.paymentAmount"),
        ("paymentAmountValue", "TRY_CAST(h.paymentAmount AS decimal(19,4))"), ("paymentIdentifier", "h.paymentIdentifier"),
        ("paymentIdentifierSystem", "h.paymentIdentifierSystem"), ("isPaymentNoticeSent", "h.IsPaymentNoticeSent"),
        ("paymentNoticeSentOn", "h.PaymentNoticeSentOn"), ("createdOn", "h.CreatedOn"),
    ];

    /// <summary>Query alias → HIS table, for checking each selected column exists in this HIS version.</summary>
    private static readonly Dictionary<string, string> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cp"] = "HIS_ClaimProcedure", ["ci"] = "HIS_ClaimInvoice", ["cm"] = "HIS_ClaimMain",
        ["sp"] = "HIS_SettlementProcedure", ["si"] = "HIS_SettlementInvoice", ["sm"] = "HIS_SettlementMain",
        ["l"] = "HIS_NPHIESClaimProcedure_Log", ["rr"] = "HIS_NPHIESClaimRejectionRemarks",
        ["d"] = "HIS_NPHIESPaymentReconciliation_Detail", ["h"] = "HIS_NPHIESPaymentReconciliation",
    };
    /// <summary>"table.column" (lower case) present in this HIS; null until read, then every expression is used as written.</summary>
    private static HashSet<string>? _columns;

    /// <summary>
    /// HIS versions differ (e.g. UAT has no HIS_ClaimInvoice.NPHIESClaimRemarks): a column this HIS lacks is written blank
    /// instead of failing the whole file, and the pack log lists them.
    /// </summary>
    private static async Task ColumnsAsync(HisConnection db, CsvZipWriter pack)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = string.Join(",", Tables.Values.Distinct().Select(x => $"OBJECT_ID('dbo.{x}')"));
        await db.QueryAsync($"SELECT OBJECT_NAME(object_id) AS T, name AS C FROM sys.columns WHERE object_id IN ({list})", _ => { },
            r => have.Add($"{HisConnection.S(r, "T")}.{HisConnection.S(r, "C")}"));
        _columns = have;
        var missing = new[] { ClaimCols, SettlementCols, NphiesCols, PaymentCols }.SelectMany(cols => cols)
            .SelectMany(x => Refs(x.Sql)).Where(x => !have.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (missing.Count > 0) pack.Note($"Claims: {missing.Count} HIS column(s) not in this HIS, written blank: {string.Join(", ", missing)}.");
    }

    /// <summary>The "table.column" an expression reads, e.g. ci.NPHIESTaxAmount → HIS_ClaimInvoice.NPHIESTaxAmount.</summary>
    private static IEnumerable<string> Refs(string sql) =>
        System.Text.RegularExpressions.Regex.Matches(sql, @"\b([A-Za-z]+)\.([A-Za-z_][A-Za-z0-9_]*)\b")
            .Where(m => Tables.ContainsKey(m.Groups[1].Value))
            .Select(m => $"{Tables[m.Groups[1].Value]}.{m.Groups[2].Value}");

    private static string Select((string Col, string Sql)[] cols) => string.Join(",\n            ", cols.Select(x =>
        $"{(_columns is { } have && !Refs(x.Sql).All(have.Contains) ? "NULL" : x.Sql)} AS [{x.Col}]"));
    private static string[] Headers((string Col, string Sql)[] cols) => cols.Select(x => x.Col).Where(x => !x.StartsWith('_')).ToArray();

    /// <summary>Encounter columns first, then every HIS column as stored, then sourceSystem / sourceRef.</summary>
    private static readonly string[] LinkedHead = ["hospitalCode", "encounterNo", "hisEncounterNo", "patientMrn", "payerCode"];

    private static async Task ClaimLinesAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("42_ClaimLine.csv", [.. LinkedHead, "revenueRef", .. Headers(ClaimCols), "sourceSystem", "sourceRef"]);
        var n = 0;
        await db.QueryAsync($"""
            SELECT {Select(ClaimCols)}
            FROM dbo.HIS_ClaimProcedure cp WITH (NOLOCK)
            INNER JOIN #packinv p ON p.TransactionType=cp.InvoiceTransactionType AND p.InvoiceNo=cp.InvoiceNo
            LEFT JOIN dbo.HIS_ClaimInvoice ci WITH (NOLOCK)
              ON ci.SetupID=cp.SetupID AND ci.ProjectID=cp.ProjectID AND ci.ClaimNo=cp.ClaimNo
             AND ci.InvoiceTransactionType=cp.InvoiceTransactionType AND ci.InvoiceNo=cp.InvoiceNo
            LEFT JOIN dbo.HIS_ClaimMain cm WITH (NOLOCK)
              ON cm.SetupID=cp.SetupID AND cm.ProjectID=cp.ProjectID AND cm.ClaimNo=cp.ClaimNo
            WHERE cp.SetupID=@SetupId AND cp.ProjectID=@ProjectId
            OPTION (HASH JOIN)
            """, s.BindTenant, r =>
        {
            var tt = HisConnection.I(r, "invoiceTransactionType");
            var inv = HisConnection.I(r, "invoiceNo");
            var line = HisConnection.I(r, "lineItemNo");
            var sourceRef = $"CLM-{HisConnection.I(r, "claimNo")}-{tt}-{inv}-{line}";
            if (Write(csv, r, c, ClaimCols, sourceRef, $"REV-{tt}-{inv}-{line}", "42_ClaimLine")) n++;
        });
        pack.Note($"ClaimLine: {n} HIS_ClaimProcedure lines (with claim invoice + claim header) on the pack invoices.");
    }

    private static async Task SettlementLinesAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("43_SettlementLine.csv", [.. LinkedHead, "revenueRef", .. Headers(SettlementCols), "sourceSystem", "sourceRef"]);
        var n = 0;
        await db.QueryAsync($"""
            SELECT {Select(SettlementCols)}
            FROM dbo.HIS_SettlementProcedure sp WITH (NOLOCK)
            INNER JOIN #packinv p ON p.TransactionType=sp.InvoiceTransactionType AND p.InvoiceNo=sp.InvoiceNo
            LEFT JOIN dbo.HIS_SettlementInvoice si WITH (NOLOCK)
              ON si.SetupID=sp.SetupID AND si.ProjectID=sp.ProjectID AND si.SettlementNo=sp.SettlementNo AND si.TransactionType=sp.TransactionType
             AND si.InvoiceTransactionType=sp.InvoiceTransactionType AND si.InvoiceNo=sp.InvoiceNo
            LEFT JOIN dbo.HIS_SettlementMain sm WITH (NOLOCK)
              ON sm.SetupID=sp.SetupID AND sm.ProjectID=sp.ProjectID AND sm.TransactionType=sp.TransactionType AND sm.SettlementNo=sp.SettlementNo
            WHERE sp.SetupID=@SetupId AND sp.ProjectID=@ProjectId
            OPTION (HASH JOIN)
            """, s.BindTenant, r =>
        {
            var tt = HisConnection.I(r, "invoiceTransactionType");
            var inv = HisConnection.I(r, "invoiceNo");
            var line = HisConnection.I(r, "lineItemNo");
            var sourceRef = $"STL-{HisConnection.I(r, "settlementTransactionType")}-{HisConnection.I(r, "settlementNo")}-{tt}-{inv}-{line}";
            if (Write(csv, r, c, SettlementCols, sourceRef, $"REV-{tt}-{inv}-{line}", "43_SettlementLine")) n++;
        });
        pack.Note($"SettlementLine: {n} HIS_SettlementProcedure lines (paid / rejected, with settlement invoice + receipt header) on the pack invoices.");
    }

    /// <summary>NPHIES holds InvoiceNo without the transaction type: an invoice number is matched to the pack invoice with the lowest type.</summary>
    private static async Task NphiesLinesAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using var csv = pack.Create("44_NphiesClaimLine.csv", [.. LinkedHead, .. Headers(NphiesCols), "sourceSystem", "sourceRef"]);
        var n = 0;
        await db.QueryAsync($"""
            SELECT {Select(NphiesCols)}
            FROM dbo.HIS_NPHIESClaimProcedure_Log l WITH (NOLOCK)
            INNER JOIN #packinv p ON p.InvoiceNo=l.InvoiceNo
             AND p.TransactionType=(SELECT MIN(q.TransactionType) FROM #packinv q WHERE q.InvoiceNo=l.InvoiceNo)
            WHERE l.SetupID=@SetupId AND l.ProjectID=@ProjectId
            """, s.BindTenant, r =>
        {
            var sourceRef = $"NPH-{HisConnection.I(r, "claimType")}-{HisConnection.I(r, "invoiceNo")}-{HisConnection.S(r, "claimIdentifier")}-{HisConnection.I(r, "sequenceNo")}";
            if (Write(csv, r, c, NphiesCols, sourceRef, null, "44_NphiesClaimLine")) n++;
        });
        pack.Note($"NphiesClaimLine: {n} HIS_NPHIESClaimProcedure_Log lines (submitted / approved / reason, with rejection remarks) on the pack invoices.");
    }

    /// <summary>Payment reconciliation lines that name a claim of the pack (by NPHIES claim identifier, reference or bundle).</summary>
    private static async Task NphiesPaymentsAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        await db.ExecAsync("""
            IF OBJECT_ID('tempdb..#packclaimids') IS NOT NULL DROP TABLE #packclaimids;
            CREATE TABLE #packclaimids (Id varchar(200) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY WITH (IGNORE_DUP_KEY = ON));
            """, _ => { });
        await db.ExecAsync("""
            INSERT INTO #packclaimids (Id)
            SELECT x.Id FROM (
                SELECT l.ClaimIdentifier AS Id FROM dbo.HIS_NPHIESClaimProcedure_Log l WITH (NOLOCK)
                INNER JOIN (SELECT DISTINCT InvoiceNo FROM #packinv) p ON p.InvoiceNo=l.InvoiceNo
                WHERE l.SetupID=@SetupId AND l.ProjectID=@ProjectId
                UNION SELECT l.ClaimBundleID FROM dbo.HIS_NPHIESClaimProcedure_Log l WITH (NOLOCK)
                INNER JOIN (SELECT DISTINCT InvoiceNo FROM #packinv) p ON p.InvoiceNo=l.InvoiceNo
                WHERE l.SetupID=@SetupId AND l.ProjectID=@ProjectId
                UNION SELECT ci.NPHIESClaimRefNum FROM dbo.HIS_ClaimInvoice ci WITH (NOLOCK)
                INNER JOIN #packinv p ON p.TransactionType=ci.InvoiceTransactionType AND p.InvoiceNo=ci.InvoiceNo
                WHERE ci.SetupID=@SetupId AND ci.ProjectID=@ProjectId
                UNION SELECT ci.ClaimBundleID FROM dbo.HIS_ClaimInvoice ci WITH (NOLOCK)
                INNER JOIN #packinv p ON p.TransactionType=ci.InvoiceTransactionType AND p.InvoiceNo=ci.InvoiceNo
                WHERE ci.SetupID=@SetupId AND ci.ProjectID=@ProjectId
            ) x WHERE x.Id IS NOT NULL AND LTRIM(RTRIM(x.Id)) <> '';
            """, s.BindTenant);
        using var csv = pack.Create("45_NphiesPayment.csv", ["hospitalCode", .. Headers(PaymentCols), "sourceSystem", "sourceRef"]);
        var n = 0;
        await db.QueryAsync($"""
            SELECT {Select(PaymentCols)}
            FROM dbo.HIS_NPHIESPaymentReconciliation_Detail d WITH (NOLOCK)
            LEFT JOIN dbo.HIS_NPHIESPaymentReconciliation h WITH (NOLOCK)
              ON h.SetupID=d.SetupID AND h.ProjectID=d.ProjectID AND h.Identifier=d.PaymentReconIdentifier
            WHERE d.SetupID=@SetupId AND d.ProjectID=@ProjectId
              AND (d.ClaimRequestidentifier IN (SELECT Id FROM #packclaimids) OR d.ClaimResponseidentifier IN (SELECT Id FROM #packclaimids))
            """, s.BindTenant, r =>
        {
            var cells = new List<string?> { c.Hospital };
            for (var i = 0; i < PaymentCols.Length; i++) cells.Add(Cell(r, i));
            cells.Add("VIDA-HIS");
            cells.Add($"NPP-{HisConnection.S(r, "paymentReconIdentifier")}-{HisConnection.I(r, "lineItemNo")}");
            csv.Write([.. cells]);
            n++;
        });
        pack.Note($"NphiesPayment: {n} HIS_NPHIESPaymentReconciliation lines naming a pack claim (identifier match — check against HIS if 0).");
    }

    /// <summary>One encounter-linked row: encounter columns, then every HIS column in order. False when its encounter was skipped.</summary>
    private static bool Write(CsvZipWriter.CsvFile csv, SqlDataReader r, Codes c, (string Col, string Sql)[] cols, string sourceRef, string? revenueRef, string file)
    {
        var enc = FactsExtract.EncounterOf(c, HisConnection.Ni(r, "_adm"), HisConnection.Ni(r, "_app"), HisConnection.Ni(r, "_ass")) ?? "";
        if (c.Skip(enc, file, sourceRef)) return false;
        var company = HisConnection.Ni(r, "_company");
        var cells = new List<string?> { c.Hospital, enc, enc, c.Mrn(HisConnection.I(r, "_patient")), company is { } id ? c.Payer(id) : "" };
        if (revenueRef is not null) cells.Add(revenueRef);
        for (var i = 0; i < cols.Length; i++)
            if (!cols[i].Col.StartsWith('_')) cells.Add(Cell(r, i));
        cells.Add("VIDA-HIS");
        cells.Add(sourceRef);
        csv.Write([.. cells]);
        return true;
    }

    /// <summary>A HIS value as stored: numbers invariant (4 dp), dates with seconds, bits true/false, text trimmed.</summary>
    private static string Cell(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return "";
        return r.GetValue(i) switch
        {
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double d => Maps.Money(Maps.Round4(d)),
            float f => Maps.Money(Maps.Round4(f)),
            decimal m => Maps.Money(decimal.Round(m, 4, MidpointRounding.AwayFromZero)),
            bool b => Maps.Bool(b),
            string t => t.Trim(),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            var o => Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim() ?? "",
        };
    }
}
