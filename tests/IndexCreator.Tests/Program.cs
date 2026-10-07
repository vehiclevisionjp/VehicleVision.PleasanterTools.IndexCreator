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
    foreach (var table in new[] { "Results", "Issues" })
        await database.Execute($"CREATE TABLE {d.Table(table)} ({d.Quote("SiteId")} bigint NOT NULL, {d.Quote(table == "Results" ? "ResultId" : "IssueId")} bigint NOT NULL, {d.Quote("UpdatedTime")} timestamp NOT NULL, {d.Quote("ClassA")} {classType}, {d.Quote("Status")} int)".Replace(dbms == Dbms.SQLServer ? "timestamp" : "__unused__", "datetime2", StringComparison.Ordinal));
    await database.Execute($"CREATE TABLE {d.Table("Sites")} ({d.Quote("SiteId")} bigint NOT NULL, {d.Quote("ReferenceType")} varchar(20) NOT NULL, {d.Quote("SiteSettings")} {str})");
    foreach (var table in new[] { "Results", "Issues" })
    {
        await database.Execute($"INSERT INTO {d.Table(table)} ({d.Quote("SiteId")},{d.Quote(table == "Results" ? "ResultId" : "IssueId")},{d.Quote("UpdatedTime")},{d.Quote("ClassA")},{d.Quote("Status")}) VALUES ({(table == "Results" ? 1 : 2)},1,'2026-01-01','123',100)");
        await database.Execute($"INSERT INTO {d.Table("Sites")} VALUES ({(table == "Results" ? 1 : 2)},'{table}', '{{\"Columns\":[{{\"ColumnName\":\"ClassA\",\"ChoicesText\":\"A\"}}],\"Views\":[{{\"ColumnFilterHash\":{{\"ClassA\":\"A\"}}}}]}}')");
        await database.Execute($"CREATE INDEX {d.Quote("standard_" + table)} ON {d.Table(table)} ({d.Quote(table == "Results" ? "ResultId" : "IssueId")})");
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
    Check(sites.Count == 2 && sites.All(s => s.RecordCount == 1), "Live site counts");
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
    Check(final.Count(i => i.Name.StartsWith("standard_", StringComparison.Ordinal)) == 2, "Live standard indexes preserved");
    Check(final.Where(i => i.Managed).All(i => updated.Any(s => s.Name == i.Name)), "Live obsolete indexes removed");
    foreach (var i in final.Where(i => i.Managed)) await database.Execute(d.Drop(i.Table, i.Name));
    await database.Apply(Reconciler.Plan(updated, await database.ReadIndexes(default), false), default);
    Check(Reconciler.Plan(updated, await database.ReadIndexes(default), false).All(c => c.Kind == ChangeKind.Keep), "Recover after managed indexes disappear");
    foreach (var table in new[] { "Sites", "Results", "Issues" }) await database.Execute($"DROP TABLE {d.Table(table)}");
    Console.WriteLine($"Integration checks passed ({dbms}). Total checks: {passed}");
}
