using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    // ═══ Patient costing (FR-5.7, NG-9: server-side paging, sorting, filtering) ═════

    public sealed class EncounterQuery
    {
        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 50;
        public string Sort { get; init; } = "total";
        public bool Desc { get; init; } = true;
        public string? Search { get; init; }
        public string? EncType { get; init; }
        public Guid? DepartmentId { get; init; }
        public Guid? DrgId { get; init; }
        public Guid? PackageId { get; init; }
        public Guid? PayerId { get; init; }
    }

    public sealed record EncounterRow(Guid Id, string EncounterNo, string Mrn, string Patient, string Segment, string EncType, string Department,
        string? Drg, string? Package, string Payer, string? Physician, DateTime AdmitAt, int LengthOfStay, int Services, decimal DirectCost,
        decimal IndirectCost, decimal OverheadCost, decimal TotalCost, decimal StandardCost, decimal Revenue, decimal ContributionMargin,
        decimal OperatingMargin, decimal MarginPct, decimal CostPerDay);
    public sealed record EncounterPage(EncounterRow[] Rows, int Total, int Page, int PageSize, bool PhiMasked);

    public async Task<EncounterPage> EncountersAsync(ScopedRun s, Principal p, EncounterQuery q, CancellationToken ct, bool auditPhi = true)
    {
        var deptCodes = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var allowedDepts = p.AllDepartments ? null : deptCodes.Where(d => p.HasDepartment(d.Value)).Select(d => d.Key).ToList();

        var query = from ec in db.EncounterCosts.AsNoTracking()
                    join e in db.Encounters on ec.EncounterId equals e.Id
                    join pt in db.Patients on e.PatientId equals pt.Id
                    join d in db.Departments on e.DepartmentId equals d.Id
                    join py in db.Payers on e.PayerId equals py.Id
                    join g in db.Drgs on e.DrgId equals g.Id into gj
                    from g in gj.DefaultIfEmpty()
                    join pk in db.Packages on e.PackageId equals pk.Id into pj
                    from pk in pj.DefaultIfEmpty()
                    join pr in db.Providers on e.ProviderId equals pr.Id into prj
                    from pr in prj.DefaultIfEmpty()
                    where ec.RunId == s.Run.Id
                    select new { ec, e, pt.Mrn, pt.FullName, pt.Segment, Dept = d.Name, Payer = py.Name, Drg = g != null ? g.Code + " " + g.Name : null,
                                 Package = pk != null ? pk.Name : null, Physician = pr != null ? pr.Name : null };
        if (allowedDepts is not null) query = query.Where(x => allowedDepts.Contains(x.e.DepartmentId));
        if (!string.IsNullOrWhiteSpace(q.Search)) query = query.Where(x => x.e.EncounterNo.Contains(q.Search));
        if (!string.IsNullOrWhiteSpace(q.EncType)) query = query.Where(x => x.e.EncType == q.EncType);
        if (q.DepartmentId is { } dep) query = query.Where(x => x.e.DepartmentId == dep);
        if (q.DrgId is { } drg) query = query.Where(x => x.e.DrgId == drg);
        if (q.PackageId is { } pkg) query = query.Where(x => x.e.PackageId == pkg);
        if (q.PayerId is { } pay) query = query.Where(x => x.e.PayerId == pay);

        query = (q.Sort, q.Desc) switch
        {
            ("margin", true) => query.OrderByDescending(x => x.ec.OperatingMargin), ("margin", false) => query.OrderBy(x => x.ec.OperatingMargin),
            ("los", true) => query.OrderByDescending(x => x.e.LengthOfStay), ("los", false) => query.OrderBy(x => x.e.LengthOfStay),
            ("admit", true) => query.OrderByDescending(x => x.e.AdmitAt), ("admit", false) => query.OrderBy(x => x.e.AdmitAt),
            ("revenue", true) => query.OrderByDescending(x => x.ec.Revenue), ("revenue", false) => query.OrderBy(x => x.ec.Revenue),
            ("encounter", true) => query.OrderByDescending(x => x.e.EncounterNo), ("encounter", false) => query.OrderBy(x => x.e.EncounterNo),
            (_, false) => query.OrderBy(x => x.ec.TotalCost),
            _ => query.OrderByDescending(x => x.ec.TotalCost),
        };

        var total = await query.CountAsync(ct);
        var size = Math.Clamp(q.PageSize, 1, 2000);
        var page = Math.Max(q.Page, 1);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var phi = p.Can("phi.view");
        var showRevenue = p.Can("revenue.view");
        var result = rows.Select(x => new EncounterRow(x.e.Id, x.e.EncounterNo, phi ? x.Mrn : Security.MaskMrn(x.Mrn), phi ? x.FullName : Security.MaskName(x.FullName),
            x.Segment, x.e.EncType, x.Dept, x.Drg, x.Package, x.Payer, x.Physician, x.e.AdmitAt, x.e.LengthOfStay, x.ec.ServiceCount, x.ec.DirectCost,
            x.ec.IndirectCost, x.ec.OverheadCost, x.ec.TotalCost, x.ec.StandardCost, showRevenue ? x.ec.Revenue : 0, showRevenue ? x.ec.ContributionMargin : 0,
            showRevenue ? x.ec.OperatingMargin : 0, showRevenue ? x.ec.MarginPct : 0, x.ec.CostPerDay)).ToArray();
        // FR-0.8: unmasked access to identifiable records is audited.
        if (auditPhi && phi && result.Length > 0)
            await audit.WriteAsync(p, "VIEW_PHI", "Encounter", null, $"{result.Length} encounters, {s.Run.Code}",
                metadata: new { encounters = result.Select(r => r.EncounterNo).Take(200).ToArray() }, ct: ct);
        return new EncounterPage(result, total, page, size, !phi);
    }

    public sealed record ServiceDto(Guid Id, string Procedure, Guid ProcedureId, decimal Quantity, string CostCentre, string? Physician, DateTime PerformedAt,
        string SourceSystem, string SourceRef, decimal StandardUnit);
    public sealed record RevenueDto(string? Procedure, string Payer, decimal Gross, decimal Discount, decimal Net, DateTime PostedAt, string SourceSystem, string SourceRef);
    public sealed record EncounterDetail(EncounterRow Encounter, ComponentDto[] Components, ServiceDto[] Services, SourceTxn[] Transactions,
        RevenueDto[] Revenue, decimal WeightedActivityUnits, Provenance Provenance);

    public async Task<EncounterDetail> EncounterAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var enc = await db.Encounters.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw AppError.NotFound("Encounter");
        var hosp = await db.Hospitals.AsNoTracking().FirstAsync(h => h.Id == enc.HospitalId, ct);
        if (!p.HasHospital(hosp.Code)) throw AppError.OutOfScope($"Encounter {enc.EncounterNo}");
        if (enc.HospitalId != s.Hospital.Id) throw AppError.NotCosted($"Encounter {enc.EncounterNo} is not in run {s.Run.Code}.");
        var dept = await db.Departments.AsNoTracking().FirstAsync(d => d.Id == enc.DepartmentId, ct);
        if (!p.HasDepartment(dept.Code)) throw AppError.OutOfScope($"Encounter {enc.EncounterNo}");

        var row = (await EncountersAsync(s, p, new EncounterQuery { Search = enc.EncounterNo, PageSize = 5 }, ct, auditPhi: false)).Rows.FirstOrDefault(r => r.Id == id)
            ?? throw AppError.NotCosted($"Encounter {enc.EncounterNo} was not costed in run {s.Run.Code}.");
        var comps = await db.CostComponents.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.Scope == "ENCOUNTER" && c.ScopeId == id).ToListAsync(ct);
        var std = await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.CostType == "STANDARD").ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        var services = (await (from sv in db.EncounterServices.AsNoTracking()
                               join pr in db.Procedures on sv.ProcedureId equals pr.Id
                               join c in db.CostCenters on sv.CostCenterId equals c.Id
                               join prov in db.Providers on sv.ProviderId equals prov.Id into pj
                               from prov in pj.DefaultIfEmpty()
                               where sv.EncounterId == id
                               orderby sv.PerformedAt
                               select new { sv.Id, pr.Name, sv.ProcedureId, sv.Quantity, Cc = c.Name, Phys = prov != null ? prov.Name : null, sv.PerformedAt, sv.SourceSystem, sv.SourceRef }).ToListAsync(ct))
            .Select(x => new ServiceDto(x.Id, x.Name, x.ProcedureId, x.Quantity, x.Cc, x.Phys, x.PerformedAt, x.SourceSystem, x.SourceRef, std.GetValueOrDefault(x.ProcedureId))).ToArray();
        // FR-11.8: every source transaction with its source system and reference.
        var txns = await (from t in db.ActivityTransactions.AsNoTracking()
                          join a in db.Activities on t.ActivityId equals a.Id
                          join r in db.Resources on t.ResourceId equals r.Id
                          where t.EncounterId == id
                          orderby t.OccurredAt
                          select new SourceTxn(t.Id, enc.EncounterNo, a.Name, r.Name, t.Quantity, t.Uom, t.OccurredAt, t.SourceSystem, t.SourceRef)).ToArrayAsync(ct);
        var revenue = p.Can("revenue.view")
            ? await (from r in db.RevenueTransactions.AsNoTracking()
                     join py in db.Payers on r.PayerId equals py.Id
                     join pr in db.Procedures on r.ProcedureId equals pr.Id into pj
                     from pr in pj.DefaultIfEmpty()
                     where r.EncounterId == id
                     select new RevenueDto(pr != null ? pr.Name : null, py.Name, r.GrossAmount, r.Discount, r.NetAmount, r.PostedAt, r.SourceSystem, r.SourceRef)).ToArrayAsync(ct)
            : [];
        var wau = await db.EncounterCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.EncounterId == id).Select(c => c.WeightedActivityUnits).FirstOrDefaultAsync(ct);
        if (p.Can("phi.view"))
            await audit.WriteAsync(p, "VIEW_PHI", "Encounter", id.ToString(), enc.EncounterNo, metadata: new { run = s.Run.Code }, ct: ct);
        return new EncounterDetail(row, Components(p, comps), services, txns, revenue, wau, Prov(s));
    }
}
