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
        DbTransaction? transaction = null;
        async Task Execute(string sql)
        {
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
        }
        async Task<(long[] Rows, double Ms)> Read(string sql)
        {
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
            return (rows.ToArray(), watch.Elapsed.TotalMilliseconds);
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
        var baseline = new Dictionary<string, long[]>();
        await Execute($"CREATE TABLE {table} ({Q("TenantId")} int NOT NULL, {Q("RecordId")} bigint NOT NULL, {Q("OwnerId")} int NOT NULL, {Q("UpdatedTime")} {(dbms == Dbms.SQLServer ? "datetime2" : "timestamp")} NOT NULL, {Q("ParentId")} int NOT NULL, PRIMARY KEY ({Q("TenantId")},{Q("RecordId")}))");
        try
        {
            await Execute($"CREATE TABLE {permissions} ({Q("ReferenceId")} bigint NOT NULL, {Q("SubjectId")} int NOT NULL, PRIMARY KEY ({Q("ReferenceId")},{Q("SubjectId")}))");
            await Execute($"CREATE TABLE {members} ({Q("GroupId")} int NOT NULL, {Q("UserId")} int NOT NULL, PRIMARY KEY ({Q("GroupId")},{Q("UserId")}))");
            await Execute($"CREATE INDEX {Q("admin_member_standard_reverse")} ON {members} ({Q("UserId")},{Q("GroupId")})");
            await Execute($"INSERT INTO {members} VALUES " + string.Join(',', Enumerable.Range(1, 23).Select(i => $"({i},{i % 3 + 1})")));
            for (var batch = 0; batch < 8; batch++)
            {
                var values = Enumerable.Range(batch * 500 + 1, 500).Select(i => $"({i % 4 + 1},{i},{(i % 10 < 8 ? 1 : i % 19 + 2)},'2026-01-{i % 28 + 1:00}',{i % 31})");
                await Execute($"INSERT INTO {table} VALUES " + string.Join(',', values));
                await Execute($"INSERT INTO {permissions} VALUES " + string.Join(',', Enumerable.Range(batch * 500 + 1, 500).Select(i => $"({i},{i % 23 + 1})")));
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
                ("permission-empty", $"{Q("TenantId")}=1 AND {Q("RecordId")} IN (SELECT {Q("ReferenceId")} FROM {permissions} WHERE {Q("SubjectId")}=999)")
            };
            foreach (var phase in new[] { "standard", "candidate" })
            {
                if (phase == "candidate")
                {
                    await Execute($"CREATE INDEX {Q("admin_fixture_candidate")} ON {table} ({Q("TenantId")},{Q("OwnerId")},{Q("UpdatedTime")},{Q("RecordId")})");
                    await Execute($"CREATE INDEX {Q("admin_fixture_updated")} ON {table} ({Q("TenantId")},{Q("UpdatedTime")},{Q("RecordId")})");
                    await Execute($"CREATE INDEX {Q("admin_fixture_subject")} ON {permissions} ({Q("SubjectId")},{Q("ReferenceId")})");
                    await Execute($"CREATE INDEX {Q("admin_fixture_parent")} ON {table} ({Q("TenantId")},{Q("ParentId")},{Q("UpdatedTime")},{Q("RecordId")})");
                }
                foreach (var workload in new[] { "insert", "update", "delete" })
                {
                    await using var writeTransaction = await db.BeginTransactionAsync();
                    transaction = writeTransaction;
                    try
                    {
                        var sql = workload switch
                        {
                            "insert" => $"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(10001, 100).Select(i => $"(1,{i},1,'2026-02-01',0)")),
                            "update" => $"UPDATE {table} SET {Q("OwnerId")}={Q("OwnerId")}+1 WHERE {Q("TenantId")}=1",
                            _ => $"DELETE FROM {table} WHERE {Q("TenantId")}=1"
                        };
                        var watch = Stopwatch.StartNew();
                        await Execute(sql);
                        watch.Stop();
                        writes.Add(new { Phase = phase, Workload = workload, ClientMs = watch.Elapsed.TotalMilliseconds, RolledBack = true });
                    }
                    finally
                    {
                        await writeTransaction.RollbackAsync();
                        transaction = null;
                    }
                }
                foreach (var item in cases)
                {
                    var sql = $"SELECT {Q("RecordId")} FROM {table} WHERE {item.Where} ORDER BY {Q("UpdatedTime")},{Q("RecordId")}";
                    var warmup = await Read(sql);
                    check(item.Name.EndsWith("empty", StringComparison.Ordinal) ? warmup.Rows.Length == 0 : warmup.Rows.Length > 0, "Admin case has expected empty/nonempty result: " + item.Name);
                    check(warmup.Rows.All(id => id % 4 + 1 == (item.Name == "other-tenant" ? 4 : 1)), "Admin tenant isolation: " + item.Name);
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
                    reports.Add(new { Phase = phase, Case = item.Name, Rows = warmup.Rows.Length, MedianClientMs = times[2], SamplesMs = times, Plan = await Plan(sql) });
                }
            }
            var path = Path.Combine(AppContext.BaseDirectory, "admin-workload-" + dbms + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Dbms = dbms.ToString(), SyntheticOnly = true, SqlServerPlanIsEstimated = true, Reports = reports, Writes = writes }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Admin workload comparison saved: " + path);
        }
        finally
        {
            await Execute("DROP TABLE " + table);
            await Execute("DROP TABLE " + permissions);
            await Execute("DROP TABLE " + members);
        }
    }
}
