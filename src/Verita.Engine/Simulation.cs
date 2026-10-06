using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine;

public sealed record LeverInput(string LeverType, string TargetType, string? TargetId, string TargetLabel, string ChangeType, decimal Value);

public sealed record SimResource(Guid Id, string ResourceType);

/// <summary>The figures a scenario comparison needs from one run (FR-8.5).</summary>
public sealed record RunSnapshot(
    decimal Cost, decimal Revenue, decimal Direct, decimal Indirect, decimal Overhead, int Encounters, decimal Idle,
    IReadOnlyDictionary<Guid, (string Name, decimal TotalCost, decimal Revenue, decimal Volume, decimal UnitCost)> Procedures);

/// <summary>
/// The what-if engine (FR-8). A scenario is executed by the same eleven-stage pipeline as a real
/// run, against a shadow run carrying the levers as overrides; it cannot diverge from reality
/// because it is reality with different inputs (FR-8.1). This class only turns levers into
/// overrides and writes the comparison; the run itself is the ordinary pipeline.
/// </summary>
public static class Simulation
{
    public static decimal Multiplier(string changeType, decimal value) => changeType switch
    {
        "PCT" => 1m + value / 100m,
        "SET" => value,
        _ => 1m + value,
    };

    public static ScenarioOverrides BuildOverrides(
        IReadOnlyList<LeverInput> levers, IReadOnlyList<SimResource> resources, IReadOnlyList<Guid> activityIds,
        IReadOnlyList<Guid> procedureIds, IReadOnlyList<Guid> payerIds)
    {
        var o = new ScenarioOverrides();
        IEnumerable<Guid> OfTypes(params string[] types) => resources.Where(r => types.Contains(r.ResourceType)).Select(r => r.Id);
        static void Mul(Dictionary<Guid, decimal> map, IEnumerable<Guid> ids, decimal m)
        {
            foreach (var id in ids) map[id] = (map.TryGetValue(id, out var v) ? v : 1m) * m;
        }
        Guid? TargetGuid(LeverInput l) => Guid.TryParse(l.TargetId, out var g) ? g : null;

        foreach (var l in levers)
        {
            var m = Multiplier(l.ChangeType, l.Value);
            switch (l.LeverType)
            {
                case "RESOURCE_RATE":
                case "EQUIPMENT_COST":
                    Mul(o.ResourceRate,
                        l.TargetType == "RESOURCE" && TargetGuid(l) is { } rid ? [rid]
                        : l.TargetType == "RESOURCE_TYPE" && l.TargetId is { } t ? OfTypes(t)
                        : l.LeverType == "EQUIPMENT_COST" ? OfTypes("EQUIPMENT", "ROOM")
                        : resources.Select(r => r.Id), m);
                    break;
                case "DRUG_PRICE":
                    Mul(o.ResourceRate, OfTypes("DRUG"), m);
                    break;
                case "CONSUMABLE_PRICE":
                    Mul(o.ResourceRate, OfTypes("CONSUMABLE", "IMPLANT", "BLOOD"), m);
                    break;
                case "RESOURCE_CAPACITY":
                case "OCCUPANCY":
                    Mul(o.ResourceCapacity,
                        l.TargetType == "RESOURCE" && TargetGuid(l) is { } cid ? [cid]
                        : l.TargetType == "RESOURCE_TYPE" && l.TargetId is { } ct ? OfTypes(ct)
                        : l.LeverType == "OCCUPANCY" ? OfTypes("ROOM")
                        : resources.Select(r => r.Id), m);
                    break;
                case "STAFF_UTILISATION":
                    // The same work fits in less paid capacity: capacity falls, consumption does not rise.
                    Mul(o.ResourceCapacity, OfTypes("LABOR_PHYSICIAN", "LABOR_NURSE", "LABOR_TECH", "LABOR_ALLIED"), 1m / m);
                    break;
                case "ACTIVITY_DURATION":
                case "DRIVER_VOLUME":
                    Mul(o.ActivityDuration, l.TargetType == "ACTIVITY" && TargetGuid(l) is { } aid ? [aid] : activityIds, m);
                    break;
                case "PROCEDURE_VOLUME":
                    Mul(o.ProcedureVolume, l.TargetType == "PROCEDURE" && TargetGuid(l) is { } pid ? [pid] : procedureIds, m);
                    break;
                case "LOS":
                    o.LosFactor *= m;
                    break;
                case "PATIENT_VOLUME":
                    o.PatientVolume *= m;
                    break;
                case "REIMBURSEMENT":
                    Mul(o.Reimbursement, l.TargetType == "PAYER" && TargetGuid(l) is { } py ? [py] : payerIds, m);
                    break;
                case "OVERHEAD_RATE":
                    o.OverheadRate *= m;
                    break;
                default:
                    throw new ArgumentException($"Unknown lever type {l.LeverType}.");
            }
        }
        return o;
    }

    public static List<ScenarioResult> Compare(Guid scenarioId, int leverCount, RunSnapshot b, RunSnapshot s)
    {
        var rows = new List<ScenarioResult>();
        static decimal D(decimal x, decimal y) => Money.R4(y - x);
        static decimal Dp(decimal x, decimal y) => x == 0m ? 0m : Money.R6((y - x) / x * 100m);
        ScenarioResult Row(string dim, string? dimId, string label, decimal bc, decimal sc, string explanation,
            decimal br = 0m, decimal sr = 0m, bool withMargin = false) => new()
        {
            Id = IdGen.Sequential(), ScenarioId = scenarioId, Dimension = dim, DimensionId = dimId, Label = label,
            BaselineCost = Money.R4(bc), ScenarioCost = Money.R4(sc), CostDelta = D(bc, sc), CostDeltaPct = Dp(bc, sc),
            BaselineRevenue = Money.R4(br), ScenarioRevenue = Money.R4(sr), RevenueDelta = D(br, sr),
            BaselineMargin = withMargin ? Money.R4(br - bc) : 0m, ScenarioMargin = withMargin ? Money.R4(sr - sc) : 0m,
            MarginDelta = withMargin ? D(br - bc, sr - sc) : 0m, Explanation = explanation,
        };

        // FR-8.6 / CR-7: a capacity lever lands entirely in idle capacity, so it is measured there.
        if (Math.Abs(s.Idle - b.Idle) >= 1m)
            rows.Add(Row("COMPONENT_TYPE", "UNUSED_CAPACITY", "Cost of unused capacity", b.Idle, s.Idle,
                s.Idle < b.Idle
                    ? $"Releases {Fmt.Sar(b.Idle - s.Idle)} of capacity currently paid for and not used. This is a prize, not a saving: the cost is committed in salaries, depreciation and service contracts, and becomes a saving only when the establishment or the asset base actually changes (FR-8.7)."
                    : $"Requires {Fmt.Sar(s.Idle - b.Idle)} of additional paid capacity that would sit idle."));

        rows.Add(Row("TOTAL", "FULL_COST", "Full cost including idle capacity", b.Cost + b.Idle, s.Cost + s.Idle,
            "Cost attributed to patients plus capacity paid for but not consumed — the number the income statement sees, and the one a capacity lever moves (FR-5.17).",
            b.Revenue, s.Revenue, withMargin: true));
        rows.Add(Row("TOTAL", null, "Cost attributed to patients", b.Cost, s.Cost, Narrate(leverCount, b, s), b.Revenue, s.Revenue, withMargin: true));

        foreach (var (label, id, bv, sv) in new[]
                 {
                     ("Direct cost", "DIRECT", b.Direct, s.Direct), ("Indirect cost", "INDIRECT", b.Indirect, s.Indirect),
                     ("Allocated overhead", "OVERHEAD", b.Overhead, s.Overhead),
                 })
            rows.Add(Row("COMPONENT_TYPE", id, label, bv, sv,
                Math.Abs(sv - bv) < 1m ? $"{label} is unchanged." : $"{label} moves {(sv > bv ? "up" : "down")} by {Fmt.Sar(Math.Abs(sv - bv))} ({Fmt.P(Dp(bv, sv))}%)."));

        foreach (var (pid, bp) in b.Procedures.OrderBy(p => p.Value.Name, StringComparer.Ordinal))
        {
            if (!s.Procedures.TryGetValue(pid, out var sp)) continue;
            if (Math.Abs(sp.TotalCost - bp.TotalCost) < 1m && Math.Abs(sp.Revenue - bp.Revenue) < 1m) continue;
            var bm = bp.Revenue - bp.TotalCost;
            var sm = sp.Revenue - sp.TotalCost;
            rows.Add(Row("PROCEDURE", pid.ToString(), bp.Name, bp.TotalCost, sp.TotalCost,
                $"Unit cost {Fmt.Sar(bp.UnitCost, 2)} to {Fmt.Sar(sp.UnitCost, 2)} across {Math.Round(bp.Volume, 0)} cases; margin {(sm >= bm ? "improves" : "deteriorates")} by {Fmt.Sar(Math.Abs(sm - bm))}.",
                bp.Revenue, sp.Revenue, withMargin: true));
        }
        return rows;
    }

    private static string Narrate(int leverCount, RunSnapshot b, RunSnapshot s)
    {
        var costDelta = s.Cost - b.Cost;
        var marginDelta = (s.Revenue - s.Cost) - (b.Revenue - b.Cost);
        var idleDelta = s.Idle - b.Idle;
        var head = $"{leverCount} {Fmt.Plural(leverCount, "lever")} applied to {b.Encounters:N0} encounters: cost attributed to patients " +
                   $"{(costDelta >= 0m ? "increases" : "reduces")} by {Fmt.Sar(Math.Abs(costDelta))} ({(b.Cost == 0m ? "0" : Fmt.P(costDelta / b.Cost * 100m, 2))}%), " +
                   $"operating margin {(marginDelta >= 0m ? "improves" : "deteriorates")} by {Fmt.Sar(Math.Abs(marginDelta))}.";
        if (Math.Abs(idleDelta) < 1m) return head;
        return $"{head} Idle capacity {(idleDelta < 0m ? "falls" : "rises")} by {Fmt.Sar(Math.Abs(idleDelta))}, so full cost {(costDelta + idleDelta >= 0m ? "rises" : "falls")} by {Fmt.Sar(Math.Abs(costDelta + idleDelta))}.";
    }
}
