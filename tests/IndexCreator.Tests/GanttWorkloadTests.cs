using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class GanttWorkloadTests
{
    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch
        {
            Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection),
            _ => new MySqlConnector.MySqlConnection(connection)
        };
        await db.OpenAsync();
        string Q(string s) => d.Quote(s);
        var table = d.Table("GanttWorkloadFixture");
        var dateType = dbms == Dbms.SQLServer ? "datetime2(3)" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp(3)";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60;
            command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync();
        }
        DateTime? Start(int id) => id switch
        {
            3 => new DateTime(2026, 1, 21).AddMilliseconds(-3),
            6 => new DateTime(2026, 1, 21).AddMilliseconds(-2),
            _ => id % 5 == 0 ? null : new DateTime(2026, 1, 1).AddDays(id % 28)
        };
        DateTime? End(int id) => id is 3 or 6 ? new DateTime(2026, 1, 25) : id % 7 == 0 ? null : new DateTime(2026, 1, 1).AddDays(id % 28 + 4);
        string Literal(DateTime? date) => date is { } value ? "'" + value.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "'" : "NULL";
        string Row(int id) => $"({id % 3 + 1},{id},{Literal(Start(id))},{Literal(End(id))},'2026-01-12')";
        var reports = new List<object>();
        var writes = new List<object>();
        var sizes = new List<object>();
        void Bind(DbCommand command, DateTime begin, DateTime finish)
        {
            foreach (var (name, value) in new[] { ("@begin", begin), ("@finish", finish) })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name;
                parameter.DbType = System.Data.DbType.DateTime2; parameter.Value = value; command.Parameters.Add(parameter);
            }
        }
        string Shift(string column, int days) => dbms switch
        {
            Dbms.SQLServer => $"DATEADD(day,{days},{Q(column)})",
            Dbms.PostgreSQL => $"({Q(column)} + INTERVAL '{days} days')",
            _ => $"DATE_ADD({Q(column)},INTERVAL {days} DAY)"
        };
        await Execute($"CREATE TABLE {table} ({Q("SiteId")} int NOT NULL,{Q("RecordId")} bigint NOT NULL,{Q("StartTime")} {dateType} NULL,{Q("CompletionTime")} {dateType} NULL,{Q("CreatedTime")} {dateType} NOT NULL,PRIMARY KEY({Q("SiteId")},{Q("RecordId")}))");
        try
        {
            for (var first = 1; first <= 4000; first += 500)
            {
                await Execute($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(first, 500).Select(Row)));
            }
            foreach (var phase in new[] { "standard", "plain-date-candidates" })
            {
                if (phase != "standard")
                {
                    foreach (var column in new[] { "StartTime", "CompletionTime" })
                    {
                        await Execute($"CREATE INDEX {Q("gantt_" + column)} ON {table} ({Q("SiteId")},{Q(column)})");
                    }
                }
                await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {table} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + table, _ => "ANALYZE TABLE " + table });
                if (phase != "standard")
                {
                    foreach (var column in new[] { "StartTime", "CompletionTime" })
                    {
                        await using var size = db.CreateCommand();
                        size.CommandText = dbms switch
                        {
                            Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{table}') AND i.name=@name",
                            Dbms.PostgreSQL => $"SELECT pg_relation_size('{d.Table("gantt_" + column)}')",
                            _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name='GanttWorkloadFixture' AND index_name=@name AND stat_name='size'"
                        };
                        if (dbms != Dbms.PostgreSQL) { var p = size.CreateParameter(); p.ParameterName = "@name"; p.Value = "gantt_" + column; size.Parameters.Add(p); }
                        var bytes = Convert.ToInt64(await size.ExecuteScalarAsync());
                        check(bytes > 0, "Gantt candidate capacity captured: " + dbms);
                        sizes.Add(new { Index = "gantt_" + column, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                    }
                }
                foreach (var workload in new[] { "insert", "update-dates", "delete" })
                {
                    foreach (var mode in new[] { "rollback", "commit" })
                    {
                        for (var sample = 0; sample < 6; sample++)
                        {
                            await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                            try
                            {
                                var sql = workload switch
                                {
                                    "insert" => $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(4001, 200).Select(Row)),
                                    "update-dates" => $"UPDATE {table} SET {Q("StartTime")}={Shift("StartTime", 1)},{Q("CompletionTime")}={Shift("CompletionTime", 1)} WHERE {Q("StartTime")} IS NOT NULL",
                                    _ => $"DELETE FROM {table} WHERE {Q("RecordId")}<=200"
                                };
                                var timer = Stopwatch.StartNew(); var affected = await Execute(sql);
                                if (mode == "commit") { await tx.CommitAsync(); committed = true; }
                                timer.Stop();
                                check(affected == (workload == "update-dates" ? 3200 : 200), "Gantt write row count: " + dbms);
                                if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                            }
                            finally
                            {
                                if (!committed) { await tx.RollbackAsync(); }
                                transaction = null;
                                if (committed)
                                {
                                    var restore = workload switch
                                    {
                                        "insert" => $"DELETE FROM {table} WHERE {Q("RecordId")}>4000",
                                        "update-dates" => $"UPDATE {table} SET {Q("StartTime")}={Shift("StartTime", -1)},{Q("CompletionTime")}={Shift("CompletionTime", -1)} WHERE {Q("StartTime")} IS NOT NULL",
                                        _ => $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 200).Select(Row))
                                    };
                                    check(await Execute(restore) == (workload == "update-dates" ? 3200 : 200), "Gantt committed data restored: " + dbms);
                                }
                            }
                        }
                    }
                }
                foreach (var offset in new[] { 0, -1 })
                {
                    // 参照で確認したNULL比較の意味を保ち、COALESCEへの置換は行わない。
                    var start = $"(CASE {Q("StartTime")} WHEN CAST(NULL AS {dateType}) THEN {Q("CreatedTime")} ELSE {Q("StartTime")} END)";
                    var end = dbms switch
                    {
                        Dbms.SQLServer => $"DATEADD(day,{offset},{Q("CompletionTime")})",
                        Dbms.PostgreSQL => $"({Q("CompletionTime")} + INTERVAL '{offset} days')",
                        _ => $"DATE_ADD({Q("CompletionTime")},INTERVAL {offset} DAY)"
                    };
                    var sql = $"SELECT {Q("RecordId")} FROM {table} WHERE {Q("SiteId")}=1 AND (({start}<=@begin AND {end}>=@finish) OR {start} BETWEEN @begin AND @finish OR {end} BETWEEN @begin AND @finish) ORDER BY {Q("RecordId")}";
                    var begin = new DateTime(2026, 1, 10); var finish = new DateTime(2026, 1, 21).AddMilliseconds(-3);
                    var expected = Enumerable.Range(1, 4000).Where(id => id % 3 == 0 &&
                        ((Start(id) <= begin && End(id)?.AddDays(offset) >= finish) ||
                         (Start(id) >= begin && Start(id) <= finish) ||
                         (End(id)?.AddDays(offset) >= begin && End(id)?.AddDays(offset) <= finish))).Select(id => (long)id).ToArray();
                    check(expected.Contains(3) && !expected.Contains(6), "Gantt millisecond end boundary distinguishes adjacent rows: " + dbms);
                    var times = new List<double>();
                    for (var sample = 0; sample < 6; sample++)
                    {
                        await using var command = db.CreateCommand(); command.CommandText = sql;
                        Bind(command, begin, finish);
                        var watch = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync();
                        var actual = new List<long>(); while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                        watch.Stop(); check(actual.SequenceEqual(expected), "Gantt expression NULL and boundary result: " + dbms + phase + offset);
                        if (sample > 0) { times.Add(watch.Elapsed.TotalMilliseconds); }
                    }
                    await using var plan = db.CreateCommand();
                    plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                    Bind(plan, begin, finish);
                    await using var plans = await plan.ExecuteReaderAsync();
                    if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                    var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                    check(lines.Count > 0, "Gantt expression execution plan captured: " + dbms + phase);
                    times.Sort(); reports.Add(new { Phase = phase, OffsetDays = offset, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "gantt-workload-" + dbms + ".json"), JsonSerializer.Serialize(new { Reports = reports, Writes = writes, IndexSizes = sizes }));
        }
        finally { await Execute("DROP TABLE " + table); }
    }
}
