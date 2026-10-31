using VehicleVision.PleasanterTools.IndexCreator;

internal static class CalendarTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var db in Enum.GetValues<Dbms>())
        {
            foreach (var table in new[] { "Results", "Issues" })
            {
                Site Make(string view, string extra = "") => new(42, table, Json.Parse("{\"Views\":[" + view + "]" + extra + "}"), 20000);
                const string view = """{"DefaultMode":"Calendar","CalendarFromTo":"DateA","ColumnFilterHash":{"Manager":"1"}}""";
                var site = Make(view);
                check(new Planner(db).Analyze([site]).Indexes.Any(i => i.Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Manager", "DateA"])), "Calendar equality prefix on " + db + table);
                check(!new Planner(db).AnalyzeQuery(site, Json.Parse(view)).Indexes.Any(i => i.Keys.Last().Column == "DateA"), "Calendar does not affect general query on " + db + table);
                foreach (var column in new[] { "", "StartTime-CompletionTime", "ClassA", "DateA~1", "Unknown" })
                {
                    check(new Planner(db).Analyze([Make("{\"DefaultMode\":\"Calendar\",\"CalendarFromTo\":\"" + column + "\"}")]).Diagnostics.Any(d => d.Message.Contains("no calendar candidate")), "Unsupported Calendar diagnosed on " + db + column);
                }
                foreach (var extra in new[] { ",\"DashboardParts\":[{}]", ",\"ServerScripts\":[{}]" })
                {
                    check(new Planner(db).Analyze([Make(view, extra)]).Diagnostics.Any(d => d.Message.Contains("no calendar candidate")), "Dynamic Calendar excluded on " + db);
                }
                check(!new Planner(db, 30000).Analyze([site]).Indexes.Any(), "Calendar threshold on " + db);
                check(new Planner(db).Analyze([Make("""{"DefaultMode":"Calendar","CalendarFromTo":"DateA","Own":true}""")]).Indexes.Count(i => i.Keys.Last().Column == "DateA") == 2, "Calendar Own branches on " + db);
            }
        }
    }
}
