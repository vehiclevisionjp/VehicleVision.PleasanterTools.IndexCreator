namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed class SqlDialect(Dbms dbms, string schema, bool offline = false)
{
    public string Quote(string name) => dbms switch
    {
        Dbms.SQLServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
        Dbms.PostgreSQL => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        _ => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`"
    };
    public string Table(string name) => Quote(schema) + "." + Quote(name);
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    public string TextLiteral(string value) => (dbms == Dbms.SQLServer ? "N" : "") + Literal(value);
    public string Create(IndexSpec spec)
    {
        var keys = string.Join(", ", spec.Keys.Select(k => Quote(k.Column) + (k.Prefix > 0 ? $"({k.Prefix})" : "") + (k.Pattern ? " varchar_pattern_ops" : "") + (k.Desc ? " DESC" : " ASC")));
        var create = $"CREATE INDEX {Quote(spec.Name)} ON {Table(spec.Table)} ({keys})";
        return dbms switch
        {
            Dbms.PostgreSQL => create.Replace("CREATE INDEX ", offline ? "CREATE INDEX " : "CREATE INDEX CONCURRENTLY ", StringComparison.Ordinal) + ";",
            Dbms.MySQL => $"ALTER TABLE {Table(spec.Table)} ADD INDEX {Quote(spec.Name)} ({keys})" + (offline ? ";" : ", ALGORITHM=INPLACE, LOCK=NONE;"),
            _ when offline => create + ";",
            _ => $"IF CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) EXEC(N{Literal(create + " WITH (ONLINE = ON)")}); ELSE EXEC(N{Literal(create)});"
        };
    }
    public string Drop(string table, string name) => dbms switch
    {
        Dbms.PostgreSQL => $"DROP INDEX {(offline ? "" : "CONCURRENTLY ")}{Quote(schema)}.{Quote(name)};",
        Dbms.MySQL => $"ALTER TABLE {Table(table)} DROP INDEX {Quote(name)}" + (offline ? ";" : ", ALGORITHM=INPLACE, LOCK=NONE;"),
        _ => $"DROP INDEX {Quote(name)} ON {Table(table)};"
    };
}
