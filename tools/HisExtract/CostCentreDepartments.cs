using System.Text;
using System.Text.RegularExpressions;

namespace HisExtract;

/// <summary>
/// Puts each Oracle cost centre on one VERITA department. First match wins:
/// 1. the reviewed override file (--cc-map), 2. HIS cost-centre rules on a clinic (C) or nursing station (N),
/// 3. the clinic or ward that bills at least half of the centre's lines in the last 12 months,
/// 4. a name match against department names, 5. a "Supplier Revenue Machine" centre follows its main centre
/// (Oracle code 200 lower). Anything else stays on UNMAPPED.
/// </summary>
public static class CostCentreDepartments
{
    public sealed record Pick(string Department, string Method);

    public static async Task<Dictionary<string, Pick>> ResolveAsync(
        HisConnection db, Settings s, Codes c, IReadOnlyDictionary<string, string> centres,
        IReadOnlyDictionary<string, string> departments, CsvZipWriter pack)
    {
        var picks = new Dictionary<string, Pick>(StringComparer.OrdinalIgnoreCase);
        bool Known(string dept) => departments.ContainsKey(dept);

        foreach (var (cc, dept) in Overrides(s.CcMapPath, pack))
            if (centres.ContainsKey(cc) && Known(dept)) picks[cc] = new Pick(dept, "override");

        // HIS rules: the same table fn_GetCostCenter reads when it posts an invoice line.
        var ruled = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT CostCenterID, ProcClassType, ProcClassID
            FROM dbo.HIS_RuleCostCenterNew WITH (NOLOCK)
            WHERE SetupID=@SetupId AND ProjectID=@ProjectId AND IsActive=1 AND ProcClassType IN ('C','N')
            """, s.BindTenant, r =>
        {
            var cc = HisConnection.S(r, "CostCenterID");
            if (!int.TryParse(HisConnection.S(r, "ProcClassID"), out var unit) || cc.Length == 0) return;
            var dept = HisConnection.S(r, "ProcClassType") == "N" ? c.WardDept(unit) : c.ClinicDept(unit);
            if (!ruled.TryGetValue(cc, out var set)) ruled[cc] = set = [];
            set.Add(dept);
        });
        foreach (var (cc, set) in ruled)
            if (!picks.ContainsKey(cc) && centres.ContainsKey(cc) && set.Count == 1 && Known(set.First()))
                picks[cc] = new Pick(set.First(), "his-rule");

        // Billed lines: inpatient lines count by the admission's ward, others by the performing clinic.
        var billed = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        await db.QueryAsync("""
            SELECT x.CostCenterID, x.Kind, x.Unit, COUNT(*) AS N
            FROM (
              SELECT LTRIM(RTRIM(d.CostCenterID)) AS CostCenterID,
                     CASE WHEN rm.NursingStationID IS NOT NULL THEN 'N' ELSE 'C' END AS Kind,
                     CASE WHEN rm.NursingStationID IS NOT NULL THEN rm.NursingStationID
                          ELSE COALESCE(d.PrimaryClinicID, m.PerformedServiceClinicID, m.ClinicID) END AS Unit
              FROM dbo.HIS_InvoiceMain m WITH (NOLOCK)
              INNER JOIN dbo.HIS_InvoiceDetail d WITH (NOLOCK)
                ON d.SetupID=m.SetupID AND d.ProjectID=m.ProjectID
               AND d.TransactionType=m.TransactionType AND d.InvoiceNo=m.InvoiceNo
              LEFT JOIN dbo.INP_Admission a WITH (NOLOCK)
                ON a.SetupID=m.SetupID AND a.ProjectID=m.ProjectID AND a.AdmissionNo=m.AdmissionNo AND ISNULL(m.AdmissionNo,0) > 0
              LEFT JOIN dbo.INP_Room rm WITH (NOLOCK)
                ON rm.SetupID=a.SetupID AND rm.ProjectID=a.ProjectID AND rm.RoomID=a.RoomID
              WHERE m.SetupID=@SetupId AND m.ProjectID=@ProjectId
                AND m.InvoiceDate >= DATEADD(MONTH, -12, GETDATE())
                AND m.CancelledOn IS NULL
                AND NULLIF(LTRIM(RTRIM(d.CostCenterID)), '') IS NOT NULL
            ) x
            WHERE x.Unit IS NOT NULL AND x.Unit > 0
            GROUP BY x.CostCenterID, x.Kind, x.Unit
            """, s.BindTenant, r =>
        {
            var cc = HisConnection.S(r, "CostCenterID");
            var unit = HisConnection.I(r, "Unit");
            var dept = HisConnection.S(r, "Kind") == "N" ? c.WardDept(unit) : c.ClinicDept(unit);
            if (!billed.TryGetValue(cc, out var byDept)) billed[cc] = byDept = new(StringComparer.OrdinalIgnoreCase);
            byDept[dept] = byDept.GetValueOrDefault(dept) + HisConnection.I(r, "N");
        });
        foreach (var (cc, byDept) in billed)
        {
            if (picks.ContainsKey(cc) || !centres.ContainsKey(cc)) continue;
            var (dept, n) = byDept.MaxBy(kv => kv.Value);
            if (Known(dept) && n * 2 >= byDept.Values.Sum()) picks[cc] = new Pick(dept, "billed");
        }

        // Name match, e.g. "ICU-Intensive Care Unit" → ward "ICU", "Blood Bank" → lab "Blood Bank".
        var byName = new Dictionary<string, List<string>>();
        foreach (var (code, name) in departments)
        {
            if (name.Contains("(inactive)", StringComparison.OrdinalIgnoreCase) || code == c.UnmappedDept) continue;
            var key = Key(name);
            if (key.Length < 2) continue;
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            list.Add(code);
        }
        // Headings (x00), machine-revenue centres (they follow their main centre below) and pharmacies or
        // stores (a "(ER)" in "Pharmacy 2 (ER)" is a location, not the owner) are not matched by name.
        foreach (var (cc, name) in centres)
        {
            if (picks.ContainsKey(cc) || cc.EndsWith("00", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(name, @"Supplier Revenue|Pharmacy|Store", RegexOptions.IgnoreCase)) continue;
            var inpatient = cc.StartsWith("111", StringComparison.Ordinal);
            // Whole name first, then each part in order ("NICU (Neonatal …)" → "NICU"); unit type breaks ties.
            var hit = Phrases(name).Select(Key).Where(byName.ContainsKey)
                .Select(k => byName[k].OrderBy(d => Rank(d, inpatient)).First()).FirstOrDefault();
            if (hit is not null) picks[cc] = new Pick(hit, "name");
        }

        // Oracle tree: 113xx / 114xx are the supplier-machine revenue of 111xx / 112xx.
        foreach (var (cc, name) in centres)
        {
            if (picks.ContainsKey(cc) || cc.Length != 5 || !int.TryParse(cc, out var code)) continue;
            if (cc[..3] is not ("113" or "114") || cc.EndsWith("00", StringComparison.Ordinal)) continue;
            var main = (code - 200).ToString();
            if (name.Contains("Supplier Revenue", StringComparison.OrdinalIgnoreCase) && picks.TryGetValue(main, out var p))
                picks[cc] = new Pick(p.Department, "sibling");
        }

        foreach (var g in picks.Values.GroupBy(p => p.Method).OrderBy(g => g.Key))
            pack.Note($"CostCenter → Department by {g.Key}: {g.Count()}");
        pack.Note($"CostCenter → Department: {centres.Count - picks.Count} of {centres.Count} left on {c.UnmappedDept}.");
        return picks;
    }

    private static IEnumerable<(string Cc, string Dept)> Overrides(string? path, CsvZipWriter pack)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        if (!File.Exists(path))
        {
            pack.Note($"CostCenter → Department: override file not found: {path}");
            yield break;
        }
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) yield break;
        var head = SplitCsv(lines[0]);
        var ci = head.FindIndex(h => h.Equals("costCentreCode", StringComparison.OrdinalIgnoreCase));
        var di = head.FindIndex(h => h.Equals("departmentCode", StringComparison.OrdinalIgnoreCase));
        if (ci < 0 || di < 0)
        {
            pack.Note($"CostCenter → Department: {path} needs costCentreCode and departmentCode columns.");
            yield break;
        }
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsv(line);
            if (cells.Count <= Math.Max(ci, di)) continue;
            var cc = cells[ci].Trim();
            if (cc.StartsWith("ORA-", StringComparison.OrdinalIgnoreCase)) cc = cc[4..];
            var dept = cells[di].Trim();
            if (cc.Length > 0 && dept.Length > 0) yield return (cc, dept);
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static readonly HashSet<string> Filler = ["CLINIC", "UNIT", "DEPARTMENT", "DEPT", "THE", "AND", "OF", "SECTION", "DIVISION"];

    private static IEnumerable<string> Phrases(string name) =>
        Regex.Split(name, @"[-()/]").Prepend(name).Where(p => p.Trim().Length > 0);

    /// <summary>Upper-case letters and digits only, without filler words: "E.N.T. Clinic" → "ENT".</summary>
    private static string Key(string text)
    {
        var words = Regex.Split(text.ToUpperInvariant(), "[^A-Z0-9]+").Where(w => w.Length > 0 && !Filler.Contains(w));
        return string.Concat(words);
    }

    /// <summary>Inpatient centres (111xx) prefer wards and theatres; the rest prefer clinics and labs.</summary>
    private static int Rank(string dept, bool inpatient)
    {
        var order = inpatient ? new[] { "NS-", "OT-", "CLN-", "LAB-" } : new[] { "CLN-", "LAB-", "NS-", "OT-" };
        var i = Array.FindIndex(order, p => dept.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? order.Length : i;
    }
}
