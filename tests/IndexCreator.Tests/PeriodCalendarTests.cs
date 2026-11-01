using System.Data.Common;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class PeriodCalendarTests
{
    public static async Task Run(Dbms dbms, string connection, Database database, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch
        {
            Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection),
            _ => new MySqlConnector.MySqlConnection(connection)
        };
        await db.OpenAsync();
        var site = new Site(73, "Results", Json.Parse("""{"Views":[{"DefaultMode":"Calendar","CalendarFromTo":"DateA-DateB"}]}"""), 10000);
        var indexes = new Planner(dbms, includePeriodCalendars: true).Analyze([site]).Indexes.Where(i => i.Keys.Last().Column is "DateA" or "DateB").ToArray();
        var created = new List<Change>();
        var reports = new List<object>();
        var table = d.Table("Results");
        string Q(string name) => d.Quote(name);
        var rows = new (int Id, string? From, string? To)[]
        {
            (901,"2026-01-05","2026-01-06"), (902,"2025-12-01","2026-02-01"),
            (903,"2025-12-01","2026-01-01"), (904,"2026-01-31","2026-02-01"),
            (905,"2026-02-01","2026-02-03"), (906,null,"2026-01-10"),
            (907,"2026-01-10",null), (908,null,null)
        };
        string Date(string? value) => value == null ? "NULL" : "'" + value + "'";
        var sql = $"SELECT {Q("ResultId")} FROM {table} WHERE {Q("SiteId")}=73 AND ({Q("DateA")} BETWEEN '2026-01-01' AND '2026-01-31' OR {Q("DateB")} BETWEEN '2026-01-01' AND '2026-01-31' OR ({Q("DateA")}<='2026-01-01' AND {Q("DateB")}>='2026-01-31')) ORDER BY {Q("ResultId")}";
        try
        {
            foreach (var row in rows)
            {
                await database.Execute($"INSERT INTO {table} ({Q("SiteId")},{Q("ResultId")},{Q("UpdatedTime")},{Q("DateA")},{Q("DateB")}) VALUES (73,{row.Id},'2026-01-01',{Date(row.From)},{Date(row.To)})");
            }
            foreach (var phase in new[] { "before", "after" })
            {
                if (phase == "after")
                {
                    await database.ValidateColumns(indexes, default);
                    var changes = Reconciler.Plan(indexes, await database.ReadIndexes(default), false);
                    created.AddRange(changes.Where(c => c.Kind == ChangeKind.Create));
                    await database.Apply(changes, default);
                    check(Reconciler.Plan(indexes, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Interval branch indexes roundtrip on " + dbms);
                }
                await using (var command = db.CreateCommand())
                {
                    command.CommandText = sql;
                    await using var reader = await command.ExecuteReaderAsync();
                    var actual = new List<long>();
                    while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                    check(actual.SequenceEqual(new long[] { 901, 902, 903, 904, 906, 907 }), "Interval OR preserves endpoints, containment and NULL semantics: " + dbms + phase);
                }
                await using var plan = db.CreateCommand();
                plan.CommandText = dbms switch
                {
                    Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;",
                    Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql,
                    _ => "EXPLAIN ANALYZE " + sql
                };
                await using var plans = await plan.ExecuteReaderAsync();
                if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                var lines = new List<string>();
                while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                check(lines.Count > 0, "Interval OR plan captured on " + dbms + phase);
                reports.Add(new { Phase = phase, Plan = string.Join('\n', lines) });
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "period-calendar-" + dbms + ".json"), JsonSerializer.Serialize(reports));
        }
        finally
        {
            await database.Execute($"DELETE FROM {table} WHERE {Q("SiteId")}=73");
            foreach (var change in created) { await database.Execute(d.Drop(change.Spec.Table, change.Name)); }
        }
    }
}
