using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class SysLogsWorkloadTests
{
    private sealed record Row(long Id, DateTime Created, DateTime Updated, int Kind);

    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        const string tableName = "SysLogsBaseline";
        var table = d.Table(tableName);
        string Q(string name) => d.Quote(name);
        var dateType = dbms == Dbms.SQLServer ? "datetime" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.CommandTimeout = 60;
            return await command.ExecuteNonQueryAsync();
        }
        void Bind(DbCommand command, string name, DbType type, object value)
        {
            var p = command.CreateParameter(); p.ParameterName = name; p.DbType = type;
            if (p is Npgsql.NpgsqlParameter pg && type == DbType.DateTime) { pg.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Timestamp; }
            p.Value = value; command.Parameters.Add(p);
        }
        async Task<string> Plan(string sql, Action<DbCommand> bind)
        {
            await using var command = db.CreateCommand(); bind(command);
            command.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
            await using var reader = await command.ExecuteReaderAsync();
            if (dbms == Dbms.SQLServer) { while (await reader.ReadAsync()) { } await reader.NextResultAsync(); }
            var lines = new List<string>(); while (await reader.ReadAsync()) { lines.Add(reader.GetString(0)); }
            check(lines.Count > 0, "SysLogs actual read plan: " + dbms);
            return string.Join('\n', lines);
        }
        string Date(DateTime value) => "'" + value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";
        string Record(Row row) => $"({row.Id},{Date(row.Created)},{Date(row.Updated)},{row.Kind},'{new string('X', 256)}')";
        var columns = string.Join(',', new[] { "SysLogId", "CreatedTime", "UpdatedTime", "SysLogType", "Payload" }.Select(Q));
        var primary = dbms == Dbms.MySQL ? Q("SysLogId") : Q("CreatedTime") + (dbms == Dbms.SQLServer ? " DESC," : ",") + Q("SysLogId");
        await Execute($"CREATE TABLE {table} ({Q("SysLogId")} bigint NOT NULL,{Q("CreatedTime")} {dateType} NOT NULL,{Q("UpdatedTime")} {dateType} NOT NULL,{Q("SysLogType")} int NOT NULL,{Q("Payload")} {(dbms == Dbms.SQLServer ? "nvarchar" : "varchar")}(512) NOT NULL,PRIMARY KEY({primary}))" + (dbms == Dbms.MySQL ? " DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci" : ""));
        try
        {
            await Execute($"CREATE INDEX {Q("syslogs_standard_id")} ON {table} ({Q("SysLogId")})");
            if (dbms == Dbms.MySQL) { await Execute($"CREATE INDEX {Q("syslogs_standard_created")} ON {table} ({Q("CreatedTime")} DESC,{Q("SysLogId")})"); }
            var candidates = new[]
            {
                (Phase:"kind-created",Name:"syslogs_kind_created",Keys:$"{Q("SysLogType")},{Q("CreatedTime")} DESC,{Q("SysLogId")}"),
                (Phase:"updated",Name:"syslogs_updated",Keys:$"{Q("UpdatedTime")} DESC,{Q("SysLogId")}")
            };
            foreach (var count in new[] { 400, 4000 })
            {
                foreach (var skewed in new[] { false, true })
                {
                    var scenario = count + (skewed ? "-skewed" : "-uniform");
                    var origin = new DateTime(2026, 1, 1);
                    Row Make(long id) => new(id, origin.AddSeconds(id * 37 % count), origin.AddSeconds(count * 2 - id), skewed ? id % 20 == 0 ? 2 : 1 : (int)(id % 4) + 1);
                    var rows = Enumerable.Range(1, count).Select(id => Make(id)).ToArray();
                    var reports = new List<object>(); var switches = new List<object>(); var writes = new List<object>(); var sizes = new List<object>();
                    var previous = "standard";
                    foreach (var phase in new[] { "standard", "kind-created", "updated", "combined" })
                    {
                        foreach (var candidate in candidates.Where(c => c.Phase == previous || previous == "combined")) { await Execute(d.Drop(tableName, candidate.Name)); }
                        await Execute("TRUNCATE TABLE " + table);
                        for (var first = 0; first < count; first += 200) { await Execute($"INSERT INTO {table} ({columns}) VALUES " + string.Join(',', rows.Skip(first).Take(200).Select(Record))); }
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
                            var bytes = Convert.ToInt64(await size.ExecuteScalarAsync()); check(bytes > 0, "SysLogs candidate capacity: " + dbms);
                            sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                        }
                        foreach (var query in new[]
                        {
                            (Name:"all",From:0,To:count,Kind:0), (Name:"half",From:count / 2,To:count,Kind:0),
                            (Name:"narrow",From:count / 2,To:count / 2 + count / 10,Kind:0),
                            (Name:"kind-one",From:0,To:count,Kind:1), (Name:"kind-two",From:0,To:count,Kind:2),
                            (Name:"empty-kind",From:0,To:count,Kind:99), (Name:"empty-range",From:count,To:count + 1,Kind:0)
                        })
                        {
                            var expected = rows.Where(r => r.Created >= origin.AddSeconds(query.From) && r.Created < origin.AddSeconds(query.To) && (query.Kind == 0 || r.Kind == query.Kind)).OrderByDescending(r => r.Updated).Select(r => r.Id).ToArray();
                            var where = $"{Q("CreatedTime")}>=@from AND {Q("CreatedTime")}<@to" + (query.Kind == 0 ? "" : $" AND {Q("SysLogType")}=@kind");
                            void Parameters(DbCommand command)
                            {
                                Bind(command, "@from", DbType.DateTime, origin.AddSeconds(query.From)); Bind(command, "@to", DbType.DateTime, origin.AddSeconds(query.To));
                                if (query.Kind != 0) { Bind(command, "@kind", DbType.Int32, query.Kind); }
                            }
                            var counterSql = $"SELECT COUNT(*) FROM {table} WHERE {where}";
                            var countPlan = await Plan(counterSql, Parameters);
                            foreach (var wide in new[] { false, true })
                            {
                                var sql = $"SELECT {Q("SysLogId")}" + (wide ? $",{Q("CreatedTime")},{Q("UpdatedTime")},{Q("SysLogType")},{Q("Payload")}" : "") + $" FROM {table} WHERE {where} ORDER BY {Q("UpdatedTime")} DESC";
                                var times = new List<double>();
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    await using var command = db.CreateCommand(); command.CommandText = sql; Parameters(command);
                                    var timer = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync(); var actual = new List<long>();
                                    while (await reader.ReadAsync()) { actual.Add(reader.GetInt64(0)); }
                                    timer.Stop(); check(actual.SequenceEqual(expected), "SysLogs read result and updated order: " + dbms + phase + query.Name);
                                    if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                                }
                                times.Sort(); reports.Add(new { Phase = phase, Query = query.Name, Wide = wide, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = await Plan(sql, Parameters), CountPlan = countPlan });
                            }
                            foreach (var limit in new[] { -1, 0, 500, expected.Length - 1, expected.Length, expected.Length + 1 }.Distinct())
                            {
                                var current = rows.FirstOrDefault(r => !expected.Contains(r.Id))?.Id ?? rows[0].Id;
                                var targets = new List<long>(); var counted = false; var fetched = false;
                                var countMs = 0.0; var fetchMs = 0.0;
                                if (limit > 0)
                                {
                                    counted = true;
                                    await using var counter = db.CreateCommand(); counter.CommandText = counterSql; Parameters(counter);
                                    var timer = Stopwatch.StartNew(); var actualCount = Convert.ToInt32(await counter.ExecuteScalarAsync()); timer.Stop(); countMs = timer.Elapsed.TotalMilliseconds;
                                    check(actualCount == expected.Length, "SysLogs switch count: " + dbms);
                                    if (actualCount <= limit)
                                    {
                                        fetched = true;
                                        await using var command = db.CreateCommand(); Parameters(command); command.CommandText = $"SELECT {Q("SysLogId")} FROM {table} WHERE {where} ORDER BY {Q("UpdatedTime")} DESC";
                                        timer.Restart(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) { targets.Add(reader.GetInt64(0)); } timer.Stop(); fetchMs = timer.Elapsed.TotalMilliseconds;
                                    }
                                }
                                if (!targets.Contains(current)) { targets.Add(current); }
                                var expectedTargets = fetched ? expected.Contains(current) ? expected : expected.Append(current) : [current];
                                check(counted == (limit > 0) && fetched == (limit > 0 && expected.Length <= limit) && targets.SequenceEqual(expectedTargets), "SysLogs switch boundary and current fallback: " + dbms);
                                switches.Add(new { Phase = phase, Query = query.Name, Limit = limit, CountExecuted = counted, RowQueryExecuted = fetched, Targets = targets.Count, CountMs = countMs, FetchMs = fetchMs });
                            }
                        }
                        var cutoff = origin.AddSeconds(count / 2);
                        var expired = rows.Where(r => r.Created < cutoff).ToArray();
                        var retentionWhere = $"{Q("CreatedTime")}<@cutoff";
                        var boundsSql = $"SELECT MIN({Q("SysLogId")}),MAX({Q("SysLogId")}) FROM {table} WHERE {retentionWhere}";
                        reports.Add(new { Phase = phase, Query = "retention-bounds", Rows = 1, Plan = await Plan(boundsSql, c => Bind(c, "@cutoff", DbType.DateTime, cutoff)) });
                        foreach (var workload in new[] { "append", "retention-single", "retention-chunked" })
                        {
                            foreach (var mode in new[] { "rollback", "commit" })
                            {
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                                    try
                                    {
                                        var timer = Stopwatch.StartNew(); var affected = 0; var commands = 0;
                                        if (workload == "append") { affected = await Execute($"INSERT INTO {table} ({columns}) VALUES " + string.Join(',', Enumerable.Range(count + 1, 100).Select(id => Make(id)).Select(Record))); commands++; }
                                        else if (workload == "retention-single") { affected = await Execute($"DELETE FROM {table} WHERE {Q("CreatedTime")}<{Date(cutoff)}"); commands++; }
                                        else
                                        {
                                            await using var bounds = db.CreateCommand(); bounds.Transaction = tx; bounds.CommandText = boundsSql; Bind(bounds, "@cutoff", DbType.DateTime, cutoff);
                                            long min; long max;
                                            await using (var reader = await bounds.ExecuteReaderAsync())
                                            {
                                                check(await reader.ReadAsync() && !reader.IsDBNull(0), "SysLogs retention bounds: " + dbms);
                                                min = reader.GetInt64(0); max = reader.GetInt64(1);
                                            }
                                            commands++;
                                            check(min == expired.Min(r => r.Id) && max == expired.Max(r => r.Id), "SysLogs retention min/max: " + dbms);
                                            for (var low = min; low <= max; low += 100) { affected += await Execute($"DELETE FROM {table} WHERE {Q("CreatedTime")}<{Date(cutoff)} AND {Q("SysLogId")} BETWEEN {low} AND {low + 99}"); commands++; }
                                        }
                                        if (mode == "commit") { await tx.CommitAsync(); committed = true; transaction = null; }
                                        timer.Stop(); check(affected == (workload == "append" ? 100 : expired.Length), "SysLogs write count: " + dbms);
                                        await using var verify = db.CreateCommand(); verify.Transaction = transaction; verify.CommandText = $"SELECT {Q("SysLogId")} FROM {table} ORDER BY {Q("SysLogId")}";
                                        await using var actual = await verify.ExecuteReaderAsync(); var ids = new List<long>(); while (await actual.ReadAsync()) { ids.Add(actual.GetInt64(0)); }
                                        var expectedIds = workload == "append" ? Enumerable.Range(1, count + 100).Select(id => (long)id) : rows.Except(expired).Select(r => r.Id);
                                        check(ids.SequenceEqual(expectedIds), "SysLogs exact surviving IDs: " + dbms);
                                        if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Rows = affected, Commands = commands, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                                    }
                                    finally
                                    {
                                        if (!committed) { await tx.RollbackAsync(); }
                                        transaction = null;
                                        if (committed)
                                        {
                                            if (workload == "append") { check(await Execute($"DELETE FROM {table} WHERE {Q("SysLogId")}>{count}") == 100, "SysLogs append restored: " + dbms); }
                                            else { for (var first = 0; first < expired.Length; first += 200) { await Execute($"INSERT INTO {table} ({columns}) VALUES " + string.Join(',', expired.Skip(first).Take(200).Select(Record))); } }
                                        }
                                    }
                                }
                            }
                        }
                        await using var all = db.CreateCommand(); all.CommandText = $"SELECT {columns} FROM {table} ORDER BY {Q("SysLogId")}";
                        await using var restored = await all.ExecuteReaderAsync(); var same = true; var seen = 0;
                        while (await restored.ReadAsync())
                        {
                            if (seen >= rows.Length) { same = false; continue; }
                            var row = rows[seen++]; same &= restored.GetInt64(0) == row.Id && restored.GetDateTime(1) == row.Created && restored.GetDateTime(2) == row.Updated && restored.GetInt32(3) == row.Kind && restored.GetString(4) == new string('X', 256);
                        }
                        check(same && seen == rows.Length, "SysLogs all columns restored: " + dbms + phase);
                    }
                    foreach (var candidate in candidates) { await Execute(d.Drop(tableName, candidate.Name)); }
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"syslogs-workload-{dbms}-{scenario}.json"), JsonSerializer.Serialize(new { Reports = reports, Switches = switches, Writes = writes, IndexSizes = sizes }));
                }
            }
        }
        finally { await Execute("DROP TABLE " + table); }
    }
}
