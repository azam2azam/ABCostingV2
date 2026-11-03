using System.Text.RegularExpressions;

namespace Verita.Infrastructure.CostingSeed;

/// <summary>
/// Picks one hub cost centre per pool token from an imported HIS tree
/// (docs/seed/HIS-COST-POOL-SEED.md §4). Pure: no database.
/// </summary>
public static class HubMatcher
{
    public sealed record Centre(Guid Id, string Code, string Name, bool IsActive);

    public sealed record HubHit(
        string Token, Guid? CostCenterId, string? CostCenterCode, string? CostCenterName,
        string Match, bool Fallback, bool Overridden);

    private static readonly Regex Junk = new(
        @"\b(test|dummy|demo)\b|ssss|wwww|invalid-do not|supplier revenue machine|waada123|reeem|hadeel",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsJunk(string name, string code) =>
        Junk.IsMatch(name) || Junk.IsMatch(code);

    public static string LocalCode(string code)
    {
        var i = code.LastIndexOf('.');
        return i >= 0 ? code[(i + 1)..] : code;
    }

    public static string Norm(string name) =>
        name.Trim().EndsWith(" CC", StringComparison.OrdinalIgnoreCase)
            ? name.Trim()[..^3].Trim()
            : name.Trim();

    public static IReadOnlyList<HubHit> Match(
        IEnumerable<Centre> centres,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyDictionary<Guid, int>? opEncountersByCentre = null)
    {
        var live = centres.Where(c => c.IsActive && !IsJunk(c.Name, c.Code)).ToList();
        var byCode = live.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var hits = new List<HubHit>(SeedTemplates.Pools.Length);

        Guid? Resolve(string token, Func<Centre?> find, string match, bool fallback = false)
        {
            if (overrides is not null && overrides.TryGetValue(token, out var code))
            {
                if (!byCode.TryGetValue(code, out var o))
                    o = live.FirstOrDefault(c =>
                        LocalCode(c.Code).Equals(LocalCode(code), StringComparison.OrdinalIgnoreCase));
                if (o is not null)
                {
                    hits.Add(new HubHit(token, o.Id, o.Code, o.Name, "override", false, true));
                    return o.Id;
                }
            }
            var c = find();
            if (c is null)
            {
                hits.Add(new HubHit(token, null, null, null, "unmatched", fallback, false));
                return null;
            }
            hits.Add(new HubHit(token, c.Id, c.Code, c.Name, match, fallback, false));
            return c.Id;
        }

        Centre? SameAs(string token) =>
            hits.FirstOrDefault(h => h.Token == token) is { CostCenterId: { } id } ? live.First(c => c.Id == id) : null;

        Centre? Pick(IEnumerable<Centre> candidates, string? exact = null)
        {
            var list = candidates.ToList();
            if (list.Count == 0) return null;
            if (exact is not null)
            {
                var eq = list.Where(c => Norm(c.Name).Equals(exact, StringComparison.OrdinalIgnoreCase)).ToList();
                if (eq.Count > 0) list = eq;
            }
            return list
                .OrderBy(c => Norm(c.Name).Length)
                .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        IEnumerable<Centre> Prefixed(string prefix) =>
            live.Where(c => LocalCode(c.Code).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // Dummy CC-CLN/NS/OT/LAB rows are no longer extracted. If those prefixes are
        // absent, the same name rules run against the HIS/Oracle tree.
        IEnumerable<Centre> Scope(string prefix)
        {
            var hit = Prefixed(prefix).ToList();
            return hit.Count > 0 ? hit : live;
        }

        Centre? Hub(string prefix, string exact, string? also = null)
        {
            var scoped = Scope(prefix).ToList();
            var hit = Pick(scoped.Where(c => Norm(c.Name).Equals(exact, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null) return hit;
            foreach (var token in also is null ? new[] { exact } : new[] { exact, also })
            {
                hit = Pick(scoped.Where(c => ContainsToken(Norm(c.Name), token)));
                if (hit is not null) return hit;
            }
            return null;
        }

        var im = Pick(Scope("CC-CLN-").Where(c => Norm(c.Name).Contains("INTERNAL MEDICINE", StringComparison.OrdinalIgnoreCase)));
        if (im is not null) Resolve("PHYS-COMP", () => im, "CC-CLN INTERNAL MEDICINE");
        else Resolve("PHYS-COMP", () => FallbackClinic(live, opEncountersByCentre), "fallback clinic", fallback: true);

        Resolve("OPD-OPS", () => SameAs("PHYS-COMP"), "same as PHYS-COMP");
        Resolve("ER-STAFF", () => Hub("CC-NS-", "ER", "Emergency"), "CC-NS ER");
        Resolve("WARD-NURSE", () => Hub("CC-NS-", "Medical Ward", "Medical Surgical Ward"), "CC-NS Medical Ward");
        Resolve("ICU-NURSE", () => Hub("CC-NS-", "ICU"), "CC-NS ICU");
        Resolve("ICU-BED", () => SameAs("ICU-NURSE"), "same as ICU-NURSE");
        Resolve("NICU-NURSE", () => Hub("CC-NS-", "NICU"), "CC-NS NICU");
        Resolve("OR-STAFF", () => Hub("CC-NS-", "OR", "Operation Room"), "CC-NS OR");
        Resolve("OR-FAC", () => SameAs("OR-STAFF"), "same as OR-STAFF");
        Resolve("SUPPLY-CONS", () => SameAs("OR-STAFF"), "same as OR-STAFF");
        Resolve("CATH-OPS", () => Hub("CC-NS-", "CATH LAB", "Cath Lab"), "CC-NS CATH LAB");
        Resolve("LD-OPS", () => Hub("CC-OT-", "LDR", "DELIVERY ROOM")
            ?? Pick(Scope("CC-OT-").Where(c => ContainsToken(Norm(c.Name), "LDR"))), "CC-OT LDR");
        Resolve("LAB-STAFF", () => Hub("CC-LAB-", "Chemistry"), "CC-LAB Chemistry");
        Resolve("LAB-REAGENT", () => SameAs("LAB-STAFF"), "same as LAB-STAFF");
        Resolve("BLOOD-OPS", () => Hub("CC-CLN-", "Blood Bank"), "CC-CLN Blood Bank");
        Resolve("RAD-STAFF", () => Hub("CC-CLN-", "Radiology"), "CC-CLN Radiology");
        Resolve("RAD-EQUIP", () => SameAs("RAD-STAFF"), "same as RAD-STAFF");

        var pharm = Pick(Prefixed("ORA-").Where(IsPharmacyPrimary));
        if (pharm is not null) Resolve("PHARM-DRUG", () => pharm, "ORA Pharmacy 1");
        else Resolve("PHARM-DRUG", () => Pick(Prefixed("ORA-").Where(IsPharmacyFallback)), "fallback Pharmacy", fallback: true);
        Resolve("PHARM-OPS", () => SameAs("PHARM-DRUG"), "same as PHARM-DRUG");
        Resolve("PHARM-STAFF", () => SameAs("PHARM-DRUG"), "same as PHARM-DRUG");

        Resolve("FACILITY", () => Pick(Prefixed("ORA-"), "Housekeeping"), "ORA Housekeeping");
        Resolve("IT", () => Pick(Prefixed("ORA-").Where(c =>
            Norm(c.Name).Contains("Information Technology", StringComparison.OrdinalIgnoreCase)
            && !Norm(c.Name).Contains("Back End", StringComparison.OrdinalIgnoreCase)
            && !Norm(c.Name).Contains("eServices", StringComparison.OrdinalIgnoreCase))), "ORA Information Technology");
        Resolve("BIOMED", () => Pick(Prefixed("ORA-").Where(c =>
            Norm(c.Name).Equals("Bio Medical - Hospital", StringComparison.OrdinalIgnoreCase))), "ORA Bio Medical - Hospital");
        Resolve("LAUNDRY", () => Pick(Prefixed("ORA-"), "Laundry"), "ORA Laundry");
        Resolve("ADMIN-FIN", () => Pick(Prefixed("ORA-"), "Finance Department"), "ORA Finance Department");
        Resolve("ADMIN-HR", () => Pick(Prefixed("ORA-"), "Human Resources Department"), "ORA Human Resources Department");

        return hits;
    }

    private static bool IsPharmacyPrimary(Centre c)
    {
        var n = Norm(c.Name);
        return n.Equals("Rayan Pharmacy 1", StringComparison.OrdinalIgnoreCase)
            || n.Equals("HMC Main Pharmacy", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(" Pharmacy 1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPharmacyFallback(Centre c)
    {
        var n = Norm(c.Name);
        if (!n.Contains("Pharmacy", StringComparison.OrdinalIgnoreCase)) return false;
        return !n.Contains("Derma", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Qassim", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Takhassusi", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("Sub-Store", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsToken(string name, string token)
    {
        if (name.Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Length <= 3)
            return Regex.IsMatch(name, $@"(^|[^A-Za-z]){Regex.Escape(token)}([^A-Za-z]|$)", RegexOptions.IgnoreCase);
        return name.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static Centre? FallbackClinic(List<Centre> live, IReadOnlyDictionary<Guid, int>? opByCentre)
    {
        var clinics = live.Where(c => LocalCode(c.Code).StartsWith("CC-CLN-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (clinics.Count == 0)
        {
            clinics = live.Where(c =>
            {
                var n = Norm(c.Name);
                return n.Contains("INTERNAL MEDICINE", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("Out-Patient", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Out-Patient", StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        if (clinics.Count == 0) return null;
        if (opByCentre is { Count: > 0 })
        {
            var ranked = clinics
                .Select(c => (c, n: opByCentre.GetValueOrDefault(c.Id)))
                .OrderByDescending(x => x.n)
                .ThenBy(x => x.c.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ranked[0].n > 0) return ranked[0].c;
        }
        return clinics.OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase).First();
    }
}
