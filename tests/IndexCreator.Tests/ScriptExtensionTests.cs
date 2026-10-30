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
            var site = OrderedSite("view.Filters.Status='100,200'; view.FilterNegative('Status');", "const status='100', direction=1, negative=false, column='Status', sort='UpdatedTime', f='Filters', s='Sorters', clear='ClearFilters', negate='FilterNegative'; view[clear](); view[f][column]=status; view[negate](column,negative); view[s][sort]=direction;");
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
            check(candidates.All(i => i.Keys.All(k => k.Column != "Manager")), "Live ClearFilters removes saved predicate keys");
            check(candidates.Any(i => i.Keys.Any(k => k.Column == "UpdatedTime" && k.Desc)), "Live numeric sorter retains descending direction");
            await database.ValidateColumns(candidates, default);
            await database.Apply(Reconciler.Plan(candidates, await database.ReadIndexes(default), false), default);
            check(Reconciler.Plan(candidates, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Live mutation indexes roundtrip");
            check(Reconciler.Plan([], await database.ReadIndexes(default), false).All(c => c.Kind != ChangeKind.Drop), "Absent candidates do not delete indexes without prune");
            foreach (var index in candidates)
            {
                await database.Execute(dialect.Drop(index.Table, index.Name));
            }
            var searchCandidates = ViewMutationPlanner.Analyze([SearchSite("'ForwardMatch'")], dbms).Indexes;
            check(searchCandidates.Count == 1, "Live SearchTypes prefix candidate generated");
            await database.ValidateColumns(searchCandidates, default);
            await database.Apply(Reconciler.Plan(searchCandidates, await database.ReadIndexes(default), false), default);
            check(Reconciler.Plan(searchCandidates, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Live SearchTypes candidate roundtrip with DB-specific keys");
            foreach (var index in searchCandidates)
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

    private static Site OrderedSite(params string[] bodies)
    {
        var site = MutationSite();
        var settings = JsonNode.Parse(site.SiteSettings.GetRawText())!.AsObject();
        settings["ServerScripts"] = JsonSerializer.SerializeToNode(bodies.Select((body, index) => new { Id = 100 - index, Body = body, WhenViewProcessing = true }));
        return site with { SiteSettings = Json.Parse(settings.ToJsonString()) };
    }

    private static Site SearchSite(string value) => new(1, "Results", JsonSerializer.SerializeToElement(new
    {
        Columns = new[] { new { ColumnName = "ClassA", SearchType = "PartialMatch" } },
        ServerScripts = new[] { new { WhenViewProcessing = true, Body = "view.Filters.ClassA='A'; view.SearchTypes.ClassA=" + value + ";" } }
    }), 20000);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var source = MutationSite();
        foreach (var db in Enum.GetValues<Dbms>())
        {
            var operationBody = "const f='Filters', s='Sorters', clear='ClearFilters', negate='FilterNegative', alias=clear; view[alias](); view[f].Status='100'; view[s].UpdatedTime=1; view[negate]('Status',false);";
            var operationPlan = ViewMutationPlanner.Analyze([MutationSite(operationBody)], db);
            var directOperation = ViewMutationPlanner.Analyze([MutationSite("view.ClearFilters(); view.Filters.Status='100'; view.Sorters.UpdatedTime=1; view.FilterNegative('Status',false);")], db);
            check(operationPlan.Indexes.Count > 0 && operationPlan.Indexes.Select(i => i.Signature).SequenceEqual(directOperation.Indexes.Select(i => i.Signature)), "Constant groups and methods match direct operations on " + db);
            var orderedOperations = ViewMutationPlanner.Analyze([OrderedSite("const f='Filters', clear='ClearFilters'; view[f].Status='200';", "view[clear](); view[f].Status='100';")], db);
            var orderedDirect = ViewMutationPlanner.Analyze([OrderedSite("view.Filters.Status='200';", "view.ClearFilters(); view.Filters.Status='100';")], db);
            check(orderedOperations.Indexes.Count > 0 && orderedOperations.Indexes.Select(i => i.Signature).SequenceEqual(orderedDirect.Indexes.Select(i => i.Signature)), "Constant operations follow handler order on " + db);
            var operationSearch = SearchSite("'ExactMatch'");
            var operationSettings = JsonNode.Parse(operationSearch.SiteSettings.GetRawText())!.AsObject();
            operationSettings["ServerScripts"]![0]!["Body"] = "const f='Filters', s='SearchTypes', c='ClassA'; view[f][c]='A'; view[s][c]='ExactMatch';";
            check(ViewMutationPlanner.Analyze([operationSearch with { SiteSettings = Json.Parse(operationSettings.ToJsonString()) }], db).Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "Constant SearchTypes group resolved on " + db);
            var columnBody = "const column='Status', alias=column, sort='UpdatedTime'; view.Filters[alias]='100'; view.Sorters[sort]=1;";
            var columnPlan = ViewMutationPlanner.Analyze([MutationSite(columnBody)], db);
            var directColumnPlan = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view.Sorters.UpdatedTime=1;")], db);
            check(columnPlan.Indexes.Count > 0 && columnPlan.Indexes.Select(i => i.Signature).SequenceEqual(directColumnPlan.Indexes.Select(i => i.Signature)), "Constant column names match direct keys on " + db);
            var orderedColumns = ViewMutationPlanner.Analyze([OrderedSite("const column='Status'; view.Filters[column]='200';", "view.ClearFilters(); view.Filters[column]='100';")], db);
            var directOrdered = ViewMutationPlanner.Analyze([OrderedSite("view.Filters.Status='200';", "view.ClearFilters(); view.Filters.Status='100';")], db);
            check(orderedColumns.Indexes.Count > 0 && orderedColumns.Indexes.Select(i => i.Signature).SequenceEqual(directOrdered.Indexes.Select(i => i.Signature)), "Constant columns follow handler order and clearing on " + db);
            var columnSearch = SearchSite("'ExactMatch'");
            var columnSettings = JsonNode.Parse(columnSearch.SiteSettings.GetRawText())!.AsObject();
            columnSettings["ServerScripts"]![0]!["Body"] = "const key='ClassA'; view['Filters'][key]='A'; view.SearchTypes[key]='ExactMatch';";
            check(ViewMutationPlanner.Analyze([columnSearch with { SiteSettings = Json.Parse(columnSettings.ToJsonString()) }], db).Indexes.Select(i => i.Signature).SequenceEqual(ViewMutationPlanner.Analyze([columnSearch], db).Indexes.Select(i => i.Signature)), "Constant search type column matches direct column on " + db);
            var constantBody = "const status='100', direction=1; const alias=status; const column='Status', negative=false; view.Filters.Status=alias; view.Sorters.UpdatedTime=direction; view.FilterNegative(column,negative);";
            var constantPlan = ViewMutationPlanner.Analyze([MutationSite(constantBody)], db);
            var literalPlan = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view.Sorters.UpdatedTime=1; view.FilterNegative('Status',false);")], db);
            check(constantPlan.Indexes.Count > 0 && constantPlan.Indexes.Select(i => i.Signature).SequenceEqual(literalPlan.Indexes.Select(i => i.Signature)), "Constant values match inline literals on " + db);
            var crossHandler = ViewMutationPlanner.Analyze([OrderedSite("const status='100';", "view.Filters.Status=status;")], db);
            var inlineHandler = ViewMutationPlanner.Analyze([OrderedSite("view.Filters.Status='100';")], db);
            check(crossHandler.Indexes.Count > 0 && crossHandler.Indexes.Select(i => i.Signature).SequenceEqual(inlineHandler.Indexes.Select(i => i.Signature)), "Constants follow combined handler order on " + db);
            check(ViewMutationPlanner.Analyze([MutationSite("const name='Status', negative=true; view.Filters.Status='100'; view.FilterNegative(name,negative);")], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "Boolean constants retain negation on " + db);
            check(ViewMutationPlanner.Analyze([MutationSite("const status='100'; view.Filters.Status=status; view.ClearFilters();")], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "ClearFilters removes constant assignments on " + db);
            foreach (var mode in new[] { "'ExactMatch'", "2" })
            {
                var constantSearch = SearchSite("searchMode");
                var constantSettings = JsonNode.Parse(constantSearch.SiteSettings.GetRawText())!.AsObject();
                constantSettings["ServerScripts"]![0]!["Body"] = "const searchMode=" + mode + ";" + constantSettings["ServerScripts"]![0]!["Body"]!.ToString();
                check(ViewMutationPlanner.Analyze([constantSearch with { SiteSettings = Json.Parse(constantSettings.ToJsonString()) }], db).Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "Constant search type resolved on " + db);
            }
            foreach (var (literal, direction) in new[] { ("0", "asc"), ("1", "desc"), ("'0'", "asc"), ("'1'", "desc") })
            {
                var numericSort = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view.Sorters.UpdatedTime=" + literal + ";")], db);
                var namedSort = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view.Sorters.UpdatedTime='" + direction + "';")], db);
                check(numericSort.Indexes.Count > 0 && numericSort.Indexes.Select(i => i.Signature).SequenceEqual(namedSort.Indexes.Select(i => i.Signature)), "Numeric sorter matches named sorter on " + db);
            }
            var numericOverwrite = ViewMutationPlanner.Analyze([OrderedSite("view.Sorters.UpdatedTime=0;", "view.Sorters.UpdatedTime=1;")], db);
            check(numericOverwrite.Indexes.Any(i => i.Keys.Any(k => k.Column == "UpdatedTime")) && numericOverwrite.Indexes.SelectMany(i => i.Keys).Where(k => k.Column == "UpdatedTime").All(k => k.Desc), "Last numeric sorter wins across handlers on " + db);
            var clearedView = MutationSite("view.ClearFilters();") with { ReferenceType = "Issues" };
            var clearSettings = JsonNode.Parse(clearedView.SiteSettings.GetRawText())!.AsObject();
            foreach (var flag in new[] { "Incomplete", "Own", "NearCompletionTime", "Delay", "Overdue" })
            {
                clearSettings["Views"]![0]![flag] = true;
            }
            clearSettings["Views"]![0]!["Search"] = "PRIVATE_SEARCH";
            clearSettings["Views"]![0]!["ColumnSorterHash"]!["UpdatedTime"] = "asc";
            clearSettings["Views"]![0]!["ColumnFilterSearchTypes"] = JsonNode.Parse("{\"ClassA\":\"ExactMatch\"}");
            clearedView = clearedView with { SiteSettings = Json.Parse(clearSettings.ToJsonString()) };
            var clearPlan = ViewMutationPlanner.Analyze([clearedView], db);
            check(clearPlan.Indexes.All(i => i.Keys.All(k => k.Column is not ("Status" or "Manager" or "Owner" or "CompletionTime"))), "ClearFilters clears saved predicates and special flags on " + db);
            check(clearPlan.Indexes.Any(i => i.Keys.Any(k => k.Column == "UpdatedTime" && !k.Desc)), "ClearFilters retains sorting on " + db);
            check(clearPlan.Diagnostics.All(n => !n.Message.Contains("Full-text") && !n.Message.Contains("PRIVATE_SEARCH")), "ClearFilters clears full-text state on " + db);
            clearSettings["ServerScripts"]![0]!["Body"] = "view.ClearFilters(); view.Filters.ClassA='A';";
            check(ViewMutationPlanner.Analyze([clearedView with { SiteSettings = Json.Parse(clearSettings.ToJsonString()) }], db).Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "ClearFilters retains search type for later filters on " + db);
            check(ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view['ClearFilters']();")], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "ClearFilters drops earlier script assignments on " + db);
            check(ViewMutationPlanner.Analyze([MutationSite("view.FilterNegative('Status'); view.ClearFilters(); view.Filters.Status='100';")], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "ClearFilters retains explicit negative overrides on " + db);
            var repeated = ViewMutationPlanner.Analyze([OrderedSite("view.ClearFilters(); view.Filters.Manager='1';", "view.ClearFilters(); view.Filters.Status='100';")], db);
            check(repeated.Indexes.Any(i => i.Keys.Any(k => k.Column == "Status")) && repeated.Indexes.All(i => i.Keys.All(k => k.Column != "Manager")), "Last clear determines retained filters across handlers on " + db);
            check(clearedView.SiteSettings.Get("Views").Array().First().Get("Own").Bool(), "Clear planning does not mutate saved settings on " + db);
            foreach (var body in new[] { "view.Filters.Status='100'; view.FilterNegative('Status');", "view.FilterNegative('Status',true); view.Filters.Status='100';", "view.Filters.Status='100'; view['FilterNegative']('Status',true);" })
            {
                check(ViewMutationPlanner.Analyze([MutationSite(body)], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "Explicit negation excludes key regardless of filter assignment order on " + db);
            }
            var negativeSite = MutationSite("view.FilterNegative('Status',false);");
            var negativeSettings = JsonNode.Parse(negativeSite.SiteSettings.GetRawText())!.AsObject();
            negativeSettings["Views"]![0]!["ColumnFilterNegatives"] = new JsonArray("Status", "Manager");
            var cleared = ViewMutationPlanner.Analyze([negativeSite with { SiteSettings = Json.Parse(negativeSettings.ToJsonString()) }], db);
            check(cleared.Indexes.Any(i => i.Keys.Any(k => k.Column == "Status")) && cleared.Indexes.All(i => i.Keys.All(k => k.Column != "Manager")), "False clears only specified saved negation on " + db);
            var clearedLast = OrderedSite("view.FilterNegative('Status');", "view.FilterNegative('Status',false);");
            check(ViewMutationPlanner.Analyze([clearedLast], db).Indexes.Any(i => i.Keys.Any(k => k.Column == "Status")), "Later false wins across handlers on " + db);
            var setLast = OrderedSite("view.FilterNegative('Status',false);", "view.FilterNegative('Status');");
            check(ViewMutationPlanner.Analyze([setLast], db).Indexes.All(i => i.Keys.All(k => k.Column != "Status")), "Later true wins across handlers on " + db);
            foreach (var value in new[] { "'ExactMatch'", "'ExactMatchMultiple'", "2", "12", "'2'", "'12'" })
            {
                check(ViewMutationPlanner.Analyze([SearchSite(value)], db).Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "SearchTypes exact override creates a key on " + db);
            }
            foreach (var value in new[] { "'PartialMatch'", "'PartialMatchMultiple'", "1", "11" })
            {
                check(ViewMutationPlanner.Analyze([SearchSite(value)], db).Indexes.All(i => i.Keys.All(k => k.Column != "ClassA")), "SearchTypes partial match is excluded on " + db);
            }
            foreach (var value in new[] { "'ForwardMatch'", "'ForwardMatchMultiple'", "3", "13" })
            {
                var key = ViewMutationPlanner.Analyze([SearchSite(value)], db).Indexes.Single().Keys.Single(k => k.Column == "ClassA");
                check(key.Pattern == (db == Dbms.PostgreSQL) && key.Prefix == (db == Dbms.MySQL ? 100 : 0), "SearchTypes forward match respects DB-specific keys on " + db);
            }
            var orderedSearch = OrderedSite("view.Filters.ClassA='A'; view.SearchTypes.ClassA='ExactMatch';", "view.SearchTypes.ClassA='PartialMatch';");
            check(ViewMutationPlanner.Analyze([orderedSearch], db).Indexes.All(i => i.Keys.All(k => k.Column != "ClassA")), "Later SearchTypes override prevents unusable key on " + db);
            var plan = ViewMutationPlanner.Analyze([source], db);
            check(plan.Indexes.Any(i => i.Keys.Any(k => k.Column == "Manager") && i.Keys.Any(k => k.Column == "Status") && i.Keys.Any(k => k.Column == "UpdatedTime" && !k.Desc)), "Mutation merges saved filter and overrides sorter on " + db);
            check(plan.Indexes.Any(i => i.Keys.Any(k => k.Column == "Status") && !i.Keys.Any(k => k.Column == "Manager")), "Mutation includes empty baseline on " + db);
            check(ViewMutationPlanner.Analyze([source with { RecordCount = 1 }], db).Indexes.Count == 0, "Mutation threshold on " + db);
            check(ViewMutationPlanner.Analyze([source with { ReferenceType = "Issues" }], db).Indexes.All(i => i.Table == "Issues"), "Mutation targets Issues on " + db);
            check(ViewMutationPlanner.Analyze([source], db, externalSites: new HashSet<long> { 1 }).Indexes.Count == 0, "Extensions block mutation inference on " + db);
            var bodies = new[] { "view.Filters.Status='100,200'; view.Sorters.UpdatedTime='desc';", "view.Filters.Status='100'; view.Sorters.UpdatedTime='asc';" };
            var ordered = OrderedSite(bodies);
            var multiPlan = ViewMutationPlanner.Analyze([ordered], db);
            check(multiPlan.Indexes.Select(i => i.Signature).Order().SequenceEqual(plan.Indexes.Select(i => i.Signature).Order()), "Array order overrides earlier handlers, independent of Id on " + db);
            var reversed = ViewMutationPlanner.Analyze([OrderedSite(bodies.Reverse().ToArray())], db);
            var equivalentReverse = ViewMutationPlanner.Analyze([MutationSite(string.Join("\n", bodies.Reverse()))], db);
            check(reversed.Indexes.Select(i => i.Signature).Order().SequenceEqual(equivalentReverse.Indexes.Select(i => i.Signature).Order()), "Reversed handlers match combined body on " + db);
            check(!reversed.Indexes.Select(i => i.Signature).Order().SequenceEqual(multiPlan.Indexes.Select(i => i.Signature).Order()), "Changing handler order changes candidates on " + db);
            check(ViewMutationPlanner.Analyze([OrderedSite(bodies[0], "unknown();")], db).Indexes.Count == 0, "Unresolved last handler discards entire inference on " + db);
            check(ViewMutationPlanner.Analyze([OrderedSite("unknown();", bodies[1])], db).Indexes.Count == 0, "Unresolved first handler discards entire inference on " + db);
            check(ViewMutationPlanner.Analyze([OrderedSite(bodies)], db, externalSites: new HashSet<long> { 1 }).Indexes.Count == 0, "Extensions still block multiple handlers on " + db);
        }
        foreach (var unsupportedConstant in new[] {
            "let value='100'; view.Filters.Status=value;",
            "var value='100'; view.Filters.Status=value;",
            "const value=context.Id; view.Filters.Status=value;",
            "const value='10'+'0'; view.Filters.Status=value;",
            "const value={Status:'100'}; view.Filters.Status=value.Status;",
            "const value=view; value.Filters.Status='100';",
            "const view='100'; view.Filters.Status='100';",
            "view.Filters.Status=value; const value='100';",
            "const value='100'; value='200'; view.Filters.Status=value;",
            "const value='100'; unknown(value); view.Filters.Status=value;",
            "const {value}={value:'100'}; view.Filters.Status=value;",
            "const value=null; view.Filters.Status=value;",
            "const value=100; view.Filters.Status=value;",
            "const name=context.Column; view.Filters[name]='100';",
            "const value='100'; if(context.Id) view.Filters.Status=value;",
            "const value='100'; view.FilterNegative('Status',value);"
        })
        {
            check(ViewMutationPlanner.Analyze([MutationSite(unsupportedConstant)], Dbms.SQLServer).Indexes.Count == 0, "Unresolved constant body does not generate partial candidates");
        }
        foreach (var unknownColumn in new[] {
            "const key=1; view.Filters[key]='100';",
            "const key=true; view.Filters[key]='100';",
            "const key='Status'; view.Filters[key+'']='100';",
            "view.Filters[key]='100'; const key='Status';",
            "const key='__proto__'; view.Filters[key]='100';",
            "const key='PRIVATE_UNKNOWN_COLUMN'; view.Filters[key]='100';",
            "const group='PRIVATE_UNKNOWN_GROUP'; view[group].Status='100';",
            "const method='PRIVATE_UNKNOWN_METHOD'; view[method]();",
            "const key='Status'; key='Manager'; view.Filters[key]='100';"
        })
        {
            var unresolved = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Manager='1';" + unknownColumn)], Dbms.SQLServer);
            check(unresolved.Indexes.Count == 0 && unresolved.Diagnostics.All(n => !n.Message.Contains("PRIVATE_UNKNOWN_COLUMN")), "Unknown column expression discards whole inference without exposing keys");
        }
        foreach (var unknownOperation in new[] {
            "view[group].Status='100'; const group='Filters';",
            "const group=context.Group; view[group].Status='100';",
            "const group=1; view[group].Status='100';",
            "const method=true; view[method]();",
            "const method='ClearFilters'; view[method](true);",
            "const method='FilterNegative'; view[method]('Status','false');",
            "const method='ClearFilters'; other[method]();",
            "const method='ClearFilters'; view[method+'']();",
            "const group='Filters'; group='Sorters'; view[group].Status='100';",
            "const method='ClearFilters'; const call=view[method]; call();"
        })
        {
            check(ViewMutationPlanner.Analyze([MutationSite("view.Filters.Manager='1';" + unknownOperation)], Dbms.SQLServer).Indexes.Count == 0, "Unresolved operation discards whole inference");
        }
        var computedNotes = ServerScriptDiagnostics.Analyze([MutationSite("const PRIVATE_OPERATION_NAME='PRIVATE_OPERATION_VALUE'; view[PRIVATE_OPERATION_NAME].Status='100'; view[PRIVATE_OPERATION_NAME]();")]);
        check(computedNotes.Any(n => n.Message.Contains("computed view group")) && computedNotes.Any(n => n.Message.Contains("computed view method")), "Computed view operations are diagnosed without evaluation");
        check(computedNotes.All(n => !n.Message.Contains("PRIVATE_OPERATION")), "Computed operation diagnostics hide arbitrary names and values");
        var privateConstant = ViewMutationPlanner.Analyze([MutationSite("const value='PRIVATE_CONSTANT_VALUE'; view.Filters.ClassA=value;")], Dbms.SQLServer);
        check(privateConstant.Diagnostics.All(n => !n.Message.Contains("PRIVATE_CONSTANT_VALUE")), "Constant values are not disclosed in diagnostics");
        foreach (var unsupportedSort in new[] { "2", "'2'", "'release'", "3", "-1", "0.5", "true", "null", "context.Direction", "'ASC'" })
        {
            check(ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100'; view.Sorters.UpdatedTime=" + unsupportedSort + ";")], Dbms.SQLServer).Indexes.Count == 0, "Unsupported sorter discards whole inference");
        }
        check(ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status=100;")], Dbms.SQLServer).Indexes.Count == 0, "Numeric sorter support does not expand filter values");
        foreach (var unsupportedClear in new[] { "view.ClearFilters(true);", "view.ClearFilters(context.Id);", "other.ClearFilters();", "if(context.Id) view.ClearFilters();" })
        {
            check(ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100';" + unsupportedClear)], Dbms.SQLServer).Indexes.Count == 0, "Unsupported clear call discards whole inference");
        }
        check(ServerScriptDiagnostics.Analyze([MutationSite("view.ClearFilters();")]).Any(n => n.Message.Contains("ClearFilters")), "ClearFilters is diagnosed");
        foreach (var unsupportedNegative in new[] { "view.FilterNegative();", "view.FilterNegative(column);", "view.FilterNegative('Status',context.Flag);", "view.FilterNegative('Status','false');", "view.FilterNegative('Status',0);", "view.FilterNegative('Status',true,false);", "view.FilterNegative('PRIVATE_KEY');", "other.FilterNegative('Status');" })
        {
            var result = ViewMutationPlanner.Analyze([MutationSite("view.Filters.Status='100';" + unsupportedNegative)], Dbms.SQLServer);
            check(result.Indexes.Count == 0 && result.Diagnostics.All(n => !n.Message.Contains("PRIVATE_KEY")), "Unsupported negative call discards whole inference without leaking arguments");
        }
        check(ServerScriptDiagnostics.Analyze([MutationSite("view.FilterNegative('PRIVATE_KEY',secret);")]).Any(n => n.Message.Contains("FilterNegative")) && ServerScriptDiagnostics.Analyze([MutationSite("view.FilterNegative('PRIVATE_KEY',secret);")]).All(n => !n.Message.Contains("PRIVATE_KEY")), "Negative call is diagnosed without exposing arguments");
        foreach (var unsupported in new[] { "'UNKNOWN_PRIVATE_VALUE'", "0", "4", "2.5", "true", "null", "context.UserId" })
        {
            var result = ViewMutationPlanner.Analyze([SearchSite(unsupported)], Dbms.SQLServer);
            check(result.Indexes.Count == 0 && result.Diagnostics.All(n => !n.Message.Contains("PRIVATE_VALUE")), "Unknown SearchTypes rejects whole inference without exposing value");
        }
        check(ServerScriptDiagnostics.Analyze([SearchSite("'ExactMatch'")]).Any(n => n.Message.Contains("SearchTypes")), "SearchTypes mutation appears in diagnostics");
        check(ViewMutationPlanner.Analyze([OrderedSite("view.Filters.Status='100'", "(view.Sorters.UpdatedTime='asc');")], Dbms.SQLServer).Indexes.Count == 0, "Cross-script ASI call is not treated as independent assignments");
        check(ViewMutationPlanner.Analyze([OrderedSite("view.Filters.Status='100'; // comment", "view.Sorters.UpdatedTime='asc';")], Dbms.SQLServer).Indexes.Count > 0, "Newline terminates cross-script line comment");
        foreach (var wrapper in new[] { "TryCatch", "Functionalize" })
        {
            var wrapped = OrderedSite("view.Filters.Status='100';", "view.Sorters.UpdatedTime='asc';");
            var node = JsonNode.Parse(wrapped.SiteSettings.GetRawText())!.AsObject();
            node["ServerScripts"]![1]![wrapper] = true;
            check(ViewMutationPlanner.Analyze([wrapped with { SiteSettings = Json.Parse(node.ToJsonString()) }], Dbms.SQLServer).Indexes.Count == 0, "Wrapper boundary requires review");
            node["ServerScripts"]![1]!["Disabled"] = true;
            check(ViewMutationPlanner.Analyze([wrapped with { SiteSettings = Json.Parse(node.ToJsonString()) }], Dbms.SQLServer).Indexes.Count > 0, "Disabled wrapped handler is ignored");
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
        foreach (var body in new[] { "view.Filters.Status=context.UserId;", "view.Filters.Status+='100';", "view.Filters[column]='100';", "view.Filters.Status='100'; unknown();", "const view=other; view.Filters.Status='100';", "if (true) view.Filters.Status='100';", "view.Sorters.UpdatedTime='invalid';", "view.Filters.Status='100'; return;", "//Include: Other\nview.Filters.Status='100';" })
        {
            check(ViewMutationPlanner.Analyze([MutationSite(body)], Dbms.SQLServer).Indexes.Count == 0, "Unresolved mutation does not create candidates");
        }
        var sharedNode = JsonNode.Parse(source.SiteSettings.GetRawText())!.AsObject();
        sharedNode["ServerScripts"]!.AsArray().Add(JsonNode.Parse("{\"Shared\":true,\"Body\":\"unknown();\"}"));
        var shared = source with { SiteSettings = Json.Parse(sharedNode.ToJsonString()) };
        check(ViewMutationPlanner.Analyze([shared], Dbms.SQLServer).Indexes.Count == 0, "Shared code blocks View inference");
        sharedNode["ServerScripts"]!.AsArray()[1]!["Shared"] = false;
        sharedNode["ServerScripts"]!.AsArray()[1]!["WhenViewProcessing"] = true;
        check(ViewMutationPlanner.Analyze([source with { SiteSettings = Json.Parse(sharedNode.ToJsonString()) }], Dbms.SQLServer).Indexes.Count == 0, "Unknown second View handler prevents combination");
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
