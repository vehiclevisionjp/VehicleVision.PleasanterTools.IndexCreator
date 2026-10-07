using System.Globalization;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed record Options(string Action, string? Path, string? SitesFile, string? DbmsName, string? Schema, long MinRecords, int MysqlPrefix, bool IncludeFilters, bool Offline, bool Prune, bool Yes, string? Output, bool Force = false, int LockTimeout = 5, SiteExclusion? Exclusion = null, string ColumnNames = "label")
{
    // CodeDefiner の引数と同じ書式。最初の引数が操作で、オプションは / で始まる。
    // 値のあるオプションは次の引数を値にする。パスを取るもの（p / sites / output）は / で始まる値も消費する（Linux の絶対パス）。
    private static readonly string[] FlagOptions = ["y", "f", "c", "prune", "offline", "include-filter-columns"];
    private static readonly string[] PathOptions = ["p", "sites", "output"];
    private static readonly string[] ValueOptions = [.. PathOptions, "dbms", "schema", "min-records", "mysql-prefix", "lock-timeout", "exclude-tree", "exclude-site", "names"];
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "/?") return new("help", null, null, null, null, 10000, 100, false, false, false, false, null);
        var action = args[0] switch { "_rds" => "apply", "_views" => "views-apply", "choice-lists" => "views-choices", "_choice-lists" => "views-choices-apply", _ => args[0] };
        if (action is not ("plan" or "apply" or "views" or "views-apply" or "views-choices" or "views-choices-apply")) throw new UserError("Unknown action. Use plan, _rds, views, _views, choice-lists or _choice-lists.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Length < 2 || args[i][0] != '/') throw new UserError("Options start with /. Use help for usage.");
            var name = args[i][1..];
            if (FlagOptions.Contains(name))
            { if (!flags.Add(name)) throw new UserError("Duplicate option."); continue; }
            if (!ValueOptions.Contains(name)) throw new UserError("Unknown option. Use help for usage.");
            if (++i >= args.Length || (!PathOptions.Contains(name) && args[i].StartsWith('/')) || !values.TryAdd(name, args[i])) throw new UserError("Missing or duplicate option value.");
        }
        string? V(string key) => values.GetValueOrDefault(key);
        long Number(string key, long fallback, long max)
        {
            if (V(key) == null) return fallback;
            if (!long.TryParse(V(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n > max) throw new UserError("Numeric option out of range.");
            return n;
        }
        var prefix = (int)Number("mysql-prefix", 100, 191);
        if (prefix < 1) throw new UserError("MySQL prefix must be between 1 and 191.");
        // 稼働中の DB を止めないため、ロック待ちには必ず上限を持たせる。
        var lockTimeout = (int)Number("lock-timeout", 5, 3600);
        if (lockTimeout < 1) throw new UserError("Lock timeout must be between 1 and 3600 seconds.");
        if (flags.Contains("c")) action = action.Contains("choices", StringComparison.Ordinal) ? "views-choices" : action.StartsWith("views", StringComparison.Ordinal) ? "views" : "plan";
        if (action is "apply" or "views-apply" or "views-choices-apply" && V("sites") != null) throw new UserError("Apply requires a live database; /sites is only available for planning.");
        if (V("sites") != null && flags.Contains("prune")) throw new UserError("Prune requires a live database inventory.");
        IReadOnlySet<long> Ids(string key)
        {
            var ids = new HashSet<long>();
            foreach (var part in (V(key) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id)) throw new UserError("Excluded SiteIds must be distinct positive numbers separated by commas.");
            return ids;
        }
        var exclusion = new SiteExclusion(Ids("exclude-tree"), Ids("exclude-site"));
        if (!exclusion.IsEmpty && !action.StartsWith("views", StringComparison.Ordinal)) throw new UserError("Site exclusions apply only to views and choice-lists.");
        // label は Pleasanter の表示名（LabelText）、column は列名（ColumnName）。選択肢 View の列は固定なので対象外。
        var names = V("names") ?? "label";
        if (names is not ("label" or "column")) throw new UserError("/names must be label or column.");
        if (V("names") != null && action is not ("views" or "views-apply")) throw new UserError("/names applies only to views.");
        return new(action, V("p"), V("sites"), V("dbms"), V("schema"), Number("min-records", 10000, long.MaxValue), prefix, flags.Contains("include-filter-columns"), flags.Contains("offline"), flags.Contains("prune"), flags.Contains("y"), V("output"), flags.Contains("f"), lockTimeout, exclusion, names);
    }
}
public sealed record Configuration(Dbms Dbms, string Schema, string ConnectionString, bool DisableIndexChangeDetection, int Timeout)
{
    public static string ResolvePath(string? explicitPath, string? executableDirectory = null)
    {
        if (explicitPath != null) return System.IO.Path.GetFullPath(explicitPath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        var directory = new DirectoryInfo(executableDirectory ?? AppContext.BaseDirectory);
        // CodeDefiner の GetSourcePath と同じく、実行ファイルの階層から本体の兄弟フォルダを探す。
        var parts = directory.FullName.Split(System.IO.Path.DirectorySeparatorChar);
        var index = Array.FindIndex(parts, part => part.StartsWith("IndexCreator", StringComparison.Ordinal));
        if (index >= 0) return System.IO.Path.GetFullPath(System.IO.Path.Combine(string.Join(System.IO.Path.DirectorySeparatorChar, parts.Take(index)), "Implem.Pleasanter"));
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(directory.FullName, "..", "Implem.Pleasanter"));
    }
    public static string ParameterPath(string? applicationPath)
    {
        var path = ResolvePath(applicationPath);
        var parameterPath = System.IO.Path.Combine(path, "App_Data", "Parameters");
        var envFile = System.IO.Path.Combine(parameterPath, "Env.json");
        if (File.Exists(envFile))
        {
            var env = Json.Parse(File.ReadAllText(envFile));
            if (env.Get("ParametersPath").Text() != "") parameterPath = System.IO.Path.GetFullPath(env.Get("ParametersPath").Text().Replace('\\', System.IO.Path.DirectorySeparatorChar));
        }
        return parameterPath;
    }
    public static Configuration Load(Options options)
    {
        var parameterPath = ParameterPath(options.Path);
        var rdsFile = System.IO.Path.Combine(parameterPath, "Rds.json");
        var rds = File.Exists(rdsFile) ? Json.Parse(File.ReadAllText(rdsFile)) : default;
        var serviceFile = System.IO.Path.Combine(parameterPath, "Service.json");
        var service = File.Exists(serviceFile) ? Json.Parse(File.ReadAllText(serviceFile)) : default;
        var serviceName = service.Get("Name").Text();
        if (serviceName == "") serviceName = "Implem.Pleasanter";
        var dbmsName = new[] { options.DbmsName, Environment.GetEnvironmentVariable("INDEXCREATOR_DBMS"), rds.Get("Dbms").Text() }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (!Enum.TryParse<Dbms>(dbmsName, false, out var dbms) || !Enum.IsDefined(dbms)) throw new UserError("Dbms must be SQLServer, PostgreSQL or MySQL. Use /p to locate Pleasanter or /dbms for offline planning.");
        var envName = service.Get("EnvironmentName").Text();
        var connection = new[] {
            Environment.GetEnvironmentVariable("INDEXCREATOR_CONNECTION_STRING"),
            rds.Get("OwnerConnectionString").Text(),
            envName == "" ? null : Environment.GetEnvironmentVariable(envName + "_OwnerConnectionString"),
            Environment.GetEnvironmentVariable($"{serviceName}_Rds_{dbms}_OwnerConnectionString"),
            Environment.GetEnvironmentVariable($"{serviceName}_Rds_{dbms}_ConnectionString"),
            Environment.GetEnvironmentVariable($"{serviceName}_Rds_OwnerConnectionString"),
            Environment.GetEnvironmentVariable($"{serviceName}_Rds_ConnectionString")
        }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        connection = connection.Replace("#ServiceName#", serviceName, StringComparison.Ordinal);
        if (options.SitesFile == null && connection == "") throw new UserError("No owner connection string is available. Configure Rds.json or INDEXCREATOR_CONNECTION_STRING.");
        var schema = new[] { options.Schema, Environment.GetEnvironmentVariable("INDEXCREATOR_SCHEMA"), dbms == Dbms.SQLServer ? "dbo" : serviceName }.First(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 128 || schema.Any(char.IsControl)) throw new UserError("Invalid schema name.");
        var timeout = rds.Get("SqlCommandTimeOut").ValueKind == JsonValueKind.Number ? rds.Get("SqlCommandTimeOut").GetInt32() : 0;
        if (timeout < 0) throw new UserError("SqlCommandTimeOut must not be negative.");
        return new(dbms, schema, connection, rds.Get("DisableIndexChangeDetection").Bool(), timeout);
    }
}
