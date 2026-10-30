using Acornima;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class ScriptQueryPlanner
{
    public static Analysis Analyze(IReadOnlyList<Site> sites, Dbms dbms, long minRecords = 10000, int mysqlPrefix = 100)
    {
        var targets = sites.ToDictionary(s => s.SiteId);
        var indexes = new List<IndexSpec>();
        var diagnostics = new List<Diagnostic>();
        foreach (var source in sites)
        {
            if (source.SiteSettings.Get("ServerScriptsAllDisabled").Bool())
            {
                continue;
            }
            var ordinal = 0;
            foreach (var script in source.SiteSettings.Get("ServerScripts").Array())
            {
                ordinal++;
                if (script.Get("Disabled").Bool() || string.IsNullOrWhiteSpace(script.Get("Body").Text()))
                {
                    continue;
                }
                IReadOnlyList<ScriptQuery> queries;
                try
                {
                    queries = ScriptQueryResolver.Resolve(new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(script.Get("Body").Text()));
                }
                catch (ParseErrorException)
                {
                    throw new UserError($"Site {source.SiteId}, server script #{ordinal}: JavaScript parsing failed.");
                }
                foreach (var query in queries)
                {
                    if (!targets.TryGetValue(query.TargetSiteId, out var target))
                    {
                        diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: target site {query.TargetSiteId} is unavailable; no candidate generated."));
                        continue;
                    }
                    var plan = new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix).AnalyzeQuery(target, query.View);
                    indexes.AddRange(plan.Indexes);
                    diagnostics.AddRange(plan.Diagnostics);
                    diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: target site {target.SiteId}, {plan.Indexes.Count} index candidates for review."));
                }
            }
        }
        return new(indexes.GroupBy(i => i.Signature).Select(g => g.OrderBy(i => i.SiteId).First()).ToArray(), diagnostics.Distinct().ToArray());
    }
}
