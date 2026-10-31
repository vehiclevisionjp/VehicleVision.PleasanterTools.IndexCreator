using Acornima;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class ScriptQueryPlanner
{
    public static Analysis Analyze(IReadOnlyList<Site> sites, Dbms dbms, long minRecords = 10000, int mysqlPrefix = 100, IReadOnlySet<long>? externalSites = null)
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
            if (externalSites?.Contains(source.SiteId) == true || source.SiteSettings.Get("ServerScripts").Array().Any(s => !s.Get("Disabled").Bool() && s.Get("Shared").Bool()))
            {
                if (externalSites?.Contains(source.SiteId) == true)
                {
                    diagnostics.Add(new(source.SiteId, "[script-query:unresolved] extension-impact; no query candidate generated."));
                }
                if (source.SiteSettings.Get("ServerScripts").Array().Any(s => !s.Get("Disabled").Bool() && s.Get("Shared").Bool()))
                {
                    diagnostics.Add(new(source.SiteId, "[script-query:unresolved] shared-script; no query candidate generated."));
                }
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
                if (script.Get("Body").Text().Contains("//Include:", StringComparison.Ordinal))
                {
                    diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: [script-query:unresolved] include; no query candidate generated."));
                    continue;
                }
                ScriptQueryResolution resolution;
                try
                {
                    resolution = ScriptQueryResolver.Inspect(new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(script.Get("Body").Text()));
                }
                catch (ParseErrorException)
                {
                    throw new UserError($"Site {source.SiteId}, server script #{ordinal}: JavaScript parsing failed.");
                }
                var location = resolution.StatementOrdinal == 0 ? "whole-body scope check" : $"prefix statement #{resolution.StatementOrdinal}";
                diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: [script-query:{resolution.Status}] {resolution.Reason}; {location}. Only the supported prefix through the first query is covered; later statements and catch/finally are not covered."));
                foreach (var query in resolution.Queries)
                {
                    if (!targets.TryGetValue(query.TargetSiteId, out var target))
                    {
                        diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: [script-query:unresolved] target-unavailable; target site {query.TargetSiteId} is unavailable; no candidate generated."));
                        continue;
                    }
                    var plan = new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix).AnalyzeQuery(target, query.View);
                    indexes.AddRange(plan.Indexes);
                    diagnostics.AddRange(plan.Diagnostics);
                    var status = plan.Indexes.Count == 0 ? "resolved-no-candidates" : "resolved";
                    diagnostics.Add(new(source.SiteId, $"Server script #{ordinal}: [script-query:{status}] target site {target.SiteId}, {plan.Indexes.Count} index candidates for review."));
                }
            }
        }
        return new(indexes.GroupBy(i => i.Signature).Select(g => g.OrderBy(i => i.SiteId).First()).ToArray(), diagnostics.Distinct().ToArray());
    }
}
