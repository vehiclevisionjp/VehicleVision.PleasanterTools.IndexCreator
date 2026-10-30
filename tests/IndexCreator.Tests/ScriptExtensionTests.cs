using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class ScriptExtensionTests
{
    public static async Task RunDatabase(Database database, SqlDialect dialect, Dbms dbms, Action<bool, string> check)
    {
        var q = dialect.Quote;
        var table = dialect.Table("Extensions");
        var textType = dbms == Dbms.SQLServer ? "nvarchar(max)" : "text";
        var booleanType = dbms == Dbms.PostgreSQL ? "boolean" : dbms == Dbms.SQLServer ? "bit" : "boolean";
        var enabled = dbms == Dbms.PostgreSQL ? "false" : "0";
        var disabled = dbms == Dbms.PostgreSQL ? "true" : "1";
        string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        await database.Execute($"CREATE TABLE {table} ({q("ExtensionId")} bigint, {q("ExtensionName")} varchar(100), {q("ExtensionType")} varchar(30), {q("ExtensionSettings")} {textType}, {q("Body")} {textType}, {q("Disabled")} {booleanType})");
        try
        {
            check((await database.ReadAnalysisExtensions(default)).Count == 0, "Live empty Extensions inventory");
            var settings = "{\"WhenViewProcessing\":true,\"Body\":\"view.Filters.Status='200';\",\"SiteIdList\":[1]}";
            var body = "view.Filters.Status='100';";
            await database.Execute($"INSERT INTO {table} VALUES (1,'fixture','ServerScript',{Literal(settings)},{Literal(body)},{enabled}), (2,'sql','Sql','{{\"OnSelectingWhere\":true}}','PRIVATE_SQL',{enabled}), (3,'disabled','ServerScript','invalid','invalid',{disabled}), (4,'style','Style','invalid','invalid',{enabled})");
            var entries = await database.ReadAnalysisExtensions(default);
            check(entries.Count == 2 && entries.Single(e => e.Type == "ServerScript").Settings.Get("Body").Text() == body, "Live table selects active analysis types and overrides Body");
            var site = MutationSite("view.Filters.Status='100';");
            var inspection = ExtensionInventory.Inspect([site], entries);
            check(ViewMutationPlanner.Analyze([site], dbms, externalSites: inspection.UncertainSites).Indexes.Count == 0, "Live extensions block unproven mutation inference");
            check(inspection.Diagnostics.All(n => !n.Message.Contains("PRIVATE_SQL")), "Live SQL body not disclosed");
            await database.Execute($"UPDATE {table} SET {q("Disabled")}={disabled}");
            check((await database.ReadAnalysisExtensions(default)).Count == 0, "Live disable is visible on next read");
            await database.Execute($"UPDATE {table} SET {q("Disabled")}={enabled} WHERE {q("ExtensionId")}=1");
            check((await database.ReadAnalysisExtensions(default)).Count == 1, "Live re-enable is visible on next read");
            await database.Execute($"DELETE FROM {table} WHERE {q("ExtensionId")}=1");
            var refreshed = ExtensionInventory.Inspect([site], await database.ReadAnalysisExtensions(default));
            check(refreshed.UncertainSites.Count == 0, "Live deletion clears old analysis state");
            var candidates = ViewMutationPlanner.Analyze([site], dbms, externalSites: refreshed.UncertainSites).Indexes;
            check(candidates.Count > 0, "Live mutation candidates resume after blockers disappear");
            await database.ValidateColumns(candidates, default);
            await database.Apply(Reconciler.Plan(candidates, await database.ReadIndexes(default), false), default);
            check(Reconciler.Plan(candidates, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Live mutation indexes roundtrip");
            check(Reconciler.Plan([], await database.ReadIndexes(default), false).All(c => c.Kind != ChangeKind.Drop), "Absent candidates do not delete indexes without prune");
            foreach (var index in candidates)
            {
                await database.Execute(dialect.Drop(index.Table, index.Name));
            }
        }
        finally
        {
            await database.Execute($"DROP TABLE {table}");
        }
        try
        {
            await database.ReadAnalysisExtensions(default);
            check(false, "Missing table must fail");
        }
        catch (System.Data.Common.DbException)
        {
            check(true, "Missing table is not treated as empty");
        }
    }

    public static Site MutationSite(string body = "view.Filters.Status='100'; view.Sorters.UpdatedTime='asc';") =>
        new(1, "Results", JsonSerializer.SerializeToElement(new
        {
            ServerScripts = new[] { new { Body = body, WhenViewProcessing = true } },
            Views = new[] { new { ColumnFilterHash = new { Manager = "1", Status = "200" }, ColumnSorterHash = new { UpdatedTime = "desc" } } }
        }), 20000);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var source = MutationSite();
        foreach (var db in Enum.GetValues<Dbms>())
        {
            var plan = ViewMutationPlanner.Analyze([source], db);
            check(plan.Indexes.Any(i => i.Keys.Any(k => k.Column == "Manager") && i.Keys.Any(k => k.Column == "Status") && i.Keys.Any(k => k.Column == "UpdatedTime" && !k.Desc)), "Mutation merges saved filter and overrides sorter on " + db);
            check(plan.Indexes.Any(i => i.Keys.Any(k => k.Column == "Status") && !i.Keys.Any(k => k.Column == "Manager")), "Mutation includes empty baseline on " + db);
            check(ViewMutationPlanner.Analyze([source with { RecordCount = 1 }], db).Indexes.Count == 0, "Mutation threshold on " + db);
            check(ViewMutationPlanner.Analyze([source with { ReferenceType = "Issues" }], db).Indexes.All(i => i.Table == "Issues"), "Mutation targets Issues on " + db);
            check(ViewMutationPlanner.Analyze([source], db, externalSites: new HashSet<long> { 1 }).Indexes.Count == 0, "Extensions block mutation inference on " + db);
        }
        check(source.SiteSettings.Get("Views").Array().First().Get("ColumnSorterHash").Get("UpdatedTime").Text() == "desc", "Saved View remains unchanged");
        var negativeNode = JsonNode.Parse(source.SiteSettings.GetRawText())!.AsObject();
        negativeNode["Views"]![0]!["ColumnFilterNegatives"] = new JsonArray("Status");
        check(ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(negativeNode.ToJsonString()) }], Dbms.SQLServer).Indexes.Any(i => i.Keys.Any(k => k.Column == "Manager") && i.Keys.Any(k => k.Column == "Status")), "Changed filter removes its old negation");
        negativeNode["Views"]![0]!["ColumnFilterHash"]!["Status"] = "100";
        check(!ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(negativeNode.ToJsonString()) }], Dbms.SQLServer).Indexes.Any(i => i.Keys.Any(k => k.Column == "Manager") && i.Keys.Any(k => k.Column == "Status")), "Same-value assignment conservatively keeps negation");
        var overwrite = ViewMutationPlanner.Analyze([MutationSite("view.Sorters.UpdatedTime='asc'; view.Sorters.UpdatedTime='desc';")], Dbms.SQLServer);
        check(overwrite.Indexes.SelectMany(i => i.Keys).Where(k => k.Column == "UpdatedTime").All(k => k.Desc), "Last literal assignment wins");
        check(ViewMutationPlanner.Analyze([MutationSite("view['Filters']['Status']='100';")], Dbms.SQLServer).Indexes.Count > 0, "Literal bracket assignment supported");
        foreach (var body in new[] { "view.Filters.Status=context.UserId;", "view.Filters.Status+='100';", "view.Filters[column]='100';", "view.Filters.Status='100'; unknown();", "const view=other; view.Filters.Status='100';", "if (true) view.Filters.Status='100';", "view.ClearFilters();", "view.Sorters.UpdatedTime='invalid';", "view.Filters.Status='100'; return;", "//Include: Other\nview.Filters.Status='100';" })
        {
            check(ViewMutationPlanner.Analyze([MutationSite(body)], Dbms.SQLServer).Indexes.Count == 0, "Unresolved mutation does not create candidates");
        }
        var sharedNode = JsonNode.Parse(source.SiteSettings.GetRawText())!.AsObject();
        sharedNode["ServerScripts"]!.AsArray().Add(JsonNode.Parse("{\"Shared\":true,\"Body\":\"unknown();\"}"));
        var shared = source with { SiteSettings = Json.Parse(sharedNode.ToJsonString()) };
        check(ViewMutationPlanner.Analyze([shared], Dbms.SQLServer).Indexes.Count == 0, "Shared code blocks View inference");
        sharedNode["ServerScripts"]!.AsArray()[1]!["Shared"] = false;
        sharedNode["ServerScripts"]!.AsArray()[1]!["WhenViewProcessing"] = true;
        check(ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(sharedNode.ToJsonString()) }], Dbms.SQLServer).Indexes.Count == 0, "Multiple View handlers do not get combined");
        sharedNode["ServerScriptsAllDisabled"] = true;
        check(ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(sharedNode.ToJsonString()) }], Dbms.SQLServer).Indexes.Count == 0, "Disabled site does not infer mutations");

        var settings = Json.Parse("{\"SiteIdList\":[1],\"WhenViewProcessing\":true,\"Body\":\"view.Filters.Status='200';\"}");
        var fromFile = ExtensionInventory.Normalize("file", "ServerScript", settings, "view.Filters.Status='100';", true);
        var fromTable = ExtensionInventory.Normalize("table", "ServerScript", settings, "view.Filters.Status='100';");
        check(fromFile.Settings.GetRawText() == fromTable.Settings.GetRawText(), "File and table normalize identically");
        check(ExtensionInventory.Normalize("table", "ServerScript", settings, "").Settings.Get("Body").Text().Contains("200"), "Empty table Body preserves settings Body");
        check(ExtensionInventory.Normalize("file", "ServerScript", settings, "", true).Settings.Get("Body").Text() == "", "Empty file sidecar overrides settings Body");
        var inspected = ExtensionInventory.Inspect([source, source with { SiteId = 2 }], [fromFile, fromTable]);
        check(inspected.UncertainSites.SetEquals(new[] { 1L }), "Positive site scope bounds uncertainty");
        check(inspected.Diagnostics.Any(n => n.Message.Contains("file")) && inspected.Diagnostics.Any(n => n.Message.Contains("table")), "Both sources are visible without collapsing duplicates");
        var sql = ExtensionInventory.Normalize("table", "Sql", Json.Parse("{\"OnSelectingWhere\":true}"), "SELECT PRIVATE_BODY");
        var sqlNotes = ExtensionInventory.Inspect([source], [sql]);
        check(sqlNotes.Diagnostics.Any(n => n.Message.Contains("OnSelectingWhere")) && sqlNotes.Diagnostics.All(n => !n.Message.Contains("PRIVATE_BODY")), "SQL hooks diagnosed without exposing or executing text");
        var disabled = ExtensionInventory.Normalize("table", "ServerScript", Json.Parse("{\"Disabled\":true}"), "invalid {");
        check(ExtensionInventory.Inspect([source], [disabled]).UncertainSites.Count == 0, "Disabled extension is not parsed and releases uncertainty");
        check(ExtensionInventory.Inspect([source], []).UncertainSites.Count == 0, "Empty refreshed inventory has no stale blockers");
        check(ExtensionInventory.Inspect([source, source with { SiteId = 2 }], [ExtensionInventory.Normalize("file", "Sql", Json.Parse("{\"SiteIdList\":[-1]}"), "")]).UncertainSites.Count == 2, "Negative scopes are conservatively unresolved");
        reject(() => Options.Parse(["plan", "/extensions-table"]), "Extension inspection is opt-in script planning");
        reject(() => Options.Parse(["_rds", "/include-script-queries", "/extensions-table"]), "Extensions cannot enable apply");
        reject(() => Options.Parse(["plan", "/include-script-queries", "/extensions-table", "/prune"]), "Extensions cannot enable prune");
        reject(() => Options.Parse(["plan", "/include-script-queries", "/extensions-table", "/sites", "fixture.json"]), "Table inspection requires live DB");
        check(Options.Parse(["plan", "/include-script-queries", "/extension-path", "/tmp/parameters", "/extensions-table"]).ExtensionPath == "/tmp/parameters", "Both extension sources may be selected");

        var temporary = Path.Combine(Path.GetTempPath(), "indexcreator-extension-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            check(ExtensionInventory.ReadFiles(temporary).Count == 0, "Readable empty source is a valid inventory");
            var directory = Path.Combine(temporary, "ExtendedServerScripts", "nested");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "fixture.json");
            File.WriteAllText(file, settings.GetRawText());
            File.WriteAllText(file + ".js", "view.Filters.Status='100';");
            check(ExtensionInventory.ReadFiles(temporary).Single().Settings.Get("Body").Text().Contains("100"), "Nested sidecar is read");
            File.Delete(file + ".js");
            check(ExtensionInventory.ReadFiles(temporary).Single().Settings.Get("Body").Text().Contains("200"), "Deleted sidecar falls back to settings");
            File.WriteAllText(file, "{\"Disabled\":true,\"Body\":\"invalid {\"}");
            check(ExtensionInventory.Inspect([source], ExtensionInventory.ReadFiles(temporary)).UncertainSites.Count == 0, "File disabling is reflected on next read");
            File.Delete(file);
            check(ExtensionInventory.ReadFiles(temporary).Count == 0, "Deleted JSON no longer exists in inventory");
            File.WriteAllText(file, "invalid PRIVATE_TEXT");
            try { ExtensionInventory.ReadFiles(temporary); check(false, "Invalid JSON must fail"); }
            catch (JsonException) { check(true, "Invalid JSON is not mistaken for deletion"); }
            File.WriteAllText(file, settings.GetRawText());
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { ExtensionInventory.ReadFiles(temporary); check(false, "Unreadable file must fail"); }
                catch (IOException) { check(true, "Read failure is not mistaken for empty inventory"); }
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
        reject(() => ExtensionInventory.ReadFiles(temporary), "Missing source directory is not empty");
    }
}
