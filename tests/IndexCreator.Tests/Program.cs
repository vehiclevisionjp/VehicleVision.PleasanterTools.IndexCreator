using VehicleVision.PleasanterTools.IndexCreator;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (UserError) { passed++; return; }
    throw new Exception("FAIL: " + name);
}
Site SiteWith(string settings, long count = 20000, string table = "Results") => new(1, table, Json.Parse(settings), count);
Analysis Plan(Dbms db, string settings) => new Planner(db).Analyze([SiteWith(settings)]);

Check(Plan(Dbms.SQLServer, "{}").Indexes.Count == 1, "SQL Server default sort");
Check(Plan(Dbms.MySQL, "{}").Indexes.Count == 1, "MySQL default sort");
Check(Plan(Dbms.PostgreSQL, "{}").Indexes.Count == 0, "PostgreSQL primary key covers default sort");
Check(new Planner(Dbms.SQLServer).Analyze([SiteWith("{}", 9999)]).Indexes.Count == 0, "Minimum record threshold");
var filtered = """
    {"Columns":[{"ColumnName":"ClassA","ChoicesText":"A\nB"},{"ColumnName":"NumA","Nullable":true}],
     "Views":[{"ColumnFilterHash":{"ClassA":"A"},"ColumnSorterHash":{"NumA":"desc"}}]}
    """;
var analysis = Plan(Dbms.SQLServer, filtered);
var spec = analysis.Indexes.Single(i => i.Keys.Any(k => k.Column == "ClassA"));
Check(spec.Keys.Select(k => k.Column).SequenceEqual(["SiteId", "ClassA", "NumA", "UpdatedTime", "ResultId"]), "Equality before sort and tie breakers");
Check(spec.Keys[2].Desc, "Sort descending");
Check(Plan(Dbms.MySQL, filtered).Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA" && k.Prefix == 100)), "MySQL TEXT prefix");
var range = Plan(Dbms.PostgreSQL, """{"Views":[{"ColumnFilterHash":{"DateA":"[\"2026-01-01,2026-12-31\"]"},"ColumnSorterHash":{"CreatedTime":"desc"}}]}""");
Check(range.Indexes.Single().Keys.Select(k => k.Column).SequenceEqual(["SiteId", "DateA"]), "Range stops sort coverage");
var prefix = Plan(Dbms.PostgreSQL, """{"Columns":[{"ColumnName":"ClassA","SearchType":3}],"Views":[{"ColumnFilterHash":{"ClassA":"A"}}]}""");
Check(prefix.Indexes.Single().Keys[1].Pattern, "PostgreSQL prefix opclass");
Check(!Plan(Dbms.SQLServer, """{"Columns":[{"ColumnName":"ClassA","MultipleSelections":true,"ChoicesText":"A"}],"Views":[{"ColumnFilterHash":{"ClassA":"A"}}]}""").Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "Multiple selections excluded");
Check(!Plan(Dbms.SQLServer, """{"Views":[{"ColumnSorterHash":{"NumA":"desc"}}]}""").Indexes.Any(i => i.Keys.Any(k => k.Column == "NumA")), "Wrapped sort excluded");
Check(Plan(Dbms.PostgreSQL, """{"Views":[{"Incomplete":true}]}""").Indexes.Single().Keys[1].Column == "Status", "Incomplete range");
Check(Plan(Dbms.PostgreSQL, """{"Columns":[{"ColumnName":"ClassA","ChoicesText":"[[123]]"}]}""").Indexes.Single().Keys[1].Column == "ClassA", "Link choices");
Check(Plan(Dbms.PostgreSQL, """{"Summaries":[{"LinkColumn":"ClassB"}]}""").Indexes.Single().Keys[1].Column == "ClassB", "Summary link");
Check(!Plan(Dbms.SQLServer, """{"Columns":[{"ColumnName":"ClassA","ChoicesText":"A"}],"Views":[{"ColumnFilterHash":{"ClassA":"A"},"ColumnFilterNegatives":["ClassA"]}]}""").Indexes.Any(i => i.Keys.Any(k => k.Column == "ClassA")), "Negative filters excluded");
Check(Plan(Dbms.PostgreSQL, """{"Views":[{"ColumnFilterHash":{"and_1":"{\"Status\":\"[100]\"}"}}]}""").Indexes.Single().Keys[1].Column == "Status", "Nested AND filters");
Check(!Plan(Dbms.SQLServer, """{"Views":[{"ColumnFilterHash":{"ClassA~2,ClassB":"A"}}]}""").Indexes.Any(i => i.Keys.Any(k => k.Column.Contains('~'))), "Joined filters excluded");
Check(spec.Name == new IndexSpec(spec.Table, spec.Keys.ToArray()).Name, "Stable index name");
Check(spec.Name.Length < 63 && IndexSpec.IsManaged(spec.Table, spec.Name), "Portable owned name");
Check(!IndexSpec.IsManaged("Results", "vvic_v1_Results_standard"), "Unrelated prefix not owned");
Check(spec.Name.StartsWith("IX_vvplic_Results_1_ClassA_", StringComparison.Ordinal), "Readable index purpose");
var sharedSites = new[] { SiteWith("{}") with { SiteId = 12 }, SiteWith("{}") with { SiteId = 3 } };
var sharedIndex = new Planner(Dbms.SQLServer).Analyze(sharedSites).Indexes.Single();
Check(sharedIndex.SiteId == 3 && new Planner(Dbms.SQLServer).Analyze(sharedSites.Reverse().ToArray()).Indexes.Single().Name == sharedIndex.Name, "Shared index uses deterministic representative site");
Check((spec with { SiteId = long.MaxValue }).Name.Length <= 63 && IndexSpec.IsManaged("Results", (spec with { SiteId = long.MaxValue }).Name), "Large site ID remains portable");
Check(!IndexSpec.IsManaged("Issues", spec.Name), "Ownership requires matching table");
var existing = new ExistingIndex(spec.Table, spec.Name, spec.Keys);
Check(Reconciler.Plan([spec], [existing], true).Single().Kind == ChangeKind.Keep, "Repeat is no-op");
Check(Reconciler.Plan([spec], [existing with { Valid = false }], false).Single().Kind == ChangeKind.Repair, "Invalid index repair");
Reject(() => Reconciler.Plan([spec], [existing with { Keys = [new("SiteId")] }], false), "Definition collision");
Check(Reconciler.Plan([spec], [existing with { Name = "standard" }], true).Single().Kind == ChangeKind.Keep, "Existing unmanaged coverage");
Check(Reconciler.Plan([], [existing with { Name = "standard" }], true).Count == 0, "Never prune standard indexes");
Check(Reconciler.Plan([], [existing], false).Count == 0, "Prune opt-in");
Check(Reconciler.Plan([], [existing], true).Single().Kind == ChangeKind.Drop, "Obsolete owned index prune");
var shorter = new IndexSpec("Results", [new("SiteId"), new("ClassA")]);
var reconciliation = Reconciler.Plan([shorter], [existing], true);
Check(reconciliation[0].Kind == ChangeKind.Create && reconciliation[1].Kind == ChangeKind.Drop, "Replace owned covering index before prune");
Check(Reconciler.Plan([spec], [existing], false, true).Single().Kind == ChangeKind.Repair, "Force rebuild only owned matching index");
Check(Reconciler.Plan([spec], [existing with { Name = "standard" }], false, true).Single().Kind == ChangeKind.Keep, "Force preserves standard index");
var slash = Options.Parse(["_rds", "/p", "/opt/pleasanter app/Implem.Pleasanter", "/y"]);
Check(slash.Action == "apply" && slash.Path == "/opt/pleasanter app/Implem.Pleasanter" && slash.Yes, "CodeDefiner slash options and absolute path");
Check(Options.Parse(["_rds", "/c", "/f"]).Action == "plan", "Check always prevents apply");
Reject(() => Options.Parse(["_rds", "/p", "app", "-p", "other"]), "Duplicate path aliases");
Reject(() => Options.Parse(["_rds", "/y", "-y"]), "Duplicate yes aliases");
Reject(() => Json.ReadSites("""[{"SiteId":1,"ReferenceType":"Results","RecordCount":1,"SiteSettings":{"Views":"broken"}}]"""), "Malformed collection fails closed");
var layout = Path.Combine(Path.GetTempPath(), "vvic-layout", "IndexCreator", "nested", "bin");
Check(Configuration.ResolvePath(null, layout) == Path.Combine(Path.GetTempPath(), "vvic-layout", "Implem.Pleasanter"), "Default sibling path independent of working directory");
var configRoot = Path.Combine(Path.GetTempPath(), "IndexCreator-config-" + Guid.NewGuid().ToString("N"));
var appRoot = Path.Combine(configRoot, "Pleasanter app");
var parameters = Path.Combine(appRoot, "App_Data", "Parameters");
var redirected = Path.Combine(configRoot, "parameters with spaces");
Directory.CreateDirectory(parameters);
Directory.CreateDirectory(redirected);
try
{
    File.WriteAllText(Path.Combine(parameters, "Env.json"), System.Text.Json.JsonSerializer.Serialize(new { ParametersPath = redirected }));
    File.WriteAllText(Path.Combine(redirected, "Rds.json"), """{"Dbms":"PostgreSQL","OwnerConnectionString":"Database=#ServiceName#;Username=test;Password=test-only","DisableIndexChangeDetection":true}""");
    File.WriteAllText(Path.Combine(redirected, "Service.json"), """{"Name":"ExampleService"}""");
    var config = Configuration.Load(Options.Parse(["_rds", "/p", appRoot, "/c"]));
    Check(config.Dbms == Dbms.PostgreSQL && config.ConnectionString.Contains("Database=ExampleService", StringComparison.Ordinal), "Env redirect and service placeholder");
    Check(config.Schema == "ExampleService" && config.DisableIndexChangeDetection, "Service schema and safety setting");
}
finally { Directory.Delete(configRoot, true); }
Reject(() => Options.Parse(["apply", "--sites", "sites.json"]), "Offline apply refused");
Reject(() => Options.Parse(["plan", "--mysql-prefix", "0"]), "Invalid prefix");
Reject(() => Options.Parse(["plan", "--unknown"]), "Unknown option");
try { Json.ReadSites("""[{"SiteId":1,"ReferenceType":"Results","RecordCount":1,"SiteSettings":"broken"}]"""); throw new Exception("Invalid settings accepted"); }
catch (System.Text.Json.JsonException) { passed++; }
var viewSettings = """{"GridColumns":["ClassA","Title","ResultId"],"Columns":[{"ColumnName":"ClassA","GridLabelText":"分類見出し","LabelText":"分類"},{"ColumnName":"Title","LabelText":"タイトル"}]}""";
var siteView = new ViewPlanner(Dbms.PostgreSQL).Generate([SiteWith(viewSettings)]).Single();
Check(siteView.Name == "View_vvplic_Results_1_Untitled", "Site ID and name rule");
Check(siteView.Columns.Select(c => c.Source).SequenceEqual(["ClassA", "Title", "ResultId"]), "Grid column order");
Check(siteView.Columns[0].Alias == "分類見出し", "Grid label takes precedence");
Check(siteView.Select(new(Dbms.PostgreSQL, "Implem.Pleasanter")).Contains("INNER JOIN", StringComparison.Ordinal), "Title sourced from Items");
Check(!SiteView.IsManaged("View_vvplic_Results_0_invalid") && !SiteView.IsManaged("standard"), "Strict view ownership rule");
Check((siteView with { SiteName = "顧客 / 一覧" }).Name == "View_vvplic_Results_1_顧客_一覧", "Readable normalized site name");
Check(System.Text.Encoding.UTF8.GetByteCount((siteView with { SiteName = new string('顧', 100) }).Name) <= 63, "Portable UTF8 identifier length");
Check(new ViewPlanner(Dbms.MySQL).Generate([SiteWith(viewSettings)]).Single().Name == siteView.Name, "View name independent of DBMS");
Reject(() => new ViewPlanner(Dbms.SQLServer).Generate([SiteWith("""{"GridColumns":["ClassA~2,Title"]}""")]), "Joined grid cannot be silently omitted");
Reject(() => new ViewPlanner(Dbms.SQLServer).Generate([SiteWith("{}")]), "Missing defaults fail closed");
Reject(() => Options.Parse(["_views", "--sites", "sites.json"]), "Offline view apply refused");
Check(Options.Parse(["_views", "/c"]).Action == "views", "Views check prevents mutation");
Check(RuntimeLog.FileName(new DateTime(2026, 10, 7, 12, 34, 56)) == "VehicleVision.PleasanterTools.IndexCreator_20261007_123456.log", "CodeDefiner log naming convention");
Check(Plan(Dbms.SQLServer, "{}").Indexes.Single().Keys[^1].Column == "ResultId", "Result identifier mapping");
Check(new Planner(Dbms.SQLServer).Analyze([SiteWith("{}", table: "Wikis")]).Indexes.Single().Keys[^1].Column == "WikiId", "Wiki identifier mapping");
Reject(() => new ViewPlanner(Dbms.SQLServer).Generate([SiteWith("""{"GridColumns":["WikiId","ClassA"]}""", table: "Wikis")]), "Wiki sites reject columns the table lacks");
Reject(() => new ViewPlanner(Dbms.SQLServer).Generate([SiteWith("""{"GridColumns":["WikiId","Status"]}""", table: "Wikis")]), "Wiki sites reject Status");
Check(new ChoicePlanner().Generate([SiteWith("""{"Columns":[{"ColumnName":"ClassA","ControlType":"Spinner","ChoicesText":"1"},{"ColumnName":"ClassB","ControlType":"ChoicesText","ChoicesText":"1"}]}""")]).Single().ChoiceColumn == "ClassB", "Choice export follows ControlType");
Check(Json.ReadSites("""[{"SiteId":3,"ReferenceType":"Wikis","RecordCount":1,"SiteSettings":{}}]""").Count == 1, "Wiki sites accepted");
var choiceSite = SiteWith("""{"Columns":[{"ColumnName":"ClassA","ChoicesText":"100,受付\n200,完了\n100,duplicate\n300\\,x,引用'名称"}]}""") with { Title = "選択肢" };
var choiceView = new ChoicePlanner().Generate([choiceSite]).Single();
Check(choiceView.Choices!.Count == 3 && choiceView.Choices[1].Text == "完了", "Choice labels and duplicate values");
Check(choiceView.Choices![2].Value == "300,x", "Escaped choice comma");
Check(choiceView.Columns.Select(c => c.Alias).SequenceEqual(["Value", "Text", "TextMini"]), "Choice view has exactly three fixed columns");
var multipleChoices = new ChoicePlanner().Generate([SiteWith("""{"Columns":[{"ColumnName":"ClassA","ChoicesText":"1,表示,短縮"},{"ColumnName":"ClassB","ChoicesText":"1,別表示"}]}""")]);
Check(multipleChoices.Count == 2 && multipleChoices.Select(v => v.Name).Distinct().Count() == 2, "Multiple choice columns create distinct views in one site");
Check(multipleChoices[0].Choices![0].TextMini == "短縮" && multipleChoices[1].Choices![0].TextMini == "別表示", "Short label and fallback");
Check(SiteView.IsManaged(choiceView.Name, true) && !SiteView.IsManaged(choiceView.Name), "Separate choice ownership scope");
Check(Options.Parse(["_choice-lists", "/c"]).Action == "views-choices", "Choice check prevents writes");
Check(new SqlDialect(Dbms.MySQL, "s").TextLiteral("a\\'") == "CONVERT(0x615C27 USING utf8mb4)" && new SqlDialect(Dbms.MySQL, "s").TextLiteral("a'b") == "'a''b'" && new SqlDialect(Dbms.PostgreSQL, "s").TextLiteral("a\\b") == "'a\\b'", "MySQL backslash literal is mode independent");
Reject(() => new ChoicePlanner().Generate([SiteWith("""{"Columns":[{"ColumnName":"ClassA","ChoicesText":"[[123]]"}]}""")]), "Dynamic choices cannot silently become static values");
Console.WriteLine($"Unit checks passed: {passed}");

if (args.Contains("--integration"))
{
    var dbName = Environment.GetEnvironmentVariable("INDEXCREATOR_TEST_DBMS");
    var cs = Environment.GetEnvironmentVariable("INDEXCREATOR_TEST_CONNECTION");
    if (!Enum.TryParse<Dbms>(dbName, out var dbms) || string.IsNullOrWhiteSpace(cs)) throw new Exception("Integration requires INDEXCREATOR_TEST_DBMS and INDEXCREATOR_TEST_CONNECTION.");
    var target = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
    if (!target.ContainsKey("Database") || target["Database"].ToString() != "IndexCreatorTest") throw new Exception("Integration only accepts the dedicated IndexCreatorTest database.");
    // 専用検証 DB だけを使用。実運用 DB を渡さないこと。
    var schema = dbms == Dbms.SQLServer ? "dbo" : "IndexCreatorTest";
    var configuration = new Configuration(dbms, schema, cs, true, 120);
    await using var database = new Database(configuration);
    await database.Open(default);
    var d = new SqlDialect(dbms, schema);
    if (dbms == Dbms.PostgreSQL) await database.Execute($"CREATE SCHEMA IF NOT EXISTS {d.Quote(schema)}");
    var str = dbms == Dbms.SQLServer ? "nvarchar(max)" : "text";
    var classType = dbms switch { Dbms.SQLServer => "nvarchar(1024)", Dbms.PostgreSQL => "varchar(1024)", _ => "text" };
    foreach (var table in new[] { "Results", "Issues", "Wikis" })
        await database.Execute($"CREATE TABLE {d.Table(table)} ({d.Quote("SiteId")} bigint NOT NULL, {d.Quote(table[..^1] + "Id")} bigint NOT NULL, {d.Quote("UpdatedTime")} timestamp NOT NULL, {d.Quote("ClassA")} {classType}, {d.Quote("Status")} int)".Replace(dbms == Dbms.SQLServer ? "timestamp" : "__unused__", "datetime2", StringComparison.Ordinal));
    await database.Execute($"CREATE TABLE {d.Table("Sites")} ({d.Quote("SiteId")} bigint NOT NULL, {d.Quote("ReferenceType")} varchar(20) NOT NULL, {d.Quote("SiteSettings")} {str}, {d.Quote("Title")} {str})");
    await database.Execute($"CREATE TABLE {d.Table("Items")} ({d.Quote("SiteId")} bigint NOT NULL, {d.Quote("ReferenceId")} bigint NOT NULL, {d.Quote("Title")} {str})");
    if (dbms == Dbms.MySQL)
    {
        await database.Execute($"CREATE FULLTEXT INDEX {d.Quote("fulltext_Items")} ON {d.Table("Items")} ({d.Quote("Title")})");
        Check((await database.ReadIndexes(default)).Any(i => i.Name == "fulltext_Items" && !i.Plain), "Fulltext catalog null collation does not break planning");
    }
    await database.Execute($"INSERT INTO {d.Table("Items")} VALUES (1,1,'Display Title')");
    foreach (var table in new[] { "Results", "Issues", "Wikis" })
    {
        await database.Execute($"INSERT INTO {d.Table(table)} ({d.Quote("SiteId")},{d.Quote(table[..^1] + "Id")},{d.Quote("UpdatedTime")},{d.Quote("ClassA")},{d.Quote("Status")}) VALUES ({(table == "Results" ? 1 : table == "Issues" ? 2 : 3)},1,'2026-01-01','123',100)");
        await database.Execute($"INSERT INTO {d.Table("Sites")} VALUES ({(table == "Results" ? 1 : table == "Issues" ? 2 : 3)},'{table}', '{{\"Columns\":[{{\"ColumnName\":\"ClassA\",\"ChoicesText\":\"A\"}}],\"Views\":[{{\"ColumnFilterHash\":{{\"ClassA\":\"A\"}}}}]}}', 'Test site')");
        await database.Execute($"CREATE INDEX {d.Quote("standard_" + table)} ON {d.Table(table)} ({d.Quote(table[..^1] + "Id")})");
    }
    await database.AcquireLock(default);
    await using (var second = new Database(configuration))
    {
        await second.Open(default);
        try { await second.AcquireLock(default); throw new Exception("Second apply acquired lock"); }
        catch (UserError) { passed++; }
    }
    var sites = await database.ReadSites(default);
    var desired = new Planner(dbms, 0).Analyze(sites).Indexes;
    Check(sites.Count == 3 && sites.All(s => s.RecordCount == 1), "Live site counts");
    await database.ValidateColumns(desired, default);
    var first = Reconciler.Plan(desired, await database.ReadIndexes(default), true);
    await database.Apply(first, default);
    Check(Reconciler.Plan(desired, await database.ReadIndexes(default), true).All(c => c.Kind == ChangeKind.Keep), "Live repeat apply no-op");
    await database.Apply(Reconciler.Plan(desired, await database.ReadIndexes(default), false, true), default);
    Check(Reconciler.Plan(desired, await database.ReadIndexes(default), true).All(c => c.Kind == ChangeKind.Keep), "Live force rebuild");
    var snapshotRejected = false;
    try { await database.Apply([], default, [sites[0]]); }
    catch (UserError) { snapshotRejected = true; }
    Check(snapshotRejected, "Changed site snapshot aborts apply");
    if (dbms == Dbms.PostgreSQL)
    {
        var patternSpec = new IndexSpec("Results", [new("SiteId"), new("ClassA", Pattern: true)]);
        await database.Apply(Reconciler.Plan([patternSpec], await database.ReadIndexes(default), true), default);
        Check(Reconciler.Plan([patternSpec], await database.ReadIndexes(default), true).Single().Kind == ChangeKind.Keep, "Live pattern opclass roundtrip");
    }
    var updated = new Planner(dbms, 0).Analyze([SiteWith("""{"Views":[{"Incomplete":true}]}""", 1)]).Indexes;
    await database.Apply(Reconciler.Plan(updated, await database.ReadIndexes(default), true), default);
    var final = await database.ReadIndexes(default);
    Check(final.Count(i => i.Name.StartsWith("standard_", StringComparison.Ordinal)) == 3, "Live standard indexes preserved");
    Check(final.Where(i => i.Managed).All(i => updated.Any(s => s.Name == i.Name)), "Live obsolete indexes removed");
    foreach (var i in final.Where(i => i.Managed)) await database.Execute(d.Drop(i.Table, i.Name));
    await database.Apply(Reconciler.Plan(updated, await database.ReadIndexes(default), false), default);
    Check(Reconciler.Plan(updated, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Recover after managed indexes disappear");
    var liveView = new ViewPlanner(dbms).Generate([SiteWith(viewSettings) with { Title = "顧客一覧" }]).Single();
    await database.ApplyViews([liveView], false, default);
    await database.ApplyViews([liveView], false, default);
    Check((await database.ReadViewNames(default)).Count(n => n == liveView.Name) == 1, "Live view repeat update");
    await using (System.Data.Common.DbConnection query = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(cs), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(cs), _ => new MySqlConnector.MySqlConnection(cs) })
    {
        await query.OpenAsync();
        await using var command = query.CreateCommand();
        command.CommandText = "SELECT * FROM " + d.Table(liveView.Name);
        await using var rows = await command.ExecuteReaderAsync();
        Check(await rows.ReadAsync() && rows.GetName(0) == "分類見出し" && rows.GetString(0) == "123" && rows.GetString(1) == "Display Title" && Convert.ToInt64(rows.GetValue(2)) == 1, "Live view columns and Items title values");
        Check(!await rows.ReadAsync(), "Site view row isolation");
    }
    if (dbms == Dbms.PostgreSQL)
    {
        var changedView = liveView with { Columns = [new("ClassA", "New label")] };
        var blocked = false;
        try { await database.ApplyViews([changedView], false, default); } catch (UserError) { blocked = true; }
        Check(blocked, "PostgreSQL column migration preserves existing view");
        await database.Execute("GRANT SELECT ON " + d.Table(liveView.Name) + " TO PUBLIC");
        await database.ApplyViews([changedView], false, default, force: true);
        Check((await database.ReadViewNames(default)).Contains(liveView.Name), "PostgreSQL forced shape update");
        await using (var grantQuery = new Npgsql.NpgsqlConnection(cs))
        {
            await grantQuery.OpenAsync();
            await using var grantCommand = grantQuery.CreateCommand();
            grantCommand.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_PRIVILEGES WHERE TABLE_SCHEMA=" + SqlDialect.Literal(schema) + " AND TABLE_NAME=" + SqlDialect.Literal(liveView.Name) + " AND grantee='PUBLIC' AND privilege_type='SELECT'";
            Check(Convert.ToInt64(await grantCommand.ExecuteScalarAsync()) == 1, "PostgreSQL replacement preserves public grant");
        }
        await database.Execute("CREATE VIEW " + d.Table("dependent_site_view") + " AS SELECT * FROM " + d.Table(liveView.Name));
        var dependencyBlocked = false;
        try { await database.ApplyViews([liveView], false, default, force: true); } catch (System.Data.Common.DbException) { dependencyBlocked = true; }
        Check(dependencyBlocked && (await database.ReadViewNames(default)).Contains(liveView.Name), "PostgreSQL dependency prevents destructive replacement");
        await database.Execute("DROP VIEW " + d.Table("dependent_site_view"));
        await database.ApplyViews([liveView], false, default, force: true);
    }
    await database.Execute((dbms == Dbms.SQLServer ? "CREATE VIEW " : "CREATE OR REPLACE VIEW ") + d.Table("standard_site_view") + " AS " + liveView.Select(d));
    var wikiView = new ViewPlanner(dbms).Generate([SiteWith("""{"GridColumns":["WikiId","Title"]}""", table: "Wikis") with { SiteId = 3, Title = "Wiki" }]).Single();
    await database.ApplyViews([wikiView], false, default);
    Check((await database.ReadViewNames(default)).Contains(wikiView.Name), "Live Wiki view creation");
    await database.ApplyViews([choiceView], false, default, choices: true);
    await database.ApplyViews([choiceView], false, default, choices: true);
    await database.ApplyViews(multipleChoices, false, default, choices: true);
    var choiceNames = await database.ReadViewNames(default);
    Check(multipleChoices.All(v => choiceNames.Contains(v.Name)), "Live multiple choice columns coexist in one site");
    await using (System.Data.Common.DbConnection query = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(cs), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(cs), _ => new MySqlConnector.MySqlConnection(cs) })
    {
        await query.OpenAsync();
        await using var command = query.CreateCommand();
        command.CommandText = "SELECT * FROM " + d.Table(choiceView.Name) + " ORDER BY " + d.Quote("Value");
        await using var rows = await command.ExecuteReaderAsync();
        Check(await rows.ReadAsync() && rows.GetString(0) == "100" && rows.GetString(1) == "受付" && rows.GetString(2) == "受付", "Live fixed choice value and Unicode label");
        await rows.CloseAsync();
        var slashView = new SiteView(9, "Results", [new("Value", "Value"), new("Text", "Text"), new("TextMini", "TextMini")], "Slash", [new("x\\", "a\\' b", "c")], "ClassA");
        await database.ApplyViews([slashView], false, default, choices: true);
        command.CommandText = "SELECT " + d.Quote("Value") + ", " + d.Quote("Text") + " FROM " + d.Table(slashView.Name);
        await using var slashRows = await command.ExecuteReaderAsync();
        Check(await slashRows.ReadAsync() && slashRows.GetString(0) == "x\\" && slashRows.GetString(1) == "a\\' b", "Live backslash choice value is preserved");
        await slashRows.CloseAsync();
        var manyView = new SiteView(10, "Results", slashView.Columns, "Many", Enumerable.Range(1, 5000).Select(n => new ChoiceRow(n.ToString(), "表示" + n, "短" + n)).ToArray(), "ClassA");
        await database.ApplyViews([manyView], false, default, choices: true);
        command.CommandText = "SELECT COUNT(*) FROM " + d.Table(manyView.Name);
        Check(Convert.ToInt64(await command.ExecuteScalarAsync()) == 5000, "Live view holds thousands of fixed choices");
    }
    await database.ApplyViews([], true, default);
    Check((await database.ReadViewNames(default)).Contains("standard_site_view") && !(await database.ReadViewNames(default)).Contains(liveView.Name), "Only managed views pruned");
    Check((await database.ReadViewNames(default)).Contains(choiceView.Name), "Grid view prune preserves choice views");
    await database.ApplyViews([], true, default, choices: true);
    Check(!(await database.ReadViewNames(default)).Contains(choiceView.Name), "Choice prune removes only choice views");
    await database.Execute("DROP VIEW " + d.Table("standard_site_view"));
    if (args.Contains("--benchmark")) await Benchmark.Run(configuration, database, d);
    foreach (var table in new[] { "Sites", "Items", "Results", "Issues", "Wikis" }) await database.Execute($"DROP TABLE {d.Table(table)}");
    Console.WriteLine($"Integration checks passed ({dbms}). Total checks: {passed}");
}
