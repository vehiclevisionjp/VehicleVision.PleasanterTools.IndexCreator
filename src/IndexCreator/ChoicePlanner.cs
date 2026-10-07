using System.Globalization;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed class ChoicePlanner
{
    private readonly string split;
    private readonly string replace;
    private readonly string replacement;
    public ChoicePlanner(string? applicationPath = null)
    {
        var path = Path.Combine(Configuration.ParameterPath(applicationPath), "General.json");
        var general = File.Exists(path) ? Json.Parse(File.ReadAllText(path)) : default;
        split = general.Get("ChoiceSplitRegexPattern").Text() is { Length: > 0 } s ? s : @"(?<!\\),";
        replace = general.Get("ChoiceReplaceRegexPattern").Text() is { Length: > 0 } r ? r : @"\\(,)";
        replacement = general.Get("ChoiceReplaceRegexReplacement").ValueKind == System.Text.Json.JsonValueKind.String ? general.Get("ChoiceReplaceRegexReplacement").Text() : "$1";
        // 選択肢 View は DB の組込関数で既定の区切り規則を再現する。独自の規則は DB 側で同じ結果にできないため止める。
        if (split != @"(?<!\\)," || replace != @"\\(,)" || replacement != "$1") throw new UserError("Choice views support only the default ChoiceSplitRegexPattern and ChoiceReplaceRegex settings. No choice views were applied.");
    }
    // targets は View を作るサイト、lookup はリンク先の種別を調べるための全サイト（除外したサイトも含む）。
    public IReadOnlyList<SiteView> Generate(IReadOnlyList<Site> targets, IReadOnlyList<Site>? lookup = null)
    {
        lookup ??= targets;
        var views = new List<SiteView>();
        foreach (var site in targets.OrderBy(s => s.SiteId))
        {
            Json.ValidateSettings(site.SiteSettings);
            foreach (var column in site.SiteSettings.Get("Columns").Array())
            {
                var name = column.Get("ColumnName").Text();
                var text = column.Get("ChoicesText").Text();
                if (text == "" || column.Get("ControlType").Text() is not ("" or "ChoicesText")) continue;
                if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)) throw new UserError("Invalid choice column name.");
                var columns = new List<ViewColumn> { new("Value", "Value"), new("Text", "Text"), new("TextMini", "TextMini") };
                // リンクかどうかは Links で判断する。Links は本体が保存時に ChoicesText から作る（[[N]] の行、または JSON 形式）。
                // 本体の Column.Linked と同じく、SiteId のある設定が1つでもあればリンク項目で、他の行は選択肢に使われない。
                var links = site.SiteSettings.Get("Links").Array().Where(l => l.Get("ColumnName").Text() == name && l.Get("SiteId").TryGetInt64(out var id) && id > 0).ToArray();
                if (links.Length > 0)
                {
                    if (links.Any(l => l.Get("JsonFormat").Bool())) throw new UserError("JSON-format link choices are not supported. No choice views were applied.");
                    var wikis = links.Select((l, i) => new WikiChoiceSource(i + 1, WikiSite(l.Get("SiteId").GetInt64(), lookup))).ToArray();
                    views.Add(new(site.SiteId, site.ReferenceType, columns, site.Title, [], name, wikis));
                    continue;
                }
                // Links がなくても、ChoicesText が JSON の配列なら JSON 形式のリンク（本体の SetLinks と同じ判定）。
                if (LooksLikeJsonLinks(text)) throw new UserError("JSON-format link choices are not supported. No choice views were applied.");
                var rows = new List<ChoiceRow>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l != ""))
                {
                    // Links のない [[...]] は、動的な選択肢（[[Users]] など）か、リンクにならない文字列。推測せずに停止する。
                    if (line.StartsWith("[[", StringComparison.Ordinal)) throw new UserError("Dynamic choice sources cannot be exported as fixed choice lists. No choice views were applied.");
                    var timeout = TimeSpan.FromSeconds(1);
                    var values = Regex.Split(line, split, RegexOptions.CultureInvariant, timeout).Select(v => Regex.Replace(v, replace, replacement, RegexOptions.CultureInvariant, timeout)).ToArray();
                    var value = values[0];
                    var label = values.Length > 1 && values[1] != "" ? values[1] : value;
                    var mini = values.Length > 2 && values[2] != "" ? values[2] : label;
                    // 本体と同じく、行をカンマで単純に区切った先頭要素が重なる場合は最初の行だけを採用する。
                    if (seen.Add(line.Split(',')[0])) rows.Add(new(value, label, mini));
                }
                views.Add(new(site.SiteId, site.ReferenceType, columns, site.Title, rows, name));
            }
        }
        return views;
    }
    private static bool LooksLikeJsonLinks(string text)
    {
        if (!text.TrimStart().StartsWith("[", StringComparison.Ordinal)) return false;
        try { return Json.Parse(text) is { ValueKind: System.Text.Json.JsonValueKind.Array } array && array.EnumerateArray().Any(e => e.ValueKind == System.Text.Json.JsonValueKind.Object); }
        catch (System.Text.Json.JsonException) { return false; }
    }
    private static long WikiSite(long target, IReadOnlyList<Site> lookup)
    {
        var destination = lookup.FirstOrDefault(s => s.SiteId == target) ?? throw new UserError("A linked choice site was not found. No choice views were applied.");
        if (destination.ReferenceType != "Wikis") throw new UserError("Choice links to tables are not supported yet. No choice views were applied.");
        return target;
    }
}