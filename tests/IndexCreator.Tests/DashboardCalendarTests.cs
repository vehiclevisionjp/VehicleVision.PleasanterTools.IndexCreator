using VehicleVision.PleasanterTools.IndexCreator;

internal static class DashboardCalendarTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Site Source(string parts, string extra = "") => new(900, "Dashboards", Json.Parse("{\"DashboardParts\":[" + parts + "]" + extra + "}"), 0);
        const string part = """{"Type":4,"CalendarSites":"10","SiteId":999,"CalendarFromTo":"DateA","View":{"ColumnFilterHash":{"Manager":"7"}}}""";
        var target = new Site(10, "Results", Json.Parse("{}"), 20000);
        foreach (var db in Enum.GetValues<Dbms>())
        {
            Analysis Plan(string p, Site? t = null) => DashboardCalendarPlanner.Analyze([Source(p)], [t ?? target], db);
            var plan = Plan(part);
            check(plan.Indexes.Single().SiteId == 10 && plan.Indexes.Single().Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Manager", "DateA"]), "Dashboard uses canonical target and part equality on " + db);
            check(plan.Diagnostics.Any(n => n.Message.Contains("runtime session/form")), "Dashboard runtime coverage is explicit on " + db);
            check(Plan(part.Replace("\"SiteId\":999", "\"SiteId\":999,\"CalendarSitesData\":[\"20\"]")).Indexes.Count == 0, "Conflicting cached Dashboard targets are rejected on " + db);
            check(Plan(part.Replace("\"CalendarSites\":\"10\",", "")).Indexes.Count == 0, "Runtime SiteId alone never identifies a saved target on " + db);
            var interval = Source(part.Replace("DateA\"", "DateA-DateB\""));
            check(DashboardCalendarPlanner.Analyze([interval], [target], db).Indexes.Count == 0, "Dashboard interval requires separate opt-in on " + db);
            check(DashboardCalendarPlanner.Analyze([interval], [target], db, periods: true).Indexes.Count == 2, "Dashboard interval review honors both explicit flags on " + db);
            var second = """{"Type":"Calendar","CalendarSites":"20","CalendarFromTo":"DateB","View":{"ColumnFilterHash":{"Owner":"8"}}}""";
            var multiple = DashboardCalendarPlanner.Analyze([Source(part + "," + second)], [target, target with { SiteId = 20, ReferenceType = "Issues" }], db);
            check(multiple.Indexes.Count == 2 && multiple.Indexes.Any(i => i.SiteId == 20 && i.Table == "Issues" && i.Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Owner", "DateB"])), "Dashboard parts do not mix sites or conditions on " + db);
            foreach (var selector in new[] { "10,20", "PRIVATE_SITE", "0", "-10", "999" })
            {
                var result = Plan(part.Replace("\"CalendarSites\":\"10\"", "\"CalendarSites\":\"" + selector + "\""));
                check(result.Indexes.Count == 0 && result.Diagnostics.All(n => !n.Message.Contains("PRIVATE_SITE")), "Unresolved Dashboard selector is not inferred or logged on " + db);
            }
            check(Plan(part, target with { RecordCount = 1 }).Indexes.Count == 0, "Dashboard uses target threshold on " + db);
            check(Plan(part, target with { SiteSettings = Json.Parse("""{"ServerScripts":[{"Body":"unknown();"}]}""") }).Indexes.Count == 0, "Target scripts block Dashboard inference on " + db);
            check(DashboardCalendarPlanner.Analyze([Source(part, ",\"ServerScripts\":[{}]")], [target], db).Indexes.Count == 0, "Source scripts block Dashboard inference on " + db);
            check(DashboardCalendarPlanner.Analyze([Source(part)], [target], db, uncertain: new HashSet<long> { 10 }).Indexes.Count == 0, "Extensions block Dashboard inference on " + db);
            reject(() => Plan(part.Replace("\"View\":{\"ColumnFilterHash\":{\"Manager\":\"7\"}}", "\"View\":[]")), "Malformed Dashboard View fails on " + db);
        }
        check(Options.Parse(["plan", "/include-dashboard-calendars"]).IncludeDashboardCalendars, "Dashboard review is explicit");
        reject(() => Options.Parse(["_rds", "/include-dashboard-calendars"]), "Dashboard cannot auto-apply");
        reject(() => Options.Parse(["plan", "/include-dashboard-calendars", "/prune"]), "Dashboard cannot prune");
        reject(() => Options.Parse(["plan", "/include-dashboard-calendars", "/sites", "synthetic.json"]), "Dashboard requires live input");
    }
}
