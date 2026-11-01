namespace VehicleVision.PleasanterTools.IndexCreator;

public static class SiteDeltaPlanner
{
    public static IndexSpec Candidate => new("Sites", [new("TenantId"), new("UpdatedTime")], 0);

    // 明示指定がない実行では共通Sites索引をpruneの範囲へ含めない。
    public static IReadOnlyList<ExistingIndex> Scope(IReadOnlyList<ExistingIndex> existing, bool enabled) =>
        existing.Where(index => enabled || index.Table != "Sites").ToArray();
}
