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
                check(new Planner(db).Analyze([Make("""{"DefaultMode":"Gantt"}""")]).Diagnostics.Any(d => d.Message.Contains("no Gantt-specific")), "Gantt uncovered expression is diagnosed on " + db);
                var intervalSite = Make("""{"DefaultMode":"Calendar","CalendarFromTo":"DateA-DateB","ColumnFilterHash":{"Manager":"1"}}""");
                var intervals = new Planner(db, includePeriodCalendars: true).Analyze([intervalSite]);
                check(intervals.Indexes.Count(i => i.Keys.Last().Column is "DateA" or "DateB") == 2, "Interval review generates separate branch keys on " + db);
                check(intervals.Diagnostics.Any(d => d.Message.Contains("full overlap predicate")), "Interval does not claim full OR coverage on " + db);
                check(!new Planner(db, includePeriodCalendars: true).AnalyzeQuery(intervalSite, intervalSite.SiteSettings.Get("Views").Array().First()).Indexes.Any(i => i.Keys.Last().Column is "DateA" or "DateB"), "Interval metadata never changes general API on " + db);
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
                const string disabled = ",\"ServerScriptsAllDisabled\":true,\"ServerScripts\":[{}]";
                check(new Planner(db).Analyze([Make(view, disabled)]).Indexes.Any(i => i.Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Manager", "DateA"])), "Global script disable allows saved Calendar: " + db + table);
                check(new Planner(db).AnalyzeCalendarReview(Make(view, disabled), Json.Parse(view)).Indexes.Any(i => i.Keys.Last().Column == "DateA"), "Global script disable allows Calendar base review: " + db + table);
                check(!new Planner(db).Analyze([Make(view, disabled.Replace(":true", ":false"))]).Indexes.Any(i => i.Keys.Last().Column == "DateA"), "Explicitly enabled scripts still block Calendar: " + db + table);
                check(new Planner(db).Analyze([Make(view, ",\"ServerScripts\":[{\"Disabled\":true}]")]).Indexes.Any(i => i.Keys.Last().Column == "DateA"), "Individually disabled scripts allow Calendar: " + db + table);
                const string intervalView = """{"DefaultMode":"Calendar","CalendarFromTo":"DateA-DateB"}""";
                var disabledInterval = Make(intervalView, disabled);
                check(new Planner(db, includePeriodCalendars: true).Analyze([disabledInterval]).Indexes.Count(i => i.Keys.Last().Column is "DateA" or "DateB") == 2, "Global script disable allows interval opt-in: " + db + table);
                check(!new Planner(db, includePeriodCalendars: true).Analyze([Make(intervalView, disabled.Replace(":true", ":false"))]).Indexes.Any(i => i.Keys.Last().Column is "DateA" or "DateB"), "Enabled scripts still block Calendar interval review: " + db + table);
                check(new Planner(db, includePeriodCalendars: true).Analyze([Make(intervalView, ",\"ServerScripts\":[{\"Disabled\":true}]")]).Indexes.Count(i => i.Keys.Last().Column is "DateA" or "DateB") == 2, "Individually disabled scripts allow Calendar interval review: " + db + table);
                check(!new Planner(db).Analyze([disabledInterval]).Indexes.Any(i => i.Keys.Last().Column is "DateA" or "DateB"), "Global script disable does not bypass interval opt-in: " + db + table);
                check(!new Planner(db).AnalyzeQuery(Make(view, disabled), Json.Parse(view)).Indexes.Any(i => i.Keys.Last().Column == "DateA"), "Global script disable does not mix Calendar into general API: " + db + table);
                check(!new Planner(db, 30000).Analyze([site]).Indexes.Any(), "Calendar threshold on " + db);
                check(new Planner(db).Analyze([Make("""{"DefaultMode":"Calendar","CalendarFromTo":"DateA","Own":true}""")]).Indexes.Count(i => i.Keys.Last().Column == "DateA") == 2, "Calendar Own branches on " + db);
            }
        }
    }
}
