using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed partial class Database : IAsyncDisposable
{
    private readonly Configuration config;
    private readonly SqlDialect dialect;
    private readonly DbConnection connection;
    private readonly bool offline;
    private readonly int lockTimeout;
    public Database(Configuration configuration, bool offline = false, int lockTimeout = 5)
    {
        config = configuration;
        this.offline = offline;
        this.lockTimeout = lockTimeout;
        dialect = new(config.Dbms, config.Schema, offline);
        connection = config.Dbms switch
        {
            Dbms.SQLServer => new SqlConnection(config.ConnectionString),
            Dbms.PostgreSQL => new NpgsqlConnection(config.ConnectionString),
            _ => new MySqlConnection(config.ConnectionString)
        };
    }
    public async Task Open(CancellationToken ct) => await connection.OpenAsync(ct);
    private DbCommand Command(string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = config.Timeout;
        var p = cmd.CreateParameter(); p.ParameterName = "@schema"; p.Value = config.Schema; cmd.Parameters.Add(p);
        return cmd;
    }
    public async Task AcquireLock(CancellationToken ct)
    {
        var sql = config.Dbms switch
        {
            Dbms.SQLServer => "DECLARE @result int; DECLARE @resource nvarchar(255) = N'IndexCreator:' + @schema; EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;",
            Dbms.PostgreSQL => "SELECT pg_try_advisory_lock(hashtext(current_database()), hashtext('IndexCreator:' || @schema));",
            _ => "SELECT GET_LOCK(CONCAT('vvic:', SHA2(CONCAT(DATABASE(), ':', @schema), 224)), 0);"
        };
        await using var cmd = Command(sql);
        var result = await cmd.ExecuteScalarAsync(ct);
        var acquired = config.Dbms switch
        {
            Dbms.SQLServer => result != null && Convert.ToInt32(result) >= 0,
            Dbms.PostgreSQL => result is true,
            _ => result != null && result != DBNull.Value && Convert.ToInt32(result) == 1
        };
        if (!acquired) throw new UserError("Another IndexCreator apply is running. No changes were applied.");
    }
    public async Task<IReadOnlyList<Site>> ReadSites(CancellationToken ct)
    {
        var q = dialect.Quote;
        var count = config.Dbms == Dbms.SQLServer ? "COUNT_BIG(*)" : "COUNT(*)";
        var sql = $"""
            SELECT s.{q("SiteId")}, s.{q("ReferenceType")}, s.{q("SiteSettings")}, COALESCE(r.cnt, i.cnt, w.cnt, 0), s.{q("Title")}
            FROM {dialect.Table("Sites")} s
            LEFT JOIN (SELECT {q("SiteId")}, {count} AS cnt FROM {dialect.Table("Results")} GROUP BY {q("SiteId")}) r ON r.{q("SiteId")}=s.{q("SiteId")} AND s.{q("ReferenceType")}='Results'
            LEFT JOIN (SELECT {q("SiteId")}, {count} AS cnt FROM {dialect.Table("Issues")} GROUP BY {q("SiteId")}) i ON i.{q("SiteId")}=s.{q("SiteId")} AND s.{q("ReferenceType")}='Issues'
            LEFT JOIN (SELECT {q("SiteId")}, {count} AS cnt FROM {dialect.Table("Wikis")} GROUP BY {q("SiteId")}) w ON w.{q("SiteId")}=s.{q("SiteId")} AND s.{q("ReferenceType")}='Wikis'
            WHERE s.{q("ReferenceType")} IN ('Results', 'Issues', 'Wikis')
            ORDER BY s.{q("SiteId")}
            """;
        await using var cmd = Command(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sites = new List<Site>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(2)) throw new UserError("A site has missing SiteSettings. No changes were applied.");
            var settings = Json.Parse(reader.GetString(2));
            Json.ValidateSettings(settings);
            sites.Add(new(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), settings, Convert.ToInt64(reader.GetValue(3)), reader.IsDBNull(4) ? "" : reader.GetString(4)));
        }
        return sites;
    }
    public async Task<IReadOnlyDictionary<long, long>> ReadSiteParents(CancellationToken ct)
    {
        await using var cmd = Command($"SELECT {dialect.Quote("SiteId")}, {dialect.Quote("ParentId")} FROM {dialect.Table("Sites")}");
        cmd.Parameters.Clear();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var parents = new Dictionary<long, long>();
        while (await reader.ReadAsync(ct)) parents[Convert.ToInt64(reader.GetValue(0))] = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1));
        return parents;
    }
    public async Task<IReadOnlyList<ExistingIndex>> ReadIndexes(CancellationToken ct)
    {
        var sql = config.Dbms switch
        {
            Dbms.SQLServer => """
                SELECT t.name, i.name, c.name, ic.is_descending_key, 0, '',
                       CASE WHEN i.is_disabled=0 AND i.is_hypothetical=0 THEN 1 ELSE 0 END,
                       CASE WHEN i.type IN (1,2) AND i.has_filter=0 THEN 1 ELSE 0 END
                FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE SCHEMA_NAME(t.schema_id)=@schema AND t.name IN ('Results','Issues','Wikis','Items')
                ORDER BY t.name, i.name, ic.key_ordinal
                """,
            Dbms.PostgreSQL => """
                SELECT t.relname, i.relname, COALESCE(a.attname,''), (x.indoption[k.ord-1] & 1)=1, 0,
                       op.opcname, x.indisvalid AND x.indisready,
                       x.indpred IS NULL AND am.amname='btree' AND x.indexprs IS NULL AND op.opcname IN ('int8_ops','int4_ops','int2_ops','numeric_ops','timestamp_ops','timestamptz_ops','bool_ops','text_ops','varchar_ops','varchar_pattern_ops') AND x.indcollation[k.ord-1]=a.attcollation
                FROM pg_index x JOIN pg_class i ON i.oid=x.indexrelid
                JOIN pg_class t ON t.oid=x.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace
                JOIN pg_am am ON am.oid=i.relam
                CROSS JOIN LATERAL unnest(x.indkey::int2[]) WITH ORDINALITY k(attnum,ord)
                LEFT JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
                JOIN pg_opclass op ON op.oid=x.indclass[k.ord-1]
                WHERE n.nspname=@schema AND t.relname IN ('Results','Issues','Wikis','Items') AND k.ord<=x.indnkeyatts
                ORDER BY t.relname, i.relname, k.ord
                """,
            _ => """
                SELECT TABLE_NAME, INDEX_NAME, COALESCE(COLUMN_NAME,''), COALESCE(COLLATION='D',FALSE), COALESCE(SUB_PART,0), '', IS_VISIBLE='YES', INDEX_TYPE='BTREE' AND COLUMN_NAME IS NOT NULL
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA=@schema AND TABLE_NAME IN ('Results','Issues','Wikis','Items')
                ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
                """
        };
        await using var cmd = Command(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var entries = new Dictionary<(string Table, string Name), (List<Key> Keys, bool Valid, bool Plain)>();
        while (await reader.ReadAsync(ct))
        {
            var id = (reader.GetString(0), reader.GetString(1));
            var key = new Key(reader.GetString(2), Convert.ToBoolean(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4)), reader.GetString(5) == "varchar_pattern_ops");
            var valid = Convert.ToBoolean(reader.GetValue(6));
            var plain = !reader.IsDBNull(7) && Convert.ToBoolean(reader.GetValue(7));
            if (!entries.TryGetValue(id, out var entry)) entry = ([], true, true);
            entry.Keys.Add(key);
            entries[id] = (entry.Keys, entry.Valid && valid, entry.Plain && plain);
        }
        return entries.Select(e => new ExistingIndex(e.Key.Table, e.Key.Name, e.Value.Keys, e.Value.Valid, e.Value.Plain)).ToArray();
    }
    public async Task ValidateColumns(IReadOnlyList<IndexSpec> specs, CancellationToken ct)
    {
        await using var cmd = Command("SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=@schema AND TABLE_NAME IN ('Results','Issues','Wikis','Items')");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var columns = new HashSet<(string, string)>();
        while (await reader.ReadAsync(ct)) columns.Add((reader.GetString(0), reader.GetString(1)));
        if (specs.Any(s => s.Keys.Any(k => !columns.Contains((s.Table, k.Column))))) throw new UserError("A required column does not exist in the target schema. No changes were applied.");
    }
    public async Task Apply(IReadOnlyList<Change> changes, CancellationToken ct, IReadOnlyList<Site>? expectedSites = null)
    {
        async Task CheckSiteSnapshot()
        {
            if (expectedSites == null) return;
            var current = await ReadSites(ct);
            var snapshot = expectedSites.ToDictionary(s => s.SiteId, s => s.ReferenceType + "|" + s.Title + "|" + s.SiteSettings.GetRawText());
            if (current.Count != snapshot.Count || current.Any(s => !snapshot.TryGetValue(s.SiteId, out var value) || value != s.ReferenceType + "|" + s.Title + "|" + s.SiteSettings.GetRawText()))
                throw new UserError("Site configuration changed during apply. No further changes were applied; re-run plan.");
        }
        await CheckSiteSnapshot();
        // PostgreSQL の CONCURRENTLY は業務の読み書きを妨げない弱いロックだけを使う。
        // 待機を打ち切ると INVALID な索引が残るため、この場合だけ上限を設けない。
        await SetLockTimeout(config.Dbms != Dbms.PostgreSQL || offline, ct);
        // 並行する CodeDefiner やサイト設定変更は別運用で止める。索引作成後にのみ古い索引を削除する。
        foreach (var c in changes.Where(c => c.Kind is ChangeKind.Create or ChangeKind.Repair))
        {
            if (c.Kind == ChangeKind.Repair) await Retry(() => Execute(dialect.Drop(c.Spec.Table, c.Name), ct), ct);
            await Retry(() => Execute(dialect.Create(c.Spec, lockTimeout), ct), ct);
            RuntimeLog.WriteLine("Created " + c.Name);
        }
        var refreshed = await ReadIndexes(ct);
        foreach (var c in changes.Where(c => c.Kind is not ChangeKind.Drop))
            if (!refreshed.Any(e => e.Table == c.Spec.Table && e.Name == c.Name && e.Plain && e.Valid && e.Spec.Covers(c.Spec)))
                throw new UserError("Index verification failed. Obsolete indexes were not removed.");
        await CheckSiteSnapshot();
        foreach (var c in changes.Where(c => c.Kind == ChangeKind.Drop))
        {
            if (!IndexSpec.IsManaged(c.Spec.Table, c.Name)) throw new UserError("Refusing to remove an unmanaged index.");
            await Retry(() => Execute(dialect.Drop(c.Spec.Table, c.Name), ct), ct);
            RuntimeLog.WriteLine("Dropped " + c.Name);
        }
    }
    public async Task SetLockTimeout(bool enabled, CancellationToken ct)
    {
        if (enabled) await Execute(dialect.LockTimeout(lockTimeout), ct);
        else if (config.Dbms == Dbms.PostgreSQL) await Execute("SET lock_timeout = 0;", ct);
    }
    // ロック待ちの上限に達した DDL は何も変更せずに中止されるため、間隔を空けて再試行する。
    public async Task Retry(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await action(); return; }
            catch (DbException e) when (IsLockTimeout(e) && attempt < 3)
            {
                RuntimeLog.WriteLine($"Lock wait limit reached. Retrying ({attempt}/2).");
                await Task.Delay(TimeSpan.FromSeconds(lockTimeout * attempt), ct);
            }
            catch (DbException e) when (IsLockTimeout(e)) { throw new UserError("Could not obtain a database lock within the wait limit. Completed steps remain applied; re-run when the workload is lighter."); }
            catch (SqlException e) when (e.Number == 1712) { throw new UserError("This SQL Server edition cannot create indexes online. Re-run with /offline during a maintenance window."); }
        }
    }
    private static bool IsLockTimeout(DbException e) => e switch
    {
        SqlException s => s.Number == 1222,
        PostgresException p => p.SqlState == PostgresErrorCodes.LockNotAvailable,
        MySqlException m => m.ErrorCode == MySqlErrorCode.LockWaitTimeout,
        _ => false
    };
    public async Task Execute(string sql, CancellationToken ct = default)
    {
        await using var cmd = Command(sql);
        // DDL は引用済み識別子を使う。SQL Server の VIEW 定義をパラメーター付きバッチへ変換しない。
        cmd.Parameters.Clear();
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        // セッションロックをプールに返さず、必ずサーバ側セッションを終了させる。
        switch (connection)
        {
            case SqlConnection sql: SqlConnection.ClearPool(sql); break;
            case NpgsqlConnection pg: NpgsqlConnection.ClearPool(pg); break;
            case MySqlConnection mysql: await MySqlConnection.ClearPoolAsync(mysql); break;
        }
        await connection.DisposeAsync();
    }
}
