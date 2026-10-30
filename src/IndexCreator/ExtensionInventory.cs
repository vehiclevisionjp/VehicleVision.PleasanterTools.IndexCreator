using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed record AnalysisExtension(string Source, string Type, JsonElement Settings);
public sealed record ExtensionInspection(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlySet<long> UncertainSites);

public static class ExtensionInventory
{
    public static IReadOnlyList<AnalysisExtension> ReadFiles(string parameterPath)
    {
        if (!DirectoryPresent(parameterPath))
        {
            throw new UserError("Extension parameter directory was not found.");
        }
        var entries = new List<AnalysisExtension>();
        foreach (var (folder, type, suffix) in new[] { ("ExtendedServerScripts", "ServerScript", ".js"), ("ExtendedSqls", "Sql", ".sql") })
        {
            var directory = Path.Combine(parameterPath, folder);
            if (!DirectoryPresent(directory))
            {
                continue;
            }
            foreach (var file in JsonFiles(directory).Order(StringComparer.Ordinal))
            {
                var settings = Json.Parse(File.ReadAllText(file));
                string? body;
                try
                {
                    body = File.ReadAllText(file + suffix);
                }
                catch (FileNotFoundException)
                {
                    body = null;
                }
                entries.Add(Normalize("file", type, settings, body, overrideEmpty: true));
            }
        }
        return entries;
    }

    private static bool DirectoryPresent(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            {
                throw new UserError("Extension directory must be a physical directory.");
            }
            return true;
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    private static IEnumerable<string> JsonFiles(string directory)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UserError("Linked extension files or directories require a physical inventory.");
            }
            if (entry is DirectoryInfo)
            {
                foreach (var file in JsonFiles(entry.FullName))
                {
                    yield return file;
                }
            }
            else if (entry.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry.FullName;
            }
        }
    }

    public static AnalysisExtension Normalize(string source, string type, JsonElement settings, string? body, bool overrideEmpty = false)
    {
        if (settings.ValueKind != JsonValueKind.Object || type is not ("ServerScript" or "Sql"))
        {
            throw new UserError("Extension settings or type is invalid.");
        }
        var node = JsonNode.Parse(settings.GetRawText())!.AsObject();
        if (body != null && (overrideEmpty || body.Length > 0))
        {
            node[type == "ServerScript" ? "Body" : "CommandText"] = body;
        }
        return new(source, type, Json.Parse(node.ToJsonString()));
    }

    public static ExtensionInspection Inspect(IReadOnlyList<Site> sites, IReadOnlyList<AnalysisExtension> extensions)
    {
        var notes = new List<Diagnostic>();
        var uncertain = new HashSet<long>();
        for (var i = 0; i < extensions.Count; i++)
        {
            var extension = extensions[i];
            if (extension.Settings.Get("Disabled").Bool())
            {
                continue;
            }
            var scope = extension.Settings.Get("SiteIdList");
            var ids = scope.Array().ToArray();
            // 負指定や文脈依存条件は適用なしと断定せず、全サイトを要確認にする。
            var positiveOnly = scope.ValueKind == JsonValueKind.Array && ids.Length > 0
                && ids.All(id => id.TryInteger(out var number) && number > 0);
            var targets = sites.Where(s => !positiveOnly || ids.Any(id => id.TryInteger(out var number) && number == s.SiteId)).ToArray();
            foreach (var site in targets)
            {
                if (extension.Type == "ServerScript" && site.SiteSettings.Get("ServerScriptsAllDisabled").Bool())
                {
                    continue;
                }
                uncertain.Add(site.SiteId);
                var origin = $"Extension #{i + 1} ({extension.Source}, {extension.Type})";
                notes.Add(new(site.SiteId, origin + ": possible applicability; runtime scope and ordering require review. Extension text and names are not logged."));
                if (extension.Type == "ServerScript")
                {
                    var isolated = site with { SiteSettings = JsonSerializer.SerializeToElement(new { ServerScripts = new[] { extension.Settings } }) };
                    notes.AddRange(ServerScriptDiagnostics.Analyze([isolated]).Select(n => new Diagnostic(n.SiteId, origin + ": " + n.Message)));
                }
                else
                {
                    var hooks = new[] { "OnSelectingColumn", "OnSelectingWhere", "OnSelectingOrderBy", "OnSelectingWherePermissionsDepts", "OnSelectingWherePermissionsGroups", "OnSelectingWherePermissionsUsers", "Api", "Html" }
                        .Where(key => extension.Settings.Get(key).Bool());
                    notes.Add(new(site.SiteId, origin + $": SQL hooks [{string.Join(", ", hooks)}]; arbitrary SQL is not executed or converted into index candidates. Review its execution plan for the selected DBMS."));
                }
            }
        }
        return new(notes, uncertain);
    }
}
