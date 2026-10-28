using Microsoft.Data.SqlClient;

namespace HisExtract;

public sealed class HisConnection : IAsyncDisposable
{
    private readonly SqlConnection _conn;

    public HisConnection(string connectionString) =>
        _conn = new SqlConnection(connectionString);

    public async Task OpenAsync()
    {
        await _conn.OpenAsync();
        await DirtyReadAsync();
    }

    /// <summary>Dirty reads, no shared locks — extract must not block HIS writers on prod.</summary>
    public async Task DirtyReadAsync()
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandTimeout = 15;
        cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET ARITHABORT ON; SET DEADLOCK_PRIORITY LOW;";
        await cmd.ExecuteNonQueryAsync();
    }

    public SqlCommand Cmd(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        return cmd;
    }

    public async Task<long> CountAsync(Settings s, string table, bool byProject = true, string extraWhere = "")
    {
        var sql = byProject
            ? $"SELECT COUNT_BIG(1) FROM dbo.{table} WITH (NOLOCK) WHERE SetupID = @SetupId AND ProjectID = @ProjectId {extraWhere}"
            : $"SELECT COUNT_BIG(1) FROM dbo.{table} WITH (NOLOCK) WHERE SetupID = @SetupId {extraWhere}";
        await DirtyReadAsync();
        await using var cmd = Cmd(sql);
        s.BindTenant(cmd);
        var v = await cmd.ExecuteScalarAsync();
        return v is long l ? l : Convert.ToInt64(v);
    }

    public async Task ScalarAsync(string sql, Action<SqlCommand> bind, Action<object?> use)
    {
        await DirtyReadAsync();
        await using var cmd = Cmd(sql);
        bind(cmd);
        use(await cmd.ExecuteScalarAsync());
    }

    public async Task QueryAsync(string sql, Action<SqlCommand> bind, Action<SqlDataReader> each)
    {
        await DirtyReadAsync();
        await using var cmd = Cmd(sql);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) each(r);
    }

    public async Task QueryAsync(string sql, Action<SqlCommand> bind, Func<SqlDataReader, Task> each)
    {
        await DirtyReadAsync();
        await using var cmd = Cmd(sql);
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) await each(r);
    }

    public static string S(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i))?.Trim() ?? "";
    }

    public static int I(SqlDataReader r, string col, int fallback = 0)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return fallback;
        return Convert.ToInt32(r.GetValue(i));
    }

    public static int? Ni(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = Convert.ToInt32(r.GetValue(i));
        return v == 0 ? null : v;
    }

    public static bool B(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return false;
        var v = r.GetValue(i);
        if (v is bool b) return b;
        return Convert.ToInt32(v) != 0;
    }

    public static DateTime? Dt(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        return Convert.ToDateTime(r.GetValue(i));
    }

    public static double F(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return 0;
        return Convert.ToDouble(r.GetValue(i));
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
