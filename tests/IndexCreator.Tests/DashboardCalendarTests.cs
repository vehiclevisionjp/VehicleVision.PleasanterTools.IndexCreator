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
            var multiPart = part.Replace("\"CalendarSites\":\"10\"", "\"CalendarSites\":\"20,10\"");
            var other = target with { SiteId = 20, SiteSettings = Json.Parse("""{"Views":[{"ColumnFilterHash":{"Owner":"PRIVATE_FILTER_VALUE"}}]}""") };
            Analysis Together(Site alternative, string? body = null) => DashboardCalendarPlanner.Analyze([Source(body ?? multiPart)], [target, alternative], db);
            var common = Together(other);
            check(common.Indexes.Single().SiteId == 10 && common.Indexes.Single().Keys.Select(k => k.Column).SequenceEqual(["SiteId", "Manager", "DateA"]), "Dashboard common physical keys are independent of unrelated saved Views on " + db);
            check(common.Diagnostics.Any(n => n.Message.Contains("common to 2 possible bases")) && common.Diagnostics.Any(n => n.Message.Contains("allowed/denied site permissions")), "Dashboard common candidates do not claim runtime authorization or a fixed base on " + db);
            check(common.Diagnostics.All(n => !n.Message.Contains("PRIVATE_FILTER_VALUE")), "Dashboard unrelated filter values never leak into diagnostics on " + db);
            check(Together(other, multiPart.Replace("20,10", "10,20")).Indexes.Single().Name == common.Indexes.Single().Name, "Dashboard representative name is stable across selector order on " + db);
            check(Together(other, multiPart.Replace("20,10", ",20,,10, ")).Indexes.Count == 1, "Dashboard ignores empty comma segments like the source selector on " + db);
            check(Together(other, multiPart.Replace("20,10", "10,10")).Indexes.Single().SiteId == 10, "Dashboard repeated numeric targets do not create duplicate candidates on " + db);
            check(Together(other, multiPart.Replace("\"SiteId\":999", "\"SiteId\":999,\"CalendarSitesData\":[\"20\",\"10\"]")).Indexes.Count == 1, "Dashboard matching ordered target cache is accepted on " + db);
            check(Together(other, multiPart.Replace("\"SiteId\":999", "\"SiteId\":999,\"CalendarSitesData\":[\"10\",\"20\"]")).Indexes.Count == 0, "Dashboard cached order cannot silently change possible base resolution on " + db);
            check(Together(other with { ReferenceType = "Issues" }).Indexes.Count == 0, "Dashboard mixed physical tables suppress the entire part on " + db);
            check(Together(other with { RecordCount = 1 }).Indexes.Count == 0, "Dashboard counts are not summed to bypass an uncertain base threshold on " + db);
            check(Together(other, multiPart.Replace("20,10", "20,999")).Indexes.Count == 0, "Dashboard missing target never yields partial part candidates on " + db);
            check(Together(other with { SiteSettings = Json.Parse("""{"ServerScripts":[{}]}""") }).Indexes.Count == 0, "Dashboard script on any possible base suppresses the entire part on " + db);
            check(DashboardCalendarPlanner.Analyze([Source(multiPart)], [target, other], db, uncertain: new HashSet<long> { 20 }).Indexes.Count == 0, "Dashboard extension uncertainty on any base suppresses the part on " + db);
            var classPart = multiPart.Replace("\"Manager\":\"7\"", "\"ClassA\":\"PRIVATE_CLASS_VALUE\"");
            var exact = target with { SiteSettings = Json.Parse("""{"Columns":[{"ColumnName":"ClassA","SearchType":"ExactMatch"}]}""") };
            var forward = other with { SiteSettings = Json.Parse("""{"Columns":[{"ColumnName":"ClassA","SearchType":"ForwardMatch"}]}""") };
            var incompatible = DashboardCalendarPlanner.Analyze([Source(classPart)], [exact, forward], db);
            check(incompatible.Indexes.Count == 0 && incompatible.Diagnostics.Any(n => n.Message.Contains("no physical key definition")) && incompatible.Diagnostics.All(n => !n.Message.Contains("PRIVATE_CLASS_VALUE")), "Dashboard differing base filter semantics do not get unioned or exposed on " + db);
            var sharedRange = DashboardCalendarPlanner.Analyze([Source(classPart)], [target, forward], db);
            check(sharedRange.Indexes.Single().Keys.Select(k => k.Column).SequenceEqual(["SiteId", "DateA"]), "Dashboard retains a genuinely shared date key while excluding base-specific extra keys on " + db);
            var multiInterval = Source(multiPart.Replace("DateA\"", "DateA-DateB\""));
            check(DashboardCalendarPlanner.Analyze([multiInterval], [target, other], db).Indexes.Count == 0, "Dashboard multiple bases do not bypass period opt-in on " + db);
            check(DashboardCalendarPlanner.Analyze([multiInterval], [target, other], db, periods: true).Indexes.Count == 2, "Dashboard interval keys must be common to all possible bases on " + db);
            var sameKeysPart = part.Replace("\"CalendarSites\":\"10\"", "\"CalendarSites\":\"20\"");
            var acrossParts = DashboardCalendarPlanner.Analyze([Source(sameKeysPart + "," + part)], [target, other], db);
            check(acrossParts.Indexes.Single().SiteId == 10, "Dashboard identical physical keys across parts choose a stable representative on " + db);
            reject(() => Together(other with { SiteSettings = Json.Parse("""{"Columns":{}}""") }), "Malformed alternate settings fail before candidate generation on " + db);
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
