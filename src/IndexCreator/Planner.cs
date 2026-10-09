using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed class Planner(Dbms dbms, long minRecords = 10000, bool includeFilterColumns = false, int mysqlPrefix = 100)
{
    private readonly List<IndexSpec> indexes = [];
    private readonly List<Diagnostic> diagnostics = [];
    private enum Kind { Unknown, Text, Class, Number, Date, Bool }
    private static Kind ColumnKind(string name) => name switch
    {
        "SiteId" or "ResultId" or "IssueId" or "WikiId" or "Status" or "Manager" or "Owner" or "Creator" or "Updator" or "Ver" or "WorkValue" or "ProgressRate" or "RemainingWorkValue" => Kind.Number,
        "CreatedTime" or "UpdatedTime" or "StartTime" or "CompletionTime" => Kind.Date,
        "Locked" => Kind.Bool,
        _ when Regex.IsMatch(name, "^Class([A-Z]|[0-9]{3})$") => Kind.Class,
        _ when Regex.IsMatch(name, "^Num([A-Z]|[0-9]{3})$") => Kind.Number,
        _ when Regex.IsMatch(name, "^Date([A-Z]|[0-9]{3})$") => Kind.Date,
        _ when Regex.IsMatch(name, "^Check([A-Z]|[0-9]{3})$") => Kind.Bool,
        "Title" or "Body" or "Comments" => Kind.Text,
        _ => Kind.Unknown
    };
    private Key KeyFor(string name, bool desc = false, bool pattern = false) => new(name, desc, dbms == Dbms.MySQL && ColumnKind(name) == Kind.Class ? mysqlPrefix : 0, pattern);
    private void Note(Site s, string message) => diagnostics.Add(new(s.SiteId, message));
    private void Add(Site s, IEnumerable<Key> keys)
    {
        var list = keys.DistinctBy(k => k.Column).ToArray();
        if (list.Length < 2) return;
        indexes.Add(new(s.ReferenceType, list, s.SiteId));
        if (dbms == Dbms.SQLServer && list.Any(k => ColumnKind(k.Column) == Kind.Class))
            Note(s, "Class keys may exceed the SQL Server 1700-byte limit. Review maximum value lengths before applying.");
    }
    private IEnumerable<Key> Tie(Site s) => [new("UpdatedTime", true), new(s.ReferenceType[..^1] + "Id", true)];
    private static JsonElement Setting(Site s, string name) => s.SiteSettings.Get("Columns").Array().FirstOrDefault(c => c.Get("ColumnName").Text() == name);
    private static bool Choices(JsonElement c) => !string.IsNullOrWhiteSpace(c.Get("ChoicesText").Text()) && c.Get("ControlType").Text() is "" or "ChoicesText";
    private static HashSet<string> Links(Site s)
    {
        var result = s.SiteSettings.Get("Links").Array().Where(l => l.Get("SiteId").TryGetInt64(out var id) && id > 0).Select(l => l.Get("ColumnName").Text()).ToHashSet(StringComparer.Ordinal);
        foreach (var c in s.SiteSettings.Get("Columns").Array())
            if (Regex.IsMatch(c.Get("ChoicesText").Text(), @"(?m)^\s*\[\[\d+[^\]]*\]\]\s*$")) result.Add(c.Get("ColumnName").Text());
        return result;
    }
    private string Filter(Site s, JsonElement view, string name, JsonElement value)
    {
        var c = Setting(s, name);
        switch (ColumnKind(name))
        {
            case Kind.Date: return "range";
            case Kind.Bool: return "eq";
            case Kind.Number: return value.Text().Contains(',') ? "range" : "eq";
            case Kind.Class:
                if (c.Get("MultipleSelections").Bool()) break;
                var search = view.Get("ColumnFilterSearchTypes").Get(name).Text();
                if (search == "") search = c.Get("SearchType").Text();
                if (search == "") search = Choices(c) ? "ExactMatch" : "PartialMatch";
                if (search is "2" or "12" or "ExactMatch" or "ExactMatchMultiple") return "eq";
                if (search is "3" or "13" or "ForwardMatch" or "ForwardMatchMultiple") return "prefix";
                // 画面で選択肢を設定すると検索方法が部分一致で保存される場合がある。本体は LIKE '%値%' で検索する。
                if (Choices(c))
                {
                    Note(s, $"The {name} filter uses partial match, so no index can serve it. Set the search type to exact match in the site's filter settings if the choices are compared as whole values.");
                    return "skip";
                }
                break;
        }
        Note(s, "A filter cannot use a plain B-tree key (joined, text, partial-match or unknown column).");
        return "skip";
    }
    private IEnumerable<JsonProperty> Filters(JsonElement hash)
    {
        if (hash.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new UserError("A nested filter dictionary has an invalid shape.");
        foreach (var p in hash.Props())
        {
            if (p.Name.StartsWith("and_", StringComparison.Ordinal))
            {
                foreach (var child in Filters(p.Value.Object())) yield return child;
            }
            else yield return p;
        }
    }
    public Analysis Analyze(IReadOnlyList<Site> sites)
    {
        indexes.Clear(); diagnostics.Clear();
        foreach (var site in sites) Json.ValidateSettings(site.SiteSettings);
        foreach (var s in sites.Where(s => s.RecordCount >= minRecords).OrderBy(s => s.SiteId))
        {
            if (dbms != Dbms.PostgreSQL) Add(s, new[] { new Key("SiteId") }.Concat(Tie(s)));
            var links = Links(s);
            foreach (var name in links.Concat(s.SiteSettings.Get("Summaries").Array().Select(x => x.Get("LinkColumn").Text())).Distinct())
                if (ColumnKind(name) == Kind.Class) Add(s, [new("SiteId"), KeyFor(name)]);
            foreach (var v in s.SiteSettings.Get("Views").Array()) AnalyzeView(s, v, links);
            if (includeFilterColumns)
            {
                var names = s.SiteSettings.Get("FilterColumns").Array().Concat(s.SiteSettings.Get("Views").Array().SelectMany(v => v.Get("FilterColumns").Array())).Select(x => x.Text()).Distinct();
                foreach (var name in names)
                    if (name != "SiteId" && Filter(s, default, name, default) == "eq") Add(s, new[] { new Key("SiteId"), KeyFor(name) }.Concat(Tie(s)));
            }
            if (s.SiteSettings.Get("GridColumns").Array().Concat(s.SiteSettings.Get("Views").Array().SelectMany(v => v.Get("GridColumns").Array())).Any(x => x.Text().Contains("~~", StringComparison.Ordinal)))
                Note(s, "Parent-to-child joins require expression indexes; automatic creation is not supported in v0.1.");
        }
        AnalyzeLinks(sites);
        var unique = indexes.GroupBy(i => i.Signature).Select(g => g.OrderBy(i => i.SiteId).First()).ToArray();
        var compact = unique.Where(i => !unique.Any(j => j.Keys.Count > i.Keys.Count && j.Covers(i))).OrderBy(i => i.Name, StringComparer.Ordinal).ToArray();
        return new(compact, diagnostics.Distinct().ToArray());
    }
    private void AnalyzeLinks(IReadOnlyList<Site> sites)
    {
        var lookup = sites.ToDictionary(s => s.SiteId);
        foreach (var source in sites.OrderBy(s => s.SiteId))
        {
            var sourceLinks = source.SiteSettings.Get("Links").Array().ToArray();
            foreach (var link in sourceLinks)
            {
                var view = link.Get("View");
                if (view.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                Json.ValidateView(view);
                if (!link.Get("SiteId").TryGetInt64(out var id) || id <= 0) continue;
                if (!lookup.TryGetValue(id, out var target))
                { Note(source, $"Link {link.Get("ColumnName").Text()}: target site {id} is unavailable; choice filters were not planned."); continue; }
                if (target.ReferenceType is not ("Results" or "Issues")) continue;
                if (target.RecordCount < minRecords)
                { Note(source, $"Link {link.Get("ColumnName").Text()}: target site {id} is below the minimum record threshold; choice filters were not planned."); continue; }
                // 選択肢取得の既定順は Items.Title。保存ビューの UpdatedTime 順を補わない。
                AnalyzeView(target, view, Links(target), choiceLookup: true);
            }
            foreach (var relation in source.SiteSettings.Get("RelatingColumns").Array())
            {
                var columns = relation.Get("Columns").Array().Select(c => c.Text()).ToArray();
                for (var n = 1; n < columns.Length; n++)
                {
                    var parent = sourceLinks.FirstOrDefault(l => l.Get("ColumnName").Text() == columns[n - 1] && l.Get("SiteId").TryGetInt64(out var id) && id > 0);
                    var child = sourceLinks.FirstOrDefault(l => l.Get("ColumnName").Text() == columns[n] && l.Get("SiteId").TryGetInt64(out var id) && id > 0);
                    if (!parent.Get("SiteId").TryGetInt64(out var parentId) || !child.Get("SiteId").TryGetInt64(out var childId))
                    { Note(source, $"Relating columns {columns[n - 1]} -> {columns[n]}: link settings are missing; no candidate was generated."); continue; }
                    if (!lookup.TryGetValue(childId, out var target))
                    { Note(source, $"Relating columns {columns[n - 1]} -> {columns[n]}: target site {childId} is unavailable; no candidate was generated."); continue; }
                    if (target.ReferenceType is not ("Results" or "Issues")) continue;
                    var editor = target.SiteSettings.Get("EditorColumnHash").Props().SelectMany(p => p.Value.Array()).Select(c => c.Text()).ToList();
                    // 本体 GetParentLinkedClass と同じく、編集項目順を優先し、同順位は Links の順を残す。
                    var parentLinks = target.SiteSettings.Get("Links").Array()
                        .Where(l => l.Get("SiteId").TryGetInt64(out var id) && id == parentId)
                        .Select(l => l.Get("ColumnName").Text()).ToArray();
                    if (editor.Count == 0 && parentLinks.Length > 1)
                    { Note(source, $"Relating columns target site {childId}: editor order is unavailable and multiple parent links exist; no candidate was generated."); continue; }
                    var name = parentLinks.OrderBy(c => editor.Contains(c) ? editor.IndexOf(c) : int.MaxValue).FirstOrDefault();
                    if (name == null || ColumnKind(name) != Kind.Class)
                    { Note(source, $"Relating columns {columns[n - 1]} -> {columns[n]}: the parent link in target site {childId} is unavailable; no candidate was generated."); continue; }
                    if (Setting(target, name).Get("MultipleSelections").Bool())
                    { Note(source, $"Relating columns target site {childId}, {name}: multiple selections use contains matching; no additional relation index was generated."); continue; }
                    if (target.RecordCount < minRecords)
                    { Note(source, $"Relating columns target site {childId}, {name}: below the minimum record threshold; no additional relation index was generated."); continue; }
                    Add(target, [new("SiteId"), KeyFor(name), new(target.ReferenceType[..^1] + "Id")]);
                    Note(source, $"Relating columns target site {childId}, {name}: a raw-column index with record ID was planned; numeric conversion remains a residual condition, not an indexed seek.");
                    if (dbms == Dbms.MySQL) Note(source, $"Relating columns target site {childId}, {name}: a MySQL prefix index does not cover the full value used by numeric conversion.");
                }
            }
        }
    }
    private void AnalyzeView(Site s, JsonElement view, HashSet<string> links, bool choiceLookup = false)
    {
        if (view.Get("ColumnFilterExpressions").Array().Any()) Note(s, "ColumnFilterExpressions are not covered by ordinary index planning.");
        var equality = new List<Key>();
        var ranges = new List<Key>();
        var negatives = view.Get("ColumnFilterNegatives").Array().Select(x => x.Text()).ToHashSet();
        foreach (var p in Filters(view.Get("ColumnFilterHash")))
        {
            if (p.Name == "SiteId") continue;
            if (negatives.Contains(p.Name) || p.Name.Contains('~') || p.Name.StartsWith("or_", StringComparison.Ordinal) || p.Name.StartsWith("eq_", StringComparison.Ordinal) || p.Name.StartsWith("notEq_", StringComparison.Ordinal))
            { Note(s, "A negative, joined or OR filter was excluded from index candidates."); continue; }
            var use = Filter(s, view, p.Name, p.Value);
            if (use == "eq") equality.Add(KeyFor(p.Name));
            else if (use is "range" or "prefix") ranges.Add(KeyFor(p.Name, pattern: use == "prefix" && dbms == Dbms.PostgreSQL));
        }
        bool Positive(string flag)
        {
            if (!view.Get(flag).Bool()) return false;
            if (!s.SiteSettings.Get("UseNegativeFilters").Bool() || !negatives.Contains("ViewFilters_" + flag)) return true;
            Note(s, $"The negative {flag} filter was excluded from index candidates.");
            return false;
        }
        if (Positive("Incomplete")) ranges.Add(new("Status"));
        var near = Positive("NearCompletionTime");
        var overdue = Positive("Overdue");
        if (near || overdue)
        {
            if (s.ReferenceType == "Issues")
            {
                ranges.Add(new("CompletionTime"));
                if (overdue) ranges.Add(new("Status"));
            }
            else Note(s, "Deadline filters require Issues columns; no deadline index was generated.");
        }
        if (view.Get("Search").Text() != "") Note(s, "Full-text search conditions are not covered by ordinary index planning.");
        // Own は Manager=@user OR Owner=@user。OR を複合キー1本にせず、各分岐を等価キーとして計画する。
        var own = Positive("Own");
        var branches = own ? new[] { "Manager", "Owner" } : new[] { "" };
        var distinctRanges = ranges.DistinctBy(k => k.Column).OrderBy(k => k.Column, StringComparer.Ordinal).ToArray();
        foreach (var branch in branches)
        {
            var keys = new List<Key> { new("SiteId") };
            keys.AddRange(equality.Concat(branch == "" ? [] : new[] { KeyFor(branch) }).DistinctBy(k => k.Column).OrderBy(k => k.Column, StringComparer.Ordinal));
            if (distinctRanges.Length > 0)
            {
                foreach (var range in distinctRanges) Add(s, keys.Concat(new[] { range }));
                continue;
            }
            var sort = new List<Key>();
            var usable = true;
            foreach (var p in view.Get("ColumnSorterHash").Props())
            {
                var kind = ColumnKind(p.Name);
                var wrapped = kind is Kind.Class or Kind.Bool || kind == Kind.Number && p.Name is not ("SiteId" or "ResultId" or "IssueId" or "WikiId" or "Creator" or "Updator" or "Ver") && !(p.Name == "Status" && s.ReferenceType == "Issues") && !Setting(s, p.Name).Get("Nullable").Bool();
                if (links.Contains(p.Name) || kind is Kind.Text or Kind.Unknown || wrapped || p.Name.Contains('~'))
                {
                    Note(s, "A sort needs an expression or joined key; automatic creation is not supported in v0.1. Enable Nullable for numeric columns where appropriate.");
                    usable = false; break;
                }
                var direction = p.Value.Text().ToLowerInvariant();
                if (direction is not ("0" or "1" or "asc" or "desc")) throw new UserError("An unsupported sort direction was found.");
                sort.Add(KeyFor(p.Name, direction is "1" or "desc"));
            }
            if (usable) { keys.AddRange(sort); if (!choiceLookup) keys.AddRange(Tie(s)); }
            if (equality.Count == 0 && branch == "" && (sort.Count == 0 || !usable)) continue;
            Add(s, keys);
        }
    }
}
