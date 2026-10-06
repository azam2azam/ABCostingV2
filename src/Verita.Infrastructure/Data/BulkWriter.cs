using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Verita.Infrastructure.Data;

/// <summary>
/// SqlBulkCopy for result writes and fixture loads (ARCH-4). Column names come from the EF model,
/// so the bulk path and the ORM path cannot disagree about the schema. Row-by-row inserts would
/// not meet NFR-1 at ~60,000 components per run.
/// </summary>
public sealed class BulkWriter(IModel model)
{
    private sealed record Plan(string Table, (string Column, PropertyInfo Prop, Type Type)[] Columns);
    private readonly Dictionary<Type, Plan> _plans = [];

    private Plan For(Type t)
    {
        if (_plans.TryGetValue(t, out var p)) return p;
        var et = model.FindEntityType(t) ?? throw new InvalidOperationException($"{t.Name} is not mapped.");
        var table = et.GetTableName()!;
        var store = StoreObjectIdentifier.Table(table, et.GetSchema());
        var cols = et.GetProperties()
            .Where(x => x.PropertyInfo is not null && !x.IsShadowProperty())
            .Select(x => (x.GetColumnName(store)!, x.PropertyInfo!, Nullable.GetUnderlyingType(x.ClrType) ?? x.ClrType))
            .ToArray();
        return _plans[t] = new Plan(table, cols);
    }

    public async Task WriteAsync<T>(SqlConnection conn, SqlTransaction? tx, IReadOnlyCollection<T> rows, bool checkConstraints = true, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var plan = For(typeof(T));
        var table = new DataTable();
        foreach (var (column, _, type) in plan.Columns)
            table.Columns.Add(column, type == typeof(DateOnly) ? typeof(DateTime) : type);
        foreach (var row in rows)
        {
            var values = new object[plan.Columns.Length];
            for (var i = 0; i < plan.Columns.Length; i++)
            {
                var v = plan.Columns[i].Prop.GetValue(row);
                values[i] = v switch
                {
                    null => DBNull.Value,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    _ => v,
                };
            }
            table.Rows.Add(values);
        }

        using var bulk = new SqlBulkCopy(conn, checkConstraints ? SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.FireTriggers : SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = $"[{plan.Table}]",
            BatchSize = 5000,
            BulkCopyTimeout = 600,
        };
        foreach (var (column, _, _) in plan.Columns) bulk.ColumnMappings.Add(column, column);
        await bulk.WriteToServerAsync(table, ct);
    }
}
