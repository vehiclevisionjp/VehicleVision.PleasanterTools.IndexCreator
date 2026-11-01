using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class AdminWorkloadTests
{
    public static async Task Run(Dbms dbms, string connection, SqlDialect dialect, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch
        {
            Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection),
            Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection),
            _ => new MySqlConnector.MySqlConnection(connection)
        };
        await db.OpenAsync();
        string Q(string name) => dialect.Quote(name);
        var table = dialect.Table("AdminWorkloadFixture");
        var permissions = dialect.Table("AdminPermissionFixture");
        var members = dialect.Table("AdminMemberFixture");
        var logs = dialect.Table("AdminLogFixture");
        DbTransaction? transaction = null;
        var ioMessages = new List<string>();
        if (db is Microsoft.Data.SqlClient.SqlConnection sqlConnection)
        {
            sqlConnection.InfoMessage += (_, message) => ioMessages.Add(message.Message);
        }
        async Task Execute(string sql)
        {
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
        }
        async Task<(long[] Rows, double Ms, string[] Io)> Read(string sql)
        {
            ioMessages.Clear();
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 30;
            var watch = Stopwatch.StartNew();
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<long>();
            while (await reader.ReadAsync())
            {
                rows.Add(Convert.ToInt64(reader.GetValue(0)));
            }
            while (await reader.NextResultAsync()) { }
            return (rows.ToArray(), watch.Elapsed.TotalMilliseconds, ioMessages.ToArray());
        }
        async Task<string> Plan(string sql)
        {
            if (dbms == Dbms.SQLServer)
            {
                await Execute("SET SHOWPLAN_XML ON");
            }
            try
            {
                await using var command = db.CreateCommand();
                command.CommandText = dbms switch
                {
                    Dbms.SQLServer => sql,
                    Dbms.PostgreSQL => "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql,
                    _ => "EXPLAIN ANALYZE " + sql
                };
                command.CommandTimeout = 30;
                await using var reader = await command.ExecuteReaderAsync();
                var lines = new List<string>();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }
                return string.Join('\n', lines);
            }
            finally
            {
                if (dbms == Dbms.SQLServer)
                {
                    await Execute("SET SHOWPLAN_XML OFF");
                }
            }
        }
        var reports = new List<object>();
        var writes = new List<object>();
        var sizes = new List<object>();
        var indexSizes = new List<object>();
        var baseline = new Dictionary<string, long[]>();
        await Execute($"CREATE TABLE {table} ({Q("TenantId")} int NOT NULL, {Q("RecordId")} bigint NOT NULL, {Q("OwnerId")} int NOT NULL, {Q("UpdatedTime")} {(dbms == Dbms.SQLServer ? "datetime2" : "timestamp")} NOT NULL, {Q("ParentId")} int NOT NULL, PRIMARY KEY ({Q("TenantId")},{Q("RecordId")}))");
        try
        {
            await Execute($"CREATE TABLE {permissions} ({Q("ReferenceId")} bigint NOT NULL, {Q("SubjectId")} int NOT NULL, PRIMARY KEY ({Q("ReferenceId")},{Q("SubjectId")}))");
            await Execute($"CREATE TABLE {members} ({Q("GroupId")} int NOT NULL, {Q("UserId")} int NOT NULL, PRIMARY KEY ({Q("GroupId")},{Q("UserId")}))");
            await Execute($"CREATE TABLE {logs} ({Q("CreatedTime")} {(dbms == Dbms.SQLServer ? "datetime2" : "timestamp")} NOT NULL, {Q("RecordId")} bigint NOT NULL, {Q("Kind")} int NOT NULL)");
            await Execute($"CREATE INDEX {Q("admin_log_standard_id")} ON {logs} ({Q("RecordId")})");
            await Execute($"CREATE UNIQUE INDEX {Q("admin_log_standard_time")} ON {logs} ({Q("CreatedTime")} DESC,{Q("RecordId")})");
            await Execute($"CREATE INDEX {Q("admin_member_standard_reverse")} ON {members} ({Q("UserId")},{Q("GroupId")})");
            await Execute($"INSERT INTO {members} VALUES " + string.Join(',', Enumerable.Range(1, 23).Select(i => $"({i},{i % 3 + 1})")));
            for (var batch = 0; batch < 8; batch++)
            {
                var values = Enumerable.Range(batch * 500 + 1, 500).Select(i => $"({i % 4 + 1},{i},{(i % 10 < 8 ? 1 : i % 19 + 2)},'2026-01-{i % 28 + 1:00}',{i % 31})");
                await Execute($"INSERT INTO {table} VALUES " + string.Join(',', values));
                await Execute($"INSERT INTO {permissions} VALUES " + string.Join(',', Enumerable.Range(batch * 500 + 1, 500).Select(i => $"({i},{i % 23 + 1})")));
                await Execute($"INSERT INTO {logs} VALUES " + string.Join(',', Enumerable.Range(batch * 500 + 1, 500).Select(i => $"('2026-01-{i % 28 + 1:00}',{i},{(i % 20 == 0 ? 7 : 0)})")));
            }
            var cases = new (string Name, string Where)[]
            {
                ("tenant", $"{Q("TenantId")}=1"),
                ("frequent-owner", $"{Q("TenantId")}=1 AND {Q("OwnerId")}=1"),
                ("rare-owner", $"{Q("TenantId")}=1 AND {Q("OwnerId")}=19"),
                ("empty", $"{Q("TenantId")}=1 AND {Q("OwnerId")}=999"),
                ("other-tenant", $"{Q("TenantId")}=4 AND {Q("OwnerId")}=1"),
                ("updated-range", $"{Q("TenantId")}=1 AND {Q("UpdatedTime")}>='2026-01-20'"),
                ("permission-subject", $"{Q("TenantId")}=1 AND {Q("RecordId")} IN (SELECT {Q("ReferenceId")} FROM {permissions} WHERE {Q("SubjectId")}=7)"),
                ("parent", $"{Q("TenantId")}=1 AND {Q("ParentId")}=7"),
                ("membership", $"{Q("TenantId")}=1 AND {Q("RecordId")} IN (SELECT {Q("ReferenceId")} FROM {permissions} p INNER JOIN {members} m ON p.{Q("SubjectId")}=m.{Q("GroupId")} WHERE m.{Q("UserId")}=2)"),
                ("permission-empty", $"{Q("TenantId")}=1 AND {Q("RecordId")} IN (SELECT {Q("ReferenceId")} FROM {permissions} WHERE {Q("SubjectId")}=999)"),
                ("log-period", $"{Q("CreatedTime")}>='2026-01-20'"),
                ("log-common-kind", $"{Q("Kind")}=0 AND {Q("CreatedTime")}>='2026-01-20'"),
                ("log-rare-kind", $"{Q("Kind")}=7 AND {Q("CreatedTime")}>='2026-01-20'"),
                ("log-empty", $"{Q("Kind")}=999 AND {Q("CreatedTime")}>='2026-01-20'")
            };
            var definitions = new[]
            {
                (Phase: "owner", Table: "AdminWorkloadFixture", Name: "admin_fixture_candidate", Keys: $"{Q("TenantId")},{Q("OwnerId")},{Q("UpdatedTime")},{Q("RecordId")}"),
                (Phase: "updated", Table: "AdminWorkloadFixture", Name: "admin_fixture_updated", Keys: $"{Q("TenantId")},{Q("UpdatedTime")},{Q("RecordId")}"),
                (Phase: "subject", Table: "AdminPermissionFixture", Name: "admin_fixture_subject", Keys: $"{Q("SubjectId")},{Q("ReferenceId")}"),
                (Phase: "parent", Table: "AdminWorkloadFixture", Name: "admin_fixture_parent", Keys: $"{Q("TenantId")},{Q("ParentId")},{Q("UpdatedTime")},{Q("RecordId")}"),
                (Phase: "log", Table: "AdminLogFixture", Name: "admin_log_candidate", Keys: $"{Q("Kind")},{Q("CreatedTime")} DESC,{Q("RecordId")}")
            };
            var previousPhase = "standard";
            foreach (var phase in new[] { "standard", "owner", "updated", "subject", "parent", "log", "candidate" })
            {
                foreach (var definition in definitions.Where(d => previousPhase == "candidate" || d.Phase == previousPhase))
                {
                    await Execute(dialect.Drop(definition.Table, definition.Name));
                }
                var active = definitions.Where(d => phase == "candidate" || d.Phase == phase).ToArray();
                foreach (var definition in active)
                {
                    await Execute($"CREATE INDEX {Q(definition.Name)} ON {dialect.Table(definition.Table)} ({definition.Keys})");
                }
                previousPhase = phase;
                foreach (var fixture in new[] { table, permissions, members, logs })
                {
                    await Execute(dbms switch
                    {
                        Dbms.SQLServer => $"UPDATE STATISTICS {fixture} WITH FULLSCAN",
                        Dbms.PostgreSQL => "ANALYZE " + fixture,
                        _ => "ANALYZE TABLE " + fixture
                    });
                }
                foreach (var definition in active)
                {
                    await using var sizeCommand = db.CreateCommand();
                    sizeCommand.CommandText = dbms switch
                    {
                        Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{dialect.Table(definition.Table)}') AND i.name=@name",
                        Dbms.PostgreSQL => $"SELECT pg_relation_size('{dialect.Table(definition.Name)}')",
                        _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name=@table AND index_name=@name AND stat_name='size'"
                    };
                    if (dbms != Dbms.PostgreSQL)
                    {
                        var name = sizeCommand.CreateParameter(); name.ParameterName = "@name"; name.Value = definition.Name; sizeCommand.Parameters.Add(name);
                    }
                    if (dbms == Dbms.MySQL)
                    {
                        var target = sizeCommand.CreateParameter(); target.ParameterName = "@table"; target.Value = definition.Table; sizeCommand.Parameters.Add(target);
                    }
                    var bytes = Convert.ToInt64(await sizeCommand.ExecuteScalarAsync());
                    check(bytes > 0, "Individual candidate capacity is available: " + definition.Phase);
                    indexSizes.Add(new { Phase = phase, Index = definition.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                }
                await using (var sizeCommand = db.CreateCommand())
                {
                    sizeCommand.CommandText = dbms switch
                    {
                        Dbms.SQLServer => $"SELECT SUM(used_page_count)*8192 FROM sys.dm_db_partition_stats WHERE object_id IN (OBJECT_ID(N'{table}'),OBJECT_ID(N'{permissions}'),OBJECT_ID(N'{members}'),OBJECT_ID(N'{logs}'))",
                        Dbms.PostgreSQL => $"SELECT pg_total_relation_size('{table}')+pg_total_relation_size('{permissions}')+pg_total_relation_size('{members}')+pg_total_relation_size('{logs}')",
                        _ => "SELECT SUM(DATA_LENGTH+INDEX_LENGTH) FROM information_schema.tables WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('AdminWorkloadFixture','AdminPermissionFixture','AdminMemberFixture','AdminLogFixture')"
                    };
                    var bytes = Convert.ToInt64(await sizeCommand.ExecuteScalarAsync());
                    check(bytes > 0, "Admin fixture storage is reported on " + dbms);
                    sizes.Add(new { Phase = phase, TotalTableAndIndexBytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                }
                foreach (var workload in new[] { "insert", "update", "delete", "log-insert" })
                {
                    foreach (var mode in new[] { "rollback", "commit" })
                    {
                        for (var sample = 0; sample < 6; sample++)
                        {
                            await using var writeTransaction = await db.BeginTransactionAsync();
                            transaction = writeTransaction;
                            var committed = false;
                            try
                            {
                                var sql = workload switch
                                {
                                    "log-insert" => $"INSERT INTO {logs} VALUES " + string.Join(',', Enumerable.Range(10001, 100).Select(i => $"('2026-02-01',{i},0)")),
                                    "insert" => $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(10001, 100).Select(i => $"(1,{i},1,'2026-02-01',0)")),
                                    "update" => $"UPDATE {table} SET {Q("OwnerId")}={Q("OwnerId")}+1 WHERE {Q("TenantId")}=1",
                                    _ => $"DELETE FROM {table} WHERE {Q("TenantId")}=1"
                                };
                                var watch = Stopwatch.StartNew();
                                await Execute(sql);
                                if (mode == "commit")
                                {
                                    await writeTransaction.CommitAsync();
                                    committed = true;
                                }
                                watch.Stop();
                                if (sample > 0)
                                {
                                    writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Sample = sample, ClientMs = watch.Elapsed.TotalMilliseconds, RolledBack = !committed });
                                }
                            }
                            finally
                            {
                                if (!committed)
                                {
                                    await writeTransaction.RollbackAsync();
                                }
                                transaction = null;
                                if (committed)
                                {
                                    if (workload is "insert" or "log-insert")
                                    {
                                        await Execute($"DELETE FROM {(workload == "insert" ? table : logs)} WHERE {Q("RecordId")}>=10001");
                                    }
                                    else if (workload == "update")
                                    {
                                        await Execute($"UPDATE {table} SET {Q("OwnerId")}={Q("OwnerId")}-1 WHERE {Q("TenantId")}=1");
                                    }
                                    else
                                    {
                                        foreach (var batch in Enumerable.Range(1, 4000).Where(i => i % 4 == 0).Chunk(500))
                                        {
                                            await Execute($"INSERT INTO {table} VALUES " + string.Join(',', batch.Select(i => $"({i % 4 + 1},{i},{(i % 10 < 8 ? 1 : i % 19 + 2)},'2026-01-{i % 28 + 1:00}',{i % 31})")));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                if (dbms == Dbms.SQLServer)
                {
                    await Execute("SET STATISTICS IO ON");
                }
                foreach (var item in cases)
                {
                    var isLog = item.Name.StartsWith("log-", StringComparison.Ordinal);
                    var sql = isLog
                        ? $"SELECT {Q("RecordId")} FROM {logs} WHERE {item.Where} ORDER BY {Q("CreatedTime")} DESC,{Q("RecordId")}"
                        : $"SELECT {Q("RecordId")} FROM {table} WHERE {item.Where} ORDER BY {Q("UpdatedTime")},{Q("RecordId")}";
                    var warmup = await Read(sql);
                    check(item.Name.EndsWith("empty", StringComparison.Ordinal) ? warmup.Rows.Length == 0 : warmup.Rows.Length > 0, "Admin case has expected empty/nonempty result: " + item.Name);
                    check(isLog || warmup.Rows.All(id => id % 4 + 1 == (item.Name == "other-tenant" ? 4 : 1)), "Admin tenant isolation: " + item.Name);
                    if (isLog)
                    {
                        var expected = Enumerable.Range(1, 4000).Where(id => id % 28 + 1 >= 20
                            && (item.Name == "log-period" || item.Name == "log-common-kind" && id % 20 != 0 || item.Name == "log-rare-kind" && id % 20 == 0))
                            .OrderByDescending(id => id % 28 + 1).ThenBy(id => id).Select(id => (long)id);
                        check(warmup.Rows.SequenceEqual(expected), "Log results match independent synthetic oracle: " + item.Name);
                    }
                    if (phase == "standard")
                    {
                        baseline[item.Name] = warmup.Rows;
                    }
                    check(warmup.Rows.SequenceEqual(baseline[item.Name]), "Admin fixture preserves ordered results: " + item.Name + phase);
                    var times = new List<double>();
                    for (var repeat = 0; repeat < 5; repeat++)
                    {
                        var observed = await Read(sql);
                        check(observed.Rows.SequenceEqual(baseline[item.Name]), "Admin repeated result: " + item.Name + phase);
                        times.Add(observed.Ms);
                    }
                    times.Sort();
                    check(dbms != Dbms.SQLServer || warmup.Io.Length > 0, "SQL Server runtime IO is captured");
                    reports.Add(new { Phase = phase, Case = item.Name, Rows = warmup.Rows.Length, MedianClientMs = times[2], SamplesMs = times, SqlServerRuntimeIo = warmup.Io, Plan = await Plan(sql) });
                }
                if (dbms == Dbms.SQLServer)
                {
                    await Execute("SET STATISTICS IO OFF");
                }
            }
            var path = Path.Combine(AppContext.BaseDirectory, "admin-workload-" + dbms + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Dbms = dbms.ToString(), SyntheticOnly = true, SqlServerPlanIsEstimated = true, Reports = reports, Writes = writes, Sizes = sizes, IndexSizes = indexSizes }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Admin workload comparison saved: " + path);
        }
        finally
        {
            await Execute("DROP TABLE " + table);
            await Execute("DROP TABLE " + permissions);
            await Execute("DROP TABLE " + members);
            await Execute("DROP TABLE " + logs);
        }
    }
}
