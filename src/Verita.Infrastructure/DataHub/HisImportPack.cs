using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Verita.Infrastructure.DataHub;

/// <summary>HIS zip/CSV identity, file skip list, and code remap — no database.</summary>
public static class HisImportPack
{
    public static readonly HashSet<string> IdentityColumns = new(StringComparer.OrdinalIgnoreCase)
        { "setupId", "projectId" };

    public static readonly HashSet<string> ExtraColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "setupId", "projectId", "pack", "from", "toInclusive", "toExclusive", "extractedAtUtc", "veritaHospitalCode",
    };

    private static readonly HashSet<string> CodeColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "hospitalCode", "code", "catalogCode", "departmentCode", "serviceLineCode", "costCentreCode", "facilityCode",
        "procedureCode", "packageCode", "resourceCode", "activityCode", "providerCode", "payerCode", "drgCode", "encounterNo",
    };

    /// <summary>
    /// References to these are written hospital-local. A cost centre's, procedure's and package's own code is stored that
    /// way (a code is unique within its hospital, so 01002003 is 01002003 in every hospital); facilities and departments
    /// are unique across hospitals, so theirs keep `{hospital}.{code}`.
    /// </summary>
    private static readonly HashSet<string> OrgCodeColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "departmentCode", "costCentreCode", "facilityCode", "procedureCode", "packageCode",
    };

    /// <summary>Entities whose own code column is hospital-local.</summary>
    private static readonly HashSet<string> LocalCodeEntities = new(StringComparer.OrdinalIgnoreCase) { "CostCenter", "Procedure", "Package" };

    /// <summary>
    /// A HIS row whose org code is missing from the catalogue still happened: it loads against the
    /// hospital's Unmapped department or cost centre rather than being lost with its service lines.
    /// </summary>
    public static string? UnmappedFallback(string entity, string column) => (entity, column) switch
    {
        ("EncounterService", "costCentreCode") => "CC-UNMAPPED",
        ("Encounter", "departmentCode") or ("Provider", "departmentCode") or ("ProviderClinic", "departmentCode") => "UNMAPPED",
        _ => null,
    };

    /// <summary>
    /// A HIS visit with nothing billed is not an encounter: a row with a HIS status and no service or revenue line
    /// in the pack is left out. A pack with no billing in it cannot tell, so it loads as sent.
    /// </summary>
    public static (List<ParsedFile> Files, int Skipped) SkipUnbilledEncounters(IReadOnlyList<ParsedFile> files)
    {
        if (!files.Any(f => f.Spec?.Name is "EncounterService" or "RevenueTransaction")) return (files.ToList(), 0);
        var billed = files.Where(f => f.Spec?.Name is "EncounterService" or "RevenueTransaction").SelectMany(f => f.Rows)
            .Select(r => r.GetValueOrDefault("encounterNo")?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Unbilled(Dictionary<string, string> r) =>
            !string.IsNullOrWhiteSpace(r.GetValueOrDefault("hisStatus")) && !billed.Contains(r.GetValueOrDefault("encounterNo")?.Trim() ?? "");
        var skipped = 0;
        var kept = new List<ParsedFile>();
        foreach (var file in files)
        {
            if (file.Spec?.Name != "Encounter") { kept.Add(file); continue; }
            var rows = file.Rows.Where(r => !Unbilled(r)).ToList();
            skipped += file.Rows.Count - rows.Count;
            if (rows.Count > 0) kept.Add(file with { Rows = rows });
        }
        return (kept, skipped);
    }

    /// <summary>Doctors on a pack's rows that are in neither the database nor the pack: Not assigned to any resource.</summary>
    public static List<Dictionary<string, string>> UnknownDoctors(IEnumerable<ParsedFile> files, IReadOnlySet<string> known)
    {
        var unknown = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => f.Spec?.Name is "Encounter" or "EncounterService"))
            foreach (var row in file.Rows)
                if (row.GetValueOrDefault("providerCode")?.Trim() is { Length: > 0 } code && !known.Contains(code) && !unknown.ContainsKey(code))
                    unknown[code] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["code"] = code, ["name"] = $"Doctor {code[(code.LastIndexOf('-') + 1)..]}", ["departmentCode"] = "UNMAPPED",
                    };
        return unknown.Values.ToList();
    }

    public static bool IsIgnoredFile(string name)
    {
        var file = Path.GetFileName(name);
        if (file.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("extract-log.txt", StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(file);
        var us = stem.IndexOf('_');
        if (us > 0 && stem[..us].All(char.IsDigit)) stem = stem[(us + 1)..];
        return stem.Equals("manifest", StringComparison.OrdinalIgnoreCase);
    }

    public static string? SpecFromFileName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var us = stem.IndexOf('_');
        if (us > 0 && stem[..us].All(char.IsDigit)) stem = stem[(us + 1)..];
        var page = System.Text.RegularExpressions.Regex.Match(stem, @"_p(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (page.Success) stem = stem[..page.Index];
        return EntitySpecs.Find(stem)?.Name;
    }

    public static int PageOf(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var page = System.Text.RegularExpressions.Regex.Match(stem, @"_p(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return page.Success && int.TryParse(page.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : 1;
    }

    public static string HisToken(string setupId, string projectId) => $"{setupId.Trim()}-{projectId.Trim()}";

    public static string RemapValue(string value, string hisToken, string hospitalCode)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(hisToken)) return value;
        if (value.Equals(hisToken, StringComparison.OrdinalIgnoreCase)) return hospitalCode;
        if (value.StartsWith(hisToken + ".", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(hisToken + "-", StringComparison.OrdinalIgnoreCase))
            return hospitalCode + value[hisToken.Length..];
        return value;
    }

    /// <summary>Hospital-local org code: strip a HIS `{setup}-{project}.` prefix and a VERITA `{hospital}.` prefix.</summary>
    public static string RemapOrgCode(string value, string hisToken, string? hospitalCode = null)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = StripDottedPrefix(value, hisToken);
        value = StripDottedPrefix(value, hospitalCode);
        return value;
    }

    public static string LocalOrgCode(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var i = value.LastIndexOf('.');
        return i >= 0 ? value[(i + 1)..] : value;
    }

    /// <summary>
    /// The encounter number alone: drops a leading `{hospital}-` and `OP-`/`IP-`/`ER-` so older
    /// packs load the same as new ones. Returns the hospital the prefix named, if any.
    /// </summary>
    public static (string No, string? Hospital) LocalEncounterNo(string value, IEnumerable<string> hospitalCodes)
    {
        var hospital = hospitalCodes.Where(h => value.StartsWith(h + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Length).FirstOrDefault();
        if (hospital is null) return (value, null);
        var no = value[(hospital.Length + 1)..];
        foreach (var type in new[] { "OP-", "IP-", "ER-" })
            if (no.StartsWith(type, StringComparison.OrdinalIgnoreCase)) return (no[type.Length..], hospital);
        return (no, hospital);
    }

    private static string StripDottedPrefix(string value, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return value;
        if (value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            return value[(prefix.Length + 1)..];
        return value;
    }

    public static void BindHospital(Dictionary<string, string> row, string entity, string hospitalCode, string setupId, string projectId,
        string? name = null, string? city = null, string? currency = null, bool? isActive = null)
    {
        var token = HisToken(setupId, projectId);
        foreach (var key in row.Keys.ToList())
        {
            if (key is "__row") continue;
            if (ExtraColumns.Contains(key) && !CodeColumns.Contains(key)) continue;
            if (OrgCodeColumns.Contains(key)
                || (LocalCodeEntities.Contains(entity) && (key.Equals("code", StringComparison.OrdinalIgnoreCase)
                    || entity != "CostCenter" && key.Equals("catalogCode", StringComparison.OrdinalIgnoreCase))))
                row[key] = RemapOrgCode(row[key], token, hospitalCode);
            else if (CodeColumns.Contains(key) || (entity == "Hospital" && key.Equals("code", StringComparison.OrdinalIgnoreCase)))
                row[key] = RemapValue(row[key], token, hospitalCode);
        }
        if (entity == "Hospital")
        {
            row["code"] = hospitalCode;
            row["hisSetupId"] = setupId;
            row["hisProjectId"] = projectId;
            row["bedCount"] = "";
            if (name is not null) row["name"] = name;
            if (city is not null) row["city"] = city;
            if (currency is not null) row["currency"] = currency;
            if (isActive is not null) row["isActive"] = isActive.Value ? "true" : "false";
        }
        if (row.ContainsKey("hospitalCode")) row["hospitalCode"] = hospitalCode;
    }

    public static Dictionary<string, string> StripExtra(Dictionary<string, string> row)
    {
        var keep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in row)
            if (!ExtraColumns.Contains(kv.Key)) keep[kv.Key] = kv.Value;
        return keep;
    }

    public static bool IsKnownColumn(EntitySpec spec, string column)
    {
        if (column is "__row") return true;
        if (ExtraColumns.Contains(column)) return true;
        return DataHubService.Describe(spec).Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record ParsedFile(string FileName, EntitySpec? Spec, List<Dictionary<string, string>> Rows);
    public sealed record Identity(string SetupId, string ProjectId, string? Pack, string? From, string? ToInclusive, string? ExtractedAtUtc);

    /// <summary>Costing models are set up in Costing Models and Data entry. Import never changes them.</summary>
    public static readonly HashSet<string> CostingModelTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CostingVersion", "CostingDefinition", "ProcedureActivityLine", "ProcedureResourceLine",
    };

    public static (List<ParsedFile> Files, Identity? His, string[] Skipped) Read(string fileName, Stream stream, string? entityHint = null)
    {
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return ReadZip(stream);
        return ReadCsv(fileName, stream, entityHint);
    }

    private static (List<ParsedFile>, Identity?, string[]) ReadCsv(string fileName, Stream stream, string? entityHint)
    {
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = sr.ReadToEnd();
        var rows = Csv.Parse(text);
        var specName = string.IsNullOrWhiteSpace(entityHint) ? SpecFromFileName(fileName) : entityHint;
        var spec = specName is null ? null : EntitySpecs.Find(specName);
        if (spec is null)
            return ([], null, [Path.GetFileName(fileName)]);
        if (rows.Count == 0) return ([], IdentityFrom([], []), []);
        List<ParsedFile> files = [new ParsedFile(Path.GetFileName(fileName), spec, rows)];
        return (files, IdentityFrom(files, []), []);
    }

    private static (List<ParsedFile>, Identity?, string[]) ReadZip(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var files = new List<ParsedFile>();
        var skipped = new List<string>();
        List<Dictionary<string, string>>? manifest = null;
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            if (IsIgnoredFile(entry.Name))
            {
                skipped.Add(entry.Name);
                if (Path.GetFileName(entry.Name).Contains("manifest", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    manifest = Csv.Parse(reader.ReadToEnd());
                }
                continue;
            }
            if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(entry.Name);
                continue;
            }
            var specName = SpecFromFileName(entry.Name);
            var spec = specName is null ? null : EntitySpecs.Find(specName);
            using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
            var rows = Csv.Parse(sr.ReadToEnd());
            files.Add(new ParsedFile(entry.Name, spec, rows));
        }
        files = CoalescePages(files);
        return (files, IdentityFrom(files.Where(f => f.Spec is not null).ToList(), manifest ?? []), skipped.ToArray());
    }

    /// <summary>
    /// Extract splits a table at 50,000 rows into <c>NN_Entity.csv</c> + <c>NN_Entity_p2.csv</c>….
    /// Import concatenates those pages in page order so every page loads, not only the last file.
    /// </summary>
    public static List<ParsedFile> CoalescePages(IEnumerable<ParsedFile> files)
    {
        return files
            .Where(f => f.Rows.Count > 0)
            .GroupBy(f => f.Spec?.Name ?? f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(f => PageOf(f.FileName)).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
                if (ordered.Count == 1) return ordered[0];
                var rows = ordered.SelectMany(f => f.Rows).ToList();
                var label = ordered.Count == 2
                    ? $"{ordered[0].FileName} + {Path.GetFileName(ordered[1].FileName)}"
                    : $"{ordered[0].FileName} + {ordered.Count - 1} pages";
                return new ParsedFile(label, ordered[0].Spec, rows);
            })
            .OrderBy(f => f.Spec is null ? int.MaxValue : Array.IndexOf(EntitySpecs.All, f.Spec))
            .ToList();
    }

    public static Identity? IdentityFrom(IReadOnlyList<ParsedFile> files, IReadOnlyList<Dictionary<string, string>> manifest)
    {
        static string? Get(Dictionary<string, string> row, string key) =>
            row.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        if (manifest.Count > 0)
        {
            var m = manifest[0];
            var setup = Get(m, "setupId");
            var project = Get(m, "projectId");
            if (setup is not null && project is not null)
                return new Identity(setup, project, Get(m, "pack"), Get(m, "from"), Get(m, "toInclusive"), Get(m, "extractedAtUtc"));
        }
        foreach (var file in files)
        {
            foreach (var row in file.Rows)
            {
                var setup = Get(row, "setupId");
                var project = Get(row, "projectId");
                if (setup is not null && project is not null)
                    return new Identity(setup, project, null, null, null, null);
            }
        }
        return null;
    }

    public static IEnumerable<string> TenantConflicts(IReadOnlyList<ParsedFile> files, Identity id)
    {
        foreach (var file in files)
        {
            foreach (var row in file.Rows)
            {
                var setup = row.GetValueOrDefault("setupId")?.Trim();
                var project = row.GetValueOrDefault("projectId")?.Trim();
                if (string.IsNullOrEmpty(setup) && string.IsNullOrEmpty(project)) continue;
                if (!string.Equals(setup, id.SetupId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(project, id.ProjectId, StringComparison.OrdinalIgnoreCase))
                    yield return $"{file.FileName} has {setup}/{project}";
            }
        }
    }

    public static bool TryProjectId(string raw, out int projectId) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out projectId);

    public static bool IsFactsPack(Identity? id, IReadOnlyList<ParsedFile> files) =>
        id is not null && (
            string.Equals(id.Pack, "facts", StringComparison.OrdinalIgnoreCase)
            || files.Any(f => f.Spec?.Name is "Encounter" or "EncounterService" or "Patient" or "RevenueTransaction"));

    public static bool TryFactRange(Identity? id, string? fileName, out DateOnly from, out DateOnly toInclusive)
    {
        from = default;
        toInclusive = default;
        if (id is not null
            && DateOnly.TryParse(id.From, CultureInfo.InvariantCulture, out from))
        {
            if (!DateOnly.TryParse(id.ToInclusive, CultureInfo.InvariantCulture, out toInclusive))
                toInclusive = from;
            if (toInclusive < from) (from, toInclusive) = (toInclusive, from);
            return true;
        }
        var name = Path.GetFileNameWithoutExtension(fileName ?? "");
        var m = System.Text.RegularExpressions.Regex.Match(name, @"-(\d{4}-\d{2})-facts$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && DateOnly.TryParseExact(m.Groups[1].Value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            from = month;
            toInclusive = month.AddMonths(1).AddDays(-1);
            return true;
        }
        return false;
    }
}
