namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed partial class Database
{
    public async Task<IReadOnlyList<Site>> ReadDashboardSites(CancellationToken ct)
    {
        var q = dialect.Quote;
        await using var command = Command($"SELECT {q("SiteId")},{q("SiteSettings")} FROM {dialect.Table("Sites")} WHERE {q("ReferenceType")}='Dashboards' ORDER BY {q("SiteId")}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var sites = new List<Site>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1)) { throw new UserError("Dashboard SiteSettings is missing."); }
            var settings = Json.Parse(reader.GetString(1)); Json.ValidateSettings(settings);
            sites.Add(new(Convert.ToInt64(reader.GetValue(0)), "Dashboards", settings, 0));
        }
        return sites;
    }
}
