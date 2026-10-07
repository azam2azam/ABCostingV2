using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verita.Application;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.DataHub;

public sealed record ColumnSpec(string Name, string Kind, bool Required, bool Key, string[]? Options, string? RefEntity, string? Description);
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
public sealed class DataHubService(IDbContextFactory<VeritaDbContext> factory, IAuditWriter audit)
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
            cols.Add(new(Camel(p.Name), kind, required, spec.Key.Contains(p.Name), options, null,
                hasDefault && !required ? $"Defaults to {Convert.ToString(value, CultureInfo.InvariantCulture)}" : null));
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
        var hasHospital = typeof(T).GetProperty("HospitalId") is not null;
        var rows = hasHospital
            ? await db.Set<T>().AsNoTracking().Select(e => new { Id = EF.Property<Guid>(e, "Id"), K = EF.Property<string>(e, key), H = EF.Property<Guid>(e, "HospitalId") }).ToListAsync(ct)
            : (await db.Set<T>().AsNoTracking().Select(e => new { Id = EF.Property<Guid>(e, "Id"), K = EF.Property<string>(e, key) }).ToListAsync(ct))
                .Select(x => new { x.Id, x.K, H = Guid.Empty }).ToList();
        var dict = new Dictionary<string, RefTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) dict.TryAdd(r.K, new RefTarget(r.Id, r.H == Guid.Empty ? null : hospitals.GetValueOrDefault(r.H)));
        return dict;
    }

    public async Task<Dictionary<string, string>[]> LookupAsync(EntitySpec spec, Principal p, CancellationToken ct)
    {
        var page = await ListAsync(spec, p, null, 1, 5000, ct);
        var key = Describe(spec).KeyColumns[0];
        return page.Rows.Select(r => new Dictionary<string, string>
        {
            ["code"] = r.GetValueOrDefault(key) ?? "",
            ["name"] = r.GetValueOrDefault("name") ?? r.GetValueOrDefault("fullName") ?? r.GetValueOrDefault(key) ?? "",
        }).ToArray();
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
            reverse[r.Column] = (await CodesAsync(db, r.Target, r.TargetKey, hospitals, ct)).ToDictionary(kv => kv.Value.Id, kv => kv.Key);
        Dictionary<Guid, (string Proc, string Ver)>? defs = null;
        if (spec.ExtraColumns?.Contains("procedureCode") == true)
        {
            var procs = await db.Procedures.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
            var vers = await db.CostingVersions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
            defs = await db.CostingDefinitions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => (procs[x.ProcedureId], vers[x.CostingVersionId]), ct);
        }

        var all = await db.Set<T>().AsNoTracking().ToListAsync(ct);
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
        var key = d.KeyColumns[0].Split('+')[0];
        rows = rows.OrderBy(r => r.GetValueOrDefault(key), StringComparer.OrdinalIgnoreCase).ToList();
        pageSize = Math.Clamp(pageSize, 1, 5000);
        page = Math.Max(1, page);
        return new RowPage(spec.Name, d.Columns.Where(c => c.Kind != "password").Select(c => c.Name).ToArray(),
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), rows.Count, page, pageSize);
    }

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
    public async Task<ImportResult[]> ImportSetAsync(IReadOnlyList<(EntitySpec Spec, List<Dictionary<string, string>> Rows)> files, Principal p, bool dryRun, string source, CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        await using var conn = new SqlConnection(probe.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        _shared = (conn, tx);
        _rehearsal = dryRun;
        var results = new List<ImportResult>();
        try
        {
            foreach (var (spec, rows) in files)
            {
                try { var r = await SaveAsync(spec, p, rows, false, source, ct); results.Add(r with { DryRun = dryRun, BatchId = dryRun ? null : r.BatchId }); }
                catch (AppError ex) when (ex.Code is "FORBIDDEN")
                {
                    results.Add(new ImportResult(spec.Name, null, dryRun, rows.Count, 0, 0, 0, rows.Count, [new RowError(0, null, [ex.Message], [])]));
                }
            }
            if (dryRun) await tx.RollbackAsync(ct); else await tx.CommitAsync(ct);
            return results.ToArray();
        }
        finally
        {
            _shared = null;
            _rehearsal = false;
        }
    }

    public async Task<ImportResult> SaveAsync(EntitySpec spec, Principal p, IReadOnlyList<Dictionary<string, string>> rows, bool dryRun, string source, CancellationToken ct)
    {
        if (!p.Can(spec.WritePermission)) throw AppError.Forbidden(spec.WritePermission);
        var method = typeof(DataHubService).GetMethod(nameof(SaveOf), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(spec.Type);
        return await (Task<ImportResult>)method.Invoke(this, [spec, p, rows, dryRun, source, ct])!;
    }

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
        var existing = (await db.Set<T>().ToListAsync(ct)).GroupBy(e => KeyOf(e)).ToDictionary(g => g.Key, g => (object)g.First());

        var groupProps = spec.VersionGroup?.Select(g => spec.Type.GetProperty(g)!).ToArray();
        string GroupOf(object e) => string.Join("|", groupProps!.Select(k => Format(k.GetValue(e))));
        var versions = groupProps is null ? null
            : existing.Values.Cast<IEffectiveDated>().GroupBy(e => GroupOf(e)).ToDictionary(g => g.Key, g => g.OrderBy(x => x.EffectiveFrom).ToList());

        var scalars = Scalars(spec).ToDictionary(x => Camel(x.Name), StringComparer.OrdinalIgnoreCase);
        var rejects = new List<RowError>();
        var pending = new List<Pending>();
        var seen = new HashSet<string>();
        var unchanged = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var values = rows[i];
            var rowNo = values.TryGetValue("__row", out var rn) && int.TryParse(rn, out var n) ? n : i + 1;
            var errors = new List<string>();
            var entity = new T();

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
                if (!lookups[r.Column].TryGetValue(code, out var target)) { errors.Add($"{r.Column} '{code}' does not exist. Load the {r.Target.Name} first."); continue; }
                if (target.HospitalCode is { } hc && !p.HasHospital(hc)) { errors.Add($"{r.Column} '{code}' belongs to {hc}, outside your scope."); continue; }
                prop.SetValue(entity, target.Id);
            }
            if (usesDefinition)
            {
                var pc = values.GetValueOrDefault("procedureCode")?.Trim() ?? "";
                var vc = values.GetValueOrDefault("costingVersionCode")?.Trim() ?? "";
                if (!procLookup!.TryGetValue(pc, out var proc)) errors.Add($"procedureCode '{pc}' does not exist.");
                else if (!verLookup!.TryGetValue(vc, out var ver)) errors.Add($"costingVersionCode '{vc}' does not exist.");
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
                spec.Name is "ServiceLine" or "GLAccount" or "Period" or "Drg" or "Payer" or "CostingVersion" or "Hospital")
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
            if (!seen.Add(key)) { rejects.Add(new RowError(rowNo, key, ["The same key appears earlier in this file."], values)); continue; }

            if (existing.TryGetValue(key, out var current))
            {
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

        var created = pending.Count(x => x.IsNew);
        var updated = pending.Count(x => !x.IsNew);
        Guid? batchId = null;
        if (!dryRun && (pending.Count > 0 || rejects.Count > 0))
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
            rejects.OrderBy(r => r.Row).Select(Clean).ToArray());
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
        r with { Values = r.Values.Where(kv => kv.Key is not ("password" or "__row")).ToDictionary(kv => kv.Key, kv => kv.Value) };

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
