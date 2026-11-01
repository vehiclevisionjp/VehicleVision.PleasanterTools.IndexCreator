using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class SitesWorkloadTests
{
    private sealed record Row(int Tenant, long Id, long Parent, DateTime Updated, string Reference, string? Form);

    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        const string tableName = "SitesBaseline";
        var table = d.Table(tableName);
        string Q(string name) => d.Quote(name);
        string Columns(params string[] names) => string.Join(',', names.Select(Q));
        var dateType = dbms == Dbms.SQLServer ? "datetime" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp";
        string Text(int length) => (dbms == Dbms.SQLServer ? "nvarchar" : "varchar") + "(" + length + ")";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60; command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync();
        }
        void Bind(DbCommand command, string name, DbType type, object value)
        {
            var p = command.CreateParameter(); p.ParameterName = name; p.DbType = type;
            if (p is Npgsql.NpgsqlParameter pg && type == DbType.DateTime) { pg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Timestamp; }
            p.Value = value; command.Parameters.Add(p);
        }
        string Record(Row row) => $"({row.Tenant},{row.Id},{row.Parent},'{row.Updated.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}',{SqlDialect.Literal(row.Reference)},{(row.Form == null ? "NULL" : SqlDialect.Literal(row.Form))},'{new string('X', 256)}')";
        var allColumns = Columns("TenantId", "SiteId", "ParentId", "UpdatedTime", "ReferenceType", "Form", "Payload");
        await Execute($"CREATE TABLE {table} ({Q("TenantId")} int NOT NULL,{Q("SiteId")} bigint NOT NULL,{Q("ParentId")} bigint NOT NULL,{Q("UpdatedTime")} {dateType} NOT NULL,{Q("ReferenceType")} {Text(32)} NOT NULL,{Q("Form")} {Text(32)} NULL,{Q("Payload")} {Text(512)} NOT NULL,PRIMARY KEY({Columns("TenantId", "SiteId")},{Q("UpdatedTime")}{(dbms == Dbms.PostgreSQL ? "" : " DESC")}))" + (dbms == Dbms.MySQL ? " DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci" : ""));
        try
        {
            await Execute($"CREATE INDEX {Q("sites_standard_1")} ON {table} ({Columns("ReferenceType", "SiteId")},{Q("UpdatedTime")} DESC)");
            await Execute($"CREATE UNIQUE INDEX {Q("sites_standard_2")} ON {table} ({Q("SiteId")})");
            await Execute($"CREATE INDEX {Q("sites_standard_4")} ON {table} ({Q("Form")})");
            var candidates = new[] { (Phase: "parent", Name: "sites_parent", Keys: Columns("TenantId", "ParentId", "SiteId")), (Phase: "delta", Name: "sites_delta", Keys: Columns("TenantId", "UpdatedTime")) };
            foreach (var count in new[] { 400, 4000 })
            {
                foreach (var skewed in new[] { false, true })
                {
                    var scenario = count + (skewed ? "-skewed" : "-uniform");
                    var origin = new DateTime(2026, 1, 1);
                    Row Make(long id) => new(skewed && id % 100 < 75 ? 1 : (int)(id % 4) + 1, id, id % 5 == 0 ? 0 : id % 17 + 1, origin.AddSeconds(id), id % 3 == 0 ? "Issues" : "Results", id % 11 == 0 ? "Form-A" : null);
                    var rows = Enumerable.Range(1, count).Select(id => Make(id)).ToArray();
                    var reports = new List<object>(); var writes = new List<object>(); var sizes = new List<object>();
                    var previous = "standard";
                    foreach (var phase in new[] { "standard", "parent", "delta", "combined" })
                    {
                        foreach (var candidate in candidates.Where(c => c.Phase == previous || previous == "combined")) { await Execute(d.Drop(tableName, candidate.Name)); }
                        await Execute("TRUNCATE TABLE " + table);
                        for (var first = 0; first < rows.Length; first += 200) { await Execute($"INSERT INTO {table} ({allColumns}) VALUES " + string.Join(',', rows.Skip(first).Take(200).Select(Record))); }
                        var active = candidates.Where(c => c.Phase == phase || phase == "combined").ToArray();
                        foreach (var candidate in active) { await Execute($"CREATE INDEX {Q(candidate.Name)} ON {table} ({candidate.Keys})"); }
                        previous = phase;
                        await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {table} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + table, _ => "ANALYZE TABLE " + table });
                        foreach (var candidate in active)
                        {
                            await using var size = db.CreateCommand();
                            size.CommandText = dbms switch
                            {
                                Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{table}') AND i.name=@name",
                                Dbms.PostgreSQL => $"SELECT pg_relation_size('{d.Table(candidate.Name)}')",
                                _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name=@table AND index_name=@name AND stat_name='size'"
                            };
                            if (dbms != Dbms.PostgreSQL) { Bind(size, "@name", DbType.String, candidate.Name); }
                            if (dbms == Dbms.MySQL) { Bind(size, "@table", DbType.String, tableName); }
                            var bytes = Convert.ToInt64(await size.ExecuteScalarAsync()); check(bytes > 0, "Sites candidate capacity: " + dbms);
                            sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                        }
                        foreach (var query in new[]
                        {
                            (Name:"root",Tenant:1,Kind:"parent",Value:0L), (Name:"children",Tenant:1,Kind:"parent",Value:7L),
                            (Name:"other-tenant",Tenant:2,Kind:"parent",Value:7L), (Name:"empty-tenant",Tenant:99,Kind:"parent",Value:7L),
                            (Name:"empty-parent",Tenant:1,Kind:"parent",Value:999L), (Name:"delta-half",Tenant:1,Kind:"delta",Value:(long)count / 2),
                            (Name:"delta-empty",Tenant:1,Kind:"delta",Value:(long)count), (Name:"id",Tenant:1,Kind:"id",Value:4L),
                            (Name:"reference",Tenant:1,Kind:"reference",Value:0L), (Name:"form",Tenant:1,Kind:"form",Value:0L)
                        })
                        {
                            foreach (var wide in new[] { false, true })
                            {
                                var expected = rows.Where(r => r.Tenant == query.Tenant && (query.Kind switch { "parent" => r.Parent == query.Value, "delta" => r.Updated > origin.AddSeconds(query.Value), "id" => r.Id == query.Value, "reference" => r.Reference == "Issues", _ => r.Form == "Form-A" })).Select(r => r.Id).Order().ToArray();
                                var predicate = query.Kind switch { "parent" => Q("ParentId") + "=@value", "delta" => Q("UpdatedTime") + ">@value", "id" => Q("SiteId") + "=@value", "reference" => Q("ReferenceType") + "=@value", _ => Q("Form") + "=@value" };
                                var sql = $"SELECT {Q("SiteId")}" + (wide ? $",{Columns("ParentId", "UpdatedTime", "ReferenceType", "Form", "Payload")}" : "") + $" FROM {table} WHERE {Q("TenantId")}=@tenant AND {predicate}";
                                void Parameters(DbCommand command)
                                {
                                    Bind(command, "@tenant", DbType.Int32, query.Tenant);
                                    Bind(command, "@value", query.Kind == "delta" ? DbType.DateTime : query.Kind is "reference" or "form" ? DbType.String : DbType.Int64,
                                        query.Kind switch { "delta" => origin.AddSeconds(query.Value), "reference" => "Issues", "form" => "Form-A", _ => query.Value });
                                }
                                var times = new List<double>();
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    await using var command = db.CreateCommand(); command.CommandText = sql; Parameters(command);
                                    var timer = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync(); var actual = new List<long>();
                                    while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                                    timer.Stop(); check(actual.Order().SequenceEqual(expected), "Sites unordered result: " + dbms + scenario + phase + query.Name);
                                    if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                                }
                                await using var plan = db.CreateCommand(); Parameters(plan);
                                plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                                await using var plans = await plan.ExecuteReaderAsync(); if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                                var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                                check(lines.Count > 0, "Sites actual plan: " + dbms);
                                times.Sort(); reports.Add(new { Phase = phase, Query = query.Name, Wide = wide, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                            }
                        }
                        foreach (var workload in new[] { "insert", "reparent", "update-time", "delete" })
                        {
                            var affectedCount = workload is "insert" or "delete" ? 50 : 100;
                            string Shift(int days) => dbms switch { Dbms.SQLServer => $"DATEADD(day,{days},{Q("UpdatedTime")})", Dbms.PostgreSQL => $"{Q("UpdatedTime")}+INTERVAL '{days} days'", _ => $"DATE_ADD({Q("UpdatedTime")},INTERVAL {days} DAY)" };
                            foreach (var mode in new[] { "rollback", "commit" })
                            {
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                                    try
                                    {
                                        var sql = workload switch
                                        {
                                            "insert" => $"INSERT INTO {table} ({allColumns}) VALUES " + string.Join(',', Enumerable.Range(count + 1, 50).Select(id => Make(id)).Select(Record)),
                                            "delete" => $"DELETE FROM {table} WHERE {Q("SiteId")}<=50",
                                            _ => $"UPDATE {table} SET {Q("UpdatedTime")}={Shift(1)}" + (workload == "reparent" ? $",{Q("ParentId")}={Q("ParentId")}+100" : "") + $" WHERE {Q("SiteId")}<=100"
                                        };
                                        var timer = Stopwatch.StartNew(); var affected = await Execute(sql); if (mode == "commit") { await tx.CommitAsync(); committed = true; } timer.Stop();
                                        check(affected == affectedCount, "Sites write count: " + dbms);
                                        if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Rows = affected, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                                    }
                                    finally
                                    {
                                        if (!committed) { await tx.RollbackAsync(); }
                                        transaction = null;
                                        if (committed)
                                        {
                                            var restore = workload switch
                                            {
                                                "insert" => $"DELETE FROM {table} WHERE {Q("SiteId")}>{count}",
                                                "delete" => $"INSERT INTO {table} ({allColumns}) VALUES " + string.Join(',', rows.Take(50).Select(Record)),
                                                _ => $"UPDATE {table} SET {Q("UpdatedTime")}={Shift(-1)}" + (workload == "reparent" ? $",{Q("ParentId")}={Q("ParentId")}-100" : "") + $" WHERE {Q("SiteId")}<=100"
                                            };
                                            check(await Execute(restore) == affectedCount, "Sites restored row count: " + dbms);
                                        }
                                    }
                                }
                            }
                        }
                        await using var verify = db.CreateCommand(); verify.CommandText = $"SELECT {allColumns} FROM {table}";
                        await using var restored = await verify.ExecuteReaderAsync(); var same = true; var ids = new HashSet<long>();
                        while (await restored.ReadAsync())
                        {
                            var id = restored.GetInt64(1); same &= ids.Add(id);
                            if (id < 1 || id > rows.Length) { same = false; continue; }
                            var row = rows[(int)id - 1];
                            same &= restored.GetInt32(0) == row.Tenant && restored.GetInt64(2) == row.Parent && restored.GetDateTime(3) == row.Updated && restored.GetString(4) == row.Reference && (restored.IsDBNull(5) ? row.Form == null : restored.GetString(5) == row.Form) && restored.GetString(6) == new string('X', 256);
                        }
                        check(same && ids.Count == rows.Length, "Sites all values restored: " + dbms + scenario + phase);
                    }
                    foreach (var candidate in candidates) { await Execute(d.Drop(tableName, candidate.Name)); }
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"sites-workload-{dbms}-{scenario}.json"), JsonSerializer.Serialize(new { Reports = reports, Writes = writes, IndexSizes = sizes }));
                }
            }
        }
        finally { await Execute("DROP TABLE " + table); }
    }
}
