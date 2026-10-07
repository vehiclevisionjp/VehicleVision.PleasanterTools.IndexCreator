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
    }
    public IReadOnlyList<SiteView> Generate(IReadOnlyList<Site> sites)
    {
        var views = new List<SiteView>();
        foreach (var site in sites.OrderBy(s => s.SiteId))
        {
            Json.ValidateSettings(site.SiteSettings);
            foreach (var column in site.SiteSettings.Get("Columns").Array())
            {
                var name = column.Get("ColumnName").Text();
                var text = column.Get("ChoicesText").Text();
                if (text == "") continue;
                if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)) throw new UserError("Invalid choice column name.");
                var rows = new List<ChoiceRow>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l != ""))
                {
                    if (line.StartsWith("[[", StringComparison.Ordinal)) throw new UserError("Dynamic choice sources cannot be exported as fixed choice lists. No choice views were applied.");
                    var timeout = TimeSpan.FromSeconds(1);
                    var values = Regex.Split(line, split, RegexOptions.CultureInvariant, timeout).Select(v => Regex.Replace(v, replace, replacement, RegexOptions.CultureInvariant, timeout)).ToArray();
                    var value = values[0];
                    var label = values.Length > 1 && values[1] != "" ? values[1] : value;
                    var mini = values.Length > 2 && values[2] != "" ? values[2] : label;
                    if (seen.Add(value)) rows.Add(new(value, label, mini));
                }
                views.Add(new(site.SiteId, site.ReferenceType, [new("Value", "Value"), new("Text", "Text"), new("TextMini", "TextMini")], site.Title, rows, name));
            }
        }
        return views;
    }
}
