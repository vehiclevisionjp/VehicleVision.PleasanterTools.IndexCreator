using System.Data.Common;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class DashboardCalendarIntegrationTests
{
    public static async Task Run(Dbms dbms, string connection, Database database, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        string Q(string name) => d.Quote(name);
        var rows = new[]
        {
            (Id:9101,Site:1,Manager:7,Owner:8,Date:"2026-01-10"),
            (Id:9104,Site:4,Manager:7,Owner:9,Date:"2026-01-10"),
            (Id:9105,Site:5,Manager:7,Owner:8,Date:"2026-01-10"),
            (Id:9114,Site:4,Manager:8,Owner:8,Date:"2026-01-10"),
            (Id:9115,Site:4,Manager:7,Owner:8,Date:"2026-02-10")
        };
        var created = new List<Change>();
        try
        {
            foreach (var id in new[] { 4, 5 })
            {
                await database.Execute($"INSERT INTO {d.Table("Sites")} ({Q("SiteId")},{Q("ReferenceType")},{Q("SiteSettings")},{Q("Title")}) VALUES ({id},'Results','{{}}','Synthetic record site')");
            }
            const string settings = """{"DashboardParts":[{"Type":4,"CalendarSites":"4,1","CalendarFromTo":"DateA","View":{"ColumnFilterHash":{"Manager":"7"}}}]}""";
            await database.Execute($"INSERT INTO {d.Table("Sites")} ({Q("SiteId")},{Q("ReferenceType")},{Q("SiteSettings")},{Q("Title")}) VALUES (901,'Dashboards','{settings}','Synthetic multi-site calendar')");
            foreach (var row in rows)
            {
                await database.Execute($"INSERT INTO {d.Table("Results")} ({Q("SiteId")},{Q("ResultId")},{Q("UpdatedTime")},{Q("Manager")},{Q("Owner")},{Q("DateA")}) VALUES ({row.Site},{row.Id},'2026-01-01',{row.Manager},{row.Owner},'{row.Date}')");
            }
            var inputs = await database.ReadDashboardSites(default);
            var plan = DashboardCalendarPlanner.Analyze(inputs, await database.ReadSites(default), dbms, 0);
            check(plan.Indexes.Count == 1 && plan.Indexes[0].SiteId == 1 && plan.Indexes[0].Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Manager", "DateA"]), "Dashboard common key uses live target settings without unrelated View filters: " + dbms);
            foreach (var phase in new[] { "before", "after" })
            {
                if (phase == "after")
                {
                    await database.ValidateColumns(plan.Indexes, default);
                    var changes = Reconciler.Plan(plan.Indexes, await database.ReadIndexes(default), false);
                    created.AddRange(changes.Where(c => c.Kind == ChangeKind.Create));
                    await database.Apply(changes, default);
                    check(Reconciler.Plan(plan.Indexes, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Dashboard shared physical key roundtrips on " + dbms);
                }
                foreach (var query in new (string Name, string Scope, string Extra, long[] Expected)[]
                {
                    ("both-sites",$"{Q("SiteId")} IN(1,4)","",[9101,9104]),
                    ("first-only",$"{Q("SiteId")}=1","",[9101]),
                    ("second-only",$"{Q("SiteId")}=4","",[9104]),
                    ("empty-subset","1=0","",[]),
                    ("additional-runtime-filter",$"{Q("SiteId")} IN(1,4)",$" AND {Q("Owner")}=8",[9101]),
                    ("conflicting-runtime-filter",$"{Q("SiteId")} IN(1,4)",$" AND {Q("Manager")}=8",[])
                })
                {
                    await using var command = db.CreateCommand();
                    command.CommandText = $"SELECT {Q("ResultId")} FROM {d.Table("Results")} WHERE ({query.Scope}) AND {Q("Manager")}=7 AND {Q("DateA")} BETWEEN '2026-01-01' AND '2026-01-31'{query.Extra}";
                    await using var reader = await command.ExecuteReaderAsync(); var actual = new List<long>();
                    while (await reader.ReadAsync()) { actual.Add(Convert.ToInt64(reader.GetValue(0))); }
                    check(actual.Order().SequenceEqual(query.Expected), "Dashboard synthetic subset and AND composition: " + dbms + phase + query.Name);
                }
            }
        }
        finally
        {
            foreach (var change in created) { await database.Execute(d.Drop(change.Spec.Table, change.Name)); }
            await database.Execute($"DELETE FROM {d.Table("Results")} WHERE {Q("ResultId")} IN({string.Join(',', rows.Select(r => r.Id))})");
            await database.Execute($"DELETE FROM {d.Table("Sites")} WHERE {Q("SiteId")} IN(4,5,901)");
        }
    }
}
