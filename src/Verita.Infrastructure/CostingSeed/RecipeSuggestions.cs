using System.Globalization;

namespace Verita.Infrastructure.CostingSeed;

public sealed record RecipeSuggestion(
    string Entity,
    string Key,
    string Title,
    string Reason,
    Dictionary<string, string> Fields,
    string[] Missing,
    bool AlreadyExists,
    int BilledCount);

public sealed record RecipeSuggestionList(
    string Hospital,
    string? CostingVersionCode,
    RecipeSuggestion[] Items,
    string[] Notices);

/// <summary>
/// Live recipe hints for Data entry. Nothing here is persisted — Save on the form writes the row.
/// </summary>
public static class RecipeSuggestions
{
    public const int TopProcedures = 24;

    private static readonly Dictionary<string, string> PhysicianActivityByFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CONSULT"] = "CONSULT",
        ["ER"] = "ER-ASSESS",
        ["SURGERY"] = "SURGERY",
        ["ANES"] = "ANESTHESIA",
        ["CATH"] = "CATH-PROC",
        ["DENTAL"] = "DENT-VISIT",
        ["DENTIMP"] = "DENT-IMPLANT",
        ["DELIVERY"] = "DELIVERY",
        ["PROC"] = "PROC",
    };

    public sealed record HubCodes(string Token, string? CostCentreCode, string? DepartmentCode);

    public sealed record BilledProc(string Code, string Name, string Category, string ProcType, int Lines);

    public sealed record InvoiceRole(string Family, string ResourceCode, string ResourceName, int Lines);

    public sealed record Input(
        string Hospital,
        DateOnly EffectiveFrom,
        string? CostingVersionCode,
        IReadOnlyList<HubCodes> Hubs,
        HubCodes? Dental,
        IReadOnlySet<string> ActivityCodes,
        IReadOnlySet<string> ResourceCodes,
        IReadOnlySet<string> RecipeKeys,
        IReadOnlySet<string> BoundKeys,
        IReadOnlyList<BilledProc> Billed,
        IReadOnlyList<InvoiceRole> InvoiceRoles);

    public static string ActCode(string hospital, string token) => $"{hospital}.ACT-{token}";
    public static string ResCode(string hospital, string token) => $"{hospital}.RES-{token}";
    public static string RecipeKey(string activityCode, string resourceCode) => activityCode + "|" + resourceCode;
    public static string BoundKey(string procedureCode, string activityCode) => procedureCode + "|" + activityCode;

    public static RecipeSuggestionList Build(Input input, string? entity)
    {
        var want = string.IsNullOrWhiteSpace(entity) ? null : entity.Trim();
        bool Include(string name) => want is null || want.Equals(name, StringComparison.OrdinalIgnoreCase);

        var h = input.Hospital;
        var from = input.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hubBy = input.Hubs.ToDictionary(x => x.Token, StringComparer.OrdinalIgnoreCase);
        var items = new List<RecipeSuggestion>();
        var notices = new List<string>
        {
            "Suggestions are computed from billed HIS procedures and the recipe template. They are not stored. Save in Data entry to write a real row.",
        };
        if (string.IsNullOrWhiteSpace(input.CostingVersionCode))
            notices.Add("No costing version exists yet. Add a CostingVersion first; procedure recipe Save needs costingVersionCode.");

        if (Include("Activity"))
        {
            foreach (var def in SeedTemplates.Activities)
            {
                var code = ActCode(h, def.Token);
                var hub = def.Token is "DENT-VISIT" or "DENT-IMPLANT" && input.Dental is not null
                    ? input.Dental
                    : hubBy.GetValueOrDefault(def.HubPool);
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(hub?.DepartmentCode)) missing.Add("departmentCode");
                if (string.IsNullOrWhiteSpace(hub?.CostCentreCode)) missing.Add("costCentreCode");
                items.Add(new RecipeSuggestion(
                    "Activity",
                    "ACT:" + def.Token,
                    def.Name,
                    $"Template activity {def.Token} on hub {def.HubPool}.",
                    new Dictionary<string, string>
                    {
                        ["hospitalCode"] = h,
                        ["code"] = code,
                        ["catalogCode"] = "ACT-" + def.Token,
                        ["name"] = def.Name,
                        ["category"] = def.Category,
                        ["departmentCode"] = hub?.DepartmentCode ?? "",
                        ["costCentreCode"] = hub?.CostCentreCode ?? "",
                        ["stdDuration"] = Dec(def.Std),
                        ["minDuration"] = Dec(def.Min),
                        ["maxDuration"] = Dec(def.Max),
                        ["variability"] = def.Variability,
                        ["directness"] = "DIRECT",
                        ["status"] = "DRAFT",
                        ["effectiveFrom"] = from,
                    },
                    missing.ToArray(),
                    input.ActivityCodes.Contains(code),
                    0));
            }
        }

        if (Include("ActivityResource"))
        {
            foreach (var line in SeedTemplates.Recipes)
            {
                var act = ActCode(h, line.Activity);
                var res = ResCode(h, line.Resource);
                var missing = new List<string>();
                if (!input.ActivityCodes.Contains(act)) missing.Add("activityCode");
                if (!input.ResourceCodes.Contains(res)) missing.Add("resourceCode");
                items.Add(new RecipeSuggestion(
                    "ActivityResource",
                    "AR:" + line.Activity + "/" + line.Resource,
                    $"{line.Activity} consumes {line.Resource}",
                    $"Template recipe: {Dec(line.Qty)} {line.Uom} of {line.Resource} per {line.Activity}.",
                    new Dictionary<string, string>
                    {
                        ["activityCode"] = act,
                        ["resourceCode"] = res,
                        ["stdQuantity"] = Dec(line.Qty),
                        ["uom"] = line.Uom,
                    },
                    missing.ToArray(),
                    input.RecipeKeys.Contains(RecipeKey(act, res)),
                    0));
            }

            foreach (var role in input.InvoiceRoles)
            {
                if (!PhysicianActivityByFamily.TryGetValue(role.Family, out var tok)) continue;
                var act = ActCode(h, tok);
                var template = SeedTemplates.Recipes.FirstOrDefault(r =>
                    r.Activity.Equals(tok, StringComparison.OrdinalIgnoreCase)
                    && r.Resource.StartsWith("MD-", StringComparison.Ordinal));
                var templateRes = template is null ? null : ResCode(h, template.Resource);
                if (templateRes is not null && templateRes.Equals(role.ResourceCode, StringComparison.OrdinalIgnoreCase))
                    continue;
                var qty = template?.Qty ?? 20m;
                var uom = template?.Uom ?? "MIN";
                var missing = new List<string>();
                if (!input.ActivityCodes.Contains(act)) missing.Add("activityCode");
                if (!input.ResourceCodes.Contains(role.ResourceCode)) missing.Add("resourceCode");
                items.Add(new RecipeSuggestion(
                    "ActivityResource",
                    "AR-INV:" + tok + "/" + role.ResourceCode,
                    $"{tok} from invoice doctors ({role.ResourceName})",
                    $"Invoice providerCode on {role.Family} bills maps to {role.ResourceCode} ({role.ResourceName}) on {role.Lines} lines. Template uses {templateRes ?? "MD-*"}; pick the invoice role if that is the work consumed.",
                    new Dictionary<string, string>
                    {
                        ["activityCode"] = act,
                        ["resourceCode"] = role.ResourceCode,
                        ["stdQuantity"] = Dec(qty),
                        ["uom"] = uom,
                    },
                    missing.ToArray(),
                    input.RecipeKeys.Contains(RecipeKey(act, role.ResourceCode)),
                    role.Lines));
            }
        }

        if (Include("ProcedureActivityLine"))
        {
            var classified = input.Billed
                .Select(p =>
                {
                    var family = ProcedureFamily.Of(p.Name, p.Category, p.ProcType);
                    return (Proc: p, Family: family, Tokens: ProcedureFamily.Activities(family));
                })
                .Where(x => x.Family != ProcedureFamily.Skip && x.Tokens.Count > 0)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in classified.GroupBy(x => x.Family).OrderByDescending(g => g.Sum(x => x.Proc.Lines)))
            {
                foreach (var tok in ProcedureFamily.Activities(family.Key))
                {
                    var act = ActCode(h, tok);
                    var rows = family.OrderByDescending(x => x.Proc.Lines).ToList();
                    var unbound = rows.Where(x => !input.BoundKeys.Contains(BoundKey(x.Proc.Code, act))).ToList();
                    var sample = (unbound.Count > 0 ? unbound[0] : rows[0]).Proc;
                    var missing = PalMissing(input, act);
                    var billed = rows.Sum(x => x.Proc.Lines);
                    items.Add(new RecipeSuggestion(
                        "ProcedureActivityLine",
                        "PAL-FAMILY:" + family.Key + "/" + tok,
                        $"{family.Key} → {tok} ({unbound.Count} of {rows.Count} procedures unbound)",
                        $"HIS family {family.Key}. Use fills the highest-volume unbound procedure ({sample.Code} · {sample.Name}, {sample.Lines} invoice lines). Save writes that recipe; other procedures stay unbound until you pick them.",
                        PalFields(input.CostingVersionCode, sample.Code, act),
                        missing,
                        unbound.Count == 0,
                        billed));
                    if (unbound.Count > 0)
                        seen.Add("PAL:" + sample.Code + "/" + tok);
                }
            }

            var extra = 0;
            foreach (var row in classified
                .SelectMany(x => x.Tokens.Select(tok => (x.Proc, x.Family, Token: tok, Act: ActCode(h, tok))))
                .Where(x => !input.BoundKeys.Contains(BoundKey(x.Proc.Code, x.Act)))
                .OrderByDescending(x => x.Proc.Lines)
                .ThenBy(x => x.Proc.Code, StringComparer.OrdinalIgnoreCase))
            {
                var key = "PAL:" + row.Proc.Code + "/" + row.Token;
                if (!seen.Add(key)) continue;
                if (++extra > TopProcedures) break;
                items.Add(new RecipeSuggestion(
                    "ProcedureActivityLine",
                    key,
                    $"{row.Proc.Code} → {row.Token}",
                    $"{row.Proc.Name}. Family {row.Family}. {row.Proc.Lines} billed invoice lines.",
                    PalFields(input.CostingVersionCode, row.Proc.Code, row.Act),
                    PalMissing(input, row.Act),
                    false,
                    row.Proc.Lines));
            }
        }

        var pending = items.Count(x => !x.AlreadyExists);
        notices.Add(pending == 0
            ? "No pending suggestions for this table — either the rows already exist or billed procedures have not been imported yet."
            : $"{pending} suggestion{(pending == 1 ? "" : "s")} ready to pick. Already-saved rows stay listed so you can see what is done.");

        return new RecipeSuggestionList(h, input.CostingVersionCode, items.ToArray(), notices.ToArray());
    }

    private static Dictionary<string, string> PalFields(string? version, string procedureCode, string activityCode) => new()
    {
        ["procedureCode"] = procedureCode,
        ["costingVersionCode"] = version ?? "",
        ["activityCode"] = activityCode,
        ["quantity"] = "1",
    };

    private static string[] PalMissing(Input input, string activityCode)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(input.CostingVersionCode)) missing.Add("costingVersionCode");
        if (!input.ActivityCodes.Contains(activityCode)) missing.Add("activityCode");
        return missing.ToArray();
    }

    private static string Dec(decimal v) => v.ToString(CultureInfo.InvariantCulture);
}
