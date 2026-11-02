using System.Data.Common;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class PermissionBaseline
{
    public static async Task<List<object>> Verify(DbConnection db, Dbms dbms, string prefix, IEnumerable<string> tables, Action<bool, string> check)
    {
        var result = new List<object>();
        foreach (var table in tables)
        {
            await using var command = db.CreateCommand();
            command.CommandText = dbms switch
            {
                Dbms.SQLServer => "SELECT i.name,i.is_primary_key,i.is_unique,ic.key_ordinal,c.name,ic.is_descending_key FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID(@table) AND ic.key_ordinal>0 ORDER BY i.name,ic.key_ordinal",
                Dbms.PostgreSQL => "SELECT ci.relname,ix.indisprimary,ix.indisunique,k.pos,a.attname,(ix.indoption[(k.pos-1)::int]&1)<>0 FROM pg_index ix JOIN pg_class t ON t.oid=ix.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace JOIN pg_class ci ON ci.oid=ix.indexrelid CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY k(attnum,pos) JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum WHERE t.relname=@table AND n.nspname='IndexCreatorTest' ORDER BY ci.relname,k.pos",
                _ => "SELECT INDEX_NAME,INDEX_NAME='PRIMARY',NON_UNIQUE=0,SEQ_IN_INDEX,COLUMN_NAME,COLLATION='D' FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY INDEX_NAME,SEQ_IN_INDEX"
            };
            var parameter = command.CreateParameter(); parameter.ParameterName = "@table"; parameter.Value = prefix + table; command.Parameters.Add(parameter);
            var rows = new List<(string Name, bool Primary, bool Unique, string Key)>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) { rows.Add((reader.GetString(0), Convert.ToBoolean(reader.GetValue(1)), Convert.ToBoolean(reader.GetValue(2)), reader.GetString(4) + (Convert.ToBoolean(reader.GetValue(5)) ? " DESC" : ""))); }
            }
            void Expect(bool primary, bool unique, string keys)
            {
                check(rows.GroupBy(r => r.Name).Any(g => g.First().Primary == primary && g.First().Unique == unique && string.Join(',', g.Select(r => r.Key)) == keys), "Permission native baseline catalog: " + dbms + table + keys);
            }
            if (table == "Sites") { Expect(true, true, "TenantId,SiteId,UpdatedTime" + (dbms == Dbms.PostgreSQL ? "" : " DESC")); Expect(false, true, "SiteId"); }
            else if (table is "Depts" or "Groups" or "Users")
            {
                var id = table[..^1] + "Id";
                Expect(true, true, dbms == Dbms.MySQL ? id : "TenantId," + id);
                if (dbms == Dbms.MySQL) { Expect(false, false, "TenantId," + id); }
                if (table == "Users") { Expect(false, false, "UserId,TenantId"); }
                else { Expect(false, false, "Disabled"); }
            }
            else if (table == "Permissions") { Expect(true, true, "ReferenceId,DeptId,GroupId,UserId"); }
            else if (table == "Items") { Expect(true, true, "ReferenceId"); Expect(false, false, "SiteId,ReferenceId"); }
            else if (table == "GroupMembers")
            {
                Expect(true, true, "GroupId,DeptId,UserId,ChildGroup"); Expect(false, false, "DeptId,GroupId"); Expect(false, false, "UserId,GroupId"); Expect(false, false, "GroupId");
            }
            else if (table == "GroupChildren") { Expect(true, true, "GroupId,ChildId"); Expect(false, false, "ChildId,GroupId"); Expect(false, false, "GroupId"); }
            result.Add(new { Table = table, Indexes = rows.GroupBy(r => r.Name).Select(g => new { Name = g.Key, g.First().Primary, g.First().Unique, Keys = g.Select(r => r.Key).ToArray() }).ToArray() });
        }
        await using var type = db.CreateCommand();
        type.CommandText = "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME=@table AND COLUMN_NAME='PermissionType' AND TABLE_SCHEMA=" + (dbms == Dbms.SQLServer ? "'dbo'" : dbms == Dbms.PostgreSQL ? "'IndexCreatorTest'" : "DATABASE()");
        var p = type.CreateParameter(); p.ParameterName = "@table"; p.Value = prefix + "Permissions"; type.Parameters.Add(p);
        var actualType = Convert.ToString(await type.ExecuteScalarAsync());
        check(string.Equals(actualType, "bigint", StringComparison.OrdinalIgnoreCase), "Permission mask uses native bigint: " + dbms);
        result.Add(new { Table = "Permissions", Column = "PermissionType", DataType = actualType });
        return result;
    }
}
