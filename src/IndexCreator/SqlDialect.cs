using System.Globalization;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed class SqlDialect(Dbms dbms, string schema, bool offline = false)
{
    public Dbms Dbms => dbms;
    // MySQL の選択肢 View の出力列に付ける照合順序。本体の列と結合したときに照合順序の衝突を起こさないよう、スキーマの既定に合わせる。
    public string? Collation
    {
        get;
        set => field = value == null || System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_]{1,64}$") ? value : throw new UserError("Invalid collation name.");
    }
    public string Quote(string name) => dbms switch
    {
        Dbms.SQLServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
        Dbms.PostgreSQL => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        _ => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`"
    };
    public string Table(string name) => Quote(schema) + "." + Quote(name);
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    // 稼働中の DB で DDL がロック待ちの列に並び続けると、後続の業務クエリまで止まる。
    // セッションの待機上限を短くし、取れなければ中止して再試行する。
    public string LockTimeout(int seconds) => dbms switch
    {
        Dbms.SQLServer => $"SET LOCK_TIMEOUT {(seconds * 1000).ToString(CultureInfo.InvariantCulture)};",
        Dbms.PostgreSQL => $"SET lock_timeout = '{seconds.ToString(CultureInfo.InvariantCulture)}s';",
        _ => $"SET SESSION lock_wait_timeout = {Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture)};"
    };
    public string Create(IndexSpec spec, int lockTimeoutSeconds = 5)
    {
        var keys = string.Join(", ", spec.Keys.Select(k => Quote(k.Column) + (k.Prefix > 0 ? $"({k.Prefix})" : "") + (k.Pattern ? " varchar_pattern_ops" : "") + (k.Desc ? " DESC" : " ASC")));
        var create = $"CREATE INDEX {Quote(spec.Name)} ON {Table(spec.Table)} ({keys})";
        var minutes = Math.Max(1, (lockTimeoutSeconds + 59) / 60).ToString(CultureInfo.InvariantCulture);
        return dbms switch
        {
            Dbms.PostgreSQL => create.Replace("CREATE INDEX ", offline ? "CREATE INDEX " : "CREATE INDEX CONCURRENTLY ", StringComparison.Ordinal) + ";",
            Dbms.MySQL => $"ALTER TABLE {Table(spec.Table)} ADD INDEX {Quote(spec.Name)} ({keys})" + (offline ? ";" : ", ALGORITHM=INPLACE, LOCK=NONE;"),
            _ when offline => create + ";",
            // ONLINE に対応しないエディションでは失敗させ、黙ってオフライン作成へ切り替えない。
            // SQL Server 2022 以降と Azure SQL は低優先度でロックを待ち、業務クエリを先に通す。
            _ => $"IF CAST(SERVERPROPERTY('EngineEdition') AS int) IN (5, 8) OR CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 16 EXEC(N{Literal(create + $" WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = {minutes} MINUTES, ABORT_AFTER_WAIT = SELF)))")}); ELSE EXEC(N{Literal(create + " WITH (ONLINE = ON)")});"
        };
    }
    public string Drop(string table, string name) => dbms switch
    {
        Dbms.PostgreSQL => $"DROP INDEX {(offline ? "" : "CONCURRENTLY ")}{Quote(schema)}.{Quote(name)};",
        Dbms.MySQL => $"ALTER TABLE {Table(table)} DROP INDEX {Quote(name)}" + (offline ? ";" : ", ALGORITHM=INPLACE, LOCK=NONE;"),
        _ => $"DROP INDEX {Quote(name)} ON {Table(table)};"
    };
    // 選択肢 View は Sites.SiteSettings を DB の組込関数で都度展開する。選択肢の編集は View の再作成なしで反映される。
    // 本体の既定規則（改行で行に分け前後の空白を除く、\ が前にないカンマで区切り \, をカンマへ戻す、
    // 先頭要素が重なる行は最初の行を採用）を各 DB で再現する。列名は英数字に限定済み。
    public string ChoiceSelect(long siteId, string column)
    {
        var id = siteId.ToString(CultureInfo.InvariantCulture);
        var name = Literal(column);
        var collate = Collation == null ? "" : " COLLATE " + Collation;
        var q = Quote;
        return dbms switch
        {
            Dbms.SQLServer => $"""
                SELECT REPLACE(p.[1], NCHAR(1), N',') COLLATE DATABASE_DEFAULT AS {q("Value")}, REPLACE(COALESCE(NULLIF(p.[2], N''), p.[1]), NCHAR(1), N',') COLLATE DATABASE_DEFAULT AS {q("Text")}, REPLACE(COALESCE(NULLIF(p.[3], N''), NULLIF(p.[2], N''), p.[1]), NCHAR(1), N',') COLLATE DATABASE_DEFAULT AS {q("TextMini")}
                FROM (
                SELECT l.line, ROW_NUMBER() OVER (PARTITION BY LEFT(l.line, CHARINDEX(N',', l.line + N',') - 1) ORDER BY x.ordinal) AS rn
                FROM {Table("Sites")} s
                CROSS APPLY OPENJSON(CASE WHEN ISJSON(s.{q("SiteSettings")}) = 1 THEN s.{q("SiteSettings")} END, N'$.Columns') WITH (ColumnName nvarchar(128) N'$.ColumnName', ControlType nvarchar(128) N'$.ControlType', ChoicesText nvarchar(max) N'$.ChoicesText') c
                CROSS APPLY STRING_SPLIT(REPLACE(c.ChoicesText COLLATE Latin1_General_100_BIN2, NCHAR(13) + NCHAR(10), NCHAR(10)), NCHAR(10), 1) x
                CROSS APPLY (SELECT TRIM(CONCAT(N' ', NCHAR(9), NCHAR(11), NCHAR(12), NCHAR(13), NCHAR(160), NCHAR(12288)) FROM x.value) AS line) l
                WHERE s.{q("SiteId")} = {id} AND c.ColumnName COLLATE Latin1_General_100_BIN2 = N{name} AND COALESCE(c.ControlType, N'') IN (N'', N'ChoicesText') AND l.line <> N'' AND LEFT(l.line, 2) <> N'[['
                ) t
                CROSS APPLY (SELECT pv.[1], pv.[2], pv.[3] FROM (SELECT ordinal, value FROM STRING_SPLIT(REPLACE(t.line, N'\,', NCHAR(1)), N',', 1)) v PIVOT (MAX(value) FOR ordinal IN ([1], [2], [3])) pv) p
                WHERE t.rn = 1
                """,
            Dbms.PostgreSQL => $"""
                SELECT t.a[1] AS {q("Value")}, COALESCE(NULLIF(t.a[2], ''), t.a[1]) AS {q("Text")}, COALESCE(NULLIF(t.a[3], ''), NULLIF(t.a[2], ''), t.a[1]) AS {q("TextMini")}
                FROM (
                SELECT DISTINCT ON (split_part(l.line, ',', 1)) ARRAY(SELECT regexp_replace(e.v, E'\\\\(,)', E'\\1', 'g') FROM unnest(regexp_split_to_array(l.line, E'(?<!\\\\),')) WITH ORDINALITY e(v, o) ORDER BY e.o) AS a
                FROM (SELECT {q("SiteSettings")}::jsonb -> 'Columns' AS columns FROM {Table("Sites")} WHERE {q("SiteId")} = {id}) s
                CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(s.columns) = 'array' THEN s.columns ELSE '[]'::jsonb END) c(col)
                CROSS JOIN LATERAL regexp_split_to_table(c.col ->> 'ChoicesText', E'\n') WITH ORDINALITY x(raw, n)
                CROSS JOIN LATERAL (SELECT btrim(x.raw, E' \t\r\f\u000B 　') AS line) l
                WHERE c.col ->> 'ColumnName' = {name} AND COALESCE(c.col ->> 'ControlType', '') IN ('', 'ChoicesText') AND l.line <> '' AND left(l.line, 2) <> '[['
                ORDER BY split_part(l.line, ',', 1), x.n
                ) t
                """,
            // MySQL は sql_mode でバックスラッシュの扱いが変わるため、バックスラッシュを含むリテラルを使わない。
            _ => $"""
                SELECT REPLACE(z.f1, CHAR(1 USING utf8mb4), ','){collate} AS {q("Value")}, REPLACE(COALESCE(NULLIF(z.f2, ''), z.f1), CHAR(1 USING utf8mb4), ','){collate} AS {q("Text")}, REPLACE(COALESCE(NULLIF(z.f3, ''), NULLIF(z.f2, ''), z.f1), CHAR(1 USING utf8mb4), ','){collate} AS {q("TextMini")}
                FROM (
                SELECT SUBSTRING_INDEX(y.e, ',', 1) AS f1, IF(y.e LIKE '%,%', SUBSTRING_INDEX(SUBSTRING_INDEX(y.e, ',', 2), ',', -1), NULL) AS f2, IF(y.e LIKE '%,%,%', SUBSTRING_INDEX(SUBSTRING_INDEX(y.e, ',', 3), ',', -1), NULL) AS f3,
                ROW_NUMBER() OVER (PARTITION BY CAST(SUBSTRING_INDEX(y.line, ',', 1) AS BINARY) ORDER BY y.n) AS rn
                FROM (
                SELECT l.line, l.n, REPLACE(l.line, CONCAT(CHAR(92 USING utf8mb4), ','), CHAR(1 USING utf8mb4)) AS e
                FROM (
                SELECT x.n, REGEXP_REPLACE(SUBSTRING_INDEX(SUBSTRING_INDEX(c.ChoicesText, CHAR(10 USING utf8mb4), x.n), CHAR(10 USING utf8mb4), -1), '^[[:space:]]+|[[:space:]]+$', '') AS line
                FROM {Table("Sites")} s
                CROSS JOIN JSON_TABLE(IF(JSON_VALID(s.{q("SiteSettings")}), s.{q("SiteSettings")}, '{"{}"}'), '$.Columns[*]' COLUMNS(ColumnName VARCHAR(128) PATH '$.ColumnName', ControlType VARCHAR(128) PATH '$.ControlType', ChoicesText LONGTEXT PATH '$.ChoicesText')) c
                CROSS JOIN JSON_TABLE(CONCAT('[', REPEAT('0,', CHAR_LENGTH(c.ChoicesText) - CHAR_LENGTH(REPLACE(c.ChoicesText, CHAR(10 USING utf8mb4), ''))), '0]'), '$[*]' COLUMNS(n FOR ORDINALITY)) x
                WHERE s.{q("SiteId")} = {id} AND CAST(c.ColumnName AS BINARY) = CAST({name} AS BINARY) AND COALESCE(c.ControlType, '') IN ('', 'ChoicesText')
                ) l
                WHERE l.line <> '' AND LEFT(l.line, 2) <> '[['
                ) y
                ) z
                WHERE z.rn = 1
                """
        };
    }
}
