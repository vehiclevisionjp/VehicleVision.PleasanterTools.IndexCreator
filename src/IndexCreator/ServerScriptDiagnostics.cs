using Acornima;
using Acornima.Ast;

namespace VehicleVision.PleasanterTools.IndexCreator;

/// <summary>構文上の検索操作を報告する。実行・値の推論・索引生成は行わない。</summary>
public static class ServerScriptDiagnostics
{
    public static IReadOnlyList<Diagnostic> Analyze(IReadOnlyList<Site> sites)
    {
        var result = new List<Diagnostic>();
        foreach (var site in sites)
        {
            var ordinal = 0;
            foreach (var script in site.SiteSettings.Get("ServerScripts").Array())
            {
                ordinal++;
                if (script.Get("Disabled").Bool())
                {
                    continue;
                }
                var source = script.Get("Body").Text();
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }
                Node root;
                try
                {
                    root = new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(source);
                }
                catch (ParseErrorException)
                {
                    throw new UserError($"Site {site.SiteId}, server script #{ordinal}: JavaScript parsing failed; no operations were applied.");
                }
                var nodes = new Stack<Node>();
                foreach (var query in ScriptQueryResolver.Resolve(root))
                {
                    var filters = query.View.Get("ColumnFilterHash").Props().Select(p => Column(p.Name));
                    var sorters = query.View.Get("ColumnSorterHash").Props().Select(p => Column(p.Name));
                    var available = sites.Any(s => s.SiteId == query.TargetSiteId);
                    result.Add(new(site.SiteId, $"Server script #{ordinal}: Literal View targets site {query.TargetSiteId} (available: {available}); filters: {string.Join(", ", filters)}; sorters: {string.Join(", ", sorters)}. Runtime behavior requires verification."));
                }
                nodes.Push(root);
                while (nodes.TryPop(out var node))
                {
                    string? message = null;
                    if (node is AssignmentExpression assignment && assignment.Left is MemberExpression member
                        && member.Object is MemberExpression group && group.Object is Identifier { Name: "view" }
                        && Property(group) is "Filters" or "Sorters")
                    {
                        message = $"Possible view.{Property(group)} mutation ({Column(Property(member))}); runtime scope and values require review.";
                    }
                    if (node is CallExpression { Callee: MemberExpression call } && call.Object is Identifier { Name: "items" }
                        && Property(call) == "Get")
                    {
                        message = "Possible items.Get call; resolve the target and View at runtime before planning indexes.";
                    }
                    if (node is ObjectProperty property && Name(property.Key) is "ColumnFilterHash" or "ColumnSorterHash")
                    {
                        message = property.Value is ObjectExpression obj
                            ? $"Declared {Name(property.Key)} columns: {string.Join(", ", obj.Properties.OfType<ObjectProperty>().Select(p => Column(Name(p.Key))).Distinct())}. Query use and runtime scope require review."
                            : $"Dynamic {Name(property.Key)} declaration requires review.";
                    }
                    if (message != null)
                    {
                        result.Add(new(site.SiteId, $"Server script #{ordinal}: {message}"));
                    }
                    foreach (var child in node.ChildNodes)
                    {
                        nodes.Push(child);
                    }
                }
            }
        }
        return result.Distinct().ToArray();
    }

    private static string Name(Node node) => node switch { Identifier id => id.Name, StringLiteral text => text.Value, _ => "" };
    private static string Property(MemberExpression member) => !member.Computed || member.Property is StringLiteral ? Name(member.Property) : "";
    // 任意の文字列や検索値をログへ流さない。
    private static string Column(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        "^(Class|Num|Date|Check)([A-Z]|[0-9]{3})$|^(SiteId|ResultId|IssueId|WikiId|Status|Owner|Manager|Creator|Updator|CreatedTime|UpdatedTime|StartTime|CompletionTime|Title|Body)$") ? name : "dynamic/unsupported";
}
