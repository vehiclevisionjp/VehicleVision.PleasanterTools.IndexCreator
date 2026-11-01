using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class AdminDirectoryWorkloadTests
{
    private sealed record Row(int Tenant, int Id, DateTime Updated, bool Disabled, string? Code);

    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        string Q(string name) => d.Quote(name);
        string Text(int length) => (dbms == Dbms.SQLServer ? "nvarchar" : "varchar") + "(" + length + ")";
        var dateType = dbms == Dbms.SQLServer ? "datetime" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp";
        var boolean = dbms == Dbms.SQLServer ? "bit" : "boolean";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.CommandTimeout = 60;
            return await command.ExecuteNonQueryAsync();
        }
        void Bind(DbCommand command, int tenant, string? pattern = null)
        {
            var p = command.CreateParameter(); p.ParameterName = "@tenant"; p.DbType = System.Data.DbType.Int32; p.Value = tenant; command.Parameters.Add(p);
            if (pattern != null) { p = command.CreateParameter(); p.ParameterName = "@pattern"; p.DbType = System.Data.DbType.String; p.Value = pattern; command.Parameters.Add(p); }
        }
        string Literal(object? value) => value switch
        {
            null => "NULL",
            bool b => dbms == Dbms.PostgreSQL ? b ? "TRUE" : "FALSE" : b ? "1" : "0",
            DateTime date => "'" + date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'",
            int id => id.ToString(CultureInfo.InvariantCulture),
            _ => SqlDialect.Literal((string)value)
        };
        foreach (var kind in new[] { "Users", "Depts", "Groups" })
        {
            var idColumn = kind[..^1] + "Id";
            var codeColumn = kind == "Users" ? "LoginId" : kind == "Depts" ? "DeptCode" : "LdapGuid";
            var tableName = "Directory" + kind; var table = d.Table(tableName);
            var columns = new List<string> { "TenantId", idColumn, "UpdatedTime", "Disabled", codeColumn, "Payload" };
            if (kind != "Depts") { columns.AddRange(["ScimExternalId", "ScimId"]); }
            if (kind == "Users") { columns.Add("ApiKey"); }
            IEnumerable<object?> Values(Row row)
            {
                yield return row.Tenant; yield return row.Id; yield return row.Updated; yield return row.Disabled; yield return row.Code; yield return new string('X', 256);
                if (kind != "Depts") { yield return row.Id % 3 == 0 ? null : "external-" + row.Id; yield return row.Id % 5 == 0 ? null : "scim-" + row.Id; }
                if (kind == "Users") { yield return row.Id % 2 == 0 ? null : "synthetic-not-a-secret-" + row.Id; }
            }
            string Record(Row row) => "(" + string.Join(',', Values(row).Select(Literal)) + ")";
            var definitions = new List<string>
            {
                $"{Q("TenantId")} int NOT NULL",$"{Q(idColumn)} int NOT NULL",$"{Q("UpdatedTime")} {dateType} NOT NULL",$"{Q("Disabled")} {boolean} NOT NULL",
                $"{Q(codeColumn)} {Text(kind == "Depts" ? dbms == Dbms.MySQL ? 760 : 1024 : 256)} {(kind == "Groups" ? "NULL" : "NOT NULL")}",$"{Q("Payload")} {Text(512)} NOT NULL"
            };
            if (kind != "Depts") { definitions.AddRange([$"{Q("ScimExternalId")} {Text(256)} NULL", $"{Q("ScimId")} {Text(64)} NULL"]); }
            if (kind == "Users") { definitions.Add($"{Q("ApiKey")} {Text(128)} NULL"); }
            definitions.Add("PRIMARY KEY(" + (dbms == Dbms.MySQL ? Q(idColumn) : Q("TenantId") + "," + Q(idColumn)) + ")");
            await Execute($"CREATE TABLE {table} ({string.Join(',', definitions)})" + (dbms == Dbms.MySQL ? " DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci" : ""));
            try
            {
                var standard = new List<(string Keys, bool Unique)>();
                if (dbms == Dbms.MySQL) { standard.Add(("TenantId," + idColumn, false)); }
                if (kind == "Users")
                {
                    standard.AddRange([("UserId,TenantId", false), ("LoginId,ScimExternalId,ScimId,TenantId,UserId", false), ("ApiKey,TenantId,UserId", false), ("LoginId", true)]);
                }
                else if (kind == "Depts") { standard.AddRange([("DeptCode", false), ("Disabled", false)]); }
                else { standard.AddRange([("Disabled", false), ("LdapGuid,ScimExternalId,ScimId", false)]); }
                for (var n = 0; n < standard.Count; n++)
                {
                    await Execute($"CREATE {(standard[n].Unique ? "UNIQUE " : "")}INDEX {Q("directory_" + kind + "_standard_" + n)} ON {table} ({string.Join(',', standard[n].Keys.Split(',').Select(Q))})");
                }
                foreach (var scenario in new[] { "small-uniform", "larger-skewed" })
                {
                    var count = scenario == "small-uniform" ? 400 : 4000;
                    Row Make(int id) => new(scenario == "larger-skewed" && id % 100 < 75 ? 1 : id % 4 + 1, id, new DateTime(2026, 1, 1).AddSeconds(id), id % (scenario == "small-uniform" ? 7 : 5) == 0, kind == "Groups" && id % 7 == 0 ? null : "K" + id.ToString("D6", CultureInfo.InvariantCulture));
                    var rows = Enumerable.Range(1, count).Select(Make).ToArray();
                    var reports = new List<object>(); var switches = new List<object>(); var countPlans = new List<object>(); var writes = new List<object>(); var sizes = new List<object>();
                    var candidates = new[] { (Phase: "updated", Name: "directory_updated", Keys: $"{Q("TenantId")},{Q("UpdatedTime")} DESC,{Q(idColumn)}"), (Phase: "disabled-updated", Name: "directory_disabled_updated", Keys: $"{Q("TenantId")},{Q("Disabled")},{Q("UpdatedTime")} DESC,{Q(idColumn)}") };
                    var previous = "standard";
                    foreach (var phase in new[] { "standard", "updated", "disabled-updated", "combined" })
                    {
                        foreach (var candidate in candidates.Where(c => c.Phase == previous || previous == "combined")) { await Execute(d.Drop(tableName, candidate.Name)); }
                        await Execute("TRUNCATE TABLE " + table);
                        for (var first = 0; first < rows.Length; first += 200) { await Execute($"INSERT INTO {table} ({string.Join(',', columns.Select(Q))}) VALUES " + string.Join(',', rows.Skip(first).Take(200).Select(Record))); }
                        var active = candidates.Where(c => c.Phase == phase || phase == "combined").ToArray();
                        foreach (var candidate in active) { await Execute($"CREATE INDEX {Q(candidate.Name)} ON {table} ({candidate.Keys})"); }
                        previous = phase;
                        await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {table} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + table, _ => "ANALYZE TABLE " + table });
                        foreach (var candidate in active)
                        {
                            await using var size = db.CreateCommand();
                            size.CommandText = dbms switch
                            {
                                Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{table}') AND i.name=@name",
                                Dbms.PostgreSQL => $"SELECT pg_relation_size('{d.Table(candidate.Name)}')",
                                _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name=@table AND index_name=@name AND stat_name='size'"
                            };
                            if (dbms != Dbms.PostgreSQL) { var p = size.CreateParameter(); p.ParameterName = "@name"; p.Value = candidate.Name; size.Parameters.Add(p); }
                            if (dbms == Dbms.MySQL) { var p = size.CreateParameter(); p.ParameterName = "@table"; p.Value = tableName; size.Parameters.Add(p); }
                            var bytes = Convert.ToInt64(await size.ExecuteScalarAsync()); check(bytes > 0, "Directory candidate capacity: " + dbms + kind);
                            sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                        }
                        foreach (var query in new[]
                        {
                            (Name:"tenant-default",Tenant:1,Enabled:false,Pattern:(string?)null,Mode:"",Wide:false,Explicit:false),
                            (Name:"enabled-switch",Tenant:1,Enabled:true,Pattern:(string?)null,Mode:"",Wide:false,Explicit:false),
                            (Name:"enabled-list",Tenant:1,Enabled:true,Pattern:(string?)null,Mode:"",Wide:true,Explicit:false),
                            (Name:"exact",Tenant:1,Enabled:false,Pattern:(string?)"K000004",Mode:"exact",Wide:false,Explicit:false),
                            (Name:"prefix",Tenant:1,Enabled:false,Pattern:(string?)"K0000",Mode:"prefix",Wide:false,Explicit:false),
                            (Name:"contains",Tenant:1,Enabled:false,Pattern:(string?)"004",Mode:"contains",Wide:true,Explicit:false),
                            (Name:"other-tenant",Tenant:2,Enabled:false,Pattern:(string?)null,Mode:"",Wide:false,Explicit:false),
                            (Name:"empty",Tenant:99,Enabled:false,Pattern:(string?)null,Mode:"",Wide:false,Explicit:false),
                            (Name:"explicit-id-order",Tenant:1,Enabled:false,Pattern:(string?)null,Mode:"",Wide:false,Explicit:true)
                        })
                        {
                            var matching = rows.Where(r => r.Tenant == query.Tenant && (!query.Enabled || !r.Disabled) && (query.Pattern == null || query.Mode switch { "exact" => r.Code == query.Pattern, "prefix" => r.Code?.StartsWith(query.Pattern, StringComparison.Ordinal) == true, _ => r.Code?.Contains(query.Pattern, StringComparison.Ordinal) == true })).ToArray();
                            var expected = (query.Explicit ? matching.OrderBy(r => r.Id) : matching.OrderByDescending(r => r.Updated)).Select(r => r.Id).ToArray();
                            var where = $"{Q("TenantId")}=@tenant" + (query.Enabled ? $" AND {Q("Disabled")}={Literal(false)}" : "") + (query.Pattern == null ? "" : $" AND {Q(codeColumn)}{(query.Mode == "exact" ? "=" : " LIKE ")}@pattern");
                            var pattern = query.Pattern == null ? null : query.Mode == "prefix" ? query.Pattern + "%" : query.Mode == "contains" ? "%" + query.Pattern + "%" : query.Pattern;
                            var sql = $"SELECT {Q(idColumn)}" + (query.Wide ? $",{Q(codeColumn)},{Q("Payload")},{Q("UpdatedTime")}" : "") + $" FROM {table} WHERE {where} ORDER BY " + (query.Explicit ? Q(idColumn) + " ASC," : "") + Q("UpdatedTime") + " DESC";
                            var times = new List<double>();
                            for (var sample = 0; sample < 6; sample++)
                            {
                                await using var command = db.CreateCommand(); command.CommandText = sql; Bind(command, query.Tenant, pattern);
                                var timer = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync(); var actual = new List<int>();
                                while (await reader.ReadAsync()) { actual.Add(Convert.ToInt32(reader.GetValue(0))); }
                                timer.Stop(); check(actual.SequenceEqual(expected), "Directory result and order: " + dbms + kind + phase + query.Name);
                                if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                            }
                            await using var plan = db.CreateCommand(); Bind(plan, query.Tenant, pattern);
                            plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                            await using var plans = await plan.ExecuteReaderAsync(); if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                            var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                            check(lines.Count > 0, "Directory actual plan: " + dbms + kind);
                            times.Sort(); reports.Add(new { Phase = phase, Query = query.Name, Rows = expected.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                        }
                        var enabled = rows.Where(r => r.Tenant == 1 && !r.Disabled).OrderByDescending(r => r.Updated).Select(r => r.Id).ToArray();
                        var current = rows.First(r => r.Tenant == 1 && r.Disabled).Id;
                        await using (var plan = db.CreateCommand())
                        {
                            Bind(plan, 1);
                            var sql = $"SELECT COUNT(*) FROM {table} WHERE {Q("TenantId")}=@tenant AND {Q("Disabled")}={Literal(false)}";
                            plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                            await using var reader = await plan.ExecuteReaderAsync();
                            if (dbms == Dbms.SQLServer) { while (await reader.ReadAsync()) { } await reader.NextResultAsync(); }
                            var lines = new List<string>(); while (await reader.ReadAsync()) { lines.Add(reader.GetString(0)); }
                            check(lines.Count > 0, "Directory count plan is independent of skipped row retrieval: " + dbms + kind);
                            countPlans.Add(new { Phase = phase, ExpectedMatches = enabled.Length, Plan = string.Join('\n', lines) });
                        }
                        foreach (var limit in new[] { -1, 0, 500, enabled.Length - 1, enabled.Length, enabled.Length + 1 }.Distinct())
                        {
                            foreach (var manage in kind == "Users" ? new[] { true, false } : new[] { true })
                            {
                                var countAllowed = manage && (kind == "Users" || limit > 0);
                                var expectedFetch = countAllowed && enabled.Length <= limit;
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    var fetched = new List<int>(); var countMs = 0.0; var rowMs = 0.0; var queried = false;
                                    if (countAllowed)
                                    {
                                        await using var counter = db.CreateCommand(); Bind(counter, 1); counter.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {Q("TenantId")}=@tenant AND {Q("Disabled")}={Literal(false)}";
                                        var timer = Stopwatch.StartNew(); var actualCount = Convert.ToInt32(await counter.ExecuteScalarAsync()); timer.Stop(); countMs = timer.Elapsed.TotalMilliseconds;
                                        check(actualCount == enabled.Length, "Directory switch count: " + dbms + kind);
                                        if (actualCount <= limit)
                                        {
                                            queried = true;
                                            await using var command = db.CreateCommand(); Bind(command, 1); command.CommandText = $"SELECT {Q(idColumn)} FROM {table} WHERE {Q("TenantId")}=@tenant AND {Q("Disabled")}={Literal(false)} ORDER BY {Q("UpdatedTime")} DESC";
                                            timer.Restart(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) { fetched.Add(Convert.ToInt32(reader.GetValue(0))); } timer.Stop(); rowMs = timer.Elapsed.TotalMilliseconds;
                                        }
                                    }
                                    var fetchCount = fetched.Count;
                                    if (!fetched.Contains(current)) { fetched.Add(current); }
                                    check(queried == expectedFetch && fetched.SequenceEqual(expectedFetch ? enabled.Append(current) : [current]), "Directory switch boundary and current ID fallback: " + dbms + kind);
                                    if (sample > 0) { switches.Add(new { Phase = phase, Limit = limit, CanManage = manage, CountExecuted = countAllowed, RowQueryExecuted = queried, FetchedRows = fetchCount, Targets = fetched.Count, CountMs = countMs, RowMs = rowMs }); }
                                }
                            }
                        }
                        foreach (var workload in new[] { "insert", "update-time", "delete" })
                        {
                            var expectedCount = workload == "update-time" ? 100 : 50;
                            string Shift(int days) => dbms switch { Dbms.SQLServer => $"DATEADD(day,{days},{Q("UpdatedTime")})", Dbms.PostgreSQL => $"{Q("UpdatedTime")}+INTERVAL '{days} days'", _ => $"DATE_ADD({Q("UpdatedTime")},INTERVAL {days} DAY)" };
                            foreach (var mode in new[] { "rollback", "commit" })
                            {
                                for (var sample = 0; sample < 6; sample++)
                                {
                                    await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                                    try
                                    {
                                        var sql = workload switch { "insert" => $"INSERT INTO {table} ({string.Join(',', columns.Select(Q))}) VALUES " + string.Join(',', Enumerable.Range(count + 1, 50).Select(Make).Select(Record)), "update-time" => $"UPDATE {table} SET {Q("UpdatedTime")}={Shift(1)} WHERE {Q(idColumn)}<=100", _ => $"DELETE FROM {table} WHERE {Q(idColumn)}<=50" };
                                        var timer = Stopwatch.StartNew(); var affected = await Execute(sql); if (mode == "commit") { await tx.CommitAsync(); committed = true; } timer.Stop();
                                        check(affected == expectedCount, "Directory write count: " + dbms + kind);
                                        if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Rows = affected, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                                    }
                                    finally
                                    {
                                        if (!committed) { await tx.RollbackAsync(); }
                                        transaction = null;
                                        if (committed)
                                        {
                                            var restore = workload switch { "insert" => $"DELETE FROM {table} WHERE {Q(idColumn)}>{count}", "update-time" => $"UPDATE {table} SET {Q("UpdatedTime")}={Shift(-1)} WHERE {Q(idColumn)}<=100", _ => $"INSERT INTO {table} ({string.Join(',', columns.Select(Q))}) VALUES " + string.Join(',', rows.Take(50).Select(Record)) };
                                            check(await Execute(restore) == expectedCount, "Directory restored row count: " + dbms + kind);
                                        }
                                    }
                                }
                            }
                        }
                        await using var verify = db.CreateCommand(); verify.CommandText = $"SELECT {string.Join(',', columns.Select(Q))} FROM {table}";
                        await using var restored = await verify.ExecuteReaderAsync(); var same = true; var seen = 0;
                        while (await restored.ReadAsync())
                        {
                            var id = Convert.ToInt32(restored.GetValue(1)); seen++;
                            if (id < 1 || id > rows.Length) { same = false; continue; }
                            var expected = Values(rows[id - 1]).ToArray();
                            for (var n = 0; n < expected.Length; n++)
                            {
                                var actual = restored.IsDBNull(n) ? null : restored.GetValue(n);
                                same &= expected[n] switch { null => actual == null, bool b => actual != null && Convert.ToBoolean(actual) == b, int i => actual != null && Convert.ToInt32(actual) == i, _ => Equals(expected[n], actual) };
                            }
                        }
                        check(same && seen == rows.Length, "Directory all values restored: " + dbms + kind + phase);
                    }
                    foreach (var candidate in candidates) { await Execute(d.Drop(tableName, candidate.Name)); }
                    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"directory-workload-{dbms}-{kind}-{scenario}.json"), JsonSerializer.Serialize(new { Reports = reports, Switches = switches, CountPlans = countPlans, Writes = writes, IndexSizes = sizes }));
                }
            }
            finally { await Execute("DROP TABLE " + table); }
        }
    }
}
