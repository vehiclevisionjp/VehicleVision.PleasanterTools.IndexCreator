using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class DashboardCalendarPlanner
{
    public static Analysis Analyze(IReadOnlyList<Site> dashboards, IReadOnlyList<Site> sites, Dbms dbms, long minRecords = 10000, int mysqlPrefix = 100, bool periods = false, IReadOnlySet<long>? uncertain = null)
    {
        var targets = sites.ToDictionary(s => s.SiteId);
        var indexes = new List<IndexSpec>(); var notes = new List<Diagnostic>();
        bool Active(Site s) => !s.SiteSettings.Get("ServerScriptsAllDisabled").Bool() && s.SiteSettings.Get("ServerScripts").Array().Any(x => !x.Get("Disabled").Bool());
        foreach (var source in dashboards)
        {
            Json.ValidateSettings(source.SiteSettings);
            if (Active(source) || uncertain?.Contains(source.SiteId) == true)
            {
                notes.Add(new(source.SiteId, "Dashboard Calendar: source scripts or extensions are unresolved; no candidate generated."));
                continue;
            }
            var ordinal = 0;
            foreach (var part in source.SiteSettings.Get("DashboardParts").Array())
            {
                ordinal++;
                var origin = $"Dashboard Calendar part #{ordinal}: ";
                if (part.ValueKind != JsonValueKind.Object) { throw new UserError("A Dashboard part must be an object."); }
                if (part.Get("Type").Text() is not ("4" or "Calendar"))
                {
                    notes.Add(new(source.SiteId, origin + "unsupported part type; no Calendar candidate generated."));
                    continue;
                }
                foreach (var key in new[] { "CalendarSites", "CalendarFromTo" })
                {
                    if (part.Get(key).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                    {
                        throw new UserError("Dashboard Calendar metadata has an invalid shape.");
                    }
                }
                var selector = part.Get("CalendarSites").Text().Trim();
                if (!long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    notes.Add(new(source.SiteId, origin + "target is not one explicit numeric site; no candidate generated."));
                    continue;
                }
                var data = part.Get("CalendarSitesData");
                if (data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                    && (data.ValueKind != JsonValueKind.Array || data.Array().Count() != 1 || data.Array().Any(x => !long.TryParse(x.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var other) || other != id)))
                {
                    notes.Add(new(source.SiteId, origin + "conflicting target metadata; no candidate generated."));
                    continue;
                }
                if (!targets.TryGetValue(id, out var target) || target.ReferenceType is not ("Results" or "Issues")
                    || Active(target) || uncertain?.Contains(id) == true)
                {
                    notes.Add(new(source.SiteId, origin + "target unavailable or runtime scripts/extensions unresolved; no candidate generated."));
                    continue;
                }
                var view = part.Get("View");
                if (view.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) { Json.ValidateView(view); }
                var node = view.ValueKind == JsonValueKind.Object ? JsonNode.Parse(view.GetRawText())!.AsObject() : new JsonObject();
                node["DefaultMode"] = "Calendar";
                node["CalendarFromTo"] = part.Get("CalendarFromTo").Text();
                var plan = new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix, includePeriodCalendars: periods).AnalyzeCalendarReview(target, Json.Parse(node.ToJsonString()));
                indexes.AddRange(plan.Indexes); notes.AddRange(plan.Diagnostics);
                notes.Add(new(source.SiteId, origin + $"{plan.Indexes.Count} saved-part candidates for review only; runtime session/form View conditions and unprovided extensions are not covered."));
            }
        }
        return new(indexes.DistinctBy(i => i.Signature).ToArray(), notes.Distinct().ToArray());
    }
}
