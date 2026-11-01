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
                var tokens = part.Get("CalendarSites").Text().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                var ids = new List<long>();
                var invalid = tokens.Length == 0;
                foreach (var token in tokens)
                {
                    if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) { invalid = true; }
                    else { ids.Add(id); }
                }
                if (invalid)
                {
                    notes.Add(new(source.SiteId, origin + "targets are not explicit positive numeric sites; no candidate generated."));
                    continue;
                }
                var data = part.Get("CalendarSitesData");
                if (data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                    && (data.ValueKind != JsonValueKind.Array || data.Array().Count() != ids.Count
                        || data.Array().Select((x, n) => long.TryParse(x.Text(), NumberStyles.None, CultureInfo.InvariantCulture, out var other) && other == ids[n]).Any(matches => !matches)))
                {
                    notes.Add(new(source.SiteId, origin + "conflicting target metadata; no candidate generated."));
                    continue;
                }
                var selected = ids.Distinct().Select(id => targets.GetValueOrDefault(id)).ToArray();
                if (selected.Any(s => s == null || s.ReferenceType is not ("Results" or "Issues")))
                {
                    notes.Add(new(source.SiteId, origin + "a target is unavailable or not a record table; no candidate generated for this part."));
                    continue;
                }
                var bases = selected.Select(s => s!).ToArray();
                foreach (var target in bases) { Json.ValidateSettings(target.SiteSettings); }
                if (bases.Select(s => s.ReferenceType).Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    notes.Add(new(source.SiteId, origin + "possible bases have different reference types; no candidate generated for this part."));
                    continue;
                }
                if (bases.Any(s => Active(s) || uncertain?.Contains(s.SiteId) == true || s.RecordCount < minRecords))
                {
                    notes.Add(new(source.SiteId, origin + "a possible base is below the threshold or has unresolved scripts/extensions; no candidate generated for this part."));
                    continue;
                }
                var view = part.Get("View");
                if (view.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) { Json.ValidateView(view); }
                var node = view.ValueKind == JsonValueKind.Object ? JsonNode.Parse(view.GetRawText())!.AsObject() : new JsonObject();
                node["DefaultMode"] = "Calendar";
                node["CalendarFromTo"] = part.Get("CalendarFromTo").Text();
                var savedView = Json.Parse(node.ToJsonString());
                var plans = bases.Select(target => new Planner(dbms, minRecords, mysqlPrefix: mysqlPrefix, includePeriodCalendars: periods).AnalyzeCalendarReview(target, savedView)).ToArray();
                var common = plans[0].Indexes.Select(i => i.Signature).ToHashSet(StringComparer.Ordinal);
                foreach (var plan in plans.Skip(1)) { common.IntersectWith(plan.Indexes.Select(i => i.Signature)); }
                var representative = bases.Min(s => s.SiteId);
                var candidates = plans[0].Indexes.Where(i => common.Contains(i.Signature)).Select(i => i with { SiteId = representative }).ToArray();
                if (bases.Length > 1 && candidates.Length == 0)
                {
                    notes.Add(new(source.SiteId, origin + "no physical key definition is shared by all possible bases; no candidate generated for this part."));
                }
                indexes.AddRange(candidates); notes.AddRange(plans.SelectMany(p => p.Diagnostics));
                notes.Add(new(source.SiteId, origin + $"{candidates.Length} saved-part candidates common to {bases.Length} possible bases for review only; the representative index ID is not a resolved runtime base."));
                notes.Add(new(source.SiteId, origin + "runtime session/form View conditions, name/group expansion, allowed/denied site permissions and unprovided extensions are not covered."));
            }
        }
        return new(indexes.GroupBy(i => i.Signature).Select(g => g.OrderBy(i => i.SiteId).First()).ToArray(), notes.Distinct().ToArray());
    }
}
