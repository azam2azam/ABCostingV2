using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    public sealed record ComponentDto(Guid Id, string ComponentType, string CostNature, string Label, decimal Quantity, string Uom, string? UnitCost,
        decimal Amount, decimal Share, string CalcExpression, string? SourceSystem, string? SourceRef, Guid? ResourceId, Guid? ActivityId);
    public sealed record Provenance(string Run, string RunStatus, string CostingVersion, string Hospital, string Period, string TriggeredBy,
        string? ApprovedBy, DateTime? PublishedAt, DateTime? CalculatedAt);

    private static Provenance Prov(ScopedRun s) => new(s.Run.Code, s.Run.Status, s.Version.Code, s.Hospital.Name, s.Period.Code, s.Run.TriggeredBy,
        s.Run.ApprovedBy, s.Run.PublishedAt, s.Run.FinishedAt);

    private ComponentDto[] Components(Principal p, IEnumerable<CostComponent> rows)
    {
        var list = rows.ToList();
        var total = list.Sum(c => c.Amount);
        return list.OrderBy(c => Array.IndexOf(Catalogue.ComponentDisplayOrder, c.ComponentType)).ThenByDescending(c => c.Amount)
            .Select(c => new ComponentDto(c.Id, c.ComponentType, c.CostNature, c.Label, c.Quantity, c.Uom,
                c.ComponentType.StartsWith("LABOR_") && !p.Can("salary.view") ? null : Money.R6(c.UnitCost).ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.Amount, Share(c.Amount, total), c.CalcExpression, c.SourceSystem, c.SourceRef, c.ResourceId, c.ActivityId)).ToArray();
    }

    // ═══ Procedures and the cost explainer (FR-11.7, AC primary test 23.1) ═══════════

    public sealed record ProcedureRow(Guid Id, string Code, string CatalogCode, string Name, string Category, string ProcType, string Department,
        string? ServiceLine, decimal Volume, decimal StandardUnit, decimal ActualUnit, decimal ActualTotal, decimal Revenue, decimal Variance,
        decimal VariancePct, decimal ListPrice, decimal UnitCost, decimal TotalCost, string State);
    public sealed record ChainActivity(Guid ActivityId, string Activity, decimal Quantity, string CostCentre, decimal OverheadPerOccurrence,
        AllocationDto[] OverheadSources, RecipeLineDto[] Resources);
    public sealed record SourceTxn(Guid Id, string EncounterNo, string Activity, string Resource, decimal Quantity, string Uom, DateTime OccurredAt,
        string SourceSystem, string SourceRef);
    public sealed record CrossHospital(string Hospital, string HospitalName, string Run, string RunStatus, Guid ProcedureId, decimal StandardUnit,
        decimal ActualUnit, decimal Volume);
    public sealed record ContainingPackage(Guid Id, string Code, string Name, decimal Quantity, decimal Price, int Cases);
    public sealed record ProcedureDetail(ProcedureRow Procedure, ComponentDto[] StandardComponents, decimal StandardTotal, ProcedureCost? Actual,
        VarianceDetail? Variance, ChainActivity[] Chain, SourceTxn[] SampleTransactions, CrossHospital[] AcrossHospitals, Provenance Provenance,
        ContainingPackage[] Packages, RecipeLineDto[] Recipe);

    /// <summary>The Procedure/Activity list: a package is billed as a procedure of the same code, but it belongs on the Packages list.</summary>
    public async Task<ProcedureRow[]> ProcedureListAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var packages = (await db.Packages.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).Select(x => x.Code).ToListAsync(ct)).ToHashSet();
        return (await ProceduresAsync(s, p, ct)).Where(x => !packages.Contains(x.Code)).ToArray();
    }

    public async Task<ProcedureRow[]> ProceduresAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var sls = await db.ServiceLines.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var costs = (await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id).ToListAsync(ct)).ToLookup(c => c.ProcedureId);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct)).Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.First().ListPrice);
        // With a run, cost is what the run stored. Only a period with no run works it out from the costing model.
        var hasRun = s.Run.Id != Guid.Empty;
        var typeMargins = hasRun ? [] : await TypeMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        var itemMargins = hasRun ? [] : await ItemMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        Dictionary<Guid, decimal> packagePcts = hasRun ? [] : await PackagePctsAsync(s, ct);
        var showRevenue = p.Can("revenue.view");
        decimal? MarginFor(Procedure x)
        {
            if (itemMargins.TryGetValue(x.Id, out var item)) return item;
            var type = string.Equals(x.Category, "MEDICINE", StringComparison.OrdinalIgnoreCase) ? ProcedureTypeMedicine
                : string.IsNullOrWhiteSpace(x.ProcType) ? "UNSPECIFIED" : x.ProcType;
            if (typeMargins.TryGetValue(type, out var typePct)) return typePct;
            return typeMargins.GetValueOrDefault(ProcedureTypeAll);
        }
        return (await db.Procedures.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct))
            .Where(x => DeptAllowed(p, deptCodes, x.DepartmentId))
            .Select(x =>
            {
                var std = costs[x.Id].FirstOrDefault(c => c.CostType == "STANDARD");
                var act = costs[x.Id].FirstOrDefault(c => c.CostType == "ACTUAL");
                var vol = act?.Volume ?? 0;
                var stdTotal = (std?.UnitCost ?? 0) * vol;
                var variance = act is null ? 0 : Money.R4(act.TotalCost - stdTotal);
                var state = std is null ? "NOT_COSTED" : act is null ? "NO_CASES" : "COSTED";
                var price = prices.GetValueOrDefault(x.Id);
                var margin = packagePcts.TryGetValue(x.Id, out var packagePct) ? packagePct : MarginFor(x);
                var unit = hasRun ? act?.UnitCost ?? std?.UnitCost ?? 0 : margin is { } pct && pct != 0 ? Money.R4(price * pct / 100m) : std?.UnitCost ?? 0;
                var total = hasRun ? act?.TotalCost ?? 0 : Money.R4(unit * vol);
                return new ProcedureRow(x.Id, x.Code, x.CatalogCode, x.Name, x.Category, x.ProcType, depts[x.DepartmentId].Name,
                    x.ServiceLineId is { } sl ? sls.GetValueOrDefault(sl) : null, vol, std?.UnitCost ?? 0, act?.UnitCost ?? 0, act?.TotalCost ?? 0,
                    showRevenue ? act?.Revenue ?? 0 : 0, variance, stdTotal == 0 ? 0 : Money.R4(variance / stdTotal * 100m), price, unit, total, state);
            }).ToArray();
    }

    public async Task<ProcedureDetail> ProcedureAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await ProceduresAsync(s, p, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Procedure");
        var pkgs = await (from c in db.PackageComponents.AsNoTracking()
                          join pk in db.Packages.AsNoTracking() on c.PackageId equals pk.Id
                          where c.ProcedureId == id
                          orderby pk.Name
                          select new { pk.Id, pk.Code, pk.Name, pk.Price, c.Quantity }).ToListAsync(ct);
        var pkgIds = pkgs.Select(x => x.Id).ToHashSet();
        Dictionary<Guid, int> billed = [];
        if (pkgIds.Count > 0)
        {
            billed = (await (from es in CountedServices(s)
                             join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                             join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                             join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                             where ec.RunId == s.Run.Id && pkgIds.Contains(pkg.Id)
                             select new { pkg.Id, es.Quantity }).ToListAsync(ct))
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => (int)Math.Round(g.Sum(x => x.Quantity), MidpointRounding.AwayFromZero));
        }
        var packages = pkgs.Select(x => new ContainingPackage(x.Id, x.Code, x.Name, x.Quantity, x.Price, billed.GetValueOrDefault(x.Id))).ToArray();
        return new ProcedureDetail(row, [], 0, null, null, [], [], [], Prov(s), packages, await ProcedureRecipeAsync(s, p, id, ct));
    }

    private async Task<RecipeLineDto[]> ProcedureRecipeAsync(ScopedRun s, Principal p, Guid procedureId, CancellationToken ct)
    {
        var units = await db.ResourceUnitCosts.AsNoTracking().Where(u => u.RunId == s.Run.Id).ToDictionaryAsync(u => u.ResourceId, u => u.UnitCost, ct);
        var fallback = (await (
                from rt in db.ResourceRates.AsNoTracking()
                join res in db.Resources.AsNoTracking() on rt.ResourceId equals res.Id
                where res.HospitalId == s.Hospital.Id
                select rt).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ResourceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.EffectiveFrom).First().UnitCost);
        var lines = await (
            from l in db.ProcedureResourceLines.AsNoTracking()
            join d in db.CostingDefinitions.AsNoTracking() on l.DefinitionId equals d.Id
            join r in db.Resources on l.ResourceId equals r.Id
            where d.ProcedureId == procedureId && d.CostingVersionId == s.Version.Id
            orderby r.Name
            select new { r.Id, r.Name, r.ResourceType, l.Quantity, l.Uom }).ToListAsync(ct);
        return lines.Select(x =>
        {
            decimal rate = 0;
            var has = units.TryGetValue(x.Id, out rate) || fallback.TryGetValue(x.Id, out rate);
            var masked = has ? Masked(p, x.ResourceType, rate) : null;
            return new RecipeLineDto(x.Id, x.Name, x.ResourceType, x.Quantity, x.Uom, has ? masked : null,
                has && masked is not null ? Money.R4(x.Quantity * rate) : null);
        }).ToArray();
    }

    public async Task<RecipeLineDto[]> AddProcedureRecipeAsync(ScopedRun s, Principal p, Guid procedureId, Guid resourceId,
        decimal quantity, string uom, decimal? rate, CancellationToken ct)
    {
        if (quantity <= 0) throw AppError.Invalid("Std qty must be greater than zero.");
        uom = uom.Trim();
        if (uom.Length == 0) throw AppError.Invalid("Unit is required.");
        var proc = await db.Procedures.FirstOrDefaultAsync(x => x.Id == procedureId && x.HospitalId == s.Hospital.Id, ct)
            ?? throw AppError.NotFound("Procedure");
        var res = await db.Resources.FirstOrDefaultAsync(x => x.Id == resourceId && x.HospitalId == s.Hospital.Id && x.IsActive, ct)
            ?? throw AppError.NotFound("Resource");

        var def = await db.CostingDefinitions.FirstOrDefaultAsync(d => d.ProcedureId == procedureId && d.CostingVersionId == s.Version.Id, ct);
        if (def is null)
        {
            def = new CostingDefinition { Id = IdGen.Sequential(), ProcedureId = procedureId, CostingVersionId = s.Version.Id };
            db.CostingDefinitions.Add(def);
        }

        var line = await db.ProcedureResourceLines.FirstOrDefaultAsync(l => l.DefinitionId == def.Id && l.ResourceId == resourceId, ct);
        var created = line is null;
        if (line is null)
        {
            db.ProcedureResourceLines.Add(new ProcedureResourceLine
            {
                Id = IdGen.Sequential(), DefinitionId = def.Id, ResourceId = resourceId, Quantity = Money.R6(quantity), Uom = uom,
            });
        }
        else
        {
            line.Quantity = Money.R6(quantity);
            line.Uom = uom;
        }

        if (rate is > 0)
        {
            var asOf = s.Period.EndDate;
            var existing = (await db.ResourceRates.Where(r => r.ResourceId == resourceId).ToListAsync(ct))
                .Where(r => r.IsEffective(asOf)).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
            if (existing is null)
            {
                db.ResourceRates.Add(new ResourceRate
                {
                    Id = IdGen.Sequential(), ResourceId = resourceId, UnitCost = Money.R6(rate.Value), Uom = uom,
                    Basis = "TDABC", SourceSystem = "DATA-ENTRY", EffectiveFrom = s.Period.StartDate, Status = "PUBLISHED",
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, created ? "CREATE" : "UPDATE", "ProcedureResourceLine", proc.Id.ToString(), proc.Code,
            metadata: new { resource = res.Code, quantity, uom, rate }, ct: ct);
        return await ProcedureRecipeAsync(s, p, procedureId, ct);
    }

    public async Task<RecipeLineDto[]> DeleteProcedureRecipeAsync(ScopedRun s, Principal p, Guid procedureId, Guid resourceId,
        CancellationToken ct)
    {
        var proc = await db.Procedures.FirstOrDefaultAsync(x => x.Id == procedureId && x.HospitalId == s.Hospital.Id, ct)
            ?? throw AppError.NotFound("Procedure");
        var def = await db.CostingDefinitions.FirstOrDefaultAsync(d => d.ProcedureId == procedureId && d.CostingVersionId == s.Version.Id, ct)
            ?? throw AppError.NotFound("Recipe line");
        var line = await db.ProcedureResourceLines.FirstOrDefaultAsync(l => l.DefinitionId == def.Id && l.ResourceId == resourceId, ct)
            ?? throw AppError.NotFound("Recipe line");
        var res = await db.Resources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == resourceId, ct);
        db.ProcedureResourceLines.Remove(line);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "DELETE", "ProcedureResourceLine", proc.Id.ToString(), proc.Code,
            metadata: new { resource = res?.Code, quantity = line.Quantity, uom = line.Uom }, ct: ct);
        return await ProcedureRecipeAsync(s, p, procedureId, ct);
    }

    // ═══ Packages and DRGs ═══════════════════════════════════════════════════════════

    public sealed record PackageRow(Guid Id, string Code, string Name, string Description, decimal Price, decimal CompositionPrice, int CoveredLOS, int Cases, decimal StandardCost,
        decimal ActualCost, decimal Revenue, decimal Variance, decimal VariancePct, decimal OperatingMargin, decimal MarginPct, decimal StandardPerCase, decimal ActualPerCase,
        string ProcType, decimal UnitCost, decimal TotalCost);
    public sealed record ProcedureCostingItem(Guid Id, string Code, string Name, decimal? MarginPct, decimal ListPrice, Guid? PackageId = null, decimal? CompositionCost = null);
    public sealed record ProcedureTypeRow(string Code, string Name, int Procedures, decimal? MarginPct, ProcedureCostingItem[] Items);
    public sealed record PackageComponentDto(Guid ProcedureId, string Procedure, decimal Quantity, decimal StandardUnit, decimal StandardLine, string Code, string Category, string ProcType, decimal Price, bool IsMedicine, decimal Cost);
    public sealed record PackageDetail(PackageRow Package, PackageComponentDto[] Components, EncounterRow[] Encounters, string Explanation, int EncounterCount, decimal? AverageEncounterFee);

    public async Task<PackageRow[]> PackagesAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var costs = await db.PackageCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id).ToDictionaryAsync(c => c.PackageId, ct);
        var std = await db.ProcedureCosts.AsNoTracking()
            .Where(c => c.RunId == s.Run.Id && c.CostType == "STANDARD")
            .ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        var comps = (await (from c in db.PackageComponents.AsNoTracking()
                            join pkg in db.Packages.AsNoTracking() on c.PackageId equals pkg.Id
                            where pkg.HospitalId == s.Hospital.Id
                            select new { c.PackageId, c.ProcedureId, c.Quantity }).ToListAsync(ct));
        var estimated = comps
            .GroupBy(x => x.PackageId)
            .ToDictionary(g => g.Key, g => Money.R4(g.Sum(x => x.Quantity * std.GetValueOrDefault(x.ProcedureId))));
        var composition = comps
            .GroupBy(x => x.PackageId)
            .ToDictionary(g => g.Key, g => Money.R4(g.Sum(x => x.Quantity * prices.GetValueOrDefault(x.ProcedureId))));
        var procs = await db.Procedures.AsNoTracking()
            .Where(pr => pr.HospitalId == s.Hospital.Id)
            .Select(pr => new { pr.Id, pr.Code, pr.ProcType, pr.Category })
            .ToListAsync(ct);
        var types = procs.ToDictionary(pr => pr.Code, pr => pr.ProcType);
        var procIdByCode = procs.ToDictionary(pr => pr.Code, pr => pr.Id);
        // Unit and total cost are what the run stored for the billed package. With no run for the period they
        // come from the period's costing model: the package's Package Costing %, else its composition.
        var hasRun = s.Run.Id != Guid.Empty;
        Dictionary<Guid, decimal> packagePcts = hasRun ? [] : await PackagePctsAsync(s, ct);
        Dictionary<Guid, decimal> modelUnit = hasRun ? [] : await CompositionCostsAsync(s, ct);
        var runCost = await db.ProcedureCosts.AsNoTracking()
            .Where(c => c.RunId == s.Run.Id && c.CostType == "ACTUAL")
            .ToDictionaryAsync(c => c.ProcedureId, ct);
        var (periodStart, periodEnd) = (s.Period.StartDate.ToDateTime(TimeOnly.MinValue), s.Period.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var fromHis = FromHis(s);
        var billed = (hasRun
            ? await (from es in CountedServices(s)
                     join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                     join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                     join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                     where ec.RunId == s.Run.Id && pkg.HospitalId == s.Hospital.Id
                     select new { pkg.Id, es.Quantity }).ToListAsync(ct)
            : await (from es in CountedServices(s)
                     join e in db.Encounters.AsNoTracking() on es.EncounterId equals e.Id
                     join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                     join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                     where e.HospitalId == s.Hospital.Id && (fromHis || (e.AdmitAt >= periodStart && e.AdmitAt < periodEnd)) && pkg.HospitalId == s.Hospital.Id
                     select new { pkg.Id, es.Quantity }).ToListAsync(ct))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => (int)Math.Round(g.Sum(x => x.Quantity), MidpointRounding.AwayFromZero));
        var showRevenue = p.Can("revenue.view");
        return (await db.Packages.AsNoTracking().Where(x => x.HospitalId == s.Hospital.Id).OrderBy(x => x.Code).ToListAsync(ct)).Select(x =>
        {
            costs.TryGetValue(x.Id, out var c);
            var cases = billed.GetValueOrDefault(x.Id);
            var estimate = estimated.GetValueOrDefault(x.Id);
            var actual = procIdByCode.TryGetValue(x.Code, out var procedureId) ? runCost.GetValueOrDefault(procedureId) : null;
            // A package the run did not see billed still carries the standard cost the run stored for it.
            var unit = actual?.UnitCost ?? (c is { CaseCount: > 0 } ? Money.R4(c.ActualCost / c.CaseCount)
                : procIdByCode.TryGetValue(x.Code, out var billedAs) ? std.GetValueOrDefault(billedAs) : 0);
            var total = actual?.TotalCost ?? c?.ActualCost ?? 0;
            if (!hasRun)
            {
                unit = procIdByCode.TryGetValue(x.Code, out var id) && packagePcts.TryGetValue(id, out var pct) ? Money.R4(x.Price * pct / 100m) : modelUnit.GetValueOrDefault(x.Id);
                total = Money.R4(unit * cases);
            }
            return new PackageRow(x.Id, x.Code, x.Name, x.Description, x.Price, composition.GetValueOrDefault(x.Id), x.CoveredLOS, cases, c?.StandardCost ?? 0, c?.ActualCost ?? 0,
                showRevenue ? actual?.Revenue ?? c?.Revenue ?? 0 : 0, c?.Variance ?? 0, c?.VariancePct ?? 0, showRevenue ? c?.OperatingMargin ?? 0 : 0, showRevenue ? c?.MarginPct ?? 0 : 0,
                estimate, cases == 0 ? 0 : Money.R4((c?.ActualCost ?? 0) / cases), types.GetValueOrDefault(x.Code) ?? "", unit, total);
        }).ToArray();
    }

    public const string ProcedureTypeAll = "ALL";
    public const string ProcedureTypeMedicine = "MEDICINE";
    public const string CostingStreamDirect = "DIRECT";
    public const string CostingStreamIndirect = "INDIRECT";
    public const string CostingStreamPackageDirect = "PACKAGE_DIRECT";
    public const string CostingStreamZeroFixed = "ZERO_FIXED";

    public sealed record ProcedureTypeItemsPage(ProcedureCostingItem[] Items, int Total);

    private static void EnsureVersionEditable(ScopedRun s)
    {
        if (s.Version.Status is "PUBLISHED" or "RETIRED")
            throw AppError.Conflict("VERSION_IMMUTABLE", "A published costing version is never edited; create a new version (FR-10.5).");
    }

    public async Task<Hospital> HospitalInScopeAsync(Principal p, string? hospital, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(hospital) ? (await RunScope.HospitalCodesInScope(db, p, ct)).FirstOrDefault() : hospital.Trim();
        if (code is null) throw AppError.NotFound("Hospital");
        if (!p.HasHospital(code)) throw AppError.OutOfScope($"Hospital {code}");
        return await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(h => h.Code == code, ct) ?? throw AppError.NotFound($"Hospital {code}");
    }

    /// <summary>
    /// Direct and Indirect costing belong to a hospital and a costing version, not to a run or a
    /// period. With no version named: the latest open version holding this hospital's costing, else
    /// the published one that does, else the most recent. Contracted prices are read as of today,
    /// kept inside the version's effective dates.
    /// </summary>
    public async Task<ScopedRun> ConfigScopeAsync(Principal p, string? hospital, string? version, CancellationToken ct)
    {
        var h = await HospitalInScopeAsync(p, hospital, ct);
        var versions = await db.CostingVersions.AsNoTracking().Where(x => x.HospitalId == h.Id).ToListAsync(ct);
        CostingVersion? v;
        if (!string.IsNullOrWhiteSpace(version))
        {
            var code = version.Trim();
            v = versions.FirstOrDefault(x => x.Code == code) ?? throw AppError.NotFound($"Costing version {code}");
        }
        else
        {
            var configured = (await db.ProcedureTypeCostings.AsNoTracking().Where(x => x.HospitalId == h.Id).Select(x => x.CostingVersionId).Distinct().ToListAsync(ct))
                .Concat(await db.ProcedureMargins.AsNoTracking().Where(x => x.HospitalId == h.Id).Select(x => x.CostingVersionId).Distinct().ToListAsync(ct))
                .Concat(await db.EncounterOverheads.AsNoTracking().Where(x => x.HospitalId == h.Id).Select(x => x.CostingVersionId).Distinct().ToListAsync(ct))
                .ToHashSet();
            v = versions.Where(x => configured.Contains(x.Id) && x.Status is "DRAFT" or "REVIEW").OrderByDescending(x => x.CreatedAt).FirstOrDefault()
                ?? versions.Where(x => configured.Contains(x.Id)).OrderByDescending(x => x.EffectiveFrom).FirstOrDefault()
                ?? versions.OrderByDescending(x => x.EffectiveFrom).FirstOrDefault()
                ?? throw AppError.NotFound("Costing version");
        }
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        if (v.EffectiveTo is { } to && asOf > to) asOf = to;
        if (asOf < v.EffectiveFrom) asOf = v.EffectiveFrom;
        return new ScopedRun(new CalculationRun { HospitalId = h.Id, CostingVersionId = v.Id }, h, new Period { Code = "", StartDate = asOf, EndDate = asOf }, v);
    }

    /// <summary>
    /// Catalogue screens always show their data; a run only adds the calculated figures. With no
    /// run for the hospital and period, the scope carries an empty run, so results read as nothing.
    /// </summary>
    public async Task<ScopedRun> CatalogueScopeAsync(Principal p, string? run, string? hospital, string? period, CancellationToken ct)
    {
        try
        {
            return await Scope(p, run, hospital, period, ct);
        }
        catch (AppError e) when (e.Code == "NOT_COSTED")
        {
            var h = await HospitalInScopeAsync(p, hospital, ct);
            var periods = await db.Periods.AsNoTracking().Where(x => x.HospitalId == h.Id).ToListAsync(ct);
            var named = periods.FirstOrDefault(x => x.Code == period?.Trim()) ?? periods.OrderByDescending(x => x.EndDate).FirstOrDefault();
            // A new hospital has no costing model yet: its catalogue still opens, with nothing costed.
            if (!await db.CostingVersions.AsNoTracking().AnyAsync(v => v.HospitalId == h.Id, ct))
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                return new ScopedRun(new CalculationRun { HospitalId = h.Id, Status = "" }, h, named ?? new Period { Code = "", StartDate = today, EndDate = today }, new CostingVersion { Code = "", Status = "" });
            }
            var config = await ConfigScopeAsync(p, hospital, null, ct);
            var scope = named ?? config.Period;
            // The model covering the period, even a draft: published first, then the most recent.
            var version = (await db.CostingVersions.AsNoTracking().Where(v => v.HospitalId == h.Id).ToListAsync(ct))
                .Where(v => v.IsEffective(scope.EndDate)).OrderByDescending(v => v.Status == "PUBLISHED").ThenByDescending(v => v.EffectiveFrom).FirstOrDefault() ?? config.Version;
            return new ScopedRun(new CalculationRun { HospitalId = h.Id, PeriodId = scope.Id, CostingVersionId = version.Id, Status = "" }, h, scope, version);
        }
    }

    public sealed record VersionOption(string Code, string Status);
    public sealed record ConfigVersions(string Current, VersionOption[] Options);

    public async Task<ConfigVersions> ConfigVersionsAsync(Principal p, string? hospital, string? version, CancellationToken ct)
    {
        var s = await ConfigScopeAsync(p, hospital, version, ct);
        var options = await db.CostingVersions.AsNoTracking().Where(v => v.HospitalId == s.Hospital.Id).OrderBy(v => v.EffectiveFrom).Select(v => new VersionOption(v.Code, v.Status)).ToArrayAsync(ct);
        return new ConfigVersions(s.Version.Code, options);
    }

    private Task<Dictionary<string, decimal>> TypeMarginMapAsync(Guid hospitalId, Guid versionId, string stream, CancellationToken ct) =>
        db.ProcedureTypeCostings.AsNoTracking()
            .Where(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId && x.Stream == stream)
            .ToDictionaryAsync(x => x.ProcType, x => x.MarginPct, StringComparer.OrdinalIgnoreCase, ct);

    private Task<Dictionary<Guid, decimal>> ItemMarginMapAsync(Guid hospitalId, Guid versionId, string stream, CancellationToken ct) =>
        db.ProcedureMargins.AsNoTracking()
            .Where(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId && x.Stream == stream)
            .ToDictionaryAsync(x => x.ProcedureId, x => x.MarginPct, ct);

    private Task<Dictionary<string, decimal?>> TypeMarginNullableMapAsync(Guid hospitalId, Guid versionId, string stream, CancellationToken ct) =>
        db.ProcedureTypeCostings.AsNoTracking()
            .Where(x => x.HospitalId == hospitalId && x.CostingVersionId == versionId && x.Stream == stream)
            .ToDictionaryAsync(x => x.ProcType, x => stream == CostingStreamZeroFixed ? x.FixedAmount : x.MarginPct, StringComparer.OrdinalIgnoreCase, ct);

    public async Task<ProcedureTypeRow[]> ProcedureTypesAsync(ScopedRun s, Principal p, CancellationToken ct,
        string stream = CostingStreamDirect)
    {
        var procs = await ListedProceduresAsync(s, p, ct, stream);
        var typeMargins = await TypeMarginNullableMapAsync(s.Hospital.Id, s.Version.Id, stream, ct);
        decimal? TypeMargin(string code) => typeMargins.GetValueOrDefault(code);
        var types = procs
            .Where(x => !x.Medicine)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProcType) ? "UNSPECIFIED" : x.ProcType)
            .Select(g => new ProcedureTypeRow(g.Key, TypeDisplayName(g.Key), g.Count(), TypeMargin(g.Key), []))
            .OrderBy(x => x.Name)
            .ToList();
        var medicines = procs.Count(x => x.Medicine);
        ProcedureTypeRow[] medicine = medicines > 0 ? [new ProcedureTypeRow(ProcedureTypeMedicine, "Medicine", medicines, TypeMargin(ProcedureTypeMedicine), [])] : [];
        return
        [
            new ProcedureTypeRow(ProcedureTypeAll, "All", procs.Count, TypeMargin(ProcedureTypeAll), []),
            .. types,
            .. medicine,
        ];
    }

    public async Task<ProcedureTypeRow[]> ProcedureTypeSearchAsync(ScopedRun s, Principal p, string search, int take, CancellationToken ct,
        string stream = CostingStreamDirect)
    {
        var q = (search ?? "").Trim();
        if (q.Length == 0) return await ProcedureTypesAsync(s, p, ct, stream);
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 200);
        var procs = (await ListedProceduresAsync(s, p, ct, stream)).Where(x => MatchesSearch(x, q)).ToList();
        var typeMargins = await TypeMarginNullableMapAsync(s.Hospital.Id, s.Version.Id, stream, ct);
        decimal? TypeMargin(string code) => typeMargins.GetValueOrDefault(code);
        var groups = procs
            .GroupBy(x => x.Medicine ? ProcedureTypeMedicine
                : string.IsNullOrWhiteSpace(x.ProcType) ? "UNSPECIFIED" : x.ProcType)
            .Select(g => new
            {
                Code = g.Key,
                Name = g.Key == ProcedureTypeMedicine ? "Medicine" : TypeDisplayName(g.Key),
                Total = g.Count(),
                Page = g.OrderBy(x => x.Name).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase).Take(take).ToList(),
            })
            .OrderBy(g => g.Name)
            .ToList();
        var ids = groups.SelectMany(g => g.Page.Select(x => x.Id)).ToHashSet();
        var asOf = s.Period.EndDate;
        var itemMargins = await db.ProcedureMargins.AsNoTracking()
            .Where(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id && x.Stream == stream && ids.Contains(x.ProcedureId))
            .ToDictionaryAsync(x => x.ProcedureId, x => stream == CostingStreamZeroFixed ? x.FixedAmount : x.MarginPct, ct);
        var prices = (await db.ProcedureRates.AsNoTracking()
                .Where(r => r.HospitalId == s.Hospital.Id && ids.Contains(r.ProcedureId))
                .ToListAsync(ct))
            .Where(r => r.IsEffective(asOf))
            .GroupBy(r => r.ProcedureId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        Dictionary<Guid, decimal> compositions = stream == CostingStreamPackageDirect ? await CompositionCostsAsync(s, ct) : [];
        return groups.Select(g =>
        {
            var parent = TypeMargin(g.Code);
            return new ProcedureTypeRow(g.Code, g.Name, g.Total, parent, g.Page.Select(x =>
                new ProcedureCostingItem(x.Id, x.Code, x.Name, itemMargins.GetValueOrDefault(x.Id) ?? parent,
                    prices.GetValueOrDefault(x.Id), x.PackageId, x.PackageId is { } pk ? compositions.GetValueOrDefault(pk) : null)).ToArray());
        }).ToArray();
    }

    public async Task<ProcedureTypeItemsPage> ProcedureTypeItemsAsync(ScopedRun s, Principal p, string typeCode, int skip, int take,
        string? search, CancellationToken ct, string stream = CostingStreamDirect)
    {
        typeCode = (typeCode ?? "").Trim();
        if (typeCode.Length == 0 || typeCode.Equals(ProcedureTypeAll, StringComparison.OrdinalIgnoreCase))
            throw AppError.Invalid("Expand a procedure type, not All.");
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 200);
        skip = Math.Max(0, skip);
        var q = (search ?? "").Trim();
        var procs = (await ListedProceduresAsync(s, p, ct, stream))
            .Where(x => MatchesType(x, typeCode) && MatchesSearch(x, q))
            .OrderBy(x => x.Name).ThenBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parent = (await db.ProcedureTypeCostings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id && x.Stream == stream && x.ProcType == typeCode, ct)) is { } parentRow
            ? (stream == CostingStreamZeroFixed ? parentRow.FixedAmount : parentRow.MarginPct) : null;
        var page = procs.Skip(skip).Take(take).ToList();
        var ids = page.Select(x => x.Id).ToHashSet();
        var itemMargins = await db.ProcedureMargins.AsNoTracking()
            .Where(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id && x.Stream == stream && ids.Contains(x.ProcedureId))
            .ToDictionaryAsync(x => x.ProcedureId, x => stream == CostingStreamZeroFixed ? x.FixedAmount : x.MarginPct, ct);
        var asOf = s.Period.EndDate;
        var prices = (await db.ProcedureRates.AsNoTracking()
                .Where(r => r.HospitalId == s.Hospital.Id && ids.Contains(r.ProcedureId))
                .ToListAsync(ct))
            .Where(r => r.IsEffective(asOf))
            .GroupBy(r => r.ProcedureId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        Dictionary<Guid, decimal> compositions = stream == CostingStreamPackageDirect ? await CompositionCostsAsync(s, ct) : [];
        var items = page.Select(x => new ProcedureCostingItem(x.Id, x.Code, x.Name,
            itemMargins.GetValueOrDefault(x.Id) ?? parent, prices.GetValueOrDefault(x.Id), x.PackageId, x.PackageId is { } pk ? compositions.GetValueOrDefault(pk) : null)).ToArray();
        return new ProcedureTypeItemsPage(items, procs.Count);
    }

    public sealed record ProcedureTypeMarginItem(string Code, decimal? MarginPct);
    public sealed record ProcedureMarginItem(Guid Id, decimal? MarginPct);

    public async Task<ProcedureTypeRow[]> SaveProcedureTypeMarginsAsync(ScopedRun s, Principal p,
        IReadOnlyList<ProcedureTypeMarginItem> items, IReadOnlyList<ProcedureMarginItem>? procedures, CancellationToken ct,
        string stream = CostingStreamDirect)
    {
        stream = stream is CostingStreamIndirect or CostingStreamPackageDirect or CostingStreamZeroFixed ? stream : CostingStreamDirect;
        // The zero-price list saves a fixed cost in SAR through the same rows; its % stays 0.
        var isFixed = stream == CostingStreamZeroFixed;
        decimal Round(decimal raw) => isFixed ? Money.R4(raw) : Money.R6(raw);
        void CheckValue(decimal value)
        {
            if (isFixed ? value < 0 : value < 0 || value > 999.999999m)
                throw AppError.Invalid(isFixed ? "Fixed Cost (SAR) cannot be negative." : "Margin Cost (%) must be between 0 and 999.999999.");
        }
        EnsureVersionEditable(s);
        var types = await ProcedureTypesAsync(s, p, ct, stream);
        var allowed = types.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0) throw AppError.Invalid("Nothing to save.");
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Code) || !allowed.Contains(item.Code.Trim()))
                throw AppError.Invalid($"Unknown procedure type '{item.Code}'.");
            if (item.MarginPct is { } checkedValue) CheckValue(checkedValue);
        }

        var existing = await db.ProcedureTypeCostings.Where(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id && x.Stream == stream).ToListAsync(ct);
        var byType = existing.ToDictionary(x => x.ProcType, StringComparer.OrdinalIgnoreCase);
        var typeValue = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var code = item.Code.Trim();
            typeValue[code] = item.MarginPct is { } raw && raw != 0 ? Round(raw) : null;
            if (item.MarginPct is not { } pct || pct == 0)
            {
                if (byType.Remove(code, out var gone)) db.ProcedureTypeCostings.Remove(gone);
                continue;
            }
            var value = Round(pct);
            if (byType.TryGetValue(code, out var row))
            {
                if (isFixed) row.FixedAmount = value; else row.MarginPct = value;
            }
            else
            {
                var added = new ProcedureTypeCosting
                {
                    Id = IdGen.Sequential(), HospitalId = s.Hospital.Id, CostingVersionId = s.Version.Id, ProcType = code, Stream = stream, MarginPct = isFixed ? 0m : value, FixedAmount = isFixed ? value : null,
                };
                db.ProcedureTypeCostings.Add(added);
                byType[code] = added;
            }
        }

        if (procedures is { Count: > 0 })
        {
            var procs = (await ListedProceduresAsync(s, p, ct, stream)).ToDictionary(x => x.Id);
            var existingItems = await db.ProcedureMargins.Where(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id && x.Stream == stream).ToListAsync(ct);
            var byProc = existingItems.ToDictionary(x => x.ProcedureId);
            foreach (var item in procedures)
            {
                if (!procs.TryGetValue(item.Id, out var hit))
                    throw AppError.Invalid("Unknown procedure in margin list.");
                if (item.MarginPct is { } checkedItem) CheckValue(checkedItem);
                var typeCode = hit.Medicine ? ProcedureTypeMedicine
                    : string.IsNullOrWhiteSpace(hit.ProcType) ? "UNSPECIFIED" : hit.ProcType;
                var parent = typeValue.GetValueOrDefault(typeCode) ?? types.FirstOrDefault(t => t.Code == typeCode)?.MarginPct;
                var value = item.MarginPct is { } raw && raw != 0 ? Round(raw) : (decimal?)null;
                var inherits = value is null || parent is decimal parentVal && value == parentVal;
                if (inherits)
                {
                    if (byProc.Remove(item.Id, out var gone)) db.ProcedureMargins.Remove(gone);
                    continue;
                }
                if (byProc.TryGetValue(item.Id, out var row))
                {
                    if (isFixed) row.FixedAmount = value; else row.MarginPct = value!.Value;
                }
                else
                {
                    var added = new ProcedureMargin
                    {
                        Id = IdGen.Sequential(), HospitalId = s.Hospital.Id, CostingVersionId = s.Version.Id, ProcedureId = item.Id, Stream = stream, MarginPct = isFixed ? 0m : value!.Value, FixedAmount = isFixed ? value : null,
                    };
                    db.ProcedureMargins.Add(added);
                    byProc[item.Id] = added;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "UPDATE", "ProcedureTypeCosting", s.Hospital.Id.ToString(), s.Hospital.Code,
            metadata: new { stream, items = items.Select(x => new { x.Code, x.MarginPct }), procedureCount = procedures?.Count }, ct: ct);
        return await ProcedureTypesAsync(s, p, ct, stream);
    }

    public sealed record EncounterOverheadRow(Guid Id, Guid CostCenterId, string CostCenterCode, string CostCenterName, string Description,
        decimal? OverheadPct, decimal? OverheadAmount);
    public sealed record CostCentrePick(Guid Id, string Code, string Name);
    public sealed record EncounterOverheadPage(EncounterOverheadRow[] Items, CostCentrePick[] CostCentres);
    public sealed record EncounterOverheadBody(Guid CostCenterId, string? Description, decimal? OverheadPct, decimal? OverheadAmount);

    public async Task<EncounterOverheadPage> EncounterOverheadsAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var centres = (await db.CostCenters.AsNoTracking().Where(c => c.HospitalId == s.Hospital.Id).OrderBy(c => c.Code).ToListAsync(ct))
            .Where(c => DeptAllowed(p, deptCodes, c.DepartmentId))
            .ToList();
        var picks = centres.Select(c => new CostCentrePick(c.Id, c.Code, c.Name)).ToArray();
        var allowed = centres.Select(c => c.Id).ToHashSet();
        var names = centres.ToDictionary(c => c.Id);
        var rows = await db.EncounterOverheads.AsNoTracking()
            .Where(x => x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id)
            .ToListAsync(ct);
        var items = rows
            .Where(x => allowed.Contains(x.CostCenterId) && names.ContainsKey(x.CostCenterId))
            .OrderBy(x => names[x.CostCenterId].Code).ThenBy(x => x.Description)
            .Select(x => ToEncounterOverheadRow(x, names[x.CostCenterId]))
            .ToArray();
        return new EncounterOverheadPage(items, picks);
    }

    public async Task<EncounterOverheadPage> SaveEncounterOverheadAsync(ScopedRun s, Principal p, Guid? id,
        EncounterOverheadBody body, CancellationToken ct)
    {
        EnsureVersionEditable(s);
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == body.CostCenterId && c.HospitalId == s.Hospital.Id, ct)
            ?? throw AppError.NotFound("Cost centre");
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        if (!DeptAllowed(p, deptCodes, cc.DepartmentId)) throw AppError.Forbidden("masterdata.view");
        var pct = NormalizePct(body.OverheadPct, "Overhead (%)");
        var amount = NormalizeAmount(body.OverheadAmount);
        var description = (body.Description ?? "").Trim();

        EncounterOverhead row;
        if (id is { } existingId)
        {
            row = await db.EncounterOverheads.FirstOrDefaultAsync(x => x.Id == existingId && x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id, ct)
                ?? throw AppError.NotFound("Encounter overhead");
            row.CostCenterId = cc.Id;
            row.Description = description;
            row.OverheadPct = pct;
            row.OverheadAmount = amount;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(p, "UPDATE", "EncounterOverhead", row.Id.ToString(), cc.Code,
                after: new { cc.Code, description, pct, amount }, ct: ct);
        }
        else
        {
            row = new EncounterOverhead
            {
                Id = IdGen.Sequential(), HospitalId = s.Hospital.Id, CostingVersionId = s.Version.Id, CostCenterId = cc.Id,
                Description = description, OverheadPct = pct, OverheadAmount = amount,
            };
            db.EncounterOverheads.Add(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(p, "CREATE", "EncounterOverhead", row.Id.ToString(), cc.Code,
                after: new { cc.Code, description, pct, amount }, ct: ct);
        }
        return await EncounterOverheadsAsync(s, p, ct);
    }

    public async Task<EncounterOverheadPage> DeleteEncounterOverheadAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        EnsureVersionEditable(s);
        var row = await db.EncounterOverheads.FirstOrDefaultAsync(x => x.Id == id && x.HospitalId == s.Hospital.Id && x.CostingVersionId == s.Version.Id, ct)
            ?? throw AppError.NotFound("Encounter overhead");
        db.EncounterOverheads.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(p, "DELETE", "EncounterOverhead", row.Id.ToString(), row.CostCenterId.ToString(), ct: ct);
        return await EncounterOverheadsAsync(s, p, ct);
    }

    private static EncounterOverheadRow ToEncounterOverheadRow(EncounterOverhead x, CostCenter cc) =>
        new(x.Id, x.CostCenterId, cc.Code, cc.Name, x.Description,
            x.OverheadPct == 0 ? null : x.OverheadPct, x.OverheadAmount == 0 ? null : x.OverheadAmount);

    private static decimal NormalizePct(decimal? raw, string label)
    {
        if (raw is null || raw == 0) return 0;
        if (raw < 0 || raw > 999.999999m) throw AppError.Invalid($"{label} must be between 0 and 999.999999.");
        return Money.R6(raw.Value);
    }

    private static decimal NormalizeAmount(decimal? raw)
    {
        if (raw is null || raw == 0) return 0;
        if (raw < 0) throw AppError.Invalid("Max Cap (Fixed) cannot be negative.");
        return Money.R4(raw.Value);
    }

    /// <summary>Procedure Costing lists priced procedures, and its zero-price list the rest; other streams list everything.</summary>
    private async Task<List<CostingProc>> ListedProceduresAsync(ScopedRun s, Principal p, CancellationToken ct, string stream)
    {
        var procs = await CostingProceduresAsync(s, p, ct, stream);
        if (stream is not (CostingStreamDirect or CostingStreamZeroFixed)) return procs;
        var priced = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId)
            .Where(g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice != 0m)
            .Select(g => g.Key).ToHashSet();
        return procs.Where(x => priced.Contains(x.Id) == (stream == CostingStreamDirect)).ToList();
    }

    private sealed record CostingProc(Guid Id, string Code, string Name, string CatalogCode, string ProcType, bool Medicine, Guid? PackageId = null);

    /// <summary>The priced rows a costing stream lists: a package is billed as a procedure of the same code, so package costing lists those rows and the others leave them out.</summary>
    private async Task<List<CostingProc>> CostingProceduresAsync(ScopedRun s, Principal p, CancellationToken ct, string stream = CostingStreamDirect)
    {
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var deptCodes = depts.ToDictionary(d => d.Key, d => d.Value.CatalogCode);
        var packages = await db.Packages.AsNoTracking()
            .Where(x => x.HospitalId == s.Hospital.Id)
            .ToDictionaryAsync(x => x.Code, x => x.Id, ct);
        var wantPackages = stream == CostingStreamPackageDirect;
        return (await db.Procedures.AsNoTracking()
                .Where(x => x.HospitalId == s.Hospital.Id)
                .Select(x => new { x.Id, x.Code, x.Name, x.CatalogCode, x.ProcType, x.Category, x.DepartmentId })
                .ToListAsync(ct))
            .Where(x => DeptAllowed(p, deptCodes, x.DepartmentId) && packages.ContainsKey(x.Code) == wantPackages)
            .Select(x => new CostingProc(x.Id, x.Code, x.Name, x.CatalogCode, x.ProcType,
                string.Equals(x.Category, "MEDICINE", StringComparison.OrdinalIgnoreCase), packages.TryGetValue(x.Code, out var package) ? package : null))
            .ToList();
    }

    private static bool MatchesType(CostingProc x, string typeCode)
    {
        if (typeCode.Equals(ProcedureTypeMedicine, StringComparison.OrdinalIgnoreCase)) return x.Medicine;
        if (x.Medicine) return false;
        var code = string.IsNullOrWhiteSpace(x.ProcType) ? "UNSPECIFIED" : x.ProcType;
        return code.Equals(typeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearch(CostingProc x, string q)
    {
        if (q.Length == 0) return true;
        if (x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (x.Code.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (x.CatalogCode.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        var i = x.Code.IndexOf('.');
        return i >= 0 && x.Code[(i + 1)..].Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static string TypeDisplayName(string code) =>
        string.Join(' ', code.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    /// <summary>The item's own %, else its type's, else All's. Zero counts as not set.</summary>
    private static decimal? MarginOf(Guid id, string category, string procType,
        IReadOnlyDictionary<Guid, decimal> itemMargins, IReadOnlyDictionary<string, decimal> typeMargins)
    {
        decimal? margin = itemMargins.TryGetValue(id, out var item) ? item
            : typeMargins.TryGetValue(
                string.Equals(category, "MEDICINE", StringComparison.OrdinalIgnoreCase) ? ProcedureTypeMedicine
                : string.IsNullOrWhiteSpace(procType) ? "UNSPECIFIED" : procType, out var typePct) ? typePct
            : typeMargins.TryGetValue(ProcedureTypeAll, out var all) ? all : null;
        return margin is 0m ? null : margin;
    }

    private static decimal ProcedureUnitCost(Guid id, string category, string procType,
        IReadOnlyDictionary<Guid, decimal> prices, IReadOnlyDictionary<Guid, decimal> recipe,
        IReadOnlyDictionary<Guid, decimal> itemMargins, IReadOnlyDictionary<string, decimal> typeMargins) =>
        MarginOf(id, category, procType, itemMargins, typeMargins) is { } pct ? Money.R4(prices.GetValueOrDefault(id) * pct / 100m) : recipe.GetValueOrDefault(id);

    /// <summary>Each package's composition cost under the scope's costing model: the sum of its procedures' Direct cost.</summary>
    private async Task<Dictionary<Guid, decimal>> CompositionCostsAsync(ScopedRun s, CancellationToken ct)
    {
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().ListPrice);
        var std = await db.ProcedureCosts.AsNoTracking()
            .Where(c => c.RunId == s.Run.Id && c.CostType == "STANDARD")
            .ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        var typeMargins = await TypeMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        var itemMargins = await ItemMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        var lines = await (from c in db.PackageComponents.AsNoTracking()
                           join pkg in db.Packages.AsNoTracking() on c.PackageId equals pkg.Id
                           join pr in db.Procedures.AsNoTracking() on c.ProcedureId equals pr.Id
                           where pkg.HospitalId == s.Hospital.Id
                           select new { c.PackageId, c.Quantity, pr.Id, pr.Category, pr.ProcType }).ToListAsync(ct);
        return lines.GroupBy(x => x.PackageId).ToDictionary(g => g.Key, g => Money.R4(g.Sum(x =>
            x.Quantity * ProcedureUnitCost(x.Id, x.Category, x.ProcType, prices, std, itemMargins, typeMargins))));
    }

    /// <summary>Package Costing % by the procedure a package is billed as; a package with no % set is left out.</summary>
    private async Task<Dictionary<Guid, decimal>> PackagePctsAsync(ScopedRun s, CancellationToken ct)
    {
        var typeMargins = await TypeMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamPackageDirect, ct);
        var itemMargins = await ItemMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamPackageDirect, ct);
        if (typeMargins.Count == 0 && itemMargins.Count == 0) return [];
        var rows = await (from pr in db.Procedures.AsNoTracking()
                          join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                          where pr.HospitalId == s.Hospital.Id
                          select new { pr.Id, pr.Category, pr.ProcType }).ToListAsync(ct);
        var pcts = new Dictionary<Guid, decimal>();
        foreach (var x in rows)
            if (MarginOf(x.Id, x.Category, x.ProcType, itemMargins, typeMargins) is { } pct) pcts[x.Id] = pct;
        return pcts;
    }

    public async Task<PackageDetail> PackageAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await PackagesAsync(s, p, ct)).FirstOrDefault(x => x.Id == id) ?? throw AppError.NotFound("Package");
        var std = await db.ProcedureCosts.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.CostType == "STANDARD").ToDictionaryAsync(c => c.ProcedureId, c => c.UnitCost, ct);
        var prices = (await db.ProcedureRates.AsNoTracking().Where(r => r.HospitalId == s.Hospital.Id).ToListAsync(ct))
            .Where(r => r.IsEffective(s.Period.EndDate))
            .GroupBy(r => r.ProcedureId).ToDictionary(g => g.Key, g => g.First().ListPrice);
        var hasRun = s.Run.Id != Guid.Empty;
        var typeMargins = hasRun ? [] : await TypeMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        var itemMargins = hasRun ? [] : await ItemMarginMapAsync(s.Hospital.Id, s.Version.Id, CostingStreamDirect, ct);
        var comps = (await (from c in db.PackageComponents.AsNoTracking()
                            join pr in db.Procedures on c.ProcedureId equals pr.Id
                            where c.PackageId == id
                            select new { pr.Id, pr.Name, pr.Code, pr.ProcType, pr.Category, c.Quantity }).ToListAsync(ct))
            .Select(x =>
            {
                var unit = hasRun ? std.GetValueOrDefault(x.Id) : ProcedureUnitCost(x.Id, x.Category, x.ProcType, prices, std, itemMargins, typeMargins);
                return new PackageComponentDto(x.Id, x.Name, x.Quantity, std.GetValueOrDefault(x.Id), Money.R4(std.GetValueOrDefault(x.Id) * x.Quantity), x.Code, x.Category, x.ProcType, prices.GetValueOrDefault(x.Id),
                    string.Equals(x.Category, "MEDICINE", StringComparison.OrdinalIgnoreCase), Money.R4(unit * x.Quantity));
            }).ToArray();
        var encPage = await EncountersAsync(s, p, new EncounterQuery { BilledPackageId = id, PageSize = 1 }, ct, auditPhi: false);
        var perCase = row.Cases == 0 ? 0 : (row.ActualCost - row.StandardCost) / row.Cases;
        var explanation = row.Cases == 0 ? "No cases this period."
            : Math.Abs(perCase) < 1 ? "Actual cost is in line with the standard."
            : $"Each case ran SAR {Math.Abs(perCase):N0} {(perCase > 0 ? "above" : "below")} standard across {row.Cases} cases" +
              (p.Can("revenue.view") ? $"; the contracted price of SAR {row.Price:N0} {(row.ActualPerCase > row.Price ? "no longer covers" : "still covers")} the average case." : ".");
        return new PackageDetail(row, comps, [], explanation, encPage.Total, await AverageEncounterFeeAsync(s, id, encPage.Total, ct));
    }

    /// <summary>Billed net of this package procedure, divided by encounters in the run that invoiced it.</summary>
    private async Task<decimal?> AverageEncounterFeeAsync(ScopedRun s, Guid packageId, int encounterCount, CancellationToken ct)
    {
        if (encounterCount <= 0) return null;
        var billedEncounters = from es in CountedServices(s)
                               join pr in db.Procedures.AsNoTracking() on es.ProcedureId equals pr.Id
                               join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                               join ec in db.EncounterCosts.AsNoTracking() on es.EncounterId equals ec.EncounterId
                               where pkg.Id == packageId && ec.RunId == s.Run.Id
                               select es.EncounterId;
        var net = await (from rt in CountedRevenue(s)
                         join pr in db.Procedures.AsNoTracking() on rt.ProcedureId equals pr.Id
                         join pkg in db.Packages.AsNoTracking() on new { pr.HospitalId, pr.Code } equals new { pkg.HospitalId, pkg.Code }
                         where pkg.Id == packageId && billedEncounters.Contains(rt.EncounterId)
                         select rt.NetAmount - (rt.RefundedAmount ?? 0m)).SumAsync(ct);
        return Money.R4(net / encounterCount);
    }

    public sealed record DrgRow(Guid Id, string Code, string Name, string Mdc, decimal RelativeWeight, decimal ExpectedLOS, int Cases, decimal Mean, decimal Median,
        decimal P25, decimal P75, decimal P90, decimal Min, decimal Max, decimal StdDev, decimal Cv, decimal AvgLOS, decimal CostPerDay, decimal AvgReimbursement,
        decimal AvgMargin, decimal MarginPct, int Outliers);
    public sealed record CompositionDiff(string ComponentType, string Label, decimal OutlierAverage, decimal OthersAverage, decimal Difference, decimal ShareOfGap);
    public sealed record DrgDetail(DrgRow Drg, decimal[] Costs, decimal OutlierFence, CompositionDiff[] WhyExpensive, EncounterRow[] Outliers, string Explanation);

    public async Task<DrgRow[]> DrgsAsync(ScopedRun s, Principal p, CancellationToken ct)
    {
        var used = await db.Encounters.AsNoTracking()
            .Where(e => e.HospitalId == s.Hospital.Id && e.DrgId != null)
            .Select(e => e.DrgId!.Value).Distinct().ToListAsync(ct);
        if (used.Count == 0) return [];
        var sums = await db.DrgCostSummaries.AsNoTracking().Where(d => d.RunId == s.Run.Id).ToDictionaryAsync(d => d.DrgId, ct);
        var showRevenue = p.Can("revenue.view");
        return (await db.Drgs.AsNoTracking().Where(d => used.Contains(d.Id)).OrderBy(d => d.Code).ToListAsync(ct)).Select(d =>
        {
            sums.TryGetValue(d.Id, out var x);
            return new DrgRow(d.Id, d.Code, d.Name, d.Mdc, d.RelativeWt, d.ExpectedLOS, x?.CaseCount ?? 0, x?.AvgCost ?? 0, x?.MedianCost ?? 0, x?.P25Cost ?? 0,
                x?.P75Cost ?? 0, x?.P90Cost ?? 0, x?.MinCost ?? 0, x?.MaxCost ?? 0, x?.StdDevCost ?? 0, x?.VariationCoeff ?? 0, x?.AvgLOS ?? 0, x?.CostPerDay ?? 0,
                showRevenue ? x?.AvgReimbursement ?? 0 : 0, showRevenue ? x?.AvgMargin ?? 0 : 0, showRevenue ? x?.MarginPct ?? 0 : 0, x?.OutlierCount ?? 0);
        }).ToArray();
    }

    public async Task<DrgDetail> DrgAsync(ScopedRun s, Principal p, Guid id, CancellationToken ct)
    {
        var row = (await DrgsAsync(s, p, ct)).FirstOrDefault(d => d.Id == id) ?? throw AppError.NotFound("DRG");
        var encs = (await EncountersAsync(s, p, new EncounterQuery { DrgId = id, PageSize = 2000, Sort = "total" }, ct)).Rows;
        var fence = Money.R4(row.P75 + 1.5m * (row.P75 - row.P25));
        var outlierIds = encs.Where(e => e.TotalCost > fence).Select(e => e.Id).ToHashSet();
        // FR-5.14: why the expensive cases are expensive — average composition, outliers vs the rest.
        var comps = await db.CostComponents.AsNoTracking().Where(c => c.RunId == s.Run.Id && c.Scope == "ENCOUNTER" && c.EncounterId != null)
            .Join(db.Encounters.Where(e => e.DrgId == id), c => c.EncounterId, e => e.Id, (c, e) => new { e.Id, c.ComponentType, c.Amount }).ToListAsync(ct);
        var nOut = Math.Max(outlierIds.Count, 1);
        var nRest = Math.Max(encs.Length - outlierIds.Count, 1);
        var diff = comps.GroupBy(c => c.ComponentType).Select(g =>
        {
            var o = g.Where(x => outlierIds.Contains(x.Id)).Sum(x => x.Amount) / nOut;
            var r = g.Where(x => !outlierIds.Contains(x.Id)).Sum(x => x.Amount) / nRest;
            return (Type: g.Key, O: Money.R4(o), R: Money.R4(r), D: Money.R4(o - r));
        }).OrderByDescending(x => x.D).ToList();
        var gap = diff.Where(d => d.D > 0).Sum(d => d.D);
        var whys = diff.Select(d => new CompositionDiff(d.Type, Catalogue.ComponentLabel(d.Type), d.O, d.R, d.D, gap == 0 ? 0 : Share(Math.Max(d.D, 0), gap))).ToArray();
        var top = whys.Where(w => w.Difference > 0).Take(2).ToList();
        var explanation = outlierIds.Count == 0
            ? "No case sits above the Tukey fence (P75 + 1.5 x IQR) this period."
            : $"{outlierIds.Count} high-cost {Fmt(outlierIds.Count)} above SAR {fence:N0}. They cost SAR {gap:N0} more on average than the rest, " +
              $"mostly {string.Join(" and ", top.Select(t => $"{t.Label.ToLowerInvariant()} (SAR {t.Difference:N0}, {t.ShareOfGap:N0}% of the gap)"))}.";
        return new DrgDetail(row, encs.Select(e => e.TotalCost).OrderBy(x => x).ToArray(), fence, whys,
            encs.Where(e => outlierIds.Contains(e.Id)).OrderByDescending(e => e.TotalCost).ToArray(), explanation);
        static string Fmt(int n) => n == 1 ? "case sits" : "cases sit";
    }
}

