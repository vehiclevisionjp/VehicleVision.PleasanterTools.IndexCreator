using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class ViewMutationPlanner
{
    public static Analysis Analyze(IReadOnlyList<Site> sites, Dbms dbms, long minRecords = 10000,
        int mysqlPrefix = 100, IReadOnlySet<long>? externalSites = null)
    {
        var indexes = new List<IndexSpec>();
        var notes = new List<Diagnostic>();
        foreach (var site in sites)
        {
            if (site.SiteSettings.Get("ServerScriptsAllDisabled").Bool())
            {
                continue;
            }
            var active = site.SiteSettings.Get("ServerScripts").Array().Where(s => !s.Get("Disabled").Bool()).ToArray();
            var scripts = active.Where(s => s.Get("WhenViewProcessing").Bool() && !s.Get("Shared").Bool()).ToArray();
            if (scripts.Length == 0)
            {
                continue;
            }
            // サイト配列の順序は保持する。共有・Include・実行ラッパーの効果は推測しない。
            if (active.Any(s => s.Get("Shared").Bool() || s.Get("WhenloadingSiteSettings").Bool())
                || scripts.Length > 1 && scripts.Any(s => s.Get("Functionalize").Bool() || s.Get("TryCatch").Bool())
                || scripts.Any(s => s.Get("Body").Text().Contains("//Include:", StringComparison.Ordinal))
                || externalSites?.Contains(site.SiteId) == true)
            {
                notes.Add(new(site.SiteId, "View mutations require review: wrappers, shared/include code, settings-loading scripts or extensions may affect execution."));
                continue;
            }
            // 改行で連結してから解析し、セミコロン省略によるスクリプト境界の作用も検査する。
            var updates = Resolve(string.Join("\n", scripts.Select(s => s.Get("Body").Text())));
            if (updates == null || updates.Count == 0)
            {
                notes.Add(new(site.SiteId, "View mutation body is not a complete sequence of literal assignments; no mutation candidate generated."));
                continue;
            }
            foreach (var baseline in new[] { Json.Parse("{}") }.Concat(site.SiteSettings.Get("Views").Array()))
            {
                Json.ValidateView(baseline);
                var changed = JsonNode.Parse(baseline.GetRawText())!.AsObject();
                foreach (var update in updates)
                {
                    var hashName = update.Group == "Filters" ? "ColumnFilterHash" : "ColumnSorterHash";
                    var hash = changed[hashName] as JsonObject ?? new JsonObject();
                    if (changed[hashName] == null)
                    {
                        changed[hashName] = hash;
                    }
                    var oldValue = hash[update.Column]?.ToString();
                    hash[update.Column] = update.Value;
                    if (update.Group == "Filters" && oldValue != update.Value && changed["ColumnFilterNegatives"] is JsonArray negatives)
                    {
                        for (var i = negatives.Count - 1; i >= 0; i--)
                        {
                            if (negatives[i]?.ToString() == update.Column)
                            {
                                negatives.RemoveAt(i);
                            }
                        }
                    }
                }
                var plan = new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix).AnalyzeQuery(site, Json.Parse(changed.ToJsonString()));
                indexes.AddRange(plan.Indexes);
                notes.AddRange(plan.Diagnostics);
            }
            notes.Add(new(site.SiteId, "Literal View mutations were applied to the empty baseline and saved Views for review; runtime/session conditions are not reproduced."));
        }
        return new(indexes.DistinctBy(i => i.Signature).ToArray(), notes.Distinct().ToArray());
    }

    private sealed record Update(string Group, string Column, string Value);
    private static List<Update>? Resolve(string source)
    {
        Node root;
        try
        {
            root = new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(source);
        }
        catch (ParseErrorException)
        {
            throw new UserError("View mutation JavaScript parsing failed.");
        }
        var updates = new List<Update>();
        foreach (var statement in root.ChildNodes)
        {
            if (statement is not ExpressionStatement { Expression: AssignmentExpression assignment }
                || assignment.Operator != Operator.Assignment
                || assignment.Left is not MemberExpression member
                || member.Object is not MemberExpression group || group.Object is not Identifier { Name: "view" }
                || Property(group) is not ("Filters" or "Sorters")
                || assignment.Right is not StringLiteral value)
            {
                return null;
            }
            var column = Property(member);
            if (!Regex.IsMatch(column, "^(Status|Manager|Owner|Creator|Updator|CreatedTime|UpdatedTime|StartTime|CompletionTime|ResultId|IssueId|Class([A-Z]|[0-9]{3})|Num([A-Z]|[0-9]{3})|Date([A-Z]|[0-9]{3})|Check([A-Z]|[0-9]{3}))$"))
            {
                return null;
            }
            if (Property(group) == "Sorters" && value.Value is not ("asc" or "desc"))
            {
                return null;
            }
            updates.Add(new(Property(group), column, value.Value));
        }
        return updates;
    }
    private static string Property(MemberExpression member) => member.Computed
        ? member.Property is StringLiteral literal ? literal.Value : ""
        : member.Property is Identifier identifier ? identifier.Name : "";
}
