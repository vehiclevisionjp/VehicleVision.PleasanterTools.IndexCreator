namespace VehicleVision.PleasanterTools.IndexCreator;

public static class RecordIdLookupPlanner
{
    public static Analysis Complete(Analysis analysis, IReadOnlyList<Site> sites)
    {
        // ID はサイトをまたぐ参照に使う。サイトの件数閾値では物理表の探索負荷を判断できない。
        // 標準索引の復旧ではなく、既存の先頭 ID 索引で代替できる通常の非一意索引候補とする。
        var lookups = sites
            .Where(s => s.ReferenceType is "Results" or "Issues" or "Wikis")
            .GroupBy(s => s.ReferenceType, StringComparer.Ordinal)
            .Select(g => new IndexSpec(g.Key, [new(g.Key[..^1] + "Id")], g.Min(s => s.SiteId)))
            .ToArray();
        var desired = analysis.Indexes.Concat(lookups)
            .GroupBy(i => i.Signature, StringComparer.Ordinal)
            .Select(g => g.OrderBy(i => i.SiteId).First())
            .ToArray();
        var compact = desired
            .Where(i => !desired.Any(j => j.Keys.Count > i.Keys.Count && j.Covers(i)))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToArray();
        return new(compact, analysis.Diagnostics);
    }
}
