using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Verita.Application;
using Verita.Infrastructure.Data;

namespace Verita.Infrastructure.Runs;

public sealed record PeriodFactEntity(
    string Entity, string Label, string Group, long Rows, long EstimatedBytes, long TableRows, long TableBytes,
    bool Deletable, string[] AlsoDeletes, string? Note);

public sealed record PeriodFactRun(string Code, string Status, string? CostingVersion);

public sealed record PeriodFactsDto(
    Guid PeriodId, string Period, string Hospital, DateOnly StartDate, DateOnly EndDate, string Status,
    PeriodFactEntity[] Entities, long TotalRows, long TotalEstimatedBytes, PeriodFactRun[] Runs, string? Blocker);

public sealed record PeriodFactsDeleted(string Entity, long Rows);

public sealed record StorageTable(string Table, string Group, long Rows, long Bytes, long DataBytes, long IndexBytes);
public sealed record StoragePeriodEntity(string Entity, long Rows, long EstimatedBytes);
public sealed record StoragePeriod(Guid Id, string Code, DateOnly StartDate, DateOnly EndDate, string Status, string? Run, string? RunStatus,
    StoragePeriodEntity[] Entities, long FactRows, long FactBytes, long ResultRows, long ResultBytes);
public sealed record StorageFactEntity(string Entity, string Label, string Group);
public sealed record StorageDto(string Hospital, long DatabaseBytes, long UsedBytes, StorageTable[] Tables, StorageFactEntity[] FactEntities, StoragePeriod[] Periods);

/// <summary>
/// The facts that belong to one hospital's period: encounters admitted in it (the engine's rule) with
/// their services, activity, revenue, refunds and package items, plus the period's ledger and driver
/// volumes. Counts, storage estimates, and deletion — only while no calculation run uses them.
/// </summary>
public sealed class PeriodFactsService(IDbContextFactory<VeritaDbContext> factory, IAuditWriter audit, ILogger<PeriodFactsService> log)
{
    private const int DeleteChunk = 20_000;
    private const int DeleteTimeoutSeconds = 3600;

    // Encounters of the period: same membership as RunContextLoader (admit date in the period).
    private const string Enc = "SELECT e.Id FROM [Encounter] e WHERE e.HospitalId = @h AND e.AdmitAt >= @s AND e.AdmitAt < @e";

    private sealed record Def(string Entity, string Label, string Group, string Table, string Where, bool Deletable, string[] AlsoDeletes, string? Note = null);

    private static readonly Def[] Defs =
    [
        new("Encounter", "Encounters", "Clinical", "Encounter",
            "t.HospitalId = @h AND t.AdmitAt >= @s AND t.AdmitAt < @e", true,
            ["EncounterService", "ActivityTransaction", "RevenueTransaction", "RefundTransaction", "InvoicePackageItem", "ConsumptionItem", "StaffTime", "Patient"],
            "Admitted in this period."),
        new("Patient", "Patients", "Clinical", "Patient",
            $"t.Id IN (SELECT x.PatientId FROM [Encounter] x WHERE x.Id IN ({Enc})) AND NOT EXISTS (SELECT 1 FROM [Encounter] o WHERE o.PatientId = t.Id AND o.Id NOT IN ({Enc}))",
            false, [], "Only those with no encounter in another period. Removed with encounters."),
        new("EncounterService", "Encounter services", "Clinical", "EncounterService",
            $"t.EncounterId IN ({Enc})", true, ["ActivityTransaction"], "Also removes activity rows that point to these services."),
        new("ActivityTransaction", "Activity transactions", "Clinical", "ActivityTransaction",
            $"t.EncounterId IN ({Enc})", true, []),
        new("RevenueTransaction", "Revenue transactions", "Financial", "RevenueTransaction",
            $"t.EncounterId IN ({Enc})", true, []),
        new("RefundTransaction", "Refund transactions", "Financial", "RefundTransaction",
            $"t.HospitalId = @h AND (t.EncounterId IN ({Enc}) OR (t.EncounterId IS NULL AND t.RefundDate >= @s AND t.RefundDate < @e))", true, [],
            "On this period's encounters, or dated in it when not linked."),
        new("InvoicePackageItem", "Invoice package items", "Financial", "InvoicePackageItem",
            $"t.HospitalId = @h AND (t.EncounterId IN ({Enc}) OR (t.EncounterId IS NULL AND t.InvoiceDate >= @s AND t.InvoiceDate < @e))", true, [],
            "On this period's encounters, or dated in it when not linked."),
        new("ConsumptionItem", "Consumption items", "Clinical", "ConsumptionItem",
            $"t.HospitalId = @h AND (t.EncounterId IN ({Enc}) OR (t.EncounterId IS NULL AND t.OccurredAt >= @s AND t.OccurredAt < @e))", true, [],
            "Consumables, implants and medicine by item. Unlinked rows count by date."),
        new("StaffTime", "Staff time", "Clinical", "StaffTime",
            $"t.HospitalId = @h AND (t.EncounterId IN ({Enc}) OR (t.EncounterId IS NULL AND t.OccurredAt >= @s AND t.OccurredAt < @e))", true, [],
            "Doctor, nurse and technician minutes. Unlinked rows count by date."),
        new("GLPosting", "GL postings", "Ledger", "GLPosting",
            "t.HospitalId = @h AND t.PeriodId = @p", true, []),
        new("DriverVolume", "Driver volumes", "Ledger", "DriverVolume",
            "t.PeriodId = @p AND t.CostCenterId IN (SELECT c.Id FROM [CostCenter] c WHERE c.HospitalId = @h)", true, []),
    ];

    // Children before parents: NoAction foreign keys.
    private static readonly string[] DeleteOrder =
        ["ActivityTransaction", "EncounterService", "RevenueTransaction", "RefundTransaction", "InvoicePackageItem", "ConsumptionItem", "StaffTime", "GLPosting", "DriverVolume", "Encounter", "Patient"];

    private sealed record Scope(Guid PeriodId, string PeriodCode, DateOnly Start, DateOnly End, string PeriodStatus, Guid HospitalId, string HospitalCode);

    public async Task<PeriodFactsDto> SummaryAsync(Principal p, Guid periodId, string hospital, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await ScopeAsync(db, p, periodId, hospital, ct);
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        var storage = await TableStorageAsync(conn, ct);
        var entities = new List<PeriodFactEntity>();
        foreach (var d in Defs)
        {
            var rows = await ScalarAsync(conn, $"SELECT COUNT_BIG(*) FROM [{d.Table}] t WHERE {d.Where}", s, 300, ct);
            var (tableRows, tableBytes) = storage.GetValueOrDefault(d.Table);
            var est = tableRows > 0 ? (long)Math.Round((double)tableBytes * rows / tableRows) : 0;
            entities.Add(new PeriodFactEntity(d.Entity, d.Label, d.Group, rows, est, tableRows, tableBytes, d.Deletable, d.AlsoDeletes, d.Note));
        }

        var runs = await RunsAsync(db, s, ct);
        return new PeriodFactsDto(s.PeriodId, s.PeriodCode, s.HospitalCode, s.Start, s.End, s.PeriodStatus,
            entities.ToArray(), entities.Sum(x => x.Rows), entities.Sum(x => x.EstimatedBytes), runs, Blocker(s, runs));
    }

    /// <summary>Delete one entity's facts for the period (with what depends on it), or all of them when entity is null.</summary>
    public async Task<PeriodFactsDeleted[]> DeleteAsync(Principal p, Guid periodId, string hospital, string? entity, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await ScopeAsync(db, p, periodId, hospital, ct);

        string[] targets;
        if (string.IsNullOrWhiteSpace(entity))
            targets = DeleteOrder;
        else
        {
            var d = Defs.FirstOrDefault(x => string.Equals(x.Entity, entity, StringComparison.OrdinalIgnoreCase))
                ?? throw AppError.Invalid($"Unknown entity {entity}.");
            if (!d.Deletable) throw AppError.Invalid($"{d.Label} are removed together with encounters.");
            var set = new HashSet<string>([d.Entity, .. d.AlsoDeletes], StringComparer.OrdinalIgnoreCase);
            targets = DeleteOrder.Where(set.Contains).ToArray();
        }

        var runs = await RunsAsync(db, s, ct);
        if (Blocker(s, runs) is { } blocker) throw AppError.Conflict("PERIOD_HAS_RUN", blocker);

        // Patients are chosen before their encounters go; afterwards they have no encounter to find them by.
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        var deleted = new List<PeriodFactsDeleted>();
        List<Guid>? patients = null;
        if (targets.Contains("Patient"))
        {
            patients = [];
            var patientDef = Defs.First(x => x.Entity == "Patient");
            await using var cmd = Command(conn, $"SELECT t.Id FROM [Patient] t WHERE {patientDef.Where}", s, 600);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) patients.Add(r.GetGuid(0));
        }

        foreach (var name in targets)
        {
            long n;
            if (name == "Patient")
                n = await DeletePatientsAsync(conn, patients!, ct);
            else
            {
                var d = Defs.First(x => x.Entity == name);
                // Services only: remove just the activity rows that point to those services.
                var where = name == "ActivityTransaction" && string.Equals(entity, "EncounterService", StringComparison.OrdinalIgnoreCase)
                    ? $"t.EncounterServiceId IN (SELECT es.Id FROM [EncounterService] es WHERE es.EncounterId IN ({Enc}))"
                    : d.Where;
                n = await ChunkedDeleteAsync(conn, d.Table, where, s, ct);
            }
            deleted.Add(new PeriodFactsDeleted(name, n));
            log.LogInformation("Period facts delete {Hospital} {Period} {Entity}: {Rows} rows", s.HospitalCode, s.PeriodCode, name, n);
        }

        await audit.WriteAsync(p, "DELETE", "PeriodFacts", s.PeriodId.ToString(), $"{s.HospitalCode} {s.PeriodCode}{(entity is null ? "" : $" · {entity}")}",
            metadata: new { hospital = s.HospitalCode, period = s.PeriodCode, entity = entity ?? "ALL", deleted }, ct: ct);
        return deleted.ToArray();
    }

    // Calculation output: one run's results, deleted with the run.
    private static readonly HashSet<string> ResultTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CalculationRun", "RunStep", "ActivityCost", "AllocationResult", "CostCenterBalance", "CostComponent", "CostPoolBalance",
        "DataQualityIssue", "DrgCostSummary", "EncounterCost", "PackageCost", "ProcedureCost", "Reconciliation", "ReconciliationLine",
        "ResidualBucket", "ResourceUnitCost", "RunDimensionCost", "RunHisInvoicing", "Variance", "VarianceComponent",
        "Scenario", "ScenarioLever", "ScenarioResult",
    };
    private static readonly HashSet<string> ImportTables = new(StringComparer.OrdinalIgnoreCase) { "ImportFile", "ImportFileContent", "ImportBatch" };

    private static string GroupOf(string table)
    {
        if (table.EndsWith("History", StringComparison.OrdinalIgnoreCase)) return "History";
        if (ResultTables.Contains(table)) return "Calculation results";
        if (ImportTables.Contains(table)) return "Import files";
        var spec = DataHub.EntitySpecs.All.FirstOrDefault(x => string.Equals(x.Type.Name == "Drg" ? "DRG" : x.Type.Name, table, StringComparison.OrdinalIgnoreCase));
        if (spec is not null) return spec.IsTransaction ? "Facts" : "Catalogue";
        return "System";
    }

    /// <summary>
    /// Storage of every table in the database (all hospitals), and the selected hospital's facts and calculation
    /// results per period. Period figures use the same rules as the period detail screen; bytes are estimated from
    /// each table's average row size.
    /// </summary>
    public async Task<StorageDto> StorageAsync(Principal p, string hospital, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hospital)) throw AppError.Invalid("Choose a hospital.");
        if (!p.HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        var tables = new List<StorageTable>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 120;
            cmd.CommandText = """
                SELECT t.name,
                       SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END),
                       SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.used_page_count ELSE 0 END) * CAST(8192 AS bigint),
                       SUM(CASE WHEN ps.index_id > 1 THEN ps.used_page_count ELSE 0 END) * CAST(8192 AS bigint)
                FROM sys.dm_db_partition_stats ps
                JOIN sys.tables t ON t.object_id = ps.object_id
                WHERE t.is_ms_shipped = 0
                GROUP BY t.name
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var (data, index) = (r.GetInt64(2), r.GetInt64(3));
                tables.Add(new StorageTable(name, GroupOf(name), r.GetInt64(1), data + index, data, index));
            }
        }
        long dbBytes = 0, usedBytes = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT SUM(CAST(size AS bigint)) * 8192, SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint)) * 8192 FROM sys.database_files";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { dbBytes = r.IsDBNull(0) ? 0 : r.GetInt64(0); usedBytes = r.IsDBNull(1) ? 0 : r.GetInt64(1); }
        }
        var bytesPerRow = tables.ToDictionary(t => t.Table, t => t.Rows > 0 ? (double)t.Bytes / t.Rows : 0, StringComparer.OrdinalIgnoreCase);
        long Est(string table, long rows) => (long)Math.Round(bytesPerRow.GetValueOrDefault(table) * rows);

        var periods = await db.Periods.AsNoTracking().Where(x => x.HospitalId == h.Id || x.HospitalId == null).OrderByDescending(x => x.StartDate).ToListAsync(ct);
        var counts = new Dictionary<(Guid, string), long>();
        var periodSet = """
            (SELECT Id, CAST(StartDate AS datetime2) AS S, DATEADD(day, 1, CAST(EndDate AS datetime2)) AS E
             FROM [Period] WHERE HospitalId = @h OR HospitalId IS NULL) ps
            """;
        foreach (var d in Defs)
        {
            var where = d.Where.Replace("@s", "ps.S").Replace("@e", "ps.E").Replace("@p", "ps.Id");
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = $"SELECT ps.Id, x.n FROM {periodSet} CROSS APPLY (SELECT COUNT_BIG(*) AS n FROM [{d.Table}] t WHERE {where}) x";
            cmd.Parameters.Add(new SqlParameter("@h", SqlDbType.UniqueIdentifier) { Value = h.Id });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) counts[(r.GetGuid(0), d.Entity)] = r.GetInt64(1);
        }

        // Results: every result table keyed by RunId, for the hospital's runs, by period.
        var results = new Dictionary<Guid, (long Rows, long Bytes)>();
        var resultTables = tables.Where(t => t.Group == "Calculation results" && t.Rows > 0 && t.Table is not ("CalculationRun" or "Scenario" or "ScenarioLever" or "ScenarioResult" or "ReconciliationLine" or "VarianceComponent")).Select(t => t.Table).ToList();
        foreach (var table in resultTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = $"SELECT r.PeriodId, COUNT_BIG(*) FROM [{table}] t JOIN [CalculationRun] r ON r.Id = t.RunId WHERE r.HospitalId = @h GROUP BY r.PeriodId";
            cmd.Parameters.Add(new SqlParameter("@h", SqlDbType.UniqueIdentifier) { Value = h.Id });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (rows, bytes) = results.GetValueOrDefault(r.GetGuid(0));
                var n = r.GetInt64(1);
                results[r.GetGuid(0)] = (rows + n, bytes + Est(table, n));
            }
        }

        var runs = await db.CalculationRuns.AsNoTracking().Where(x => x.HospitalId == h.Id).OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.PeriodId, x.Code, x.Status }).ToListAsync(ct);
        var list = periods.Select(per =>
        {
            var ents = Defs.Select(d =>
            {
                var n = counts.GetValueOrDefault((per.Id, d.Entity));
                return new StoragePeriodEntity(d.Entity, n, Est(d.Table, n));
            }).ToArray();
            var run = runs.FirstOrDefault(x => x.PeriodId == per.Id);
            var (rr, rb) = results.GetValueOrDefault(per.Id);
            return new StoragePeriod(per.Id, per.Code, per.StartDate, per.EndDate, per.Status, run?.Code, run?.Status,
                ents, ents.Sum(e => e.Rows), ents.Sum(e => e.EstimatedBytes), rr, rb);
        }).ToArray();

        return new StorageDto(h.Code, dbBytes, usedBytes, tables.OrderByDescending(t => t.Bytes).ToArray(),
            Defs.Select(d => new StorageFactEntity(d.Entity, d.Label, d.Group)).ToArray(), list);
    }

    private static string? Blocker(Scope s, PeriodFactRun[] runs) =>
        runs.Length == 0 ? null
            : $"{s.PeriodCode} has calculation run{(runs.Length == 1 ? "" : "s")} {string.Join(", ", runs.Select(r => r.Code))}. Delete {(runs.Length == 1 ? "it" : "them")} first.";

    private static async Task<PeriodFactRun[]> RunsAsync(VeritaDbContext db, Scope s, CancellationToken ct)
    {
        var start = s.Start.ToDateTime(TimeOnly.MinValue);
        var end = s.End.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // Runs on this period, and any run elsewhere that costed these encounters.
        var runIds = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.HospitalId == s.HospitalId && (r.PeriodId == s.PeriodId
                || db.EncounterCosts.Any(c => c.RunId == r.Id && db.Encounters.Any(e => e.Id == c.EncounterId && e.HospitalId == s.HospitalId && e.AdmitAt >= start && e.AdmitAt < end))))
            .Select(r => new { r.Code, r.Status, r.CostingVersionId, r.CreatedAt })
            .ToListAsync(ct);
        var versions = await db.CostingVersions.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Code, ct);
        return runIds.OrderBy(r => r.CreatedAt)
            .Select(r => new PeriodFactRun(r.Code, r.Status, versions.GetValueOrDefault(r.CostingVersionId)))
            .ToArray();
    }

    private static async Task<Scope> ScopeAsync(VeritaDbContext db, Principal p, Guid periodId, string hospital, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hospital)) throw AppError.Invalid("Choose a hospital.");
        if (!p.HasHospital(hospital)) throw AppError.OutOfScope($"Hospital {hospital}");
        var h = await db.Hospitals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == hospital, ct) ?? throw AppError.NotFound($"Hospital {hospital}");
        var period = await db.Periods.AsNoTracking().FirstOrDefaultAsync(x => x.Id == periodId, ct) ?? throw AppError.NotFound("Period");
        if (period.HospitalId is { } owner && owner != h.Id) throw AppError.NotFound("Period");
        return new Scope(period.Id, period.Code, period.StartDate, period.EndDate, period.Status, h.Id, h.Code);
    }

    /// <summary>Rows and used bytes (data + indexes) per table, from partition stats.</summary>
    private static async Task<Dictionary<string, (long Rows, long Bytes)>> TableStorageAsync(DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT t.name,
                   SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END),
                   SUM(ps.used_page_count) * CAST(8192 AS bigint)
            FROM sys.dm_db_partition_stats ps
            JOIN sys.tables t ON t.object_id = ps.object_id
            WHERE t.name IN ({0})
            GROUP BY t.name
            """;
        var names = string.Join(", ", Defs.Select(d => $"N'{d.Table}'").Distinct());
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(sql, names);
        cmd.CommandTimeout = 120;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        return map;
    }

    private static async Task<long> ChunkedDeleteAsync(DbConnection conn, string table, string where, Scope s, CancellationToken ct)
    {
        // Each chunk commits on its own, so the log stays small and a stopped delete can simply be repeated.
        var sql = $"""
            SET NOCOUNT ON;
            DECLARE @total bigint = 0, @n int = 1;
            WHILE @n > 0
            BEGIN
                DELETE TOP ({DeleteChunk}) t FROM [{table}] t WHERE {where};
                SET @n = @@ROWCOUNT;
                SET @total += @n;
            END
            SELECT @total;
            """;
        return await ScalarAsync(conn, sql, s, DeleteTimeoutSeconds, ct);
    }

    private static async Task<long> DeletePatientsAsync(DbConnection conn, List<Guid> ids, CancellationToken ct)
    {
        long total = 0;
        foreach (var chunk in ids.Chunk(2_000))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = DeleteTimeoutSeconds;
            cmd.CommandText = """
                DELETE t FROM [Patient] t
                JOIN OPENJSON(@ids) WITH (Id uniqueidentifier '$') j ON j.Id = t.Id
                WHERE NOT EXISTS (SELECT 1 FROM [Encounter] e WHERE e.PatientId = t.Id)
                """;
            cmd.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = System.Text.Json.JsonSerializer.Serialize(chunk) });
            total += await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }

    private static async Task<long> ScalarAsync(DbConnection conn, string sql, Scope s, int timeout, CancellationToken ct)
    {
        await using var cmd = Command(conn, sql, s, timeout);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    private static DbCommand Command(DbConnection conn, string sql, Scope s, int timeout)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        cmd.Parameters.Add(new SqlParameter("@h", SqlDbType.UniqueIdentifier) { Value = s.HospitalId });
        cmd.Parameters.Add(new SqlParameter("@p", SqlDbType.UniqueIdentifier) { Value = s.PeriodId });
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.DateTime2) { Value = s.Start.ToDateTime(TimeOnly.MinValue) });
        cmd.Parameters.Add(new SqlParameter("@e", SqlDbType.DateTime2) { Value = s.End.AddDays(1).ToDateTime(TimeOnly.MinValue) });
        return cmd;
    }
}
