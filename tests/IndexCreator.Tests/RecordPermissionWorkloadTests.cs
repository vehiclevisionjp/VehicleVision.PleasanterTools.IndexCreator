using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class RecordPermissionWorkloadTests
{
    private sealed record Item(int Id, int Site);
    private sealed record Grant(int Reference, int Dept, int Group, int User, long Mask);

    public static async Task Run(Dbms dbms, string connection, SqlDialect d, Action<bool, string> check)
    {
        await using DbConnection db = dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(connection), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(connection), _ => new MySqlConnector.MySqlConnection(connection) };
        await db.OpenAsync();
        string Q(string s) => d.Quote(s);
        string T(string s) => d.Table("RecordPermission" + s);
        var boolean = dbms == Dbms.SQLServer ? "bit" : "boolean";
        var no = dbms == Dbms.PostgreSQL ? "FALSE" : "0";
        var date = dbms == Dbms.SQLServer ? "datetime" : dbms == Dbms.MySQL ? "datetime(3)" : "timestamp";
        string MasterPk(string id) => dbms == Dbms.MySQL ? Q(id) : Q("TenantId") + "," + Q(id);
        DbTransaction? transaction = null;
        async Task<int> Execute(string sql)
        {
            await using var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60; command.Transaction = transaction;
            return await command.ExecuteNonQueryAsync();
        }
        var sites = new[] { (Id: 10, Tenant: 1, Reference: 1000), (Id: 20, Tenant: 1, Reference: 1100), (Id: 30, Tenant: 2, Reference: 2000), (Id: 40, Tenant: 2, Reference: 2100) };
        var departments = new Dictionary<int, bool> { [11] = false, [12] = true, [21] = false, [22] = true };
        var groups = new Dictionary<int, bool> { [101] = false, [102] = true, [103] = false, [104] = false, [201] = false, [202] = true, [203] = false, [204] = false };
        var users = new Dictionary<int, bool> { [1001] = false, [1002] = true, [1003] = false, [2001] = false, [2002] = true, [2003] = false };
        var members = new List<(int Group, int Dept, int User, bool Child)>();
        foreach (var tenant in new[] { 1, 2 })
        {
            members.AddRange(new[] { (tenant * 100 + 1, tenant * 10 + 1, 0, false), (tenant * 100 + 1, tenant * 10 + 1, 0, true), (tenant * 100 + 2, tenant * 10 + 1, 0, false), (tenant * 100 + 3, 0, tenant * 1000 + 1, false), (tenant * 100 + 4, 0, tenant * 1000 + 2, false) });
        }
        var tables = new List<string>();
        void Bind(DbCommand command, int tenant, int dept, int user, long mask)
        {
            foreach (var (name, value) in new[] { ("@tenant", tenant), ("@dept", dept), ("@user", user) })
            {
                var p = command.CreateParameter(); p.ParameterName = name; p.DbType = System.Data.DbType.Int32; p.Value = value; command.Parameters.Add(p);
            }
            var maskParameter = command.CreateParameter(); maskParameter.ParameterName = "@mask"; maskParameter.DbType = System.Data.DbType.Int64; maskParameter.Value = mask; command.Parameters.Add(maskParameter);
        }
        string GrantValues(Grant p) => $"({p.Reference},{p.Dept},{p.Group},{p.User},{p.Mask})";
        string ItemValues(Item item) => $"({item.Id},{item.Site})";
        try
        {
            var definitions = new[]
            {
                ("Sites",$"{Q("TenantId")} int NOT NULL,{Q("SiteId")} bigint NOT NULL,{Q("UpdatedTime")} {date} NOT NULL,{Q("InheritPermission")} bigint NOT NULL,PRIMARY KEY({Q("TenantId")},{Q("SiteId")},{Q("UpdatedTime")}{(dbms == Dbms.PostgreSQL ? "" : " DESC")} )"),
                ("Items",$"{Q("ReferenceId")} bigint NOT NULL PRIMARY KEY,{Q("SiteId")} bigint NOT NULL"),
                ("Permissions",$"{Q("ReferenceId")} bigint NOT NULL,{Q("DeptId")} int NOT NULL,{Q("GroupId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("PermissionType")} bigint NOT NULL,PRIMARY KEY({Q("ReferenceId")},{Q("DeptId")},{Q("GroupId")},{Q("UserId")})"),
                ("Depts",$"{Q("TenantId")} int NOT NULL,{Q("DeptId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,PRIMARY KEY({MasterPk("DeptId")})"),
                ("Groups",$"{Q("TenantId")} int NOT NULL,{Q("GroupId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,PRIMARY KEY({MasterPk("GroupId")})"),
                ("Users",$"{Q("TenantId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("Disabled")} {boolean} NOT NULL,PRIMARY KEY({MasterPk("UserId")})"),
                ("GroupMembers",$"{Q("GroupId")} int NOT NULL,{Q("DeptId")} int NOT NULL,{Q("UserId")} int NOT NULL,{Q("ChildGroup")} {boolean} NOT NULL,PRIMARY KEY({Q("GroupId")},{Q("DeptId")},{Q("UserId")},{Q("ChildGroup")})")
            };
            foreach (var (name, columns) in definitions) { await Execute($"CREATE TABLE {T(name)} ({columns})"); tables.Add(name); }
            var standard = new[] { ("Sites", "SiteId"), ("Items", "SiteId,ReferenceId"), ("Depts", "Disabled"), ("Groups", "Disabled"), ("Users", "UserId,TenantId"), ("GroupMembers", "DeptId,GroupId"), ("GroupMembers", "UserId,GroupId"), ("GroupMembers", "GroupId") };
            for (var i = 0; i < standard.Length; i++) { var (name, keys) = standard[i]; await Execute($"CREATE {(name == "Sites" ? "UNIQUE " : "")}INDEX {Q("record_standard_" + i)} ON {T(name)} ({string.Join(',', keys.Split(',').Select(Q))})"); }
            if (dbms == Dbms.MySQL)
            {
                foreach (var kind in new[] { "Depts", "Groups", "Users" })
                {
                    var id = kind[..^1] + "Id";
                    await Execute($"CREATE INDEX {Q("record_mysql_" + kind)} ON {T(kind)} ({Q("TenantId")},{Q(id)})");
                }
            }
            var baseline = await PermissionBaseline.Verify(db, dbms, "RecordPermission", tables, check);
            await Execute($"INSERT INTO {T("Sites")} VALUES " + string.Join(',', sites.Select(s => $"({s.Tenant},{s.Id},'2026-01-01',{s.Reference})")));
            foreach (var (name, data, divisor) in new[] { ("Depts", departments, 10), ("Groups", groups, 100), ("Users", users, 1000) })
            {
                await Execute($"INSERT INTO {T(name)} VALUES " + string.Join(',', data.Select(p => $"({p.Key / divisor},{p.Key},{(dbms == Dbms.PostgreSQL ? p.Value ? "TRUE" : "FALSE" : p.Value ? "1" : "0")})")));
            }
            await Execute($"INSERT INTO {T("GroupMembers")} VALUES " + string.Join(',', members.Select(m => $"({m.Group},{m.Dept},{m.User},{(dbms == Dbms.PostgreSQL ? m.Child ? "TRUE" : "FALSE" : m.Child ? "1" : "0")})")));
            var branchTails = new[]
            {
                (Join:$" JOIN {T("Depts")} a ON a.{Q("DeptId")}=p.{Q("DeptId")}",Where:$"a.{Q("DeptId")}=@dept AND a.{Q("Disabled")}={no}"),
                (Join:$" JOIN {T("Groups")} g ON g.{Q("GroupId")}=p.{Q("GroupId")} JOIN {T("GroupMembers")} m ON m.{Q("GroupId")}=g.{Q("GroupId")} JOIN {T("Depts")} a ON a.{Q("DeptId")}=m.{Q("DeptId")}",Where:$"g.{Q("Disabled")}={no} AND a.{Q("Disabled")}={no} AND a.{Q("DeptId")}=@dept"),
                (Join:$" JOIN {T("Groups")} g ON g.{Q("GroupId")}=p.{Q("GroupId")} JOIN {T("GroupMembers")} m ON m.{Q("GroupId")}=g.{Q("GroupId")} JOIN {T("Users")} u ON u.{Q("UserId")}=m.{Q("UserId")}",Where:$"g.{Q("Disabled")}={no} AND u.{Q("Disabled")}={no} AND u.{Q("UserId")}=@user"),
                (Join:"",Where:$"p.{Q("UserId")}>0 AND p.{Q("UserId")}=@user"),
                (Join:"",Where:$"p.{Q("UserId")}=-1")
            };
            string Branches(string projection, string join, string condition) => string.Join(" UNION ALL ", branchTails.Select(b => $"SELECT DISTINCT {projection} AS {Q("ReferenceId")},p.{Q("PermissionType")} FROM {join}{b.Join} WHERE {condition} AND {b.Where}"));
            var inherited = Branches($"s.{Q("SiteId")}", $"{T("Sites")} s JOIN {T("Permissions")} p ON p.{Q("ReferenceId")}=s.{Q("InheritPermission")}", $"s.{Q("TenantId")}=@tenant");
            var deptExists = $"EXISTS (SELECT 1 FROM {T("Depts")} a WHERE a.{Q("TenantId")}=@tenant AND a.{Q("DeptId")}=@dept AND a.{Q("Disabled")}={no})";
            var groupFrom = $" FROM {T("Groups")} g JOIN {T("GroupMembers")} m ON m.{Q("GroupId")}=g.{Q("GroupId")}";
            var groupCondition = $"g.{Q("TenantId")}=@tenant AND g.{Q("Disabled")}={no} AND g.{Q("GroupId")}=p.{Q("GroupId")}";
            var principalBranches = new[]
            {
                $"SELECT 1 FROM {T("Depts")} a WHERE a.{Q("TenantId")}=@tenant AND a.{Q("DeptId")}=@dept AND a.{Q("Disabled")}={no} AND a.{Q("DeptId")}=p.{Q("DeptId")} AND @dept<>0",
                "SELECT 1"+groupFrom+$" WHERE {groupCondition} AND m.{Q("DeptId")}=@dept AND @dept<>0 AND {deptExists}",
                "SELECT 1"+groupFrom+$" WHERE {groupCondition} AND m.{Q("UserId")}=@user AND @user<>0",
                $"SELECT 1 FROM {T("Permissions")} other WHERE other.{Q("ReferenceId")}=p.{Q("ReferenceId")} AND other.{Q("UserId")}=p.{Q("UserId")} AND other.{Q("UserId")}=@user AND @user<>0",
                $"SELECT 1 FROM {T("Permissions")} other WHERE other.{Q("ReferenceId")}=p.{Q("ReferenceId")} AND other.{Q("UserId")}=-1"
            };
            var candidates = new[]
            {
                (Phase:"duplicate-item-id",Table:"Items",Name:"record_duplicate_item_id",Keys:"ReferenceId"),
                (Phase:"dept",Table:"Permissions",Name:"record_dept",Keys:"DeptId,ReferenceId,PermissionType"),
                (Phase:"group",Table:"Permissions",Name:"record_group",Keys:"GroupId,ReferenceId,PermissionType"),
                (Phase:"user",Table:"Permissions",Name:"record_user",Keys:"UserId,ReferenceId,PermissionType")
            };
            var contexts = new[]
            {
                (Name:"department-duplicates",Tenant:1,Dept:11,User:1001,Id:10001,Site:10,Mask:1L),
                (Name:"group-members",Tenant:1,Dept:11,User:1001,Id:10002,Site:10,Mask:2),
                (Name:"public-reference",Tenant:1,Dept:0,User:1003,Id:10004,Site:10,Mask:(1L << 40) | 32),
                (Name:"disabled-principals",Tenant:1,Dept:12,User:1002,Id:10005,Site:10,Mask:(1L << 40) | 32),
                (Name:"disabled-user-membership",Tenant:1,Dept:0,User:1002,Id:10006,Site:10,Mask:64),
                (Name:"zero-principals",Tenant:1,Dept:0,User:0,Id:10007,Site:10,Mask:1L),
                (Name:"inheritance-only",Tenant:1,Dept:0,User:1003,Id:10008,Site:10,Mask:1L),
                (Name:"other-tenant",Tenant:2,Dept:21,User:2001,Id:10010,Site:30,Mask:2),
                (Name:"tenant-boundary",Tenant:1,Dept:11,User:1001,Id:10010,Site:10,Mask:2),
                (Name:"missing-item",Tenant:1,Dept:11,User:1001,Id:999999,Site:10,Mask:4),
                (Name:"site-context",Tenant:1,Dept:11,User:1001,Id:10,Site:10,Mask:4)
            };
            foreach (var scenario in new[] { "small-uniform", "larger-skewed" })
            {
                var count = scenario == "small-uniform" ? 400 : 4000;
                var items = Enumerable.Range(1, count).Select(id => new Item(10000 + id, id <= 8 || (scenario == "larger-skewed" && id > 12 && id % 10 < 8) ? 10 : (id % 4 + 1) * 10)).ToArray();
                var grants = new List<Grant>();
                foreach (var s in sites)
                {
                    if (s.Id is 20 or 40) { grants.Add(new(s.Reference, 0, 0, s.Tenant * 1000 + 3, 4)); }
                    else { grants.AddRange(new[] { new Grant(s.Reference, s.Tenant * 10 + 1, 0, 0, 1), new(s.Reference, 0, s.Tenant * 100 + 1, 0, 2), new(s.Reference, 0, 0, s.Tenant * 1000 + 1, 4), new(s.Reference, 0, 0, -1, 8) }); }
                }
                foreach (var item in items)
                {
                    var tenant = sites.Single(s => s.Id == item.Site).Tenant; var dept = tenant * 10 + 1; var group = tenant * 100 + 1; var user = tenant * 1000 + 1;
                    grants.AddRange(((item.Id - 10000) % 8) switch
                    {
                        1 => new[] { new Grant(item.Id, dept, 0, 0, 1), new(item.Id, dept, 0, user, 1) },
                        2 => [new Grant(item.Id, 0, group, 0, 2), new(item.Id, 0, group + 2, 0, 4)],
                        3 => [new Grant(item.Id, 0, 0, user, 4)],
                        4 => [new Grant(item.Id, 0, 0, -1, (1L << 40) | 8), new(item.Id, 0, 0, 9999, (1L << 40) | 32)],
                        5 => [new Grant(item.Id, dept + 1, 0, 0, 16), new(item.Id, 0, group + 1, 0, 32)],
                        6 => [new Grant(item.Id, 0, group + 3, 0, 64)],
                        7 => [new Grant(item.Id, dept, 0, 0, 1), new(item.Id, 0, 0, user, 2), new(item.Id, 0, 0, -1, 4)],
                        _ => Array.Empty<Grant>()
                    });
                }
                var reports = new List<object>(); var sizes = new List<object>(); var writes = new List<object>();
                var previous = "standard";
                foreach (var phase in new[] { "standard", "duplicate-item-id", "dept", "group", "user", "combined-subjects" })
                {
                    foreach (var candidate in candidates.Where(c => c.Phase == previous || (previous == "combined-subjects" && c.Table == "Permissions"))) { await Execute(d.Drop("RecordPermission" + candidate.Table, candidate.Name)); }
                    foreach (var name in new[] { "Items", "Permissions" }) { await Execute("TRUNCATE TABLE " + T(name)); }
                    for (var first = 0; first < items.Length; first += 200) { await Execute($"INSERT INTO {T("Items")} VALUES " + string.Join(',', items.Skip(first).Take(200).Select(ItemValues))); }
                    for (var first = 0; first < grants.Count; first += 200) { await Execute($"INSERT INTO {T("Permissions")} VALUES " + string.Join(',', grants.Skip(first).Take(200).Select(GrantValues))); }
                    var active = candidates.Where(c => c.Phase == phase || (phase == "combined-subjects" && c.Table == "Permissions")).ToArray();
                    foreach (var candidate in active) { await Execute($"CREATE INDEX {Q(candidate.Name)} ON {T(candidate.Table)} ({string.Join(',', candidate.Keys.Split(',').Select(Q))})"); }
                    previous = phase;
                    foreach (var name in tables) { await Execute(dbms switch { Dbms.SQLServer => $"UPDATE STATISTICS {T(name)} WITH FULLSCAN", Dbms.PostgreSQL => "ANALYZE " + T(name), _ => "ANALYZE TABLE " + T(name) }); }
                    foreach (var candidate in active)
                    {
                        await using var size = db.CreateCommand();
                        size.CommandText = dbms switch
                        {
                            Dbms.SQLServer => $"SELECT SUM(p.used_page_count)*8192 FROM sys.dm_db_partition_stats p JOIN sys.indexes i ON i.object_id=p.object_id AND i.index_id=p.index_id WHERE p.object_id=OBJECT_ID(N'{T(candidate.Table)}') AND i.name=@name",
                            Dbms.PostgreSQL => $"SELECT pg_relation_size('{d.Table(candidate.Name)}')",
                            _ => "SELECT SUM(stat_value)*@@innodb_page_size FROM mysql.innodb_index_stats WHERE database_name=DATABASE() AND table_name=@table AND index_name=@name AND stat_name='size'"
                        };
                        if (dbms != Dbms.PostgreSQL) { var p = size.CreateParameter(); p.ParameterName = "@name"; p.Value = candidate.Name; size.Parameters.Add(p); }
                        if (dbms == Dbms.MySQL) { var p = size.CreateParameter(); p.ParameterName = "@table"; p.Value = "RecordPermission" + candidate.Table; size.Parameters.Add(p); }
                        var bytes = Convert.ToInt64(await size.ExecuteScalarAsync()); check(bytes > 0, "Record permission index capacity: " + dbms);
                        sizes.Add(new { Phase = phase, Index = candidate.Name, Bytes = bytes, MysqlSizeIsEstimate = dbms == Dbms.MySQL });
                    }
                    foreach (var context in contexts)
                    {
                        bool Matches(Grant p, int branch) => branch switch
                        {
                            0 => p.Dept == context.Dept && !departments.GetValueOrDefault(context.Dept, true),
                            1 => !groups.GetValueOrDefault(p.Group, true) && !departments.GetValueOrDefault(context.Dept, true) && members.Any(m => m.Group == p.Group && m.Dept == context.Dept),
                            2 => !groups.GetValueOrDefault(p.Group, true) && !users.GetValueOrDefault(context.User, true) && members.Any(m => m.Group == p.Group && m.User == context.User),
                            3 => p.User > 0 && p.User == context.User,
                            _ => p.User == -1
                        };
                        var expected = new List<(long Id, long Mask)>();
                        for (var branch = 0; branch < 5; branch++)
                        {
                            expected.AddRange(sites.Where(s => s.Tenant == context.Tenant).SelectMany(s => grants.Where(p => p.Reference == s.Reference && Matches(p, branch)).Select(p => ((long)s.Id, p.Mask))).Distinct());
                            if (context.Id > 0 && context.Id != context.Site)
                            {
                                expected.AddRange(items.Where(i => i.Id == context.Id && sites.Any(s => s.Id == i.Site && s.Tenant == context.Tenant)).SelectMany(i => grants.Where(p => p.Reference == i.Id && Matches(p, branch)).Select(p => ((long)i.Id, p.Mask))).Distinct());
                            }
                        }
                        bool Allowed(Grant p) =>
                            (context.Dept != 0 && p.Dept == context.Dept && context.Dept / 10 == context.Tenant && !departments.GetValueOrDefault(context.Dept, true)) ||
                            (p.Group / 100 == context.Tenant && !groups.GetValueOrDefault(p.Group, true) && members.Any(m => m.Group == p.Group &&
                                ((context.Dept != 0 && m.Dept == context.Dept && context.Dept / 10 == context.Tenant && !departments.GetValueOrDefault(context.Dept, true)) || (context.User != 0 && m.User == context.User)))) ||
                            (context.User != 0 && p.User == context.User) || grants.Any(other => other.Reference == p.Reference && other.User == -1);
                        var exists = grants.Any(p => p.Reference == context.Id && (p.Mask & context.Mask) == context.Mask && Allowed(p));
                        if (context.Name is "public-reference" or "disabled-user-membership" or "zero-principals") { check(exists, "Record permission correlated EXISTS oracle exercises distinct paths: " + dbms); }
                        if (context.Name == "disabled-user-membership") { check(!expected.Any(p => p.Id == context.Id), "Record permission row query retains enabled-user boundary: " + dbms); }
                        var combined = inherited;
                        if (context.Id > 0 && context.Id != context.Site)
                        {
                            combined += " UNION ALL " + Branches($"i.{Q("ReferenceId")}", $"{T("Items")} i JOIN {T("Sites")} s ON s.{Q("SiteId")}=i.{Q("SiteId")} JOIN {T("Permissions")} p ON p.{Q("ReferenceId")}=i.{Q("ReferenceId")}", $"s.{Q("TenantId")}=@tenant AND i.{Q("ReferenceId")}={context.Id}");
                        }
                        var existsSql = $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {T("Permissions")} p WHERE p.{Q("ReferenceId")}={context.Id} AND (p.{Q("PermissionType")} & @mask)=@mask AND EXISTS ({string.Join(" UNION ALL ", principalBranches)})) THEN 1 ELSE 0 END";
                        foreach (var query in new[] { (Name: "combined-rows", Sql: combined), (Name: "record-exists", Sql: existsSql) })
                        {
                            var times = new List<double>();
                            for (var sample = 0; sample < 6; sample++)
                            {
                                await using var command = db.CreateCommand(); command.CommandText = query.Sql; Bind(command, context.Tenant, context.Dept, context.User, context.Mask);
                                var timer = Stopwatch.StartNew(); await using var reader = await command.ExecuteReaderAsync();
                                var actual = new List<(long Id, long Mask)>();
                                while (await reader.ReadAsync()) { actual.Add((Convert.ToInt64(reader.GetValue(0)), query.Name == "combined-rows" ? Convert.ToInt64(reader.GetValue(1)) : 0L)); }
                                timer.Stop();
                                check(query.Name == "combined-rows" ? actual.OrderBy(p => p.Id).ThenBy(p => p.Mask).SequenceEqual(expected.OrderBy(p => p.Id).ThenBy(p => p.Mask)) : actual.SequenceEqual(new[] { (exists ? 1L : 0L, 0L) }), "Record permission result: " + dbms + phase + context.Name + query.Name);
                                if (sample > 0) { times.Add(timer.Elapsed.TotalMilliseconds); }
                            }
                            await using var plan = db.CreateCommand(); Bind(plan, context.Tenant, context.Dept, context.User, context.Mask);
                            plan.CommandText = dbms switch { Dbms.SQLServer => "SET STATISTICS XML ON; " + query.Sql + "; SET STATISTICS XML OFF;", Dbms.PostgreSQL => "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + query.Sql, _ => "EXPLAIN ANALYZE " + query.Sql };
                            await using var plans = await plan.ExecuteReaderAsync(); if (dbms == Dbms.SQLServer) { while (await plans.ReadAsync()) { } await plans.NextResultAsync(); }
                            var lines = new List<string>(); while (await plans.ReadAsync()) { lines.Add(plans.GetString(0)); }
                            check(lines.Count > 0, "Record permission plan: " + dbms);
                            times.Sort(); reports.Add(new { Phase = phase, Context = context.Name, Query = query.Name, Rows = query.Name == "combined-rows" ? expected.Count : 1, MedianClientMs = times[2], SamplesMs = times, Plan = string.Join('\n', lines) });
                        }
                    }
                    foreach (var workload in new[] { "insert-items", "update-grants", "delete-items" })
                    {
                        var affectedRows = workload == "update-grants" ? grants.Count(p => p.Reference is >= 10001 and <= 10050) : 50;
                        foreach (var mode in new[] { "rollback", "commit" })
                        {
                            for (var sample = 0; sample < 6; sample++)
                            {
                                await using var tx = await db.BeginTransactionAsync(); transaction = tx; var committed = false;
                                try
                                {
                                    var sql = workload switch
                                    {
                                        "insert-items" => $"INSERT INTO {T("Items")} VALUES " + string.Join(',', Enumerable.Range(900000, 50).Select(id => ItemValues(new(id, 10)))),
                                        "update-grants" => $"UPDATE {T("Permissions")} SET {Q("PermissionType")}={Q("PermissionType")}+128 WHERE {Q("ReferenceId")} BETWEEN 10001 AND 10050",
                                        _ => $"DELETE FROM {T("Items")} WHERE {Q("ReferenceId")} BETWEEN 10001 AND 10050"
                                    };
                                    var timer = Stopwatch.StartNew(); var affected = await Execute(sql);
                                    if (mode == "commit") { await tx.CommitAsync(); committed = true; }
                                    timer.Stop(); check(affected == affectedRows, "Record permission write count: " + dbms);
                                    if (sample > 0) { writes.Add(new { Phase = phase, Workload = workload, Mode = mode, Rows = affected, Sample = sample, ClientMs = timer.Elapsed.TotalMilliseconds }); }
                                }
                                finally
                                {
                                    if (!committed) { await tx.RollbackAsync(); }
                                    transaction = null;
                                    if (committed)
                                    {
                                        var restore = workload switch
                                        {
                                            "insert-items" => $"DELETE FROM {T("Items")} WHERE {Q("ReferenceId")}>=900000",
                                            "update-grants" => $"UPDATE {T("Permissions")} SET {Q("PermissionType")}={Q("PermissionType")}-128 WHERE {Q("ReferenceId")} BETWEEN 10001 AND 10050",
                                            _ => $"INSERT INTO {T("Items")} VALUES " + string.Join(',', items.Take(50).Select(ItemValues))
                                        };
                                        check(await Execute(restore) == affectedRows, "Record permission restored row count: " + dbms);
                                    }
                                }
                            }
                        }
                    }
                    await using (var verify = db.CreateCommand())
                    {
                        verify.CommandText = $"SELECT {Q("ReferenceId")},{Q("SiteId")} FROM {T("Items")}";
                        await using var reader = await verify.ExecuteReaderAsync(); var actual = new List<Item>();
                        while (await reader.ReadAsync()) { actual.Add(new(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)))); }
                        check(actual.OrderBy(i => i.Id).SequenceEqual(items), "Record permission full Items restoration: " + dbms);
                    }
                    await using (var verify = db.CreateCommand())
                    {
                        verify.CommandText = $"SELECT {Q("ReferenceId")},{Q("DeptId")},{Q("GroupId")},{Q("UserId")},{Q("PermissionType")} FROM {T("Permissions")}";
                        await using var reader = await verify.ExecuteReaderAsync(); var actual = new List<Grant>();
                        while (await reader.ReadAsync()) { actual.Add(new(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)), Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)), Convert.ToInt64(reader.GetValue(4)))); }
                        IOrderedEnumerable<Grant> Ordered(IEnumerable<Grant> data) => data.OrderBy(p => p.Reference).ThenBy(p => p.Dept).ThenBy(p => p.Group).ThenBy(p => p.User);
                        check(Ordered(actual).SequenceEqual(Ordered(grants)), "Record permission full grant restoration: " + dbms);
                    }
                }
                foreach (var candidate in candidates.Where(c => c.Table == "Permissions")) { await Execute(d.Drop("RecordPermission" + candidate.Table, candidate.Name)); }
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"record-permission-{dbms}-{scenario}.json"), JsonSerializer.Serialize(new { BaselineVersion = 2, Baseline = baseline, Reports = reports, Writes = writes, IndexSizes = sizes }));
            }
        }
        finally { foreach (var name in tables.AsEnumerable().Reverse()) { await Execute("DROP TABLE " + T(name)); } }
    }
}
