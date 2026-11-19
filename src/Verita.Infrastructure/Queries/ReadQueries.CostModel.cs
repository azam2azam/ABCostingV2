using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Engine;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    public sealed record AllocationDto(Guid Id, int Stage, string? Rule, string Source, string Target, string TargetType, Guid? TargetCostCentreId,
        Guid? TargetActivityId, string? Driver, decimal DriverQty, decimal DriverVolume, decimal DriverShare, decimal Amount, int Iteration, string CalcExpression);

    private async Task<List<AllocationDto>> AllocationsAsync(Guid runId, Func<AllocationResult, bool> filter, CancellationToken ct)
    {
        var rows = (await db.AllocationResults.AsNoTracking().Where(a => a.RunId == runId).ToListAsync(ct)).Where(filter).ToList();
        var rules = await db.AllocationRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.OrderBy(a => a.Stage).ThenByDescending(a => a.Amount).Select(a => new AllocationDto(a.Id, a.Stage,
            a.RuleId is { } r ? rules.GetValueOrDefault(r) : null, a.SourceCostCenterId is { } s ? ccs.GetValueOrDefault(s, "?") : "?", a.TargetLabel, a.TargetType,
            a.TargetCostCenterId, a.TargetActivityId, a.DriverId is { } d ? drivers.GetValueOrDefault(d) : null, a.DriverQty, a.DriverVolume, a.DriverShare,
            a.Amount, a.Iteration, a.CalcExpression)).ToList();
    }

    private bool DeptAllowed(Principal p, Dictionary<Guid, string> deptCodes, Guid deptId) =>
        p.AllDepartments || (deptCodes.TryGetValue(deptId, out var code) && p.HasDepartment(code));

    // ═══ Departments ═════════════════════════════════════════════════════════════════

    /// <summary>Catalogue departments for the hospital. A run, when present, adds cases/revenue/cost.</summary>
    /// <param name="Packages">Package cases (billings) on the department's encounters in the run.</param>
    /// <param name="AmountBilled">Every counted line on the department's encounters, once: package lines (own and child) plus the
    /// other procedure lines, net of refunds plus VAT. From the run's stored package and procedure rows.</param>
    /// <param name="Claimed">What HIS claimed on those same lines, on invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed = 1).</param>
    public sealed record DepartmentRow(Guid Id, string Code, string Name, string DeptType, string? Specialty, string? ServiceLine,
        int CostCentres, int Cases, decimal Revenue, decimal Cost, int Doctors, int Packages = 0, decimal AmountBilled = 0, decimal Claimed = 0);

    public async Task<DepartmentRow[]> DepartmentsAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => d.HospitalId == s.Hospital.Id).OrderBy(d => d.Code).ToListAsync(ct);
        var deptCodes = depts.ToDictionary(d => d.Id, d => d.CatalogCode);
        var lines = await db.ServiceLines.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var ccCounts = await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == s.Hospital.Id)
            .GroupBy(c => c.DepartmentId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        // Doctors: home department or an active HIS clinic there; HIS placeholder doctors are not people.
        var deptIds = depts.Select(d => d.Id).ToList();
        var homes = await db.Providers.AsNoTracking().Where(x => !x.IsDummy && deptIds.Contains(x.DepartmentId))
            .Select(x => new { x.Id, x.DepartmentId }).ToListAsync(ct);
        var clinics = await (from c in db.ProviderClinics.AsNoTracking()
                             join x in db.Providers.AsNoTracking() on c.ProviderId equals x.Id
                             where c.IsActive && !x.IsDummy && deptIds.Contains(c.DepartmentId)
                             select new { x.Id, c.DepartmentId }).ToListAsync(ct);
        var doctors = homes.Concat(clinics).Distinct().GroupBy(x => x.DepartmentId).ToDictionary(g => g.Key, g => g.Count());
        var showRevenue = p.Can("revenue.view");
        var figures = await db.RunDimensionCosts.AsNoTracking()
            .Where(x => x.RunId == s.Run.Id && x.Dimension == "department" && x.Key != "")
            .ToDictionaryAsync(x => x.Key, ct);
        // Package and procedure lines from the run's stored rows, by the encounter's department. Lines only (not the shared
        // consumables / medicine shares), so the admission's general lines, which are procedure rows, count once.
        var pkg = await (from r in db.RunPackageDoctors.AsNoTracking()
                         join e in db.Encounters.AsNoTracking() on r.EncounterId equals e.Id
                         where r.RunId == s.Run.Id
                         group r by e.DepartmentId into g
                         select new { g.Key, Packages = g.Sum(x => x.Performed), Billed = g.Sum(x => x.FeeBilled), Claimed = g.Sum(x => x.ClaimsReceivedLines ?? 0m) })
            .ToDictionaryAsync(x => x.Key, ct);
        var proc = await (from r in db.RunProcedureDoctors.AsNoTracking()
                          join e in db.Encounters.AsNoTracking() on r.EncounterId equals e.Id
                          where r.RunId == s.Run.Id
                          group r by e.DepartmentId into g
                          select new { g.Key, Billed = g.Sum(x => x.FeeBilled), Claimed = g.Sum(x => x.ClaimsReceived ?? 0m) })
            .ToDictionaryAsync(x => x.Key, ct);
        return depts.Where(d => DeptAllowed(p, deptCodes, d.Id)).Select(d =>
        {
            figures.TryGetValue(d.Id.ToString(), out var f);
            return new DepartmentRow(d.Id, d.Code, d.Name, d.DeptType, d.Specialty,
                d.ServiceLineId is { } sl ? lines.GetValueOrDefault(sl) : null,
                ccCounts.GetValueOrDefault(d.Id),
                f?.Encounters ?? 0,
                showRevenue ? f?.Revenue ?? 0m : 0m,
                f?.TotalCost ?? 0m,
                doctors.GetValueOrDefault(d.Id),
                pkg.TryGetValue(d.Id, out var k) ? k.Packages : 0,
                showRevenue ? (k?.Billed ?? 0m) + (proc.TryGetValue(d.Id, out var pr) ? pr.Billed : 0m) : 0m,
                showRevenue ? (k?.Claimed ?? 0m) + (proc.TryGetValue(d.Id, out var pc) ? pc.Claimed : 0m) : 0m);
        }).ToArray();
    }

    public sealed record DepartmentDoctorRow(Guid Id, string Code, string Name, string Specialty, string Grade, bool IsHome, bool IsDefault, bool IsDummy, int Cases);
    /// <param name="Packages">Packages billed on the department's encounters in the run, with the Packages list's figures for those encounters only.</param>
    /// <param name="ProcedureWork">Procedures billed on the department's encounters not as or under a package (the doctor page's non-package items).</param>
    /// <param name="AllTotals">The tiles with non-package items too, worked out as on the doctor page.</param>
    public sealed record DepartmentDetail(DepartmentRow Department, CostCentreRow[] CostCentres, DepartmentDoctorRow[] Doctors, PackageRow[]? Packages = null,
        DepartmentPackageTotals? PackageTotals = null, PackageWorkRow[]? ProcedureWork = null, DepartmentPackageTotals? AllTotals = null,
        Dictionary<Guid, PackageRow[]>? PackageDoctors = null);

    /// <summary>
    /// The department's package tiles, as the package page works them out, over its encounters: package cases; resources
    /// consumed (contracted price × cases, plus consumables and medicine covered and billed separately); amount billed
    /// (package lines plus consumables and medicine billed); claims on invoices HIS put on a claim (null until the flag is in).
    /// </summary>
    public sealed record DepartmentPackageTotals(int Performed, decimal ResourcesConsumed, decimal AmountBilled, decimal? ClaimsReceived);

    /// <summary>One department: its figures for the run, the cost centres it owns and the doctors who work in it.</summary>
    public async Task<DepartmentDetail> DepartmentAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await DepartmentsAsync(s, p, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("Department");
        var ccIds = (await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == s.Hospital.Id && c.DepartmentId == id)
            .Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var centres = ccIds.Count == 0 ? [] : (await CostCentresAsync(s, p, ct)).Where(c => ccIds.Contains(c.Id)).ToArray();

        // A doctor belongs here when this is their home department or one of their HIS clinics.
        var clinics = await db.ProviderClinics.AsNoTracking().Where(c => c.DepartmentId == id && c.IsActive)
            .Select(c => new { c.ProviderId, c.IsDefault }).ToListAsync(ct);
        var clinicOf = clinics.GroupBy(c => c.ProviderId).ToDictionary(g => g.Key, g => g.Any(c => c.IsDefault));
        var linked = clinicOf.Keys.ToList();
        // Cases: this run's encounters in the department, by the encounter's doctor (the same encounters as the department's Cases).
        var cases = await (from e in db.Encounters.AsNoTracking()
                           join ec in db.EncounterCosts.AsNoTracking() on e.Id equals ec.EncounterId
                           where ec.RunId == s.Run.Id && e.DepartmentId == id && e.ProviderId != null
                           group e by e.ProviderId into g
                           select new { ProviderId = g.Key!.Value, N = g.Select(x => x.Id).Distinct().Count() })
            .ToDictionaryAsync(x => x.ProviderId, x => x.N, ct);
        linked.AddRange(cases.Keys.Where(k => !clinicOf.ContainsKey(k)));
        var doctors = (await db.Providers.AsNoTracking().Where(x => x.DepartmentId == id || linked.Contains(x.Id)).ToListAsync(ct))
            .Select(x => new DepartmentDoctorRow(x.Id, x.Code, x.Name, x.Specialty, x.Grade, x.DepartmentId == id,
                clinicOf.GetValueOrDefault(x.Id), x.IsDummy, cases.GetValueOrDefault(x.Id)))
            .OrderByDescending(x => x.Cases).ThenBy(x => x.IsDummy).ThenBy(x => x.Name).ToArray();
        var (packages, totals, pkgRows, packageDoctors) = await DepartmentPackagesAsync(s, p, id, ct);
        var procedureWork = await DepartmentProcedureWorkAsync(s, p, id, ct);
        return new DepartmentDetail(row, centres, doctors, packages, totals, procedureWork, AllTotalsOf(pkgRows, procedureWork, p.Can("revenue.view")),
            packageDoctors);
    }

    /// <summary>
    /// The tiles with non-package items, as the doctor page adds them up: cases are package cases plus procedures performed;
    /// amount billed and claims count lines only (package lines plus procedure lines), and resources consumed leaves out the
    /// package's separately billed consumables and medicine, since those are the admission's general lines, which are
    /// procedure lines already.
    /// </summary>
    private static DepartmentPackageTotals? AllTotalsOf(IReadOnlyCollection<(RunPackageDoctor Row, decimal Price)> pkgs, PackageWorkRow[] procs, bool money)
    {
        if (pkgs.Count == 0 && procs.Length == 0) return null;
        var claims = pkgs.Any(r => r.Row.ClaimsReceivedLines != null) || procs.Any(r => r.ClaimsReceived != null);
        return new DepartmentPackageTotals(
            pkgs.Sum(r => r.Row.Performed) + procs.Sum(r => r.Performed),
            money ? Money.R4(pkgs.Sum(r => r.Price * r.Row.Performed + r.Row.ConsumablesCovered + r.Row.MedicineCovered)
                             + procs.Sum(r => r.FeeBilled + r.ConsumablesBilled + r.MedicineBilled)) : 0,
            money ? Money.R4(pkgs.Sum(r => r.Row.FeeBilled) + procs.Sum(r => r.FeeBilled)) : 0,
            money && claims ? Money.R4(pkgs.Sum(r => r.Row.ClaimsReceivedLines ?? 0m) + procs.Sum(r => r.ClaimsReceived ?? 0m)) : null);
    }

    /// <summary>The non-package procedures billed on the department's encounters, from the run's stored rows, one row per procedure.</summary>
    private async Task<PackageWorkRow[]> DepartmentProcedureWorkAsync(ScopedRun s, Principal p, Guid departmentId, CancellationToken ct)
    {
        if (s.Run.Id == Guid.Empty) return [];
        var rows = await (from r in db.RunProcedureDoctors.AsNoTracking()
                          join e in db.Encounters.AsNoTracking() on r.EncounterId equals e.Id
                          where r.RunId == s.Run.Id && e.DepartmentId == departmentId
                          select r).ToListAsync(ct);
        return await ProcedureWorkOfAsync(rows, p.Can("revenue.view"), ct, withDoctors: true);
    }

    /// <summary>
    /// The packages billed on the department's encounters (the encounter's department), from the run's stored package rows:
    /// the Packages list's catalogue columns, with cases, billing, stay and claims for these encounters only.
    /// </summary>
    /// <returns>Also each package's rows split by the encounter doctor (Id, Code and Name are the doctor's), for the list's expand.</returns>
    private async Task<(PackageRow[] Packages, DepartmentPackageTotals? Totals, (RunPackageDoctor Row, decimal Price)[] Rows, Dictionary<Guid, PackageRow[]> Doctors)>
        DepartmentPackagesAsync(ScopedRun s, Principal p, Guid departmentId, CancellationToken ct)
    {
        if (s.Run.Id == Guid.Empty) return ([], null, [], []);
        var rows = await (from r in db.RunPackageDoctors.AsNoTracking()
                          join e in db.Encounters.AsNoTracking() on r.EncounterId equals e.Id
                          where r.RunId == s.Run.Id && e.DepartmentId == departmentId
                          select r).ToListAsync(ct);
        if (rows.Count == 0) return ([], null, [], []);
        var money = p.Can("revenue.view");
        var catalogue = (await PackagesAsync(s, p, ct)).ToDictionary(x => x.Id);
        rows = rows.Where(r => catalogue.ContainsKey(r.PackageId)).ToList();
        var totals = new DepartmentPackageTotals(
            rows.Sum(r => r.Performed),
            money ? Money.R4(rows.GroupBy(r => r.PackageId).Sum(g => catalogue[g.Key].Price * g.Sum(r => r.Performed))
                             + rows.Sum(r => r.ConsumablesCovered + r.MedicineCovered + r.ConsumablesBilled + r.MedicineBilled)) : 0,
            money ? Money.R4(rows.Sum(r => r.FeeBilled + r.ConsumablesBilled + r.MedicineBilled)) : 0,
            money && rows.Any(r => r.ClaimsReceived != null) ? Money.R4(rows.Sum(r => r.ClaimsReceived ?? 0m)) : null);
        PackageRow Figures(PackageRow basis, IReadOnlyCollection<RunPackageDoctor> g)
        {
            var received = g.Any(r => r.ClaimsReceived != null) ? g.Sum(r => r.ClaimsReceived ?? 0m) : (decimal?)null;
            return basis with
            {
                Cases = g.Sum(r => r.Performed), Encounters = g.Select(r => r.EncounterId).Distinct().Count(),
                FeeBilled = money ? g.Sum(r => r.FeeBilled) : 0, MaterialsBilled = g.Sum(r => r.ConsumablesBilled), MedicineBilled = g.Sum(r => r.MedicineBilled),
                ConsumablesCovered = g.Sum(r => r.ConsumablesCovered), MedicineCovered = g.Sum(r => r.MedicineCovered),
                WardDays = Money.R4(g.Sum(r => r.WardDays)), StaffMinutes = Money.R4(g.Sum(r => r.StaffMinutes)),
                Claimed = money ? g.Sum(r => r.Claimed) : 0, ClaimSettled = money ? g.Sum(r => r.Settled) : 0, ClaimRejected = money ? g.Sum(r => r.Rejected) : 0,
                ClaimsReceived = money ? received : null,
            };
        }
        var list = rows.GroupBy(r => r.PackageId).Select(g => Figures(catalogue[g.Key], g.ToList()))
            .OrderByDescending(x => x.FeeBilled).ThenByDescending(x => x.Cases).ToArray();
        var providerIds = rows.Where(r => r.ProviderId != null).Select(r => r.ProviderId!.Value).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking().Where(x => providerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.Name }).ToDictionaryAsync(x => x.Id, ct);
        var doctors = rows.GroupBy(r => r.PackageId).ToDictionary(g => g.Key, g => g.GroupBy(r => r.ProviderId).Select(d =>
        {
            var doc = d.Key is { } pid ? providers.GetValueOrDefault(pid) : null;
            return Figures(catalogue[g.Key], d.ToList()) with { Id = d.Key ?? Guid.Empty, Code = doc?.Code ?? "", Name = doc?.Name ?? "No doctor" };
        }).OrderByDescending(x => x.FeeBilled).ThenByDescending(x => x.Cases).ToArray());
        return (list, totals, rows.Select(r => (r, catalogue[r.PackageId].Price)).ToArray(), doctors);
    }

    // ═══ Doctors ═════════════════════════════════════════════════════════════════════

    /// <summary>Catalogue doctors for the hospital. A run, when present, adds cases/revenue/cost.</summary>
    public sealed record DoctorRow(Guid Id, string Code, string Name, string Specialty, string Grade, Guid DepartmentId, string? Department,
        int Departments, bool IsDummy, int Cases, decimal Revenue, decimal Cost,
        /// <summary>Staff minutes (doctors, nurses, technicians) on the period's encounters the doctor attended.</summary>
        decimal StaffMinutes = 0,
        /// <summary>Billed consumables, implants and medicine on those encounters (charge, not cost).</summary>
        decimal Materials = 0)
    {
        /// <summary>The doctor's package rows in the run (as the doctor page's Packages performed total): package lines billed.</summary>
        public decimal FeeBilled { get; init; }
        /// <summary>Consumables on those rows, billed separately plus covered by the package.</summary>
        public decimal Consumables { get; init; }
        /// <summary>Medicine on those rows billed separately (not what the package covered).</summary>
        public decimal Medicine { get; init; }
        /// <summary>Ward days on those rows' encounters, and the encounters, for Avg. LOS.</summary>
        public decimal WardDays { get; init; }
        public int PackageEncounters { get; init; }
        /// <summary>Claims on invoices HIS put on a claim (IsClaimed); null until the facts carry the flag.</summary>
        public decimal? ClaimsReceived { get; init; }
    }

    public async Task<DoctorRow[]> DoctorsAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().Where(d => d.HospitalId == s.Hospital.Id).ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var deptIds = depts.Keys.ToList();
        // A doctor is in this hospital when their home department or an active HIS clinic is here.
        var clinics = await db.ProviderClinics.AsNoTracking().Where(c => c.IsActive && deptIds.Contains(c.DepartmentId))
            .Select(c => new { c.ProviderId, c.DepartmentId }).ToListAsync(ct);
        var clinicIds = clinics.Select(c => c.ProviderId).Distinct().ToList();
        var providers = await db.Providers.AsNoTracking().Where(x => deptIds.Contains(x.DepartmentId) || clinicIds.Contains(x.Id)).ToListAsync(ct);
        var deptsOf = clinics.GroupBy(c => c.ProviderId).ToDictionary(g => g.Key, g => g.Select(c => c.DepartmentId).ToHashSet());
        var showRevenue = p.Can("revenue.view");
        var figures = await db.RunDimensionCosts.AsNoTracking()
            .Where(x => x.RunId == s.Run.Id && x.Dimension == "physician" && x.Key != "")
            .ToDictionaryAsync(x => x.Key, ct);
        // Staff time and materials on the period's encounters, by attending doctor (the same encounters Cases counts).
        var start = s.Period.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var counted = CountedRevenue(s);
        var periodEncounters = FromHis(s)
            ? db.Encounters.AsNoTracking().Where(e => e.HospitalId == s.Hospital.Id && e.ProviderId != null && counted.Any(r => r.EncounterId == e.Id))
            : db.Encounters.AsNoTracking().Where(e => e.HospitalId == s.Hospital.Id && e.ProviderId != null && e.AdmitAt >= start && e.AdmitAt < end);
        var staffByDoctor = await (from t in db.StaffTimes.AsNoTracking()
                                   join e in periodEncounters on t.EncounterId equals e.Id
                                   group t by e.ProviderId!.Value into g
                                   select new { g.Key, Minutes = g.Sum(x => x.Minutes) }).ToDictionaryAsync(x => x.Key, x => x.Minutes, ct);
        // Materials billed: the invoice lines consumption points to, each once, including VAT (see MaterialLines).
        var materialsByDoctor = showRevenue
            ? await (from m in MaterialLines(s)
                     join e in periodEncounters on m.EncounterId equals e.Id
                     group m by e.ProviderId!.Value into g
                     select new { g.Key, Billed = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Billed, ct)
            : [];
        // Package work by doctor, from the run's stored rows (see PackageBillingSummary.DoctorRowsAsync).
        var work = s.Run.Id == Guid.Empty ? [] : await db.RunPackageDoctors.AsNoTracking()
            .Where(r => r.RunId == s.Run.Id && r.ProviderId != null)
            .GroupBy(r => r.ProviderId!.Value)
            .Select(g => new
            {
                g.Key, Fee = g.Sum(r => r.FeeBilled), Consumables = g.Sum(r => r.ConsumablesBilled + r.ConsumablesCovered),
                Medicine = g.Sum(r => r.MedicineBilled), WardDays = g.Sum(r => r.WardDays),
                Encounters = g.Select(r => r.EncounterId).Distinct().Count(),
                Claimed = g.Sum(r => r.ClaimsReceived ?? 0m), HasClaims = g.Count(r => r.ClaimsReceived != null),
            }).ToDictionaryAsync(x => x.Key, ct);
        return providers.Select(x =>
        {
            var all = deptsOf.GetValueOrDefault(x.Id) ?? [];
            if (depts.ContainsKey(x.DepartmentId)) all.Add(x.DepartmentId);
            return (x, all);
        })
        .Where(t => t.all.Any(d => DeptAllowed(p, deptCodes, d)))
        .Select(t =>
        {
            var x = t.x;
            figures.TryGetValue(x.Id.ToString(), out var f);
            var w = work.GetValueOrDefault(x.Id);
            return new DoctorRow(x.Id, x.Code, x.Name, x.Specialty, x.Grade, x.DepartmentId,
                depts.GetValueOrDefault(x.DepartmentId)?.Name, t.all.Count, x.IsDummy,
                f?.Encounters ?? 0, showRevenue ? f?.Revenue ?? 0m : 0m, f?.TotalCost ?? 0m,
                Money.R4(staffByDoctor.GetValueOrDefault(x.Id)), Money.R4(materialsByDoctor.GetValueOrDefault(x.Id)))
            {
                FeeBilled = showRevenue && w != null ? w.Fee : 0, Consumables = w?.Consumables ?? 0, Medicine = w?.Medicine ?? 0,
                WardDays = Money.R4(w?.WardDays ?? 0), PackageEncounters = w?.Encounters ?? 0,
                ClaimsReceived = showRevenue && w is { HasClaims: > 0 } ? w.Claimed : null,
            };
        })
        .OrderBy(x => x.IsDummy).ThenBy(x => x.Name).ToArray();
    }

    public sealed record DoctorDepartmentRow(Guid Id, string Code, string Name, string? HisClinicId, bool IsHome, bool IsDefault, int Cases);
    /// <param name="PackageWork">The doctor's packages from the run's stored rows, as the package page's doctor table adds them up.</param>
    /// <param name="DepartmentWork">The same work grouped by the encounter's department.</param>
    public sealed record DoctorDetail(DoctorRow Doctor, DoctorDepartmentRow[] Departments, DoctorWorkRow[] Packages, DoctorWorkRow[] Procedures,
        PackageWorkRow[]? PackageWork = null, PackageWorkRow[]? DepartmentWork = null, PackageWorkRow[]? ProcedureWork = null,
        PackageWorkRow[]? DepartmentWorkAll = null);

    /// <summary>One doctor: their figures for the run and the departments (home and HIS clinics) they work in.</summary>
    public async Task<DoctorDetail> DoctorAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await DoctorsAsync(s, p, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("Doctor");
        var depts = await db.Departments.AsNoTracking().Where(d => d.HospitalId == s.Hospital.Id).ToDictionaryAsync(d => d.Id, ct);
        var deptIds = depts.Keys.ToList();
        var clinics = await db.ProviderClinics.AsNoTracking().Where(c => c.ProviderId == id && c.IsActive && deptIds.Contains(c.DepartmentId))
            .ToListAsync(ct);
        // Cases: this run's encounters by this doctor, per encounter department.
        var cases = await (from e in db.Encounters.AsNoTracking()
                           join ec in db.EncounterCosts.AsNoTracking() on e.Id equals ec.EncounterId
                           where ec.RunId == s.Run.Id && e.ProviderId == id
                           group e by e.DepartmentId into g
                           select new { g.Key, N = g.Select(x => x.Id).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var ids = clinics.Select(c => c.DepartmentId).Append(row.DepartmentId).Concat(cases.Keys).Distinct().Where(depts.ContainsKey);
        var rows = ids.Select(d =>
        {
            var dept = depts[d];
            var c = clinics.Where(x => x.DepartmentId == d).ToList();
            return new DoctorDepartmentRow(d, dept.Code, dept.Name, c.Count == 0 ? null : string.Join(", ", c.Select(x => x.HisClinicId)),
                d == row.DepartmentId, c.Any(x => x.IsDefault), cases.GetValueOrDefault(d));
        }).OrderByDescending(x => x.IsHome).ThenByDescending(x => x.Cases).ThenBy(x => x.Name).ToArray();
        var (packages, procedures) = await DoctorWorkAsync(s, p, id, ct);
        var (packageWork, departmentWork) = await DoctorPackageWorkAsync(s, p, id, depts, ct);
        var procedureWork = await DoctorProcedureWorkAsync(s, p, id, depts, ct);
        var departmentWorkAll = await DepartmentWorkAllAsync(packageWork, procedureWork, depts, ct);
        return new DoctorDetail(row, rows, packages, procedures, packageWork, departmentWork, procedureWork, departmentWorkAll);
    }

    /// <summary>
    /// Package and non-package work together, by the encounter's department: each encounter's package and procedure figures
    /// added up, so a department row is everything billed under the doctor on its encounters.
    /// </summary>
    private async Task<PackageWorkRow[]> DepartmentWorkAllAsync(PackageWorkRow[] packageWork, PackageWorkRow[] procedureWork,
        Dictionary<Guid, Department> depts, CancellationToken ct)
    {
        // A package's consumables and medicine claims are the admission's general lines, which are procedure lines here
        // already: drop them from the package side so Claimed counts each line once.
        var units = packageWork.SelectMany(w => w.EncounterRows)
            .Select(e => e with { ConsumablesClaimed = 0, MedicineClaimed = 0, ClaimsReceived = e.ClaimsReceivedLines })
            .Concat(procedureWork.SelectMany(w => w.EncounterRows)).ToList();
        if (units.Count == 0) return [];
        var encIds = units.Select(u => u.EncounterId).Distinct().ToList();
        var deptOf = await db.Encounters.AsNoTracking().Where(e => encIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DepartmentId, ct);
        static PackageDoctorEncounterRow Sum(Guid id, string no, IReadOnlyCollection<PackageDoctorEncounterRow> g) =>
            new(id, no, g.Sum(x => x.Performed), g.Sum(x => x.FeeBilled), g.Sum(x => x.ConsumablesBilled), g.Sum(x => x.ConsumablesCovered),
                g.Sum(x => x.MedicineBilled), g.Sum(x => x.MedicineCovered), g.Sum(x => x.Claimed), g.Sum(x => x.ConsumablesClaimed), g.Sum(x => x.MedicineClaimed),
                g.Sum(x => x.Settled), g.Sum(x => x.RejectedClaims), g.Sum(x => x.WardDays),
                g.Any(x => x.ClaimsReceived != null) ? g.Sum(x => x.ClaimsReceived ?? 0m) : null);
        return units.GroupBy(u => deptOf.GetValueOrDefault(u.EncounterId)).Select(g =>
        {
            var encounters = g.GroupBy(u => u.EncounterId).Select(e => Sum(e.Key, e.First().EncounterNo, e.ToList())).OrderBy(e => e.EncounterNo).ToArray();
            var t = Sum(Guid.Empty, "", encounters);
            var (id, code, name) = depts.TryGetValue(g.Key, out var d) ? ((Guid?)d.Id, d.Code, d.Name) : (null, "", "Not assigned");
            return new PackageWorkRow(id, code, name, encounters.Length, t.Performed, t.FeeBilled, t.ConsumablesBilled, t.ConsumablesCovered,
                t.MedicineBilled, t.MedicineCovered, t.Claimed, t.ConsumablesClaimed, t.MedicineClaimed, t.ClaimsReceived, t.Settled, t.RejectedClaims,
                t.WardDays, encounters);
        }).OrderByDescending(x => x.FeeBilled).ThenBy(x => x.Name).ToArray();
    }

    /// <summary>
    /// The doctor's procedures not billed as or under a package, from the run's stored rows (see ProcedureBillingSummary), one
    /// row per procedure with its encounters; Consumables and Medicine are what consumption ties to the lines. No stay.
    /// </summary>
    private async Task<PackageWorkRow[]> DoctorProcedureWorkAsync(ScopedRun s, Principal p, Guid doctorId, Dictionary<Guid, Department> depts, CancellationToken ct)
    {
        if (s.Run.Id == Guid.Empty) return [];
        var rows = await db.RunProcedureDoctors.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.ProviderId == doctorId).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var encIds = rows.Select(r => r.EncounterId).Distinct().ToList();
        var encs = await db.Encounters.AsNoTracking().Where(e => encIds.Contains(e.Id)).Select(e => new { e.Id, e.EncounterNo, e.DepartmentId }).ToDictionaryAsync(e => e.Id, ct);
        if (!p.AllDepartments)
            rows = rows.Where(r => encs.TryGetValue(r.EncounterId, out var e) && depts.TryGetValue(e.DepartmentId, out var d) && p.HasDepartment(d.Code)).ToList();
        return await ProcedureWorkOfAsync(rows, p.Can("revenue.view"), ct);
    }

    /// <summary>Stored non-package procedure rows as work rows, one per procedure with its encounters.</summary>
    /// <param name="withDoctors">Also split each procedure by the line's doctor (Doctors; Id, Code and Name are the doctor's).</param>
    private async Task<PackageWorkRow[]> ProcedureWorkOfAsync(List<RunProcedureDoctor> rows, bool money, CancellationToken ct, bool withDoctors = false)
    {
        if (rows.Count == 0) return [];
        var encIds = rows.Select(r => r.EncounterId).Distinct().ToList();
        var encs = await db.Encounters.AsNoTracking().Where(e => encIds.Contains(e.Id)).Select(e => new { e.Id, e.EncounterNo }).ToDictionaryAsync(e => e.Id, ct);
        var procIds = rows.Select(r => r.ProcedureId).Distinct().ToList();
        var procs = await db.Procedures.AsNoTracking().Where(x => procIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        PackageDoctorEncounterRow Enc(Guid encounter, IReadOnlyCollection<RunProcedureDoctor> g) =>
            new(encounter, encs.GetValueOrDefault(encounter)?.EncounterNo ?? "", (int)Math.Round(g.Sum(r => r.Performed)),
                money ? g.Sum(r => r.FeeBilled) : 0, g.Sum(r => r.Consumables), 0, g.Sum(r => r.Medicine), 0,
                money ? g.Sum(r => r.Claimed) : 0, 0, 0, money ? g.Sum(r => r.Settled) : 0, money ? g.Sum(r => r.Rejected) : 0, 0,
                money && g.Any(r => r.ClaimsReceived != null) ? g.Sum(r => r.ClaimsReceived ?? 0m) : null,
                money && g.Any(r => r.ClaimsReceived != null) ? g.Sum(r => r.ClaimsReceived ?? 0m) : null);
        var providerIds = withDoctors ? rows.Where(r => r.ProviderId != null).Select(r => r.ProviderId!.Value).Distinct().ToList() : [];
        var providers = await db.Providers.AsNoTracking().Where(x => providerIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.Name }).ToDictionaryAsync(x => x.Id, ct);
        return rows.GroupBy(r => r.ProcedureId).Select(g =>
        {
            var proc = procs.GetValueOrDefault(g.Key);
            var row = Row(g.Key, proc?.Code ?? "", proc?.Name ?? "", g.ToList());
            if (!withDoctors) return row;
            return row with
            {
                Doctors = g.GroupBy(r => r.ProviderId).Select(d =>
                {
                    var doc = d.Key is { } pid ? providers.GetValueOrDefault(pid) : null;
                    return Row(d.Key, doc?.Code ?? "", doc?.Name ?? "No doctor", d.ToList());
                }).OrderByDescending(x => x.FeeBilled).ThenBy(x => x.Name).ToArray(),
            };
        }).OrderByDescending(x => x.Performed).ThenBy(x => x.Name).ToArray();

        PackageWorkRow Row(Guid? id, string code, string name, List<RunProcedureDoctor> list) =>
            new PackageWorkRow(id, code, name, list.Select(r => r.EncounterId).Distinct().Count(),
                (int)Math.Round(list.Sum(r => r.Performed)), money ? list.Sum(r => r.FeeBilled) : 0, list.Sum(r => r.Consumables), 0,
                list.Sum(r => r.Medicine), 0, money ? list.Sum(r => r.Claimed) : 0, 0, 0,
                money && list.Any(r => r.ClaimsReceived != null) ? list.Sum(r => r.ClaimsReceived ?? 0m) : null,
                money ? list.Sum(r => r.Settled) : 0, money ? list.Sum(r => r.Rejected) : 0, 0,
                list.GroupBy(r => r.EncounterId).Select(e => Enc(e.Key, e.ToList())).OrderBy(e => e.EncounterNo).ToArray(),
                money && list.Any(r => r.ClaimsReceived != null) ? list.Sum(r => r.ClaimsReceived ?? 0m) : null);
    }

    /// <summary>The doctor's stored package rows in the run (see PackageBillingSummary.DoctorRowsAsync), by package and by encounter department.</summary>
    private async Task<(PackageWorkRow[] Packages, PackageWorkRow[] Departments)> DoctorPackageWorkAsync(ScopedRun s, Principal p, Guid doctorId,
        Dictionary<Guid, Department> depts, CancellationToken ct)
    {
        if (s.Run.Id == Guid.Empty) return ([], []);
        var rows = await db.RunPackageDoctors.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.ProviderId == doctorId).ToListAsync(ct);
        if (rows.Count == 0) return ([], []);
        var encIds = rows.Select(r => r.EncounterId).Distinct().ToList();
        var encs = await db.Encounters.AsNoTracking().Where(e => encIds.Contains(e.Id)).Select(e => new { e.Id, e.EncounterNo, e.DepartmentId }).ToDictionaryAsync(e => e.Id, ct);
        if (!p.AllDepartments)
            rows = rows.Where(r => encs.TryGetValue(r.EncounterId, out var e) && depts.TryGetValue(e.DepartmentId, out var d) && p.HasDepartment(d.Code)).ToList();
        var encNo = encs.ToDictionary(e => e.Key, e => e.Value.EncounterNo);
        var money = p.Can("revenue.view");
        var pkgIds = rows.Select(r => r.PackageId).Distinct().ToList();
        var packages = await db.Packages.AsNoTracking().Where(k => pkgIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        var byPackage = rows.GroupBy(r => r.PackageId)
            // With the package's contracted price, for Resources Consumed (price × cases), as on the package page.
            .Select(g => WorkRowOf(g.Key, packages.GetValueOrDefault(g.Key)?.Code ?? "", packages.GetValueOrDefault(g.Key)?.Name ?? "", g.ToList(), encNo, money)
                with { Price = money ? packages.GetValueOrDefault(g.Key)?.Price : null })
            .OrderByDescending(x => x.Performed).ThenBy(x => x.Name).ToArray();
        var byDepartment = rows.GroupBy(r => encs.TryGetValue(r.EncounterId, out var e) ? e.DepartmentId : Guid.Empty)
            .Select(g => depts.TryGetValue(g.Key, out var d)
                ? WorkRowOf(d.Id, d.Code, d.Name, g.ToList(), encNo, money)
                : WorkRowOf(null, "", "Not assigned", g.ToList(), encNo, money))
            .OrderByDescending(x => x.Performed).ThenBy(x => x.Name).ToArray();
        return (byPackage, byDepartment);
    }

    // ═══ Cost centres ════════════════════════════════════════════════════════════════

    public sealed record CostCentreRow(Guid Id, string Code, string Name, string CcType, int StepDownSeq, string Department, string? Facility,
        decimal Direct, decimal Received, decimal AllocatedOut, decimal Final, bool IsClosed, decimal GlPosted, int Cases, decimal Revenue, decimal Cost,
        Guid DepartmentId);
    public sealed record CostCentreBilledRow(Guid Id, string Code, string Name, decimal UnitPrice, decimal Cases, decimal Revenue, decimal UnitCost, decimal TotalCost);
    public sealed record CostCentreDetail(CostCentreRow CostCentre, CostCentreBilledRow[] Packages, CostCentreBilledRow[] Procedures);

    public async Task<CostCentreRow[]> CostCentresAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var facs = await db.Facilities.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var bal = await db.CostCenterBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id).ToDictionaryAsync(b => b.CostCenterId, ct);
        var gl = await db.GLPostings.AsNoTracking().Where(g => g.HospitalId == s.Hospital.Id && g.PeriodId == s.Period.Id)
            .GroupBy(g => g.CostCenterId).Select(g => new { g.Key, A = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Key, x => x.A, ct);
        var showRevenue = p.Can("revenue.view");
        Dictionary<Guid, int> cases;
        Dictionary<Guid, decimal> revenue, cost;
        if (s.Run.CostingMethod == CostingModelPipeline.Method)
        {
            var stored = (await db.RunDimensionCosts.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.Dimension == "costCentre" && x.Key != "").ToListAsync(ct))
                .ToDictionary(x => Guid.Parse(x.Key));
            cases = stored.ToDictionary(x => x.Key, x => x.Value.Encounters);
            revenue = stored.ToDictionary(x => x.Key, x => x.Value.Revenue);
            cost = stored.ToDictionary(x => x.Key, x => x.Value.TotalCost);
        }
        else
        {
            cases = await (from es in db.EncounterServices.AsNoTracking()
                               join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                               where ec.RunId == s.Run.Id
                               group es by es.CostCenterId into g
                               select new { g.Key, N = g.Select(x => x.EncounterId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
                .Where(r => r.IsEffective(s.Period.EndDate))
                .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.First().ListPrice);
            var billed = await (from es in db.EncounterServices.AsNoTracking()
                                join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                                where ec.RunId == s.Run.Id
                                group es by new { es.CostCenterId, es.ProcedureId } into g
                                select new { g.Key.CostCenterId, g.Key.ProcedureId, Qty = g.Sum(x => x.Quantity) }).ToListAsync(ct);
            var stored = await db.ProcedureCosts.AsNoTracking()
                .Where(c => c.RunId == s.Run.Id && c.CostType == "ACTUAL")
                .ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
            revenue = billed.GroupBy(x => x.CostCenterId)
                .ToDictionary(g => g.Key, g => Money.R4(g.Sum(x => x.Qty * prices.GetValueOrDefault(x.ProcedureId))));
            cost = billed.GroupBy(x => x.CostCenterId).ToDictionary(g => g.Key, g => Money.R4(g.Sum(x => x.Qty * stored.GetValueOrDefault(x.ProcedureId))));
        }
        return (await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(c => DeptAllowed(p, deptCodes, c.DepartmentId))
            .OrderBy(c => c.StepDownSeq).ThenBy(c => c.Code)
            .Select(c =>
            {
                bal.TryGetValue(c.Id, out var b);
                return new CostCentreRow(c.Id, c.Code, c.Name, c.CcType, c.StepDownSeq, depts[c.DepartmentId].Name, c.FacilityId is { } f ? facs.GetValueOrDefault(f) : null,
                    b?.DirectAmount ?? 0, b?.ReceivedAmount ?? 0, b?.AllocatedOut ?? 0, b?.FinalAmount ?? 0, b?.IsClosed ?? false, gl.GetValueOrDefault(c.Id),
                    cases.GetValueOrDefault(c.Id), showRevenue ? revenue.GetValueOrDefault(c.Id) : 0, cost.GetValueOrDefault(c.Id), c.DepartmentId);
            }).ToArray();
    }

    public async Task<CostCentreDetail> CostCentreAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await CostCentresAsync(s, p, ct)).FirstOrDefault(c => c.Id == id) ?? throw AppError.NotFound("Cost centre");
        var billed = await (from es in CountedServices(s)
                            join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                            where ec.RunId == s.Run.Id && es.CostCenterId == id
                            group es by es.ProcedureId into g
                            select new { ProcedureId = g.Key, Qty = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        if (billed.Count == 0) return new CostCentreDetail(row, [], []);

        var billedIds = billed.Select(x => x.ProcedureId).ToList();
        var billedProcs = await db.Procedures.AsNoTracking()
            .Where(x => billedIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.Name, x.Category, x.ProcType })
            .ToListAsync(ct);
        var billedById = billedProcs.ToDictionary(x => x.Id);
        var billedCodes = billedProcs.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pkgs = (await db.Packages.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(x => billedCodes.Contains(x.Code)).ToList();
        var pkgByCode = pkgs.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.First().ListPrice);
        // Unit cost is what the run stored: billed lines carry their actual cost.
        var stored = await db.ProcedureCosts.AsNoTracking()
            .Where(c => c.RunId == s.Run.Id && c.CostType == "ACTUAL")
            .ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        decimal UnitOf(Guid procedureId) => stored.GetValueOrDefault(procedureId);
        var showRevenue = p.Can("revenue.view");
        var pkgRows = new Dictionary<Guid, CostCentreBilledRow>();
        var procRows = new List<CostCentreBilledRow>();
        foreach (var b in billed)
        {
            if (!billedById.TryGetValue(b.ProcedureId, out var pr)) continue;
            if (pkgByCode.TryGetValue(pr.Code, out var pkg))
            {
                var unit = UnitOf(pr.Id);
                if (pkgRows.TryGetValue(pkg.Id, out var existing))
                {
                    var qty = existing.Cases + b.Qty;
                    pkgRows[pkg.Id] = existing with
                    {
                        Cases = qty,
                        Revenue = showRevenue ? Money.R4(pkg.Price * qty) : 0,
                        TotalCost = Money.R4(unit * qty),
                    };
                }
                else
                {
                    pkgRows[pkg.Id] = new CostCentreBilledRow(pkg.Id, pkg.Code, pkg.Name, pkg.Price, b.Qty,
                        showRevenue ? Money.R4(pkg.Price * b.Qty) : 0, unit, Money.R4(unit * b.Qty));
                }
            }
            else
            {
                var price = prices.GetValueOrDefault(pr.Id);
                var unit = UnitOf(pr.Id);
                procRows.Add(new CostCentreBilledRow(pr.Id, pr.Code, pr.Name, price, b.Qty,
                    showRevenue ? Money.R4(price * b.Qty) : 0, unit, Money.R4(unit * b.Qty)));
            }
        }
        return new CostCentreDetail(row,
            pkgRows.Values.OrderByDescending(x => x.Cases).ThenBy(x => x.Code).ToArray(),
            procRows.OrderByDescending(x => x.Cases).ThenBy(x => x.Code).ToArray());
    }

    // ═══ Cost pools ══════════════════════════════════════════════════════════════════

    public sealed record PoolRow(Guid Id, string Code, string Name, string? Description, Guid CostCentreId, string CostCentre, string Classification,
        string AllocMethod, string Stream, string? PrimaryDriver, string Status, int Version, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal DirectAmount, int Resources);
    public sealed record MappingDto(string Account, string NaturalClass, string CostCentre, decimal Weight, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
    public sealed record PoolDetail(PoolRow Pool, MappingDto[] Mappings, RuleRow[] Rules, AllocationDto[] Allocations, ResourceRow[] FundedResources);

    public async Task<PoolRow[]> CostPoolsAsync(ScopedRun s, CancellationToken ct)
    {
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var funded = await db.Resources.AsNoTracking().Where(r => r.CostPoolId != null).GroupBy(r => r.CostPoolId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var bal = await db.CostPoolBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id).ToDictionaryAsync(b => b.CostPoolId, b => b.DirectAmount, ct);
        return (await db.CostPools.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => new PoolRow(x.Id, x.Code, x.Name, x.Description, x.CostCenterId, ccs.GetValueOrDefault(x.CostCenterId, "?"), x.Classification, x.AllocMethod,
                // CR-4: derived, never flagged by hand.
                funded.ContainsKey(x.Id) ? "CONSUMPTION" : "ALLOCATION", x.PrimaryDriverId is { } d ? drivers.GetValueOrDefault(d) : null, x.Status, x.Version,
                x.EffectiveFrom, x.EffectiveTo, bal.GetValueOrDefault(x.Id), funded.GetValueOrDefault(x.Id))).ToArray();
    }

    public async Task<PoolDetail> CostPoolAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var pool = (await CostPoolsAsync(s, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Cost pool");
        var maps = await (from m in db.GLMappings.AsNoTracking()
                          join a in db.GLAccounts on m.GLAccountId equals a.Id
                          join c in db.CostCenters on m.CostCenterId equals c.Id
                          where m.CostPoolId == id
                          select new MappingDto($"{a.Code} {a.Name}", a.NaturalClass, c.Name, m.Weight, m.EffectiveFrom, m.EffectiveTo)).ToArrayAsync(ct);
        var rules = (await AllocationRulesAsync(s, ct)).Where(r => r.SourcePoolId == id).ToArray();
        var ruleIds = rules.Select(r => r.Id).ToHashSet();
        var allocs = await AllocationsAsync(s.Run.Id, a => a.SourcePoolId == id || (a.RuleId is { } r && ruleIds.Contains(r)), ct);
        var resources = (await ResourcesAsync(s.Hospital, p, ct, s.Run.Id)).Where(r => r.CostPoolId == id).ToArray();
        return new PoolDetail(pool, maps, rules, allocs.ToArray(), resources);
    }

    // ═══ Resources ═══════════════════════════════════════════════════════════════════

    public sealed record ResourceRow(Guid Id, string Code, string Name, string ResourceType, string Nature, string Uom, bool IsCapacityDriven, string CostCentre,
        Guid? CostPoolId, string? CostPool, string? Rate, decimal UtilisationPct, decimal CostOfUsed, decimal CostOfUnused, decimal UsedQuantity, int Physicians, int Cases = 0, decimal Revenue = 0m);
    public sealed record RateDto(string? UnitCost, string Uom, string Basis, string SourceSystem, DateOnly EffectiveFrom, DateOnly? EffectiveTo, int Version, string Status, bool EffectiveInRun);
    public sealed record CapacityDto2(decimal TheoreticalMinutes, decimal PracticalRatio, decimal PracticalMinutes, string? AnnualCost, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
    public sealed record ResourceDetail(ResourceRow Resource, RateDto[] Rates, CapacityDto2[] Capacity, CapacityRow? UnitCost, ProviderShare[] Providers, RecipeUse[] UsedBy);
    public sealed record ProviderShare(Guid Id, string Code, string Name, string Department, string Specialty, string Grade, int BilledLines);
    public sealed record RecipeUse(Guid ActivityId, string Activity, decimal StdQuantity, string Uom);

    /// <summary>A hospital's resource catalogue. Rates are the ones effective today, unless a run's are asked for.</summary>
    public async Task<ResourceRow[]> ResourcesAsync(Hospital hospital, Principal p, CancellationToken ct, Guid? runId = null)
    {
        var ccs = await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == hospital.Id).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var pools = await db.CostPools.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var units = runId is { } run
            ? await db.ResourceUnitCosts.AsNoTracking().Where(u => u.RunId == run).ToDictionaryAsync(u => u.ResourceId, ct)
            : [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rates = (await (from x in db.ResourceRates.AsNoTracking()
                            join r in db.Resources on x.ResourceId equals r.Id
                            where r.HospitalId == hospital.Id
                            select x).ToListAsync(ct))
            .Where(x => x.IsEffective(today))
            .GroupBy(x => x.ResourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveFrom).First().UnitCost);
        var q = db.Resources.AsNoTracking().Where(r => r.HospitalId == hospital.Id && r.IsActive);
        if (!string.IsNullOrWhiteSpace(hospital.HisSetupId))
            q = q.Where(r => r.Code.Contains(".RES-MD-"));
        var physicians = await (from x in db.Providers.AsNoTracking()
                                join d in db.Departments.AsNoTracking() on x.DepartmentId equals d.Id
                                where d.HospitalId == hospital.Id
                                group x by x.ResourceId into g
                                select new { g.Key, N = g.Count() }).ToListAsync(ct);
        var physiciansOf = physicians.Where(x => x.Key != null).ToDictionary(x => x.Key!.Value, x => x.N);
        return (await q.OrderBy(r => r.ResourceType).ThenBy(r => r.Code).ToListAsync(ct))
            .Select(r =>
            {
                units.TryGetValue(r.Id, out var u);
                var rate = runId is null ? rates.GetValueOrDefault(r.Id) : u?.UnitCost;
                return new ResourceRow(r.Id, r.Code, r.Name, r.ResourceType, Catalogue.NatureOf(r.ResourceType), r.Uom, r.IsCapacityDriven, ccs.GetValueOrDefault(r.CostCenterId, "?"),
                    r.CostPoolId, r.CostPoolId is { } pid ? pools.GetValueOrDefault(pid) : null, rate is { } value ? Masked(p, r.ResourceType, value) : null,
                    u?.UtilisationPct ?? 0, u?.CostOfUsedCapacity ?? 0, u?.CostOfUnusedCapacity ?? 0, u?.UsedMinutes ?? 0, physiciansOf.GetValueOrDefault(r.Id));
            }).ToArray();
    }

    private static ResourceRow NotAssignedRow(int physicians) =>
        new(Guid.Empty, "", "Not assigned", "", "", "", false, "", null, null, null, 0, 0, 0, 0, physicians);

    /// <summary>The Resources screen: physicians with no resource, if any, come first as "Not assigned".</summary>
    public async Task<ResourceRow[]> ResourceListAsync(Hospital hospital, Principal p, CancellationToken ct)
    {
        var rows = await ResourcesAsync(hospital, p, ct);
        var listed = rows.Select(r => r.Id).ToList();
        var notAssigned = await (from x in db.Providers.AsNoTracking()
                                 join d in db.Departments.AsNoTracking() on x.DepartmentId equals d.Id
                                 where d.HospitalId == hospital.Id && (x.ResourceId == null || !listed.Contains(x.ResourceId.Value))
                                 select x.Id).CountAsync(ct);
        return notAssigned == 0 ? rows : [NotAssignedRow(notAssigned), .. rows];
    }

    /// <summary>The resource list with the encounters and revenue its doctors had in the scope's run; nothing without a run.</summary>
    public async Task<ResourceRow[]> ResourceListAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var rows = await ResourceListAsync(s.Hospital, p, ct);
        var results = await db.RunDimensionCosts.AsNoTracking().Where(x => x.RunId == s.Run.Id && x.Dimension == "resource").ToListAsync(ct);
        var byKey = results.ToDictionary(x => x.Key);
        var showRevenue = p.Can("revenue.view");
        return rows.Select(r => byKey.TryGetValue(r.Id == Guid.Empty ? "" : r.Id.ToString(), out var x)
            ? r with { Cases = x.Encounters, Revenue = showRevenue ? x.Revenue : 0m } : r).ToArray();
    }

    /// <summary>A role and the doctors mapped to it; invoice lines count everything billed at the hospital.</summary>
    public async Task<ResourceDetail> ResourceAsync(Hospital hospital, Principal p, Guid id, CancellationToken ct)
    {
        var listedRows = await ResourceListAsync(hospital, p, ct);
        var notAssigned = id == Guid.Empty;
        var row = listedRows.FirstOrDefault(r => r.Id == id)
            ?? (notAssigned ? NotAssignedRow(0) : throw AppError.NotFound("Resource"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rates = (await db.ResourceRates.AsNoTracking().Where(r => r.ResourceId == id).OrderBy(r => r.EffectiveFrom).ToListAsync(ct))
            .Select(r => new RateDto(Masked(p, row.ResourceType, r.UnitCost), r.Uom, r.Basis, r.SourceSystem, r.EffectiveFrom, r.EffectiveTo, r.Version, r.Status, r.IsEffective(today))).ToArray();
        var caps = (await db.ResourceCapacities.AsNoTracking().Where(c => c.ResourceId == id).OrderBy(c => c.EffectiveFrom).ToListAsync(ct))
            .Select(c => new CapacityDto2(c.TheoreticalMinutes, c.PracticalRatio, c.PracticalMinutes, row.ResourceType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : c.AnnualCost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), c.EffectiveFrom, c.EffectiveTo)).ToArray();
        var listed = listedRows.Select(r => r.Id).ToList();
        var providers = await (from x in db.Providers.AsNoTracking()
                               join d in db.Departments.AsNoTracking() on x.DepartmentId equals d.Id
                               where notAssigned
                                   ? d.HospitalId == hospital.Id && (x.ResourceId == null || !listed.Contains(x.ResourceId.Value))
                                   : x.ResourceId == id
                               orderby x.Name
                               select new { x.Id, x.Code, x.Name, Department = d.Name, x.Specialty, x.Grade }).ToListAsync(ct);
        var doctorIds = providers.Select(x => x.Id).ToList();
        var billed = await (from svc in db.EncounterServices.AsNoTracking()
                            where svc.ProviderId != null && doctorIds.Contains(svc.ProviderId.Value)
                            group svc by svc.ProviderId into g
                            select new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.N, ct);
        var used = await (from ar in db.ActivityResources.AsNoTracking()
                          join a in db.Activities on ar.ActivityId equals a.Id
                          where ar.ResourceId == id
                          select new RecipeUse(a.Id, a.Name, ar.StdQuantity, ar.Uom)).ToArrayAsync(ct);
        return new ResourceDetail(row, rates, caps, null,
            providers.Select(x => new ProviderShare(x.Id, x.Code, x.Name, x.Department, x.Specialty, x.Grade, billed.GetValueOrDefault(x.Id))).ToArray(), used);
    }

    // ═══ Cost drivers ════════════════════════════════════════════════════════════════

    public sealed record DriverRow(Guid Id, string Code, string CatalogCode, string Name, string Description, string Unit, string DriverType,
        string SourceSystem, string SourceField, string Formula, string Frequency, string AllocMethodology, string Status, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal Volume);
    public sealed record DriverVolumeDto(string CostCentre, decimal Quantity, decimal Share, string SourceSystem);
    public sealed record DriverDetail(DriverRow Driver, DriverVolumeDto[] Volumes, RuleRow[] Rules);

    public async Task<DriverRow[]> DriversAsync(ScopedRun s, CancellationToken ct)
    {
        var vols = await db.DriverVolumes.AsNoTracking().Where(v => v.PeriodId == s.Period.Id).GroupBy(v => v.DriverId)
            .Select(g => new { g.Key, Q = g.Sum(v => v.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q, ct);
        return (await db.CostDrivers.AsNoTracking().Where(d => d.HospitalId == s.Hospital.Id).OrderBy(d => d.Code).ToListAsync(ct))
            .Select(d => new DriverRow(d.Id, d.Code, d.CatalogCode, d.Name, d.Description, d.Unit, d.DriverType, d.SourceSystem, d.SourceField, d.Formula,
                d.Frequency, d.AllocMethodology, d.Status, d.EffectiveFrom, d.EffectiveTo, vols.GetValueOrDefault(d.Id))).ToArray();
    }

    public async Task<DriverDetail> DriverAsync(ScopedRun s, Guid id, CancellationToken ct)
    {
        var row = (await DriversAsync(s, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("Cost driver");
        var vols = await (from v in db.DriverVolumes.AsNoTracking()
                          join c in db.CostCenters on v.CostCenterId equals c.Id
                          where v.DriverId == id && v.PeriodId == s.Period.Id
                          select new { c.Name, v.Quantity, v.SourceSystem }).ToListAsync(ct);
        var total = vols.Sum(v => v.Quantity);
        return new DriverDetail(row, vols.OrderByDescending(v => v.Quantity).Select(v => new DriverVolumeDto(v.Name, v.Quantity, Share(v.Quantity, total), v.SourceSystem)).ToArray(),
            (await AllocationRulesAsync(s, ct)).Where(r => r.DriverId == id).ToArray());
    }

    // ═══ Activities ══════════════════════════════════════════════════════════════════

    public sealed record ActivityRow(Guid Id, string Code, string CatalogCode, string Name, string Category, Guid CostCentreId, string CostCentre, string Department,
        decimal StdDuration, decimal MinDuration, decimal MaxDuration, string Variability, string Directness, decimal Volume, decimal DirectCost,
        decimal OverheadCost, decimal TotalCost, decimal UnitCost, string? CalcExpression, string State);
    public sealed record RecipeLineDto(Guid ResourceId, string Resource, string ResourceType, decimal StdQuantity, string Uom, string? Rate, decimal? LineCost);
    public sealed record ActivityDetail(ActivityRow Activity, RecipeLineDto[] Recipe, AllocationDto[] OverheadIn, ProcedureUse[] UsedBy);
    public sealed record ProcedureUse(Guid ProcedureId, string Procedure, decimal Quantity);

    public async Task<ActivityRow[]> ActivitiesAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var costs = await db.ActivityCosts.AsNoTracking().Where(a => a.RunId == s.Run.Id).ToDictionaryAsync(a => a.ActivityId, ct);
        var closed = await db.CostCenterBalances.AsNoTracking().Where(b => b.RunId == s.Run.Id && b.IsClosed).Select(b => b.CostCenterId).ToListAsync(ct);
        return (await db.Activities.AsNoTracking().Where(a => a.HospitalId == s.Hospital.Id).OrderBy(a => a.Code).ToListAsync(ct))
            .Where(a => DeptAllowed(p, deptCodes, a.DepartmentId))
            .Select(a =>
            {
                costs.TryGetValue(a.Id, out var c);
                // FR-11.14: degraded states are named, never rendered as an empty row.
                var state = c is not null ? "COSTED"
                    : closed.Contains(a.CostCenterId) ? "FULLY_ALLOCATED_SUPPORT"
                    : "NO_CASES";
                return new ActivityRow(a.Id, a.Code, a.CatalogCode, a.Name, a.Category, a.CostCenterId, ccs[a.CostCenterId].Name, depts[a.DepartmentId].Name,
                    a.StdDuration, a.MinDuration, a.MaxDuration, a.Variability, a.Directness, c?.Volume ?? 0, c?.DirectCost ?? 0, c?.OverheadCost ?? 0,
                    c?.TotalCost ?? 0, c?.UnitCost ?? 0, c?.CalcExpression, state);
            }).ToArray();
    }

    public async Task<ActivityDetail> ActivityAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await ActivitiesAsync(s, p, ct)).FirstOrDefault(a => a.Id == id) ?? throw AppError.NotFound("Activity");
        var units = await db.ResourceUnitCosts.AsNoTracking().Where(u => u.RunId == s.Run.Id).ToDictionaryAsync(u => u.ResourceId, u => u.UnitCost, ct);
        var recipe = (await (from ar in db.ActivityResources.AsNoTracking()
                             join r in db.Resources on ar.ResourceId equals r.Id
                             where ar.ActivityId == id
                             select new { r.Id, r.Name, r.ResourceType, ar.StdQuantity, ar.Uom }).ToListAsync(ct))
            .Select(x =>
            {
                var hasRate = units.TryGetValue(x.Id, out var rate);
                var masked = Masked(p, x.ResourceType, rate);
                return new RecipeLineDto(x.Id, x.Name, x.ResourceType, x.StdQuantity, x.Uom, hasRate ? masked : null, hasRate && masked is not null ? Money.R4(x.StdQuantity * rate) : null);
            }).ToArray();
        var allocs = await AllocationsAsync(s.Run.Id, a => a.TargetActivityId == id, ct);
        var uses = await (from l in db.ProcedureActivityLines.AsNoTracking()
                          join d in db.CostingDefinitions on l.DefinitionId equals d.Id
                          join pr in db.Procedures on d.ProcedureId equals pr.Id
                          where l.ActivityId == id && d.CostingVersionId == s.Version.Id
                          select new ProcedureUse(pr.Id, pr.Name, l.Quantity)).ToArrayAsync(ct);
        return new ActivityDetail(row, recipe, allocs.ToArray(), uses);
    }

    // ═══ Allocation rules ════════════════════════════════════════════════════════════

    public sealed record RuleTargetDto(string? CostCentre, decimal? Percentage, decimal Weight);
    public sealed record RuleRow(Guid Id, string Code, string Name, Guid SourcePoolId, string SourcePool, string SourceCostCentre, Guid? DriverId, string? Driver,
        string Method, string Methodology, int Stage, int Sequence, decimal PoolShare, string? Formula, string Status, int Version, DateOnly EffectiveFrom,
        DateOnly? EffectiveTo, decimal MovedThisRun, int Results, RuleTargetDto[] Targets);

    public async Task<RuleRow[]> AllocationRulesAsync(ScopedRun s, CancellationToken ct)
    {
        var pools = await db.CostPools.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).ToDictionaryAsync(x => x.Id, ct);
        var ccs = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var drivers = await db.CostDrivers.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var moved = await db.AllocationResults.AsNoTracking().Where(a => a.RunId == s.Run.Id && a.RuleId != null).GroupBy(a => a.RuleId!.Value)
            .Select(g => new { g.Key, A = g.Sum(x => x.Amount), N = g.Count() }).ToDictionaryAsync(x => x.Key, ct);
        var targets = (await db.AllocationRuleTargets.AsNoTracking().ToListAsync(ct)).ToLookup(t => t.RuleId);
        return (await db.AllocationRules.AsNoTracking().ToListAsync(ct)).Where(r => pools.ContainsKey(r.SourcePoolId))
            .OrderBy(r => r.Stage).ThenBy(r => r.Sequence).ThenBy(r => r.Code)
            .Select(r =>
            {
                moved.TryGetValue(r.Id, out var m);
                var pool = pools[r.SourcePoolId];
                return new RuleRow(r.Id, r.Code, r.Name, r.SourcePoolId, pool.Name, ccs.GetValueOrDefault(pool.CostCenterId, "?"), r.DriverId,
                    r.DriverId is { } d ? drivers.GetValueOrDefault(d) : null, r.Method, r.Methodology, r.Stage, r.Sequence, r.PoolShare, r.Formula, r.Status,
                    r.Version, r.EffectiveFrom, r.EffectiveTo, m?.A ?? 0, m?.N ?? 0,
                    targets[r.Id].Select(t => new RuleTargetDto(t.CostCenterId is { } c ? ccs.GetValueOrDefault(c) : null, t.Percentage, t.Weight)).ToArray());
            }).ToArray();
    }
}
