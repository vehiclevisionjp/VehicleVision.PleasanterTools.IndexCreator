using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class ScriptDiagnosticTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var db in Enum.GetValues<Dbms>())
        {
            var cases = new[]
            {
                ("view.Filters.Status=context.Value;", "unresolved-value", "#1"),
                ("const x='100'; { const x='200'; view.Filters.Status=x; }", "scope-or-shadowing", "#2.1"),
                ("if(true) { unknown(); } else { view.Filters.Status='100'; }", "unsupported-statement-or-operation", "#1.then.1"),
                ("if(false) { view.Filters.Status='100'; } else { unknown(); }", "unsupported-statement-or-operation", "#1.else.1"),
                ("const x=context.Value; view.Filters.Status=x;", "unresolved-constant", "#1"),
                ("let x='100'; view.Filters.Status=x;", "mutable-declaration", "#1"),
                ("view.ClearFilters(true);", "unsupported-call-arguments", "#1"),
                ("view.Filters.PRIVATE_IDENTIFIER='PRIVATE_VALUE';", "unsupported-or-dynamic-column", "#1"),
                ("view.Sorters.UpdatedTime='PRIVATE_DIRECTION';", "unsupported-sorter", "#1"),
                ("view.SearchTypes.ClassA='PRIVATE_SEARCH';", "unsupported-search-type", "#1"),
                ("if(context.Flag) { view.ClearFilters(); }", "unresolved-condition-or-branch-shape", "#1")
            };
            foreach (var (body, reason, location) in cases)
            {
                var result = ViewMutationPlanner.Analyze([ScriptExtensionTests.MutationSite(body)], db);
                check(result.Indexes.Count == 0 && result.Diagnostics.Any(n => n.Message.Contains("[script-view:unresolved]") && n.Message.Contains(reason) && n.Message.Contains(location)), "View diagnostic reason and statement path on " + db);
                check(result.Diagnostics.All(n => !n.Message.Contains("PRIVATE_")), "View diagnostic redacts values and arbitrary keys on " + db);
            }
            var source = ScriptExtensionTests.MutationSite();
            foreach (var flag in new[] { "Shared", "WhenloadingSiteSettings" })
            {
                var settings = JsonNode.Parse(source.SiteSettings.GetRawText())!.AsObject();
                var extra = JsonNode.Parse("{\"Body\":\"unknown();\"}")!;
                extra[flag] = true;
                settings["ServerScripts"]!.AsArray().Add(extra);
                var blocked = ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(settings.ToJsonString()) }], db);
                check(blocked.Indexes.Count == 0 && blocked.Diagnostics.Any(n => n.Message.Contains(flag == "Shared" ? "shared-script" : "settings-loading")), "External execution blocker classified on " + db);
            }
            var wrappers = JsonNode.Parse(source.SiteSettings.GetRawText())!.AsObject();
            var extraHandler = wrappers["ServerScripts"]![0]!.DeepClone();
            extraHandler["TryCatch"] = true;
            wrappers["ServerScripts"]!.AsArray().Add(extraHandler);
            check(ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(wrappers.ToJsonString()) }], db).Diagnostics.Any(n => n.Message.Contains("execution-wrapper")), "Wrapper reason is distinct on " + db);
            check(ViewMutationPlanner.Analyze([ScriptExtensionTests.MutationSite("//Include: PRIVATE_NAME\nview.Filters.Status='100';")], db).Diagnostics.Any(n => n.Message.Contains("include") && !n.Message.Contains("PRIVATE_NAME")), "Include reason hides names on " + db);
            check(ViewMutationPlanner.Analyze([source], db, externalSites: new HashSet<long> { 1 }).Diagnostics.Any(n => n.Message.Contains("extension-impact")), "Extension impact distinct on " + db);
            check(ViewMutationPlanner.Analyze([ScriptExtensionTests.MutationSite("const x='100';")], db).Diagnostics.Any(n => n.Message.Contains("resolved-no-mutations")), "Supported declaration-only body is not unresolved on " + db);
            check(ViewMutationPlanner.Analyze([source], db, minRecords: long.MaxValue).Diagnostics.Any(n => n.Message.Contains("resolved-no-candidates")), "Resolved view without candidates is distinct on " + db);
            check(ViewMutationPlanner.Analyze([source], db).Diagnostics.Any(n => n.Message.Contains("[script-view:resolved]")), "Resolved candidate status on " + db);

            var query = ScriptExtensionTests.MutationSite("items.Get(1,JSON.stringify({View:{ColumnFilterHash:{Status:'100'}}})); unknown();");
            var queryPlan = ScriptQueryPlanner.Analyze([query], db);
            check(queryPlan.Indexes.Count > 0 && queryPlan.Diagnostics.Any(n => n.Message.Contains("first-query-only") && n.Message.Contains("prefix statement #1") && n.Message.Contains("not covered")), "Query coverage stops at first Get on " + db);
            check(ScriptQueryPlanner.Analyze([query], db, minRecords: long.MaxValue).Diagnostics.Any(n => n.Message.Contains("resolved-no-candidates")), "Resolved query with no candidates distinct on " + db);
            check(ScriptQueryPlanner.Analyze([ScriptExtensionTests.MutationSite("const x='100';")], db).Diagnostics.Any(n => n.Message.Contains("resolved-no-query")), "No query in supported declarations is not unresolved on " + db);
            check(ScriptQueryPlanner.Analyze([ScriptExtensionTests.MutationSite("items.Get(context.Id,'PRIVATE_JSON');")], db).Diagnostics.Any(n => n.Message.Contains("unsupported-or-dynamic-call") && !n.Message.Contains("PRIVATE_JSON")), "Unresolved query is diagnosed without argument values on " + db);
            check(ScriptQueryPlanner.Analyze([ScriptExtensionTests.MutationSite("const items={}; items.Get(1,'PRIVATE_JSON');")], db).Diagnostics.Any(n => n.Message.Contains("scope-or-shadowing")), "Query scope check is explicit on " + db);
        }
        var entries = new[] { ExtensionInventory.Normalize("file", "Sql", Json.Parse("{\"Disabled\":true}"), "PRIVATE_SQL"), ExtensionInventory.Normalize("table", "Sql", Json.Parse("{}"), "PRIVATE_SQL") };
        check(ExtensionInventory.DescribeInputs(false, false, []).Contains("files=not-provided; table=not-provided"), "Unprovided extension inputs are not read-empty");
        check(ExtensionInventory.DescribeInputs(true, true, []).Contains("files=read (0 entries); table=read (0 entries)"), "Read-empty extension inputs are explicit");
        check(ExtensionInventory.DescribeInputs(true, true, entries).Contains("files=read (1 entries); table=read (1 entries)"), "Extension counts are per source including disabled files");
        check(!ExtensionInventory.DescribeInputs(true, true, entries).Contains("PRIVATE_SQL"), "Input summary hides bodies");
    }
}
