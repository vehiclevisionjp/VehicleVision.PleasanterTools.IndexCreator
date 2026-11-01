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
        var dateType = dbms == Dbms.SQLServer ? "datetime2" : dbms == Dbms.MySQL ? "datetime" : "timestamp";
        async Task Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
        }
        DateTime? Start(int id) => id % 5 == 0 ? null : new DateTime(2026, 1, 1).AddDays(id % 28);
        DateTime? End(int id) => id % 7 == 0 ? null : new DateTime(2026, 1, 1).AddDays(id % 28 + 4);
        string Literal(DateTime? date) => date is { } value ? "'" + value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "'" : "NULL";
        string Row(int id) => $"({id % 3 + 1},{id},{Literal(Start(id))},{Literal(End(id))},'2026-01-12')";
        var reports = new List<object>();
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
                    var sql = $"SELECT {Q("RecordId")} FROM {table} WHERE {Q("SiteId")}=1 AND (({start}<='2026-01-10' AND {end}>='2026-01-20') OR {start} BETWEEN '2026-01-10' AND '2026-01-20' OR {end} BETWEEN '2026-01-10' AND '2026-01-20') ORDER BY {Q("RecordId")}";
                    var begin = new DateTime(2026, 1, 10); var finish = new DateTime(2026, 1, 20);
                    var expected = Enumerable.Range(1, 4000).Where(id => id % 3 == 0 &&
                        ((Start(id) <= begin && End(id)?.AddDays(offset) >= finish) ||
                         (Start(id) >= begin && Start(id) <= finish) ||
                         (End(id)?.AddDays(offset) >= begin && End(id)?.AddDays(offset) <= finish))).Select(id => (long)id).ToArray();
                    var times = new List<double>();
                    for (var sample = 0; sample < 6; sample++)
                    {
                        await using var command = db.CreateCommand(); command.CommandText = sql;
                        var watch = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync();
                        var actual = new List<long>(); while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                        watch.Stop(); check(actual.SequenceEqual(expected), "Gantt expression NULL and boundary result: " + dbms + phase + offset);
                        if (sample > 0) { times.Add(watch.Elapsed.TotalMilliseconds); }
                    }
                    await using var plan = db.CreateCommand();
                    plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                    await using var plans = await plan.ExecuteReaderAsync();
                    if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                    var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                    check(lines.Count > 0, "Gantt expression execution plan captured: " + dbms + phase);
                    times.Sort(); reports.Add(new { Phase = phase, OffsetDays = offset, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "gantt-workload-" + dbms + ".json"), JsonSerializer.Serialize(reports));
        }
        finally { await Execute("DROP TABLE " + table); }
    }
}
