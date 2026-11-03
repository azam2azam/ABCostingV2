using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
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
        public string? Drg { get; init; }
        public string? Segment { get; init; }
    }

    public const string NotAssigned = "none";
    public sealed record FilterOption(string Value, string Label);
    public sealed record PatientEncounterFilters(FilterOption[] ServiceLines, FilterOption[] Departments, FilterOption[] Resources, FilterOption[] Physicians,
        FilterOption[] Payers, FilterOption[] Drgs, FilterOption[] EncounterTypes, FilterOption[] Segments);

    public sealed record PatientEncounterRow(Guid Id, string EncounterNo, string Mrn, string Patient, string Segment, string EncType, string Department,
        string Payer, string? Physician, DateTime AdmitAt, DateTime? DischargeAt, int LengthOfStay, int Services, decimal Gross, decimal Discount, decimal Net);
    public sealed record PatientEncounterPage(PatientEncounterRow[] Rows, int Total, int Page, int PageSize, bool PhiMasked);
    public sealed record PatientEncounterLine(string? Procedure, Guid? ProcedureId, decimal? Quantity, string? CostCentre, string? Physician, string? Payer,
        DateTime? PerformedAt, decimal? Gross, decimal? Discount, decimal? Net, string SourceRef);
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
        public Guid? DrgId { get; init; }
        public string? DrgCode { get; init; }
        public string? DrgName { get; init; }
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
                    where e.HospitalId == s.Hospital.Id
                    select new EncounterBase
                    {
                        Id = e.Id, EncounterNo = e.EncounterNo, Mrn = pt.Mrn, FullName = pt.FullName, Segment = pt.Segment, EncType = e.EncType, Department = d.Name, Payer = py.Name,
                        Physician = pr != null ? pr.Name : null, AdmitAt = e.AdmitAt, DischargeAt = e.DischargeAt, LengthOfStay = e.LengthOfStay, DepartmentId = e.DepartmentId,
                        ServiceLineId = e.ServiceLineId, ServiceLine = sl != null ? sl.Name : null, ResourceId = pr != null ? pr.ResourceId : null, Resource = rs != null ? rs.Name : null,
                        ProviderId = e.ProviderId, PayerId = e.PayerId, DrgId = e.DrgId, DrgCode = g != null ? g.Code : null, DrgName = g != null ? g.Name : null,
                    };
        if (!p.AllDepartments)
        {
            var allowed = deptCodes.Where(d => p.HasDepartment(d.Value)).Select(d => d.Key).ToList();
            query = query.Where(x => allowed.Contains(x.DepartmentId));
        }
        return query;
    }

    private async Task<PatientEncounterRow[]> EncounterRowsAsync(IReadOnlyList<EncounterBase> rows, Principal p, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).ToList();
        var services = await db.EncounterServices.AsNoTracking().Where(x => ids.Contains(x.EncounterId))
            .GroupBy(x => x.EncounterId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var billed = p.Can("revenue.view")
            ? await db.RevenueTransactions.AsNoTracking().Where(x => ids.Contains(x.EncounterId)).GroupBy(x => x.EncounterId)
                .Select(g => new { g.Key, Gross = g.Sum(x => x.GrossAmount), Discount = g.Sum(x => x.Discount), Net = g.Sum(x => x.NetAmount) })
                .ToDictionaryAsync(x => x.Key, x => (x.Gross, x.Discount, x.Net), ct)
            : [];
        var phi = p.Can("phi.view");
        return rows.Select(x =>
        {
            var b = billed.GetValueOrDefault(x.Id);
            return new PatientEncounterRow(x.Id, x.EncounterNo, x.Mrn, phi ? x.FullName : Security.MaskName(x.FullName),
                x.Segment, x.EncType, x.Department, x.Payer, x.Physician, x.AdmitAt, x.DischargeAt, x.LengthOfStay, services.GetValueOrDefault(x.Id), b.Gross, b.Discount, b.Net);
        }).ToArray();
    }

    public async Task<PatientEncounterPage> PatientEncountersAsync(ScopedRun s, Principal p, PatientEncounterQuery q, CancellationToken ct)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.CatalogCode, ct);
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var query = EncounterBases(s, p, deptCodes).Where(x => x.AdmitAt >= start && x.AdmitAt < end);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(x => x.EncounterNo.Contains(term) || x.Mrn.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(q.EncType)) query = query.Where(x => x.EncType == q.EncType);
        if (!string.IsNullOrWhiteSpace(q.Segment)) query = query.Where(x => x.Segment == q.Segment);
        if (Parse(q.Department) is { } department) query = query.Where(x => x.DepartmentId == department);
        if (Parse(q.Payer) is { } payer) query = query.Where(x => x.PayerId == payer);
        if (q.ServiceLine == NotAssigned) query = query.Where(x => x.ServiceLineId == null);
        else if (Parse(q.ServiceLine) is { } serviceLine) query = query.Where(x => x.ServiceLineId == serviceLine);
        if (q.Resource == NotAssigned) query = query.Where(x => x.ResourceId == null);
        else if (Parse(q.Resource) is { } resource) query = query.Where(x => x.ResourceId == resource);
        if (q.Physician == NotAssigned) query = query.Where(x => x.ProviderId == null);
        else if (Parse(q.Physician) is { } physician) query = query.Where(x => x.ProviderId == physician);
        if (q.Drg == NotAssigned) query = query.Where(x => x.DrgId == null);
        else if (Parse(q.Drg) is { } drg) query = query.Where(x => x.DrgId == drg);
        if (q.BilledPackageId is { } billedPackage)
        {
            var billedEncounters = from es in db.EncounterServices.AsNoTracking()
                                   join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                                   join pk in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pk.HospitalId, pk.Code }
                                   where pk.Id == billedPackage
                                   select es.EncounterId;
            query = query.Where(x => billedEncounters.Contains(x.Id));
        }
        // Sorting by an amount the caller cannot see would reveal it, so that falls back to admission.
        var money = q.Sort is "gross" or "discount" or "net";
        var sort = money && !p.Can("revenue.view") ? "admitAt" : q.Sort;
        IQueryable<EncounterBase> By<TKey>(System.Linq.Expressions.Expression<Func<EncounterBase, TKey>> key) =>
            (q.Desc ? query.OrderByDescending(key) : query.OrderBy(key)).ThenByDescending(x => x.AdmitAt).ThenBy(x => x.EncounterNo);
        query = sort switch
        {
            "encounterNo" => By(x => x.EncounterNo),
            "mrn" => By(x => x.Mrn),
            "encType" => By(x => x.EncType),
            "department" => By(x => x.Department),
            "payer" => By(x => x.Payer),
            "physician" => By(x => x.Physician),
            "lengthOfStay" => By(x => x.LengthOfStay),
            "services" => By(x => db.EncounterServices.Count(t => t.EncounterId == x.Id)),
            "gross" => By(x => db.RevenueTransactions.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)t.GrossAmount) ?? 0m),
            "discount" => By(x => db.RevenueTransactions.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)t.Discount) ?? 0m),
            "net" => By(x => db.RevenueTransactions.Where(t => t.EncounterId == x.Id).Sum(t => (decimal?)t.NetAmount) ?? 0m),
            _ => By(x => x.AdmitAt),
        };
        var total = await query.CountAsync(ct);
        var size = Math.Clamp(q.PageSize, 1, 2000);
        var page = Math.Max(q.Page, 1);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var result = await EncounterRowsAsync(rows, p, ct);
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
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var q = EncounterBases(s, p, deptCodes).Where(x => x.AdmitAt >= start && x.AdmitAt < end);
        static FilterOption[] Options(IEnumerable<(string? Value, string? Label)> rows, bool notAssigned = false)
        {
            var list = rows.Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => new FilterOption(x.Value!, x.Label ?? x.Value!))
                .DistinctBy(x => x.Value).OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
            if (notAssigned && rows.Any(x => string.IsNullOrEmpty(x.Value))) list.Insert(0, new FilterOption(NotAssigned, "Not assigned"));
            return list.ToArray();
        }
        var serviceLines = await q.Select(x => new { x.ServiceLineId, x.ServiceLine }).Distinct().ToListAsync(ct);
        var departments = await q.Select(x => new { x.DepartmentId, x.Department }).Distinct().ToListAsync(ct);
        var resources = await q.Select(x => new { x.ResourceId, x.Resource }).Distinct().ToListAsync(ct);
        var physicians = await q.Select(x => new { x.ProviderId, x.Physician }).Distinct().ToListAsync(ct);
        var payers = await q.Select(x => new { x.PayerId, x.Payer }).Distinct().ToListAsync(ct);
        var drgs = await q.Select(x => new { x.DrgId, x.DrgCode, x.DrgName }).Distinct().ToListAsync(ct);
        var types = await q.Select(x => x.EncType).Distinct().ToListAsync(ct);
        var segments = await q.Select(x => x.Segment).Distinct().ToListAsync(ct);
        var anyDrg = drgs.Any(x => x.DrgId != null);
        return new PatientEncounterFilters(
            Options(serviceLines.Select(x => (x.ServiceLineId?.ToString(), x.ServiceLine)), true),
            Options(departments.Select(x => ((string?)x.DepartmentId.ToString(), (string?)x.Department))),
            Options(resources.Select(x => (x.ResourceId?.ToString(), x.Resource)), true),
            Options(physicians.Select(x => (x.ProviderId?.ToString(), x.Physician)), true),
            Options(payers.Select(x => ((string?)x.PayerId.ToString(), (string?)x.Payer))),
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
        var encounter = (await EncounterRowsAsync([row], p, ct))[0];

        var services = await (from sv in db.EncounterServices.AsNoTracking()
                              join pr in db.Procedures on sv.ProcedureId equals pr.Id
                              join c in db.CostCenters on sv.CostCenterId equals c.Id
                              join doc in db.Providers on sv.ProviderId equals doc.Id into dj
                              from doc in dj.DefaultIfEmpty()
                              where sv.EncounterId == id
                              orderby sv.PerformedAt
                              select new { pr.Name, sv.ProcedureId, sv.Quantity, CostCentre = c.Name, Doctor = doc != null ? doc.Name : null, sv.PerformedAt, sv.SourceRef }).ToListAsync(ct);
        var invoiced = p.Can("revenue.view")
            ? await (from r in db.RevenueTransactions.AsNoTracking()
                     join py in db.Payers on r.PayerId equals py.Id
                     join pr in db.Procedures on r.ProcedureId equals pr.Id into pj
                     from pr in pj.DefaultIfEmpty()
                     where r.EncounterId == id
                     select new { Procedure = pr != null ? pr.Name : null, r.ProcedureId, Payer = py.Name, r.GrossAmount, r.Discount, r.NetAmount, r.PostedAt, r.SourceRef }).ToListAsync(ct)
            : [];
        var byLine = invoiced.GroupBy(x => InvoiceLineKey(x.SourceRef)).ToDictionary(g => g.Key, g => g.First());
        var lines = services.Select(x =>
        {
            var key = InvoiceLineKey(x.SourceRef);
            byLine.Remove(key, out var rev);
            return new PatientEncounterLine(x.Name, x.ProcedureId, x.Quantity, x.CostCentre, x.Doctor, rev?.Payer, x.PerformedAt, rev?.GrossAmount, rev?.Discount, rev?.NetAmount, x.SourceRef);
        }).ToList();
        lines.AddRange(byLine.Values.Select(x => new PatientEncounterLine(x.Procedure, x.ProcedureId, null, null, null, x.Payer, x.PostedAt, x.GrossAmount, x.Discount, x.NetAmount, x.SourceRef)));
        if (p.Can("revenue.view"))
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
        return new PatientEncounterDetail(encounter, lines.ToArray());
    }
}
