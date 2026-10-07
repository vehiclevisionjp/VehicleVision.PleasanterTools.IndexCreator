using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IndexCreator;

public enum Dbms { SQLServer, PostgreSQL, MySQL }
public sealed record Site(long SiteId, string ReferenceType, JsonElement SiteSettings, long RecordCount, string Title = "");
public sealed record Key(string Column, bool Desc = false, int Prefix = 0, bool Pattern = false)
{
    public string Signature => $"{Column}:{(Desc ? "D" : "A")}:{Prefix}:{Pattern}";
}
public sealed record IndexSpec(string Table, IReadOnlyList<Key> Keys, long SiteId = 1)
{
    public const string Prefix = "IX_vvplic_";
    public string Signature => Table + "|" + string.Join("|", Keys.Select(k => k.Signature));
    public string Name
    {
        get
        {
            var prefix = Prefix + Table + "_" + SiteId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_";
            var purpose = Keys.FirstOrDefault(k => k.Column != "SiteId")?.Column ?? "SiteId";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Signature)))[..16];
            return prefix + purpose[..Math.Min(purpose.Length, 63 - prefix.Length - 17)] + "_" + hash;
        }
    }
    public bool Covers(IndexSpec other) => Table == other.Table && Keys.Count >= other.Keys.Count && Keys.Take(other.Keys.Count).SequenceEqual(other.Keys);
    public static bool IsManaged(string table, string name) => table is "Results" or "Issues" or "Wikis" && name.Length <= 63 && Regex.IsMatch(name, "^" + Prefix + Regex.Escape(table) + "_[1-9][0-9]{0,18}_[A-Za-z0-9]+_[0-9a-f]{16}$", RegexOptions.CultureInvariant);
}
public sealed record ExistingIndex(string Table, string Name, IReadOnlyList<Key> Keys, bool Valid = true, bool Plain = true)
{
    public IndexSpec Spec => new(Table, Keys);
    public bool Managed => IndexSpec.IsManaged(Table, Name);
}
public sealed record Diagnostic(long SiteId, string Message);
public sealed record Analysis(IReadOnlyList<IndexSpec> Indexes, IReadOnlyList<Diagnostic> Diagnostics);
public enum ChangeKind { Create, Keep, Repair, Drop }
public sealed record Change(ChangeKind Kind, IndexSpec Spec, string Name);
public static class Reconciler
{
    public static IReadOnlyList<Change> Plan(IReadOnlyList<IndexSpec> desired, IReadOnlyList<ExistingIndex> existing, bool prune, bool force = false)
    {
        var changes = new List<Change>();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in desired)
        {
            var same = existing.FirstOrDefault(e => e.Table == spec.Table && e.Name == spec.Name);
            if (same != null)
            {
                if (!same.Plain || same.Spec.Signature != spec.Signature)
                    throw new UserError("A managed index name has a conflicting definition. No changes were applied.");
                retained.Add(same.Name);
                changes.Add(new(same.Valid && !force ? ChangeKind.Keep : ChangeKind.Repair, spec, same.Name));
                continue;
            }
            // 管理外の既存索引のみで代替する。古い管理索引を代替にすると prune 後に不足する。
            var cover = existing.FirstOrDefault(e => !e.Managed && e.Plain && e.Valid && e.Spec.Covers(spec));
            changes.Add(new(cover == null ? ChangeKind.Create : ChangeKind.Keep, spec, cover?.Name ?? spec.Name));
        }
        if (prune)
            foreach (var old in existing.Where(e => e.Managed && !retained.Contains(e.Name)))
                changes.Add(new(ChangeKind.Drop, old.Spec, old.Name));
        return changes;
    }
}
public sealed class UserError(string message) : Exception(message);
public static class Json
{
    public static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
    public static JsonElement Get(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    public static string Text(this JsonElement e) => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : e.ToString();
    public static bool Bool(this JsonElement e) => e.ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Array(this JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    public static IEnumerable<JsonProperty> Props(this JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : [];
    public static JsonElement Object(this JsonElement e) => e.ValueKind == JsonValueKind.String ? Parse(e.GetString()!) : e;
    public static void ValidateSettings(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object) throw new UserError("SiteSettings must be a JSON object.");
        foreach (var name in new[] { "Views", "Columns", "Links", "Summaries", "GridColumns", "FilterColumns" })
        {
            var value = settings.Get(name);
            if (value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Array)) throw new UserError("A SiteSettings collection has an invalid shape. No changes were applied.");
        }
        foreach (var v in settings.Get("Views").Array())
        {
            if (v.ValueKind != JsonValueKind.Object) throw new UserError("A view must be a JSON object.");
            foreach (var name in new[] { "ColumnFilterHash", "ColumnSorterHash", "ColumnFilterSearchTypes" })
                if (v.Get(name).ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new UserError("A view dictionary has an invalid shape. No changes were applied.");
        }
    }
    public static IReadOnlyList<Site> ReadSites(string text)
    {
        var root = Parse(text);
        if (root.ValueKind != JsonValueKind.Array) throw new UserError("The sites input must be a JSON array.");
        var sites = new List<Site>();
        foreach (var row in root.EnumerateArray())
        {
            var table = row.Get("ReferenceType").Text();
            if (table is not ("Results" or "Issues" or "Wikis")) continue;
            if (!row.Get("SiteId").TryGetInt64(out var id) || id <= 0 || !row.Get("RecordCount").TryGetInt64(out var count) || count < 0)
                throw new UserError("Each site requires a positive SiteId and a non-negative RecordCount.");
            var settings = row.Get("SiteSettings").Object();
            ValidateSettings(settings);
            sites.Add(new(id, table, settings, count, row.Get("Title").Text()));
        }
        if (sites.Select(s => s.SiteId).Distinct().Count() != sites.Count) throw new UserError("Duplicate SiteId in input.");
        return sites;
    }
}
