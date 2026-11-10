using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Engine;
using Verita.Infrastructure.Runs;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ HIS invoicing: invoice counts by HIS invoice type, child invoices included ═══

    public sealed record HisInvoiceTypeCount(int? InvoiceType, string Name, int Invoices, int Encounters, int Patients, decimal Refunded, decimal Revenue);
    public sealed record HisInvoicingDto(long? MinInvoiceNo, long? MaxInvoiceNo, HisInvoiceTypeCount[] Types, HisInvoiceTypeCount? Total);
    private sealed record HisInvoiceTypeRaw(int? InvoiceType, int Invoices, int Encounters, int Patients, decimal? Refunded, decimal? Revenue, long? MinInvoiceNo, long? MaxInvoiceNo);

    public async Task<HisInvoicingDto> HisInvoicingAsync(ScopedRun s, CancellationToken ct)
    {
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // SourceRef is REV-{transactionType}-{invoiceNo}-{line}; dropping the line leaves the invoice.
        // The same lines a run counts: see HisInvoiceRules.
        // ROLLUP adds the total as a row with no type. Its counts are distinct, not the sum of the
        // rows, because a patient can have invoices of both types.
        var rows = await db.Database.SqlQuery<HisInvoiceTypeRaw>($"""
            SELECT t.HisInvoiceType AS InvoiceType,
                   COUNT(DISTINCT LEFT(t.SourceRef, LEN(t.SourceRef) - CHARINDEX('-', REVERSE(t.SourceRef)))) AS Invoices,
                   COUNT(DISTINCT t.EncounterId) AS Encounters,
                   COUNT(DISTINCT e.PatientId) AS Patients,
                   SUM(ISNULL(t.RefundedAmount, 0)) AS Refunded,
                   SUM(t.NetAmount - ISNULL(t.RefundedAmount, 0)) AS Revenue,
                   MIN(TRY_CAST(PARSENAME(REPLACE(t.SourceRef, '-', '.'), 2) AS bigint)) AS MinInvoiceNo,
                   MAX(TRY_CAST(PARSENAME(REPLACE(t.SourceRef, '-', '.'), 2) AS bigint)) AS MaxInvoiceNo
            FROM RevenueTransaction t
            INNER JOIN Encounter e ON e.Id = t.EncounterId
            WHERE e.HospitalId = {s.Hospital.Id} AND t.PostedAt >= {start} AND t.PostedAt < {end}
              AND t.HisLineStatus = 2 AND t.HisInvoiceType IN (1, 75)
            GROUP BY ROLLUP(t.HisInvoiceType)
            """).ToListAsync(ct);
        var types = rows.Where(x => x.InvoiceType is not null).OrderBy(x => x.InvoiceType)
            .Select(x => new HisInvoiceTypeCount(x.InvoiceType, x.InvoiceType switch
            {
                1 => "Outpatient",
                _ => "Inpatient",
            }, x.Invoices, x.Encounters, x.Patients, x.Refunded ?? 0m, x.Revenue ?? 0m)).ToArray();
        var whole = rows.FirstOrDefault(x => x.InvoiceType is null);
        return new HisInvoicingDto(whole?.MinInvoiceNo, whole?.MaxInvoiceNo, types,
            types.Length == 0 || whole is null ? null : new HisInvoiceTypeCount(null, "", whole.Invoices, whole.Encounters, whole.Patients, whole.Refunded ?? 0m, whole.Revenue ?? 0m));
    }

    // ═══ No-billable: invoice lines the period counts that carry no price, by procedure type ═══

    private const string MedicineType = "MEDICINE";
    private static readonly string[] NoBillableTypes = ["CONSULT", "IMAGING", "LAB", "PROCEDURE", MedicineType];

    private IQueryable<RevenueTransaction> NoBillableLines(ScopedRun s)
    {
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // A line with no procedure is left out: it has no service to cost.
        return CountedRevenue(s).Where(r => r.HisUnitPrice == 0m && r.ProcedureId != null && r.PostedAt >= start && r.PostedAt < end);
    }

    /// <summary>Prepared by the run: read, never re-added. A run from before this was stored holds none, and shows empty until it is run again.</summary>
    private async Task<ProfitDto> NoBillableAsync(ScopedRun s, string dimension, Func<string, string, int, decimal, decimal, decimal, decimal, ProfitRow> make, decimal idle, bool showRevenue, CancellationToken ct)
    {
        var stored = await db.RunDimensionCosts.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.Dimension == dimension).ToListAsync(ct);
        ProfitRow Row(string key, string label)
        {
            var x = stored.FirstOrDefault(r => r.Key == key);
            return make(key, label, x?.Encounters ?? 0, x?.Revenue ?? 0m, x?.DirectCost ?? 0m, x?.IndirectCost ?? 0m, x?.OverheadCost ?? 0m) with { Services = x?.Services ?? 0 };
        }
        var others = stored.Select(x => x.Key).Where(k => k != CostingModelPipeline.NoBillableTotal).Except(NoBillableTypes).Order();
        // BED_DAY reads "Bed day"; a short word such as ER stays in capitals.
        static string Label(string type) => string.Join(' ', type.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => w.Length <= 2 ? w : i == 0 ? w[0] + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));
        var rows = NoBillableTypes.Concat(others).Select(type => Row(type, Label(type))).ToArray();
        var total = Row(CostingModelPipeline.NoBillableTotal, "Total");
        return new ProfitDto(dimension, rows, total, idle, showRevenue ? Money.R4(total.Operating - idle) : 0m);
    }

    private IQueryable<EncounterBase> WithNoBillableType(IQueryable<EncounterBase> query, ScopedRun s, string type)
    {
        var lines = NoBillableLines(s);
        if (type.Equals(CostingModelPipeline.NoBillableTotal, StringComparison.OrdinalIgnoreCase)) return query.Where(x => lines.Any(r => r.EncounterId == x.Id));
        return type.Equals(MedicineType, StringComparison.OrdinalIgnoreCase)
            ? query.Where(x => lines.Any(r => r.EncounterId == x.Id && db.Procedures.Any(pr => pr.Id == r.ProcedureId && pr.Category == MedicineType)))
            : query.Where(x => lines.Any(r => r.EncounterId == x.Id && db.Procedures.Any(pr => pr.Id == r.ProcedureId && (pr.Category == null || pr.Category != MedicineType) && pr.ProcType == type)));
    }

    // ═══ Patient encounters: the catalogue of visits and what was invoiced, no costing or run ═══

    public sealed class PatientEncounterQuery
    {
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 50;
        public string Sort { get; init; } = "admitAt";
        public bool Desc { get; init; } = true;
        public string? Search { get; init; }
        public string? EncType { get; init; }
        /// <summary>Encounters that billed this package as an invoice procedure (HIS IsPackage line).</summary>
        public Guid? BilledPackageId { get; init; }
        // A member's id or code, or NotAssigned for encounters with none.
        public string? ServiceLine { get; init; }
        public string? Department { get; init; }
        public string? Resource { get; init; }
        public string? Physician { get; init; }
        public string? Payer { get; init; }
        public string? Insurer { get; init; }
        /// <summary>Encounters holding a zero-price line of this procedure type.</summary>
        public string? NoBillableType { get; init; }
        public string? Drg { get; init; }
        public string? Segment { get; init; }
    }

    public const string NotAssigned = "none";
    public sealed record FilterOption(string Value, string Label);
    public sealed record PatientEncounterFilters(FilterOption[] ServiceLines, FilterOption[] Departments, FilterOption[] Resources, FilterOption[] Physicians,
        FilterOption[] Payers, FilterOption[] Insurers, FilterOption[] Drgs, FilterOption[] EncounterTypes, FilterOption[] Segments);

    public sealed record PatientEncounterRow(Guid Id, string EncounterNo, string Mrn, string Patient, string Segment, string EncType, string Department,
        string Payer, string? Insurer, string? Physician, DateTime AdmitAt, DateTime? DischargeAt, int LengthOfStay, int Services, decimal Gross, decimal Discount, decimal Net, decimal? Cost = null);
    public sealed record PatientEncounterPage(PatientEncounterRow[] Rows, int Total, int Page, int PageSize, bool PhiMasked);
    public sealed record PatientEncounterLine(string? Procedure, Guid? ProcedureId, decimal? Quantity, string? CostCentre, string? Physician, string? Payer,
        DateTime? PerformedAt, decimal? Gross, decimal? Discount, decimal? Net, string SourceRef, decimal? Cost = null, string? ProcedureCode = null, bool IsPackage = false, decimal? Refunded = null,
        /// <summary>When the line billed a package header, the Packages row to open (same HIS code as the procedure).</summary>
        Guid? PackageId = null);
    public sealed record PatientEncounterDetail(PatientEncounterRow Encounter, PatientEncounterLine[] Lines);

    // Properties, not a constructor: EF has to filter and sort on these after the projection.
    private sealed class EncounterBase
    {
        public Guid Id { get; init; }
        public string EncounterNo { get; init; } = "";
        public string Mrn { get; init; } = "";
        public string FullName { get; init; } = "";
        public string Segment { get; init; } = "";
        public string EncType { get; init; } = "";
        public string Department { get; init; } = "";
        public string Payer { get; init; } = "";
        public string? Physician { get; init; }
        public DateTime AdmitAt { get; init; }
        public DateTime? DischargeAt { get; init; }
        public int LengthOfStay { get; init; }
        public Guid DepartmentId { get; init; }
        public Guid? ServiceLineId { get; init; }
        public string? ServiceLine { get; init; }
        public Guid? ResourceId { get; init; }
        public string? Resource { get; init; }
        public Guid? ProviderId { get; init; }
        public Guid PayerId { get; init; }
        public Guid? InsurerId { get; init; }
        public string? InsurerName { get; init; }
        public Guid? DrgId { get; init; }
        public string? DrgCode { get; init; }
        public string? DrgName { get; init; }
    }

    // HIS Invoicing is the source of truth for a HIS hospital: what a period holds is its counted invoice
    // lines, whenever the patient was admitted. Other hospitals keep the encounters admitted in the period.
    private static bool FromHis(ScopedRun s) => !string.IsNullOrWhiteSpace(s.Hospital.HisSetupId);

    private IQueryable<RevenueTransaction> CountedRevenue(ScopedRun s)
    {
        if (!FromHis(s)) return db.RevenueTransactions.AsNoTracking();
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return db.RevenueTransactions.AsNoTracking().Where(HisInvoiceRules.Counts).Where(r => r.PostedAt >= start && r.PostedAt < end);
    }

    /// <summary>INV-2-7-1 and REV-2-7-1 are the same invoice line, so a service counts when its revenue line does.</summary>
    private IQueryable<EncounterService> CountedServices(ScopedRun s)
    {
        if (!FromHis(s)) return db.EncounterServices.AsNoTracking();
        var counted = CountedRevenue(s);
        // A line refunded in full counts as not given, so it is not a service either.
        return db.EncounterServices.AsNoTracking()
            .Where(x => counted.Any(r => r.EncounterId == x.EncounterId && r.SourceRef.Substring(4) == x.SourceRef.Substring(4)
                && (r.RefundedAmount == null || r.NetAmount <= 0m || r.RefundedAmount < r.NetAmount)));
    }

    private IQueryable<EncounterBase> InPeriod(IQueryable<EncounterBase> query, ScopedRun s)
    {
        if (FromHis(s))
        {
            var counted = CountedRevenue(s);
            return query.Where(x => counted.Any(r => r.EncounterId == x.Id));
        }
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return query.Where(x => x.AdmitAt >= start && x.AdmitAt < end);
    }

    /// <summary>
    /// A costing-model run files an encounter under the resource of each line's doctor, so one encounter can
    /// sit under several resources. Not assigned holds lines with no such doctor and revenue with no service.
    /// </summary>
    private IQueryable<EncounterBase> ByLineResource(IQueryable<EncounterBase> query, ScopedRun s, string? resource)
    {
        var services = CountedServices(s);
        if (Parse(resource) is { } id)
            return query.Where(x => services.Any(v => v.EncounterId == x.Id && db.Providers.Any(d => d.Id == v.ProviderId && d.ResourceId == id)));
        if (resource != NotAssigned) return query;
        var revenue = CountedRevenue(s);
        return query.Where(x => !services.Any(v => v.EncounterId == x.Id)
            || services.Any(v => v.EncounterId == x.Id && !db.Providers.Any(d => d.Id == v.ProviderId && d.ResourceId != null))
            || revenue.Any(r => r.EncounterId == x.Id && !services.Any(v => v.EncounterId == x.Id && v.SourceRef.Substring(4) == r.SourceRef.Substring(4))));
    }

    private IQueryable<EncounterBase> EncounterBases(ScopedRun s, Principal p, IReadOnlyDictionary<Guid, string> deptCodes)
    {
        var query = from e in db.Encounters.AsNoTracking()
                    join pt in db.Patients on e.PatientId equals pt.Id
                    join d in db.Departments on e.DepartmentId equals d.Id
                    join py in db.Payers on e.PayerId equals py.Id
                    join pr in db.Providers on e.ProviderId equals pr.Id into prj
                    from pr in prj.DefaultIfEmpty()
                    join rs in db.Resources on pr.ResourceId equals rs.Id into rsj
                    from rs in rsj.DefaultIfEmpty()
                    join sl in db.ServiceLines on e.ServiceLineId equals sl.Id into slj
                    from sl in slj.DefaultIfEmpty()
                    join g in db.Drgs on e.DrgId equals g.Id into gj
                    from g in gj.DefaultIfEmpty()
                    join ins in db.Insurers on py.InsurerId equals ins.Id into insj
                    from ins in insj.DefaultIfEmpty()
                    where e.HospitalId == s.Hospital.Id
                    select new EncounterBase
                    {
                        Id = e.Id, EncounterNo = e.EncounterNo, Mrn = pt.Mrn, FullName = pt.FullName, Segment = pt.Segment, EncType = e.EncType, Department = d.Name, Payer = py.Name,
                        Physician = pr != null ? pr.Name : null, AdmitAt = e.AdmitAt, DischargeAt = e.DischargeAt, LengthOfStay = e.LengthOfStay, DepartmentId = e.DepartmentId,
                        ServiceLineId = e.ServiceLineId, ServiceLine = sl != null ? sl.Name : null, ResourceId = pr != null ? pr.ResourceId : null, Resource = rs != null ? rs.Name : null,
                        ProviderId = e.ProviderId, PayerId = e.PayerId, InsurerId = py.InsurerId, InsurerName = ins != null ? ins.Name : null, DrgId = e.DrgId, DrgCode = g != null ? g.Code : null, DrgName = g != null ? g.Name : null,
                    };
        if (!p.AllDepartments)
        {
            var allowed = deptCodes.Where(d => p.HasDepartment(d.Value)).Select(d => d.Key).ToList();
            query = query.Where(x => allowed.Contains(x.DepartmentId));
        }
        return query;
    }

    private async Task<PatientEncounterRow[]> EncounterRowsAsync(IReadOnlyList<EncounterBase> rows, ScopedRun s, Principal p, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).ToList();
        // Cost is what the run stored; a period with no run has none.
        var costs = await db.EncounterCosts.AsNoTracking().Where(x => x.RunId == s.Run.Id && ids.Contains(x.EncounterId))
            .ToDictionaryAsync(x => x.EncounterId, x => x.TotalCost, ct);
        var services = await CountedServices(s).Where(x => ids.Contains(x.EncounterId))
            .GroupBy(x => x.EncounterId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var billed = p.Can("revenue.view")
            ? await CountedRevenue(s).Where(x => ids.Contains(x.EncounterId)).GroupBy(x => x.EncounterId)
                .Select(g => new { g.Key, Gross = g.Sum(x => x.GrossAmount), Discount = g.Sum(x => x.Discount), Net = g.Sum(x => x.NetAmount - (x.RefundedAmount ?? 0m)) })
                .ToDictionaryAsync(x => x.Key, x => (x.Gross, x.Discount, x.Net), ct)
            : [];
        var phi = p.Can("phi.view");
        return rows.Select(x =>
        {
            var b = billed.GetValueOrDefault(x.Id);
            return new PatientEncounterRow(x.Id, x.EncounterNo, x.Mrn, phi ? x.FullName : Security.MaskName(x.FullName),
                x.Segment, x.EncType, x.Department, x.Payer, x.InsurerName, x.Physician, x.AdmitAt, x.DischargeAt, x.LengthOfStay, services.GetValueOrDefault(x.Id), b.Gross, b.Discount, b.Net,
                costs.TryGetValue(x.Id, out var cost) ? cost : null);
        }).ToArray();
    }

    public async Task<PatientEncounterPage> PatientEncountersAsync(ScopedRun s, Principal p, PatientEncounterQuery q, CancellationToken ct)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.CatalogCode, ct);
        var query = InPeriod(EncounterBases(s, p, deptCodes), s);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(x => x.EncounterNo.Contains(term) || x.Mrn.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(q.EncType)) query = query.Where(x => x.EncType == q.EncType);
        if (!string.IsNullOrWhiteSpace(q.Segment)) query = query.Where(x => x.Segment == q.Segment);
        if (Parse(q.Department) is { } department) query = query.Where(x => x.DepartmentId == department);
        if (Parse(q.Payer) is { } payer) query = query.Where(x => x.PayerId == payer);
        if (q.Insurer == NotAssigned) query = query.Where(x => x.InsurerId == null);
        else if (Parse(q.Insurer) is { } insurer) query = query.Where(x => x.InsurerId == insurer);
        if (q.ServiceLine == NotAssigned) query = query.Where(x => x.ServiceLineId == null);
        else if (Parse(q.ServiceLine) is { } serviceLine) query = query.Where(x => x.ServiceLineId == serviceLine);
        if (s.Run.CostingMethod == CostingModelPipeline.Method) query = ByLineResource(query, s, q.Resource);
        else if (q.Resource == NotAssigned) query = query.Where(x => x.ResourceId == null);
        else if (Parse(q.Resource) is { } resource) query = query.Where(x => x.ResourceId == resource);
        if (q.Physician == NotAssigned) query = query.Where(x => x.ProviderId == null);
        else if (Parse(q.Physician) is { } physician) query = query.Where(x => x.ProviderId == physician);
        if (q.Drg == NotAssigned) query = query.Where(x => x.DrgId == null);
        else if (Parse(q.Drg) is { } drg) query = query.Where(x => x.DrgId == drg);
        if (!string.IsNullOrWhiteSpace(q.NoBillableType)) query = WithNoBillableType(query, s, q.NoBillableType);
        if (q.BilledPackageId is { } billedPackage)
        {
            var billedEncounters = from es in CountedServices(s)
                                   join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                                   join pk in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pk.HospitalId, pk.Code }
                                   where pk.Id == billedPackage
                                   select es.EncounterId;
            query = query.Where(x => billedEncounters.Contains(x.Id));
        }
        // Sorting by an amount the caller cannot see would reveal it, so that falls back to admission.
        var money = q.Sort is "gross" or "discount" or "net";
        var sort = money && !p.Can("revenue.view") ? "admitAt" : q.Sort;
        var countedServices = CountedServices(s);
        var countedRevenue = CountedRevenue(s);
        IQueryable<EncounterBase> By<TKey>(System.Linq.Expressions.Expression<Func<EncounterBase, TKey>> key) =>
            (q.Desc ? query.OrderByDescending(key) : query.OrderBy(key)).ThenByDescending(x => x.AdmitAt).ThenBy(x => x.EncounterNo);
        query = sort switch
        {
            "encounterNo" => By(x => x.EncounterNo),
            "mrn" => By(x => x.Mrn),
            "encType" => By(x => x.EncType),
            "department" => By(x => x.Department),
            "payer" => By(x => x.Payer),
            "insurer" => By(x => x.InsurerName),
            "physician" => By(x => x.Physician),
            "lengthOfStay" => By(x => x.LengthOfStay),
            "services" => By(x => countedServices.Count(t => t.EncounterId == x.Id)),
            "gross" => By(x => countedRevenue.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)t.GrossAmount) ?? 0m),
            "discount" => By(x => countedRevenue.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)t.Discount) ?? 0m),
            "net" => By(x => countedRevenue.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)(t.NetAmount - (t.RefundedAmount ?? 0m))) ?? 0m),
            "cost" => By(x => db.EncounterCosts.Where(t => t.EncounterId == x.Id && t.RunId == s.Run.Id).Sum(t => (decimal?)t.TotalCost) ?? 0m),
            _ => By(x => x.AdmitAt),
        };
        var total = await query.CountAsync(ct);
        var size = Math.Clamp(q.PageSize, 1, 2000);
        var page = Math.Max(q.Page, 1);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var result = await EncounterRowsAsync(rows, s, p, ct);
        var phi = p.Can("phi.view");
        // FR-0.8: unmasked access to identifiable records is audited. Every role sees the MRN here.
        if (result.Length > 0)
            await audit.WriteAsync(p, "VIEW_PHI", "Encounter", null, $"{result.Length} encounters, {s.Hospital.Code} {s.Period.Code}",
                metadata: new { encounters = result.Select(r => r.EncounterNo).Take(200).ToArray() }, ct: ct);
        return new PatientEncounterPage(result, total, page, size, !phi);
    }

    private static Guid? Parse(string? value) => Guid.TryParse(value, out var id) ? id : null;

    /// <summary>What the period's encounters actually hold, so a filter never offers an empty choice.</summary>
    public async Task<PatientEncounterFilters> PatientEncounterFiltersAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.CatalogCode, ct);
        var q = InPeriod(EncounterBases(s, p, deptCodes), s);
        static FilterOption[] Options(IEnumerable<(string? Value, string? Label)> rows, bool notAssigned = false)
        {
            var list = rows.Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => new FilterOption(x.Value!, x.Label ?? x.Value!))
                .DistinctBy(x => x.Value).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            if (notAssigned && rows.Any(x => string.IsNullOrEmpty(x.Value))) list.Insert(0, new FilterOption(NotAssigned, "Not assigned"));
            return list.ToArray();
        }
        // One read instead of one per filter: this list is what a filtered link waits on.
        var held = await q.Select(x => new
        {
            x.ServiceLineId, x.ServiceLine, x.DepartmentId, x.Department, x.ResourceId, x.Resource, x.ProviderId, x.Physician, x.PayerId, x.Payer,
            x.InsurerId, x.InsurerName, x.DrgId, x.DrgCode, x.DrgName, x.EncType, x.Segment,
        }).Distinct().ToListAsync(ct);
        var serviceLines = held.Select(x => new { x.ServiceLineId, x.ServiceLine }).Distinct().ToList();
        var departments = held.Select(x => new { x.DepartmentId, x.Department }).Distinct().ToList();
        // A costing-model run files encounters by each line's doctor, so its own rows are the choices.
        var resources = s.Run.CostingMethod == CostingModelPipeline.Method
            ? (await db.RunDimensionCosts.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.Dimension == "resource").Select(x => new { x.Key, x.Label }).ToListAsync(ct))
                .Select(x => new { ResourceId = Guid.TryParse(x.Key, out var id) ? id : (Guid?)null, Resource = (string?)x.Label }).ToList()
            : held.Select(x => new { x.ResourceId, x.Resource }).Distinct().ToList();
        var physicians = held.Select(x => new { x.ProviderId, x.Physician }).Distinct().ToList();
        var payers = held.Select(x => new { x.PayerId, x.Payer }).Distinct().ToList();
        var insurers = held.Select(x => new { x.InsurerId, x.InsurerName }).Distinct().ToList();
        var drgs = held.Select(x => new { x.DrgId, x.DrgCode, x.DrgName }).Distinct().ToList();
        var types = held.Select(x => x.EncType).Distinct().ToList();
        var segments = held.Select(x => x.Segment).Distinct().ToList();
        var anyDrg = drgs.Any(x => x.DrgId != null);
        return new PatientEncounterFilters(
            Options(serviceLines.Select(x => (x.ServiceLineId?.ToString(), x.ServiceLine)), true),
            Options(departments.Select(x => ((string?)x.DepartmentId.ToString(), (string?)x.Department))),
            Options(resources.Select(x => (x.ResourceId?.ToString(), x.Resource)), true),
            Options(physicians.Select(x => (x.ProviderId?.ToString(), x.Physician)), true),
            Options(payers.Select(x => ((string?)x.PayerId.ToString(), (string?)x.Payer))),
            Options(insurers.Select(x => (x.InsurerId?.ToString(), x.InsurerName)), true),
            anyDrg ? Options(drgs.Select(x => (x.DrgId?.ToString(), x.DrgCode is null ? null : $"{x.DrgCode} {x.DrgName}")), true) : [],
            Options(types.Select(x => ((string?)x, (string?)x))),
            Options(segments.Select(x => ((string?)x, (string?)x))));
    }

    /// <summary>INV-2-7-1 and REV-2-7-1 are the same invoice line.</summary>
    private static string InvoiceLineKey(string sourceRef) =>
        sourceRef.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) || sourceRef.StartsWith("REV-", StringComparison.OrdinalIgnoreCase)
            ? sourceRef[4..] : sourceRef;

    public async Task<PatientEncounterDetail> PatientEncounterAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.CatalogCode, ct);
        var row = await EncounterBases(s, p, deptCodes).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw AppError.NotFound("Encounter");
        var encounter = (await EncounterRowsAsync([row], s, p, ct))[0];

        var services = await (from sv in CountedServices(s)
                              join pr in db.Procedures on sv.ProcedureId equals pr.Id
                              join c in db.CostCenters on sv.CostCenterId equals c.Id
                              join doc in db.Providers on sv.ProviderId equals doc.Id into dj
                              from doc in dj.DefaultIfEmpty()
                              where sv.EncounterId == id
                              orderby sv.PerformedAt
                              select new { pr.Name, sv.ProcedureId, sv.Quantity, CostCentre = c.Name, Doctor = doc != null ? doc.Name : null, sv.PerformedAt, sv.SourceRef }).ToListAsync(ct);
        var invoiced = p.Can("revenue.view")
            ? await (from r in CountedRevenue(s)
                     join py in db.Payers on r.PayerId equals py.Id
                     join pr in db.Procedures on r.ProcedureId equals pr.Id into pj
                     from pr in pj.DefaultIfEmpty()
                     where r.EncounterId == id
                     select new { Procedure = pr != null ? pr.Name : null, r.ProcedureId, Payer = py.Name, r.GrossAmount, r.Discount, NetAmount = r.NetAmount - (r.RefundedAmount ?? 0m), Refunded = r.RefundedAmount ?? 0m, r.PostedAt, r.SourceRef, IsPackage = r.IsPackageSelected == true }).ToListAsync(ct)
            : [];
        var byLine = invoiced.GroupBy(x => InvoiceLineKey(x.SourceRef)).ToDictionary(g => g.Key, g => g.First());
        var procedureIds = services.Select(x => x.ProcedureId).Distinct().ToList();
        var runCosts = (await db.ProcedureCosts.AsNoTracking()
            .Where(c => c.RunId == s.Run.Id && (c.CostType == "ACTUAL" || c.CostType == "ZERO_PRICE") && procedureIds.Contains(c.ProcedureId)).ToListAsync(ct))
            .ToLookup(c => c.ProcedureId);
        // A zero-price line costs its fixed amount; ACTUAL holds both kinds of line, so the rest share what is left.
        var zeroUnits = runCosts.Where(g => g.Any(c => c.CostType == "ZERO_PRICE")).ToDictionary(g => g.Key, g => g.First(c => c.CostType == "ZERO_PRICE").UnitCost);
        var unitCosts = runCosts.Where(g => g.Any(c => c.CostType == "ACTUAL")).ToDictionary(g => g.Key, g =>
        {
            var actual = g.First(c => c.CostType == "ACTUAL");
            var zero = g.FirstOrDefault(c => c.CostType == "ZERO_PRICE");
            return zero is not null && actual.Volume > zero.Volume ? Money.R6((actual.TotalCost - zero.TotalCost) / (actual.Volume - zero.Volume)) : actual.UnitCost;
        });
        var zeroLines = (await CountedRevenue(s).Where(r => r.EncounterId == id && r.HisUnitPrice == 0m).Select(r => r.SourceRef).ToListAsync(ct))
            .Select(InvoiceLineKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = services.Select(x =>
        {
            var key = InvoiceLineKey(x.SourceRef);
            byLine.Remove(key, out var rev);
            return new PatientEncounterLine(x.Name, x.ProcedureId, x.Quantity, x.CostCentre, x.Doctor, rev?.Payer, x.PerformedAt, rev?.GrossAmount, rev?.Discount, rev?.NetAmount, x.SourceRef,
                zeroLines.Contains(key) && zeroUnits.TryGetValue(x.ProcedureId, out var zeroUnit) ? Money.R4(x.Quantity * zeroUnit)
                : unitCosts.TryGetValue(x.ProcedureId, out var unit) ? Money.R4(x.Quantity * unit) : null, IsPackage: rev?.IsPackage ?? false, Refunded: rev?.Refunded);
        }).ToList();
        lines.AddRange(byLine.Values.Select(x => new PatientEncounterLine(x.Procedure, x.ProcedureId, null, null, null, x.Payer, x.PostedAt, x.GrossAmount, x.Discount, x.NetAmount, x.SourceRef, IsPackage: x.IsPackage, Refunded: x.Refunded)));
        // A HIS hospital's refunds are the refunded amount on each invoice line, already off its net; the refund
        // records are dated by refund, not invoice, so adding them too would count the refund twice.
        if (p.Can("revenue.view") && !FromHis(s))
        {
            // Refunds are moving from revenue rows to RefundTransaction as fresh packs are imported; a cancelled one refunded nothing.
            var refunds = await (from r in db.RefundTransactions.AsNoTracking()
                                 join pr in db.Procedures on r.ProcedureId equals pr.Id into pj
                                 from pr in pj.DefaultIfEmpty()
                                 where r.EncounterId == id && r.CancelledAt == null && r.InvoiceCancelledAt == null
                                 select new { Procedure = pr != null ? pr.Name : null, r.ProcedureId, r.RefundAmount, r.RefundDate, r.SourceRef }).ToListAsync(ct);
            var loaded = lines.Select(l => l.SourceRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lines.AddRange(refunds.Where(r => !loaded.Contains(r.SourceRef)).Select(r => new PatientEncounterLine(r.Procedure, r.ProcedureId, null, null, null, null, r.RefundDate,
                -r.RefundAmount, 0m, -r.RefundAmount, r.SourceRef)));
        }
        await audit.WriteAsync(p, "VIEW_PHI", "Encounter", id.ToString(), encounter.EncounterNo, metadata: new { hospital = s.Hospital.Code }, ct: ct);
        var linedProcedures = lines.Where(l => l.ProcedureId != null).Select(l => l.ProcedureId!.Value).Distinct().ToList();
        var codes = await db.Procedures.AsNoTracking().Where(x => linedProcedures.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        // Package headers are billed as procedures of the same code but listed under Packages, not Procedure/Activity.
        var packageByCode = await db.Packages.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id)
            .ToDictionaryAsync(x => x.Code, x => x.Id, ct);
        return new PatientEncounterDetail(encounter, lines.Select(l =>
        {
            var code = l.ProcedureId is { } procedure ? codes.GetValueOrDefault(procedure) : null;
            Guid? packageId = l.IsPackage && code is { } c && packageByCode.TryGetValue(c, out var pk) ? pk : null;
            return l with { ProcedureCode = code, PackageId = packageId };
        }).ToArray());
    }

    // ═══ HIS source: what an encounter holds, under the HIS table and column it came from ═══

    public sealed record HisField(string Label, string HisColumn, string? Value);
    public sealed record HisServiceLine(int? TransactionType, string? InvoiceNo, int? LineItemNo, string? ProcedureId, string? CostCenterId, string? PrimaryDoctorId,
        decimal Quantity, DateTime PerformDate, decimal? Price, decimal? DiscountAmount, decimal? PatientShare, decimal? CompanyShare, string SourceRef, bool IsChildInvoice = false);
    public sealed record HisRefundLine(string RefundNo, int InvoiceLineItemNo, int RefundType, DateTime RefundDate, int OriginalTransactionType, string OriginalTransactionRef,
        DateTime? OriginalTransactionDate, DateTime? InvoiceCancelledOn, string? ProcedureId, decimal RefundAmount, decimal PatientShare, decimal CompanyShare, decimal PatientPaid,
        decimal TaxAmount, decimal HeaderRefundAmount, decimal HeaderAmountPaid, int Status, int? ApprovalStatus, DateTime? ApprovedDate, DateTime? CancelledOn, int? ReasonId, string SourceRef);
    public sealed record HisPackageLine(int TransactionType, string InvoiceNo, int LineItemNo, string? ProcedureId, string? ProcedurePackageId, int? PackageLineItemNo, int? PackageSequence,
        DateTime InvoiceDate, decimal Price, decimal DiscountedCharge, decimal PatientShare, decimal PatientPaid, decimal CompanyShare, decimal CompanyPaid,
        string? ChildInvoiceNo, int? ChildInvoiceLineItemNo, bool? IsCompleted, bool? IsRefunded, string SourceRef);
    public sealed record HisEncounterSource(HisField[] Fields, HisServiceLine[] Services, HisRefundLine[] Refunds, HisPackageLine[] PackageItems);

    /// <summary>Codes are written as {hospital}.{prefix}-{HIS id}; a reader validating against HIS needs the id.</summary>
    private static string? HisId(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var local = code[(code.IndexOf('.') + 1)..];
        if (local.Equals("PAY-SELF", StringComparison.OrdinalIgnoreCase)) return "None";
        foreach (var prefix in new[] { "CLN-", "DOC-", "PAY-", "ORA-" })
            if (local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return local[prefix.Length..];
        return local;
    }

    /// <summary>Splits INV-{type}-{invoice}-{line}.</summary>
    private static (int? Type, string? Invoice, int? Line) InvoiceRef(string sourceRef)
    {
        var parts = sourceRef.Split('-');
        return parts.Length == 4 && int.TryParse(parts[1], out var type) && int.TryParse(parts[3], out var line) ? (type, parts[2], line) : (null, null, null);
    }

    public async Task<HisEncounterSource> PatientEncounterHisSourceAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.CatalogCode, ct);
        if (!await EncounterBases(s, p, deptCodes).AnyAsync(x => x.Id == id, ct)) throw AppError.NotFound("Encounter");
        var e = await (from x in db.Encounters.AsNoTracking()
                       join pa in db.Patients on x.PatientId equals pa.Id
                       join d in db.Departments on x.DepartmentId equals d.Id
                       join py in db.Payers on x.PayerId equals py.Id
                       join doc in db.Providers on x.ProviderId equals doc.Id into dj
                       from doc in dj.DefaultIfEmpty()
                       join dr in db.Drgs on x.DrgId equals dr.Id into gj
                       from dr in gj.DefaultIfEmpty()
                       where x.Id == id
                       select new { x, pa.Mrn, Clinic = d.Code, ClinicName = d.Name, Payer = py.Code, PayerName = py.Name, Doctor = doc != null ? doc.Code : null, DoctorName = doc != null ? doc.Name : null, Drg = dr != null ? dr.Code : null }).FirstAsync(ct);
        static string? When(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm");
        var enc = e.x;
        var merged = enc.EncType == "ER" && !string.IsNullOrEmpty(enc.SourceAssessmentNo) && enc.SourceAssessmentNo != enc.EncounterNo;
        var table = enc.EncType switch { "IP" => "INP_Admission", "ER" => "INP_ERAssessment", _ => "HIS_Appointment" };
        var fields = new List<HisField>();
        fields.AddRange(enc.EncType switch
        {
            "IP" => [new("Admission", "INP_Admission.AdmissionNo", enc.EncounterNo)],
            "ER" when merged => [new("Assessment", "INP_ERAssessment.AssessmentNo", enc.SourceAssessmentNo), new("Linked appointment", "INP_ERAssessment.AppointmentID, or HIS_InvoiceMain.AppointmentNo through InvoiceNo", enc.EncounterNo)],
            "ER" => [new("Assessment", "INP_ERAssessment.AssessmentNo", enc.EncounterNo)],
            _ => (HisField[])[new("Appointment", "HIS_Appointment.AppointmentNo", enc.EncounterNo)],
        });
        fields.Add(new("Patient", $"{table}.PatientID", e.Mrn));
        fields.Add(new("Clinic", enc.EncType == "IP" ? "INP_Admission.AdmittingClinicID, else ClinicID" : $"{table}.ClinicID", $"{HisId(e.Clinic)} · {e.ClinicName}"));
        fields.Add(new("Doctor", enc.EncType == "IP" ? "INP_Admission.AdmittingDoctorID, else DoctorID" : $"{table}.DoctorID", e.Doctor is null ? null : $"{HisId(e.Doctor)} · {e.DoctorName}"));
        fields.Add(new("Company", merged ? "HIS_Appointment.CompanyID, else HIS_InvoiceMain.CompanyID" : enc.EncType == "IP" ? "INP_Admission.CompanyId" : $"{table}.CompanyID", $"{HisId(e.Payer)} · {e.PayerName}"));
        fields.Add(new("Start", enc.EncType switch { "IP" => "INP_Admission.AdmissionDate", "ER" => "INP_ERAssessment.SignInTime, else AssessmentDate + ArrivalTime", _ => "HIS_Appointment.ActualVisitStartTime, else StartTime, on AppointmentDate" }, When(enc.AdmitAt)));
        fields.Add(new("End", enc.EncType switch { "IP" => "INP_Admission.DischargeDate", "ER" => "INP_ERAssessment.SignOutTime, else DischargeTime", _ => "HIS_Appointment.ActualVisitEndTime, else EndTime" }, When(enc.DischargeAt)));
        if (enc.EncType == "IP") fields.Add(new("Days stayed", "DaysStayed, else DischargeDate - AdmissionDate", enc.LengthOfStay.ToString()));
        fields.Add(new("Status", $"{table}.Status", enc.HisStatus?.ToString()));
        if (enc.EncType == "ER") fields.Add(new("ER status", "INP_ERAssessment.ERStatus", enc.HisErStatus?.ToString()));
        if (enc.EncType == "OP" || merged)
        {
            fields.Add(new("Visit closed", "HIS_Appointment.IsVisitClosed", enc.HisVisitClosed?.ToString()));
            fields.Add(new("Arrived", "HIS_Appointment.ArrivedOn", When(enc.HisArrivedAt)));
        }
        if (e.Drg is not null) fields.Add(new("DRG", "HIS DRG code", e.Drg));

        // The same HIS invoice line is stored twice: INV-… for the service, REV-… for its money.
        var amounts = p.Can("revenue.view")
            ? await db.RevenueTransactions.IgnoreQueryFilters().AsNoTracking().Where(r => r.EncounterId == id)
                .Select(r => new { r.SourceRef, r.HisUnitPrice, r.HisDiscountAmount, r.PatientShare, r.CompanyShare }).ToDictionaryAsync(r => r.SourceRef, ct)
            : [];
        var services = (await (from sv in db.EncounterServices.IgnoreQueryFilters().AsNoTracking()
                               join pr in db.Procedures on sv.ProcedureId equals pr.Id
                               join c in db.CostCenters on sv.CostCenterId equals c.Id
                               join doc in db.Providers on sv.ProviderId equals doc.Id into dj
                               from doc in dj.DefaultIfEmpty()
                               where sv.EncounterId == id
                               orderby sv.SourceRef
                               select new { sv.SourceRef, Procedure = pr.Code, Centre = c.Code, Doctor = doc != null ? doc.Code : null, sv.Quantity, sv.PerformedAt, sv.IsChildInvoice }).ToListAsync(ct))
            .Select(x => { var r = InvoiceRef(x.SourceRef); var m = amounts.GetValueOrDefault("REV-" + x.SourceRef[(x.SourceRef.IndexOf('-') + 1)..]); return new HisServiceLine(r.Type, r.Invoice, r.Line, HisId(x.Procedure), HisId(x.Centre), HisId(x.Doctor), x.Quantity, x.PerformedAt, m?.HisUnitPrice, m?.HisDiscountAmount, m?.PatientShare, m?.CompanyShare, x.SourceRef, x.IsChildInvoice); }).ToArray();
        if (!p.Can("revenue.view")) return new HisEncounterSource(fields.ToArray(), services, [], []);

        var refunds = await db.RefundTransactions.AsNoTracking().Where(r => r.EncounterId == id).OrderBy(r => r.SourceRef)
            .Select(r => new HisRefundLine(r.RefundNo, r.InvoiceLineNo, r.RefundType, r.RefundDate, r.OriginalTransactionType, r.OriginalInvoiceNo, r.OriginalInvoiceDate, r.InvoiceCancelledAt,
                r.HisProcedureId, r.RefundAmount, r.PatientShare, r.CompanyShare, r.PatientPaid, r.TaxAmount, r.HeaderRefundAmount, r.HeaderAmountPaid, r.Status, r.ApprovalStatus, r.ApprovedAt,
                r.CancelledAt, r.ReasonId, r.SourceRef)).ToArrayAsync(ct);
        var packages = await db.InvoicePackageItems.AsNoTracking().Where(r => r.EncounterId == id).OrderBy(r => r.SourceRef)
            .Select(r => new HisPackageLine(r.TransactionType, r.InvoiceNo, r.LineItemNo, r.HisProcedureId, r.PackageProcedureId, r.PackageLineItemNo, r.PackageSequence, r.InvoiceDate, r.Price,
                r.DiscountedCharge, r.PatientShare, r.PatientPaid, r.CompanyShare, r.CompanyPaid, r.ChildInvoiceNo, r.ChildInvoiceLineItemNo, r.IsCompleted, r.IsRefunded, r.SourceRef)).ToArrayAsync(ct);
        return new HisEncounterSource(fields.ToArray(), services, refunds, packages);
    }
}
