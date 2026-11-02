using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class PermissionWorkloadTests
{
    public static async Task Run(Dbms dbms, string connection, SqlDialect dialect, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        string Q(string value) => dialect.Quote(value);
        string T(string name) => dialect.Table("PermissionWorkload" + name);
        var boolean = dbms == Dbms.SQLServer ? "bit" : "boolean";
        var date = dbms == Dbms.SQLServer ? "datetime2" : "timestamp";
        var no = dbms == Dbms.PostgreSQL ? "FALSE" : "0";
        var yes = dbms == Dbms.PostgreSQL ? "TRUE" : "1";
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 60; cmd.Transaction = transaction; return await cmd.ExecuteNonQueryAsync();
        }
        var created = new List<string>();
        var sites = Enumerable.Range(1, 400).Select(id => (Tenant: id % 2 + 1, Id: id, Reference: (id % 2 + 1) * 1000 + id % 17)).ToArray();
        var departments = new Dictionary<int, bool> { [11] = false, [12] = true, [21] = false, [22] = true };
        var groups = new Dictionary<int, bool> { [101] = false, [102] = true, [103] = false, [201] = false, [202] = true, [203] = false };
        var users = new Dictionary<int, bool> { [1001] = false, [1002] = true, [1003] = false, [2001] = false };
        var members = new (int Group, int Dept, int User, bool Child)[] { (101, 11, 0, false), (101, 11, 0, true), (103, 0, 1001, false), (102, 11, 0, false), (201, 21, 0, false), (203, 0, 2001, false) };
        var permissions = new List<(int Reference, int Dept, int Group, int User, long Permission)>();
        const long High31 = PermissionStandardKeys.High31; const long High40 = PermissionStandardKeys.High40;
        const long UpdateDelta = (1L << 34) + 64;
        const long InsertValue = 4 | High31;
        for (var tenant = 1; tenant <= 2; tenant++)
        {
            for (var reference = 0; reference < 17; reference++)
            {
                var r = tenant * 1000 + reference; var dept = tenant * 10 + 1; var user = tenant * 1000 + 1; var group = tenant * 100 + 1;
                permissions.AddRange(new (int, int, int, int, long)[] { (r, dept, 0, 0, 1), (r, dept, 0, user, 1 | High40), (r, 0, group, 0, 2), (r, 0, group + 2, 0, 4 | High31), (r, 0, 0, user, 4), (r, 0, 0, -1, 8 | High40 | High31), (r, 0, group + 1, 0, 16), (r, dept + 1, 0, 0, 32 | High31) });
            }
        }
        async Task<long> Scalar(string sql)
        {
            await using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = transaction; return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        async Task<string> Snapshot()
        {
            var text = new List<string>();
            foreach (var sql in new[] { $"SELECT {Q("ReferenceId")},{Q("DeptId")},{Q("GroupId")},{Q("UserId")},{Q("PermissionType")} FROM {T("Permissions")} ORDER BY 1,2,3,4", $"SELECT {Q("SiteId")},{Q("InheritPermission")},{Q("TenantId")},0,0 FROM {T("Sites")} ORDER BY 1" })
            {
                await using var cmd = db.CreateCommand(); cmd.CommandText = sql; await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { text.Add(string.Join(',', Enumerable.Range(0, 5).Select(i => Convert.ToInt64(reader.GetValue(i))))); }
                text.Add("|");
            }
            return string.Join(';', text);
        }
        var reports = new List<object>();
        var writes = new List<object>();
        var sizes = new List<object>();
        void Bind(DbCommand command, int tenant, int dept, int user)
        {
            foreach (var (name, value) in new[] { ("@tenant", tenant), ("@dept", dept), ("@user", user) })
            {
                var p = command.CreateParameter(); p.ParameterName = name; p.DbType = System.Data.DbType.Int32; p.Value = value; command.Parameters.Add(p);
            }
        }
        try
        {
            var definitions = new[]
            {
                ("Sites", $"{Q("TenantId")} int NOT NULL,{Q("SiteId")} bigint NOT NULL,{Q("UpdatedTime")} {date} NOT NULL,{Q("InheritPermission")} bigint NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "Sites")}"),
                ("Permissions", $"{Q("ReferenceId")} bigint NOT NULL,{Q("DeptId")} int NOT NULL,{Q("GroupId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("PermissionType")} bigint NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "Permissions")}"),
                ("Depts", $"{Q("TenantId")} int NOT NULL,{Q("DeptId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "Depts")}"),
                ("Groups", $"{Q("TenantId")} int NOT NULL,{Q("GroupId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "Groups")}"),
                ("Users", $"{Q("TenantId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "Users")}"),
                ("GroupMembers", $"{Q("GroupId")} int NOT NULL,{Q("DeptId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("ChildGroup")} {boolean} NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "GroupMembers")}"),
                ("GroupChildren", $"{Q("GroupId")} int NOT NULL,{Q("ChildId")} int NOT NULL,{PermissionStandardKeys.PrimaryKeySql(dbms, dialect, "GroupChildren")}")
            };
            foreach (var (name, columns) in definitions) { await Execute($"CREATE TABLE {T(name)} ({columns})"); created.Add(name); }
            foreach (var table in created.ToArray())
            {
                foreach (var index in PermissionStandardKeys.IndexSql(dbms, dialect, table, "PermissionWorkload").Select(i => i.Sql).ToArray()) { await Execute(index); }
            }
            await PermissionStandardKeys.Verify(db, dbms, dialect, "PermissionWorkload", created, check);
            await Execute($"INSERT INTO {T("Sites")} VALUES " + string.Join(',', sites.Select(s => $"({s.Tenant},{s.Id},'2026-01-01',{s.Reference})")));
            await Execute($"INSERT INTO {T("Permissions")} VALUES " + string.Join(',', permissions.Select(p => $"({p.Reference},{p.Dept},{p.Group},{p.User},{p.Permission})")));
            await Execute($"INSERT INTO {T("Depts")} VALUES " + string.Join(',', departments.Select(p => $"({p.Key / 10},{p.Key},{(p.Value ? yes : no)})")));
            await Execute($"INSERT INTO {T("Groups")} VALUES " + string.Join(',', groups.Select(p => $"({p.Key / 100},{p.Key},{(p.Value ? yes : no)})")));
            await Execute($"INSERT INTO {T("Users")} VALUES " + string.Join(',', users.Select(p => $"({p.Key / 1000},{p.Key},{(p.Value ? yes : no)})")));
            await Execute($"INSERT INTO {T("GroupMembers")} VALUES " + string.Join(',', members.Select(m => $"({m.Group},{m.Dept},{m.User},{(m.Child ? yes : no)})")));
            await Execute($"INSERT INTO {T("GroupChildren")} VALUES (101,103),(201,203)");
            var baseline = await Snapshot();
            check(baseline.Contains((8L | High40 | High31).ToString(), StringComparison.Ordinal), "Wide permission values are stored and read as 64-bit on " + dbms);
            foreach (var reverse in new[] { false, true })
            {
                await using var childQuery = db.CreateCommand();
                childQuery.CommandText = reverse
                    ? $"SELECT {Q("GroupId")} FROM {T("GroupChildren")} WHERE {Q("ChildId")}=103"
                    : $"SELECT {Q("ChildId")} FROM {T("GroupChildren")} WHERE {Q("GroupId")}=101";
                check(Convert.ToInt32(await childQuery.ExecuteScalarAsync()) == (reverse ? 101 : 103), "Group child forward/reverse access preserves fixture on " + dbms);
            }
            var join = $" FROM {T("Sites")} s JOIN {T("Permissions")} p ON s.{Q("InheritPermission")}=p.{Q("ReferenceId")}";
            var tails = new[]
            {
                $" JOIN {T("Depts")} d ON d.{Q("DeptId")}=p.{Q("DeptId")} WHERE d.{Q("DeptId")}=@dept AND d.{Q("Disabled")}={no}",
                $" JOIN {T("Groups")} g ON g.{Q("GroupId")}=p.{Q("GroupId")} JOIN {T("GroupMembers")} m ON m.{Q("GroupId")}=g.{Q("GroupId")} JOIN {T("Depts")} d ON d.{Q("DeptId")}=m.{Q("DeptId")} WHERE g.{Q("Disabled")}={no} AND d.{Q("Disabled")}={no} AND d.{Q("DeptId")}=@dept",
                $" JOIN {T("Groups")} g ON g.{Q("GroupId")}=p.{Q("GroupId")} JOIN {T("GroupMembers")} m ON m.{Q("GroupId")}=g.{Q("GroupId")} JOIN {T("Users")} u ON u.{Q("UserId")}=m.{Q("UserId")} WHERE g.{Q("Disabled")}={no} AND u.{Q("Disabled")}={no} AND u.{Q("UserId")}=@user",
                $" WHERE p.{Q("UserId")}>0 AND p.{Q("UserId")}=@user", $" WHERE p.{Q("UserId")}=-1"
            };
            var union = string.Join(" UNION ALL ", tails.Select(t => $"SELECT DISTINCT s.{Q("SiteId")} AS {Q("ReferenceId")},p.{Q("PermissionType")}" + join + t + $" AND s.{Q("TenantId")}=@tenant"));
            var sql = $"SELECT {Q("ReferenceId")},{Q("PermissionType")} FROM ({union}) combined ORDER BY {Q("ReferenceId")},{Q("PermissionType")}";
            var candidates = new[]
            {
                (Phase:"sites",Table:"Sites",Name:"permission_sites_candidate",Keys:$"{Q("TenantId")},{Q("InheritPermission")},{Q("SiteId")}"),
                (Phase:"dept",Table:"Permissions",Name:"permission_dept_candidate",Keys:$"{Q("DeptId")},{Q("ReferenceId")},{Q("PermissionType")}"),
                (Phase:"group",Table:"Permissions",Name:"permission_group_candidate",Keys:$"{Q("GroupId")},{Q("ReferenceId")},{Q("PermissionType")}"),
                (Phase:"user",Table:"Permissions",Name:"permission_user_candidate",Keys:$"{Q("UserId")},{Q("ReferenceId")},{Q("PermissionType")}")
            };
            var previous = "standard";
            foreach (var phase in new[] { "standard", "sites", "dept", "group", "user", "combined" })
            {
                foreach (var candidate in candidates.Where(c => previous == "combined" || c.Phase == previous))
                {
                    await Execute(dialect.Drop("PermissionWorkload" + candidate.Table, candidate.Name));
                }
                var active = candidates.Where(c => phase == "combined" || c.Phase == phase).ToArray();
                foreach (var candidate in active) { await Execute($"CREATE INDEX {Q(candidate.Name)} ON {T(candidate.Table)} ({candidate.Keys})"); }
                previous = phase;
                foreach (var name in created) { await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {T(name)} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + T(name), _ => "ANALYZE TABLE " + T(name) }); }
                foreach (var candidate in active)
                {
                    await using var cmd = db.CreateCommand();
                    cmd.CommandText = dbms switch
                    {
                        Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{T(candidate.Table)}') AND i.name=@name",
                        Dbms.PostgreSQL => $"SELECT pg_relation_size('{dialect.Table(candidate.Name)}')",
                        _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name=@table AND index_name=@name AND stat_name='size'"
                    };
                    if (dbms != Dbms.PostgreSQL) { var p = cmd.CreateParameter(); p.ParameterName = "@name"; p.Value = candidate.Name; cmd.Parameters.Add(p); }
                    if (dbms == Dbms.MySQL) { var p = cmd.CreateParameter(); p.ParameterName = "@table"; p.Value = "PermissionWorkload" + candidate.Table; cmd.Parameters.Add(p); }
                    var bytes = Convert.ToInt64(await cmd.ExecuteScalarAsync()); check(bytes > 0, "Permission candidate capacity reported on " + dbms);
                    sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                }
                foreach (var workload in new[] { "insert-grants", "update-grants", "delete-grants", "update-inheritance" })
                {
                    foreach (var mode in new[] { "rollback", "commit" })
                    {
                        for (var sample = 0; sample < 6; sample++)
                        {
                            await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                            try
                            {
                                var write = workload switch
                                {
                                    "insert-grants" => $"INSERT INTO {T("Permissions")} VALUES " + string.Join(',', Enumerable.Range(9001, 100).Select(id => $"({id},0,0,1001,{InsertValue})")),
                                    "update-grants" => $"UPDATE {T("Permissions")} SET {Q("PermissionType")}={Q("PermissionType")}+{UpdateDelta}",
                                    "delete-grants" => $"DELETE FROM {T("Permissions")} WHERE {Q("ReferenceId")}=1000",
                                    _ => $"UPDATE {T("Sites")} SET {Q("InheritPermission")}={Q("InheritPermission")}+10000 WHERE {Q("TenantId")}=1"
                                };
                                var timer = Stopwatch.StartNew();
                                var affected = await Execute(write);
                                if (mode == "commit") { await tx.CommitAsync(); committed = true; }
                                timer.Stop();
                                var expectedCount = workload switch
                                {
                                    "insert-grants" => 100,
                                    "update-grants" => permissions.Count,
                                    "delete-grants" => 8,
                                    _ => sites.Count(s => s.Tenant == 1)
                                };
                                check(affected == expectedCount, "Permission write row count on " + dbms);
                                // 32bitを超える値が更新後も保持されること（時間には含めない）。
                                if (committed) { transaction = null; }
                                var wide = workload switch
                                {
                                    "insert-grants" => await Scalar($"SELECT COUNT(*) FROM {T("Permissions")} WHERE {Q("ReferenceId")}>=9001 AND {Q("PermissionType")}={InsertValue}"),
                                    "update-grants" => await Scalar($"SELECT SUM({Q("PermissionType")}) FROM {T("Permissions")}"),
                                    _ => -1L
                                };
                                check(workload is "delete-grants" or "update-inheritance" || wide == (workload == "insert-grants" ? 100 : permissions.Sum(p => p.Permission) + UpdateDelta * permissions.Count), "Permission values above 32 bits survive " + workload + " on " + dbms);
                                if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                            }
                            finally
                            {
                                if (!committed) { await tx.RollbackAsync(); }
                                transaction = null;
                                if (committed)
                                {
                                    var restore = workload switch
                                    {
                                        "insert-grants" => $"DELETE FROM {T("Permissions")} WHERE {Q("ReferenceId")}>=9001",
                                        "update-grants" => $"UPDATE {T("Permissions")} SET {Q("PermissionType")}={Q("PermissionType")}-{UpdateDelta}",
                                        "delete-grants" => $"INSERT INTO {T("Permissions")} VALUES " + string.Join(',', permissions.Where(p => p.Reference == 1000).Select(p => $"({p.Reference},{p.Dept},{p.Group},{p.User},{p.Permission})")),
                                        _ => $"UPDATE {T("Sites")} SET {Q("InheritPermission")}={Q("InheritPermission")}-10000 WHERE {Q("TenantId")}=1"
                                    };
                                    await Execute(restore);
                                }
                                var restored = await Snapshot();
                                check(restored == baseline, "Permissions and Sites are restored exactly after " + workload + " on " + dbms);
                            }
                        }
                    }
                }
                foreach (var context in new (int Tenant, int Dept, int User)[] { (1, 11, 1001), (1, 0, 1003), (2, 21, 2001), (1, 12, 1002) })
                {
                    var expected = new List<(long Id, long Permission)>();
                    for (var branch = 0; branch < 5; branch++)
                    {
                        bool Matches((int Reference, int Dept, int Group, int User, long Permission) p) => branch switch
                        {
                            0 => p.Dept == context.Dept && !departments.GetValueOrDefault(context.Dept, true),
                            1 => !groups.GetValueOrDefault(p.Group, true) && !departments.GetValueOrDefault(context.Dept, true) && members.Any(m => m.Group == p.Group && m.Dept == context.Dept),
                            2 => !groups.GetValueOrDefault(p.Group, true) && !users.GetValueOrDefault(context.User, true) && members.Any(m => m.Group == p.Group && m.User == context.User),
                            3 => p.User > 0 && p.User == context.User,
                            _ => p.User == -1
                        };
                        expected.AddRange(sites.Where(s => s.Tenant == context.Tenant).SelectMany(s => permissions.Where(p => p.Reference == s.Reference && Matches(p)).Select(p => ((long)s.Id, p.Permission))).Distinct());
                    }
                    var ordered = expected.OrderBy(p => p.Id).ThenBy(p => p.Permission).ToArray(); var times = new List<double>();
                    for (var sample = 0; sample < 6; sample++)
                    {
                        await using var cmd = db.CreateCommand(); cmd.CommandText = sql; Bind(cmd, context.Tenant, context.Dept, context.User);
                        var timer = Stopwatch.StartNew(); await using var reader = await cmd.ExecuteReaderAsync(); var actual = new List<(long, long)>();
                        while (await reader.ReadAsync()) { actual.Add((Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)))); }
                        timer.Stop(); check(actual.SequenceEqual(ordered), "Five permission branches preserve tenant, disabled and duplicate semantics: " + dbms + phase);
                        if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                    }
                    await using var plan = db.CreateCommand(); Bind(plan, context.Tenant, context.Dept, context.User);
                    plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + sql, _ => "EXPLAIN ANALYZE " + sql };
                    await using var plans = await plan.ExecuteReaderAsync(); if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                    var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                    check(lines.Count > 0, "Permission execution plan captured on " + dbms + phase);
                    times.Sort(); reports.Add(new { Phase = phase, context.Tenant, context.Dept, context.User, Rows = ordered.Length, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "permission-workload-" + dbms + ".json"), JsonSerializer.Serialize(new { Reports = reports, Writes = writes, IndexSizes = sizes }));
        }
        finally { foreach (var name in created.AsEnumerable().Reverse()) { await Execute("DROP TABLE " + T(name)); } }
    }
}
