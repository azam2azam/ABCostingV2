using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Fixture;

/// <summary>
/// Loads the reference implementation's exported fixture (BRD 23.4) into SQL Server.
///
/// Identifiers: the reference uses CUID strings; each becomes a stable name-based GUID, so a
/// reload produces the same keys and references stay intact. Money and quantities arrive as
/// JSON numbers and are parsed straight to decimal, then rounded to the column's declared scale.
/// Every table load is recorded in ImportBatch with its accepted and rejected counts.
/// </summary>
public sealed class FixtureImporter(VeritaDbContext db, string demoPassword)
{
    /// <summary>Load order; foreign keys are re-validated after the whole set is in (see below).</summary>
    public static readonly string[] Tables =
    [
        "Hospital", "Facility", "ServiceLine", "Department", "CostCenter", "GLAccount", "Period", "GLPosting",
        "CostPool", "GLMapping", "CostDriver", "DriverVolume", "Resource", "ResourceRate", "ResourceCapacity", "Provider",
        "Activity", "ActivityResource", "ActivityDriver", "Procedure", "ProcedureRate", "CostingVersion", "CostingDefinition",
        "ProcedureActivityLine", "ProcedureResourceLine", "Package", "PackageComponent", "DRG", "Payer", "Patient", "Encounter",
        "EncounterService", "ActivityTransaction", "RevenueTransaction", "AllocationRule", "AllocationRuleTarget",
        "User", "ApprovalRequest", "AuditLog",
    ];

    private static readonly Dictionary<string, Type> EntityTypes =
        typeof(Hospital).Assembly.GetTypes().Where(t => t.Namespace == typeof(Hospital).Namespace && t.IsClass && !t.IsAbstract)
            .ToDictionary(t => t.Name == "Drg" ? "DRG" : t.Name, t => t);

    public static Guid MapId(string cuid) => IdGen.FromKey(cuid);

    /// <summary>The reference's identifiers are 25-character CUIDs; anything else is a literal.</summary>
    public static bool LooksLikeCuid(string s) => s.Length == 25 && s[0] == 'c' && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

    public static IEnumerable<JsonElement> ReadNdjson(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    public async Task<IReadOnlyList<(string Table, int Rows)>> ImportAsync(string fixtureDir, Action<string>? log = null, CancellationToken ct = default)
    {
        var writer = new BulkWriter(db.Model);
        var hasher = new PasswordHasher<User>();
        var demoHash = hasher.HashPassword(new User(), demoPassword);
        var conn = (SqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var loaded = new List<(string, int)>();
        foreach (var table in Tables)
        {
            var type = EntityTypes[table];
            var path = Path.Combine(fixtureDir, "source", $"{table}.ndjson.gz");
            var rows = ReadNdjson(path).Select(e => Map(type, e, demoHash)).ToList();

            var typed = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
            foreach (var r in rows) typed.Add(r);
            var method = typeof(BulkWriter).GetMethod(nameof(BulkWriter.WriteAsync))!.MakeGenericMethod(type);
            // Constraint checking is deferred: the catalogue has legitimate cycles (pool -> primary
            // driver -> pool), so every constraint is re-validated once the set is complete.
            await (Task)method.Invoke(writer, [conn, null, typed, false, ct])!;
            loaded.Add((table, rows.Count));
            log?.Invoke($"  {table,-24} {rows.Count,8:N0} rows");
        }

        // WITH CHECK makes SQL Server validate the deferred foreign keys and checks and mark them
        // trusted again; a fixture that breaks referential integrity fails here, loudly.
        foreach (var table in db.Model.GetEntityTypes().Select(t => t.GetTableName()).Distinct())
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE [{table}] WITH CHECK CHECK CONSTRAINT ALL", ct);

        db.ImportBatches.AddRange(loaded.Select(l => new ImportBatch
        {
            Id = IdGen.Sequential(), SourceSystem = "VERITA-REFERENCE-FIXTURE", Entity = l.Item1, RowsAccepted = l.Item2, RowsRejected = 0,
            LoadedAt = DateTime.UtcNow, LoadedBy = "verita-tools",
        }));
        await db.SaveChangesAsync(ct);
        return loaded;
    }

    private object Map(Type type, JsonElement json, string demoHash)
    {
        var entity = Activator.CreateInstance(type)!;
        var et = db.Model.FindEntityType(type)!;
        foreach (var jp in json.EnumerateObject())
        {
            var prop = type.GetProperty(jp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var v = jp.Value;
            if (v.ValueKind == JsonValueKind.Null) { prop.SetValue(entity, null); continue; }

            var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object? value = target switch
            {
                _ when target == typeof(Guid) => MapId(v.GetString()!),
                _ when target == typeof(string) => MapString(type, prop.Name, v),
                _ when target == typeof(decimal) => Round(v.GetDecimal(), et.FindProperty(prop.Name)?.GetScale() ?? 4),
                _ when target == typeof(int) => v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : int.Parse(v.GetString()!),
                _ when target == typeof(bool) => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : v.GetInt32() == 1,
                _ when target == typeof(DateOnly) => DateOnly.FromDateTime(ParseUtc(v.GetString()!)),
                _ when target == typeof(DateTime) => ParseUtc(v.GetString()!),
                _ when target == typeof(DateTimeOffset) => new DateTimeOffset(ParseUtc(v.GetString()!)),
                _ => throw new InvalidOperationException($"No mapping for {type.Name}.{prop.Name} ({target.Name})"),
            };
            prop.SetValue(entity, value);
        }
        if (entity is User u) u.PasswordHash = demoHash;
        return entity;
    }

    private static string MapString(Type type, string prop, JsonElement v)
    {
        var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        // Polymorphic references held as text: a lever's target or an audit entry's entity.
        if ((type == typeof(ScenarioLever) && prop == nameof(ScenarioLever.TargetId)) ||
            (type == typeof(AuditLog) && prop == nameof(AuditLog.EntityId)))
            return LooksLikeCuid(s) ? MapId(s).ToString() : s;
        return s;
    }

    private static decimal Round(decimal d, int scale) => Math.Round(d, scale, MidpointRounding.AwayFromZero);

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
