using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;

namespace Verita.Infrastructure.Queries;

public sealed partial class ReadQueries
{
    public static readonly (string Key, string Title, string Audience)[] Reports =
    [
        ("cost-statement", "Hospital cost statement", "Finance"),
        ("service-line", "Service-line profitability", "Executive"),
        ("physician-variation", "Physician practice variation", "Clinical operations"),
        ("payer-margin", "Payer margin", "Revenue cycle"),
        ("supply-waste", "Supply cost and waste", "Supply chain"),
        ("group-comparison", "Group comparison", "Executive"),
    ];

    public sealed record PhysicianRow(Guid? ProviderId, string Physician, string Specialty, int Encounters, decimal AvgCost, decimal AvgLOS,
        decimal AvgStandard, decimal CostVsStandardPct, decimal SpecialtyAvgCost, decimal VsSpecialtyPct);
    public sealed record GroupRow(string Hospital, string Name, string Run, string RunStatus, decimal Gl, decimal PatientCost, decimal AttributionPct,
        int Encounters, decimal CostPerEncounter, decimal Revenue, decimal OperatingMargin, decimal IdleCapacity);
    public sealed record ReportDto(string Key, string Title, string Audience, string Summary, object Body);

    /// <summary>FR-11.6: standing reports, each built from the same run-scoped read models.</summary>
    public async Task<object> ReportAsync(ScopedRun s, Principal p, string report, CancellationToken ct)
    {
        var def = Reports.FirstOrDefault(r => r.Key == report);
        if (def.Key is null) throw AppError.Invalid($"Unknown report '{report}'.", new { reports = Reports.Select(r => r.Key) });
        switch (report)
        {
            case "cost-statement":
            {
                var recon = await ReconciliationAsync(s, ct);
                var dash = await DashboardAsync(s, p, ct);
                return new ReportDto(def.Key, def.Title, def.Audience, recon.Narrative, new { recon.ByNaturalClass, recon.ByCostCentre, recon.Lines, costNature = dash.CostNature });
            }
            case "service-line":
            {
                var prof = await ProfitabilityAsync(s, p, "serviceLine", ct);
                var best = prof.Rows.FirstOrDefault();
                var worst = prof.Rows.LastOrDefault();
                return new ReportDto(def.Key, def.Title, def.Audience,
                    best is null ? "No encounters were costed." : p.Can("revenue.view")
                        ? $"{best.Label} contributes the most operating margin (SAR {best.Operating:N0}); {worst!.Label} the least (SAR {worst.Operating:N0}). Full-cost margin after idle capacity is SAR {prof.FullCostMargin:N0}."
                        : "Revenue is masked for your role, so service lines are ranked by cost.",
                    prof);
            }
            case "physician-variation":
            {
                var facts = await EncounterFacts(s.Run.Id, ct);
                var providers = await db.Providers.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
                var los = await db.Encounters.AsNoTracking().Where(e => e.HospitalId == s.Hospital.Id).ToDictionaryAsync(e => e.Id, e => e.LengthOfStay, ct);
                var rows = facts.Where(f => f.ProviderId is not null).GroupBy(f => f.ProviderId!.Value).Select(g =>
                {
                    var pr = providers[g.Key];
                    var avg = g.Average(x => x.Total);
                    var std = g.Average(x => x.Standard);
                    return (pr, n: g.Count(), avg, std, los: (decimal)g.Average(x => los.GetValueOrDefault(x.Id)));
                }).ToList();
                var bySpec = rows.GroupBy(r => r.pr.Specialty).ToDictionary(g => g.Key, g => g.Sum(r => r.avg * r.n) / Math.Max(1, g.Sum(r => r.n)));
                var result = rows.Select(r => new PhysicianRow(r.pr.Id, r.pr.Name, r.pr.Specialty, r.n, Money.R4(r.avg), Money.R4(r.los), Money.R4(r.std),
                        r.std == 0 ? 0 : Money.R4((r.avg - r.std) / r.std * 100m), Money.R4(bySpec[r.pr.Specialty]),
                        bySpec[r.pr.Specialty] == 0 ? 0 : Money.R4((r.avg - bySpec[r.pr.Specialty]) / bySpec[r.pr.Specialty] * 100m)))
                    .OrderBy(r => r.Specialty).ThenByDescending(r => r.VsSpecialtyPct).ToArray();
                var outlier = result.Where(r => r.Encounters >= 5).OrderByDescending(r => r.VsSpecialtyPct).FirstOrDefault();
                return new ReportDto(def.Key, def.Title, def.Audience,
                    outlier is null ? "Too few encounters per physician to compare practice."
                        : $"{outlier.Physician} runs {outlier.VsSpecialtyPct:N1}% above the {outlier.Specialty} average cost per encounter across {outlier.Encounters} encounters. Variation is shown against the specialty, not the hospital, so case mix is roughly like for like.",
                    result);
            }
            case "payer-margin":
            {
                var prof = await ProfitabilityAsync(s, p, "payer", ct);
                var loss = prof.Rows.Where(r => r.Operating < 0).Select(r => r.Label).ToList();
                return new ReportDto(def.Key, def.Title, def.Audience,
                    !p.Can("revenue.view") ? "Revenue is masked for your role."
                        : loss.Count == 0 ? "Every payer covers its full cost this period." : $"{string.Join(", ", loss)} {(loss.Count == 1 ? "does" : "do")} not cover the cost of the care delivered.",
                    prof);
            }
            case "supply-waste":
            {
                var cap = await CapacityAsync(s, p, ct);
                var supply = cap.Rows.Where(r => r.ResourceType is "DRUG" or "CONSUMABLE" or "IMPLANT" or "BLOOD").OrderByDescending(r => r.CostOfUnused).ToArray();
                var used = supply.Sum(r => r.CostOfUsed);
                var waste = supply.Sum(r => r.CostOfUnused);
                return new ReportDto(def.Key, def.Title, def.Audience,
                    $"SAR {used:N0} of supply reached patients and SAR {waste:N0} ({Share(waste, used + waste):N1}% of purchased value) did not — wastage, expiry and stock movement.",
                    supply);
            }
            default:
            {
                var codes = await RunScope.HospitalCodesInScope(db, p, ct);
                var runs = await (from r in db.CalculationRuns.AsNoTracking()
                                  join h in db.Hospitals on r.HospitalId equals h.Id
                                  where r.PeriodId == s.Period.Id && r.RunType != "SIMULATION"
                                  select new { r, h.Code, h.Name }).ToListAsync(ct);
                var rows = new List<GroupRow>();
                foreach (var g in runs.Where(x => codes.Contains(x.Code)).GroupBy(x => x.Code))
                {
                    var pick = g.OrderByDescending(x => x.r.Status == "PUBLISHED").ThenByDescending(x => x.r.CreatedAt).First();
                    var rec = await db.Reconciliations.AsNoTracking().FirstOrDefaultAsync(x => x.RunId == pick.r.Id, ct);
                    var enc = await db.EncounterCosts.AsNoTracking().Where(x => x.RunId == pick.r.Id)
                        .GroupBy(_ => 1).Select(x => new { N = x.Count(), C = x.Sum(e => e.TotalCost), R = x.Sum(e => e.Revenue) }).FirstOrDefaultAsync(ct);
                    var idle = await db.ResidualBuckets.AsNoTracking().Where(b => b.RunId == pick.r.Id && b.Bucket == "UNUSED_CAPACITY").SumAsync(b => (decimal?)b.Amount, ct) ?? 0;
                    var showRevenue = p.Can("revenue.view");
                    rows.Add(new GroupRow(g.Key, pick.Name, pick.r.Code, pick.r.Status, rec?.GlTotal ?? 0, enc?.C ?? 0, rec?.AttributionPct ?? 0, enc?.N ?? 0,
                        enc is null || enc.N == 0 ? 0 : Money.R4(enc.C / enc.N), showRevenue ? enc?.R ?? 0 : 0, showRevenue ? (enc?.R ?? 0) - (enc?.C ?? 0) : 0, idle));
                }
                return new ReportDto(def.Key, def.Title, def.Audience,
                    rows.Count < 2 ? "Only one hospital is in your scope for this period."
                        : $"Attribution ranges from {rows.Min(r => r.AttributionPct):N1}% to {rows.Max(r => r.AttributionPct):N1}% — the comparable measure across hospitals, because unlike cost per encounter it does not depend on case mix (FR-7.8).",
                    rows.OrderBy(r => r.Hospital).ToArray());
            }
        }
    }
}
