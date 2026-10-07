using System.Globalization;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed record Options(string Action, string? Path, string? SitesFile, string? DbmsName, string? Schema, long MinRecords, int MysqlPrefix, bool IncludeFilters, bool Offline, bool Prune, bool Yes, string? Output, bool Force = false, int LockTimeout = 5, SiteExclusion? Exclusion = null)
{
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new("help", null, null, null, null, 10000, 100, false, false, false, false, null);
        var action = args[0] switch { "_rds" => "apply", "_views" => "views-apply", "choice-lists" => "views-choices", "_choice-lists" or "choice-lists-apply" => "views-choices-apply", _ => args[0] };
        if (action is not ("plan" or "apply" or "views" or "views-apply" or "views-choices" or "views-choices-apply")) throw new UserError("Unknown action. Use plan, apply, views, _views, choice-lists or _choice-lists.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i] switch { "/p" => "-p", "/y" => "--yes", "-y" => "--yes", "/c" => "--check", "/f" => "--force", _ => args[i] };
            if (name is "--yes" or "--prune" or "--offline" or "--include-filter-columns" or "--check" or "--force")
            { if (!flags.Add(name)) throw new UserError("Duplicate option."); continue; }
            if (name is not ("-p" or "--sites" or "--dbms" or "--schema" or "--min-records" or "--mysql-prefix" or "--output" or "--lock-timeout" or "--exclude-tree" or "--exclude-site")) throw new UserError("Unknown option. Use --help for usage.");
            // CodeDefiner の /p と同じく、パスは / で始まる値も消費する。
            if (++i >= args.Length || (name != "-p" && args[i].StartsWith('-')) || !values.TryAdd(name, args[i])) throw new UserError("Missing or duplicate option value.");
        }
        string? V(string key) => values.GetValueOrDefault(key);
        long Number(string key, long fallback, long max)
        {
            if (V(key) == null) return fallback;
            if (!long.TryParse(V(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n > max) throw new UserError("Numeric option out of range.");
            return n;
        }
        var prefix = (int)Number("--mysql-prefix", 100, 191);
        if (prefix < 1) throw new UserError("MySQL prefix must be between 1 and 191.");
        // 稼働中の DB を止めないため、ロック待ちには必ず上限を持たせる。
        var lockTimeout = (int)Number("--lock-timeout", 5, 3600);
        if (lockTimeout < 1) throw new UserError("Lock timeout must be between 1 and 3600 seconds.");
        if (flags.Contains("--check")) action = action.Contains("choices", StringComparison.Ordinal) ? "views-choices" : action.StartsWith("views", StringComparison.Ordinal) ? "views" : "plan";
        if (action is "apply" or "views-apply" or "views-choices-apply" && V("--sites") != null) throw new UserError("Apply requires a live database; --sites is only available for planning.");
        if (V("--sites") != null && flags.Contains("--prune")) throw new UserError("Prune requires a live database inventory.");
        IReadOnlySet<long> Ids(string key)
        {
            var ids = new HashSet<long>();
            foreach (var part in (V(key) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id)) throw new UserError("Excluded SiteIds must be distinct positive numbers separated by commas.");
            return ids;
        }
        var exclusion = new SiteExclusion(Ids("--exclude-tree"), Ids("--exclude-site"));
        if (!exclusion.IsEmpty && !action.StartsWith("views", StringComparison.Ordinal)) throw new UserError("Site exclusions apply only to views and choice-lists.");
        return new(action, V("-p"), V("--sites"), V("--dbms"), V("--schema"), Number("--min-records", 10000, long.MaxValue), prefix, flags.Contains("--include-filter-columns"), flags.Contains("--offline"), flags.Contains("--prune"), flags.Contains("--yes"), V("--output"), flags.Contains("--force"), lockTimeout, exclusion);
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
        if (!Enum.TryParse<Dbms>(dbmsName, false, out var dbms) || !Enum.IsDefined(dbms)) throw new UserError("Dbms must be SQLServer, PostgreSQL or MySQL. Use -p to locate Pleasanter or --dbms for offline planning.");
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
