using Verita.Domain;

namespace Verita.Engine;

public sealed class ResourceUsage
{
    public decimal Quantity;
    public decimal Cost;
    public int Encounters;
    public int Transactions;
    public decimal RawQuantity;
}

public sealed class ActivityUsage(Guid costCenterId)
{
    /// <summary>Measured minutes (TIME drivers).</summary>
    public decimal Minutes;
    /// <summary>Direct + indirect resource cost consumed by this activity.</summary>
    public decimal ResourceCost;
    /// <summary>Distinct (service, activity) occurrences weighted by service quantity.</summary>
    public decimal Occurrences;
    public readonly HashSet<Guid> Encounters = [];
    /// <summary>Drug value consumed (VALUE drivers).</summary>
    public decimal DrugValue;
    public Guid CostCenterId { get; } = costCenterId;
}

public sealed class ServiceUsage(ServiceRow s)
{
    public ServiceRow Service { get; } = s;
    public decimal DirectCost;
    public decimal IndirectCost;
    /// <summary>Per-activity resource cost inside this service, used to push overhead down (FR-3.14).</summary>
    public readonly Dictionary<Guid, decimal> ByActivity = [];
}

public sealed class EncounterUsage(Guid encounterId)
{
    public Guid EncounterId { get; } = encounterId;
    public decimal DirectCost;
    public decimal IndirectCost;
    public readonly Dictionary<string, (decimal Quantity, decimal Cost)> ByType = [];
    public readonly Dictionary<Guid, (decimal Quantity, decimal Cost, string Uom)> ByResource = [];
    public readonly List<Guid> ServiceIds = [];
}

/// <summary>Facts the data-quality gate needs from the transaction stream.</summary>
public sealed class StreamFacts
{
    public readonly Dictionary<Guid, (int Count, decimal Quantity)> ConsumedByResource = [];
    public int NegativeQuantities;
    public readonly Dictionary<string, int> SourceRefCounts = new(StringComparer.Ordinal);
    public readonly Dictionary<Guid, (int Count, decimal MaxPerOccurrence)> DurationOutliers = [];
}

/// <summary>
/// One pass over the period's measured consumption (FR-2.10). Capacity, activity allocation,
/// actual costing and variance all need aggregates of the same transactions; computing them in
/// four passes would be the largest avoidable cost in a run, so the pass happens once and every
/// stage reads the result.
/// </summary>
public sealed class UsageIndex
{
    public Dictionary<Guid, ResourceUsage> ByResource { get; } = [];
    public Dictionary<Guid, ActivityUsage> ByActivity { get; } = [];
    public Dictionary<Guid, ServiceUsage> ByService { get; } = [];
    public Dictionary<Guid, EncounterUsage> ByEncounter { get; } = [];
    public Dictionary<(Guid ProcedureId, string Type), (decimal Quantity, decimal Cost, string Uom)> ByProcedureComponent { get; } = [];
    public Dictionary<Guid, decimal> ProcedureVolume { get; } = [];
    public Dictionary<Guid, decimal> ByCostCenter { get; } = [];
    public int TransactionCount { get; set; }
    public List<Guid> EncounterIds { get; } = [];
    public StreamFacts Facts { get; } = new();

    public static UsageIndex Build(RunContext ctx, IEnumerable<TxnRow> transactions)
    {
        var index = new UsageIndex();
        var o = ctx.Overrides;
        var bedDayProcedures = ctx.Procedures.Values.Where(p => p.ProcType == "BED_DAY").Select(p => p.Id).ToHashSet();

        foreach (var e in ctx.Encounters)
        {
            index.EncounterIds.Add(e.Id);
            index.ByEncounter[e.Id] = new EncounterUsage(e.Id);
        }

        var serviceQty = new Dictionary<Guid, decimal>();
        foreach (var s in ctx.Services)
        {
            index.ByService[s.Id] = new ServiceUsage(s);
            serviceQty[s.Id] = s.Quantity;
            var vol = s.Quantity * o.Get(o.ProcedureVolume, s.ProcedureId) * o.PatientVolume;
            index.ProcedureVolume[s.ProcedureId] = index.ProcedureVolume.GetValueOrDefault(s.ProcedureId) + vol;
            if (index.ByEncounter.TryGetValue(s.EncounterId, out var eu)) eu.ServiceIds.Add(s.Id);
        }

        var resourceEncounters = new Dictionary<Guid, HashSet<Guid>>();
        var facts = index.Facts;

        foreach (var t in transactions)
        {
            if (!index.ByEncounter.TryGetValue(t.EncounterId, out var eu)) continue;

            // Facts for the gate are taken from the raw stream, before any override.
            var c = facts.ConsumedByResource.GetValueOrDefault(t.ResourceId);
            facts.ConsumedByResource[t.ResourceId] = (c.Count + 1, c.Quantity + t.Quantity);
            if (t.Quantity < 0) facts.NegativeQuantities++;
            facts.SourceRefCounts[t.SourceRef] = facts.SourceRefCounts.GetValueOrDefault(t.SourceRef) + 1;
            if (t.Uom == "MIN" && ctx.Activities.TryGetValue(t.ActivityId, out var actDef))
            {
                // CR-11: per occurrence, not per row. A ten-day admission records ten days of minutes.
                var occ = t.EncounterServiceId is { } sid && serviceQty.TryGetValue(sid, out var q) ? Math.Max(q, 1m) : 1m;
                var perOcc = t.Quantity / occ;
                if (perOcc > actDef.MaxDuration * 3m)
                {
                    var d = facts.DurationOutliers.GetValueOrDefault(t.ActivityId);
                    facts.DurationOutliers[t.ActivityId] = (d.Count + 1, Math.Max(d.MaxPerOccurrence, perOcc));
                }
            }

            if (!ctx.Resources.TryGetValue(t.ResourceId, out var res)) continue;

            var multiplier = o.Get(o.ActivityDuration, t.ActivityId) * o.PatientVolume;
            ServiceUsage? su = null;
            if (t.EncounterServiceId is { } serviceId) index.ByService.TryGetValue(serviceId, out su);
            if (su is not null)
            {
                multiplier *= o.Get(o.ProcedureVolume, su.Service.ProcedureId);
                if (o.LosFactor != 1m && bedDayProcedures.Contains(su.Service.ProcedureId)) multiplier *= o.LosFactor;
            }

            var quantity = t.Quantity * multiplier;
            var cost = quantity * res.Rate;
            var nature = Catalogue.NatureOf(res.ResourceType);
            index.TransactionCount++;

            var ru = index.ByResource.TryGetValue(t.ResourceId, out var existing) ? existing : index.ByResource[t.ResourceId] = new ResourceUsage();
            ru.Quantity += quantity;
            ru.RawQuantity += t.Quantity;
            ru.Cost += cost;
            ru.Transactions++;
            if (!resourceEncounters.TryGetValue(t.ResourceId, out var encSet)) resourceEncounters[t.ResourceId] = encSet = [];
            encSet.Add(t.EncounterId);

            if (!index.ByActivity.TryGetValue(t.ActivityId, out var au)) index.ByActivity[t.ActivityId] = au = new ActivityUsage(t.CostCenterId);
            if (t.Uom == "MIN") au.Minutes += quantity;
            au.ResourceCost += cost;
            au.Encounters.Add(t.EncounterId);
            if (res.ResourceType == "DRUG") au.DrugValue += cost;

            if (su is not null)
            {
                if (nature == "DIRECT") su.DirectCost += cost; else su.IndirectCost += cost;
                su.ByActivity[t.ActivityId] = su.ByActivity.GetValueOrDefault(t.ActivityId) + cost;

                var key = (su.Service.ProcedureId, res.ResourceType);
                var pc = index.ByProcedureComponent.TryGetValue(key, out var prev) ? prev : (0m, 0m, t.Uom);
                index.ByProcedureComponent[key] = (pc.Item1 + quantity, pc.Item2 + cost, pc.Item3);
            }

            if (nature == "DIRECT") eu.DirectCost += cost; else eu.IndirectCost += cost;
            var bt = eu.ByType.GetValueOrDefault(res.ResourceType);
            eu.ByType[res.ResourceType] = (bt.Quantity + quantity, bt.Cost + cost);
            var br = eu.ByResource.TryGetValue(t.ResourceId, out var brPrev) ? brPrev : (0m, 0m, t.Uom);
            eu.ByResource[t.ResourceId] = (br.Item1 + quantity, br.Item2 + cost, br.Item3);

            index.ByCostCenter[t.CostCenterId] = index.ByCostCenter.GetValueOrDefault(t.CostCenterId) + cost;
        }

        foreach (var (resourceId, set) in resourceEncounters) index.ByResource[resourceId].Encounters = set.Count;

        // Activity volume: one occurrence per (service, activity) pair that recorded consumption.
        foreach (var su in index.ByService.Values)
            foreach (var activityId in su.ByActivity.Keys)
                if (index.ByActivity.TryGetValue(activityId, out var au)) au.Occurrences += su.Service.Quantity;

        foreach (var ru in index.ByResource.Values)
        {
            ru.Quantity = Money.R6(ru.Quantity);
            ru.Cost = Money.R6(ru.Cost);
        }
        return index;
    }
}
