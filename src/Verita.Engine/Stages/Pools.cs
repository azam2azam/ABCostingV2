using Verita.Domain;
using Verita.Domain.Entities;

namespace Verita.Engine.Stages;

public sealed class PoolBuildResult
{
    /// <summary>poolId -> amount received from the ledger.</summary>
    public Dictionary<Guid, decimal> PoolDirect { get; } = [];
    public decimal GlTotal { get; set; }
    public decimal UnmappedTotal { get; set; }
    public List<object> UnmappedDetail { get; } = [];
}

/// <summary>
/// Stage 3 — cost pools from the general ledger (FR-1.9, FR-7.13). The only stage that reads
/// money into the system; everything downstream moves it and nothing creates it.
///
///     Σ pool direct + unmapped = Σ GL postings
///
/// A posting whose (account, cost centre) pair has no effective mapping is neither dropped nor
/// guessed at: it lands in UNMAPPED_GL and appears by name on the reconciliation (FR-7.11).
/// </summary>
public static class Pools
{
    public const decimal ConservationTolerance = 0.05m;

    public static (PoolBuildResult Result, StepResult Step) Build(RunContext ctx, StageOutput output)
    {
        var result = new PoolBuildResult();
        var mapIndex = ctx.Mappings
            .GroupBy(m => (m.GlAccountId, m.CostCenterId))
            .ToDictionary(g => g.Key, g => g.ToArray());

        var unmapped = new Dictionary<(string Account, Guid Cc), (string AccountLabel, string Cc, decimal Amount)>();
        decimal glTotal = 0m, unmappedTotal = 0m;

        foreach (var p in ctx.Postings)
        {
            glTotal += p.Amount;
            if (!mapIndex.TryGetValue((p.GlAccountId, p.CostCenterId), out var targets) || targets.Length == 0)
            {
                var ccName = ctx.CcById.TryGetValue(p.CostCenterId, out var cc) ? cc.Name : p.CostCenterId.ToString();
                var k = (p.AccountCode, p.CostCenterId);
                var prev = unmapped.GetValueOrDefault(k);
                unmapped[k] = ($"{p.AccountCode} {p.AccountName}", ccName, prev.Amount + p.Amount);
                unmappedTotal += p.Amount;
                continue;
            }

            // Weights are a declared split of one posting. Distributing through the conservation
            // primitive means a table summing to 0.9 or 1.1 cannot lose or invent money; the
            // discrepancy itself is reported by DQ-GL-WEIGHT.
            var parts = Money.Distribute(p.Amount, targets.Select(t => t.Weight).ToArray(), ctx.DistributionScale);
            for (var i = 0; i < targets.Length; i++)
                result.PoolDirect[targets[i].CostPoolId] = result.PoolDirect.GetValueOrDefault(targets[i].CostPoolId) + parts[i];
        }

        foreach (var (poolId, amount) in result.PoolDirect)
        {
            output.PoolBalances.Add(new CostPoolBalance
            {
                Id = IdGen.Sequential(), RunId = ctx.RunId, CostPoolId = poolId,
                DirectAmount = Money.R4(amount), Residual = Money.R4(amount),
            });
        }

        var allocated = result.PoolDirect.Values.Sum();
        var drift = Math.Abs(glTotal - allocated - unmappedTotal);
        result.GlTotal = Money.R4(glTotal);
        result.UnmappedTotal = Money.R4(unmappedTotal);
        foreach (var u in unmapped.Values) result.UnmappedDetail.Add(new { account = u.AccountLabel, costCenter = u.Cc, amount = Money.R4(u.Amount) });

        var status = drift > ConservationTolerance ? "FAILED" : unmappedTotal > 0m ? "WARNING" : "SUCCESS";
        var message = drift > ConservationTolerance
            ? $"Pool build did not conserve: {Fmt.Sar(drift, 2)} unaccounted."
            : unmappedTotal > 0m
                ? $"{Fmt.Sar(unmappedTotal, 2)} posted to accounts with no cost-pool mapping (UNMAPPED_GL)."
                : $"{ctx.Postings.Count} postings mapped into {output.PoolBalances.Count} pools; pool totals equal the ledger.";

        return (result, new StepResult(output.PoolBalances.Count, Money.R4(glTotal), Money.R4(allocated + unmappedTotal), status, message,
            new Dictionary<string, object?>
            {
                ["postings"] = ctx.Postings.Count, ["pools"] = output.PoolBalances.Count, ["mappings"] = ctx.Mappings.Count,
                ["unmappedAccounts"] = unmapped.Count, ["drift"] = Money.R4(drift),
            }));
    }
}
