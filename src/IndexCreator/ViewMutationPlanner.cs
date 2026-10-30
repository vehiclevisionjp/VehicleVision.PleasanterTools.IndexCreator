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
                notes.Add(new(site.SiteId, "View mutation body contains unresolved declarations, assignments or calls; no mutation candidate generated."));
                continue;
            }
            foreach (var baseline in new[] { Json.Parse("{}") }.Concat(site.SiteSettings.Get("Views").Array()))
            {
                Json.ValidateView(baseline);
                var changed = JsonNode.Parse(baseline.GetRawText())!.AsObject();
                foreach (var update in updates.Where(u => u.Group != "Negatives"))
                {
                    if (update.Group == "ClearFilters")
                    {
                        changed["ColumnFilterHash"] = new JsonObject();
                        changed["Search"] = "";
                        foreach (var flag in new[] { "Incomplete", "Own", "NearCompletionTime", "Delay", "Overdue" })
                        {
                            changed[flag] = false;
                        }
                        continue;
                    }
                    var hashName = update.Group switch
                    {
                        "Filters" => "ColumnFilterHash",
                        "SearchTypes" => "ColumnFilterSearchTypes",
                        _ => "ColumnSorterHash"
                    };
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
                // 本体はフィルター値を反映した後に明示的な否定指定を適用する。
                foreach (var update in updates.Where(u => u.Group == "Negatives"))
                {
                    var negatives = changed["ColumnFilterNegatives"] as JsonArray ?? new JsonArray();
                    if (changed["ColumnFilterNegatives"] == null)
                    {
                        changed["ColumnFilterNegatives"] = negatives;
                    }
                    for (var i = negatives.Count - 1; i >= 0; i--)
                    {
                        if (negatives[i]?.ToString() == update.Column)
                        {
                            negatives.RemoveAt(i);
                        }
                    }
                    if (update.Value == "true")
                    {
                        negatives.Add(update.Column);
                    }
                }
                var plan = new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix).AnalyzeQuery(site, Json.Parse(changed.ToJsonString()));
                indexes.AddRange(plan.Indexes);
                notes.AddRange(plan.Diagnostics);
            }
            notes.Add(new(site.SiteId, "Resolved View mutations were applied to the empty baseline and saved Views for review; runtime/session conditions are not reproduced."));
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
        var constants = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var statement in root.ChildNodes)
        {
            if (statement is VariableDeclaration declaration)
            {
                if (declaration.Kind != VariableDeclarationKind.Const)
                {
                    return null;
                }
                foreach (var item in declaration.Declarations)
                {
                    if (item.Id is not Identifier name || name.Name == "view"
                        || Constant(item.Init, constants) is not { } literal
                        || !constants.TryAdd(name.Name, literal))
                    {
                        return null;
                    }
                }
                continue;
            }
            if (statement is ExpressionStatement { Expression: CallExpression clear }
                && clear.Callee is MemberExpression clearMethod && clearMethod.Object is Identifier { Name: "view" }
                && Property(clearMethod, constants) == "ClearFilters")
            {
                if (clear.Arguments.Count != 0)
                {
                    return null;
                }
                updates.Add(new("ClearFilters", "", ""));
                continue;
            }
            if (statement is ExpressionStatement { Expression: CallExpression call }
                && call.Callee is MemberExpression method && method.Object is Identifier { Name: "view" }
                && Property(method, constants) == "FilterNegative")
            {
                if (call.Arguments.Count is not (1 or 2) || Constant(call.Arguments[0], constants) is not StringLiteral name
                    || !SupportedColumn(name.Value) || call.Arguments.Count == 2 && Constant(call.Arguments[1], constants) is not BooleanLiteral)
                {
                    return null;
                }
                var negative = call.Arguments.Count == 1 || ((BooleanLiteral)Constant(call.Arguments[1], constants)!).Value;
                updates.Add(new("Negatives", name.Value, negative ? "true" : "false"));
                continue;
            }
            if (statement is not ExpressionStatement { Expression: AssignmentExpression assignment }
                || assignment.Operator != Operator.Assignment
                || assignment.Left is not MemberExpression member
                || member.Object is not MemberExpression group || group.Object is not Identifier { Name: "view" }
                || Property(group, constants) is not ("Filters" or "Sorters" or "SearchTypes"))
            {
                return null;
            }
            var value = Constant(assignment.Right, constants) switch
            {
                StringLiteral literal => literal.Value,
                NumericLiteral number when Property(group, constants) is "SearchTypes" or "Sorters" => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null
            };
            if (value == null)
            {
                return null;
            }
            var column = Property(member, constants);
            if (!SupportedColumn(column))
            {
                return null;
            }
            if (Property(group, constants) == "Sorters")
            {
                value = value switch { "0" => "asc", "1" => "desc", _ => value };
            }
            if (Property(group, constants) == "Sorters" && value is not ("asc" or "desc"))
            {
                return null;
            }
            if (Property(group, constants) == "SearchTypes" && value is not
                ("PartialMatch" or "ExactMatch" or "ForwardMatch" or "PartialMatchMultiple" or "ExactMatchMultiple" or "ForwardMatchMultiple" or "1" or "2" or "3" or "11" or "12" or "13"))
            {
                return null;
            }
            updates.Add(new(Property(group, constants), column, value));
        }
        return updates;
    }
    // プリミティブのconstだけを追跡し、式評価や参照型の別名は扱わない。
    private static Node? Constant(Node? node, IReadOnlyDictionary<string, Node> constants) => node switch
    {
        StringLiteral or NumericLiteral or BooleanLiteral => node,
        Identifier name when constants.TryGetValue(name.Name, out var value) => value,
        _ => null
    };
    private static bool SupportedColumn(string column) => Regex.IsMatch(column, "^(Status|Manager|Owner|Creator|Updator|CreatedTime|UpdatedTime|StartTime|CompletionTime|ResultId|IssueId|Class([A-Z]|[0-9]{3})|Num([A-Z]|[0-9]{3})|Date([A-Z]|[0-9]{3})|Check([A-Z]|[0-9]{3}))$");
    private static string Property(MemberExpression member, IReadOnlyDictionary<string, Node> constants) => member.Computed
        ? Constant(member.Property, constants) is StringLiteral literal ? literal.Value : ""
        : member.Property is Identifier identifier ? identifier.Name : "";
}
