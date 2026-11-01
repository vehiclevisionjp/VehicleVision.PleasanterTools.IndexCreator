using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class PeriodCalendarWorkloadTests
{
    private sealed record Row(int Site, int Id, DateTime? From, DateTime? To);

    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch
        {
            Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection),
            _ => new MySqlConnector.MySqlConnection(connection)
        };
        await db.OpenAsync();
        string Q(string name) => d.Quote(name);
        var table = d.Table("PeriodCalendarWorkload");
        var dateType = dbms == Dbms.SQLServer ? "datetime2(3)" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp(3)";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60;
            command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync();
        }
        string Date(DateTime? value) => value is { } date ? "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'" : "NULL";
        string Values(Row row) => $"({row.Site},{row.Id},{Date(row.From)},{Date(row.To)})";
        string Shift(string column, int days) => dbms switch
        {
            Dbms.SQLServer => $"DATEADD(day,{days},{Q(column)})",
            Dbms.PostgreSQL => $"({Q(column)} + INTERVAL '{days} days')",
            _ => $"DATE_ADD({Q(column)},INTERVAL {days} DAY)"
        };
        void Bind(DbCommand command, int site, DateTime begin, DateTime finish)
        {
            foreach (var (name, value, type) in new (string, object, System.Data.DbType)[]
            {
                ("@site",site,System.Data.DbType.Int32),
                ("@begin",begin,System.Data.DbType.DateTime2),
                ("@finish",finish,System.Data.DbType.DateTime2)
            })
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name;
                parameter.DbType = type; parameter.Value = value; command.Parameters.Add(parameter);
            }
        }
        var candidates = new[] { (Phase: "from", Column: "DateA", Name: "period_workload_from"), (Phase: "to", Column: "DateB", Name: "period_workload_to") };
        foreach (var scenario in new[] { "small-uniform", "larger-skewed" })
        {
            var count = scenario == "small-uniform" ? 400 : 4000;
            Row Make(int id)
            {
                var site = id <= 7 || (scenario == "larger-skewed" && id % 10 < 8) ? 1 : id % 3 + 1;
                var from = new DateTime(2026, 1, 1).AddDays(id % 90);
                var row = new Row(site, id, id % 10 == 0 ? null : from, id % 13 == 0 ? null : from.AddDays(4));
                if (id % 11 == 0) { row = row with { From = new DateTime(2025, 12, 1), To = new DateTime(2026, 4, 1) }; }
                return id switch
                {
                    1 => row with { From = new DateTime(2026, 1, 9), To = new DateTime(2026, 1, 10) },
                    2 => row with { From = new DateTime(2026, 1, 12), To = new DateTime(2026, 2, 1) },
                    3 => row with { From = new DateTime(2026, 1, 13).AddMilliseconds(-3), To = new DateTime(2026, 2, 1) },
                    4 => row with { From = new DateTime(2026, 1, 13).AddMilliseconds(-2), To = new DateTime(2026, 2, 1) },
                    5 => row with { From = null, To = new DateTime(2026, 1, 11) },
                    6 => row with { From = null, To = null },
                    7 => row with { From = new DateTime(2025, 12, 1), To = new DateTime(2026, 4, 1) },
                    _ => row
                };
            }
            var rows = Enumerable.Range(1, count).Select(Make).ToArray();
            var reports = new List<object>(); var writes = new List<object>(); var sizes = new List<object>();
            await Execute($"CREATE TABLE {table} ({Q("SiteId")} int NOT NULL,{Q("RecordId")} bigint NOT NULL,{Q("DateA")} {dateType} NULL,{Q("DateB")} {dateType} NULL,PRIMARY KEY({Q("SiteId")},{Q("RecordId")}))");
            try
            {
                var previous = "standard";
                foreach (var phase in new[] { "standard", "from", "to", "combined" })
                {
                    foreach (var candidate in candidates.Where(c => previous == "combined" || c.Phase == previous))
                    {
                        await Execute(d.Drop("PeriodCalendarWorkload", candidate.Name));
                    }
                    await Execute("TRUNCATE TABLE " + table);
                    for (var first = 0; first < rows.Length; first += 200)
                    {
                        await Execute($"INSERT INTO {table} VALUES " + string.Join(',', rows.Skip(first).Take(200).Select(Values)));
                    }
                    var active = candidates.Where(c => phase == "combined" || c.Phase == phase).ToArray();
                    foreach (var candidate in active) { await Execute($"CREATE INDEX {Q(candidate.Name)} ON {table} ({Q("SiteId")},{Q(candidate.Column)})"); }
                    previous = phase;
                    await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {table} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + table, _ => "ANALYZE TABLE " + table });
                    foreach (var candidate in active)
                    {
                        await using var size = db.CreateCommand();
                        size.CommandText = dbms switch
                        {
                            Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{table}') AND i.name=@name",
                            Dbms.PostgreSQL => $"SELECT pg_relation_size('{d.Table(candidate.Name)}')",
                            _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name='PeriodCalendarWorkload' AND index_name=@name AND stat_name='size'"
                        };
                        if (dbms != Dbms.PostgreSQL) { var p = size.CreateParameter(); p.ParameterName = "@name"; p.Value = candidate.Name; size.Parameters.Add(p); }
                        var bytes = Convert.ToInt64(await size.ExecuteScalarAsync());
                        check(bytes > 0, "Period Calendar candidate capacity: " + dbms);
                        sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                    }
                    foreach (var window in new[]
                    {
                        (Name:"narrow",Begin:new DateTime(2026,1,10),Finish:new DateTime(2026,1,13).AddMilliseconds(-3)),
                        (Name:"wide",Begin:new DateTime(2026,1,1),Finish:new DateTime(2026,4,1).AddMilliseconds(-3)),
                        (Name:"empty",Begin:new DateTime(2026,6,1),Finish:new DateTime(2026,6,11).AddMilliseconds(-3))
                    })
                    {
                        foreach (var site in new[] { 1, 2, 99 })
                        {
                            var expected = rows.Where(r => r.Site == site &&
                                ((r.From >= window.Begin && r.From <= window.Finish) || (r.To >= window.Begin && r.To <= window.Finish) ||
                                 (r.From <= window.Begin && r.To >= window.Finish))).Select(r => (long)r.Id).ToArray();
                            if (window.Name == "narrow" && site == 1)
                            {
                                check(new long[] { 1, 2, 3, 5, 7 }.All(expected.Contains) && !expected.Contains(4) && !expected.Contains(6), "Period Calendar endpoint, containment and NULL oracle: " + dbms);
                            }
                            var sql = $"SELECT {Q("RecordId")} FROM {table} WHERE {Q("SiteId")}=@site AND ({Q("DateA")} BETWEEN @begin AND @finish OR {Q("DateB")} BETWEEN @begin AND @finish OR ({Q("DateA")}<=@begin AND {Q("DateB")}>=@finish))";
                            var times = new List<double>();
                            for (var sample = 0; sample < 6; sample++)
                            {
                                await using var command = db.CreateCommand(); command.CommandText = sql; Bind(command, site, window.Begin, window.Finish);
                                var timer = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync();
                                var actual = new List<long>(); while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                                timer.Stop(); check(actual.Order().SequenceEqual(expected), "Period Calendar full overlap result: " + dbms + scenario + phase + window.Name);
                                if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                            }
                            await using var plan = db.CreateCommand();
                            plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                            Bind(plan, site, window.Begin, window.Finish);
                            await using var plans = await plan.ExecuteReaderAsync();
                            if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                            var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                            check(lines.Count > 0, "Period Calendar full overlap plan: " + dbms);
                            times.Sort(); reports.Add(new { Phase = phase, Window = window.Name, Site = site, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                        }
                    }
                    foreach (var workload in new[] { "insert", "update-dates", "delete" })
                    {
                        var expected = workload == "update-dates" ? rows.Count(r => r.Id <= 100 && r.From != null) : 50;
                        foreach (var mode in new[] { "rollback", "commit" })
                        {
                            for (var sample = 0; sample < 6; sample++)
                            {
                                await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                                try
                                {
                                    var write = workload switch
                                    {
                                        "insert" => $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(count + 1, 50).Select(Make).Select(Values)),
                                        "update-dates" => $"UPDATE {table} SET {Q("DateA")}={Shift("DateA", 1)},{Q("DateB")}={Shift("DateB", 1)} WHERE {Q("RecordId")}<=100 AND {Q("DateA")} IS NOT NULL",
                                        _ => $"DELETE FROM {table} WHERE {Q("RecordId")}<=50"
                                    };
                                    var timer = Stopwatch.StartNew(); var affected = await Execute(write);
                                    if (mode == "commit") { await tx.CommitAsync(); committed = true; }
                                    timer.Stop(); check(affected == expected, "Period Calendar write row count: " + dbms);
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
                                            "insert" => $"DELETE FROM {table} WHERE {Q("RecordId")}>{count}",
                                            "update-dates" => $"UPDATE {table} SET {Q("DateA")}={Shift("DateA", -1)},{Q("DateB")}={Shift("DateB", -1)} WHERE {Q("RecordId")}<=100 AND {Q("DateA")} IS NOT NULL",
                                            _ => $"INSERT INTO {table} VALUES " + string.Join(',', rows.Take(50).Select(Values))
                                        };
                                        check(await Execute(restore) == expected, "Period Calendar committed data restored: " + dbms);
                                    }
                                }
                            }
                        }
                    }
                    await using var verify = db.CreateCommand();
                    verify.CommandText = $"SELECT {Q("SiteId")},{Q("RecordId")},{Q("DateA")},{Q("DateB")} FROM {table}";
                    await using var restored = await verify.ExecuteReaderAsync();
                    var actualRows = new List<Row>();
                    while (await restored.ReadAsync())
                    {
                        actualRows.Add(new(Convert.ToInt32(restored.GetValue(0)), Convert.ToInt32(restored.GetValue(1)),
                            restored.IsDBNull(2) ? null : restored.GetDateTime(2), restored.IsDBNull(3) ? null : restored.GetDateTime(3)));
                    }
                    check(actualRows.OrderBy(r => r.Id).SequenceEqual(rows), "Period Calendar all fixture values restored: " + dbms + phase);
                }
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"period-calendar-workload-{dbms}-{scenario}.json"), JsonSerializer.Serialize(new { Reports = reports, Writes = writes, IndexSizes = sizes }));
            }
            finally { await Execute("DROP TABLE " + table); }
        }
    }
}
