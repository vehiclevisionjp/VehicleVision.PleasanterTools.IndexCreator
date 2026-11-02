using System.Data.Common;
using VehicleVision.PleasanterTools.IndexCreator;

// 権限取得の合成試験が使う、検索に関係する標準キーの独立した記述。
// 固定参照 664c833b144d9da9b6bb58e60f7e48eb01141e58（2026-11-02）の列定義・CodeDefiner・DDL雛形を読んで確認した
// 列順・方向・一意性・DB別の差を、本体のコードやDDLを複写せずに自前で書き下している。
// 本体の全列・全標準索引・クラスタ化の完全なレプリカではない。
// ログインID・コード・SCIM・LDAP・APIキー・Sites.Form・Sites.ReferenceType 先頭など、この検索に関係しない標準索引は含めない。
internal static class PermissionStandardKeys
{
    internal sealed record Column(string Name, bool Desc = false);

    internal sealed record Index(bool Unique, params Column[] Columns);

    // 権限値の合成データ。32bitの符号付き整数に収まらない値を含める。
    internal const long High31 = 1L << 31;
    internal const long High40 = 1L << 40;

    private static Column[] Asc(params string[] names) => names.Select(n => new Column(n)).ToArray();

    // 主キー。SQL Server と MySQL は方向指定つきで作られ、PostgreSQL の主キー生成は方向指定を除く。
    // Sites の UpdatedTime は共通列の定義（主キー3番目・降順）による。
    // MySQL は Depts / Groups / Users だけ、ID 単独の主キーに作り替える。
    internal static Column[] Primary(Dbms dbms, string table) => table switch
    {
        "Sites" => [new("TenantId"), new("SiteId"), new("UpdatedTime", dbms != Dbms.PostgreSQL)],
        "Items" => Asc("ReferenceId"),
        "Permissions" => Asc("ReferenceId", "DeptId", "GroupId", "UserId"),
        "Depts" => dbms == Dbms.MySQL ? Asc("DeptId") : Asc("TenantId", "DeptId"),
        "Groups" => dbms == Dbms.MySQL ? Asc("GroupId") : Asc("TenantId", "GroupId"),
        "Users" => dbms == Dbms.MySQL ? Asc("UserId") : Asc("TenantId", "UserId"),
        "GroupMembers" => Asc("GroupId", "DeptId", "UserId", "ChildGroup"),
        "GroupChildren" => Asc("GroupId", "ChildId"),
        _ => throw new ArgumentException(table)
    };

    // 主キー以外の、検索に関係する標準索引。
    // MySQL の Depts / Groups / Users は、元の主キー列を通常索引として残す。
    internal static Index[] Secondary(Dbms dbms, string table) => table switch
    {
        // SiteId の逆引きは一意。
        "Sites" => [new(true, Asc("SiteId"))],
        "Items" => [new(false, Asc("SiteId", "ReferenceId"))],
        "Permissions" => [],
        "Depts" => [.. Original(dbms, "TenantId", "DeptId"), new(false, Asc("Disabled"))],
        "Groups" => [.. Original(dbms, "TenantId", "GroupId"), new(false, Asc("Disabled"))],
        "Users" => [.. Original(dbms, "TenantId", "UserId"), new(false, Asc("UserId", "TenantId"))],
        "GroupMembers" => [new(false, Asc("DeptId", "GroupId")), new(false, Asc("UserId", "GroupId")), new(false, Asc("GroupId"))],
        // GroupId 単独の標準索引がある。主キー先頭でも読めるが、別の索引としても存在する。
        "GroupChildren" => [new(false, Asc("ChildId", "GroupId")), new(false, Asc("GroupId"))],
        _ => throw new ArgumentException(table)
    };

    private static Index[] Original(Dbms dbms, params string[] columns) => dbms == Dbms.MySQL ? [new(false, Asc(columns))] : [];

    internal static string PrimaryKeySql(Dbms dbms, SqlDialect d, string table) =>
        $"PRIMARY KEY({string.Join(',', Primary(dbms, table).Select(c => d.Quote(c.Name) + (c.Desc ? " DESC" : "")))})";

    internal static IEnumerable<(string Name, string Table, string Sql)> IndexSql(Dbms dbms, SqlDialect d, string table, string prefix)
    {
        var no = 0;
        foreach (var index in Secondary(dbms, table))
        {
            var name = $"{prefix}_{table}_{no++}";
            yield return (name, table, $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {d.Quote(name)} ON {d.Table(prefix + table)} ({string.Join(',', index.Columns.Select(c => d.Quote(c.Name) + (c.Desc ? " DESC" : "")))})");
        }
    }

    private static string Signature(string kind, IEnumerable<Column> columns) => kind + " " + string.Join(",", columns.Select(c => c.Name + (c.Desc ? " DESC" : "")));

    // DB側のメタデータ（索引ごとの列順・方向・一意性・主キー）を読み、標準キーの記述と照合する。
    // 付けた名前には依存しない。候補索引を作る前に呼ぶ。
    internal static async Task Verify(DbConnection db, Dbms dbms, SqlDialect d, string tablePrefix, IEnumerable<string> tables, Action<bool, string> check)
    {
        foreach (var table in tables)
        {
            var physical = tablePrefix + table;
            await using var command = db.CreateCommand();
            command.CommandText = dbms switch
            {
                Dbms.SQLServer => $"SELECT i.name,i.is_primary_key,i.is_unique,ic.key_ordinal,c.name,ic.is_descending_key FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID(N'{d.Table(physical)}') AND ic.is_included_column=0 AND ic.key_ordinal>0",
                Dbms.PostgreSQL => $"SELECT i.relname,ix.indisprimary,ix.indisunique,k.ord,a.attname,((ix.indoption[k.ord-1] & 1)=1) FROM pg_index ix JOIN pg_class i ON i.oid=ix.indexrelid CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum,ord) JOIN pg_attribute a ON a.attrelid=ix.indrelid AND a.attnum=k.attnum WHERE ix.indrelid=to_regclass('{d.Table(physical)}')",
                _ => "SELECT INDEX_NAME,INDEX_NAME='PRIMARY',NON_UNIQUE=0,SEQ_IN_INDEX,COLUMN_NAME,COLLATION='D' FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table"
            };
            if (dbms == Dbms.MySQL)
            {
                var p = command.CreateParameter(); p.ParameterName = "@table"; p.Value = physical; command.Parameters.Add(p);
            }
            var rows = new List<(string Index, bool Primary, bool Unique, int Order, string Column, bool Desc)>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), Convert.ToBoolean(reader.GetValue(1)), Convert.ToBoolean(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), reader.GetString(4), Convert.ToBoolean(reader.GetValue(5))));
                }
            }
            var actual = rows.GroupBy(r => r.Index).Select(g =>
            {
                var ordered = g.OrderBy(r => r.Order).ToArray();
                return Signature(ordered[0].Primary ? "PK" : ordered[0].Unique ? "UNIQUE" : "INDEX", ordered.Select(r => new Column(r.Column, r.Desc)));
            }).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var expected = Secondary(dbms, table).Select(i => Signature(i.Unique ? "UNIQUE" : "INDEX", i.Columns)).Append(Signature("PK", Primary(dbms, table))).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            check(actual.SequenceEqual(expected), $"Standard key metadata (order, direction, uniqueness) matches for {table} on {dbms}: actual [{string.Join("; ", actual)}] expected [{string.Join("; ", expected)}]");
        }
    }
}
