using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed record ViewColumn(string Source, string Alias);
public sealed record ChoiceRow(string Value, string Text, string TextMini);
// 選択肢のリンク先 Wiki のサイト。Line は Links に並ぶ順（1 始まり）で、選択肢の並びと重複の優先順位になる。
public sealed record WikiChoiceSource(int Line, long SiteId);
public sealed record SiteView(long SiteId, string Table, IReadOnlyList<ViewColumn> Columns, string SiteName = "", IReadOnlyList<ChoiceRow>? Choices = null, string? ChoiceColumn = null, IReadOnlyList<WikiChoiceSource>? WikiSources = null)
{
    public const string Prefix = "View_vvplic_";
    public string Name
    {
        get
        {
            var prefix = Prefix + (Choices != null ? "ChoiceList_" : "") + Table + "_" + SiteId.ToString(CultureInfo.InvariantCulture) + "_" + (Choices != null ? ChoiceColumn + "_" : "");
            if (Encoding.UTF8.GetByteCount(prefix) >= 63) throw new UserError("Choice column identifiers leave no room for a portable view name.");
            var suffix = Regex.Replace(SiteName.Normalize(NormalizationForm.FormC), @"[^\p{L}\p{N}_-]+", "_", RegexOptions.CultureInvariant).Trim('_');
            if (suffix == "") suffix = "Untitled";
            var result = new StringBuilder(prefix);
            var bytes = Encoding.UTF8.GetByteCount(prefix);
            foreach (var rune in suffix.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 63) break;
                result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
            }
            return result.ToString();
        }
    }
    public string ConsoleName => JsonSerializer.Serialize(Name);
    public static string ConsoleIdentifier(string name) => JsonSerializer.Serialize(name);
    public static bool IsManaged(string name, bool choices = false) => Regex.IsMatch(name, "^" + Prefix + (choices ? "ChoiceList_" : "") + @"(Results|Issues|Wikis)_[1-9][0-9]{0,18}_[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant) && Encoding.UTF8.GetByteCount(name) <= 63;
    public string Select(SqlDialect d)
    {
        if (Choices != null) return d.ChoiceSelect(SiteId, ChoiceColumn!, WikiSources);
        var expressions = Columns.Select(c => (c.Source == "Title" ? "i." : "r.") + d.Quote(c.Source) + " AS " + d.Quote(c.Alias));
        var id = Table[..^1] + "Id";
        var join = Columns.Any(c => c.Source == "Title") ? $" INNER JOIN {d.Table("Items")} i ON i.{d.Quote("ReferenceId")}=r.{d.Quote(id)} AND i.{d.Quote("SiteId")}=r.{d.Quote("SiteId")}" : "";
        return $"SELECT {string.Join(", ", expressions)} FROM {d.Table(Table)} r{join} WHERE r.{d.Quote("SiteId")}={SiteId.ToString(CultureInfo.InvariantCulture)}";
    }
    public IReadOnlyList<IndexSpec> RequiredColumns()
    {
        if (Choices != null) return WikiSources is { Count: > 0 } ? [new("Wikis", [new("SiteId"), new("Body")])] : [];
        var id = Table[..^1] + "Id";
        var specs = new List<IndexSpec> { new(Table, Columns.Where(c => c.Source != "Title").Select(c => new Key(c.Source)).Concat([new("SiteId"), new(id)]).ToArray()) };
        if (Columns.Any(c => c.Source == "Title")) specs.Add(new("Items", [new("ReferenceId"), new("SiteId"), new("Title")]));
        return specs;
    }
}
public sealed class ViewPlanner(Dbms dbms, string? applicationPath = null)
{
    private readonly Dictionary<string, IReadOnlyList<JsonElement>> definitionCache = new(StringComparer.Ordinal);
    private static bool PhysicalColumn(string name, string table) => name switch
    {
        "Status" => table != "Wikis",
        "SiteId" or "Title" or "TitleBody" or "Body" or "Comments" or "Ver" or "Manager" or "Owner" or "Creator" or "Updator" or "CreatedTime" or "UpdatedTime" or "Locked" => true,
        "ResultId" => table == "Results",
        "WikiId" => table == "Wikis",
        "IssueId" or "StartTime" or "CompletionTime" or "WorkValue" or "ProgressRate" or "RemainingWorkValue" => table == "Issues",
        _ => table != "Wikis" && Regex.IsMatch(name, "^(Class|Num|Date|Check|Description|Attachments)([A-Z]|[0-9]{3})$", RegexOptions.CultureInvariant)
    };
    private IReadOnlyList<JsonElement> Definitions(string table)
    {
        if (definitionCache.TryGetValue(table, out var cached)) return cached;
        if (applicationPath == null) return [];
        var path = Path.Combine(applicationPath, "App_Data", "Definitions", "Definition_Column");
        if (!Directory.Exists(path)) return [];
        var all = Directory.EnumerateFiles(path, "*.json").Select(p => Json.Parse(File.ReadAllText(p))).ToArray();
        var own = all.Where(c => c.Get("TableName").Text() == table && c.Get("Base").Text() != "1").ToArray();
        return definitionCache[table] = own.Concat(all.Where(c => c.Get("Base").Text() == "1" && !own.Any(o => o.Get("ColumnName").Text() == c.Get("ColumnName").Text()))).ToArray();
    }
    public IReadOnlyList<SiteView> Generate(IReadOnlyList<Site> sites)
    {
        var views = new List<SiteView>();
        foreach (var s in sites.OrderBy(s => s.SiteId))
        {
            Json.ValidateSettings(s.SiteSettings);
            if (s.SiteId <= 0 || s.ReferenceType is not ("Results" or "Issues" or "Wikis")) throw new UserError("Invalid site for view generation.");
            var definitions = Definitions(s.ReferenceType);
            var grid = s.SiteSettings.Get("GridColumns");
            var names = grid.ValueKind == JsonValueKind.Array ? grid.Array().Select(c => c.Text()).ToArray() : definitions.Where(c => c.Get("GridEnabled").Text() == "1").OrderBy(c => long.TryParse(c.Get("GridColumn").Text(), out var n) ? n : long.MaxValue).Select(c => c.Get("ColumnName").Text()).ToArray();
            if (names.Length == 0) throw new UserError("Site views require GridColumns or the matching Pleasanter column definitions. Use /p to locate the application.");
            var columns = new List<ViewColumn>();
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!PhysicalColumn(name, s.ReferenceType)) throw new UserError("A grid column needs a joined or computed expression. Site view generation stopped; no columns were silently omitted.");
                var c = s.SiteSettings.Get("Columns").Array().FirstOrDefault(c => c.Get("ColumnName").Text() == name);
                var label = new[] { c.Get("GridLabelText").Text(), c.Get("LabelText").Text(), definitions.FirstOrDefault(d => d.Get("ColumnName").Text() == name).Get("LabelText").Text(), name }.First(x => !string.IsNullOrWhiteSpace(x));
                if (!aliases.Add(label))
                {
                    label += " (" + name + ")";
                    if (!aliases.Add(label)) throw new UserError("Duplicate site view column labels.");
                }
                var length = dbms == Dbms.PostgreSQL ? Encoding.UTF8.GetByteCount(label) : label.Length;
                if (label.Any(char.IsControl) || length > (dbms == Dbms.PostgreSQL ? 63 : dbms == Dbms.MySQL ? 64 : 128)) throw new UserError("A view column label exceeds the database identifier limit.");
                columns.Add(new(name == "TitleBody" ? "Title" : name, label));
            }
            views.Add(new(s.SiteId, s.ReferenceType, columns, s.Title));
        }
        return views;
    }
}
public sealed partial class Database
{
    public async Task<IReadOnlyList<string>> ReadViewNames(CancellationToken ct)
    {
        await using var cmd = Command("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA=@schema");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }
    public string ViewSql(SiteView view) => (config.Dbms == Dbms.SQLServer ? "CREATE OR ALTER VIEW " : "CREATE OR REPLACE VIEW ") + dialect.Table(view.Name) + " AS " + view.Select(dialect) + ";";
    public async Task ApplyViews(IReadOnlyList<SiteView> views, bool prune, CancellationToken ct, IReadOnlyList<Site>? expectedSites = null, bool force = false, bool choices = false)
    {
        async Task VerifySnapshot()
        {
            if (expectedSites == null) return;
            var snapshot = expectedSites.ToDictionary(s => s.SiteId, s => s.ReferenceType + "|" + s.Title + "|" + s.SiteSettings.GetRawText());
            var current = await ReadSites(ct);
            if (current.Count != snapshot.Count || current.Any(s => !snapshot.TryGetValue(s.SiteId, out var value) || value != s.ReferenceType + "|" + s.Title + "|" + s.SiteSettings.GetRawText())) throw new UserError("Site configuration changed. No further views were applied; re-run views.");
        }
        await VerifySnapshot();
        await ValidateColumns(views.SelectMany(v => v.RequiredColumns()).ToArray(), ct);
        if (choices && config.Dbms == Dbms.SQLServer) await RequireChoiceFunctions(ct);
        if (choices && config.Dbms == Dbms.MySQL) dialect.Collation = await ReadSchemaCollation(ct);
        await SetLockTimeout(true, ct);
        var existing = await ReadViewNames(ct);
        var rebuild = new HashSet<string>(StringComparer.Ordinal);
        // PostgreSQL の列名・列順の変更は CREATE OR REPLACE だけではできない。
        // 依存と権限を勝手に破棄しないため、変更時は検出して停止する。
        if (config.Dbms == Dbms.PostgreSQL)
        {
            foreach (var view in views.Where(v => existing.Contains(v.Name)))
            {
                await using var cmd = Command("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@name ORDER BY ORDINAL_POSITION");
                var p = cmd.CreateParameter(); p.ParameterName = "@name"; p.Value = view.Name; cmd.Parameters.Add(p);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var columns = new List<string>();
                while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
                if (!columns.SequenceEqual(view.Columns.Select(c => c.Alias)))
                {
                    if (!force) throw new UserError("PostgreSQL view columns changed. Review dependencies and grants, then use /f to replace the affected view.");
                    rebuild.Add(view.Name);
                }
            }
        }
        foreach (var view in views)
        {
            if (rebuild.Contains(view.Name)) await Retry(() => ReplacePostgresView(view, ct), ct);
            else await Retry(() => Execute(ViewSql(view), ct), ct);
            RuntimeLog.WriteLine("Updated view " + view.ConsoleName);
        }
        await VerifySnapshot();
        if (prune)
            foreach (var name in existing.Where(n => SiteView.IsManaged(n, choices) && !views.Any(v => v.Name == n)))
            {
                await Retry(() => Execute("DROP VIEW " + dialect.Table(name) + ";", ct), ct);
                RuntimeLog.WriteLine("Dropped view " + SiteView.ConsoleIdentifier(name));
            }
    }
    public async Task<string?> ReadSchemaCollation(CancellationToken ct)
    {
        await using var cmd = Command("SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@schema");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    // STRING_SPLIT の ordinal と TRIM の除去文字指定は SQL Server 2022 以降と Azure SQL で使える。
    // STRING_SPLIT と OPENJSON は互換性レベル 130 以上を要する。
    private async Task RequireChoiceFunctions(CancellationToken ct)
    {
        await using var cmd = Command("SELECT CASE WHEN (CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8) OR CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 16) AND (SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID()) >= 130 THEN 1 ELSE 0 END");
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 1) throw new UserError("Choice views on SQL Server require SQL Server 2022 or later, or Azure SQL, with compatibility level 130 or higher. No choice views were applied.");
    }
    private async Task ReplacePostgresView(SiteView view, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var grantsCommand = Command("SELECT grantee, privilege_type, is_grantable FROM INFORMATION_SCHEMA.TABLE_PRIVILEGES WHERE TABLE_SCHEMA=@schema AND TABLE_NAME=@name AND grantee<>current_user");
        grantsCommand.Transaction = transaction;
        var name = grantsCommand.CreateParameter(); name.ParameterName = "@name"; name.Value = view.Name; grantsCommand.Parameters.Add(name);
        var grants = new List<(string Role, string Privilege, bool Grantable)>();
        await using (var reader = await grantsCommand.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) grants.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES"));
        grantsCommand.CommandText = "SELECT COUNT(*) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@name AND a.attacl IS NOT NULL";
        if (Convert.ToInt64(await grantsCommand.ExecuteScalarAsync(ct)) != 0) throw new UserError("The PostgreSQL view has column-level grants. Replace it manually to preserve access rules.");
        async Task Ddl(string sql)
        {
            await using var cmd = Command(sql); cmd.Parameters.Clear(); cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // CASCADE を使わず、依存がある場合はトランザクション全体を戻す。
        await Ddl("DROP VIEW " + dialect.Table(view.Name));
        await Ddl(ViewSql(view));
        foreach (var grant in grants)
        {
            if (grant.Privilege is not ("SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REFERENCES" or "TRIGGER")) throw new UserError("Unsupported PostgreSQL view grant. The replacement was rolled back.");
            var role = grant.Role == "PUBLIC" ? "PUBLIC" : dialect.Quote(grant.Role);
            await Ddl("GRANT " + grant.Privilege + " ON " + dialect.Table(view.Name) + " TO " + role + (grant.Grantable ? " WITH GRANT OPTION" : ""));
        }
        await transaction.CommitAsync(ct);
    }
}
