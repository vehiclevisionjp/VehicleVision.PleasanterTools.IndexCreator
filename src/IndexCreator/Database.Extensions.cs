namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed partial class Database
{
    public async Task<IReadOnlyList<AnalysisExtension>> ReadAnalysisExtensions(CancellationToken ct)
    {
        var q = dialect.Quote;
        var enabled = config.Dbms == Dbms.PostgreSQL ? "false" : "0";
        await using var command = Command($"SELECT {q("ExtensionType")}, {q("ExtensionSettings")}, {q("Body")} FROM {dialect.Table("Extensions")} WHERE {q("Disabled")}={enabled} AND {q("ExtensionType")} IN ('ServerScript','Sql') ORDER BY {q("ExtensionName")}, {q("ExtensionId")}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AnalysisExtension>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1))
            {
                throw new UserError("An extension has missing settings.");
            }
            result.Add(ExtensionInventory.Normalize("table", reader.GetString(0), Json.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }
}
