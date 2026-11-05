using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.DataHub;

public sealed record ColumnSpec(string Name, string Kind, bool Required, bool Key, string[]? Options, string? RefEntity, string? Description, string? Label = null);
public sealed record EntityDescription(string Name, string Group, string Description, bool Versioned, string WritePermission, bool IsTransaction,
    ColumnSpec[] Columns, string[] KeyColumns);
public sealed record RowError(int Row, string? Key, string[] Errors, Dictionary<string, string> Values);
public sealed record ImportResult(string Entity, Guid? BatchId, bool DryRun, int Rows, int Created, int Updated, int Unchanged, int Rejected, RowError[] Rejects);
public sealed record RowPage(string Entity, string[] Columns, Dictionary<string, string>[] Rows, int Total, int Page, int PageSize);
public sealed record ImportBatchDto(Guid Id, string Entity, string SourceSystem, int RowsAccepted, int RowsRejected, DateTime LoadedAt, string LoadedBy, bool HasRejects);

/// <summary>
/// Data entry and raw-data upload for every table in <see cref="EntitySpecs"/> (BRD 22.2). One code
/// path serves the form and the file: each row is parsed, validated, its references resolved by
/// code, and then created or updated on its natural key. A bad row is rejected on its own with the
/// reason — never the whole file. Effective-dated rows (rates, capacity, mappings, prices) are never
/// edited in place: a change is a new version and the previous one is closed the day before.
/// </summary>
public sealed class DataHubService(IDbContextFactory<VeritaDbContext> factory, IAuditWriter audit, ILogger<DataHubService> log)
{
    private static readonly string[] AlwaysRequired = ["Code", "Name", "Mrn", "EncounterNo", "SourceRef", "Email"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>The writable scalar properties of a table, in declaration order.</summary>
    private static PropertyInfo[] Scalars(EntitySpec spec) =>
        spec.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.Name != "Id" && !(spec.Hidden ?? []).Contains(p.Name))
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) != typeof(Guid))
            .ToArray();

    private static string KindOf(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(string) ? "text" : u == typeof(int) ? "int" : u == typeof(decimal) ? "decimal" : u == typeof(bool) ? "bool"
            : u == typeof(DateOnly) ? "date" : u == typeof(DateTime) || u == typeof(DateTimeOffset) ? "datetime" : "text";
    }

    public static EntityDescription Describe(EntitySpec spec)
    {
        var defaults = Activator.CreateInstance(spec.Type)!;
        var cols = new List<ColumnSpec>();
        foreach (var r in spec.Refs)
            cols.Add(new(r.Column, "ref", !r.Optional, spec.Key.Contains(r.FkProperty), null, EntitySpecs.All.FirstOrDefault(s => s.Type == r.Target)?.Name,
                $"Code of the {r.Target.Name}{(r.Optional ? " (optional)" : "")}"));
        foreach (var x in spec.ExtraColumns ?? [])
            cols.Add(new(x, x == "password" ? "password" : "ref", x != "password", x != "password",
                null, x == "procedureCode" ? "Procedure" : x == "costingVersionCode" ? "CostingVersion" : null,
                x == "password" ? "Initial password (required for a new user; leave empty to keep)" : null));
        foreach (var p in Scalars(spec))
        {
            var kind = KindOf(p.PropertyType);
            var nullable = Nullable.GetUnderlyingType(p.PropertyType) is not null || (p.PropertyType == typeof(string) && IsNullableRef(p));
            var value = p.GetValue(defaults);
            var hasDefault = kind == "bool" || (value is not null && !Equals(value, DefaultOf(p.PropertyType)) && !(value is string s0 && s0 == ""));
            var options = Catalogue.ColumnDomains.TryGetValue($"{spec.Type.Name}.{p.Name}", out var o) ? o : null;
            var required = spec.Key.Contains(p.Name) || AlwaysRequired.Contains(p.Name) ||
                           (!nullable && !hasDefault && (kind is "decimal" or "date" or "datetime" or "int" || options is not null));
            var camel = Camel(p.Name);
            var label = p.Name switch
            {
                "HisSetupId" => "HIS SetupId",
                "HisProjectId" => "HIS ProjectId",
                _ => camel,
            };
            cols.Add(new(camel, kind, required, spec.Key.Contains(p.Name), options, null,
                p.Name == "HisSetupId" ? "HISGENX SetupID for this hospital (same tenant can map to more than one VERITA hospital)"
                    : p.Name == "HisProjectId" ? "HISGENX ProjectID (branch)"
                    : hasDefault && !required ? $"Defaults to {Convert.ToString(value, CultureInfo.InvariantCulture)}" : null,
                label));
        }
        var keyCols = spec.Key.Select(k => spec.Refs.FirstOrDefault(r => r.FkProperty == k)?.Column ?? (k == "DefinitionId" ? "procedureCode+costingVersionCode" : Camel(k))).ToArray();
        return new EntityDescription(spec.Name, spec.Group, spec.Description, spec.Versioned, spec.WritePermission, spec.IsTransaction, cols.ToArray(), keyCols);
    }

    private static bool IsNullableRef(PropertyInfo p) =>
        new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    // ── Templates ──────────────────────────────────────────────────────────────

    /// <summary>A CSV template: the header row, then a guidance row the uploader skips (it starts with #).</summary>
    public static string Template(EntitySpec spec)
    {
        var d = Describe(spec);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", d.Columns.Select(c => Csv.Escape(c.Name))));
        sb.AppendLine(string.Join(",", d.Columns.Select((c, i) => Csv.Escape((i == 0 ? "# " : "") + Hint(c)))));
        return sb.ToString();
    }

    private static string Hint(ColumnSpec c) =>
        (c.Required ? "required" : "optional") + " · " + c.Kind switch
        {
            "ref" => $"{c.RefEntity} code",
            "date" => "yyyy-mm-dd",
            "datetime" => "yyyy-mm-dd hh:mm",
            "decimal" => "number, dot decimal",
            "bool" => "true/false",
            "int" => "whole number",
            "password" => "initial password",
            _ => c.Options is not null ? string.Join("|", c.Options) : "text",
        };

    // ── Lookups ────────────────────────────────────────────────────────────────

    private sealed record RefTarget(Guid Id, string? HospitalCode);

    private static async Task<Dictionary<string, RefTarget>> CodesAsync(VeritaDbContext db, Type target, string key, Dictionary<Guid, string> hospitals, CancellationToken ct)
    {
        var method = typeof(DataHubService).GetMethod(nameof(CodesOf), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(target);
        return await (Task<Dictionary<string, RefTarget>>)method.Invoke(null, [db, key, hospitals, ct])!;
    }

    private static async Task<Dictionary<string, RefTarget>> CodesOf<T>(VeritaDbContext db, string key, Dictionary<Guid, string> hospitals, CancellationToken ct) where T : class
    {
        var hospitalProp = typeof(T).GetProperty("HospitalId");
        // A shared row has no HospitalId.
        var rows = hospitalProp?.PropertyType == typeof(Guid?)
            ? (await db.Set<T>().IgnoreQueryFilters().AsNoTracking().Select(e => new { Id = EF.Property<Guid>(e, "Id"), K = EF.Property<string>(e, key), H = EF.Property<Guid?>(e, "HospitalId") }).ToListAsync(ct))
                .Select(x => new { x.Id, x.K, H = x.H ?? Guid.Empty }).ToList()
            : hospitalProp is not null
            ? await db.Set<T>().IgnoreQueryFilters().AsNoTracking().Select(e => new { Id = EF.Property<Guid>(e, "Id"), K = EF.Property<string>(e, key), H = EF.Property<Guid>(e, "HospitalId") }).ToListAsync(ct)
            : (await db.Set<T>().IgnoreQueryFilters().AsNoTracking().Select(e => new { Id = EF.Property<Guid>(e, "Id"), K = EF.Property<string>(e, key) }).ToListAsync(ct))
                .Select(x => new { x.Id, x.K, H = Guid.Empty }).ToList();
        var dict = new Dictionary<string, RefTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var hc = r.H == Guid.Empty ? null : hospitals.GetValueOrDefault(r.H);
            var target = new RefTarget(r.Id, hc);
            dict.TryAdd(r.K, target);
            if (string.IsNullOrEmpty(hc) || string.IsNullOrEmpty(r.K)) continue;
            dict.TryAdd($"{hc}|{r.K}", target);
            var local = HisImportPack.LocalOrgCode(r.K);
            dict.TryAdd($"{hc}|{local}", target);
            if (!r.K.StartsWith(hc + ".", StringComparison.OrdinalIgnoreCase))
                dict.TryAdd($"{hc}.{r.K}", target);
        }
        // Encounter numbers, MRNs, costing version and period codes repeat across hospitals, so a bare one only resolves within a hospital.
        if (typeof(T) == typeof(Encounter) || typeof(T) == typeof(Patient) || typeof(T) == typeof(CostingVersion) || typeof(T) == typeof(Period))
            foreach (var repeated in rows.GroupBy(r => r.K, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                dict.Remove(repeated.Key);
        return dict;
    }

    /// <summary>Encounters already loaded under an ER assessment's own number, which this file now merges into an appointment.</summary>
    private static async Task<Dictionary<(Guid HospitalId, string No), (Guid Id, bool Costed)>> ReplacedErEncountersAsync(VeritaDbContext db,
        IReadOnlyList<Dictionary<string, string>> rows, CancellationToken ct)
    {
        var numbers = rows.Select(r => r.GetValueOrDefault("sourceAssessmentNo")?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
        if (numbers.Count == 0) return [];
        return (await db.Encounters.AsNoTracking().Where(e => e.EncType == "ER" && (e.SourceAssessmentNo == null || e.SourceAssessmentNo == e.EncounterNo) && numbers.Contains(e.EncounterNo))
                .Select(e => new { e.HospitalId, e.EncounterNo, e.Id, Costed = db.EncounterCosts.Any(c => c.EncounterId == e.Id) }).ToListAsync(ct))
            .ToDictionary(e => (e.HospitalId, e.EncounterNo), e => (e.Id, e.Costed));
    }

    /// <summary>
    /// Moves what the old ER encounter held onto the merged one, so a re-import finds those rows instead of doubling them, then removes it.
    /// The appointment's clinic-visit minutes go too: the extract no longer writes them, as the ER rows now measure that visit.
    /// </summary>
    private async Task MergeReplacedErEncountersAsync(List<(Guid HospitalId, string AssessmentNo, string Key)> merges,
        Dictionary<(Guid HospitalId, string No), (Guid Id, bool Costed)> replaced, IReadOnlyDictionary<string, object> existing, CancellationToken ct)
    {
        var targets = merges.Where(m => existing.TryGetValue(m.Key, out var e) && e is Encounter).ToList();
        if (targets.Count == 0) return;
        await using var db = await Db(ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE t FROM [ActivityTransaction] t JOIN OPENJSON({0}) WITH (Id uniqueidentifier '$') m ON t.EncounterId = m.Id WHERE t.SourceRef LIKE 'APPT-%'",
            [JsonSerializer.Serialize(targets.Select(m => ((Encounter)existing[m.Key]).Id).Distinct())], ct);
        var moves = targets
            .Where(m => replaced.ContainsKey((m.HospitalId, m.AssessmentNo)))
            .Select(m => new { FromId = replaced[(m.HospitalId, m.AssessmentNo)].Id, ToId = ((Encounter)existing[m.Key]).Id })
            .Where(m => m.FromId != m.ToId).DistinctBy(m => m.FromId).ToList();
        if (moves.Count == 0) return;
        var json = JsonSerializer.Serialize(moves);
        foreach (var table in new[] { "ActivityTransaction", "EncounterService", "RevenueTransaction", "RefundTransaction", "InvoicePackageItem" })
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE t SET EncounterId = m.ToId FROM [{table}] t JOIN OPENJSON({{0}}) WITH (FromId uniqueidentifier, ToId uniqueidentifier) m ON t.EncounterId = m.FromId", [json], ct);
        await db.Database.ExecuteSqlRawAsync(
            "DELETE e FROM [Encounter] e JOIN OPENJSON({0}) WITH (FromId uniqueidentifier) m ON e.Id = m.FromId", [json], ct);
    }

    /// <summary>Links every payer to its insurer and administrator, creating the ones not seen before. Only rows that differ are written.</summary>
    private async Task MapPayerPartiesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        var payers = await db.Payers.AsNoTracking().Select(x => new { x.Id, x.Name, x.InsurerId, x.AdministratorId, x.HisGroupId, x.HisGroupName, x.HisGroupIsTpa, x.HisGroupManagedByTpa }).ToListAsync(ct);
        var groups = await db.Insurers.Where(x => x.HisGroupId != null).ToDictionaryAsync(x => x.HisGroupId!.Value, ct);
        Guid GroupOf(int groupId, string name, bool? isTpa, bool? managedByTpa)
        {
            if (!groups.TryGetValue(groupId, out var insurer))
                db.Insurers.Add(groups[groupId] = insurer = new Insurer { Id = IdGen.Sequential(), Code = $"GRP-{groupId}", HisGroupId = groupId });
            insurer.Name = name;
            insurer.IsTpa = isTpa;
            insurer.IsManagedByTpa = managedByTpa;
            return insurer.Id;
        }
        var insurers = await db.Insurers.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Id, StringComparer.OrdinalIgnoreCase, ct);
        var administrators = await db.PayerAdministrators.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Id, StringComparer.OrdinalIgnoreCase, ct);
        Guid? InsurerOf(string? name)
        {
            if (name is null) return null;
            var code = name.ToUpperInvariant();
            if (insurers.TryGetValue(code, out var id)) return id;
            db.Insurers.Add(new Insurer { Id = id = IdGen.Sequential(), Code = code, Name = name });
            return insurers[code] = id;
        }
        Guid? AdministratorOf(string? name)
        {
            if (name is null) return null;
            if (administrators.TryGetValue(name, out var id)) return id;
            db.PayerAdministrators.Add(new PayerAdministrator { Id = id = IdGen.Sequential(), Code = name, Name = name });
            return administrators[name] = id;
        }
        var changes = payers.Select(x =>
        {
            var (insurer, administrator) = PayerParties.Split(x.Name);
            var to = x.HisGroupId is { } g && !string.IsNullOrWhiteSpace(x.HisGroupName) ? GroupOf(g, x.HisGroupName.Trim(), x.HisGroupIsTpa, x.HisGroupManagedByTpa) : InsurerOf(insurer);
            return new { x.Id, x.InsurerId, x.AdministratorId, To = to, ToAdministrator = AdministratorOf(administrator) };
        }).Where(x => x.InsurerId != x.To || x.AdministratorId != x.ToAdministrator)
            .Select(x => new { x.Id, I = x.To, A = x.ToAdministrator }).ToList();
        await db.SaveChangesAsync(ct);
        if (changes.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE p SET InsurerId = m.I, AdministratorId = m.A FROM [Payer] p JOIN OPENJSON({0}) WITH (Id uniqueidentifier, I uniqueidentifier, A uniqueidentifier) m ON p.Id = m.Id",
            [JsonSerializer.Serialize(changes)], ct);
        // Insurers read from names are replaced by HIS groups as companies come in with one.
        await db.Database.ExecuteSqlRawAsync("DELETE i FROM [Insurer] i WHERE NOT EXISTS (SELECT 1 FROM [Payer] p WHERE p.InsurerId = i.Id)", ct);
    }

    /// <summary>
    /// A package item names the child invoice line that delivered it. Marks those lines whichever file arrives first,
    /// so packs without the InvoiceType column are handled too.
    /// </summary>
    private async Task MarkChildInvoiceLinesAsync(CancellationToken ct)
    {
        await using var db = await Db(ct);
        await db.Database.ExecuteSqlRawAsync(OpenPeriods.MarkChildInvoiceLinesSql, ct);
    }

    /// <summary>Older packs loaded refunds as negative revenue. Each one saved as a refund line stops counting as revenue.</summary>
    private async Task RemoveRefundsFromRevenueAsync(List<(Guid HospitalId, string SourceRef, string Key)> refunds, CancellationToken ct)
    {
        if (refunds.Count == 0) return;
        await using var db = await Db(ct);
        var json = JsonSerializer.Serialize(refunds.Select(r => new { r.HospitalId, r.SourceRef }).Distinct());
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE t FROM [RevenueTransaction] t
            JOIN [Encounter] e ON e.Id = t.EncounterId
            JOIN OPENJSON({0}) WITH (HospitalId uniqueidentifier, SourceRef nvarchar(100)) m ON m.HospitalId = e.HospitalId AND m.SourceRef = t.SourceRef
            WHERE t.SourceRef LIKE 'REF-%'
            """, [json], ct);
    }

    private static IQueryable<T> CandidatesOf<T>(VeritaDbContext db, string keyProperty, IReadOnlyList<Dictionary<string, string>> rows) where T : class
    {
        var column = char.ToLowerInvariant(keyProperty[0]) + keyProperty[1..];
        var wanted = rows.Select(r => r.GetValueOrDefault(column)?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
        return db.Set<T>().IgnoreQueryFilters().Where(e => wanted.Contains(EF.Property<string>(e, keyProperty)));
    }

    private static bool TryResolve(Dictionary<string, RefTarget> lookup, string code, string? hospitalCode, out RefTarget target)
    {
        if (!string.IsNullOrEmpty(hospitalCode))
        {
            if (lookup.TryGetValue($"{hospitalCode}|{code}", out target!)) return true;
            var local = HisImportPack.LocalOrgCode(code);
            if (lookup.TryGetValue($"{hospitalCode}|{local}", out target!)) return true;
            if (lookup.TryGetValue($"{hospitalCode}.{local}", out target!)) return true;
        }
        return lookup.TryGetValue(code, out target!);
    }

    public async Task<Dictionary<string, string>[]> LookupAsync(EntitySpec spec, Principal p, CancellationToken ct)
    {
        var page = await ListAsync(spec, p, null, 1, 5000, ct);
        var key = DisplayKey(Describe(spec));
        return page.Rows.Select(r => new Dictionary<string, string>
        {
            ["code"] = r.GetValueOrDefault(key) ?? "",
            ["name"] = r.GetValueOrDefault("name") ?? r.GetValueOrDefault("fullName") ?? r.GetValueOrDefault(key) ?? "",
        }).DistinctBy(r => r["code"], StringComparer.OrdinalIgnoreCase).ToArray(); // Costing version codes repeat across hospitals.
    }

    // ── Listing ────────────────────────────────────────────────────────────────

    public async Task<RowPage> ListAsync(EntitySpec spec, Principal p, string? search, int page, int pageSize, CancellationToken ct)
    {
        var method = typeof(DataHubService).GetMethod(nameof(ListOf), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(spec.Type);
        return await (Task<RowPage>)method.Invoke(this, [spec, p, search, page, pageSize, ct])!;
    }

    private async Task<RowPage> ListOf<T>(EntitySpec spec, Principal p, string? search, int page, int pageSize, CancellationToken ct) where T : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var d = Describe(spec);
        var hospitals = await db.Hospitals.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, ct);
        var reverse = new Dictionary<string, Dictionary<Guid, string>>();
        foreach (var r in spec.Refs)
            reverse[r.Column] = r.Target == typeof(CostingVersion)
                ? await db.CostingVersions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct)
                : (await CodesAsync(db, r.Target, r.TargetKey, hospitals, ct))
                .GroupBy(kv => kv.Value.Id)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Key)
                    .Where(k => k.IndexOf('|') < 0)
                    .OrderBy(k => k.Length)
                    .First());
        Dictionary<Guid, (string Proc, string Ver)>? defs = null;
        if (spec.ExtraColumns?.Contains("procedureCode") == true)
        {
            var procs = await db.Procedures.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
            var vers = await db.CostingVersions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
            defs = await db.CostingDefinitions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => (procs[x.ProcedureId], vers[x.CostingVersionId]), ct);
        }

        var all = await db.Set<T>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var scalars = Scalars(spec);
        var rows = new List<Dictionary<string, string>>();
        foreach (var e in all)
        {
            var row = new Dictionary<string, string>();
            foreach (var r in spec.Refs)
            {
                var id = spec.Type.GetProperty(r.FkProperty)!.GetValue(e) as Guid?;
                row[r.Column] = id is { } g && reverse[r.Column].TryGetValue(g, out var code) ? code : "";
            }
            if (defs is not null && spec.Type.GetProperty("DefinitionId")!.GetValue(e) is Guid defId && defs.TryGetValue(defId, out var pv))
            {
                row["procedureCode"] = pv.Proc;
                row["costingVersionCode"] = pv.Ver;
            }
            foreach (var prop in scalars) row[Camel(prop.Name)] = Format(prop.GetValue(e));
            if (!InScope(p, spec, row, hospitals)) continue;
            rows.Add(row);
        }
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(r => r.Values.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        var key = DisplayKey(d);
        rows = rows.OrderBy(r => r.GetValueOrDefault(key), StringComparer.OrdinalIgnoreCase).ToList();
        pageSize = Math.Clamp(pageSize, 1, 5000);
        page = Math.Max(1, page);
        return new RowPage(spec.Name, d.Columns.Where(c => c.Kind != "password").Select(c => c.Name).ToArray(),
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Count, page, pageSize);
    }

    private static string DisplayKey(EntityDescription d) =>
        d.KeyColumns.FirstOrDefault(c => c.Equals("code", StringComparison.OrdinalIgnoreCase))
        ?? d.KeyColumns[0].Split('+')[0];

    private static string Format(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>A row is visible and writable only inside the caller's hospital scope (FR-0.6).</summary>
    private static bool InScope(Principal p, EntitySpec spec, Dictionary<string, string> row, Dictionary<Guid, string> hospitals)
    {
        if (p.AllHospitals) return true;
        if (row.TryGetValue("hospitalCode", out var h) && !string.IsNullOrEmpty(h)) return p.HasHospital(h);
        if (spec.Name == "Hospital") return p.HasHospital(row.GetValueOrDefault("code") ?? "");
        // Codes are prefixed with the hospital code (KFH-DMM.SURG); rows reached through them follow it.
        var codes = row.Where(kv => kv.Key.EndsWith("Code") || kv.Key is "encounterNo" or "patientMrn").Select(kv => kv.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
        return codes.Count == 0 || codes.Any(c => hospitals.Values.Any(hc => c.StartsWith(hc, StringComparison.OrdinalIgnoreCase) && p.HasHospital(hc)));
    }

    // ── Writing: one form row or a whole file ──────────────────────────────────

    private (SqlConnection Conn, SqlTransaction Tx)? _shared;
    private bool _rehearsal;
    private string? _importHospital;

    /// <summary>A context on the import's shared transaction when one is open, otherwise a fresh one.</summary>
    private async Task<VeritaDbContext> Db(CancellationToken ct)
    {
        if (_shared is not { } s) return await factory.CreateDbContextAsync(ct);
        var db = new VeritaDbContext(new DbContextOptionsBuilder<VeritaDbContext>().UseSqlServer(s.Conn).Options);
        await db.Database.UseTransactionAsync(s.Tx, ct);
        return db;
    }

    /// <summary>
    /// Load a set of files in order inside one transaction. A dry run is the real load rolled back
    /// at the end, so later files see the rows of earlier ones — a zip validates as it would load.
    /// Rejected rows never abort the set; only an unexpected failure rolls everything back.
    /// </summary>
    public async Task<ImportResult[]> ImportSetAsync(IReadOnlyList<(EntitySpec Spec, List<Dictionary<string, string>> Rows)> files, Principal p, bool dryRun, string source, CancellationToken ct, string? hospital = null)
    {
        if (files.FirstOrDefault(f => HisImportPack.CostingModelTables.Contains(f.Spec.Name)).Spec is { } costing)
            throw AppError.Invalid($"Import does not change costing models, so {costing.Name} cannot be imported.");
        await using var probe = await factory.CreateDbContextAsync(ct);
        await using var conn = new SqlConnection(probe.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        _shared = (conn, tx);
        _rehearsal = dryRun;
        _importHospital = hospital;
        var results = new List<ImportResult>();
        try
        {
            foreach (var (spec, rows) in files)
            {
                try
                {
                    var r = await SaveAsync(spec, p, rows, false, source, ct);
                    results.Add(r with { DryRun = dryRun, BatchId = dryRun ? null : r.BatchId });
                    log.LogInformation("Import {Mode} {Entity} rows={Rows} created={Created} updated={Updated} unchanged={Unchanged} rejected={Rejected} source={Source}",
                        dryRun ? "validate" : "commit", spec.Name, r.Rows, r.Created, r.Updated, r.Unchanged, r.Rejected, source);
                }
                catch (AppError ex) when (ex.Code is "FORBIDDEN")
                {
                    log.LogWarning("Import {Entity} forbidden for {Email}: {Message}", spec.Name, p.Email, ex.Message);
                    results.Add(new ImportResult(spec.Name, null, dryRun, rows.Count, 0, 0, 0, rows.Count, [new RowError(0, null, [ex.Message], [])]));
                }
            }
            if (dryRun) await tx.RollbackAsync(ct); else await tx.CommitAsync(ct);
            log.LogInformation("Import set {Mode} files={Files} source={Source} rejected={Rejected}",
                dryRun ? "validate" : "commit", results.Count, source, results.Sum(x => x.Rejected));
            return results.ToArray();
        }
        finally
        {
            _shared = null;
            _rehearsal = false;
            _importHospital = null;
        }
    }

    public const int ImportPageRows = 50_000;

    public async Task<ImportResult> SaveAsync(EntitySpec spec, Principal p, IReadOnlyList<Dictionary<string, string>> rows, bool dryRun, string source, CancellationToken ct)
    {
        if (!p.Can(spec.WritePermission)) throw AppError.Forbidden(spec.WritePermission);
        if (rows.Count <= ImportPageRows)
            return await SavePageAsync(spec, p, rows, dryRun, source, ct);

        ImportResult? acc = null;
        for (var i = 0; i < rows.Count; i += ImportPageRows)
        {
            var n = Math.Min(ImportPageRows, rows.Count - i);
            IReadOnlyList<Dictionary<string, string>> page = rows is List<Dictionary<string, string>> list
                ? list.GetRange(i, n)
                : rows.Skip(i).Take(n).ToList();
            var r = await SavePageAsync(spec, p, page, dryRun, source, ct);
            acc = acc is null ? r : MergePages(acc, r);
        }
        return acc!;
    }

    private async Task<ImportResult> SavePageAsync(EntitySpec spec, Principal p, IReadOnlyList<Dictionary<string, string>> rows, bool dryRun, string source, CancellationToken ct)
    {
        var method = typeof(DataHubService).GetMethod(nameof(SaveOf), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(spec.Type);
        return await (Task<ImportResult>)method.Invoke(this, [spec, p, rows, dryRun, source, ct])!;
    }

    private static ImportResult MergePages(ImportResult a, ImportResult b) =>
        new(a.Entity, b.BatchId ?? a.BatchId, a.DryRun, a.Rows + b.Rows, a.Created + b.Created, a.Updated + b.Updated,
            a.Unchanged + b.Unchanged, a.Rejected + b.Rejected, a.Rejects.Concat(b.Rejects).OrderBy(r => r.Row).Take(100).ToArray());

    private sealed record Pending(int Row, string Key, object Entity, bool IsNew, Dictionary<string, string> Values, Dictionary<string, object?>? Before);

    private async Task<ImportResult> SaveOf<T>(EntitySpec spec, Principal p, IReadOnlyList<Dictionary<string, string>> rows, bool dryRun, string source, CancellationToken ct) where T : class, new()
    {
        await using var db = await Db(ct);
        var desc = Describe(spec);
        var hospitals = await db.Hospitals.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.Code, ct);
        var lookups = new Dictionary<string, Dictionary<string, RefTarget>>();
        foreach (var r in spec.Refs) lookups[r.Column] = await CodesAsync(db, r.Target, r.TargetKey, hospitals, ct);

        var usesDefinition = spec.ExtraColumns?.Contains("procedureCode") == true;
        Dictionary<string, RefTarget>? procLookup = null, verLookup = null;
        Dictionary<(Guid, Guid), Guid>? definitions = null;
        var newDefinitions = new List<CostingDefinition>();
        if (usesDefinition)
        {
            procLookup = await CodesAsync(db, typeof(Procedure), "Code", hospitals, ct);
            verLookup = await CodesAsync(db, typeof(CostingVersion), "Code", hospitals, ct);
            definitions = await db.CostingDefinitions.AsNoTracking().ToDictionaryAsync(x => (x.ProcedureId, x.CostingVersionId), x => x.Id, ct);
        }
        var publishedVersions = await db.CostingVersions.AsNoTracking().Where(v => v.Status == "PUBLISHED").Select(v => v.Id).ToListAsync(ct);

        var keyProps = spec.Key.Select(k => spec.Type.GetProperty(k)!).ToArray();
        string KeyOf(object e) => string.Join("|", keyProps.Select(k => Format(k.GetValue(e)).ToUpperInvariant()));
        foreach (var row in rows)
            if (row.TryGetValue("encounterNo", out var rawNo) && !string.IsNullOrWhiteSpace(rawNo))
            {
                var (localNo, named) = HisImportPack.LocalEncounterNo(rawNo.Trim(), hospitals.Values);
                row["encounterNo"] = localNo;
                if (named is not null) row["__encounterHospital"] = named;
            }
        // Monthly tables run to millions of rows: load only the rows this file can match.
        var matchOn = spec.IsTransaction ? keyProps.LastOrDefault(k => k.PropertyType == typeof(string)) : null;
        var candidates = matchOn is null ? db.Set<T>().IgnoreQueryFilters() : CandidatesOf<T>(db, matchOn.Name, rows);
        var existing = (await candidates.ToListAsync(ct)).GroupBy(e => KeyOf(e)).ToDictionary(g => g.Key, g => (object)g.First());

        var groupProps = spec.VersionGroup?.Select(g => spec.Type.GetProperty(g)!).ToArray();
        string GroupOf(object e) => string.Join("|", groupProps!.Select(k => Format(k.GetValue(e))));
        var versions = groupProps is null ? null
            : existing.Values.Cast<IEffectiveDated>().GroupBy(e => GroupOf(e)).ToDictionary(g => g.Key, g => g.OrderBy(x => x.EffectiveFrom).ToList());

        var scalars = Scalars(spec).ToDictionary(x => Camel(x.Name), StringComparer.OrdinalIgnoreCase);
        var rejects = new List<RowError>();
        var pending = new List<Pending>();
        var fellBack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merges = new List<(Guid HospitalId, string AssessmentNo, string Key)>();
        var refunds = new List<(Guid HospitalId, string SourceRef, string Key)>();
        var marksChildLines = spec.Type == typeof(InvoicePackageItem) || spec.Type == typeof(EncounterService) || spec.Type == typeof(RevenueTransaction);
        var replaced = spec.Type == typeof(Encounter) ? await ReplacedErEncountersAsync(db, rows, ct) : [];
        var seen = new HashSet<string>();
        var unchanged = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var values = rows[i];
            var rowNo = values.TryGetValue("__row", out var rn) && int.TryParse(rn, out var n) ? n : i + 1;
            var errors = new List<string>();
            var entity = new T();
            var encounterHospital = values.GetValueOrDefault("__encounterHospital");

            // References by code.
            foreach (var r in spec.Refs)
            {
                var code = values.GetValueOrDefault(r.Column)?.Trim();
                var prop = spec.Type.GetProperty(r.FkProperty)!;
                if (string.IsNullOrEmpty(code))
                {
                    if (!r.Optional) errors.Add($"{r.Column} is required.");
                    continue;
                }
                var hospital = values.GetValueOrDefault("hospitalCode")?.Trim();
                if (string.IsNullOrEmpty(hospital)) hospital = _importHospital ?? encounterHospital;
                // A costing version belongs to the hospital of the procedure bound to it.
                if (r.Target == typeof(CostingVersion) && lookups.TryGetValue("procedureCode", out var procCodes)
                    && TryResolve(procCodes, values.GetValueOrDefault("procedureCode")?.Trim() ?? "", hospital, out var boundProc))
                    hospital = boundProc.HospitalCode ?? hospital;
                // A driver volume names no hospital: its period belongs to the driver's.
                if (r.Target == typeof(Period) && string.IsNullOrEmpty(hospital) && lookups.TryGetValue("driverCode", out var driverCodes)
                    && TryResolve(driverCodes, values.GetValueOrDefault("driverCode")?.Trim() ?? "", null, out var boundDriver))
                    hospital = boundDriver.HospitalCode;
                var found = TryResolve(lookups[r.Column], code, hospital, out var target)
                    && (r.Target != typeof(Encounter) && r.Target != typeof(Patient) && r.Target != typeof(CostingVersion) && r.Target != typeof(Period)
                        || string.IsNullOrEmpty(hospital) || string.Equals(target.HospitalCode, hospital, StringComparison.OrdinalIgnoreCase));
                if (!found && _importHospital is not null && HisImportPack.UnmappedFallback(spec.Name, r.Column) is { } fallback)
                {
                    found = TryResolve(lookups[r.Column], fallback, hospital, out target);
                    if (found) fellBack.Add($"{r.Column} {code}");
                }
                if (!found)
                {
                    if (!r.Optional) errors.Add($"{r.Column} '{code}' does not exist{(r.Target == typeof(CostingVersion) && !string.IsNullOrEmpty(hospital) ? $" for {hospital}" : "")}. Load the {r.Target.Name} first.");
                    continue;
                }
                if (target.HospitalCode is { } hc && !p.HasHospital(hc)) { errors.Add($"{r.Column} '{code}' belongs to {hc}, outside your scope."); continue; }
                prop.SetValue(entity, target.Id);
            }
            if (usesDefinition)
            {
                var pc = values.GetValueOrDefault("procedureCode")?.Trim() ?? "";
                var vc = values.GetValueOrDefault("costingVersionCode")?.Trim() ?? "";
                if (!procLookup!.TryGetValue(pc, out var proc)) errors.Add($"procedureCode '{pc}' does not exist.");
                else if (!TryResolve(verLookup!, vc, proc.HospitalCode, out var ver) || ver.HospitalCode != proc.HospitalCode) errors.Add($"costingVersionCode '{vc}' does not exist for {proc.HospitalCode}. Load the CostingVersion for that hospital first.");
                else
                {
                    var exists = definitions!.TryGetValue((proc.Id, ver.Id), out var defId);
                    // A published version is never changed (FR-10.5). The one thing allowed is a
                    // recipe for a procedure that has none under it yet — a newly onboarded
                    // hospital's catalogue — because that alters no cost already calculated.
                    if (publishedVersions.Contains(ver.Id) && exists && !newDefinitions.Any(d => d.Id == defId))
                    {
                        errors.Add($"{pc} already has a recipe under published version {vc}, which cannot change (FR-10.5). Put the change in a new costing version.");
                        goto validated;
                    }
                    if (!exists)
                    {
                        var def = new CostingDefinition { Id = IdGen.Sequential(), ProcedureId = proc.Id, CostingVersionId = ver.Id };
                        newDefinitions.Add(def);
                        definitions[(proc.Id, ver.Id)] = defId = def.Id;
                    }
                    spec.Type.GetProperty("DefinitionId")!.SetValue(entity, defId);
                }
            }
            validated:
            if (values.TryGetValue("hospitalCode", out var hcode) && !string.IsNullOrEmpty(hcode) && !p.HasHospital(hcode))
                errors.Add($"Hospital {hcode} is outside your scope.");
            if (spec.Refs.All(r => r.Column != "hospitalCode") && !p.AllHospitals && spec.Group is "Organisation" or "Financial" or "Catalogue" &&
                spec.Name is "ServiceLine" or "GLAccount" or "Drg" or "Payer" or "CostingVersion" or "Hospital")
                errors.Add($"{spec.Name} is shared by the whole group; only a group-wide user can change it.");

            // Scalar columns.
            var col = desc.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, prop) in scalars)
            {
                var raw = values.GetValueOrDefault(name)?.Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    if (col[name].Required) errors.Add($"{name} is required.");
                    continue;
                }
                if (TryParse(prop, raw, out var parsed, out var problem))
                {
                    if (col[name].Options is { } opts && !opts.Contains(parsed as string))
                        errors.Add($"{name} '{raw}' is not one of {string.Join(", ", opts)}.");
                    else prop.SetValue(entity, parsed);
                }
                else errors.Add($"{name}: {problem}");
            }

            ValidateEntity(spec, entity, values, errors, publishedVersions);
            if (errors.Count > 0) { rejects.Add(new RowError(rowNo, null, errors.ToArray(), values)); continue; }

            var key = KeyOf(entity);
            if (entity is RefundTransaction refund) refunds.Add((refund.HospitalId, refund.SourceRef, key));
            if (entity is Encounter merged && !string.IsNullOrWhiteSpace(merged.SourceAssessmentNo) && merged.SourceAssessmentNo != merged.EncounterNo)
            {
                if (replaced.TryGetValue((merged.HospitalId, merged.SourceAssessmentNo), out var old) && old.Costed)
                {
                    rejects.Add(new RowError(rowNo, key, [$"ER encounter {merged.SourceAssessmentNo} merges into {merged.EncounterNo}, but it is costed in a calculation run. Delete that run first."], values));
                    continue;
                }
                merges.Add((merged.HospitalId, merged.SourceAssessmentNo, key));
            }
            if (!seen.Add(key)) { rejects.Add(new RowError(rowNo, key, ["The same key appears earlier in this file."], values)); continue; }

            if (existing.TryGetValue(key, out var current))
            {
                // An appointment loaded as OP becomes ER once its ER assessment is linked; anything else would overwrite a different visit.
                if (current is Encounter was && entity is Encounter now && !string.Equals(was.EncType, now.EncType, StringComparison.OrdinalIgnoreCase)
                    && !(was.EncType == "OP" && now.EncType == "ER"))
                {
                    rejects.Add(new RowError(rowNo, key, [$"Encounter {now.EncounterNo} already exists as {was.EncType}; it would be overwritten by this {now.EncType} row."], values));
                    continue;
                }
                if (spec.Versioned)
                {
                    if (SameValues(spec, current, entity, values)) { unchanged++; continue; }
                    rejects.Add(new RowError(rowNo, key, [$"A version effective from {Format(((IEffectiveDated)current).EffectiveFrom)} already exists and is never edited in place. Send the change with a later effectiveFrom; the current version is closed the day before."], values));
                    continue;
                }
                if (current is CostingVersion cv && cv.Status == "PUBLISHED")
                {
                    rejects.Add(new RowError(rowNo, key, ["A published costing version is never edited (FR-10.5)."], values));
                    continue;
                }
                if (current.GetType().GetProperty("Status")?.GetValue(current) is string st
                    && st is "PUBLISHED" or "RETIRED" or "REVIEW" or "APPROVED" or "FINANCE_APPROVED" or "CLINICAL_APPROVED")
                {
                    rejects.Add(new RowError(rowNo, key, [$"A {st} row is never overwritten by import. Change it through Governance, or send a later effectiveFrom for a versioned table."], values));
                    continue;
                }
                var before = Snapshot(spec, current);
                var changed = false;
                foreach (var r in spec.Refs)
                {
                    if (string.IsNullOrEmpty(values.GetValueOrDefault(r.Column))) continue;
                    var prop = spec.Type.GetProperty(r.FkProperty)!;
                    if (!Equals(prop.GetValue(current), prop.GetValue(entity))) { prop.SetValue(current, prop.GetValue(entity)); changed = true; }
                }
                foreach (var (name, prop) in scalars)
                {
                    if (string.IsNullOrEmpty(values.GetValueOrDefault(name)?.Trim())) continue; // empty cell = leave unchanged
                    if (!Equals(prop.GetValue(current), prop.GetValue(entity))) { prop.SetValue(current, prop.GetValue(entity)); changed = true; }
                }
                if (current is Period edited) edited.Days = edited.EndDate.DayNumber - edited.StartDate.DayNumber + 1;
                if (current is User cu && values.GetValueOrDefault("password") is { Length: > 0 } pw) { cu.PasswordHash = new PasswordHasher<User>().HashPassword(cu, pw); changed = true; }
                if (!changed) { unchanged++; continue; }
                pending.Add(new Pending(rowNo, key, current, false, values, before));
            }
            else
            {
                spec.Type.GetProperty("Id")!.SetValue(entity, IdGen.Sequential());
                Stamp(entity, values, errors);
                if (errors.Count > 0) { rejects.Add(new RowError(rowNo, key, errors.ToArray(), values)); continue; }
                if (versions is not null)
                {
                    var eff = (IEffectiveDated)entity;
                    var list = versions.TryGetValue(GroupOf(entity), out var l) ? l : versions[GroupOf(entity)] = [];
                    // CR-9: the version in force when this one starts is closed the day before; a
                    // version that would start before or inside an older one is refused.
                    var overlapping = list.Where(v => v.EffectiveFrom >= eff.EffectiveFrom).ToList();
                    if (overlapping.Count > 0)
                    {
                        rejects.Add(new RowError(rowNo, key, [$"A version already starts on or after {Format(eff.EffectiveFrom)} ({Format(overlapping[0].EffectiveFrom)}). Versions are added in date order."], values));
                        continue;
                    }
                    if (list.LastOrDefault() is { } prev && (prev.EffectiveTo is null || prev.EffectiveTo >= eff.EffectiveFrom))
                    {
                        spec.Type.GetProperty("EffectiveTo")!.SetValue(prev, eff.EffectiveFrom.AddDays(-1));
                        if (spec.Type.GetProperty("Version") is { } vp && vp.GetValue(prev) is int pv) vp.SetValue(entity, pv + 1);
                        if (db.Entry(prev).State == EntityState.Unchanged) db.Entry(prev).State = EntityState.Modified;
                    }
                    list.Add(eff);
                }
                existing[key] = entity;
                pending.Add(new Pending(rowNo, key, entity, true, values, null));
            }
        }

        if (fellBack.Count > 0)
            log.LogWarning("Import {Entity}: {Count} codes not in the catalogue were sent to Unmapped: {Codes}", spec.Name, fellBack.Count, string.Join(", ", fellBack.Take(50)));
        var created = pending.Count(x => x.IsNew);
        var updated = pending.Count(x => !x.IsNew);
        Guid? batchId = null;
        if (!dryRun && (pending.Count > 0 || rejects.Count > 0 || merges.Count > 0 || refunds.Count > 0 || spec.Type == typeof(Payer) || marksChildLines))
        {
            foreach (var def in newDefinitions) db.CostingDefinitions.Add(def);
            foreach (var x in pending.Where(x => x.IsNew)) db.Add(x.Entity);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // The database refused the set (a check constraint, a unique key). Find the rows
                // responsible one by one, so the rest still load (BRD 22.2: quarantine per row).
                db.ChangeTracker.Clear();
                var (ok, bad) = await SaveOneByOneAsync(spec, pending, newDefinitions, ct);
                created = ok.Count(x => x.IsNew);
                updated = ok.Count(x => !x.IsNew);
                rejects.AddRange(bad);
                if (ok.Count == 0 && bad.Count == 0) throw new AppError("SAVE_FAILED", Inner(ex), 400);
            }

            var refused = rejects.Select(r => r.Key).ToHashSet();
            await MergeReplacedErEncountersAsync(merges.Where(m => !refused.Contains(m.Key)).ToList(), replaced, existing, ct);
            await RemoveRefundsFromRevenueAsync(refunds.Where(r => !refused.Contains(r.Key)).ToList(), ct);
            if (spec.Type == typeof(Payer)) await MapPayerPartiesAsync(ct);
            if (marksChildLines) await MarkChildInvoiceLinesAsync(ct);

            await using var log = await Db(ct);
            var batch = new ImportBatch
            {
                Id = IdGen.Sequential(), SourceSystem = source, Entity = spec.Name, RowsAccepted = created + updated + unchanged, RowsRejected = rejects.Count,
                RejectReasons = rejects.Count == 0 ? null : JsonSerializer.Serialize(rejects.Take(1000).Select(Clean), Json), LoadedAt = DateTime.UtcNow, LoadedBy = p.Email,
            };
            log.ImportBatches.Add(batch);
            await log.SaveChangesAsync(ct);
            batchId = batch.Id;

            if (_rehearsal) { }
            else if (pending.Count == 1 && source == "DATA-ENTRY")
            {
                var x = pending[0];
                await audit.WriteAsync(p, x.IsNew ? "CREATE" : "UPDATE", spec.Name, spec.Type.GetProperty("Id")!.GetValue(x.Entity)?.ToString(), x.Key,
                    x.Before, Snapshot(spec, x.Entity), ct: ct);
            }
            else
                await audit.WriteAsync(p, "CREATE", "ImportBatch", batch.Id.ToString(), $"{spec.Name} upload",
                    metadata: new { entity = spec.Name, source, rows = rows.Count, created, updated, unchanged, rejected = rejects.Count }, ct: ct);
        }

        return new ImportResult(spec.Name, batchId, dryRun, rows.Count, created, updated, unchanged, rejects.Count,
            rejects.OrderBy(r => r.Row).Take(100).Select(Clean).ToArray());
    }

    private async Task<(List<Pending> Ok, List<RowError> Bad)> SaveOneByOneAsync(EntitySpec spec, List<Pending> pending, List<CostingDefinition> defs, CancellationToken ct)
    {
        var ok = new List<Pending>();
        var bad = new List<RowError>();
        if (defs.Count > 0)
        {
            await using var d0 = await Db(ct);
            d0.CostingDefinitions.AddRange(defs);
            await d0.SaveChangesAsync(ct);
        }
        foreach (var x in pending)
        {
            await using var db = await Db(ct);
            if (x.IsNew) db.Add(x.Entity); else db.Update(x.Entity);
            try { await db.SaveChangesAsync(ct); ok.Add(x); }
            catch (DbUpdateException ex) { bad.Add(new RowError(x.Row, x.Key, [$"Refused by the database: {Inner(ex)}"], x.Values)); }
        }
        return (ok, bad);
    }

    private static string Inner(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null) e = e.InnerException;
        var msg = e.Message;
        var i = msg.IndexOf("The statement has been terminated", StringComparison.Ordinal);
        return (i > 0 ? msg[..i] : msg).Trim();
    }

    /// <summary>Passwords never reach the reject log or the response.</summary>
    private static RowError Clean(RowError r) =>
        r with { Values = r.Values.Where(kv => kv.Key is not ("password" or "__row" or "__encounterHospital")).ToDictionary(kv => kv.Key, kv => kv.Value) };

    private static Dictionary<string, object?> Snapshot(EntitySpec spec, object e) =>
        Scalars(spec).ToDictionary(p => Camel(p.Name), p => p.GetValue(e));

    private static bool SameValues(EntitySpec spec, object a, object b, Dictionary<string, string> values) =>
        Scalars(spec).Where(p => !string.IsNullOrEmpty(values.GetValueOrDefault(Camel(p.Name)))).All(p => Equals(p.GetValue(a), p.GetValue(b)));

    /// <summary>Fields the system owns rather than the file: creation stamps and passwords.</summary>
    private static void Stamp(object entity, Dictionary<string, string> values, List<string> errors)
    {
        foreach (var name in new[] { "CreatedAt" })
            if (entity.GetType().GetProperty(name) is { } p && p.PropertyType == typeof(DateTime) && (DateTime)p.GetValue(entity)! == default) p.SetValue(entity, DateTime.UtcNow);
        if (entity is User u)
        {
            var pw = values.GetValueOrDefault("password");
            if (string.IsNullOrEmpty(pw)) errors.Add("password is required for a new user.");
            else if (pw.Length < 10) errors.Add("password must be at least 10 characters.");
            else u.PasswordHash = new PasswordHasher<User>().HashPassword(u, pw);
        }
    }

    private static void ValidateEntity(EntitySpec spec, object entity, Dictionary<string, string> values, List<string> errors, List<Guid> published)
    {
        switch (entity)
        {
            case IEffectiveDated e when e.EffectiveTo is { } to && to < e.EffectiveFrom:
                errors.Add("effectiveTo is before effectiveFrom.");
                break;
        }
        switch (entity)
        {
            case CostingVersion v when v.Status is not ("DRAFT" or "REVIEW"):
                errors.Add("A costing version is created as DRAFT; it is published through Governance, not here.");
                break;
            case User u:
                u.Email = u.Email.Trim().ToLowerInvariant();
                if (Security.Role(u.Role) is null) errors.Add($"role '{u.Role}' is not one of {string.Join(", ", Security.Roles.Select(r => r.Code))}.");
                break;
            case GLMapping m when m.Weight <= 0 || m.Weight > 1:
                errors.Add("weight must be greater than 0 and at most 1.");
                break;
            case AllocationRule r when r.Stage is < 1 or > 3:
                errors.Add("stage must be 1, 2 or 3.");
                break;
            case Period pe when pe.EndDate < pe.StartDate:
                errors.Add("endDate is before startDate.");
                break;
            case Period pe:
                pe.Days = pe.EndDate.DayNumber - pe.StartDate.DayNumber + 1;
                break;
            case ActivityTransaction t when t.Quantity < 0:
                errors.Add("quantity is negative (DQ-COST-NEGATIVE would block the run).");
                break;
        }
    }

    private static bool TryParse(PropertyInfo prop, string raw, out object? value, out string problem)
    {
        var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        problem = "";
        value = null;
        var inv = CultureInfo.InvariantCulture;
        if (t == typeof(string)) { value = raw; return true; }
        if (t == typeof(int))
        {
            if (int.TryParse(raw, NumberStyles.Integer, inv, out var i)) { value = i; return true; }
            problem = $"'{raw}' is not a whole number."; return false;
        }
        if (t == typeof(decimal))
        {
            if (raw.Contains(',')) { problem = $"'{raw}' has a comma; use a dot for decimals and no thousands separators."; return false; }
            if (decimal.TryParse(raw, NumberStyles.Number, inv, out var d)) { value = d; return true; }
            problem = $"'{raw}' is not a number."; return false;
        }
        if (t == typeof(bool))
        {
            switch (raw.ToLowerInvariant()) { case "true" or "yes" or "y" or "1": value = true; return true; case "false" or "no" or "n" or "0": value = false; return true; }
            problem = $"'{raw}' is not true or false."; return false;
        }
        if (t == typeof(DateOnly))
        {
            if (DateOnly.TryParseExact(raw, ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy"], inv, DateTimeStyles.None, out var d)) { value = d; return true; }
            if (DateTime.TryParse(raw, inv, DateTimeStyles.AdjustToUniversal, out var dt)) { value = DateOnly.FromDateTime(dt); return true; }
            problem = $"'{raw}' is not a date (use yyyy-mm-dd)."; return false;
        }
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
        {
            if (DateTime.TryParseExact(raw, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "dd/MM/yyyy HH:mm", "dd/MM/yyyy"],
                    inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                || DateTime.TryParse(raw, inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                value = t == typeof(DateTime) ? (object)DateTime.SpecifyKind(dt, DateTimeKind.Utc) : new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            }
            problem = $"'{raw}' is not a date and time (use yyyy-mm-dd hh:mm)."; return false;
        }
        problem = $"unsupported column type {t.Name}.";
        return false;
    }

    // ── Import history ─────────────────────────────────────────────────────────

    public async Task<ImportBatchDto[]> HistoryAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ImportBatches.AsNoTracking().OrderByDescending(b => b.LoadedAt).Take(200)
            .Select(b => new ImportBatchDto(b.Id, b.Entity, b.SourceSystem, b.RowsAccepted, b.RowsRejected, b.LoadedAt, b.LoadedBy, b.RejectReasons != null)).ToArrayAsync(ct);
    }

    /// <summary>The rejected rows of a batch as CSV, with the reason in the first column, ready to fix and re-upload.</summary>
    public async Task<(string Entity, string Csv)> RejectsCsvAsync(Guid batchId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var b = await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == batchId, ct) ?? throw AppError.NotFound("Import batch");
        var rejects = b.RejectReasons is null ? [] : JsonSerializer.Deserialize<RowError[]>(b.RejectReasons, Json) ?? [];
        var spec = EntitySpecs.Find(b.Entity);
        var cols = spec is null ? rejects.SelectMany(r => r.Values.Keys).Distinct().ToArray() : Describe(spec).Columns.Select(c => c.Name).Where(c => c != "password").ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "rejectReason", "sourceRow" }.Concat(cols).Select(Csv.Escape)));
        foreach (var r in rejects)
            sb.AppendLine(string.Join(",", new[] { string.Join(" ", r.Errors), r.Row.ToString() }.Concat(cols.Select(c => r.Values.GetValueOrDefault(c) ?? "")).Select(Csv.Escape)));
        return (b.Entity, sb.ToString());
    }
}
